using System.Data;
using ClockManager.Api.Application.Employees;
using ClockManager.Api.Application.History;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Domain;
using ClockManager.Api.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Api.Application.Manager;

/// <summary>
/// The manager's read side (plans/07-manager.md §5). Read-only. "Now" comes from <see cref="IServerNowProvider"/> and is
/// used only for flags; without trusted time the time-based flags are left out (D-036).
/// </summary>
public sealed class ManagerService(
    ICurrentEmployee currentEmployee,
    ClockManagerDbContext db,
    IServerNowProvider serverNow,
    ShiftHistoryService history,
    IOptions<ShiftRulesOptions> rules,
    ZurichTime zurich)
{
    public const int MaxAnomalies = 100;

    public async Task<IReadOnlyList<ManagerEmployeeView>> ListEmployeesAsync(CancellationToken ct)
    {
        await currentEmployee.RequireManagerAsync(ct);

        var employees = await db.Employees.AsNoTracking()
            .OrderBy(e => e.DisplayName).ThenBy(e => e.Id)
            .Select(e => new { e.Id, e.Username, e.DisplayName, e.Role, e.IsActive })
            .ToListAsync(ct);
        var open = (await db.Shifts.AsNoTracking().Where(s => s.ClockOut == null).ToListAsync(ct))
            .ToDictionary(s => s.EmployeeId);

        var now = (await serverNow.GetAsync(ct))?.Utc;
        var longToday = new HashSet<int>();
        if (now is { } n)
        {
            // "Long shift today": a closed long shift whose work date is today in Zurich.
            var today = zurich.LocalDate(n);
            var closedToday = await db.Shifts.AsNoTracking()
                .Where(s => s.ClockOut != null && s.ClockIn.ZurichDate == today)
                .ToListAsync(ct);
            longToday = closedToday.Where(s => ShiftFlags.For(s, rules.Value).Contains(ShiftFlags.LongShift))
                .Select(s => s.EmployeeId).ToHashSet();
        }

        return employees.Select(e =>
        {
            open.TryGetValue(e.Id, out var shift);
            var flags = new List<string>(2);
            if (shift is not null && now is { } t && ShiftFlags.IsForgotten(shift, t, rules.Value)) flags.Add(ShiftFlags.ForgottenClockOut);
            if (longToday.Contains(e.Id)) flags.Add(ShiftFlags.LongShift);
            return new ManagerEmployeeView(
                e.Id, e.Username, e.DisplayName, e.Role.ToString(), e.IsActive,
                shift is null ? ManagerStatus.ClockedOut : ManagerStatus.ClockedIn,
                shift is null ? null : Zurich(shift.ClockIn),
                flags);
        }).ToList();
    }

    /// <summary>
    /// Forgotten open shifts (only with trusted time) and long/short closed shifts, newest clock-in first, at most 100.
    /// Filtered in SQL on the UTC instants with the same strict bounds as <see cref="ShiftFlags"/>.
    /// </summary>
    public async Task<IReadOnlyList<AnomalyView>> ListAnomaliesAsync(CancellationToken ct)
    {
        await currentEmployee.RequireManagerAsync(ct);
        var r = rules.Value;
        var now = (await serverNow.GetAsync(ct))?.Utc;

        var nowParam = new SqlParameter("@now", SqlDbType.DateTimeOffset)
        {
            Scale = 7,
            Value = now is { } n ? n.ToDateTimeOffset() : DBNull.Value,
        };
        var hoursParam = new SqlParameter("@hours", SqlDbType.Int) { Value = r.MaxOpenShiftHours };
        var shortParam = new SqlParameter("@short", SqlDbType.Int) { Value = r.ShortShiftSeconds };
        var shifts = await db.Shifts
            .FromSqlRaw(
                "SELECT * FROM [Shifts] WHERE"
                + " ([ClockOut_Utc] IS NULL AND @now IS NOT NULL AND [ClockIn_Utc] < DATEADD(HOUR, -@hours, @now))"
                + " OR [ClockOut_Utc] > DATEADD(HOUR, @hours, [ClockIn_Utc])"
                + " OR [ClockOut_Utc] < DATEADD(SECOND, @short, [ClockIn_Utc])",
                nowParam, hoursParam, shortParam)
            .AsNoTracking()
            .OrderByDescending(s => s.ClockIn.Utc).ThenByDescending(s => s.Id)
            .Take(MaxAnomalies)
            .ToListAsync(ct);

        var ids = shifts.Select(s => s.EmployeeId).Distinct().ToList();
        var names = await db.Employees.AsNoTracking().Where(e => ids.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);

        return shifts.Select(s => new AnomalyView(
            s.Id,
            s.EmployeeId,
            names[s.EmployeeId],
            LocalDatePattern.Iso.Format(s.ClockIn.ZurichDate),
            s.ClockOut is null ? ShiftFlags.ForgottenClockOut : ShiftFlags.For(s, r).FirstOrDefault() ?? "unknown", // SQL and C# rules disagree: never throw
            Zurich(s.ClockIn),
            s.ClockOut is null ? null : Zurich(s.ClockOut),
            s.ClockOut is null ? null : (long)(s.ClockOut.Utc - s.ClockIn.Utc).TotalSeconds)).ToList();
    }

    public async Task<ManagerShiftHistoryView> GetEmployeeShiftsAsync(int employeeId, (LocalDate From, LocalDate To)? range, CancellationToken ct)
    {
        await currentEmployee.RequireManagerAsync(ct);
        var employee = await db.Employees.AsNoTracking().Where(e => e.Id == employeeId)
            .Select(e => new EmployeeRefView(e.Id, e.DisplayName)).SingleOrDefaultAsync(ct) ?? throw ManagerErrors.EmployeeNotFound();
        var view = await history.QueryAsync(employeeId, range, withRowVersion: true, ct);
        return new ManagerShiftHistoryView(view.From, view.To, view.Days, view.TotalSeconds, employee);
    }

    public async Task<ShiftDetailView> GetShiftAsync(long shiftId, CancellationToken ct)
    {
        await currentEmployee.RequireManagerAsync(ct);
        return await BuildDetailAsync(shiftId, ct);
    }

    /// <summary>The shift (history shape + rowVersion), its employee and all its corrections, oldest first. No auth check.</summary>
    public async Task<ShiftDetailView> BuildDetailAsync(long shiftId, CancellationToken ct)
    {
        var shift = await db.Shifts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shiftId, ct) ?? throw ManagerErrors.ShiftNotFound();
        var employee = await db.Employees.AsNoTracking().Where(e => e.Id == shift.EmployeeId)
            .Select(e => new EmployeeRefView(e.Id, e.DisplayName)).SingleAsync(ct);
        var corrections = await (
                from c in db.ShiftCorrections.AsNoTracking()
                join m in db.Employees.AsNoTracking() on c.ManagerId equals m.Id
                where c.ShiftId == shiftId
                orderby c.Id
                select new { Correction = c, m.DisplayName })
            .ToListAsync(ct);

        Instant? now = shift.IsOpen ? (await serverNow.GetAsync(ct))?.Utc : null;
        var corrected = corrections.Count > 0 ? new HashSet<long> { shiftId } : new HashSet<long>();
        var view = ShiftHistoryBuilder.ToView(shift, now, rules.Value, corrected, withRowVersion: true);

        return new ShiftDetailView(view, employee, corrections.Select(x => new CorrectionView(
            x.Correction.Id,
            x.Correction.Field == ShiftCorrectionField.ClockIn ? CorrectionFields.ClockIn : CorrectionFields.ClockOut,
            x.Correction.OldUtc is { } old ? Zurich(old) : null,
            Zurich(x.Correction.NewUtc),
            x.Correction.Reason,
            new EmployeeRefView(x.Correction.ManagerId, x.DisplayName),
            Zurich(x.Correction.CreatedUtc))).ToList());
    }

    private static HistoryPunchView Zurich(PunchStamp stamp) => new(ShiftViewMapper.FormatZurich(stamp.Utc, stamp.ZurichOffset));

    // Audit instants carry no stored offset: tzdb at that instant.
    private HistoryPunchView Zurich(Instant utc) => new(ShiftViewMapper.FormatZurich(utc, zurich.OffsetAt(utc)));
}
