using ClockManager.Api.Domain;
using NodaTime;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Persistence;

/// <summary><see cref="PunchStamp.From"/> derives the Zurich work date from the instant with tzdb (unit tests, no database).</summary>
public sealed class PunchStampTests
{
    [Theory]
    [InlineData("2026-01-12T21:00:00Z", 2026, 1, 12, 1)]   // T3 clock-in: 22:00 local, the shift belongs to Jan 12
    [InlineData("2026-12-31T22:00:00Z", 2026, 12, 31, 1)]  // T8 clock-in: 23:00 local, belongs to Dec 31 (ISO 2026-W53)
    [InlineData("2026-10-25T00:30:00Z", 2026, 10, 25, 2)]  // T6: 02:30 in the first (summer) pass of the repeated hour
    [InlineData("2026-10-25T01:15:00Z", 2026, 10, 25, 1)]  // T6: 02:15 in the second (winter) pass
    [InlineData("2026-03-29T01:00:00Z", 2026, 3, 29, 2)]   // T7: 03:00 +02:00, never 02:00
    public void From_takes_the_Zurich_date_and_offset_of_the_instant(string isoUtc, int year, int month, int day, int offsetHours)
    {
        var stamp = Stamp(isoUtc);

        Assert.Equal(Utc(isoUtc), stamp.Utc);
        Assert.Equal(new LocalDate(year, month, day), stamp.ZurichDate);
        Assert.Equal(Offset.FromHours(offsetHours), stamp.ZurichOffset);
    }

    [Fact]
    public void From_rejects_an_offset_that_tzdb_does_not_give_for_the_instant()
    {
        // T1 (winter) reported as +02:00: a bug upstream, never stored.
        var wrong = Trusted(Utc("2026-01-12T07:00:00Z")) with { ZurichOffset = Offset.FromHours(2) };

        Assert.Throws<ArgumentException>(() => PunchStamp.From(wrong, Zurich));
    }

    [Fact]
    public void From_rejects_a_cross_check_provider_without_its_delta()
    {
        var halfCrossChecked = Trusted(Utc("2026-01-12T07:00:00Z")) with { CrossCheckDeltaMs = null };

        Assert.Throws<ArgumentException>(() => PunchStamp.From(halfCrossChecked, Zurich));
    }

    [Fact]
    public void Close_refuses_a_clock_out_that_is_not_after_the_clock_in()
    {
        var shift = new Shift(1, Stamp("2026-01-12T07:00:00Z"));

        Assert.Throws<InvalidOperationException>(() => shift.Close(Stamp("2026-01-12T07:00:00Z")));     // T11
        Assert.Throws<InvalidOperationException>(() => shift.Close(Stamp("2026-01-12T06:59:58Z")));     // T10
        Assert.True(shift.IsOpen);
    }
}
