using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using Microsoft.Extensions.Options;
using NodaTime;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// The real <see cref="ITrustedClock"/> (plans/01-trusted-clock.md §4.2). Asks both providers in parallel on every
/// call (D-008), cross-checks them, and accepts the primary, falls back to the one that answered (D-009), or fails
/// closed with a <see cref="TrustedTimeException"/> (503). It never caches "now" and never reads a local clock:
/// the only timer it uses is the monotonic <see cref="IElapsedTimer"/>, for comparing when the requests were sent.
/// </summary>
internal sealed partial class TrustedClock : ITrustedClock
{
    private readonly ITimeSource _primary;
    private readonly ITimeSource _secondary;
    private readonly TrustedClockOptions _options;
    private readonly IElapsedTimer _timer;
    private readonly ILogger<TrustedClock> _logger;

    public TrustedClock(
        ITimeSource primary,
        ITimeSource secondary,
        IOptions<TrustedClockOptions> options,
        IElapsedTimer timer,
        ILogger<TrustedClock> logger)
    {
        _primary = primary;
        _secondary = secondary;
        _options = options.Value;
        _timer = timer;
        _logger = logger;
    }

    public async Task<TrustedInstant> GetNowAsync(CancellationToken ct)
    {
        var run = await RunAsync(ct);
        return run.Accepted ?? throw run.Failure!;
    }

    /// <summary>
    /// One full attempt: both provider results, the cross-check and the decision. Provider problems never throw here;
    /// they end up in <see cref="TrustedClockRun.Failure"/>. Only the caller's cancellation throws
    /// (<see cref="OperationCanceledException"/>), even if one provider had already answered: the caller is gone.
    /// Public to the assembly so diagnostics (health check, dev time lab) show exactly what a punch would get.
    /// </summary>
    internal async Task<TrustedClockRun> RunAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // Start both before awaiting either: the cost is the slower call, not the sum.
        var primaryTask = FetchAsync(_primary, ct);
        var secondaryTask = FetchAsync(_secondary, ct);

        // WhenAll waits for (and observes) both, so a cancelled or faulted task never goes unobserved.
        await Task.WhenAll(primaryTask, secondaryTask);
        var primary = await primaryTask;
        var secondary = await secondaryTask;

        var run = Decide(primary, secondary);
        Log(run);
        return run;
    }

    private TrustedClockRun Decide(TimeSourceResult primary, TimeSourceResult secondary)
    {
        switch (primary, secondary)
        {
            case (TimeSourceReading p, TimeSourceReading s):
                var check = CrossCheck(p, s);
                return check.Agrees
                    ? Accept(primary, secondary, p, crossCheckedBy: s.Source, check)
                    : Fail(primary, secondary, ErrorCodes.TimeProvidersDisagree, check);

            case (TimeSourceReading p, _):
                return Accept(primary, secondary, p, crossCheckedBy: null, check: null);

            case (_, TimeSourceReading s):
                return Accept(primary, secondary, s, crossCheckedBy: null, check: null);

            default:
                // A provider that answered with a body we can't trust is worth a different code than plain outages.
                var anyInvalid = primary is TimeSourceFailure { Reason: TimeSourceFailureReason.InvalidResponse }
                    || secondary is TimeSourceFailure { Reason: TimeSourceFailureReason.InvalidResponse };
                return Fail(primary, secondary, anyInvalid ? ErrorCodes.TimeInvalidResponse : ErrorCodes.TimeUnavailable, check: null);
        }
    }

    /// <summary>
    /// Compares the two readings at the midpoint of each round trip (D-008):
    /// <c>delta = (B.Instant − A.Instant) − ((B.Started + B.Rtt/2) − (A.Started + A.Rtt/2))</c>.
    /// </summary>
    private CrossCheckResult CrossCheck(TimeSourceReading a, TimeSourceReading b)
    {
        // How much later B's request left than A's. Timestamps are opaque units (Stopwatch ticks are 100 ns on
        // Windows but 1 ns on Linux), so they are only ever converted through the timer (QA-01). The two reads of
        // "elapsed since" are microseconds apart, far below the millisecond resolution we report.
        var bStartedAfterA = _timer.GetElapsedTime(a.StartedTimestamp) - _timer.GetElapsedTime(b.StartedTimestamp);
        var expectedGap = Duration.FromTimeSpan(bStartedAfterA) + Duration.FromMilliseconds((b.RoundTripMs - a.RoundTripMs) / 2.0);
        var delta = (b.Instant - a.Instant) - expectedGap;

        // The same rounded value is compared and reported (QA-05), so the log can never show "2000 ms, disagree".
        var deltaMs = (long)Math.Round(Math.Abs(delta.TotalMilliseconds), MidpointRounding.AwayFromZero);

        // Two correct clocks can look apart by up to (RttA + RttB) / 2, because we only know each reading was taken
        // somewhere inside its own round trip. The bound itself needs zero network delay in one direction, so a
        // delta that reaches it can't be timing alone: strictly below. The configured tolerance is inclusive (QA-04).
        var roundTripBoundMs = (a.RoundTripMs + (double)b.RoundTripMs) / 2;
        var agrees = deltaMs <= _options.CrossCheckToleranceMs || deltaMs < roundTripBoundMs;

        return new CrossCheckResult((int)Math.Min(int.MaxValue, deltaMs), _options.CrossCheckToleranceMs, roundTripBoundMs, agrees);
    }

    private TrustedClockRun Accept(
        TimeSourceResult primary, TimeSourceResult secondary, TimeSourceReading used, string? crossCheckedBy, CrossCheckResult? check)
    {
        var instant = new TrustedInstant(
            used.Instant,
            used.ZurichOffset,
            used.Source,
            used.RoundTripMs,
            used.RawPayload,
            crossCheckedBy,
            check?.DeltaMs,
            LowPrecision: used.RoundTripMs > _options.LowPrecisionThresholdMs);
        return new TrustedClockRun(primary, secondary, check, instant, Failure: null);
    }

    private static TrustedClockRun Fail(TimeSourceResult primary, TimeSourceResult secondary, string code, CrossCheckResult? check)
    {
        // Short reason codes only, in role order; details and bodies stay in the log.
        var outcomes = new[] { new ProviderOutcome(primary.Source, primary.OutcomeCode), new ProviderOutcome(secondary.Source, secondary.OutcomeCode) };
        return new TrustedClockRun(primary, secondary, check, Accepted: null, new TrustedTimeException(code, outcomes));
    }

    /// <summary>
    /// Calls one source and turns anything it throws into a failure of that source (QA-06), so a bug in one parser
    /// can't stop the other provider from serving. Only the caller's cancellation propagates.
    /// </summary>
    private async Task<TimeSourceResult> FetchAsync(ITimeSource source, CancellationToken ct)
    {
        var started = _timer.GetTimestamp();
        try
        {
            return await source.FetchAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Whatever it threw, if the caller is gone this is a cancellation, not a provider failure.
            ct.ThrowIfCancellationRequested();

            LogUnexpected(ex, source.Name);
            var roundTripMs = (int)Math.Min(int.MaxValue, Math.Round(_timer.GetElapsedTime(started).TotalMilliseconds, MidpointRounding.AwayFromZero));
            return new TimeSourceFailure(source.Name, TimeSourceFailureReason.Unexpected, roundTripMs, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------- Logging: one structured line per call ----------

    private void Log(TrustedClockRun run)
    {
        var p = run.Primary;
        var s = run.Secondary;
        if (run.Accepted is { } a)
        {
            if (a.CrossCheckedBy is not null)
            {
                LogCrossChecked(a.Provider, a.CrossCheckedBy, p.RoundTripMs, s.RoundTripMs, a.CrossCheckDeltaMs!.Value, a.LowPrecision);
            }
            else
            {
                var failed = p is TimeSourceReading ? s : p;
                LogSingleSource(a.Provider, a.RoundTripMs, failed.Source, failed.OutcomeCode, DetailOf(failed), a.LowPrecision);
            }
        }
        else
        {
            LogFailedClosed(run.Failure!.Code, p.Source, p.OutcomeCode, p.RoundTripMs, DetailOf(p),
                s.Source, s.OutcomeCode, s.RoundTripMs, DetailOf(s), run.CrossCheck?.DeltaMs, run.CrossCheck?.RoundTripBoundMs);
        }
    }

    private static string DetailOf(TimeSourceResult result) => result is TimeSourceFailure f ? f.Detail : "ok";

    [LoggerMessage(Level = LogLevel.Information,
        Message = "Trusted time from {Provider}, cross-checked by {CrossCheckedBy}: rtt {PrimaryRttMs}/{SecondaryRttMs} ms, delta {DeltaMs} ms, low precision {LowPrecision}")]
    private partial void LogCrossChecked(string provider, string crossCheckedBy, int primaryRttMs, int secondaryRttMs, int deltaMs, bool lowPrecision);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Trusted time from {Provider} only (rtt {RttMs} ms, low precision {LowPrecision}); {FailedProvider} failed: {FailedOutcome} ({FailedDetail})")]
    private partial void LogSingleSource(string provider, int rttMs, string failedProvider, string failedOutcome, string failedDetail, bool lowPrecision);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "No trusted time ({Code}): {PrimaryProvider} {PrimaryOutcome} after {PrimaryRttMs} ms ({PrimaryDetail}); " +
                  "{SecondaryProvider} {SecondaryOutcome} after {SecondaryRttMs} ms ({SecondaryDetail}); delta {DeltaMs} ms, rtt bound {RoundTripBoundMs} ms")]
    private partial void LogFailedClosed(string code, string primaryProvider, string primaryOutcome, int primaryRttMs, string primaryDetail,
        string secondaryProvider, string secondaryOutcome, int secondaryRttMs, string secondaryDetail, int? deltaMs, double? roundTripBoundMs);

    [LoggerMessage(Level = LogLevel.Error, Message = "Time source {Provider} threw instead of returning a result; counted as a failure of that source")]
    private partial void LogUnexpected(Exception exception, string provider);
}

/// <summary>How far apart two readings were after midpoint correction, and whether that counts as agreeing.</summary>
/// <param name="DeltaMs">|corrected delta|, rounded to whole ms (the value compared and reported).</param>
/// <param name="ToleranceMs">Configured <c>CrossCheckToleranceMs</c> (inclusive).</param>
/// <param name="RoundTripBoundMs">(RttA + RttB) / 2: what timing alone can explain (exclusive).</param>
internal sealed record CrossCheckResult(int DeltaMs, int ToleranceMs, double RoundTripBoundMs, bool Agrees);

/// <summary>
/// Everything one <see cref="TrustedClock.RunAsync"/> saw and decided. Exactly one of <see cref="Accepted"/> and
/// <see cref="Failure"/> is set. <see cref="CrossCheck"/> is set only when both providers answered.
/// </summary>
internal sealed record TrustedClockRun(
    TimeSourceResult Primary,
    TimeSourceResult Secondary,
    CrossCheckResult? CrossCheck,
    TrustedInstant? Accepted,
    TrustedTimeException? Failure);
