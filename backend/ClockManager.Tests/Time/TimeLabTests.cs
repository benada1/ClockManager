using System.Net;
using System.Text.Json;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Common.Errors;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using ClockManager.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Time;

/// <summary>
/// The Development-only Time lab, <c>GET /api/dev/time-lab</c> (plans/01-trusted-clock.md §6 step 3b, D-013).
/// Mapper tests pin every field of the contract the frontend page codes against; endpoint tests pin the JSON names,
/// "always 200", "never cached", and that the route does not exist outside Development.
/// </summary>
public sealed class TimeLabTests
{
    private const string TimeNow = "time.now";
    private const string TimeApi = "timeapi.io";
    private const string Url = "/api/dev/time-lab";

    private static readonly Instant T1 = Utc("2026-01-12T07:00:00Z"); // matrix T1 (winter, +01:00)
    private static readonly Instant T2 = Utc("2026-07-06T06:00:00Z"); // matrix T2 (summer, +02:00)

    private readonly FakeElapsedTimer _timer = new();

    // ---------- Mapper: accepted ----------

    [Fact]
    public async Task Accepted_and_cross_checked_maps_every_field()
    {
        var run = await RunAsync(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 340, rawPayload: "{\"p\":1}")),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromMilliseconds(37), roundTripMs: 340, rawPayload: "{\"s\":2}")));

        var lab = TimeLabMapper.Map(run, ZurichTime.FromTzdb());

        Assert.Equal(
            new TimeLabProvider("primary", TimeNow, "ok", 340, null, null,
                "2026-01-12T07:00:00Z", "+01:00", "2026-01-12T08:00:00", "{\"p\":1}"),
            lab.Providers[0]);
        Assert.Equal(
            new TimeLabProvider("secondary", TimeApi, "ok", 340, null, null,
                "2026-01-12T07:00:00.037Z", "+01:00", "2026-01-12T08:00:00.037", "{\"s\":2}"),
            lab.Providers[1]);
        Assert.Equal(new TimeLabCrossCheck(37, 2000, 340, true), lab.CrossCheck);
        Assert.Equal(
            new TimeLabDecision("accepted", TimeNow, TimeApi, false,
                "2026-01-12T07:00:00Z", "2026-01-12T08:00:00", "+01:00", null, null),
            lab.Decision);
    }

    [Fact]
    public async Task Accepted_single_source_has_no_cross_check_and_shows_the_failed_provider()
    {
        var run = await RunAsync(
            FakeTimeSource.Returning(FakeTimeSource.HttpFailure(TimeNow, 500, roundTripMs: 120)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T2, roundTripMs: 1800)));

        var lab = TimeLabMapper.Map(run, ZurichTime.FromTzdb());

        Assert.Equal(
            new TimeLabProvider("primary", TimeNow, "http_error", 120, 500, "HTTP 500", null, null, null, null),
            lab.Providers[0]);
        Assert.Equal("+02:00", lab.Providers[1].ZurichOffset);
        Assert.Null(lab.CrossCheck);
        Assert.Equal(
            new TimeLabDecision("accepted", TimeApi, null, true, // 1800 ms > 1500 ms → low precision (E15)
                "2026-07-06T06:00:00Z", "2026-07-06T08:00:00", "+02:00", null, null),
            lab.Decision);
    }

    [Fact]
    public async Task Sub_second_instants_keep_their_fraction_in_iso_form()
    {
        var instant = Utc("2026-11-17T12:02:58.446011Z");
        var run = await RunAsync(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, instant)),
            FakeTimeSource.Returning(FakeTimeSource.Failure(TimeApi, TimeSourceFailureReason.Timeout)));

        var decision = TimeLabMapper.Map(run, ZurichTime.FromTzdb()).Decision;

        Assert.Equal("2026-11-17T12:02:58.446011Z", decision.Utc);
        Assert.Equal("2026-11-17T13:02:58.446011", decision.ZurichLocal);
        Assert.Equal("+01:00", decision.ZurichOffset);
    }

    [Theory]
    [InlineData("2026-10-25T00:30:00Z", "2026-10-25T02:30:00", "+02:00")] // T6 clock in: first 02:30
    [InlineData("2026-10-25T01:15:00Z", "2026-10-25T02:15:00", "+01:00")] // T6 clock out: after fall-back
    [InlineData("2026-03-29T01:00:00Z", "2026-03-29T03:00:00", "+02:00")] // T7: never shown as 02:00
    public async Task Local_time_and_offset_come_from_tzdb_around_DST(string utc, string local, string offset)
    {
        // The fake reading claims a wrong offset; the lab must still show tzdb's, computed from the instant.
        var instant = Utc(utc);
        var run = await RunAsync(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, instant, offset: Offset.FromHours(5))),
            FakeTimeSource.Returning(FakeTimeSource.Failure(TimeApi, TimeSourceFailureReason.NetworkError)));

        var lab = TimeLabMapper.Map(run, ZurichTime.FromTzdb());

        Assert.Equal((local, offset), (lab.Providers[0].ZurichLocal, lab.Providers[0].ZurichOffset));
        Assert.Equal((local, offset), (lab.Decision.ZurichLocal, lab.Decision.ZurichOffset));
    }

    // ---------- Mapper: failed ----------

    [Fact]
    public async Task Disagreement_is_a_failed_decision_with_the_cross_check_and_both_readings()
    {
        var run = await RunAsync(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1, roundTripMs: 400)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1 + Duration.FromSeconds(5), roundTripMs: 400)));

        var lab = TimeLabMapper.Map(run, ZurichTime.FromTzdb());

        Assert.Equal(new TimeLabCrossCheck(5000, 2000, 400, false), lab.CrossCheck);
        Assert.Equal("2026-01-12T07:00:05Z", lab.Providers[1].Utc);
        Assert.Equal(
            new TimeLabDecision("failed", null, null, null, null, null, null, ErrorCodes.TimeProvidersDisagree, 503),
            lab.Decision);
    }

    [Fact]
    public async Task All_down_is_a_failed_decision_with_each_providers_detail()
    {
        var run = await RunAsync(
            FakeTimeSource.Returning(FakeTimeSource.Failure(TimeNow, TimeSourceFailureReason.Timeout, roundTripMs: 3000)),
            FakeTimeSource.Returning(FakeTimeSource.HttpFailure(TimeApi, 429, roundTripMs: 80)));

        var lab = TimeLabMapper.Map(run, ZurichTime.FromTzdb());

        Assert.Equal(
            new TimeLabProvider("primary", TimeNow, "timeout", 3000, null, "fake Timeout", null, null, null, null),
            lab.Providers[0]);
        Assert.Equal(
            new TimeLabProvider("secondary", TimeApi, "rate_limited", 80, 429, "HTTP 429", null, null, null, null),
            lab.Providers[1]);
        Assert.Null(lab.CrossCheck);
        Assert.Equal(
            new TimeLabDecision("failed", null, null, null, null, null, null, ErrorCodes.TimeUnavailable, 503),
            lab.Decision);
    }

    // ---------- Endpoint ----------

    [Fact]
    public async Task Development_returns_200_with_the_contract_json_names()
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1)),
            FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1)),
            "Development");

        var (status, json) = await GetAsync(factory);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(new[] { "providers", "crossCheck", "decision" }, Names(json));
        Assert.All(json.GetProperty("providers").EnumerateArray(), p => Assert.Equal(
            new[] { "role", "name", "outcome", "roundTripMs", "httpStatus", "detail", "utc", "zurichOffset", "zurichLocal", "rawPayload" },
            Names(p)));
        Assert.Equal(new[] { "deltaMs", "toleranceMs", "roundTripBoundMs", "agrees" }, Names(json.GetProperty("crossCheck")));
        Assert.Equal(
            new[] { "result", "provider", "crossCheckedBy", "lowPrecision", "utc", "zurichLocal", "zurichOffset", "code", "httpStatus" },
            Names(json.GetProperty("decision")));
        Assert.Equal("accepted", json.GetProperty("decision").GetProperty("result").GetString());
        Assert.Equal("2026-01-12T07:00:00Z", json.GetProperty("decision").GetProperty("utc").GetString());
        Assert.Equal(["primary", "secondary"], json.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Development_returns_200_even_when_a_punch_would_get_503()
    {
        await using var factory = new FakeProvidersApiFactory(
            FakeTimeSource.Returning(FakeTimeSource.Failure(TimeNow, TimeSourceFailureReason.Timeout)),
            FakeTimeSource.Returning(FakeTimeSource.Failure(TimeApi, TimeSourceFailureReason.NetworkError)),
            "Development");

        var (status, json) = await GetAsync(factory);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("crossCheck").ValueKind);
        var decision = json.GetProperty("decision");
        Assert.Equal("failed", decision.GetProperty("result").GetString());
        Assert.Equal("time.unavailable", decision.GetProperty("code").GetString());
        Assert.Equal(503, decision.GetProperty("httpStatus").GetInt32());
        Assert.Equal(JsonValueKind.Null, decision.GetProperty("utc").ValueKind);
    }

    [Fact]
    public async Task Every_request_asks_the_providers_again()
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1));
        await using var factory = new FakeProvidersApiFactory(primary, secondary, "Development");
        using var client = factory.CreateClient();

        await client.GetAsync(Url);
        await client.GetAsync(Url);

        Assert.Equal(2, primary.Calls);
        Assert.Equal(2, secondary.Calls);
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Testing")]
    [InlineData("Staging")]
    public async Task Does_not_exist_outside_Development(string environment)
    {
        var primary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeNow, T1));
        var secondary = FakeTimeSource.Returning(FakeTimeSource.Reading(TimeApi, T1));
        await using var factory = new FakeProvidersApiFactory(primary, secondary, environment);
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(Url);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, primary.Calls);
        Assert.Null(factory.Services.GetService(typeof(ITimeLab)));
    }

    // ---------- Helpers ----------

    private static Instant Utc(string iso) => InstantPattern.ExtendedIso.Parse(iso).Value;

    private static IEnumerable<string> Names(JsonElement obj) => obj.EnumerateObject().Select(p => p.Name);

    private static async Task<(HttpStatusCode Status, JsonElement Json)> GetAsync(FakeProvidersApiFactory factory)
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Url);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
        return (response.StatusCode, json);
    }

    private Task<TrustedClockRun> RunAsync(ITimeSource primary, ITimeSource secondary)
    {
        var options = Options.Create(new TrustedClockOptions
        {
            Primary = new TimeProviderEndpoint { Name = primary.Name, BaseUrl = "https://primary.test/" },
            Secondary = new TimeProviderEndpoint { Name = secondary.Name, BaseUrl = "https://secondary.test/" },
        });
        return new TrustedClock(primary, secondary, options, _timer, NullLogger<TrustedClock>.Instance).RunAsync(CancellationToken.None);
    }
}
