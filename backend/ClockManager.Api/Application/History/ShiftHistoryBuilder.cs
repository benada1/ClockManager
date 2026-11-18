using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Domain;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Api.Application.History;

/// <summary>Pure grouping and totals for the history (no I/O). Durations come from UTC instants only.</summary>
public static class ShiftHistoryBuilder
{
    /// <summary>The Monday-to-Sunday week containing <paramref name="date"/>.</summary>
    public static (LocalDate From, LocalDate To) WeekOf(LocalDate date)
    {
        var monday = date.With(DateAdjusters.PreviousOrSame(IsoDayOfWeek.Monday));
        return (monday, monday.PlusDays(6));
    }

    /// <param name="now">Trusted "now" for <c>forgotten_clock_out</c> only; null leaves that flag out (D-036).</param>
    public static ShiftHistoryView Build(
        LocalDate from, LocalDate to, IEnumerable<Shift> shifts, Instant? now, ShiftRulesOptions rules, ZurichTime zurich)
    {
        var days = shifts
            .GroupBy(s => s.ClockIn.ZurichDate)
            .OrderBy(g => g.Key)
            .Select(g =>
            {
                var views = g.OrderBy(s => s.ClockIn.Utc).ThenBy(s => s.Id).Select(s => ToView(s, now, rules)).ToList();
                return new HistoryDayView(
                    LocalDatePattern.Iso.Format(g.Key),
                    views.Sum(v => v.DurationSeconds ?? 0),
                    IsDstDay(g.Key, zurich) ? [HistoryNotes.DstChange] : [],
                    views);
            })
            .ToList();
        return new ShiftHistoryView(LocalDatePattern.Iso.Format(from), LocalDatePattern.Iso.Format(to), days, days.Sum(d => d.TotalSeconds));
    }

    /// <summary>True when that Zurich day is not 24 hours long (tz rules, not hard-coded dates).</summary>
    public static bool IsDstDay(LocalDate date, ZurichTime zurich)
    {
        var length = zurich.Zone.AtStartOfDay(date.PlusDays(1)).ToInstant() - zurich.Zone.AtStartOfDay(date).ToInstant();
        return length != Duration.FromHours(24);
    }

    private static HistoryShiftView ToView(Shift s, Instant? now, ShiftRulesOptions rules)
    {
        var flags = ShiftFlags.For(s, rules).ToList();
        if (now is { } n && ShiftFlags.IsForgotten(s, n, rules)) flags.Add(ShiftFlags.ForgottenClockOut);
        return new HistoryShiftView(
            s.Id,
            LocalDatePattern.Iso.Format(s.ClockIn.ZurichDate),
            new HistoryPunchView(ShiftViewMapper.FormatZurich(s.ClockIn.Utc, s.ClockIn.ZurichOffset)),
            s.ClockOut is null ? null : new HistoryPunchView(ShiftViewMapper.FormatZurich(s.ClockOut.Utc, s.ClockOut.ZurichOffset)),
            s.ClockOut is null ? null : (long)(s.ClockOut.Utc - s.ClockIn.Utc).TotalSeconds,
            s.ClockOut is not null && s.ClockOut.ZurichDate > s.ClockIn.ZurichDate,
            s.ClockOut is null,
            flags);
    }
}
