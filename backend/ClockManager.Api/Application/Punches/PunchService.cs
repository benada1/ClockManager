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

namespace ClockManager.Api.Application.Punches;

/// <summary>
/// Clock In / Clock Out (plans/03-punch-api.md §5). Order: who → state check → trusted time → monotonic guard → save →
/// map DB errors. The time comes only from <see cref="ITrustedClock"/>; the client never sends one.
/// <para>
/// The provider call (up to 3 s) happens BEFORE anything is written and with no transaction open (D-028), so no lock is
/// held during an HTTP call. The state check before it is only a fast answer for an obviously wrong click; the real
/// guarantee is the database (<c>UX_Shifts_OneOpenPerEmployee</c>, <c>RowVersion</c>), whose errors are mapped to 409s.
/// </para>
/// </summary>
public sealed class PunchService(
    ICurrentEmployee currentEmployee,
    ITrustedClock trustedClock,
    ClockManagerDbContext db,
    ZurichTime zurich,
    IOptions<ShiftRulesOptions> rules,
    ILogger<PunchService> logger)
{
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlUniqueConstraintViolation = 2627;

    /// <param name="idempotencyKey">Raw <c>Idempotency-Key</c> header value; required, must be a UUID.</param>
    public async Task<ShiftView> PunchAsync(PunchIntent intent, string? idempotencyKey, CancellationToken ct)
    {
        // 1. Who? (401 / 403)
        var employee = await currentEmployee.RequireActiveAsync(ct);

        // 2. Idempotency-Key. Required by the contract (plan 03 Q1), so it is validated now, but in step 1 it is NOT
        //    stored or replayed yet: that is plan 03 step 2 (IdempotencyRecords table, replay, key reuse → 422).
        _ = ParseIdempotencyKey(idempotencyKey);

        return intent switch
        {
            PunchIntent.ClockIn => await ClockInAsync(employee, ct),
            PunchIntent.ClockOut => await ClockOutAsync(employee, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(intent), intent, "Unknown intent."),
        };
    }

    private async Task<ShiftView> ClockInAsync(CurrentEmployee employee, CancellationToken ct)
    {
        // 3. State check (fast path only), and the latest punch as it was before the provider call.
        var open = await FindOpenShiftAsync(employee.Id, tracked: false, ct);
        if (open is not null) throw PunchErrors.AlreadyOpen(ShiftViewMapper.ToView(open, rules.Value));
        var lastBefore = await LastPunchAsync(employee.Id, ct);

        // 4. Trusted time (TrustedTimeException → 503). No transaction is open.
        var trusted = await trustedClock.GetNowAsync(ct);

        // 5. Monotonic guard. A punch saved by another request during our call → 409 with the current open shift.
        await EnsureLaterThanLastPunchAsync(employee.Id, trusted, lastBefore, async () =>
        {
            var current = await FindOpenShiftAsync(employee.Id, tracked: false, CancellationToken.None);
            return PunchErrors.AlreadyOpen(current is null ? null : ShiftViewMapper.ToView(current, rules.Value));
        }, ct);

        // 6. Save. One INSERT, so SaveChanges' own transaction is enough. Step 2 adds the idempotency record to the
        //    same transaction.
        var shift = new Shift(employee.Id, PunchStamp.From(trusted, zurich));
        db.Shifts.Add(shift);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex, ShiftConfiguration.OneOpenPerEmployeeIndex))
        {
            // 7. Lost the race: another request opened a shift between our state check and our INSERT.
            db.ChangeTracker.Clear();
            var winner = await FindOpenShiftAsync(employee.Id, tracked: false, CancellationToken.None);
            logger.LogInformation("Clock-in for employee {EmployeeId} lost a race to shift {ShiftId}.", employee.Id, winner?.Id);
            throw PunchErrors.AlreadyOpen(winner is null ? null : ShiftViewMapper.ToView(winner, rules.Value));
        }

        logger.LogInformation(
            "Employee {EmployeeId} clocked in: shift {ShiftId} at {Utc} from {Provider} (cross-checked by {CrossCheckedBy}).",
            employee.Id, shift.Id, trusted.Utc, trusted.Provider, trusted.CrossCheckedBy);
        return ShiftViewMapper.ToView(shift, rules.Value);
    }

    private async Task<ShiftView> ClockOutAsync(CurrentEmployee employee, CancellationToken ct)
    {
        // 3. State check. The shift is tracked with the RowVersion read here: if anyone changes or closes it before our
        //    UPDATE, the UPDATE matches no row and EF throws DbUpdateConcurrencyException.
        var shift = await FindOpenShiftAsync(employee.Id, tracked: true, ct) ?? throw PunchErrors.NotOpen();
        var lastBefore = await LastPunchAsync(employee.Id, ct);

        // 4. Trusted time. No transaction is open.
        var trusted = await trustedClock.GetNowAsync(ct);

        // 5. Monotonic guard. Covers Shift.Close's "out after in" too, so Close can't throw for a backwards time.
        //    A punch saved by another request during our call (another tab closed the shift) → 409 not open.
        await EnsureLaterThanLastPunchAsync(employee.Id, trusted, lastBefore, () => Task.FromResult(PunchErrors.NotOpen()), ct);

        // 6. Save.
        shift.Close(PunchStamp.From(trusted, zurich));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // 7. Someone else (another tab) closed it first. Their clock-out stays.
            logger.LogInformation("Clock-out of shift {ShiftId} lost a race: it was closed meanwhile.", shift.Id);
            throw PunchErrors.NotOpen();
        }

        logger.LogInformation(
            "Employee {EmployeeId} clocked out: shift {ShiftId} at {Utc} from {Provider} (cross-checked by {CrossCheckedBy}).",
            employee.Id, shift.Id, trusted.Utc, trusted.Provider, trusted.CrossCheckedBy);
        return ShiftViewMapper.ToView(shift, rules.Value);
    }

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
