using System.ComponentModel.DataAnnotations;

namespace ClockManager.Api.Infrastructure.RateLimiting;

/// <summary>Section <c>RateLimits</c>: fixed windows. Auth is per client IP, punches are per signed-in employee.</summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    [Range(1, 100_000)]
    public int AuthPermitLimit { get; set; } = 10;

    [Range(1, 86_400)]
    public int AuthWindowSeconds { get; set; } = 60;

    [Range(1, 100_000)]
    public int PunchPermitLimit { get; set; } = 60;

    [Range(1, 86_400)]
    public int PunchWindowSeconds { get; set; } = 60;
}
