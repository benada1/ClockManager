using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Infrastructure;

/// <summary>
/// The real API host in a chosen environment, with the two time providers replaced by <see cref="FakeTimeSource"/>s
/// and the monotonic timer by a <see cref="FakeElapsedTimer"/> the test moves by hand. Everything else (the
/// orchestrator, the health cache, controller discovery) is the production wiring. No network.
/// </summary>
internal sealed class FakeProvidersApiFactory(ITimeSource primary, ITimeSource secondary, string environment = "Testing")
    : WebApplicationFactory<Program>
{
    public FakeElapsedTimer Timer { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IElapsedTimer>(Timer);
            // Last registration wins: ITrustedClock, the health check and the Time lab all resolve this one.
            services.AddTransient(sp => new TrustedClock(
                primary,
                secondary,
                sp.GetRequiredService<IOptions<TrustedClockOptions>>(),
                sp.GetRequiredService<IElapsedTimer>(),
                NullLogger<TrustedClock>.Instance));
        });
    }
}
