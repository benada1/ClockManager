using NodaTime;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// One external time provider. Implementations validate the provider's response strictly and report every
/// provider problem (timeout, HTTP error, bad body, network) as a <see cref="TimeSourceFailure"/>; they do not throw
/// for those. Cancellation of the caller's token is the one exception: it propagates as
/// <see cref="OperationCanceledException"/> and is never reported as a timeout.
/// </summary>
internal interface ITimeSource
{
    string Name { get; }

    Task<TimeSourceResult> FetchAsync(CancellationToken ct);
}

/// <summary>Outcome of one provider call: either a validated <see cref="TimeSourceReading"/> or a <see cref="TimeSourceFailure"/>.</summary>
internal abstract record TimeSourceResult(string Source, int RoundTripMs)
{
    /// <summary>Short, stable reason code for logs and the ProblemDetails <c>providers</c> extension.</summary>
    public abstract string OutcomeCode { get; }
}

/// <summary>A response that passed every validation rule.</summary>
/// <param name="Instant">The provider's UTC instant.</param>
/// <param name="ZurichOffset">The offset in force at that instant (tzdb, confirmed by the provider).</param>
/// <param name="RawPayload">Sanitized body (personal data removed, max 1 KB) for the audit trail.</param>
/// <param name="StartedTimestamp">
/// <see cref="IElapsedTimer"/> timestamp taken just before the request was sent. Lets the caller compare two
/// providers at the midpoint of each round trip. Monotonic, not a time of day.
/// </param>
internal sealed record TimeSourceReading(
    string Source,
    Instant Instant,
    Offset ZurichOffset,
    int RoundTripMs,
    string RawPayload,
    long StartedTimestamp) : TimeSourceResult(Source, RoundTripMs)
{
    public override string OutcomeCode => "ok";
}

/// <summary>The provider could not give a trustworthy time.</summary>
/// <param name="HttpStatus">The status code for <see cref="TimeSourceFailureReason.HttpError"/> and <see cref="TimeSourceFailureReason.RateLimited"/>.</param>
/// <param name="Detail">Why, for logs only (never sent to clients).</param>
internal sealed record TimeSourceFailure(
    string Source,
    TimeSourceFailureReason Reason,
    int RoundTripMs,
    string Detail,
    int? HttpStatus = null) : TimeSourceResult(Source, RoundTripMs)
{
    public override string OutcomeCode => Reason switch
    {
        TimeSourceFailureReason.Timeout => "timeout",
        TimeSourceFailureReason.HttpError => "http_error",
        TimeSourceFailureReason.RateLimited => "rate_limited",
        TimeSourceFailureReason.InvalidResponse => "invalid_response",
        TimeSourceFailureReason.NetworkError => "network_error",
        TimeSourceFailureReason.Unexpected => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(Reason), Reason, null),
    };
}

internal enum TimeSourceFailureReason
{
    /// <summary>No complete response within <c>TrustedClock:TimeoutMs</c>.</summary>
    Timeout,

    /// <summary>A non-200 status other than 429.</summary>
    HttpError,

    /// <summary>HTTP 429.</summary>
    RateLimited,

    /// <summary>200, but the body failed validation (not JSON, missing fields, wrong zone, inconsistent values, ...).</summary>
    InvalidResponse,

    /// <summary>DNS, connection refused, TLS, connection reset, ...</summary>
    NetworkError,

    /// <summary>
    /// The source threw instead of returning a result (a bug, or a failure mode it doesn't map). Set by the
    /// orchestrator, not by the source, so one broken source can't take the other one down with it (QA-06).
    /// </summary>
    Unexpected,
}
