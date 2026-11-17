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

        services.AddHttpClient<TimeNowSource>((sp, client) => ConfigureClient(client, sp, TimeNowSource.ProviderName));
        services.AddHttpClient<TimeApiIoSource>((sp, client) => ConfigureClient(client, sp, TimeApiIoSource.ProviderName));
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

        return services;
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
