namespace ClockManager.Api.Application.Time;

/// <summary>
/// Development-only diagnostic (plans/01-trusted-clock.md §6 step 3b, D-013): asks the real providers once, through
/// the same orchestrator a punch uses, and returns everything it saw and decided. Never cached.
/// Registered only in the Development environment.
/// </summary>
public interface ITimeLab
{
    Task<TimeLabResponse> RunAsync(CancellationToken ct);
}

/// <summary>Body of <c>GET /api/dev/time-lab</c>. The JSON shape is a contract with the frontend Time lab page.</summary>
/// <param name="Providers">One entry per provider, in role order.</param>
/// <param name="CrossCheck">Set only when both providers answered with a valid reading.</param>
public sealed record TimeLabResponse(
    IReadOnlyList<TimeLabProvider> Providers,
    TimeLabCrossCheck? CrossCheck,
    TimeLabDecision Decision);

/// <summary>
/// What one provider returned. Time values are computed from the UTC instant with tzdb (<see cref="ZurichTime"/>),
/// never copied from the provider's text. All four time/payload fields are null when the provider failed.
/// </summary>
/// <param name="Role"><c>primary</c> or <c>secondary</c>.</param>
/// <param name="Outcome">Outcome code (ok, timeout, http_error, rate_limited, invalid_response, network_error, error).</param>
/// <param name="HttpStatus">The HTTP status for http_error / rate_limited, else null.</param>
/// <param name="Detail">Why the provider failed (log text). Dev-only: <c>/health/time</c> never shows it.</param>
/// <param name="Utc">ISO 8601 instant, e.g. <c>2026-11-17T12:02:58.446011Z</c>.</param>
/// <param name="ZurichOffset">e.g. <c>+01:00</c>.</param>
/// <param name="ZurichLocal">Zurich date-time without offset; pair it with <paramref name="ZurichOffset"/>.</param>
/// <param name="RawPayload">The sanitized body (client_ip removed, max 1 KB), as stored for audit.</param>
public sealed record TimeLabProvider(
    string Role,
    string Name,
    string Outcome,
    int RoundTripMs,
    int? HttpStatus,
    string? Detail,
    string? Utc,
    string? ZurichOffset,
    string? ZurichLocal,
    string? RawPayload);

/// <summary>The midpoint cross-check (D-008): agrees if <c>DeltaMs ≤ ToleranceMs</c> or <c>DeltaMs &lt; RoundTripBoundMs</c>.</summary>
public sealed record TimeLabCrossCheck(int DeltaMs, int ToleranceMs, double RoundTripBoundMs, bool Agrees);

/// <summary>What a punch would get right now.</summary>
/// <param name="Result"><c>accepted</c> or <c>failed</c>.</param>
/// <param name="Provider">Accepted only: the provider whose value was used.</param>
/// <param name="CrossCheckedBy">Accepted only: the agreeing provider; null for a single-source answer (D-009).</param>
/// <param name="LowPrecision">Accepted only.</param>
/// <param name="Code">Failed only: <c>time.unavailable</c>, <c>time.invalid_response</c> or <c>time.providers_disagree</c>.</param>
/// <param name="HttpStatus">Failed only: the status a punch would get (503).</param>
public sealed record TimeLabDecision(
    string Result,
    string? Provider,
    string? CrossCheckedBy,
    bool? LowPrecision,
    string? Utc,
    string? ZurichLocal,
    string? ZurichOffset,
    string? Code,
    int? HttpStatus)
{
    public const string Accepted = "accepted";
    public const string Failed = "failed";
}
