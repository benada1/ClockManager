using ClockManager.Api.Application.Time;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// Turns one <see cref="TrustedClockRun"/> into the Time lab contract (D-013). Every local time and offset is derived
/// from the UTC instant with tzdb, never from the provider's text, so the page shows what the backend would store.
/// </summary>
internal static class TimeLabMapper
{
    internal const string PrimaryRole = "primary";
    internal const string SecondaryRole = "secondary";

    // Invariant-culture NodaTime patterns: "2026-11-17T12:02:58.446011Z", "2026-11-17T13:02:58.446011", "+01:00".
    private static readonly InstantPattern UtcPattern = InstantPattern.ExtendedIso;
    private static readonly LocalDateTimePattern LocalPattern = LocalDateTimePattern.ExtendedIso;
    private static readonly OffsetPattern OffsetFormat = OffsetPattern.CreateWithInvariantCulture("+HH:mm");

    public static TimeLabResponse Map(TrustedClockRun run, ZurichTime zurich)
    {
        var providers = new[] { Provider(PrimaryRole, run.Primary, zurich), Provider(SecondaryRole, run.Secondary, zurich) };

        var crossCheck = run.CrossCheck is { } c
            ? new TimeLabCrossCheck(c.DeltaMs, c.ToleranceMs, c.RoundTripBoundMs, c.Agrees)
            : null;

        return new TimeLabResponse(providers, crossCheck, Decision(run, zurich));
    }

    private static TimeLabProvider Provider(string role, TimeSourceResult result, ZurichTime zurich) => result switch
    {
        TimeSourceReading r => new TimeLabProvider(
            role, r.Source, r.OutcomeCode, r.RoundTripMs,
            HttpStatus: null, Detail: null,
            UtcPattern.Format(r.Instant),
            OffsetFormat.Format(zurich.OffsetAt(r.Instant)),
            LocalPattern.Format(zurich.ToLocal(r.Instant).LocalDateTime),
            r.RawPayload),
        TimeSourceFailure f => new TimeLabProvider(
            role, f.Source, f.OutcomeCode, f.RoundTripMs,
            f.HttpStatus, f.Detail,
            Utc: null, ZurichOffset: null, ZurichLocal: null, RawPayload: null),
        _ => throw new ArgumentOutOfRangeException(nameof(result), result.GetType().Name, "Unknown time source result."),
    };

    private static TimeLabDecision Decision(TrustedClockRun run, ZurichTime zurich)
    {
        if (run.Accepted is { } a)
        {
            var local = zurich.ToLocal(a.Utc);
            return new TimeLabDecision(
                TimeLabDecision.Accepted, a.Provider, a.CrossCheckedBy, a.LowPrecision,
                UtcPattern.Format(a.Utc),
                LocalPattern.Format(local.LocalDateTime),
                OffsetFormat.Format(local.Offset),
                Code: null, HttpStatus: null);
        }

        var failure = run.Failure ?? throw new InvalidOperationException("A run has either an accepted time or a failure.");
        return new TimeLabDecision(
            TimeLabDecision.Failed, Provider: null, CrossCheckedBy: null, LowPrecision: null,
            Utc: null, ZurichLocal: null, ZurichOffset: null,
            failure.Code, failure.StatusCode);
    }
}

/// <summary>The real <see cref="ITimeLab"/>: one uncached <see cref="TrustedClock.RunAsync"/> per call.</summary>
internal sealed class TimeLabRunner(TrustedClock clock, ZurichTime zurich) : ITimeLab
{
    public async Task<TimeLabResponse> RunAsync(CancellationToken ct) => TimeLabMapper.Map(await clock.RunAsync(ct), zurich);
}
