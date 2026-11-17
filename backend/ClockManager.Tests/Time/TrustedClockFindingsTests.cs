using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Time;

/// <summary>
/// Orchestrator behaviour decided after qa's step-2 review (D-008): QA-04 (round trips widen the tolerance),
/// QA-05 (the rounded delta that is reported is the one compared) and QA-06 (a source that throws is a failure of
/// that source, not of the whole clock). Same conventions as <see cref="TrustedClockTests"/>: fake sources, a fake
/// monotonic timer, both requests started at the same timestamp unless stated otherwise.
/// </summary>
public sealed class TrustedClockFindingsTests
{
    private const string TimeNow = "time.now";
    private const string TimeApi = "timeapi.io";

    private static readonly Instant T1 = InstantPattern.ExtendedIso.Parse("2026-01-12T07:00:00Z").GetValueOrThrow(); // matrix T1

    private readonly FakeElapsedTimer _timer = new();

    public TrustedClockFindingsTests() => _timer.Advance(TimeSpan.FromSeconds(10));

    // ---------- QA-04: effective tolerance = max(CrossCheckToleranceMs, (RttA + RttB) / 2) ----------

    [Fact]
    public async Task Slow_round_trips_widen_the_tolerance_QA04()
    {
        // Both took 2.8 s: each reading could have been taken anywhere in its 2.8 s window, so two correct clocks
        // can look up to 2.8 s apart. 2.5 s is inside that, although it is above the 2 s tolerance.
        var result = await Clock(Reading(TimeNow, T1, 2800), Reading(TimeApi, T1 + Duration.FromMilliseconds(2500), 2800))
            .GetNowAsync(CancellationToken.None);

        Assert.Equal(TimeApi, result.CrossCheckedBy);
        Assert.Equal(2500, result.CrossCheckDeltaMs);
        Assert.True(result.LowPrecision);
    }

    [Fact]
    public async Task Fast_round_trips_keep_the_configured_tolerance_QA04()
    {
        // Same 2.5 s gap, but both answered in 0.4 s: timing explains at most 0.4 s, so the 2 s tolerance applies.
        var ex = await Assert.ThrowsAsync<TrustedTimeException>(() =>
            Clock(Reading(TimeNow, T1, 400), Reading(TimeApi, T1 + Duration.FromMilliseconds(2500), 400))
                .GetNowAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.TimeProvidersDisagree, ex.Code);
    }

    [Theory]
    [InlineData(2599, true)]
    [InlineData(2600, false)] // reaching the bound needs zero network delay one way: not explainable by timing
    public async Task Round_trip_bound_is_exclusive_QA04(int secondaryAheadMs, bool agrees)
    {
        // (2600 + 2600) / 2 = 2600 ms bound, above the 2000 ms tolerance.
        var clock = Clock(Reading(TimeNow, T1, 2600), Reading(TimeApi, T1 + Duration.FromMilliseconds(secondaryAheadMs), 2600));

        if (agrees)
        {
            Assert.Equal(secondaryAheadMs, (await clock.GetNowAsync(CancellationToken.None)).CrossCheckDeltaMs);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<TrustedTimeException>(() => clock.GetNowAsync(CancellationToken.None));
            Assert.Equal(ErrorCodes.TimeProvidersDisagree, ex.Code);
        }
    }

    [Fact]
    public async Task Round_trip_bound_uses_the_sum_of_both_round_trips_QA04()
    {
        // RTT 1400 and 2900 → bound (1400 + 2900) / 2 = 2150 ms. Both start together, so the midpoints are 750 ms
        // apart; a correct secondary reports +750 ms. It reports +2850 ms: corrected delta 2100, above the 2000 ms
        // tolerance but below the 2150 ms bound → agrees.
        var result = await Clock(Reading(TimeNow, T1, 1400), Reading(TimeApi, T1 + Duration.FromMilliseconds(2850), 2900))
            .GetNowAsync(CancellationToken.None);

        Assert.Equal(2100, result.CrossCheckDeltaMs);
    }

    // ---------- QA-05: the reported (rounded) delta is the compared delta ----------

    [Theory]
    [InlineData(2_000_499_999, true, 2000)]  // rounds to 2000 → reported 2000 and agrees (inclusive)
    [InlineData(2_000_500_000, false, 2001)] // rounds to 2001 → disagrees; never "2000 ms, disagree"
    public async Task Compares_the_same_rounded_delta_it_reports_QA05(long secondaryAheadNs, bool agrees, int roundedMs)
    {
        var clock = Clock(Reading(TimeNow, T1, 300), Reading(TimeApi, T1 + Duration.FromNanoseconds(secondaryAheadNs), 300));

        var run = await clock.RunAsync(CancellationToken.None);

        Assert.Equal(roundedMs, run.CrossCheck!.DeltaMs);
        Assert.Equal(agrees, run.CrossCheck.Agrees);
        Assert.Equal(agrees, run.Accepted is not null);
        if (agrees) Assert.Equal(roundedMs, run.Accepted!.CrossCheckDeltaMs);
    }

    // ---------- QA-06: a source that throws is that source failing ----------

    [Fact]
    public async Task Primary_that_throws_falls_back_to_secondary_QA06()
    {
        var primary = Throwing(TimeNow, new InvalidOperationException("parser bug"));
        var secondary = FakeTimeSource.Returning(Reading(TimeApi, T1, 420));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(TimeApi, result.Provider);
        Assert.Equal(T1, result.Utc);
        Assert.Null(result.CrossCheckedBy);
        Assert.Null(result.CrossCheckDeltaMs);
    }

    [Fact]
    public async Task Secondary_that_throws_leaves_primary_single_source_QA06()
    {
        var primary = FakeTimeSource.Returning(Reading(TimeNow, T1, 350));
        var secondary = Throwing(TimeApi, new NullReferenceException());

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(TimeNow, result.Provider);
        Assert.Null(result.CrossCheckedBy);
    }

    [Fact]
    public async Task Source_that_throws_synchronously_is_also_contained_QA06()
    {
        // Throws from FetchAsync itself, before returning a Task: the other source must still be asked.
        var primary = new FakeTimeSource(TimeNow, (_, _) => throw new FormatException("sync"));
        var secondary = FakeTimeSource.Returning(Reading(TimeApi, T1, 300));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(TimeApi, result.Provider);
        Assert.Equal(1, secondary.Calls);
    }

    [Fact]
    public async Task Own_OperationCanceledException_without_caller_cancellation_is_a_source_failure_QA06()
    {
        // A source that cancels itself (a bug, or a timeout it didn't map) is not the caller going away.
        var primary = Throwing(TimeNow, new OperationCanceledException("source's own token"));
        var secondary = FakeTimeSource.Returning(Reading(TimeApi, T1, 300));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(TimeApi, result.Provider);
    }

    [Fact]
    public async Task Both_throwing_fails_closed_as_unavailable_with_error_outcomes_QA06()
    {
        var ex = await Assert.ThrowsAsync<TrustedTimeException>(() =>
            Clock(Throwing(TimeNow, new InvalidOperationException()), Throwing(TimeApi, new InvalidOperationException()))
                .GetNowAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.TimeUnavailable, ex.Code);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal([new ProviderOutcome(TimeNow, "error"), new ProviderOutcome(TimeApi, "error")], ex.Providers);
    }

    [Fact]
    public async Task Throwing_source_is_logged_at_error_with_the_exception_QA06()
    {
        var logger = new CapturingLogger();
        var boom = new InvalidOperationException("parser bug");

        await Clock(Throwing(TimeNow, boom), FakeTimeSource.Returning(Reading(TimeApi, T1, 300)), logger)
            .GetNowAsync(CancellationToken.None);

        var entry = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Same(boom, entry.Exception);
        Assert.Contains(TimeNow, entry.Message);
    }

    [Fact]
    public async Task Source_throwing_after_the_caller_cancelled_is_a_cancellation_not_a_fallback_QA06_E17()
    {
        // The secondary reacts to the caller's cancellation with the wrong exception type. The caller is gone, so
        // this must not turn into "primary single source" (or a 503): it is a cancellation.
        using var caller = new CancellationTokenSource();
        var primary = FakeTimeSource.Returning(Reading(TimeNow, T1, 300));
        var secondary = new FakeTimeSource(TimeApi, async (_, ct) =>
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { }
            throw new HttpRequestException("connection aborted");
        });

        var call = Clock(primary, secondary).GetNowAsync(caller.Token);
        await secondary.Called.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ---------- Helpers ----------

    private TimeSourceReading Reading(string source, Instant instant, int roundTripMs) =>
        FakeTimeSource.Reading(source, instant, roundTripMs, startedTimestamp: TimeSpan.FromMilliseconds(5000).Ticks);

    private static FakeTimeSource Throwing(string name, Exception ex) =>
        new(name, async (_, _) =>
        {
            await Task.Yield();
            throw ex;
        });

    private TrustedClock Clock(TimeSourceReading primary, TimeSourceReading secondary) =>
        Clock(FakeTimeSource.Returning(primary), FakeTimeSource.Returning(secondary));

    private TrustedClock Clock(ITimeSource primary, ITimeSource secondary, ILogger<TrustedClock>? logger = null)
    {
        var options = Options.Create(new TrustedClockOptions
        {
            TimeoutMs = 3000,
            CrossCheckToleranceMs = 2000,
            LowPrecisionThresholdMs = 1500,
            Primary = new TimeProviderEndpoint { Name = primary.Name, BaseUrl = "https://primary.test/" },
            Secondary = new TimeProviderEndpoint { Name = secondary.Name, BaseUrl = "https://secondary.test/" },
        });
        return new TrustedClock(primary, secondary, options, _timer, logger ?? new CapturingLogger());
    }

    private sealed class CapturingLogger : ILogger<TrustedClock>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries) Entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
