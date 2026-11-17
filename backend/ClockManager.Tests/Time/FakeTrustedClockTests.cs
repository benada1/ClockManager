using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Tests.Fakes;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Time;

/// <summary>The fake later phases depend on behaves like the real contract.</summary>
public sealed class FakeTrustedClockTests
{
    [Fact]
    public async Task Returns_the_set_instant_with_the_tzdb_offset()
    {
        var clock = new FakeTrustedClock("2026-10-25T00:30:00Z");

        var first = await clock.GetNowAsync(CancellationToken.None);
        clock.Set("2026-10-25T01:15:00Z");
        var second = await clock.GetNowAsync(CancellationToken.None);

        Assert.Equal(Offset.FromHours(2), first.ZurichOffset);
        Assert.Equal(Offset.FromHours(1), second.ZurichOffset);
        Assert.Equal(Duration.FromMinutes(45), second.Utc - first.Utc); // T6
        Assert.Equal(FakeTrustedClock.ProviderName, first.Provider);
        Assert.Equal(2, clock.Calls);
    }

    [Fact]
    public async Task Advance_moves_time_forward()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");

        clock.Advance(Duration.FromMinutes(510));

        Assert.Equal(InstantPattern.ExtendedIso.Parse("2026-01-12T15:30:00Z").Value, (await clock.GetNowAsync(default)).Utc); // T1
    }

    [Fact]
    public async Task FailWith_throws_until_recovered()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");
        clock.FailWith(FakeTrustedClock.Unavailable());

        var ex = await Assert.ThrowsAsync<TrustedTimeException>(() => clock.GetNowAsync(default));
        Assert.Equal(ErrorCodes.TimeUnavailable, ex.Code);

        clock.Recover();
        Assert.NotNull(await clock.GetNowAsync(default));
    }

    [Fact]
    public async Task Honours_cancellation()
    {
        var clock = new FakeTrustedClock("2026-01-12T07:00:00Z");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => clock.GetNowAsync(new CancellationToken(canceled: true)));
    }
}
