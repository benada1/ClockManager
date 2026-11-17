using System.Net;
using System.Net.Http.Headers;
using ClockManager.Api.Application.Time;
using Microsoft.Extensions.Options;
using NodaTime;

namespace ClockManager.Api.Infrastructure.Time;

public static class TrustedClockServiceCollectionExtensions
{
    /// <summary>
    /// Registers the trusted clock: validated options, the Zurich zone, the elapsed timer, one typed client per
    /// provider, and <see cref="ITrustedClock"/> (the <see cref="TrustedClock"/> orchestrator).
    /// <c>Primary.Name</c> / <c>Secondary.Name</c> choose which provider plays which role, by exact name; each role's
    /// <c>BaseUrl</c> goes to the client of the provider named in that role.
    /// </summary>
    public static IServiceCollection AddTrustedClock(this IServiceCollection services)
    {
        services.AddOptions<TrustedClockOptions>()
            .BindConfiguration(TrustedClockOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Resolved once; tzdb lookups are not repeated per call.
        services.AddSingleton(new ZurichTime(DateTimeZoneProviders.Tzdb));
        services.AddSingleton<IElapsedTimer, StopwatchElapsedTimer>();

        // Explicit client names: the warm-up asks the factory for the same named client, so it shares the connection pool.
        services.AddHttpClient<TimeNowSource>(TimeNowClientName, (sp, client) => ConfigureClient(client, sp, TimeNowSource.ProviderName))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);
        services.AddHttpClient<TimeApiIoSource>(TimeApiIoClientName, (sp, client) => ConfigureClient(client, sp, TimeApiIoSource.ProviderName))
            .ConfigurePrimaryHttpMessageHandler(CreateHandler);

        // QA-11: open the provider connections in the background after startup. It never produces a time (D-037).
        services.AddHostedService<ProviderWarmupService>();
        services.AddTransient<ITimeSource>(sp => sp.GetRequiredService<TimeNowSource>());
        services.AddTransient<ITimeSource>(sp => sp.GetRequiredService<TimeApiIoSource>());

        // Transient, like the typed clients it holds: a singleton would pin one HttpClient (and its handler) for the
        // app's lifetime, so the factory could never rotate handlers and DNS changes would be missed.
        services.AddTransient(sp =>
        {
            var options = sp.GetRequiredService<IOptions<TrustedClockOptions>>();
            var sources = sp.GetServices<ITimeSource>().ToList();
            return new TrustedClock(
                SourceNamed(sources, options.Value.Primary!.Name),
                SourceNamed(sources, options.Value.Secondary!.Name),
                options,
                sp.GetRequiredService<IElapsedTimer>(),
                sp.GetRequiredService<ILogger<TrustedClock>>());
        });
        services.AddTransient<ITrustedClock>(sp => sp.GetRequiredService<TrustedClock>());

        // /health/time: a singleton that owns the 10 s cache (D-012). Each refresh resolves a fresh TrustedClock in its
        // own scope, so the typed HttpClients are disposed after use instead of being pinned by the singleton.
        services.AddSingleton(sp =>
        {
            var scopes = sp.GetRequiredService<IServiceScopeFactory>();
            return new TimeHealthCheck(
                async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    return await scope.ServiceProvider.GetRequiredService<TrustedClock>().RunAsync(ct);
                },
                sp.GetRequiredService<IElapsedTimer>(),
                sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping);
        });
        services.AddSingleton<ITimeHealth>(sp => sp.GetRequiredService<TimeHealthCheck>());

        return services;
    }

    /// <summary>
    /// The Development-only Time lab (D-013): one uncached run of the real orchestrator per request. Call this only in
    /// Development; its controller is excluded elsewhere (<c>ExcludeDevelopmentOnlyControllers</c>).
    /// </summary>
    public static IServiceCollection AddTimeLab(this IServiceCollection services)
    {
        services.AddTransient<ITimeLab, TimeLabRunner>();
        return services;
    }

    internal const string TimeNowClientName = "time.now";
    internal const string TimeApiIoClientName = "timeapi.io";

    private static SocketsHttpHandler CreateHandler(IServiceProvider sp) =>
        CreateHandler(sp.GetRequiredService<IOptions<TrustedClockOptions>>().Value.Connection);

    /// <summary>The pool settings as a handler (internal so tests can inspect it; no network is used).</summary>
    internal static SocketsHttpHandler CreateHandler(ProviderConnectionOptions c)
    {
        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromSeconds(c.PooledConnectionLifetimeSeconds),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(c.PooledConnectionIdleTimeoutSeconds),
            ConnectTimeout = TimeSpan.FromMilliseconds(c.ConnectTimeoutMs),
            // A time provider that redirects is suspicious (and the target would not be the configured host).
            AllowAutoRedirect = false,
            // One request at a time per provider: a second HTTP/2 connection would only add another cold handshake.
            EnableMultipleHttp2Connections = false,
        };
        if (c.KeepAlivePingDelaySeconds > 0)
        {
            handler.KeepAlivePingDelay = TimeSpan.FromSeconds(c.KeepAlivePingDelaySeconds);
            handler.KeepAlivePingTimeout = TimeSpan.FromSeconds(c.KeepAlivePingTimeoutSeconds);
            handler.KeepAlivePingPolicy = HttpKeepAlivePingPolicy.Always; // also while idle: that is the point
        }
        return handler;
    }

    private static ITimeSource SourceNamed(IEnumerable<ITimeSource> sources, string name) =>
        sources.SingleOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal))
        ?? throw new InvalidOperationException($"No time source named '{name}' (options validation should have rejected it).");

    private static void ConfigureClient(HttpClient client, IServiceProvider sp, string providerName)
    {
        var options = sp.GetRequiredService<IOptions<TrustedClockOptions>>().Value;
        client.BaseAddress = new Uri(EndpointFor(options, providerName).BaseUrl, UriKind.Absolute);
        // The real per-request timeout is the CancellationTokenSource in HttpTimeSource. This is only a backstop
        // so a bug there can never leave a request hanging for HttpClient's default 100 s.
        client.Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs + 1000);
        // Allow HTTP/2 (negotiated by ALPN, falls back to 1.1): only HTTP/2 has the keep-alive PING.
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClockManager", "1.0"));
    }

    /// <summary>The role (primary or secondary) whose Name is <paramref name="providerName"/>.</summary>
    private static TimeProviderEndpoint EndpointFor(TrustedClockOptions options, string providerName)
    {
        if (string.Equals(options.Primary?.Name, providerName, StringComparison.Ordinal)) return options.Primary!;
        if (string.Equals(options.Secondary?.Name, providerName, StringComparison.Ordinal)) return options.Secondary!;
        throw new InvalidOperationException($"Provider '{providerName}' is not configured as Primary or Secondary.");
    }
}
