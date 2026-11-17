using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Time;

/// <summary>QA-11 / D-037: pool settings and the startup warm-up. Every request goes to a stub handler: no network.</summary>
public sealed class ProviderWarmupTests
{
    private const string TimeNowPath = "/developer/api/timezone/Europe/Zurich";
    private const string TimeApiPath = "/api/time/current/zone";

    // ---------- Pool settings ----------

    [Fact]
    public void Default_pool_settings_are_applied_to_the_handler()
    {
        using var handler = TrustedClockServiceCollectionExtensions.CreateHandler(new ProviderConnectionOptions());

        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionLifetime);
        Assert.Equal(TimeSpan.FromMinutes(5), handler.PooledConnectionIdleTimeout);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), handler.ConnectTimeout);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.EnableMultipleHttp2Connections);
        Assert.Equal(TimeSpan.FromSeconds(30), handler.KeepAlivePingDelay);
        Assert.Equal(TimeSpan.FromSeconds(10), handler.KeepAlivePingTimeout);
        Assert.Equal(HttpKeepAlivePingPolicy.Always, handler.KeepAlivePingPolicy);
    }

    [Fact]
    public void Keep_alive_pings_can_be_switched_off()
    {
        using var handler = TrustedClockServiceCollectionExtensions.CreateHandler(
            new ProviderConnectionOptions { KeepAlivePingDelaySeconds = 0 });

        Assert.Equal(Timeout.InfiniteTimeSpan, handler.KeepAlivePingDelay);
    }

    [Fact]
    public void Both_provider_clients_get_a_handler_built_from_configuration()
    {
        var seen = new ConcurrentDictionary<string, SocketsHttpHandler>();
        using var factory = Host(
            new Dictionary<string, string?> { ["TrustedClock:Connection:ConnectTimeoutMs"] = "900" },
            services => services.ConfigureAll<HttpClientFactoryOptions>(o =>
                o.HttpMessageHandlerBuilderActions.Add(b =>
                {
                    // Runs after the app's own action: read the production handler, then swap in a stub.
                    if (b.PrimaryHandler is SocketsHttpHandler s) seen[b.Name!] = s;
                    b.PrimaryHandler = StubHttpMessageHandler.Returning(HttpStatusCode.NotFound, "{}");
                })));

        var clients = factory.Services.GetRequiredService<IHttpClientFactory>();
        clients.CreateClient(TrustedClockServiceCollectionExtensions.TimeNowClientName);
        clients.CreateClient(TrustedClockServiceCollectionExtensions.TimeApiIoClientName);

        Assert.Equal(2, seen.Count);
        Assert.All(seen.Values, h =>
        {
            Assert.Equal(TimeSpan.FromMilliseconds(900), h.ConnectTimeout);
            Assert.False(h.AllowAutoRedirect);
        });
    }

    [Theory]
    [InlineData("TrustedClock:Connection:PooledConnectionLifetimeSeconds", "29", "PooledConnectionLifetimeSeconds")]
    [InlineData("TrustedClock:Connection:PooledConnectionIdleTimeoutSeconds", "9", "PooledConnectionIdleTimeoutSeconds")]
    [InlineData("TrustedClock:Connection:ConnectTimeoutMs", "99", "ConnectTimeoutMs")]
    [InlineData("TrustedClock:Connection:ConnectTimeoutMs", "3000", "must be less than TimeoutMs")] // == TimeoutMs
    [InlineData("TrustedClock:Connection:KeepAlivePingDelaySeconds", "-1", "KeepAlivePingDelaySeconds")]
    [InlineData("TrustedClock:Connection:KeepAlivePingTimeoutSeconds", "0", "KeepAlivePingTimeoutSeconds")]
    [InlineData("TrustedClock:Warmup:TimeoutMs", "499", "TimeoutMs")]
    [InlineData("TrustedClock:Warmup:TimeoutMs", "10001", "TimeoutMs")]
    public void Host_fails_to_start_with_invalid_connection_or_warmup_options(string key, string value, string messageContains)
    {
        using var factory = Host(new Dictionary<string, string?> { [key] = value });

        var validation = HostStartup.FindStartupValidationFailure(factory);

        Assert.NotNull(validation);
        Assert.Contains(messageContains, validation.Message);
    }

    // ---------- Warm-up ----------

    [Fact]
    public void Warm_up_is_off_in_the_test_hosts_by_default()
    {
        var requests = new ConcurrentQueue<string>();
        using var factory = Host([], stub: Recording(requests));

        factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<IOptions<TrustedClockOptions>>().Value.Warmup.Enabled);
        Assert.Empty(requests);
    }

    [Fact]
    public async Task Warm_up_asks_each_provider_exactly_once_then_stops()
    {
        var requests = new ConcurrentQueue<string>();
        using var factory = Host(Enabled(), stub: Recording(requests));
        factory.CreateClient(); // starts the host, and with it the hosted services

        await WaitUntil(() => requests.Count >= 2);
        await Task.Delay(300); // a one-shot must not keep going

        Assert.Equal(new[] { TimeNowPath, TimeApiPath }.Order(), requests.Order());
    }

    [Theory]
    [InlineData("throw")]
    [InlineData("http500")]
    [InlineData("hang")]
    public async Task Warm_up_failure_does_not_stop_the_host_and_logs_no_exception_text(string failure)
    {
        var logs = new ConcurrentQueue<(LogLevel Level, string Message, Exception? Exception)>();
        var stub = new StubHttpMessageHandler((_, ct) => failure switch
        {
            "throw" => throw new HttpRequestException("SECRET-EXCEPTION-TEXT"),
            "http500" => Task.FromResult(StubHttpMessageHandler.Response(HttpStatusCode.InternalServerError, "SECRET-BODY")),
            _ => HangAsync(ct),
        });
        var config = Enabled();
        config["TrustedClock:Warmup:TimeoutMs"] = "500";
        using var factory = Host(config, stub: stub, configure: services =>
            services.AddSingleton<ILoggerProvider>(new ListLoggerProvider(logs)));

        using var client = factory.CreateClient();
        await WaitUntil(() => logs.Count(l => l.Message.StartsWith("Provider warm-up")) >= 2);

        var response = await client.GetAsync("/test-only/nothing");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode); // the app still serves
        var warmLogs = logs.Where(l => l.Message.StartsWith("Provider warm-up")).ToList();
        Assert.Equal(2, warmLogs.Count);
        Assert.All(warmLogs, l =>
        {
            Assert.Equal(LogLevel.Warning, l.Level);
            Assert.Null(l.Exception);
            Assert.DoesNotContain("SECRET", l.Message);
        });
    }

    [Fact]
    public async Task Warm_up_does_not_delay_host_startup()
    {
        var requests = new ConcurrentQueue<string>();
        var config = Enabled();
        config["TrustedClock:Warmup:TimeoutMs"] = "10000";
        using var factory = Host(config, stub: new StubHttpMessageHandler((r, ct) =>
        {
            requests.Enqueue(r.RequestUri!.AbsolutePath);
            return HangAsync(ct);
        }));

        // The warm-up requests never finish within this test, yet the host is up and answers at once.
        using var client = factory.CreateClient();
        var response = await client.GetAsync("/test-only/nothing").WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        await WaitUntil(() => requests.Count >= 2); // and the warm-up did start, in the background
    }

    [Fact]
    public async Task Warm_up_does_not_change_the_punch_path_which_still_asks_both_providers_fresh()
    {
        var requests = new ConcurrentQueue<string>();
        using var factory = Host(Enabled(), stub: Routed(requests));
        factory.CreateClient();
        await WaitUntil(() => requests.Count >= 2);
        await Task.Delay(200);
        Assert.Equal(2, requests.Count);

        for (var i = 1; i <= 2; i++)
        {
            using var scope = factory.Services.CreateScope();
            var now = await scope.ServiceProvider.GetRequiredService<ITrustedClock>()
                .GetNowAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("time.now", now.Provider);
            Assert.Equal("timeapi.io", now.CrossCheckedBy);
            // The warm-up's two requests were not reused as a time: every punch adds its own two.
            Assert.Equal(2 + 2 * i, requests.Count);
        }
    }

    [Fact]
    public void Warm_up_cannot_produce_a_time_it_has_no_clock_or_time_source_dependency()
    {
        var service = typeof(TrustedClockServiceCollectionExtensions).Assembly
            .GetType("ClockManager.Api.Infrastructure.Time.ProviderWarmupService", throwOnError: true)!;

        var parameters = service.GetConstructors().Single().GetParameters().Select(p => p.ParameterType).ToList();

        Assert.Equal(3, parameters.Count);
        Assert.Contains(typeof(IHttpClientFactory), parameters);
        Assert.Contains(typeof(IOptions<TrustedClockOptions>), parameters);
        Assert.Contains(typeof(ILogger<>), parameters.Where(p => p.IsGenericType).Select(p => p.GetGenericTypeDefinition()));
        Assert.DoesNotContain(parameters, t => typeof(ITrustedClock).IsAssignableFrom(t) || typeof(ITimeSource).IsAssignableFrom(t));

        // And it keeps no instant-like state.
        var fields = service.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
        Assert.DoesNotContain(fields, f => f.FieldType == typeof(DateTimeOffset) || f.FieldType == typeof(DateTime)
            || f.FieldType.Namespace == "NodaTime" || f.FieldType.Name.Contains("Instant"));
    }

    // ---------- Helpers ----------

    private static async Task<HttpResponseMessage> HangAsync(CancellationToken ct)
    {
        await Task.Delay(Timeout.Infinite, ct);
        throw new InvalidOperationException("unreachable");
    }

    private static Dictionary<string, string?> Enabled() => new() { ["TrustedClock:Warmup:Enabled"] = "true" };

    private static StubHttpMessageHandler Recording(ConcurrentQueue<string> requests) =>
        new((r, _) =>
        {
            requests.Enqueue(r.RequestUri!.AbsolutePath);
            return Task.FromResult(StubHttpMessageHandler.Response(HttpStatusCode.OK, "{}"));
        });

    /// <summary>Answers each provider path with its T1 fixture (2026-01-12T07:00Z); counts every request.</summary>
    private static StubHttpMessageHandler Routed(ConcurrentQueue<string> requests)
    {
        var timeNowBody = Fixtures.Read("TimeNow/t1-winter.json");
        var timeApiBody = Fixtures.Read("TimeApiIo/t1-winter.json");
        return new((r, _) =>
        {
            var path = r.RequestUri!.AbsolutePath;
            requests.Enqueue(path);
            return Task.FromResult(path switch
            {
                TimeNowPath => StubHttpMessageHandler.Response(HttpStatusCode.OK, timeNowBody),
                TimeApiPath => StubHttpMessageHandler.Response(HttpStatusCode.OK, timeApiBody),
                _ => StubHttpMessageHandler.Response(HttpStatusCode.NotFound, "{}"),
            });
        });
    }

    private static WebApplicationFactory<Program> Host(
        Dictionary<string, string?> config,
        Action<IServiceCollection>? configure = null,
        HttpMessageHandler? stub = null) =>
        new ApiFactory().WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config));
            builder.ConfigureTestServices(services =>
            {
                configure?.Invoke(services);
                if (stub is not null)
                {
                    services.ConfigureAll<HttpClientFactoryOptions>(o =>
                        o.HttpMessageHandlerBuilderActions.Add(b => b.PrimaryHandler = stub));
                }
            });
        });

    private static async Task WaitUntil(Func<bool> condition)
    {
        // Polling with a deadline; a test-only wait, not a record time.
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "condition not reached in time");
    }

    private sealed class ListLoggerProvider(ConcurrentQueue<(LogLevel, string, Exception?)> sink) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Logger(sink);

        public void Dispose() { }

        private sealed class Logger(ConcurrentQueue<(LogLevel, string, Exception?)> sink) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter) => sink.Enqueue((logLevel, formatter(state, exception), exception));
        }
    }
}
