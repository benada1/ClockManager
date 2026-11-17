namespace ClockManager.Api.Application.Time;

/// <summary>
/// "Would a punch succeed right now?" for <c>GET /health/time</c> (plans/01-trusted-clock.md §4.7, D-012).
/// Answers come from a short cache so the endpoint can't hammer the providers; the punch path
/// (<see cref="ITrustedClock.GetNowAsync"/>) never reads that cache.
/// </summary>
public interface ITimeHealth
{
    Task<TimeHealthReport> GetAsync(CancellationToken ct);
}

/// <summary>
/// Public status of the trusted clock. Short codes only: never a provider's raw body, error text or parsed time,
/// so it is safe to expose without authentication.
/// </summary>
/// <param name="CanPunch">True when the trusted clock accepted a time, i.e. a punch would get one.</param>
/// <param name="Providers">One entry per provider, in role order (primary, secondary).</param>
/// <param name="CrossCheck"><see cref="Agree"/>, <see cref="Disagree"/> or <see cref="NotChecked"/> (fewer than two answers).</param>
/// <param name="DeltaMs">Midpoint-corrected distance between the two providers (D-008), when both answered.</param>
/// <param name="Code">The 503 code a punch would get (<c>time.*</c>) when <paramref name="CanPunch"/> is false, else null.</param>
public sealed record TimeHealthReport(
    bool CanPunch,
    IReadOnlyList<ProviderHealth> Providers,
    string CrossCheck,
    int? DeltaMs,
    string? Code)
{
    public const string Agree = "agree";
    public const string Disagree = "disagree";
    public const string NotChecked = "not_checked";
}

/// <param name="Name">Provider name, e.g. "time.now".</param>
/// <param name="Status">Outcome code: ok, timeout, http_error, rate_limited, invalid_response, network_error, error.</param>
/// <param name="RoundTripMs">How long the request took (monotonic timer).</param>
public sealed record ProviderHealth(string Name, string Status, int RoundTripMs);
