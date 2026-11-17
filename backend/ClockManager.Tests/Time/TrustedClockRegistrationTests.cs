using System.Collections.Concurrent;
using System.Net;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Time;

/// <summary>
/// <c>AddTrustedClock()</c> wiring (plan 01 step 2): the app's <see cref="ITrustedClock"/> is the real orchestrator,
/// and <c>Primary.Name</c> / <c>Secondary.Name</c> pick which provider client plays which role. Each provider's parser
/// must only ever be pointed at its own provider's endpoint.
///
/// Test seam: every <see cref="HttpClient"/> the factory creates gets a routing stub as its primary handler
/// (<c>ConfigureAll&lt;HttpClientFactoryOptions&gt;</c>, so it does not depend on how the clients are named). The stub
/// answers by request path and records (host, path) pairs: the host shows which configured role (BaseUrl) the request
/// used, the path shows which provider client sent it.
/// </summary>
public sealed class TrustedClockRegistrationTests
{
    private const string TimeNowPath = "/developer/api/timezone/Europe/Zurich";
    private const string TimeApiPath = "/api/time/current/zone";

    // ---------- Source identity ----------

    [Fact]
    public void Time_now_source_has_a_fixed_name()
    {
        Assert.Equal("time.now", TimeNowSource.ProviderName);
    }

    [Fact]
    public void Time_now_source_name_does_not_come_from_the_primary_role()
    {
        // With roles swapped, Primary.Name is "timeapi.io". The time.now client must still call itself "time.now",
        // otherwise its readings would be attributed to the wrong provider in the audit trail.
        var options = Options.Create(new TrustedClockOptions
        {
            Primary = new TimeProviderEndpoint { Name = "timeapi.io", BaseUrl = "https://timeapi.io/" },
            Secondary = new TimeProviderEndpoint { Name = "time.now", BaseUrl = "https://time.now/" },
        });
        using var http = new HttpClient(StubHttpMessageHandler.Returning(HttpStatusCode.OK, "{}")) { BaseAddress = new Uri("https://time.now/") };

        var source = new TimeNowSource(http, new FakeElapsedTimer(), options, ZurichTime.FromTzdb());

        Assert.Equal("time.now", source.Name);
    }

    // ---------- ITrustedClock from the real host ----------

    [Fact]
    public void ITrustedClock_resolves_to_the_orchestrator()
    {
        using var factory = new ApiFactory();
        using var scope = factory.Services.CreateScope();

        var clock = scope.ServiceProvider.GetRequiredService<ITrustedClock>();

        Assert.IsType<TrustedClock>(clock);
    }

    [Fact]
    public async Task Default_configuration_uses_time_now_as_primary_and_timeapi_io_as_secondary()
    {
        var requests = new ConcurrentQueue<(string Host, string Path)>();
        using var factory = WithStubbedProviders(requests, config: []);

        var result = await GetNowAsync(factory);

        Assert.Equal("time.now", result.Provider);
        Assert.Equal("timeapi.io", result.CrossCheckedBy);
        Assert.Equal(Utc("2026-01-12T07:00:00Z"), result.Utc);
        Assert.Equal(
            new[] { ("time.now", TimeNowPath), ("timeapi.io", TimeApiPath) }.Order(),
            requests.Order());
    }

    [Theory]
    [InlineData("time.now", "timeapi.io")]
    [InlineData("timeapi.io", "time.now")] // roles swapped
    public async Task Option_names_choose_which_client_plays_which_role(string primaryName, string secondaryName)
    {
        var requests = new ConcurrentQueue<(string Host, string Path)>();
        using var factory = WithStubbedProviders(requests, new Dictionary<string, string?>
        {
            ["TrustedClock:Primary:Name"] = primaryName,
            ["TrustedClock:Primary:BaseUrl"] = "https://primary.test/",
            ["TrustedClock:Secondary:Name"] = secondaryName,
            ["TrustedClock:Secondary:BaseUrl"] = "https://secondary.test/",
        });

        var result = await GetNowAsync(factory);

        Assert.Equal(primaryName, result.Provider);
        Assert.Equal(secondaryName, result.CrossCheckedBy);
        // Each role's BaseUrl is called with ITS provider's path: the time.now parser never reads a timeapi.io body.
        Assert.Equal(
            new[] { ("primary.test", PathOf(primaryName)), ("secondary.test", PathOf(secondaryName)) }.Order(),
            requests.Order());
    }

    // ---------- Unknown provider name fails on startup ----------

    [Theory]
    [InlineData("TrustedClock:Primary:Name", "worldtimeapi.org")]
    [InlineData("TrustedClock:Secondary:Name", "worldtimeapi.org")]
    [InlineData("TrustedClock:Primary:Name", "time.now.evil")]
    public void Host_fails_to_start_with_an_unknown_provider_name(string key, string value)
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

        var validation = HostStartup.FindStartupValidationFailure(factory);

        Assert.NotNull(validation);
        Assert.Contains(value, validation.Message);
    }

    // ---------- Helpers ----------

    private static string PathOf(string providerName) => providerName switch
    {
        "time.now" => TimeNowPath,
        "timeapi.io" => TimeApiPath,
        _ => throw new ArgumentOutOfRangeException(nameof(providerName)),
    };

    private static async Task<TrustedInstant> GetNowAsync(WebApplicationFactory<Program> factory)
    {
        using var scope = factory.Services.CreateScope();
        var clock = scope.ServiceProvider.GetRequiredService<ITrustedClock>();
        return await clock.GetNowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>
    /// The real host, with every outgoing HttpClient answered by path: time.now's path gets the T1 time.now fixture,
    /// timeapi.io's path the T1 timeapi.io fixture (both 2026-01-12T07:00Z), anything else 404.
    /// </summary>
    private static WebApplicationFactory<Program> WithStubbedProviders(
        ConcurrentQueue<(string Host, string Path)> requests, Dictionary<string, string?> config)
    {
        var timeNowBody = Fixtures.Read("TimeNow/t1-winter.json");
        var timeApiBody = Fixtures.Read("TimeApiIo/t1-winter.json");

        Task<HttpResponseMessage> Route(HttpRequestMessage request, CancellationToken _)
        {
            var uri = request.RequestUri!;
            requests.Enqueue((uri.Host, uri.AbsolutePath));
            var response = uri.AbsolutePath switch
            {
                TimeNowPath => StubHttpMessageHandler.Response(HttpStatusCode.OK, timeNowBody),
                TimeApiPath when Uri.UnescapeDataString(uri.Query) == "?timeZone=Europe/Zurich"
                    => StubHttpMessageHandler.Response(HttpStatusCode.OK, timeApiBody),
                _ => StubHttpMessageHandler.Response(HttpStatusCode.NotFound, "{}"),
            };
            return Task.FromResult(response);
        }

        return new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            builder.ConfigureTestServices(services =>
                services.ConfigureAll<HttpClientFactoryOptions>(o =>
                    o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = new StubHttpMessageHandler(Route))));
        });
    }

    private static NodaTime.Instant Utc(string iso) => NodaTime.Text.InstantPattern.ExtendedIso.Parse(iso).GetValueOrThrow();
}
