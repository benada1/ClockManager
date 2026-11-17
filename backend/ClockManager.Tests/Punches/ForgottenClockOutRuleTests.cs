using ClockManager.Api.Application.Shifts;
using ClockManager.Api.Domain;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>P13, second half: an OPEN shift older than <c>MaxOpenShiftHours</c> is a forgotten clock-out (strict, like long_shift; D-031, D-036). No DB.</summary>
public sealed class ForgottenClockOutRuleTests
{
    private static readonly ShiftRulesOptions Rules = new(); // 16 h

    private static Shift Open() => new(1, Stamp("2026-01-12T07:00:00Z")); // T9's clock-in

    [Theory]
    [InlineData("2026-01-12T22:59:59Z", false)] // 15 h 59 min 59 s
    [InlineData("2026-01-12T23:00:00Z", false)] // exactly 16 h: not flagged (strict, like long_shift)
    [InlineData("2026-01-12T23:00:00.001Z", true)]
    [InlineData("2026-01-13T00:00:00Z", true)]  // 17 h
    [InlineData("2026-01-13T07:00:00Z", true)]  // T9: still open 24 h later
    [InlineData("2026-01-12T07:00:00Z", false)] // "now" equals the clock-in
    [InlineData("2026-01-12T06:59:58Z", false)] // a cached "now" slightly before the clock-in (just punched): never flagged
    public void P13_open_shift_is_forgotten_only_when_strictly_older_than_the_limit(string nowUtc, bool expected)
    {
        Assert.Equal(expected, ShiftFlags.IsForgotten(Open(), Utc(nowUtc), Rules));
    }

    [Fact]
    public void The_limit_follows_the_configuration()
    {
        var eight = new ShiftRulesOptions { MaxOpenShiftHours = 8 };

        Assert.False(ShiftFlags.IsForgotten(Open(), Utc("2026-01-12T15:00:00Z"), eight));
        Assert.True(ShiftFlags.IsForgotten(Open(), Utc("2026-01-12T15:00:01Z"), eight));
    }

    [Fact]
    public void The_duration_is_taken_from_UTC_across_the_DST_start()
    {
        // In 2026-03-28T21:00Z (22:00 +01:00), "now" = 16 h later is 2026-03-29T13:00Z = 15:00 +02:00 on the wall clock,
        // which looks like only 17 h on the wall but is exactly 16 h in UTC: not flagged. One second more is.
        var shift = new Shift(1, Stamp("2026-03-28T21:00:00Z"));

        Assert.False(ShiftFlags.IsForgotten(shift, Utc("2026-03-29T13:00:00Z"), Rules));
        Assert.True(ShiftFlags.IsForgotten(shift, Utc("2026-03-29T13:00:01Z"), Rules));
    }

    [Fact]
    public void A_closed_shift_is_never_forgotten()
    {
        var shift = Open();
        shift.Close(Stamp("2026-01-12T15:30:00Z"));

        Assert.False(ShiftFlags.IsForgotten(shift, Utc("2026-02-01T00:00:00Z"), Rules));
    }
}
