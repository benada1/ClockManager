using System.Text.Json;
using ClockManager.Api.Application.Employees;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using ClockManager.Api.Infrastructure.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using MvcJsonOptions = Microsoft.AspNetCore.Mvc.JsonOptions;

namespace ClockManager.Api.Application.Punches;

/// <summary>
/// Clock In / Clock Out (plans/03-punch-api.md §5). Order: who → key + replay of a known key → state check → trusted
/// time → monotonic guard → save (shift + idempotency record, one transaction) → map DB errors. The time comes only
/// from <see cref="ITrustedClock"/>; the client never sends one.
/// <para>
/// The provider call (up to 3 s) happens BEFORE anything is written and with no transaction open (D-028), so no lock is
/// held during an HTTP call. The state check before it is only a fast answer for an obviously wrong click; the real
/// guarantee is the database (<c>UX_Shifts_OneOpenPerEmployee</c>, <c>RowVersion</c>, the unique idempotency key),
/// whose errors are mapped to 409s or to a replay of the winner's answer (D-032).
/// </para>
/// </summary>
public sealed class PunchService(
    ICurrentEmployee currentEmployee,
    ITrustedClock trustedClock,
    ClockManagerDbContext db,
    ZurichTime zurich,
    IOptions<ShiftRulesOptions> rules,
    IOptions<MvcJsonOptions> jsonOptions,
    ILogger<PunchService> logger)
{
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlUniqueConstraintViolation = 2627;

    /// <param name="idempotencyKey">Raw <c>Idempotency-Key</c> header value; required, must be a UUID.</param>
    public async Task<PunchResult> PunchAsync(PunchIntent intent, string? idempotencyKey, CancellationToken ct)
    {
        // 1. Who? (401 / 403)
        var employee = await currentEmployee.RequireActiveAsync(ct);

        // 2. Idempotency-Key (400), then replay (D-032). A key already stored for this employee is answered from the
        //    stored response: no provider call, no new row. The other intent under the same key is a client bug → 422.
        var key = ParseIdempotencyKey(idempotencyKey);
        var wireIntent = WireIntent(intent);
        var recorded = await FindRecordAsync(employee.Id, key, ct);
        if (recorded is not null) return Replay(recorded, wireIntent);

        try
        {
            return intent switch
            {
                PunchIntent.ClockIn => await ClockInAsync(employee, key, ct),
                PunchIntent.ClockOut => await ClockOutAsync(employee, key, ct),
                _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown intent."),
            };
        }
        catch (AppException ex) when (ex.Code is ErrorCodes.ShiftAlreadyOpen or ErrorCodes.ShiftNotOpen or ErrorCodes.TimeWentBackwards)
        {
            // A 409 may only mean "my twin request with the same key won the race" (double click, a retry racing the
            // original). Then the twin's stored record exists by now: answer with it instead of the conflict.
            // time.went_backwards is included as a safety net (D-043): if the twin's commit was the reason the guard
            // refused, its stored record answers us too. With no record the original error stands.
            var winner = await FindRecordAsync(employee.Id, key, ct);
            if (winner is null) throw;
            logger.LogInformation(
                "Punch with key {Key} for employee {EmployeeId} lost a race to its twin; replaying the stored response.", key, employee.Id);
            return Replay(winner, wireIntent);
        }
    }

    private async Task<PunchResult> ClockInAsync(CurrentEmployee employee, Guid key, CancellationToken ct)
    {
        // 3. State check (fast path only), and the latest punch as it was before the provider call.
        //    The snapshot is read FIRST (D-043): it can then never be newer than the state it is paired with.
        var lastBefore = await LastPunchAsync(employee.Id, ct);
        var open = await FindOpenShiftAsync(employee.Id, tracked: false, ct);
        if (open is not null) throw PunchErrors.AlreadyOpen(ShiftViewMapper.ToView(open, rules.Value));

        // 4. Trusted time (TrustedTimeException → 503). No transaction is open. Range-checked and floored to a DB tick (D-042).
        var trusted = TrustedInstantRules.Normalize(await trustedClock.GetNowAsync(ct));

        // 5. Monotonic guard. A punch saved by another request during our call → 409 with the current open shift.
        Func<Task<AppException>> conflict = async () =>
        {
            var current = await FindOpenShiftAsync(employee.Id, tracked: false, CancellationToken.None);
            return PunchErrors.AlreadyOpen(current is null ? null : ShiftViewMapper.ToView(current, rules.Value));
        };
        await EnsureLaterThanLastPunchAsync(employee.Id, trusted, lastBefore, conflict, ct);

        // 6. Save the shift and its idempotency record in one transaction.
        var shift = new Shift(employee.Id, PunchStamp.From(trusted, zurich));
        db.Shifts.Add(shift);
        PunchResult result;
        try
        {
            result = await SaveShiftAndRecordAsync(shift, employee.Id, key, PunchRequest.ClockIn, trusted, lastBefore, conflict, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, IdempotencyRecordConfiguration.EmployeeKeyIndex))
        {
            return await ReplayWinnerAsync(employee.Id, key, PunchRequest.ClockIn, ex, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, ShiftConfiguration.OneOpenPerEmployeeIndex))
        {
            // 7. Lost the race: another request opened a shift between our state check and our INSERT. (If that request
            //    was our own twin with the same key, PunchAsync turns this 409 into a replay.)
            db.ChangeTracker.Clear();
            var winner = await FindOpenShiftAsync(employee.Id, tracked: false, CancellationToken.None);
            logger.LogInformation("Clock-in for employee {EmployeeId} lost a race to shift {ShiftId}.", employee.Id, winner?.Id);
            throw PunchErrors.AlreadyOpen(winner is null ? null : ShiftViewMapper.ToView(winner, rules.Value));
        }

        logger.LogInformation(
            "Employee {EmployeeId} clocked in: shift {ShiftId} at {Utc} from {Provider} (cross-checked by {CrossCheckedBy}).",
            employee.Id, shift.Id, trusted.Utc, trusted.Provider, trusted.CrossCheckedBy);
        return result;
    }

    private async Task<PunchResult> ClockOutAsync(CurrentEmployee employee, Guid key, CancellationToken ct)
    {
        // 3. State check. The shift is tracked with the RowVersion read here: if anyone changes or closes it before our
        //    UPDATE, the UPDATE matches no row and EF throws DbUpdateConcurrencyException.
        //    The snapshot is read FIRST (D-043), see ClockInAsync.
        var lastBefore = await LastPunchAsync(employee.Id, ct);
        var shift = await FindOpenShiftAsync(employee.Id, tracked: true, ct) ?? throw PunchErrors.NotOpen();

        // 4. Trusted time. No transaction is open. Range-checked and floored to a DB tick (D-042).
        var trusted = TrustedInstantRules.Normalize(await trustedClock.GetNowAsync(ct));

        // 5. Monotonic guard. Covers Shift.Close's "out after in" too, so Close can't throw for a backwards time.
        //    A punch saved by another request during our call (another tab closed the shift) → 409 not open.
        Func<Task<AppException>> conflict = () => Task.FromResult(PunchErrors.NotOpen());
        await EnsureLaterThanLastPunchAsync(employee.Id, trusted, lastBefore, conflict, ct);

        // 6. Save the shift and its idempotency record in one transaction.
        shift.Close(PunchStamp.From(trusted, zurich));
        PunchResult result;
        try
        {
            result = await SaveShiftAndRecordAsync(shift, employee.Id, key, PunchRequest.ClockOut, trusted, lastBefore, conflict, ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, IdempotencyRecordConfiguration.EmployeeKeyIndex))
        {
            return await ReplayWinnerAsync(employee.Id, key, PunchRequest.ClockOut, ex, ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 7. Someone else (another tab) closed it first. Their clock-out stays. (If that was our own twin with the
            //    same key, PunchAsync turns this 409 into a replay.)
            logger.LogInformation("Clock-out of shift {ShiftId} lost a race: it was closed meanwhile.", shift.Id);
            throw PunchErrors.NotOpen();
        }

        logger.LogInformation(
            "Employee {EmployeeId} clocked out: shift {ShiftId} at {Utc} from {Provider} (cross-checked by {CrossCheckedBy}).",
            employee.Id, shift.Id, trusted.Utc, trusted.Provider, trusted.CrossCheckedBy);
        return result;
    }

    /// <summary>
    /// Step 6, one transaction, still AFTER the provider call (D-028, D-032): write the shift (the INSERT or the closing
    /// UPDATE), build the response from the saved row (a new shift's Id exists only now), store it as the idempotency
    /// record, commit. If anything fails, the transaction rolls back and neither row exists.
    /// <para>
    /// D-044 (QA-23): the first statement takes an update lock on the employee's row, which serialises the punches of ONE
    /// employee (other employees are untouched) from here to the commit, and the monotonic guard runs again under it.
    /// Without this the guard (read earlier) and the write were not atomic, so a stalled punch could be stored with an
    /// instant not later than a newer punch of the same employee. Lock order in every path: Employees row, then
    /// Shifts, then IdempotencyRecords. The lock is held for the lock, one read, two writes and the commit; never
    /// across the provider call.
    /// </para>
    /// </summary>
    private async Task<PunchResult> SaveShiftAndRecordAsync(
        Shift shift, int employeeId, Guid key, string wireIntent, TrustedInstant trusted,
        Instant? lastBefore, Func<Task<AppException>> conflict, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockEmployeeAsync(employeeId, ct);
        await EnsureLaterThanLastPunchAsync(employeeId, trusted, lastBefore, conflict, ct);
        await db.SaveChangesAsync(ct);

        var view = ShiftViewMapper.ToView(shift, rules.Value);
        var body = JsonSerializer.Serialize(view, jsonOptions.Value.JsonSerializerOptions);
        db.IdempotencyRecords.Add(new IdempotencyRecord(
            employeeId, key, wireIntent, shift.Id, StatusCodes.Status201Created, body, trusted.Utc)); // the trusted instant
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return new PunchResult(StatusCodes.Status201Created, view);
    }

    /// <summary>
    /// Update lock on the employee's row (D-044). Plain readers of Employees (shared locks) are not blocked; a second
    /// punch of the same employee waits here until the first commits or rolls back. The FK checks of the Shifts and
    /// IdempotencyRecords inserts only need a shared lock on this row, which our own transaction's update lock allows.
    /// </summary>
    private Task LockEmployeeAsync(int employeeId, CancellationToken ct) => EmployeeLock.AcquireAsync(db, employeeId, ct);

    private Task<IdempotencyRecord?> FindRecordAsync(int employeeId, Guid key, CancellationToken ct) =>
        db.IdempotencyRecords.AsNoTracking().SingleOrDefaultAsync(r => r.EmployeeId == employeeId && r.Key == key, ct);

    /// <summary>The stored answer of an earlier successful punch with this key, exactly as it was then (D-032).</summary>
    private PunchResult Replay(IdempotencyRecord record, string wireIntent)
    {
        if (record.Intent != wireIntent) throw PunchErrors.KeyReused(record.Intent);
        var view = JsonSerializer.Deserialize<ShiftView>(record.ResponseBody, jsonOptions.Value.JsonSerializerOptions)
            ?? throw new InvalidOperationException($"The stored response of idempotency record {record.Id} is empty.");
        return new PunchResult(record.ResponseStatus, view);
    }

    /// <summary>Unique violation on (EmployeeId, Key): a parallel request with the same key committed first. Replay it.</summary>
    private async Task<PunchResult> ReplayWinnerAsync(int employeeId, Guid key, string wireIntent, Exception violation, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        var winner = await FindRecordAsync(employeeId, key, ct)
            ?? throw new InvalidOperationException("Idempotency key violation but no stored record.", violation);
        logger.LogInformation(
            "Punch with key {Key} for employee {EmployeeId} lost a race on the key index; replaying the stored response.", key, employeeId);
        return Replay(winner, wireIntent);
    }

    private static string WireIntent(PunchIntent intent) => intent switch
    {
        PunchIntent.ClockIn => PunchRequest.ClockIn,
        PunchIntent.ClockOut => PunchRequest.ClockOut,
        _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown intent."),
    };

    private Task<Shift?> FindOpenShiftAsync(int employeeId, bool tracked, CancellationToken ct)
    {
        var shifts = tracked ? db.Shifts : db.Shifts.AsNoTracking();
        return shifts.SingleOrDefaultAsync(s => s.EmployeeId == employeeId && s.ClockOut == null, ct);
    }

    /// <summary>The employee's latest punch: the later of their latest clock-in and latest clock-out (null if none).</summary>
    private async Task<Instant?> LastPunchAsync(int employeeId, CancellationToken ct)
    {
        var mine = db.Shifts.AsNoTracking().Where(s => s.EmployeeId == employeeId);
        var lastIn = await mine.MaxAsync(s => (Instant?)s.ClockIn.Utc, ct);
        var lastOut = await mine.Where(s => s.ClockOut != null).MaxAsync(s => (Instant?)s.ClockOut!.Utc, ct);
        return lastOut > lastIn ? lastOut : lastIn;
    }

    /// <summary>
    /// D-030. Compares the trusted instant with the latest punch read AFTER the provider call. If it isn't later, the
    /// snapshot from before the call tells why: the same punch → the time source went backwards (503); a newer punch →
    /// another request punched during our call, so the state changed (409 from <paramref name="conflict"/>). Without
    /// that distinction the loser of a double click got 503 "time went backwards" instead of 409 (found by P5).
    /// </summary>
    private async Task EnsureLaterThanLastPunchAsync(
        int employeeId, TrustedInstant trusted, Instant? lastBefore, Func<Task<AppException>> conflict, CancellationToken ct)
    {
        var lastNow = await LastPunchAsync(employeeId, ct);
        switch (PunchRules.Check(trusted.Utc, lastBefore, lastNow))
        {
            case PunchRules.GuardResult.Ok:
                return;
            case PunchRules.GuardResult.PunchedMeanwhile:
                logger.LogInformation(
                    "Employee {EmployeeId} punched elsewhere during this punch's provider call (last punch {LastBefore} → {LastNow}).",
                    employeeId, lastBefore, lastNow);
                throw await conflict();
            default:
                logger.LogWarning(
                    "Trusted time {Utc} from {Provider} is not later than employee {EmployeeId}'s last punch {LastPunch}; punch refused.",
                    trusted.Utc, trusted.Provider, employeeId, lastNow);
                throw PunchErrors.WentBackwards();
        }
    }

    private static Guid ParseIdempotencyKey(string? raw) =>
        Guid.TryParseExact(raw?.Trim(), "D", out var key) && key != Guid.Empty ? key : throw PunchErrors.KeyMissing();

    private static bool IsUniqueViolation(DbUpdateException ex, string indexName) =>
        ex.InnerException is SqlException { Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation } sql
        && sql.Message.Contains(indexName, StringComparison.Ordinal);
}
