using ClockManager.Api.Application.Employees;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace ClockManager.Api.Application.Status;

/// <summary>
/// <c>GET /api/me/status</c> (plans/03-punch-api.md §4): the caller's open shift, last closed shift, a labeled
/// "server now" and the flags that need "now". Read-only.
/// <para>
/// The time comes from <see cref="IServerNowProvider"/> (a short cache, display and flags only; D-035), never from a
/// punch's code path. If it has no trusted time, the state is still returned, <c>serverNow</c> is null and the
/// time-based flag is left out instead of guessed (P20). A provider problem is never an error here.
/// </para>
/// </summary>
public sealed class StatusService(
    ICurrentEmployee currentEmployee,
    ClockManagerDbContext db,
    IServerNowProvider serverNow,
    IOptions<ShiftRulesOptions> rules,
    ILogger<StatusService> logger)
{
    /// <summary>The <c>source</c> label of <c>serverNow</c>: the trusted clock (possibly a cached reading, see <c>ageMs</c>).</summary>
    public const string TrustedClockSource = "trusted-clock";

    public async Task<StatusView> GetAsync(CancellationToken ct)
    {
        var employee = await currentEmployee.RequireActiveAsync(ct); // 401 / 403, same path as a punch

        var open = await db.Shifts.AsNoTracking()
            .SingleOrDefaultAsync(s => s.EmployeeId == employee.Id && s.ClockOut == null, ct);

        // "Last closed" = the closed shift with the latest clock-out (D-036); the Id breaks a tie between equal instants.
        var lastClosed = await db.Shifts.AsNoTracking()
            .Where(s => s.EmployeeId == employee.Id && s.ClockOut != null)
            .OrderByDescending(s => s.ClockOut!.Utc)
            .ThenByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

        var now = await serverNow.GetAsync(ct);

        // Flags depend on "now"; without a trusted now there are none (D-036). Measured against the reported utc, not
        // extrapolated: it can lag by up to the cache TTL.
        var flags = new List<string>(1);
        if (open is not null && now is not null && ShiftFlags.IsForgotten(open, now.Utc, rules.Value))
        {
            flags.Add(ShiftFlags.ForgottenClockOut);
            logger.LogInformation("Employee {EmployeeId} has shift {ShiftId} open for more than {Hours} h.", employee.Id, open.Id, rules.Value.MaxOpenShiftHours);
        }

        return new StatusView(
            new StatusEmployee(employee.Username, employee.DisplayName),
            open is null ? null : ShiftViewMapper.ToView(open, rules.Value),
            lastClosed is null ? null : ShiftViewMapper.ToView(lastClosed, rules.Value),
            now is null ? null : new ServerNowView(
                ShiftViewMapper.FormatUtc(now.Utc),
                ShiftViewMapper.FormatZurich(now.Utc, now.ZurichOffset),
                TrustedClockSource,
                (long)now.Age.TotalMilliseconds),
            flags);
    }
}
