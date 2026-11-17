using NodaTime;

namespace ClockManager.Api.Application.Time;

/// <summary>
/// "What time is it on the server", for DISPLAY and for time-based flags ONLY (plans/03-punch-api.md Q3, D-035).
/// <para>
/// This is deliberately NOT <see cref="ITrustedClock"/> and returns no <see cref="TrustedInstant"/>: its value may be
/// up to the cache TTL old, so it must never become a punch time (CLAUDE.md rule 1, master plan D1). A punch always
/// asks <see cref="ITrustedClock"/> afresh; <c>PunchService</c> does not depend on this interface, and
/// <c>PunchStamp.From</c> needs a <see cref="TrustedInstant"/>, which this type cannot produce.
/// </para>
/// </summary>
public interface IServerNowProvider
{
    /// <summary>The cached trusted time with its age, or null when no trusted time is available. Never throws for a provider problem.</summary>
    Task<ServerNowReading?> GetAsync(CancellationToken ct);
}

/// <summary>
/// A trusted instant as it was fetched, plus how long ago that was.
/// </summary>
/// <param name="Utc">The fetched instant as-is. NOT moved forward by <paramref name="Age"/>: the client extrapolates.</param>
/// <param name="ZurichOffset">The Zurich offset at <paramref name="Utc"/> (from tzdb).</param>
/// <param name="Age">Monotonic time since the instant was fetched (never a wall-clock difference).</param>
public sealed record ServerNowReading(Instant Utc, Offset ZurichOffset, TimeSpan Age);
