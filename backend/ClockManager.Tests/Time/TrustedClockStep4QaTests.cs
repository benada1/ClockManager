using System.Net;
using System.Text.Json;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.TestHost;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Time;

/// <summary>
/// Plan 01 step 4: QA adversarial review of the finished trusted clock. Tests marked
/// <c></c> fail today on purpose; each links its finding (QA-1x).
/// </summary>
public sealed class TrustedClockStep4QaTests
{
    private const string TimeNow = "time.now";
    private const string TimeApi = "timeapi.io";
    private const string LeakText = "LEAK-EXCEPTION-TEXT-5c1a";

    private static readonly Instant T1 = InstantPattern.ExtendedIso.Parse("2026-01-12T07:00:00Z").Value; // matrix T1

    // ---------- QA-10: /health/time while the app is shutting down ----------

    /// <summary>
    /// QA-10 (fixed in TimeHealthController: now 503). Before the fix, during graceful shutdown (ApplicationStopping cancelled, requests still being served), the
    /// health refresh runs on the stopping token, so it ends in OperationCanceledException. The client did NOT go
    /// away, so AppExceptionHandler maps it as an unhandled error: 500 <c>server.error</c> + an Error log line.
    /// A probe or the Phase 5 banner should see "can't punch right now" (503), not a server bug.
    /// </summary>
    [Fact]
    
    public async Task QA10_health_time_during_shutdown_is_503_not_500_server_error()
    {
        await using var baseFactory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1)));
        // Graceful shutdown has begun: ApplicationStopping is cancelled while the pipeline still serves requests
        // (Kestrel drains in-flight/keep-alive requests for ShutdownTimeout). IHostApplicationLifetime.StopApplication()
        // can't be used here because TestServer disposes itself on it, so the health check gets an already-cancelled
        // stopping token; everything else is the production wiring.
        using var stopping = new CancellationTokenSource();
        stopping.Cancel();
        await using var factory = baseFactory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.AddSingleton(sp => new TimeHealthCheck(
                ct => sp.GetRequiredService<TrustedClock>().RunAsync(ct),
                sp.GetRequiredService<IElapsedTimer>(),
                stopping.Token))));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/time");
        var body = await response.Content.ReadAsStringAsync();

        Assert.True(response.StatusCode == HttpStatusCode.ServiceUnavailable,
            $"Expected 503 during shutdown, got {(int)response.StatusCode}: {body}");
        Assert.DoesNotContain("server.error", body);
    }

    // ---------- Lead 4: a provider that throws (outcome "error") ----------

    [Fact]
    public async Task Health_time_shows_error_outcome_for_a_throwing_provider_without_exception_text()
    {
        await using var factory = new FakeProvidersApiFactory(
            new FakeTimeSource(TimeNow, (_, _) => throw new InvalidOperationException(LeakText)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1)));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/time");
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode); // single-source punch still possible (D-009)
        Assert.Equal("error", json.GetProperty("providers")[0].GetProperty("status").GetString());
        Assert.DoesNotContain(LeakText, body);
        Assert.DoesNotContain(nameof(InvalidOperationException), body);
    }

    [Fact]
    public async Task Punch_503_body_for_two_throwing_providers_has_no_exception_text()
    {
        await using var factory = new FakeProvidersApiFactory(
            new FakeTimeSource(TimeNow, (_, _) => throw new InvalidOperationException(LeakText)),
            new FakeTimeSource(TimeApi, (_, _) => Task.FromException<TimeSourceResult>(new FormatException(LeakText))));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/time");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("time.unavailable", body);
        Assert.DoesNotContain(LeakText, body);
    }

    // ---------- Free hunt: body encoding edge cases ----------

    /// <summary>A UTF-8 BOM before an otherwise valid body: rejected (fail closed), never mis-parsed.</summary>
    [Theory]
    [InlineData("TimeNow/t1-winter.json")]
    [InlineData("TimeApiIo/t1-winter.json")]
    public async Task Body_with_utf8_bom_is_rejected_not_misread(string fixture)
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(System.Text.Encoding.UTF8.GetBytes(Fixtures.Read(fixture))).ToArray();
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));

        var result = await Source(fixture.StartsWith("TimeNow") ? TimeNow : TimeApi, handler).FetchAsync(CancellationToken.None);

        var failure = Assert.IsType<TimeSourceFailure>(result);
        Assert.Equal(TimeSourceFailureReason.InvalidResponse, failure.Reason);
    }

    /// <summary>Trailing garbage after a valid JSON object, and a JSON null in a required field: rejected.</summary>
    [Theory]
    [InlineData("TimeNow/t1-winter.json", "{\"x\":1}")]
    [InlineData("TimeApiIo/t1-winter.json", " garbage")]
    public async Task Body_with_trailing_content_is_rejected(string fixture, string suffix)
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(fixture).TrimEnd() + suffix);

        var result = await Source(fixture.StartsWith("TimeNow") ? TimeNow : TimeApi, handler).FetchAsync(CancellationToken.None);

        Assert.Equal(TimeSourceFailureReason.InvalidResponse, Assert.IsType<TimeSourceFailure>(result).Reason);
    }

    // ---------- Removed Time lab: the route is gone in every environment (D-087) ----------

    [Theory]
    [InlineData("Development", "/api/dev/time-lab")]
    [InlineData("Development", "/API/DEV/TIME-LAB")]
    [InlineData("Production", "/api/dev/time-lab")]
    public async Task Removed_time_lab_route_is_404(string environment, string url)
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1)),
            environment);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Swagger_is_404_outside_development()
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1)),
            "Production");
        using var client = factory.CreateClient();

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }

    private static ITimeSource Source(string name, StubHttpMessageHandler handler)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new TrustedClockOptions
        {
            Primary = new TimeProviderEndpoint { Name = TimeNow, BaseUrl = "https://time.now/" },
            Secondary = new TimeProviderEndpoint { Name = TimeApi, BaseUrl = "https://timeapi.io/" },
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(name == TimeNow ? "https://time.now/" : "https://timeapi.io/") };
        var zurich = ClockManager.Api.Application.Time.ZurichTime.FromTzdb();
        return name == TimeNow
            ? new TimeNowSource(http, new FakeElapsedTimer(), options, zurich)
            : new TimeApiIoSource(http, new FakeElapsedTimer(), options, zurich);
    }
}
