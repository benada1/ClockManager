using ClockManager.Api.Application.Time;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Time;

/// <summary>tzdb facts the rest of the app relies on, with values from the zurich-time-cases matrix.</summary>
public sealed class ZurichTimeTests
{
    private readonly ZurichTime _zurich = ZurichTime.FromTzdb();

    [Theory]
    [InlineData("2026-01-12T07:00:00Z", "2026-01-12T08:00:00", 1)]  // T1 in
    [InlineData("2026-01-12T15:30:00Z", "2026-01-12T16:30:00", 1)]  // T1 out
    [InlineData("2026-07-06T06:00:00Z", "2026-07-06T08:00:00", 2)]  // T2 in
    [InlineData("2026-07-06T14:00:00Z", "2026-07-06T16:00:00", 2)]  // T2 out
    [InlineData("2026-03-29T01:00:00Z", "2026-03-29T03:00:00", 2)]  // T7: never shown as 02:00
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25T02:30:00", 2)]  // T6: first 02:30
    [InlineData("2026-10-25T01:15:00Z", "2026-10-25T02:15:00", 1)]  // T6: second pass of the hour
    public void Converts_instant_to_zurich_local_with_offset(string utc, string local, int offsetHours)
    {
        var zoned = _zurich.ToLocal(Utc(utc));

        Assert.Equal(LocalDateTimePattern.ExtendedIso.Parse(local).Value, zoned.LocalDateTime);
        Assert.Equal(Offset.FromHours(offsetHours), zoned.Offset);
        Assert.Equal(Offset.FromHours(offsetHours), _zurich.OffsetAt(Utc(utc)));
    }

    [Theory]
    [InlineData("2026-01-12T21:00:00Z", 2026, 1, 12)]   // T3 overnight shift starts on the 12th
    [InlineData("2026-12-31T22:00:00Z", 2026, 12, 31)]  // T8 year boundary
    [InlineData("2026-12-31T23:30:00Z", 2027, 1, 1)]    // already the next day in Zurich
    public void Local_date_is_the_zurich_calendar_date(string utc, int y, int m, int d)
    {
        Assert.Equal(new LocalDate(y, m, d), _zurich.LocalDate(Utc(utc)));
    }

    [Theory]
    [InlineData("2026-03-29T00:59:59Z", 1)]
    [InlineData("2026-03-29T01:00:00Z", 2)]
    [InlineData("2026-10-25T00:59:59Z", 2)]
    [InlineData("2026-10-25T01:00:00Z", 1)]
    [InlineData("2027-03-28T01:00:00Z", 2)]
    [InlineData("2027-10-31T01:00:00Z", 1)]
    [InlineData("2028-03-26T01:00:00Z", 2)]
    [InlineData("2028-10-29T01:00:00Z", 1)]
    public void Dst_transitions_happen_at_0100_utc(string utc, int offsetHours)
    {
        Assert.Equal(Offset.FromHours(offsetHours), _zurich.OffsetAt(Utc(utc)));
    }

    private static Instant Utc(string iso) => InstantPattern.ExtendedIso.Parse(iso).GetValueOrThrow();
}
