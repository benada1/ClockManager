using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Text;

namespace ClockManager.Tests.Time;

/// <summary>
/// timeapi.io client and validation (plans/01-trusted-clock.md §4.3, edge cases E6–E8, E10, E11, E13, E14).
/// timeapi.io sends a Zurich wall-clock time WITHOUT an offset plus <c>dstActive</c>; the source derives the offset
/// from the flag and then checks it against tzdb. Anything doubtful is rejected, never repaired.
/// Every response comes from a stub handler; nothing touches the network.
/// </summary>
public sealed class TimeApiIoSourceTests
{
    private const string Winter = "TimeApiIo/t1-winter.json";
    private const string Summer = "TimeApiIo/t2-summer.json";
    private const string RepeatedHour = "TimeApiIo/e11-repeated-hour.json";
    private const string DstGap = "TimeApiIo/e13-dst-gap.json";
    private const string Real = "TimeApiIo/real-2026-11-17.json";

    private readonly FakeElapsedTimer _timer = new();

    // ---------- Identity and request ----------

    [Fact]
    public void Constants_match_the_plan()
    {
        Assert.Equal("timeapi.io", TimeApiIoSource.ProviderName);
        Assert.Equal("api/time/current/zone?timeZone=Europe%2FZurich", TimeApiIoSource.Path);
    }

    [Theory] // the name is the provider's identity, not whatever role/name the options give
    [InlineData("time.now", "timeapi.io")]
    [InlineData("timeapi.io", "time.now")]
    public async Task Name_is_always_timeapi_io_whatever_role_it_plays(string primaryName, string secondaryName)
    {
        var source = CreateSource(StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(Winter)), 3000, primaryName, secondaryName);

        Assert.Equal("timeapi.io", source.Name);
        var reading = AssertReading(await source.FetchAsync(CancellationToken.None));
        Assert.Equal("timeapi.io", reading.Source);
    }

    [Fact]
    public async Task Sends_GET_to_the_Zurich_endpoint_under_the_base_url()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(Winter));

        await FetchAsync(handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("timeapi.io", request.RequestUri!.Host);
        Assert.Equal("/api/time/current/zone", request.RequestUri.AbsolutePath);
        Assert.Equal("?timeZone=Europe/Zurich", Uri.UnescapeDataString(request.RequestUri.Query));
    }

    // ---------- Happy paths (matrix T1, T2, real response) ----------

    [Fact]
    public async Task Accepts_winter_response_T1()
    {
        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(Winter))));

        Assert.Equal(Utc("2026-01-12T07:00:00Z"), reading.Instant); // local 08:00, dstActive=false → +01:00
        Assert.Equal(Offset.FromHours(1), reading.ZurichOffset);
        Assert.Equal("timeapi.io", reading.Source);
    }

    [Fact]
    public async Task Accepts_summer_response_T2()
    {
        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(Summer))));

        Assert.Equal(Utc("2026-07-06T06:00:00Z"), reading.Instant); // local 08:00, dstActive=true → +02:00
        Assert.Equal(Offset.FromHours(2), reading.ZurichOffset);
    }

    [Fact]
    public async Task Accepts_real_recorded_response_with_seven_fraction_digits()
    {
        // Real body: dateTime ...43.8658902 with milliSeconds 865, i.e. the provider TRUNCATES to ms (865, not 866).
        var body = Fixtures.Read(Real);

        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));

        Assert.Equal(Utc("2026-11-17T13:18:43.8658902Z"), reading.Instant);
        Assert.Equal(Offset.FromHours(1), reading.ZurichOffset);
        Assert.Equal(body, reading.RawPayload); // nothing personal in it, so kept as-is
    }

    [Theory] // 0 to 7 fraction digits; milliSeconds = the truncated millisecond part, as the real provider sends it
    [InlineData("2026-01-12T08:00:00", 0, "2026-01-12T07:00:00Z")]
    [InlineData("2026-01-12T08:00:00.1", 100, "2026-01-12T07:00:00.1Z")]
    [InlineData("2026-01-12T08:00:00.123", 123, "2026-01-12T07:00:00.123Z")]
    [InlineData("2026-01-12T08:00:00.1239999", 123, "2026-01-12T07:00:00.1239999Z")]
    [InlineData("2026-01-12T08:00:00.9999999", 999, "2026-01-12T07:00:00.9999999Z")]
    public async Task Accepts_fraction_digits_up_to_seven(string dateTime, int milliSeconds, string expectedUtc)
    {
        var body = LocalBody(dateTime, dstActive: false);
        Assert.Contains($"\"milliSeconds\":{milliSeconds},", body); // the helper derives the same value

        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));

        Assert.Equal(Utc(expectedUtc), reading.Instant);
    }

    // ---------- E11: the repeated October hour is resolved by dstActive (T6) ----------

    [Theory]
    [InlineData(true, "2026-10-25T00:30:00Z", 2)]  // first 02:30, still summer time
    [InlineData(false, "2026-10-25T01:30:00Z", 1)] // second 02:30, after the fall-back
    public async Task Resolves_repeated_hour_with_dst_flag_E11(bool dstActive, string expectedUtc, int offsetHours)
    {
        var body = Fixtures.Mutate(RepeatedHour, o => o["dstActive"] = dstActive);

        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));

        Assert.Equal(Utc(expectedUtc), reading.Instant);
        Assert.Equal(Offset.FromHours(offsetHours), reading.ZurichOffset);
    }

    [Fact]
    public async Task Repeated_hour_readings_are_distinct_instants_one_hour_apart_E11()
    {
        var first = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK,
            Fixtures.Mutate(RepeatedHour, o => o["dstActive"] = true))));
        var second = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK,
            Fixtures.Mutate(RepeatedHour, o => o["dstActive"] = false))));

        Assert.Equal(Duration.FromHours(1), second.Instant - first.Instant);
    }

    // ---------- E13: the March gap does not exist ----------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rejects_nonexistent_local_time_E13(bool dstActive)
    {
        var body = Fixtures.Mutate(DstGap, o => o["dstActive"] = dstActive); // 2026-03-29T02:30, never happens

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));
    }

    // ---------- Boundaries around both 2026 transitions ----------

    [Theory]
    // DST start 2026-03-29 01:00Z: local 02:00–02:59 does not exist.
    [InlineData("2026-03-29T01:59:59.9999999", false, "2026-03-29T00:59:59.9999999Z")]
    [InlineData("2026-03-29T01:59:59", true, null)]   // still winter time: +02 is not a valid offset then
    [InlineData("2026-03-29T02:00:00", false, null)]  // first nonexistent instant
    [InlineData("2026-03-29T02:00:00", true, null)]
    [InlineData("2026-03-29T02:59:59.9999999", true, null)]
    [InlineData("2026-03-29T03:00:00", true, "2026-03-29T01:00:00Z")] // T7: the transition instant is 03:00 +02
    [InlineData("2026-03-29T03:00:00", false, null)]  // 03:00 +01 would be 02:00Z, but 03:00 only exists at +02
    // DST end 2026-10-25 01:00Z: local 02:00–02:59 happens twice.
    [InlineData("2026-10-25T01:59:59", true, "2026-10-24T23:59:59Z")]
    [InlineData("2026-10-25T01:59:59", false, null)]  // before the repeated hour only +02 is valid
    [InlineData("2026-10-25T02:00:00", true, "2026-10-25T00:00:00Z")]
    [InlineData("2026-10-25T02:00:00", false, "2026-10-25T01:00:00Z")]
    [InlineData("2026-10-25T02:59:59", false, "2026-10-25T01:59:59Z")]
    [InlineData("2026-10-25T03:00:00", false, "2026-10-25T02:00:00Z")]
    [InlineData("2026-10-25T03:00:00", true, null)]   // after the repeated hour only +01 is valid
    public async Task Applies_tzdb_at_the_dst_boundaries(string dateTime, bool dstActive, string? expectedUtc)
    {
        var result = await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, LocalBody(dateTime, dstActive)));

        if (expectedUtc is null)
        {
            AssertInvalid(result);
        }
        else
        {
            var reading = AssertReading(result);
            Assert.Equal(Utc(expectedUtc), reading.Instant);
            Assert.Equal(Offset.FromHours(dstActive ? 2 : 1), reading.ZurichOffset);
        }
    }

    // ---------- E10: dstActive must agree with tzdb ----------

    [Fact]
    public async Task Rejects_dst_flag_true_in_January_E10()
    {
        var body = Fixtures.Mutate(Winter, o => o["dstActive"] = true);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));
    }

    [Fact]
    public async Task Rejects_dst_flag_false_in_July_E10()
    {
        var body = Fixtures.Mutate(Summer, o => o["dstActive"] = false);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));
    }

    // ---------- E8: wrong zone ----------

    [Theory]
    [InlineData("Europe/London")]
    [InlineData("Europe/Berlin")] // same offsets as Zurich, still not what we asked for
    [InlineData("europe/zurich")] // exact match only
    [InlineData("UTC")]
    [InlineData("")]
    public async Task Rejects_wrong_zone_E8(string zone)
    {
        var body = Fixtures.Mutate(Winter, o => o["timeZone"] = zone);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "timeZone");
    }

    // ---------- E7: missing or wrongly typed fields ----------

    [Theory]
    [InlineData("timeZone")]
    [InlineData("dateTime")]
    [InlineData("dstActive")]
    [InlineData("year")]
    [InlineData("month")]
    [InlineData("day")]
    [InlineData("hour")]
    [InlineData("minute")]
    [InlineData("seconds")]
    [InlineData("milliSeconds")]
    public async Task Rejects_missing_field_E7(string field)
    {
        var body = Fixtures.Mutate(Winter, o => o.Remove(field));

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), field);
    }

    [Theory]
    [InlineData("dstActive", "\"false\"")] // a string, not a bool
    [InlineData("dstActive", "0")]
    [InlineData("dstActive", "null")]
    [InlineData("year", "\"2026\"")]
    [InlineData("hour", "8.5")]
    [InlineData("milliSeconds", "null")]
    [InlineData("dateTime", "1768201200")]
    [InlineData("timeZone", "null")]
    public async Task Rejects_field_with_wrong_type_E7(string field, string json)
    {
        var body = Fixtures.Mutate(Winter, o => o[field] = JsonNode.Parse(json));

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), field);
    }

    [Fact]
    public async Task Field_names_are_case_sensitive()
    {
        var body = Fixtures.Mutate(Winter, o =>
        {
            o.Remove("dstActive");
            o["DstActive"] = false;
        });

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "dstActive");
    }

    // ---------- dateTime must be the documented offset-less ISO shape ----------

    [Theory]
    [InlineData("2026-01-12T08:00:00+01:00")]          // an offset: not the documented shape, we don't pick one of two offsets
    [InlineData("2026-01-12T08:00:00.0000000+01:00")]
    [InlineData("2026-01-12T08:00:00Z")]               // the local time labelled as UTC
    [InlineData("2026-01-12T07:00:00Z")]               // the right instant, but as UTC: still not the shape we parse
    [InlineData("2026-01-12T08:00:00.00000000")]       // 8 fraction digits
    [InlineData("01/12/2026 08:00:00")]                // culture-specific
    [InlineData("12.01.2026 08:00:00")]
    [InlineData("2026-01-12 08:00:00")]                // space instead of T
    [InlineData("2026-01-12T08:00")]                   // no seconds (the provider always sends them)
    [InlineData("2026-1-12T08:00:00")]
    [InlineData("")]
    public async Task Rejects_dateTime_that_is_not_offset_less_iso(string dateTime)
    {
        var body = Fixtures.Mutate(Winter, o => o["dateTime"] = dateTime);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "dateTime");
    }

    // ---------- Redundant fields must agree with dateTime ----------

    [Theory]
    [InlineData("year", 2027)]
    [InlineData("month", 2)]
    [InlineData("day", 13)]
    [InlineData("hour", 9)]
    [InlineData("hour", 7)]          // the UTC hour: a provider mixing local and UTC
    [InlineData("minute", 1)]
    [InlineData("seconds", 1)]
    [InlineData("milliSeconds", 1)]
    public async Task Rejects_redundant_field_that_disagrees_with_dateTime(string field, int value)
    {
        var body = Fixtures.Mutate(Winter, o => o[field] = value);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), field);
    }

    [Theory] // the real provider truncates (865 for .8658902); a rounded value means the fields don't describe the same time
    [InlineData(866)]
    [InlineData(864)]
    public async Task Rejects_milliSeconds_that_is_not_the_truncated_fraction(int milliSeconds)
    {
        var body = Fixtures.Mutate(Real, o => o["milliSeconds"] = milliSeconds);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "milliSeconds");
    }

    // ---------- E14: implausible year (UTC year of the derived instant, fixed range) ----------

    [Theory]
    [InlineData("1970-01-01T01:00:00", "year")]
    [InlineData("2150-01-12T08:00:00", "year")]
    [InlineData("2026-01-01T00:30:00", "year")] // Zurich 2026, but UTC 2025-12-31T23:30Z: outside [2026, 2100)
    [InlineData("0001-01-01T00:30:00", null)]   // must be a clean rejection, not an overflow exception (DateTime arithmetic would throw)
    public async Task Rejects_implausible_year_E14(string dateTime, string? detailContains)
    {
        var result = await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, LocalBody(dateTime, dstActive: false)));

        AssertInvalid(result, detailContains);
    }

    // ---------- E6: bodies that are not a time response ----------

    [Fact]
    public async Task Rejects_html_error_page_with_status_200_E6()
    {
        var html = Fixtures.Read("TimeNow/html-error-page.html");

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, html, "text/html")), "JSON");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"Invalid Timezone\"")] // the real 400 body, but with status 200
    [InlineData("{")]
    public async Task Rejects_empty_or_non_object_body_E6(string body)
    {
        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "JSON");
    }

    // ---------- Inherited HTTP handling (sanity checks) ----------

    [Fact]
    public async Task Status_400_with_real_unknown_zone_body_is_http_error()
    {
        var failure = AssertFailure(
            await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.BadRequest, Fixtures.Read("TimeApiIo/real-400-unknown-zone.txt"))),
            TimeSourceFailureReason.HttpError);

        Assert.Equal(400, failure.HttpStatus);
        Assert.Equal("http_error", failure.OutcomeCode);
    }

    [Fact]
    public async Task Status_429_is_rate_limited()
    {
        var failure = AssertFailure(
            await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.TooManyRequests, "")),
            TimeSourceFailureReason.RateLimited);

        Assert.Equal(429, failure.HttpStatus);
    }

    [Fact]
    public async Task Slow_provider_is_reported_as_timeout_not_thrown()
    {
        var failure = AssertFailure(await FetchAsync(StubHttpMessageHandler.Hanging(), timeoutMs: 500), TimeSourceFailureReason.Timeout);

        Assert.Equal("timeout", failure.OutcomeCode);
    }

    [Fact]
    public async Task Propagates_caller_cancellation_E17()
    {
        using var caller = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new StubHttpMessageHandler(async (_, ct) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("unreachable");
        });

        var fetch = FetchAsync(handler, timeoutMs: 10_000, ct: caller.Token);
        await entered.Task;
        caller.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fetch);
    }

    [Fact]
    public async Task Measures_round_trip_with_the_elapsed_timer()
    {
        _timer.Advance(TimeSpan.FromSeconds(10));
        var startedAt = _timer.GetTimestamp();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            _timer.Advance(TimeSpan.FromMilliseconds(402));
            return Task.FromResult(StubHttpMessageHandler.Response(HttpStatusCode.OK, Fixtures.Read(Winter)));
        });

        var reading = AssertReading(await FetchAsync(handler));

        Assert.Equal(402, reading.RoundTripMs);
        Assert.Equal(startedAt, reading.StartedTimestamp);
    }

    [Fact]
    public async Task Truncates_raw_payload_to_1_KB()
    {
        var body = Fixtures.Mutate(Winter, o => o["padding"] = new string('x', 3000));

        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));

        Assert.Equal(PayloadSanitizer.MaxLength, reading.RawPayload.Length);
    }

    // ---------- Helpers ----------

    private Task<TimeSourceResult> FetchAsync(StubHttpMessageHandler handler, int timeoutMs = 3000, CancellationToken ct = default) =>
        CreateSource(handler, timeoutMs).FetchAsync(ct);

    private TimeApiIoSource CreateSource(StubHttpMessageHandler handler, int timeoutMs, string primaryName = "time.now", string secondaryName = "timeapi.io")
    {
        var options = Options.Create(new TrustedClockOptions
        {
            TimeoutMs = timeoutMs,
            LowPrecisionThresholdMs = 100,
            Primary = new TimeProviderEndpoint { Name = primaryName, BaseUrl = primaryName == "timeapi.io" ? "https://timeapi.io/" : "https://time.now/" },
            Secondary = new TimeProviderEndpoint { Name = secondaryName, BaseUrl = secondaryName == "timeapi.io" ? "https://timeapi.io/" : "https://time.now/" },
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://timeapi.io/") };
        return new TimeApiIoSource(http, _timer, options, ZurichTime.FromTzdb());
    }

    /// <summary>
    /// A body in the real timeapi.io shape for the local time <paramref name="dateTime"/> (sent verbatim), with every
    /// redundant field consistent with it (milliSeconds truncated, as the real provider does).
    /// </summary>
    private static string LocalBody(string dateTime, bool dstActive)
    {
        var local = LocalDateTimePattern.ExtendedIso.Parse(dateTime).GetValueOrThrow();
        return Fixtures.Mutate(Winter, o =>
        {
            o["year"] = local.Year;
            o["month"] = local.Month;
            o["day"] = local.Day;
            o["hour"] = local.Hour;
            o["minute"] = local.Minute;
            o["seconds"] = local.Second;
            o["milliSeconds"] = local.Millisecond;
            o["dateTime"] = dateTime;
            o["date"] = local.ToString("MM'/'dd'/'uuuu", CultureInfo.InvariantCulture);
            o["time"] = local.ToString("HH':'mm", CultureInfo.InvariantCulture);
            o["dayOfWeek"] = local.DayOfWeek.ToString();
            o["dstActive"] = dstActive;
        });
    }

    private static TimeSourceReading AssertReading(TimeSourceResult result) =>
        result as TimeSourceReading
        ?? throw new Xunit.Sdk.XunitException($"Expected a reading, got {result}");

    private static TimeSourceFailure AssertFailure(TimeSourceResult result, TimeSourceFailureReason reason)
    {
        var failure = result as TimeSourceFailure
            ?? throw new Xunit.Sdk.XunitException($"Expected a {reason} failure, got {result}");
        Assert.Equal(reason, failure.Reason);
        Assert.Equal("timeapi.io", failure.Source);
        return failure;
    }

    private static void AssertInvalid(TimeSourceResult result, string? detailContains = null)
    {
        var failure = AssertFailure(result, TimeSourceFailureReason.InvalidResponse);
        Assert.Equal("invalid_response", failure.OutcomeCode);
        if (detailContains is not null)
        {
            Assert.Contains(detailContains, failure.Detail, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static Instant Utc(string iso) => InstantPattern.ExtendedIso.Parse(iso).GetValueOrThrow();
}
