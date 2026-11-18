using System.Globalization;
using System.Threading.RateLimiting;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Infrastructure.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace ClockManager.Api.Infrastructure.RateLimiting;

public static class RateLimitingExtensions
{
    public const string AuthPolicy = "auth-by-ip";
    public const string PunchPolicy = "punch-by-user";

    /// <summary>Two named policies (put on controller actions with <c>[EnableRateLimiting]</c>) and a 429 ProblemDetails.</summary>
    public static IServiceCollection AddAppRateLimiting(this IServiceCollection services)
    {
        services.AddOptions<RateLimitOptions>()
            .BindConfiguration(RateLimitOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitOptions>>((limiter, configured) =>
        {
            var limits = configured.Value;
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            limiter.AddPolicy(AuthPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                PartitionKey(http.Connection.RemoteIpAddress),
                _ => Window(limits.AuthPermitLimit, limits.AuthWindowSeconds)));

            // Not signed in: no limit here (the 401 comes next). The partition is the token's subject, so it needs UseAuthentication first.
            limiter.AddPolicy(PunchPolicy, http =>
            {
                var sub = http.User.FindFirst(JwtSettings.SubjectClaim)?.Value;
                return sub is null
                    ? RateLimitPartition.GetNoLimiter("anonymous")
                    : RateLimitPartition.GetFixedWindowLimiter(sub, _ => Window(limits.PunchPermitLimit, limits.PunchWindowSeconds));
            });

            limiter.OnRejected = async (context, ct) =>
            {
                var http = context.HttpContext;
                var retryAfter = context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var wait) ? (int)Math.Ceiling(wait.TotalSeconds) : 1;
                http.Response.Headers.RetryAfter = Math.Max(retryAfter, 1).ToString(CultureInfo.InvariantCulture);

                var problem = new ProblemDetails
                {
                    Status = StatusCodes.Status429TooManyRequests,
                    Title = "Too many requests.",
                    Detail = $"Too many requests. Try again in {Math.Max(retryAfter, 1)} second(s).",
                };
                problem.Extensions[ProblemDetailsSetup.CodeKey] = ErrorCodes.RateLimited;
                http.Response.StatusCode = StatusCodes.Status429TooManyRequests;
                await http.RequestServices.GetRequiredService<IProblemDetailsService>()
                    .TryWriteAsync(new ProblemDetailsContext { HttpContext = http, ProblemDetails = problem });
            };
        });

        return services;
    }

    /// <summary>
    /// IPv4-mapped IPv6 counts as the IPv4 address; any other IPv6 address counts as its /64 (one subscriber owns 2^64 of them,
    /// QA-46). X-Forwarded-For stays ignored (D-053).
    /// </summary>
    internal static string PartitionKey(System.Net.IPAddress? address)
    {
        if (address is null) return "unknown-ip";
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        return "v6/" + Convert.ToHexString(address.GetAddressBytes().AsSpan(0, 8));
    }

    private static FixedWindowRateLimiterOptions Window(int permits, int seconds) => new()
    {
        PermitLimit = permits,
        Window = TimeSpan.FromSeconds(seconds),
        QueueLimit = 0, // reject at once, never queue
    };
}
