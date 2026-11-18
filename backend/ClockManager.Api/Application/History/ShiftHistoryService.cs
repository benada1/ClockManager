using ClockManager.Api.Application.Employees;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Api.Application.History;

/// <summary>
/// <c>GET /api/me/shifts</c> (plans/06-history.md): the caller's shifts for a Zurich date range, grouped by work date.
/// Read-only. The server clock is used only for <c>forgotten_clock_out</c>, and only when a trusted time exists.
/// </summary>
public sealed class ShiftHistoryService(
    ICurrentEmployee currentEmployee,
    ClockManagerDbContext db,
    IServerNowProvider serverNow,
    IOptions<ShiftRulesOptions> rules,
    ZurichTime zurich)
{
    public const int MaxRangeDays = 31;
    public const string BothOrNoneMessage = "from and to must be given together";
    public const string InvalidDateMessage = "must be a valid date as yyyy-MM-dd";
    public const string BeforeFromMessage = "must not be before from";
    public const string TooLongMessage = "the range must be at most 31 days";

    // Keeps the SQL date column and NodaTime arithmetic far from their limits.
    private static readonly LocalDate MinDate = new(1900, 1, 1);
    private static readonly LocalDate MaxDate = new(2200, 12, 31);

    /// <summary>Validates the raw query text. Messages are fixed sentences and never echo the input.</summary>
    public static (LocalDate From, LocalDate To)? TryParseRange(string? from, string? to, IDictionary<string, string> errors)
    {
        if ((from is null) != (to is null))
        {
            errors[from is null ? "from" : "to"] = BothOrNoneMessage;
            return null;
        }
        if (from is null || to is null) return null; // no range: the caller picks the default week

        var f = ParseDate(from);
        var t = ParseDate(to);
        if (f is null) errors["from"] = InvalidDateMessage;
        if (t is null) errors["to"] = InvalidDateMessage;
        if (f is null || t is null) return null;
        if (t < f) errors["to"] = BeforeFromMessage;
        else if (Period.Between(f.Value, t.Value, PeriodUnits.Days).Days >= MaxRangeDays) errors["to"] = TooLongMessage;
        return errors.Count == 0 ? (f.Value, t.Value) : null;
    }

    private static LocalDate? ParseDate(string text)
    {
        var result = LocalDatePattern.Iso.Parse(text);
        return result.Success && result.Value >= MinDate && result.Value <= MaxDate ? result.Value : null;
    }

    public async Task<ShiftHistoryView> GetAsync((LocalDate From, LocalDate To)? range, CancellationToken ct)
    {
        var employee = await currentEmployee.RequireActiveAsync(ct); // 401 / 403, same path as a punch

        if (range is null)
        {
            // Default: the week of the latest shift (work date, then Id). No clock needed.
            var dates = await db.Shifts.AsNoTracking()
                .Where(s => s.EmployeeId == employee.Id)
                .OrderByDescending(s => s.ClockIn.ZurichDate).ThenByDescending(s => s.Id)
                .Select(s => s.ClockIn.ZurichDate)
                .Take(1)
                .ToListAsync(ct);
            if (dates.Count == 0) return new ShiftHistoryView(null, null, [], 0);
            range = ShiftHistoryBuilder.WeekOf(dates[0]);
        }

        var (from, to) = range.Value;
        var shifts = await db.Shifts.AsNoTracking()
            .Where(s => s.EmployeeId == employee.Id && s.ClockIn.ZurichDate >= from && s.ClockIn.ZurichDate <= to)
            .ToListAsync(ct);

        // "Now" is only needed to flag a forgotten open shift; skip the lookup when none is open.
        Instant? now = null;
        if (shifts.Any(s => s.ClockOut is null)) now = (await serverNow.GetAsync(ct))?.Utc;

        return ShiftHistoryBuilder.Build(from, to, shifts, now, rules.Value, zurich);
    }
}
