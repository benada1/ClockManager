using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Time;

/// <summary>
/// The 10 s cache behind <c>GET /health/time</c> (plans/01-trusted-clock.md §4.6, D-012) and its mapping to the
/// public report. Freshness is driven by a <see cref="FakeElapsedTimer"/>; nothing sleeps.
/// </summary>
public sealed class TimeHealthCheckTests
{
    private const string TimeNow = "time.now";
    private const string TimeApi = "timeapi.io";

    private static readonly Instant T1 = InstantPattern.ExtendedIso.Parse("2026-01-12T07:00:00Z").Value; // matrix T1

    private readonly FakeElapsedTimer _timer = new();

    // ---------- Caching ----------

    [Fact]
    public async Task Second_call_within_10_s_is_served_from_cache_and_after_expiry_asks_again()
    {
        var (primary, secondary) = BothOk();
        var health = Health(primary, secondary);

        await health.GetAsync(CancellationToken.None);
        _timer.Advance(TimeSpan.FromMilliseconds(9_999));
        await health.GetAsync(CancellationToken.None);

        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, secondary.Calls);

        _timer.Advance(TimeSpan.FromMilliseconds(1)); // exactly 10 s: expired
        await health.GetAsync(CancellationToken.None);

        Assert.Equal(2, primary.Calls);
        Assert.Equal(2, secondary.Calls);
    }

    [Fact]
    public async Task A_failed_report_is_cached_too_so_an_outage_does_not_multiply_provider_calls()
    {
        var down = FakeTimeSource.Failure(TimeNow, TimeSourceFailureReason.Timeout);
        var primary = FakeTimeSource.Sequence(down, FakeTimeSource.Reading(TimeNow, T1));
        var secondary = FakeTimeSource.Sequence(FakeTimeSource.Failure(TimeApi, TimeSourceFailureReason.NetworkError), FakeTimeSource.Reading(TimeApi, T1));
        var health = Health(primary, secondary);

        var first = await health.GetAsync(CancellationToken.None);
        _timer.Advance(TimeSpan.FromSeconds(5));
        var cached = await health.GetAsync(CancellationToken.None);
        _timer.Advance(TimeSpan.FromSeconds(5));
        var refreshed = await health.GetAsync(CancellationToken.None);

        Assert.False(first.CanPunch);
        Assert.Same(first, cached);
        Assert.True(refreshed.CanPunch);
        Assert.Equal(2, primary.Calls);
    }

    [Fact]
    public async Task TTL_counts_from_when_the_answer_arrived_not_from_when_it_was_asked()
    {
        // The providers take 2.9 s (on the fake timer). The report is fresh for 10 s after that.
        var primary = new FakeTimeSource(TimeNow, (_, _) =>
        {
            _timer.Advance(TimeSpan.FromMilliseconds(2_900));
            return Task.FromResult<TimeSourceResult>(FakeTimeSource.Reading(TimeNow, T1));
        });
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1));
        var health = Health(primary, secondary);

        await health.GetAsync(CancellationToken.None);
        _timer.Advance(TimeSpan.FromMilliseconds(9_999));
        await health.GetAsync(CancellationToken.None);

        Assert.Equal(1, primary.Calls);
    }

    [Fact]
    public async Task Concurrent_callers_during_a_refresh_share_one_provider_call()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = FakeTimeSource.Gated(FakeTimeSource.Reading(TimeNow, T1), gate.Task);
        var secondary = FakeTimeSource.Gated(FakeTimeSource.Reading(TimeApi, T1), gate.Task);
        var health = Health(primary, secondary);

        var callers = Enumerable.Range(0, 20).Select(_ => Task.Run(() => health.GetAsync(CancellationToken.None))).ToList();
        await primary.Called;
        gate.SetResult();
        var reports = await Task.WhenAll(callers);

        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, secondary.Calls);
        Assert.All(reports, r => Assert.Same(reports[0], r));
    }

    [Fact]
    public async Task A_caller_that_gives_up_does_not_cancel_the_refresh_others_wait_for()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = FakeTimeSource.Gated(FakeTimeSource.Reading(TimeNow, T1), gate.Task);
        var secondary = FakeTimeSource.Gated(FakeTimeSource.Reading(TimeApi, T1), gate.Task);
        var health = Health(primary, secondary);
        using var leaving = new CancellationTokenSource();

        var impatient = health.GetAsync(leaving.Token);
        var patient = health.GetAsync(CancellationToken.None);
        await primary.Called;
        await leaving.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => impatient);
        Assert.False(primary.ReceivedToken.IsCancellationRequested);
        gate.SetResult();
        Assert.True((await patient).CanPunch);
        Assert.Equal(1, primary.Calls);
    }

    [Fact]
    public async Task A_refresh_that_throws_is_not_cached()
    {
        var calls = 0;
        var clock = Clock(FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1)), FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1)));
        var health = new TimeHealthCheck(
            ct => ++calls == 1 ? throw new OperationCanceledException("shutting down") : clock.RunAsync(ct),
            _timer,
            CancellationToken.None);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => health.GetAsync(CancellationToken.None));
        var report = await health.GetAsync(CancellationToken.None);

        Assert.True(report.CanPunch);
        Assert.Equal(2, calls);
    }

    // ---------- Mapping to the public report ----------

    [Fact]
    public async Task Both_agree_maps_to_can_punch_agree_with_delta()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 341));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(37), roundTripMs: 402));

        var report = await Health(primary, secondary).GetAsync(CancellationToken.None);

        Assert.True(report.CanPunch);
        Assert.Equal(TimeHealthReport.Agree, report.CrossCheck);
        Assert.Equal(7, report.DeltaMs); // midpoint-corrected (D-008): 37 − (402 − 341) / 2 = 6.5 → 7
        Assert.Null(report.Code);
        Assert.Equal(
            [new ProviderHealth(TimeNow, "ok", 341), new ProviderHealth(TimeApi, "ok", 402)],
            report.Providers);
    }

    [Fact]
    public async Task Single_source_maps_to_can_punch_not_checked()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.HttpFailure(TimeNow, 503, roundTripMs: 120));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1, roundTripMs: 402));

        var report = await Health(primary, secondary).GetAsync(CancellationToken.None);

        Assert.True(report.CanPunch);
        Assert.Equal(TimeHealthReport.NotChecked, report.CrossCheck);
        Assert.Null(report.DeltaMs);
        Assert.Null(report.Code);
        Assert.Equal([new ProviderHealth(TimeNow, "http_error", 120), new ProviderHealth(TimeApi, "ok", 402)], report.Providers);
    }

    [Fact]
    public async Task Disagreement_maps_to_cannot_punch_with_the_503_code()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromSeconds(5)));

        var report = await Health(primary, secondary).GetAsync(CancellationToken.None);

        Assert.False(report.CanPunch);
        Assert.Equal(TimeHealthReport.Disagree, report.CrossCheck);
        Assert.Equal(5000, report.DeltaMs);
        Assert.Equal(ErrorCodes.TimeProvidersDisagree, report.Code);
    }

    [Theory]
    [InlineData("Timeout", "RateLimited", "timeout", "rate_limited", ErrorCodes.TimeUnavailable)]
    [InlineData("NetworkError", "InvalidResponse", "network_error", "invalid_response", ErrorCodes.TimeInvalidResponse)]
    public async Task All_down_maps_to_cannot_punch_with_the_outcomes(
        string p, string s, string pCode, string sCode, string code)
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Failure(TimeNow, Enum.Parse<TimeSourceFailureReason>(p)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Failure(TimeApi, Enum.Parse<TimeSourceFailureReason>(s)));

        var report = await Health(primary, secondary).GetAsync(CancellationToken.None);

        Assert.False(report.CanPunch);
        Assert.Equal(TimeHealthReport.NotChecked, report.CrossCheck);
        Assert.Null(report.DeltaMs);
        Assert.Equal(code, report.Code);
        Assert.Equal([pCode, sCode], report.Providers.Select(x => x.Status));
    }

    // ---------- Helpers ----------

    private static (FakeTimeSource Primary, FakeTimeSource Secondary) BothOk() =>
        (FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1)), FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1)));

    private TimeHealthCheck Health(ITimeSource primary, ITimeSource secondary)
    {
        var clock = Clock(primary, secondary);
        return new TimeHealthCheck(clock.RunAsync, _timer, CancellationToken.None);
    }

    private TrustedClock Clock(ITimeSource primary, ITimeSource secondary)
    {
        var options = Options.Create(new TrustedClockOptions
        {
            Primary = new TimeProviderEndpoint { Name = primary.Name, BaseUrl = "https://primary.test/" },
            Secondary = new TimeProviderEndpoint { Name = secondary.Name, BaseUrl = "https://secondary.test/" },
        });
        return new TrustedClock(primary, secondary, options, _timer, NullLogger<TrustedClock>.Instance);
    }
}
