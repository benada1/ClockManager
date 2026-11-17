using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using static ClockManager.Tests.Persistence.PersistenceTestData;

namespace ClockManager.Tests.Punches;

/// <summary>
/// The status endpoint's server-now cache on its own (plans/03-punch-api.md Q3, D-035). No DB, no network: a
/// <see cref="FakeTrustedClock"/> counts provider calls and a <see cref="FakeElapsedTimer"/> is moved by hand, so no test sleeps.
/// </summary>
public sealed class ServerNowProviderTests
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(10);

    private readonly FakeTrustedClock _clock = new("2026-01-12T07:00:00Z");
    private readonly FakeElapsedTimer _timer = new();
    private readonly CancellationTokenSource _stopping = new();

    private ServerNowProvider NewProvider() =>
        new(_clock.GetNowAsync, _timer, Ttl, _stopping.Token, NullLogger<ServerNowProvider>.Instance);

    [Fact]
    public async Task A_fresh_fetch_has_age_zero_and_the_trusted_instant_with_its_Zurich_offset()
    {
        var provider = NewProvider();

        var now = await provider.GetAsync(CancellationToken.None);

        Assert.NotNull(now);
        Assert.Equal(Utc("2026-01-12T07:00:00Z"), now.Utc);
        Assert.Equal(Offset.FromHours(1), now.ZurichOffset);
        Assert.Equal(TimeSpan.Zero, now.Age);
        Assert.Equal(1, _clock.Calls);
    }

    [Fact]
    public async Task Two_calls_within_the_TTL_make_one_provider_call_and_serve_the_same_instant()
    {
        var provider = NewProvider();

        var first = await provider.GetAsync(CancellationToken.None);
        _clock.Set("2026-01-12T07:00:09Z"); // the real time moved on, the cache must not notice
        _timer.Advance(TimeSpan.FromSeconds(9.9));
        var second = await provider.GetAsync(CancellationToken.None);

        Assert.Equal(1, _clock.Calls);
        Assert.Equal(first!.Utc, second!.Utc);
    }

    [Fact]
    public async Task The_age_is_the_monotonic_time_since_the_fetch_and_the_instant_is_not_extrapolated()
    {
        var provider = NewProvider();
        await provider.GetAsync(CancellationToken.None);

        _timer.Advance(TimeSpan.FromMilliseconds(3200));
        var now = await provider.GetAsync(CancellationToken.None);

        Assert.Equal(TimeSpan.FromMilliseconds(3200), now!.Age);
        Assert.Equal(Utc("2026-01-12T07:00:00Z"), now.Utc); // as fetched, NOT 07:00:03.2
        Assert.Equal(1, _clock.Calls);
    }

    [Theory]
    [InlineData(10)]   // exactly the TTL counts as expired (strictly less than the TTL is fresh)
    [InlineData(10.5)]
    [InlineData(60)]
    public async Task A_call_at_or_after_the_TTL_fetches_again_and_returns_the_new_instant(double seconds)
    {
        var provider = NewProvider();
        await provider.GetAsync(CancellationToken.None);
        _clock.Set("2026-01-12T07:00:12Z");
        _timer.Advance(TimeSpan.FromSeconds(seconds));

        var now = await provider.GetAsync(CancellationToken.None);

        Assert.Equal(2, _clock.Calls);
        Assert.Equal(Utc("2026-01-12T07:00:12Z"), now!.Utc);
        Assert.Equal(TimeSpan.Zero, now.Age);
    }

    [Fact]
    public async Task Concurrent_callers_share_one_in_flight_fetch()
    {
        var provider = NewProvider();
        _clock.Hold(); // the "provider" is slow: all callers arrive while the first fetch is in flight

        var callers = Enumerable.Range(0, 8).Select(_ => provider.GetAsync(CancellationToken.None)).ToArray();
        Assert.Equal(1, _clock.Calls);
        _clock.Release();
        var results = await Task.WhenAll(callers);

        Assert.Equal(1, _clock.Calls);
        Assert.All(results, r => Assert.Equal(Utc("2026-01-12T07:00:00Z"), r!.Utc));
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_fetch_the_others_wait_for()
    {
        var provider = NewProvider();
        _clock.Hold();
        using var giveUp = new CancellationTokenSource();
        var impatient = provider.GetAsync(giveUp.Token);
        var patient = provider.GetAsync(CancellationToken.None);

        giveUp.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        _clock.Release();

        Assert.NotNull(await patient);
        Assert.Equal(1, _clock.Calls);
    }

    // ---- Failures (P20): null, never an exception, remembered for 1 s only ----

    [Fact]
    public async Task No_trusted_time_gives_null_not_an_exception()
    {
        _clock.FailWith(FakeTrustedClock.Unavailable());
        var provider = NewProvider();

        Assert.Null(await provider.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task An_unexpected_error_in_the_fetch_also_gives_null()
    {
        var provider = new ServerNowProvider(
            _ => throw new InvalidOperationException("secret detail"), _timer, Ttl, _stopping.Token, NullLogger<ServerNowProvider>.Instance);

        Assert.Null(await provider.GetAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_failure_is_remembered_for_one_second_so_a_polling_UI_does_not_hammer_the_providers()
    {
        _clock.FailWith(FakeTrustedClock.Unavailable());
        var provider = NewProvider();

        await provider.GetAsync(CancellationToken.None);
        _timer.Advance(TimeSpan.FromMilliseconds(900));
        Assert.Null(await provider.GetAsync(CancellationToken.None));

        Assert.Equal(1, _clock.Calls);
    }

    [Fact]
    public async Task A_failure_is_not_remembered_beyond_one_second_recovery_shows_at_once()
    {
        _clock.FailWith(FakeTrustedClock.Unavailable());
        var provider = NewProvider();
        await provider.GetAsync(CancellationToken.None);
        _clock.Recover();
        _timer.Advance(ServerNowProvider.FailureTtl);

        var now = await provider.GetAsync(CancellationToken.None);

        Assert.NotNull(now);
        Assert.Equal(2, _clock.Calls);
    }

    [Fact]
    public async Task An_expired_success_is_never_served_stale_when_the_provider_is_down_now()
    {
        var provider = NewProvider();
        await provider.GetAsync(CancellationToken.None);
        _clock.FailWith(FakeTrustedClock.Unavailable());
        _timer.Advance(Ttl);

        Assert.Null(await provider.GetAsync(CancellationToken.None)); // not the old 07:00:00
    }

    [Fact]
    public async Task Shutdown_cancels_the_waiters_and_nothing_is_cached()
    {
        var provider = NewProvider();
        _clock.Hold();
        var waiting = provider.GetAsync(CancellationToken.None);

        _stopping.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
    }

    // ---- It is not a trusted clock ----

    [Fact]
    public void ServerNowProvider_cannot_be_used_as_a_trusted_clock_for_a_record()
    {
        Assert.False(typeof(ITrustedClock).IsAssignableFrom(typeof(ServerNowProvider)));
        Assert.False(typeof(ITrustedClock).IsAssignableFrom(typeof(IServerNowProvider)));
        // It does not even hand out a TrustedInstant, which is what PunchStamp.From needs.
        Assert.Equal(typeof(Task<ServerNowReading?>), typeof(IServerNowProvider).GetMethod(nameof(IServerNowProvider.GetAsync))!.ReturnType);
    }
}
