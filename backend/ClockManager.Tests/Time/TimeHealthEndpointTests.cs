using System.Net;
using System.Text.Json;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Time;

/// <summary>
/// <c>GET /health/time</c> and its alias <c>/api/health/time</c> through the real host (plans/01-trusted-clock.md §4.7, D-012), with fake providers and a
/// fake monotonic timer: status codes, the exact JSON shape, nothing sensitive in it, the 10 s cache, and the punch
/// path staying uncached.
/// </summary>
public abstract class TimeHealthEndpointTestsBase(string url)
{
    private const string TimeNow = "time.now";
    private const string TimeApi = "timeapi.io";
    private readonly string Url = url;

    private static readonly Instant T1 = InstantPattern.ExtendedIso.Parse("2026-01-12T07:00:00Z").Value; // matrix T1

    private const string LeakDetail = "LEAK-DETAIL-8d2e";
    private const string LeakPayload = "{\"LEAK-PAYLOAD\":\"b71f\"}";

    [Fact]
    public async Task Returns_200_with_the_documented_shape_when_a_punch_would_succeed()
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 340)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(37), roundTripMs: 340)));

        var (status, json) = await GetAsync(factory);

        Assert.Equal(HttpStatusCode.OK, status);
        AssertReportShape(json);
        Assert.True(json.GetProperty("canPunch").GetBoolean());
        Assert.Equal("agree", json.GetProperty("crossCheck").GetString());
        Assert.Equal(37, json.GetProperty("deltaMs").GetInt32());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("code").ValueKind);
        var providers = json.GetProperty("providers").EnumerateArray().ToList();
        Assert.Equal([TimeNow, TimeApi], providers.Select(p => p.GetProperty("name").GetString()));
        Assert.All(providers, p => Assert.Equal("ok", p.GetProperty("status").GetString()));
        Assert.All(providers, p => Assert.Equal(340, p.GetProperty("roundTripMs").GetInt32()));
    }

    [Fact]
    public async Task Returns_503_when_the_providers_disagree()
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromSeconds(5))));

        var (status, json) = await GetAsync(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        AssertReportShape(json);
        Assert.False(json.GetProperty("canPunch").GetBoolean());
        Assert.Equal("disagree", json.GetProperty("crossCheck").GetString());
        Assert.Equal(5000, json.GetProperty("deltaMs").GetInt32());
        Assert.Equal("time.providers_disagree", json.GetProperty("code").GetString());
        Assert.Equal(["ok", "ok"], Statuses(json));
    }

    [Fact]
    public async Task Returns_503_when_all_providers_are_down()
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Failure(TimeNow, TimeSourceFailureReason.Timeout, roundTripMs: 3000)),
            FakeTimeSource.Returning(FakeTimeSource.HttpFailure(TimeApi, 500, roundTripMs: 90)));

        var (status, json) = await GetAsync(factory);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        AssertReportShape(json);
        Assert.False(json.GetProperty("canPunch").GetBoolean());
        Assert.Equal("not_checked", json.GetProperty("crossCheck").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("deltaMs").ValueKind);
        Assert.Equal("time.unavailable", json.GetProperty("code").GetString());
        Assert.Equal(["timeout", "http_error"], Statuses(json));
        Assert.Equal([3000, 90], json.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("roundTripMs").GetInt32()));
    }

    [Fact]
    public async Task Never_exposes_provider_detail_payload_or_parsed_time()
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, rawPayload: LeakPayload)),
            FakeTimeSource.Returning(new TimeSourceFailure(TimeApi, TimeSourceFailureReason.InvalidResponse, 300, LeakDetail)));

        using var client = factory.CreateClient();
        var body = await client.GetStringAsync(Url);

        Assert.DoesNotContain("LEAK", body);
        Assert.DoesNotContain("2026", body); // no provider time either
        Assert.DoesNotContain("detail", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("payload", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Second_request_within_10_s_does_not_call_the_providers_and_after_expiry_it_does()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1));
        await using var factory = new FakeProvidersApiFactory(primary, secondary);
        using var client = factory.CreateClient();

        await client.GetAsync(Url);
        factory.Timer.Advance(TimeSpan.FromSeconds(9));
        await client.GetAsync(Url);
        Assert.Equal(1, primary.Calls);
        Assert.Equal(1, secondary.Calls);

        factory.Timer.Advance(TimeSpan.FromSeconds(1));
        await client.GetAsync(Url);
        Assert.Equal(2, primary.Calls);
        Assert.Equal(2, secondary.Calls);
    }

    [Fact]
    public async Task Punch_path_never_uses_the_health_cache()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1));
        await using var factory = new FakeProvidersApiFactory(primary, secondary);
        using var client = factory.CreateClient();

        await client.GetAsync(Url); // fills the health cache (1 call each)
        using (var scope = factory.Services.CreateScope())
        {
            var clock = scope.ServiceProvider.GetRequiredService<ITrustedClock>();
            await clock.GetNowAsync(CancellationToken.None);
            await clock.GetNowAsync(CancellationToken.None);
        }

        // Timer never moved: the health report is still fresh, yet every punch asked both providers.
        Assert.Equal(3, primary.Calls);
        Assert.Equal(3, secondary.Calls);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Development")]
    public async Task Is_public_in_every_environment(string environment)
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1)),
            environment);

        var (status, _) = await GetAsync(factory);

        Assert.Equal(HttpStatusCode.OK, status);
    }

    // ---------- Helpers ----------

    private async Task<(HttpStatusCode Status, JsonElement Json)> GetAsync(FakeProvidersApiFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return (response.StatusCode, json);
    }

    private static IEnumerable<string?> Statuses(JsonElement json) =>
        json.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("status").GetString());

    /// <summary>Exactly the documented members, nothing more (plan §4.7).</summary>
    private static void AssertReportShape(JsonElement json)
    {
        Assert.Equal(
            new[] { "canPunch", "providers", "crossCheck", "deltaMs", "code" },
            json.EnumerateObject().Select(p => p.Name));
        Assert.All(json.GetProperty("providers").EnumerateArray(), p => Assert.Equal(
            new[] { "name", "status", "roundTripMs" },
            p.EnumerateObject().Select(x => x.Name)));
    }
}

/// <summary>The original route.</summary>
public sealed class TimeHealthEndpointTests() : TimeHealthEndpointTestsBase("/health/time");

/// <summary>The alias under /api (the Vite dev proxy only forwards /api): same behaviour, same tests.</summary>
public sealed class TimeHealthApiAliasEndpointTests() : TimeHealthEndpointTestsBase("/api/health/time");
