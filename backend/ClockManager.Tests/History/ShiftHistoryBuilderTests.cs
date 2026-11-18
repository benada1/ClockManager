using ClockManager.Api.Application.History;
using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Domain;
using NodaTime;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.History;

/// <summary>The pure grouping and totals logic of the history, no database.</summary>
public sealed class ShiftHistoryBuilderTests
{
    private static readonly ShiftRulesOptions Rules = new();

    private static Shift Closed(string inUtc, string outUtc)
    {
        var shift = new Shift(1, Stamp(inUtc));
        shift.Close(Stamp(outUtc));
        return shift;
    }

    [Theory]
    [InlineData("2026-11-16", "2026-11-16", "2026-11-22")] // Monday
    [InlineData("2026-11-22", "2026-11-16", "2026-11-22")] // Sunday
    [InlineData("2026-10-25", "2026-10-19", "2026-10-25")] // the autumn change day, a Sunday
    [InlineData("2027-01-01", "2026-12-28", "2027-01-03")] // across the year
    public void A_week_runs_Monday_to_Sunday(string date, string from, string to)
    {
        var (f, t) = ShiftHistoryBuilder.WeekOf(new LocalDate(int.Parse(date[..4]), int.Parse(date[5..7]), int.Parse(date[8..])));

        Assert.Equal(from, f.ToString("uuuu-MM-dd", null));
        Assert.Equal(to, t.ToString("uuuu-MM-dd", null));
    }

    [Theory]
    [InlineData(2026, 3, 29, true)]   // 23 h
    [InlineData(2026, 10, 25, true)]  // 25 h
    [InlineData(2026, 3, 28, false)]
    [InlineData(2026, 10, 24, false)]
    [InlineData(2026, 7, 6, false)]
    public void Only_days_that_are_not_24_hours_long_are_dst_days(int y, int m, int d, bool expected) =>
        Assert.Equal(expected, ShiftHistoryBuilder.IsDstDay(new LocalDate(y, m, d), Zurich));

    [Fact]
    public void An_open_shift_is_listed_but_adds_nothing_to_the_totals()
    {
        var open = new Shift(1, Stamp("2026-11-17T12:00:00Z"));
        var shifts = new[] { Closed("2026-11-17T07:00:00Z", "2026-11-17T08:00:00Z"), open };

        var view = ShiftHistoryBuilder.Build(new LocalDate(2026, 11, 16), new LocalDate(2026, 11, 22), shifts, null, Rules, Zurich);

        Assert.Equal(3600, view.TotalSeconds);
        Assert.Equal(3600, view.Days[0].TotalSeconds);
        Assert.Equal(2, view.Days[0].Shifts.Count);
        Assert.True(view.Days[0].Shifts[1].Open);
        Assert.Null(view.Days[0].Shifts[1].DurationSeconds);
    }

    [Fact]
    public void Duration_comes_from_utc_across_the_spring_change_and_ignores_the_wall_clock()
    {
        var shifts = new[] { Closed("2026-03-28T21:00:00Z", "2026-03-29T04:00:00Z") }; // 22:00 to 06:00 locally

        var view = ShiftHistoryBuilder.Build(new LocalDate(2026, 3, 23), new LocalDate(2026, 3, 29), shifts, null, Rules, Zurich);

        Assert.Equal(25200, view.TotalSeconds);
    }
}
