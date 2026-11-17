using ClockManager.Api.Application.Time;
using Microsoft.Extensions.Options;

namespace ClockManager.Api.Infrastructure.Time;

public static class ServerNowServiceCollectionExtensions
{
    /// <summary>
    /// Registers the status endpoint's cached server time (D-035): validated <see cref="ServerNowOptions"/> and the
    /// singleton <see cref="IServerNowProvider"/>. Needs <see cref="ITrustedClock"/> and <see cref="IElapsedTimer"/>
    /// (<c>AddTrustedClock</c>). Each fetch resolves a fresh <see cref="ITrustedClock"/> in its own scope, so the typed
    /// HttpClients behind it are disposed after use instead of being pinned by the singleton.
    /// </summary>
    public static IServiceCollection AddServerNow(this IServiceCollection services)
    {
        services.AddOptions<ServerNowOptions>()
            .BindConfiguration(ServerNowOptions.SectionName)
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<IServerNowProvider>(sp =>
        {
            var scopes = sp.GetRequiredService<IServiceScopeFactory>();
            return new ServerNowProvider(
                async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    return await scope.ServiceProvider.GetRequiredService<ITrustedClock>().GetNowAsync(ct);
                },
                sp.GetRequiredService<IElapsedTimer>(),
                TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<ServerNowOptions>>().Value.CacheSeconds),
                sp.GetRequiredService<IHostApplicationLifetime>().ApplicationStopping,
                sp.GetRequiredService<ILogger<ServerNowProvider>>());
        });

        return services;
    }
}
