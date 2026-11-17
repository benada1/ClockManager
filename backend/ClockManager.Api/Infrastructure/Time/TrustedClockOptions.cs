using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// Trusted-clock configuration (section <c>TrustedClock</c>), validated on startup (plans/01-trusted-clock.md §4.5,
/// D-010). No API keys are involved, so nothing here is secret.
/// </summary>
public sealed class TrustedClockOptions : IValidatableObject
{
    public const string SectionName = "TrustedClock";

    /// <summary>Per-provider request timeout, covering connect, headers and body.</summary>
    [Range(500, 10_000)]
    public int TimeoutMs { get; set; } = 3000;

    /// <summary>
    /// Two providers further apart than this (after midpoint correction) disagree, unless their round trips alone
    /// can explain the gap (D-008).
    /// </summary>
    [Range(100, 30_000)]
    public int CrossCheckToleranceMs { get; set; } = 2000;

    /// <summary>A round trip longer than this marks the reading as low precision. Must be below <see cref="TimeoutMs"/>.</summary>
    [Range(100, 10_000)]
    public int LowPrecisionThresholdMs { get; set; } = 1500;

    /// <summary>Connection-pool settings of the two provider clients (QA-11, D-037).</summary>
    [ValidateObjectMembers]
    public ProviderConnectionOptions Connection { get; set; } = new();

    /// <summary>Startup connection warm-up (QA-11, D-037). Opens connections only; never yields a time.</summary>
    [ValidateObjectMembers]
    public ProviderWarmupOptions Warmup { get; set; } = new();

    [Required]
    public TimeProviderEndpoint? Primary { get; set; }

    [Required]
    public TimeProviderEndpoint? Secondary { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (LowPrecisionThresholdMs >= TimeoutMs)
        {
            yield return new ValidationResult(
                $"{nameof(LowPrecisionThresholdMs)} ({LowPrecisionThresholdMs}) must be less than {nameof(TimeoutMs)} ({TimeoutMs}).",
                [nameof(LowPrecisionThresholdMs), nameof(TimeoutMs)]);
        }

        if (Connection.ConnectTimeoutMs >= TimeoutMs)
        {
            yield return new ValidationResult(
                $"Connection.{nameof(ProviderConnectionOptions.ConnectTimeoutMs)} ({Connection.ConnectTimeoutMs}) must be less than {nameof(TimeoutMs)} ({TimeoutMs}).",
                ["Connection.ConnectTimeoutMs", nameof(TimeoutMs)]);
        }

        foreach (var (label, endpoint) in new[] { (nameof(Primary), Primary), (nameof(Secondary), Secondary) })
        {
            if (endpoint is null) continue; // [Required] reports it
            foreach (var error in endpoint.ValidateEndpoint(label)) yield return error;
        }

        if (Primary?.Name is { Length: > 0 } p && Secondary?.Name is { Length: > 0 } s
            && string.Equals(p, s, StringComparison.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                $"{nameof(Primary)}.Name and {nameof(Secondary)}.Name must differ (both are '{p}').",
                [nameof(Primary), nameof(Secondary)]);
        }
    }
}

/// <summary>
/// <c>SocketsHttpHandler</c> pool settings for the provider clients (QA-11). The defaults keep a warm connection
/// between punches without pinning DNS forever. A ping delay of 0 turns keep-alive pings off.
/// </summary>
public sealed class ProviderConnectionOptions
{
    /// <summary>A pooled connection is replaced after this long, so DNS changes are picked up.</summary>
    [Range(30, 3600)]
    public int PooledConnectionLifetimeSeconds { get; set; } = 300;

    /// <summary>An idle pooled connection is closed after this long (the .NET default is 100 s).</summary>
    [Range(10, 3600)]
    public int PooledConnectionIdleTimeoutSeconds { get; set; } = 300;

    /// <summary>TCP + TLS setup limit. Must be below <c>TimeoutMs</c>, so a dead host fails before the whole budget is gone.</summary>
    [Range(100, 10_000)]
    public int ConnectTimeoutMs { get; set; } = 1500;

    /// <summary>HTTP/2 PING every this many seconds, also on an idle connection. 0 = off.</summary>
    [Range(0, 600)]
    public int KeepAlivePingDelaySeconds { get; set; } = 30;

    /// <summary>A PING unanswered for this long closes the connection.</summary>
    [Range(1, 60)]
    public int KeepAlivePingTimeoutSeconds { get; set; } = 10;
}

/// <summary>Startup warm-up (QA-11): one request per provider, only to open the connection, in the background.</summary>
public sealed class ProviderWarmupOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>Per-provider limit for the warm-up request.</summary>
    [Range(500, 10_000)]
    public int TimeoutMs { get; set; } = 3000;
}

/// <summary>One role (primary or secondary): which built-in provider plays it, and that provider's base URL.</summary>
public sealed class TimeProviderEndpoint
{
    /// <summary>Provider names with a client in this build, matched exactly (ordinal).</summary>
    internal static readonly IReadOnlyList<string> KnownProviders = [TimeNowSource.ProviderName, TimeApiIoSource.ProviderName];

    /// <summary>One of <see cref="KnownProviders"/>.</summary>
    public string Name { get; set; } = "";

    /// <summary>Absolute http(s) URL ending in '/', so relative request paths are appended, not substituted.</summary>
    public string BaseUrl { get; set; } = "";

    internal IEnumerable<ValidationResult> ValidateEndpoint(string label)
    {
        if (string.IsNullOrWhiteSpace(Name))
        {
            yield return new ValidationResult($"{label}.Name is required.", [$"{label}.Name"]);
        }
        else if (!KnownProviders.Contains(Name, StringComparer.Ordinal))
        {
            // The name picks which built-in client plays this role, so it must match one exactly.
            yield return new ValidationResult(
                $"{label}.Name '{Name}' is not a known time provider (known: {string.Join(", ", KnownProviders)}).",
                [$"{label}.Name"]);
        }

        if (!Uri.TryCreate(BaseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
            || !BaseUrl.EndsWith('/'))
        {
            yield return new ValidationResult(
                $"{label}.BaseUrl must be an absolute http(s) URL ending in '/' (got '{BaseUrl}').",
                [$"{label}.BaseUrl"]);
        }
    }
}
