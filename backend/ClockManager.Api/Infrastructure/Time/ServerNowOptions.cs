using System.ComponentModel.DataAnnotations;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>Settings of the status endpoint's "server now" (section <c>ServerNow</c>). Validated on startup.</summary>
public sealed class ServerNowOptions
{
    public const string SectionName = "ServerNow";

    /// <summary>
    /// How long a fetched trusted time is served before the providers are asked again, in seconds. A cache TTL: it
    /// only affects the status display and flags, never a punch (D-035). Longer than 30 s would make the display too stale.
    /// </summary>
    [Range(1, 30)]
    public int CacheSeconds { get; set; } = 10;
}
