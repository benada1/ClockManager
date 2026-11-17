using ClockManager.Api.Common.Errors;

namespace ClockManager.Api.Application.Time;

/// <summary>What happened with one provider during a failed attempt, as a short reason code (no raw bodies).</summary>
/// <param name="Name">Provider name, e.g. "time.now".</param>
/// <param name="Outcome">Short reason code, e.g. "ok", "timeout", "http_error", "rate_limited", "invalid_response", "network_error", "error" (the source threw).</param>
public sealed record ProviderOutcome(string Name, string Outcome);

/// <summary>
/// No trustworthy time is available, so the punch fails closed (CLAUDE.md rule 1). Always HTTP 503.
/// The ProblemDetails body carries the stable <c>code</c> and a <c>providers</c> extension
/// (<c>[{ "name": ..., "outcome": ... }]</c>) so clients and operators can see why, without any raw provider data.
/// </summary>
public sealed class TrustedTimeException : AppException
{
    public const string ProvidersKey = "providers";

    private static readonly IReadOnlyDictionary<string, string> Titles = new Dictionary<string, string>
    {
        [ErrorCodes.TimeUnavailable] = "Trusted time is unavailable.",
        [ErrorCodes.TimeInvalidResponse] = "The time providers returned invalid data.",
        [ErrorCodes.TimeProvidersDisagree] = "The time providers disagree.",
    };

    private const string ClientDetail =
        "Clock in/out needs the time from an external Europe/Zurich time source, and none could be trusted right now. " +
        "Nothing was recorded. Please try again in a moment.";

    public TrustedTimeException(string code, IReadOnlyList<ProviderOutcome> providers, Exception? innerException = null)
        : base(
            code,
            StatusCodes.Status503ServiceUnavailable,
            TitleFor(code),
            ClientDetail,
            new Dictionary<string, object?> { [ProvidersKey] = providers ?? throw new ArgumentNullException(nameof(providers)) },
            innerException)
    {
        Providers = providers;
    }

    /// <summary>Per-provider outcomes, in the order the providers are configured.</summary>
    public IReadOnlyList<ProviderOutcome> Providers { get; }

    private static string TitleFor(string code) =>
        code is not null && Titles.TryGetValue(code, out var title)
            ? title
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Not a trusted-time error code.");
}
