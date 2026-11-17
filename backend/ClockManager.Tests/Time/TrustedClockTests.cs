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
/// The orchestrator behind <see cref="ITrustedClock"/> (plans/01-trusted-clock.md §4.2, edge cases E1–E6, E15, E17).
/// Both sources are <see cref="FakeTimeSource"/>s and round trips come from a fake monotonic timer, so every number
/// is exact and no test sleeps or touches the network.
///
/// Midpoint correction used throughout (contract for step 2):
/// <c>delta = (B.Instant − A.Instant) − ((B.Started + B.Rtt/2) − (A.Started + A.Rtt/2))</c>.
/// Tests use even RTTs so integer vs. fractional halving makes no difference.
/// </summary>
public sealed class TrustedClockTests
{
    private const string TimeNow = "time.now";
    private const string TimeApi = "timeapi.io";

    private static readonly Instant T1 = Utc("2026-01-12T07:00:00Z"); // matrix T1 Clock In
    private static readonly Instant T2 = Utc("2026-07-06T06:00:00Z"); // matrix T2 Clock In

    private readonly FakeElapsedTimer _timer = new();

    public TrustedClockTests()
    {
        // "Now" on the monotonic timer is 10 s; the readings below started a few seconds earlier.
        _timer.Advance(TimeSpan.FromSeconds(10));
    }

    // ---------- E1: both answer and agree → primary, cross-checked ----------

    [Fact]
    public async Task Accepts_primary_when_both_agree_E1()
    {
        var primaryReading = FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 340, startedTimestamp: At(5000), rawPayload: "{\"p\":1}");
        var secondaryReading = FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(37), roundTripMs: 340, startedTimestamp: At(5000));

        var result = await Clock(FakeTimeSource.Returning(primaryReading), FakeTimeSource.Returning(secondaryReading))
            .GetNowAsync(CancellationToken.None);

        Assert.Equal(T1, result.Utc);
        Assert.Equal(Offset.FromHours(1), result.ZurichOffset);
        Assert.Equal(TimeNow, result.Provider);
        Assert.Equal(340, result.RoundTripMs);
        Assert.Equal("{\"p\":1}", result.RawPayload);
        Assert.Equal(TimeApi, result.CrossCheckedBy);
        Assert.Equal(37, result.CrossCheckDeltaMs);
        Assert.False(result.LowPrecision);
    }

    [Fact]
    public async Task Uses_primary_value_even_when_secondary_is_the_more_recent_one_E1()
    {
        // Both agree (1.5 s apart), but the answer is the primary's value, not an average and not the later one.
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T2, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T2 + Duration.FromMilliseconds(1500), startedTimestamp: At(5000)));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(T2, result.Utc);
        Assert.Equal(Offset.FromHours(2), result.ZurichOffset);
        Assert.Equal(1500, result.CrossCheckDeltaMs);
    }

    [Fact]
    public async Task Agreement_across_the_DST_start_instant_keeps_the_primary_offset_T7()
    {
        // Primary is 0.5 s before 2026-03-29T01:00Z (+01:00), secondary 0.5 s after (+02:00). They agree;
        // the result is the primary's instant with the primary's offset, never a mix of the two.
        var before = Utc("2026-03-29T00:59:59.5Z");
        var after = Utc("2026-03-29T01:00:00.5Z");
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, before, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, after, startedTimestamp: At(5000)));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(before, result.Utc);
        Assert.Equal(Offset.FromHours(1), result.ZurichOffset);
        Assert.Equal(1000, result.CrossCheckDeltaMs);
    }

    [Fact]
    public async Task Delta_is_reported_as_absolute_value_in_whole_milliseconds()
    {
        // Secondary is 37.6 ms BEHIND the primary: reported as +38, never -38 or 37.6.
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 - Duration.FromNanoseconds(37_600_000), startedTimestamp: At(5000)));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(38, result.CrossCheckDeltaMs);
    }

    // ---------- E2 + tolerance boundary ----------

    [Fact]
    public async Task Fails_closed_when_providers_disagree_E2()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromSeconds(5), startedTimestamp: At(5000)));

        var ex = await Assert.ThrowsAsync<TrustedTimeException>(() => Clock(primary, secondary).GetNowAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.TimeProvidersDisagree, ex.Code);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal([new ProviderOutcome(TimeNow, "ok"), new ProviderOutcome(TimeApi, "ok")], ex.Providers);
    }

    [Theory]
    [InlineData(2000, true)]   // inclusive: exactly at the tolerance still agrees
    [InlineData(-2000, true)]  // secondary behind by the same amount
    [InlineData(2001, false)]
    [InlineData(-2001, false)]
    public async Task Tolerance_is_inclusive_at_2000_ms(int secondaryAheadMs, bool agrees)
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(
            FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(secondaryAheadMs), startedTimestamp: At(5000)));
        var clock = Clock(primary, secondary);

        if (agrees)
        {
            var result = await clock.GetNowAsync(CancellationToken.None);
            Assert.Equal(T1, result.Utc);
            Assert.Equal(TimeApi, result.CrossCheckedBy);
            Assert.Equal(Math.Abs(secondaryAheadMs), result.CrossCheckDeltaMs);
        }
        else
        {
            var ex = await Assert.ThrowsAsync<TrustedTimeException>(() => clock.GetNowAsync(CancellationToken.None));
            Assert.Equal(ErrorCodes.TimeProvidersDisagree, ex.Code);
        }
    }

    [Fact]
    public async Task Tolerance_comes_from_options()
    {
        // 300 ms apart: fine with the default 2000, a disagreement with a configured 100.
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(300), startedTimestamp: At(5000)));

        var ex = await Assert.ThrowsAsync<TrustedTimeException>(
            () => Clock(primary, secondary, toleranceMs: 100).GetNowAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.TimeProvidersDisagree, ex.Code);
    }

    // ---------- Midpoint correction (§4.2 "Comparing two clocks fairly") ----------

    [Fact]
    public async Task Corrects_for_a_later_start_so_it_is_not_a_false_disagreement()
    {
        // B's request left 1000 ms after A's and B reports 1000 ms later: both clocks are right.
        // Raw delta 1000 ms > tolerance 500 would be a false alarm; the corrected delta is 0.
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 400, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(
            FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(1000), roundTripMs: 400, startedTimestamp: At(6000)));

        var result = await Clock(primary, secondary, toleranceMs: 500).GetNowAsync(CancellationToken.None);

        Assert.Equal(T1, result.Utc);
        Assert.Equal(TimeApi, result.CrossCheckedBy);
        Assert.Equal(0, result.CrossCheckDeltaMs);
    }

    [Fact]
    public async Task Correction_also_exposes_a_disagreement_hidden_by_timing()
    {
        // Same timing, but B reports the SAME instant as A although it was asked 1000 ms later: B is 1 s slow.
        // Raw instants are equal; only the corrected comparison sees the 1000 ms gap (> 500 tolerance).
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 400, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1, roundTripMs: 400, startedTimestamp: At(6000)));

        var ex = await Assert.ThrowsAsync<TrustedTimeException>(
            () => Clock(primary, secondary, toleranceMs: 500).GetNowAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.TimeProvidersDisagree, ex.Code);
    }

    [Fact]
    public async Task Corrects_for_different_round_trip_times()
    {
        // Same start. A: RTT 200 → midpoint +100 ms. B: RTT 2800 → midpoint +1400 ms. B's value is 1300 ms later,
        // exactly what a correct clock answers at its midpoint. Corrected delta 0 (raw 1300 > tolerance 500).
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 200, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(
            FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(1300), roundTripMs: 2800, startedTimestamp: At(5000)));

        var result = await Clock(primary, secondary, toleranceMs: 500).GetNowAsync(CancellationToken.None);

        Assert.Equal(0, result.CrossCheckDeltaMs);
    }

    [Fact]
    public async Task Converts_timestamps_through_the_elapsed_timer_not_as_TimeSpan_ticks()
    {
        // Same scenario as Corrects_for_a_later_start..., but timestamps count nanoseconds as Stopwatch does on Linux.
        // An implementation that does TimeSpan.FromTicks(B.Started - A.Started) reads the 1000 ms gap as 100 s and
        // fails closed on every punch in a Linux container, while passing on a Windows machine (Frequency = 1e7).
        var timer = new NanosecondElapsedTimer();
        timer.Advance(TimeSpan.FromSeconds(10));
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 400,
            startedTimestamp: NanosecondElapsedTimer.TimestampAt(TimeSpan.FromMilliseconds(5000))));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(1000), roundTripMs: 400,
            startedTimestamp: NanosecondElapsedTimer.TimestampAt(TimeSpan.FromMilliseconds(6000))));

        var result = await Clock(primary, secondary, toleranceMs: 500, timer: timer).GetNowAsync(CancellationToken.None);

        Assert.Equal(0, result.CrossCheckDeltaMs);
    }

    // ---------- E3 / E4: primary failed → secondary, single source ----------

    [Theory]
    [InlineData(nameof(TimeSourceFailureReason.Timeout), 0)]
    [InlineData(nameof(TimeSourceFailureReason.HttpError), 500)]
    [InlineData(nameof(TimeSourceFailureReason.HttpError), 503)]
    [InlineData(nameof(TimeSourceFailureReason.RateLimited), 429)]
    [InlineData(nameof(TimeSourceFailureReason.NetworkError), 0)]
    [InlineData(nameof(TimeSourceFailureReason.InvalidResponse), 0)]
    public async Task Falls_back_to_secondary_when_primary_fails_E3_E4(string reasonName, int status)
    {
        var reason = Enum.Parse<TimeSourceFailureReason>(reasonName);
        var failure = status == 0 ? FakeTimeSource.Failure(TimeNow, reason) : FakeTimeSource.HttpFailure(TimeNow, status);
        Assert.Equal(reason, failure.Reason);
        var primary = FakeTimeSource.Returning(failure);
        var secondaryReading = FakeTimeSource.Reading(TimeApi, T1, roundTripMs: 420, startedTimestamp: At(5000), rawPayload: "{\"s\":1}");
        var secondary = FakeTimeSource.Returning(secondaryReading);

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(T1, result.Utc);
        Assert.Equal(Offset.FromHours(1), result.ZurichOffset);
        Assert.Equal(TimeApi, result.Provider);
        Assert.Equal(420, result.RoundTripMs);
        Assert.Equal("{\"s\":1}", result.RawPayload);
        Assert.Null(result.CrossCheckedBy);
        Assert.Null(result.CrossCheckDeltaMs);
        Assert.False(result.LowPrecision);
        Assert.Equal(1, primary.Calls);   // no retries on the punch path: the other provider is the retry
        Assert.Equal(1, secondary.Calls);
    }

    [Theory]
    [InlineData(nameof(TimeSourceFailureReason.Timeout))]
    [InlineData(nameof(TimeSourceFailureReason.HttpError))]
    [InlineData(nameof(TimeSourceFailureReason.RateLimited))]
    [InlineData(nameof(TimeSourceFailureReason.NetworkError))]
    [InlineData(nameof(TimeSourceFailureReason.InvalidResponse))]
    public async Task Accepts_primary_single_source_when_secondary_fails(string reasonName)
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T2, roundTripMs: 350, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Failure(TimeApi, Enum.Parse<TimeSourceFailureReason>(reasonName)));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(T2, result.Utc);
        Assert.Equal(Offset.FromHours(2), result.ZurichOffset);
        Assert.Equal(TimeNow, result.Provider);
        Assert.Equal(350, result.RoundTripMs);
        Assert.Null(result.CrossCheckedBy);
        Assert.Null(result.CrossCheckDeltaMs);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, secondary.Calls);
    }

    // ---------- E5 / E6: both failed → fail closed ----------

    [Theory]
    [InlineData(nameof(TimeSourceFailureReason.Timeout), nameof(TimeSourceFailureReason.Timeout), ErrorCodes.TimeUnavailable)]
    [InlineData(nameof(TimeSourceFailureReason.HttpError), nameof(TimeSourceFailureReason.NetworkError), ErrorCodes.TimeUnavailable)]
    [InlineData(nameof(TimeSourceFailureReason.RateLimited), nameof(TimeSourceFailureReason.Timeout), ErrorCodes.TimeUnavailable)]
    [InlineData(nameof(TimeSourceFailureReason.InvalidResponse), nameof(TimeSourceFailureReason.InvalidResponse), ErrorCodes.TimeInvalidResponse)]
    [InlineData(nameof(TimeSourceFailureReason.InvalidResponse), nameof(TimeSourceFailureReason.Timeout), ErrorCodes.TimeInvalidResponse)]
    [InlineData(nameof(TimeSourceFailureReason.Timeout), nameof(TimeSourceFailureReason.InvalidResponse), ErrorCodes.TimeInvalidResponse)]
    public async Task Fails_closed_when_both_fail_E5_E6(
        string primaryReasonName, string secondaryReasonName, string expectedCode)
    {
        var primaryFailure = FakeTimeSource.Failure(TimeNow, Enum.Parse<TimeSourceFailureReason>(primaryReasonName));
        var secondaryFailure = FakeTimeSource.Failure(TimeApi, Enum.Parse<TimeSourceFailureReason>(secondaryReasonName));

        var ex = await Assert.ThrowsAsync<TrustedTimeException>(() =>
            Clock(FakeTimeSource.Returning(primaryFailure), FakeTimeSource.Returning(secondaryFailure)).GetNowAsync(CancellationToken.None));

        Assert.Equal(expectedCode, ex.Code);
        Assert.Equal(503, ex.StatusCode);
        Assert.Equal(
            [new ProviderOutcome(TimeNow, primaryFailure.OutcomeCode), new ProviderOutcome(TimeApi, secondaryFailure.OutcomeCode)],
            ex.Providers);
    }

    [Fact]
    public async Task Problem_details_providers_extension_lists_short_codes_in_role_order_E5()
    {
        var ex = await Assert.ThrowsAsync<TrustedTimeException>(() => Clock(
                FakeTimeSource.Returning(FakeTimeSource.Failure(TimeNow, TimeSourceFailureReason.Timeout)),
                FakeTimeSource.Returning(FakeTimeSource.HttpFailure(TimeApi, 429)))
            .GetNowAsync(CancellationToken.None));

        Assert.Equal(ErrorCodes.TimeUnavailable, ex.Code);
        var providers = Assert.IsAssignableFrom<IReadOnlyList<ProviderOutcome>>(ex.Extensions[TrustedTimeException.ProvidersKey]);
        Assert.Equal([new ProviderOutcome(TimeNow, "timeout"), new ProviderOutcome(TimeApi, "rate_limited")], providers);
        // Only reason codes go to the client: no detail text, no raw bodies.
        Assert.DoesNotContain(providers, p => p.Outcome.Contains("fake", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Providers_list_follows_roles_not_names_when_roles_are_swapped()
    {
        // timeapi.io configured as primary: it is listed first.
        var ex = await Assert.ThrowsAsync<TrustedTimeException>(() => Clock(
                FakeTimeSource.Returning(FakeTimeSource.Failure(TimeApi, TimeSourceFailureReason.NetworkError)),
                FakeTimeSource.Returning(FakeTimeSource.Failure(TimeNow, TimeSourceFailureReason.Timeout)))
            .GetNowAsync(CancellationToken.None));

        Assert.Equal([new ProviderOutcome(TimeApi, "network_error"), new ProviderOutcome(TimeNow, "timeout")], ex.Providers);
    }

    // ---------- E15: low precision ----------

    [Theory]
    [InlineData(1500, false)] // strictly greater than the threshold
    [InlineData(1501, true)]
    [InlineData(1800, true)]  // plan E15
    public async Task Flags_slow_primary_response_as_low_precision_E15(int roundTripMs, bool expected)
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: roundTripMs, startedTimestamp: At(5000)));
        // Same midpoint as the primary, so the delta is 0 whatever the primary's RTT.
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1, roundTripMs: roundTripMs, startedTimestamp: At(5000)));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(roundTripMs, result.RoundTripMs);
        Assert.Equal(expected, result.LowPrecision);
    }

    [Fact]
    public async Task Low_precision_uses_the_accepted_secondary_round_trip_E15()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Failure(TimeNow, TimeSourceFailureReason.Timeout, roundTripMs: 3000));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1, roundTripMs: 1800, startedTimestamp: At(5000)));

        var result = await Clock(primary, secondary).GetNowAsync(CancellationToken.None);

        Assert.Equal(TimeApi, result.Provider);
        Assert.Equal(1800, result.RoundTripMs);
        Assert.True(result.LowPrecision);
    }

    [Fact]
    public async Task Low_precision_threshold_comes_from_options()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 600, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Failure(TimeApi, TimeSourceFailureReason.Timeout));

        var result = await Clock(primary, secondary, lowPrecisionMs: 500).GetNowAsync(CancellationToken.None);

        Assert.True(result.LowPrecision);
    }

    // ---------- Parallel calls ----------

    [Fact]
    public async Task Starts_both_providers_before_either_answers()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = FakeTimeSource.Gated(FakeTimeSource.Reading(TimeNow, T1, startedTimestamp: At(5000)), gate.Task);
        var secondary = FakeTimeSource.Gated(FakeTimeSource.Reading(TimeApi, T1, startedTimestamp: At(5000)), gate.Task);

        var call = Clock(primary, secondary).GetNowAsync(CancellationToken.None);
        try
        {
            // Neither source can finish until the gate opens. A sequential implementation never calls the second one.
            await Task.WhenAll(primary.Called, secondary.Called).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(call.IsCompleted);
        }
        finally
        {
            gate.TrySetResult();
        }

        var result = await call.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(TimeApi, result.CrossCheckedBy);
    }

    [Fact]
    public async Task Asks_the_providers_again_on_every_call_no_cached_now()
    {
        var later = T1 + Duration.FromSeconds(30);
        var primary = FakeTimeSource.Sequence(
            FakeTimeSource.Reading(TimeNow, T1, startedTimestamp: At(5000)),
            FakeTimeSource.Reading(TimeNow, later, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.Sequence(
            FakeTimeSource.Reading(TimeApi, T1, startedTimestamp: At(5000)),
            FakeTimeSource.Reading(TimeApi, later, startedTimestamp: At(5000)));
        var clock = Clock(primary, secondary);

        var first = await clock.GetNowAsync(CancellationToken.None);
        var second = await clock.GetNowAsync(CancellationToken.None);

        Assert.Equal(T1, first.Utc);
        Assert.Equal(later, second.Utc);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(2, secondary.Calls);
    }

    // ---------- E17: caller cancellation is not a provider failure ----------

    [Fact]
    public async Task Propagates_caller_cancellation_while_both_are_pending_E17()
    {
        using var caller = new CancellationTokenSource();
        var primary = FakeTimeSource.UntilCancelled(TimeNow);
        var secondary = FakeTimeSource.UntilCancelled(TimeApi);

        var call = Clock(primary, secondary).GetNowAsync(caller.Token);
        await Task.WhenAll(primary.Called, secondary.Called).WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        // Not TrustedTimeException (no 503 / no "unavailable" for a client that simply went away).
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        // The cancellation reached both in-flight requests (the caller's token or one linked to it), so no provider
        // call keeps running after the client has gone.
        Assert.True(primary.ReceivedToken.IsCancellationRequested);
        Assert.True(secondary.ReceivedToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Propagates_caller_cancellation_even_if_primary_already_answered_E17()
    {
        // The primary answered; the caller then gave up while the secondary was pending. The caller is gone, so
        // returning a single-source result (or a 503) would be wrong: it is a cancellation.
        using var caller = new CancellationTokenSource();
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, startedTimestamp: At(5000)));
        var secondary = FakeTimeSource.UntilCancelled(TimeApi);

        var call = Clock(primary, secondary).GetNowAsync(caller.Token);
        await Task.WhenAll(primary.Called, secondary.Called).WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Propagates_caller_cancellation_even_if_secondary_already_failed_E17()
    {
        using var caller = new CancellationTokenSource();
        var primary = FakeTimeSource.UntilCancelled(TimeNow);
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Failure(TimeApi, TimeSourceFailureReason.Timeout));

        var call = Clock(primary, secondary).GetNowAsync(caller.Token);
        await Task.WhenAll(primary.Called, secondary.Called).WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel();

        var ex = await Record.ExceptionAsync(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.IsAssignableFrom<OperationCanceledException>(ex);
    }

    // ---------- Helpers ----------

    /// <summary>A <see cref="FakeElapsedTimer"/> timestamp <paramref name="ms"/> after timer zero.</summary>
    private static long At(int ms) => TimeSpan.FromMilliseconds(ms).Ticks;

    private TrustedClock Clock(
        ITimeSource primary,
        ITimeSource secondary,
        int toleranceMs = 2000,
        int lowPrecisionMs = 1500,
        IElapsedTimer? timer = null)
    {
        var options = Options.Create(new TrustedClockOptions
        {
            TimeoutMs = 3000,
            CrossCheckToleranceMs = toleranceMs,
            LowPrecisionThresholdMs = lowPrecisionMs,
            Primary = new TimeProviderEndpoint { Name = primary.Name, BaseUrl = "https://primary.test/" },
            Secondary = new TimeProviderEndpoint { Name = secondary.Name, BaseUrl = "https://secondary.test/" },
        });
        return new TrustedClock(primary, secondary, options, timer ?? _timer, NullLogger<TrustedClock>.Instance);
    }

    private static Instant Utc(string iso) => InstantPattern.ExtendedIso.Parse(iso).GetValueOrThrow();
}
