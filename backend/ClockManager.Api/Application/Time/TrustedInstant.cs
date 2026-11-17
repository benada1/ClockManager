using NodaTime;

namespace ClockManager.Api.Application.Time;

/// <summary>
/// A moment obtained from an external time provider, with everything needed to justify it later
/// (plans/01-trusted-clock.md §4.1). Zurich local time and date are derived from <see cref="Utc"/> with
/// tzdb (<see cref="ZurichTime"/>), never taken from the provider's text.
/// </summary>
/// <param name="Utc">The exact moment, no time zone attached.</param>
/// <param name="ZurichOffset">+01:00 or +02:00 at that instant (from tzdb, checked against the provider).</param>
/// <param name="Provider">Name of the provider whose value was used, e.g. "time.now".</param>
/// <param name="RoundTripMs">Request duration: how uncertain the value is.</param>
/// <param name="RawPayload">The provider's body, sanitized (client_ip removed), at most 1 KB.</param>
/// <param name="CrossCheckedBy">The other provider if it answered and agreed, else null.</param>
/// <param name="CrossCheckDeltaMs">How far apart the two providers were, if both answered.</param>
/// <param name="LowPrecision">True when <paramref name="RoundTripMs"/> is above the configured threshold.</param>
public sealed record TrustedInstant(
    Instant Utc,
    Offset ZurichOffset,
    string Provider,
    int RoundTripMs,
    string RawPayload,
    string? CrossCheckedBy,
    int? CrossCheckDeltaMs,
    bool LowPrecision);
