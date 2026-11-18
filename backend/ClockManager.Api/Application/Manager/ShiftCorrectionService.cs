using ClockManager.Api.Application.Employees;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using ClockManager.Api.Infrastructure.Persistence.Configurations;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClockManager.Api.Application.Manager;

/// <summary>
/// <c>POST /api/manager/shifts/{id}/corrections</c> (plans/07-manager.md §3). Order: manager → shift exists (404) →
/// trusted time, fresh, no transaction open (503, nothing saved) → transaction: employee lock (D-044) → reload →
/// rowVersion → rules → update the shift + insert the audit row → commit.
/// </summary>
public sealed class ShiftCorrectionService(
    ICurrentEmployee currentEmployee,
    ITrustedClock trustedClock,
    ClockManagerDbContext db,
    ManagerService manager,
    ZurichTime zurich,
    IOptions<ShiftRulesOptions> rules,
    ILogger<ShiftCorrectionService> logger)
{
    private const int SqlCheckViolation = 547;
    private const int SqlUniqueIndexViolation = 2601;
    private const int SqlUniqueConstraintViolation = 2627;

    public async Task<ShiftDetailView> CorrectAsync(long shiftId, ParsedCorrection change, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(change);
        var by = await currentEmployee.RequireManagerAsync(ct);

        // A shift's employee never changes, so it is safe to read before the lock.
        var employeeId = await db.Shifts.AsNoTracking().Where(s => s.Id == shiftId)
            .Select(s => (int?)s.EmployeeId).SingleOrDefaultAsync(ct) ?? throw ManagerErrors.ShiftNotFound();

        // Fresh trusted time (never the display cache) BEFORE the transaction: no lock is held during the HTTP call.
        var trusted = TrustedInstantRules.Normalize(await trustedClock.GetNowAsync(ct));
        var now = trusted.Utc;

        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await EmployeeLock.AcquireAsync(db, employeeId, ct); // same order as punches: Employees row first

            var shift = await db.Shifts.SingleAsync(s => s.Id == shiftId, ct);
            if (!shift.RowVersion.AsSpan().SequenceEqual(change.RowVersion)) throw ManagerErrors.Stale();

            var (newIn, newOut) = CorrectionRules.Check(shift, change, now);

            // Overlap or touch with another shift of the employee; an open shift runs to "now" (newIn <= now always).
            var thisEnd = newOut ?? now;
            var other = await db.Shifts.AsNoTracking()
                .Where(s => s.EmployeeId == employeeId && s.Id != shiftId && s.ClockIn.Utc <= thisEnd
                    && (s.ClockOut == null || s.ClockOut!.Utc >= newIn))
                .OrderBy(s => s.ClockIn.Utc)
                .Select(s => (long?)s.Id)
                .FirstOrDefaultAsync(ct);
            if (other is { } otherId) throw ManagerErrors.Overlap(otherId);

            CorrectionRules.EnsureMayClose(shift, change, now, rules.Value);

            var old = change.Field == ShiftCorrectionField.ClockIn ? shift.ClockIn.Utc : shift.ClockOut?.Utc;
            var stamp = PunchStamp.FromCorrection(change.NewUtc, by.Id, zurich);
            if (change.Field == ShiftCorrectionField.ClockIn) shift.CorrectClockIn(stamp);
            else shift.CorrectClockOut(stamp);
            db.ShiftCorrections.Add(new ShiftCorrection(
                shift.Id, by.Id, change.Field, old, change.NewUtc, change.Reason, now, trusted.Provider)); // trusted instant

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw ManagerErrors.Stale(); // a writer that skipped the lock changed the row
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: SqlCheckViolation } sql
            && sql.Message.Contains(ShiftConfiguration.ClockOutAfterClockInCheck, StringComparison.Ordinal))
        {
            throw ManagerErrors.InvalidOrder();
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: SqlUniqueIndexViolation or SqlUniqueConstraintViolation })
        {
            throw ManagerErrors.Stale();
        }

        logger.LogInformation(
            "Manager {ManagerId} corrected {Field} of shift {ShiftId} (employee {EmployeeId}) to {NewUtc} at {Now} from {Provider}.",
            by.Id, change.Field, shiftId, employeeId, change.NewUtc, now, trusted.Provider);
        db.ChangeTracker.Clear();
        return await manager.BuildDetailAsync(shiftId, ct);
    }
}
