using System.Net.Http.Headers;
using ClockManager.Api.Application.Time;
using Microsoft.Extensions.Options;
using NodaTime;

namespace ClockManager.Api.Infrastructure.Time;

public static class TrustedClockServiceCollectionExtensions
{
    /// <summary>
    /// Registers the trusted-clock building blocks: validated options, the Zurich zone, the elapsed timer and
    /// the provider clients. (The <see cref="ITrustedClock"/> orchestrator and the secondary provider come in
    /// plan 01 step 2.)
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

        services.AddHttpClient<TimeNowSource>((sp, client) => ConfigureClient(client, sp, o => o.Primary!));
        services.AddTransient<ITimeSource>(sp => sp.GetRequiredService<TimeNowSource>());

        return services;
    }

    private static void ConfigureClient(HttpClient client, IServiceProvider sp, Func<TrustedClockOptions, TimeProviderEndpoint> endpoint)
    {
        var options = sp.GetRequiredService<IOptions<TrustedClockOptions>>().Value;
        client.BaseAddress = new Uri(endpoint(options).BaseUrl, UriKind.Absolute);
        // The real per-request timeout is the CancellationTokenSource in HttpTimeSource. This is only a backstop
        // so a bug there can never leave a request hanging for HttpClient's default 100 s.
        client.Timeout = TimeSpan.FromMilliseconds(options.TimeoutMs + 1000);
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClockManager", "1.0"));
    }
}
