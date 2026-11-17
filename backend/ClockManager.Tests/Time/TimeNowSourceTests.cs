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
/// time.now client and validation (plans/01-trusted-clock.md §4.3, edge cases E7–E9, E12, E14, E17, E18).
/// Every response comes from a stub handler; nothing touches the network.
/// </summary>
public sealed class TimeNowSourceTests
{
    private const string Winter = "TimeNow/t1-winter.json";
    private const string Summer = "TimeNow/t2-summer.json";
    private const string DstStart = "TimeNow/t7-dst-start.json";

    private readonly FakeElapsedTimer _timer = new();

    // ---------- Happy paths (matrix T1, T2, T7) ----------

    [Fact]
    public async Task Accepts_winter_response_T1()
    {
        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(Winter))));

        Assert.Equal(Utc("2026-01-12T07:00:00Z"), reading.Instant);
        Assert.Equal(Offset.FromHours(1), reading.ZurichOffset);
        Assert.Equal("time.now", reading.Source);
    }

    [Fact]
    public async Task Accepts_summer_response_T2()
    {
        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(Summer))));

        Assert.Equal(Utc("2026-07-06T06:00:00Z"), reading.Instant);
        Assert.Equal(Offset.FromHours(2), reading.ZurichOffset);
    }

    [Fact]
    public async Task Accepts_real_recorded_response_with_microseconds()
    {
        var reading = AssertReading(await FetchAsync(
            StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read("TimeNow/real-2026-11-17.json"))));

        Assert.Equal(Utc("2026-11-17T12:13:50.156685Z"), reading.Instant);
        Assert.Equal(Offset.FromHours(1), reading.ZurichOffset);
    }

    [Fact]
    public async Task Dst_start_instant_maps_to_0300_E12()
    {
        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(DstStart))));

        Assert.Equal(Utc("2026-03-29T01:00:00Z"), reading.Instant);
        Assert.Equal(Offset.FromHours(2), reading.ZurichOffset);
        var local = ZurichTime.FromTzdb().ToLocal(reading.Instant);
        Assert.Equal(new LocalDateTime(2026, 3, 29, 3, 0), local.LocalDateTime);
    }

    [Fact]
    public async Task Rejects_dst_start_instant_claimed_as_0200_plus_one_E12()
    {
        var body = Fixtures.Mutate(DstStart, o =>
        {
            o["utc_offset"] = "+01:00";
            o["datetime"] = "2026-03-29T02:00:00.000000+01:00";
        });

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "utc_offset");
    }

    [Theory] // T6: the repeated October hour is unambiguous because time.now sends the instant and the offset
    [InlineData("2026-10-25T00:30:00Z", 1792888200, "+02:00", "2026-10-25T02:30:00+02:00")]
    [InlineData("2026-10-25T01:15:00Z", 1792890900, "+01:00", "2026-10-25T02:15:00+01:00")]
    public async Task Accepts_both_sides_of_the_repeated_hour_T6(string utc, long unix, string offset, string local)
    {
        var body = Fixtures.Mutate(Winter, o =>
        {
            o["utc_datetime"] = utc;
            o["unixtime"] = unix;
            o["utc_offset"] = offset;
            o["datetime"] = local;
        });

        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));

        Assert.Equal(Utc(utc), reading.Instant);
        Assert.Equal(OffsetPattern.GeneralInvariant.Parse(offset).Value, reading.ZurichOffset);
    }

    [Fact]
    public async Task Sends_GET_to_the_Zurich_endpoint_under_the_base_url()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(Winter));

        await FetchAsync(handler);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://time.now/developer/api/timezone/Europe/Zurich", request.RequestUri!.ToString());
    }

    // ---------- Round trip, measured with the injected monotonic timer ----------

    [Fact]
    public async Task Measures_round_trip_with_the_elapsed_timer()
    {
        _timer.Advance(TimeSpan.FromSeconds(10)); // arbitrary non-zero start
        var startedAt = _timer.GetTimestamp();
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            _timer.Advance(TimeSpan.FromMilliseconds(341));
            return Task.FromResult(StubHttpMessageHandler.Response(HttpStatusCode.OK, Fixtures.Read(Winter)));
        });

        var reading = AssertReading(await FetchAsync(handler));

        Assert.Equal(341, reading.RoundTripMs);
        Assert.Equal(startedAt, reading.StartedTimestamp);
    }

    [Fact]
    public async Task Failure_also_reports_round_trip()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
        {
            _timer.Advance(TimeSpan.FromMilliseconds(1100));
            return Task.FromResult(StubHttpMessageHandler.Response(HttpStatusCode.InternalServerError, "oops", "text/plain"));
        });

        var failure = AssertFailure(await FetchAsync(handler), TimeSourceFailureReason.HttpError);

        Assert.Equal(1100, failure.RoundTripMs);
    }

    // ---------- E7: missing or wrongly typed fields ----------

    [Theory]
    [InlineData("timezone")]
    [InlineData("utc_datetime")]
    [InlineData("unixtime")]
    [InlineData("utc_offset")]
    [InlineData("datetime")]
    public async Task Rejects_missing_field_E7(string field)
    {
        var body = Fixtures.Mutate(Winter, o => o.Remove(field));

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), field);
    }

    [Theory]
    [InlineData("unixtime", "\"1768201200\"")]
    [InlineData("utc_datetime", "1768201200")]
    [InlineData("timezone", "null")]
    [InlineData("utc_offset", "3600")]
    [InlineData("unixtime", "1768201200.5")]
    public async Task Rejects_field_with_wrong_type_E7(string field, string json)
    {
        var body = Fixtures.Mutate(Winter, o => o[field] = JsonNode.Parse(json));

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), field);
    }

    // ---------- E8: wrong zone ----------

    [Theory]
    [InlineData("Europe/London")]
    [InlineData("Europe/Berlin")]   // same offsets as Zurich, still not what we asked for
    [InlineData("europe/zurich")]   // exact match only
    [InlineData("")]
    public async Task Rejects_wrong_zone_E8(string zone)
    {
        var body = Fixtures.Mutate(Winter, o => o["timezone"] = zone);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "timezone");
    }

    // ---------- E9: offset must match tzdb at that instant ----------

    [Fact]
    public async Task Rejects_offset_not_matching_tzdb_E9()
    {
        // A self-consistent body (same instant at +02:00), but Zurich is +01:00 in January.
        var body = Fixtures.Mutate(Winter, o =>
        {
            o["utc_offset"] = "+02:00";
            o["datetime"] = "2026-01-12T09:00:00.000000+02:00";
        });

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "utc_offset");
    }

    [Theory]
    [InlineData("01:00")]
    [InlineData("+0100")]
    [InlineData("+01")]
    [InlineData("UTC+1")]
    public async Task Rejects_offset_in_another_format_E9(string offset)
    {
        var body = Fixtures.Mutate(Winter, o => o["utc_offset"] = offset);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "utc_offset");
    }

    // ---------- The instant itself must be strict UTC ----------

    [Theory]
    [InlineData("2026-01-12T07:00:00")]          // no offset: never assume one
    [InlineData("2026-01-12T08:00:00+01:00")]    // an offset instead of Z
    [InlineData("12/01/2026 07:00:00")]          // culture-specific
    [InlineData("2026-01-12 07:00:00Z")]
    [InlineData("")]
    public async Task Rejects_utc_datetime_that_is_not_strict_iso_utc(string value)
    {
        var body = Fixtures.Mutate(Winter, o => o["utc_datetime"] = value);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "utc_datetime");
    }

    // ---------- Redundant fields must agree ----------

    [Theory]
    [InlineData(1768201200 + 2)]
    [InlineData(1768201200 - 2)]
    [InlineData(1768201200 + 3600)] // the local time read as UTC, a classic bug
    [InlineData(0)]
    public async Task Rejects_unixtime_mismatch(long unix)
    {
        var body = Fixtures.Mutate(Winter, o => o["unixtime"] = unix);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "unixtime");
    }

    [Theory]
    [InlineData(1768201200 + 1)]
    [InlineData(1768201200 - 1)]
    public async Task Accepts_unixtime_within_one_second(long unix)
    {
        var body = Fixtures.Mutate(Winter, o => o["unixtime"] = unix);

        AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));
    }

    [Theory]
    [InlineData("2026-01-12T08:00:01.000000+01:00")] // one second off
    [InlineData("2026-01-12T09:00:00.000000+02:00")] // same instant, wrong offset for January
    [InlineData("2026-01-12T07:00:00.000000Z")]      // same instant, but UTC instead of Zurich
    [InlineData("2026-01-12T08:00:00.000000")]       // no offset
    [InlineData("2026-01-12T08:00:00.000000+00:00")] // the local time labelled as UTC
    public async Task Rejects_datetime_not_equal_to_instant_plus_offset(string datetime)
    {
        var body = Fixtures.Mutate(Winter, o => o["datetime"] = datetime);

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "datetime");
    }

    // ---------- E14: implausible year (fixed range, not compared with the server clock) ----------

    [Theory]
    [InlineData("1970-01-01T00:00:00Z", 0L, "1970-01-01T01:00:00+01:00")]
    [InlineData("2150-01-12T07:00:00Z", 5681257200L, "2150-01-12T08:00:00+01:00")]
    [InlineData("2025-12-31T22:00:00Z", 1767218400L, "2025-12-31T23:00:00+01:00")]
    public async Task Rejects_implausible_year_E14(string utc, long unix, string local)
    {
        var body = Fixtures.Mutate(Winter, o =>
        {
            o["utc_datetime"] = utc;
            o["unixtime"] = unix;
            o["datetime"] = local;
        });

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "year");
    }

    // ---------- Bodies that are not a time response ----------

    [Fact]
    public async Task Rejects_html_error_page_with_status_200()
    {
        var html = Fixtures.Read("TimeNow/html-error-page.html");

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, html, "text/html")), "not JSON");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("\"2026-01-12T07:00:00Z\"")]
    [InlineData("{")]
    public async Task Rejects_empty_or_non_object_body(string body)
    {
        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)), "JSON");
    }

    [Fact]
    public async Task Rejects_body_that_is_not_utf8()
    {
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent([0x7B, 0xFF, 0xFE, 0x7D]) }));

        AssertInvalid(await FetchAsync(handler), "UTF-8");
    }

    [Fact]
    public async Task Rejects_oversized_body()
    {
        var huge = "{\"padding\":\"" + new string('x', HttpTimeSource.MaxBodyBytes) + "\"}";

        AssertInvalid(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, huge)), "larger than");
    }

    // ---------- HTTP status → typed reason ----------

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.MovedPermanently)]
    public async Task Non_200_status_is_http_error(HttpStatusCode status)
    {
        var body = status == HttpStatusCode.NotFound ? Fixtures.Read("TimeNow/real-404-unknown-zone.json") : Fixtures.Read(Winter);

        var failure = AssertFailure(await FetchAsync(StubHttpMessageHandler.Returning(status, body)), TimeSourceFailureReason.HttpError);

        Assert.Equal((int)status, failure.HttpStatus);
        Assert.Equal("http_error", failure.OutcomeCode);
    }

    [Fact]
    public async Task Status_429_is_rate_limited()
    {
        var failure = AssertFailure(
            await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.TooManyRequests, "{}")),
            TimeSourceFailureReason.RateLimited);

        Assert.Equal(429, failure.HttpStatus);
        Assert.Equal("rate_limited", failure.OutcomeCode);
    }

    [Fact]
    public async Task Connection_failure_is_network_error()
    {
        var handler = new StubHttpMessageHandler((_, _) =>
            throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known."));

        var failure = AssertFailure(await FetchAsync(handler), TimeSourceFailureReason.NetworkError);

        Assert.Equal("network_error", failure.OutcomeCode);
    }

    // ---------- Timeout vs. caller cancellation (E17) ----------

    [Fact]
    public async Task Slow_provider_is_reported_as_timeout_not_thrown()
    {
        var failure = AssertFailure(
            await FetchAsync(StubHttpMessageHandler.Hanging(), timeoutMs: 500),
            TimeSourceFailureReason.Timeout);

        Assert.Equal("timeout", failure.OutcomeCode);
    }

    [Fact]
    public async Task Body_that_never_finishes_is_a_timeout()
    {
        // Headers arrive at once, then the body stalls: the timeout must cover reading the body too.
        var handler = new StubHttpMessageHandler((_, _) => Task.FromResult(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream()) }));

        AssertFailure(await FetchAsync(handler, timeoutMs: 500), TimeSourceFailureReason.Timeout);
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
    public async Task Already_cancelled_caller_sends_no_request_E17()
    {
        var handler = StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read(Winter));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => FetchAsync(handler, ct: new CancellationToken(canceled: true)));
        Assert.Empty(handler.Requests);
    }

    // ---------- E18: sanitized raw payload ----------

    [Fact]
    public async Task Sanitizes_client_ip_E18()
    {
        var reading = AssertReading(await FetchAsync(
            StubHttpMessageHandler.Returning(HttpStatusCode.OK, Fixtures.Read("TimeNow/real-2026-11-17.json"))));

        Assert.DoesNotContain("client_ip", reading.RawPayload);
        Assert.DoesNotContain("203.0.113.7", reading.RawPayload);
        Assert.Contains("\"utc_datetime\":\"2026-11-17T12:13:50.156685Z\"", reading.RawPayload);
        Assert.Contains("\"utc_offset\":\"+01:00\"", reading.RawPayload); // readable, not +-escaped
    }

    [Fact]
    public async Task Truncates_raw_payload_to_1_KB()
    {
        var body = Fixtures.Mutate(Winter, o => o["padding"] = new string('x', 3000));

        var reading = AssertReading(await FetchAsync(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)));

        Assert.Equal(PayloadSanitizer.MaxLength, reading.RawPayload.Length);
        Assert.DoesNotContain("client_ip", reading.RawPayload);
    }

    // ---------- Helpers ----------

    private async Task<TimeSourceResult> FetchAsync(StubHttpMessageHandler handler, int timeoutMs = 3000, CancellationToken ct = default)
    {
        var options = Options.Create(new TrustedClockOptions
        {
            TimeoutMs = timeoutMs,
            LowPrecisionThresholdMs = 100,
            Primary = new TimeProviderEndpoint { Name = "time.now", BaseUrl = "https://time.now/" },
            Secondary = new TimeProviderEndpoint { Name = "timeapi.io", BaseUrl = "https://timeapi.io/" },
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.Value.Primary!.BaseUrl) };
        var source = new TimeNowSource(http, _timer, options, ZurichTime.FromTzdb());
        return await source.FetchAsync(ct);
    }

    private static TimeSourceReading AssertReading(TimeSourceResult result) =>
        result as TimeSourceReading
        ?? throw new Xunit.Sdk.XunitException($"Expected a reading, got {result}");

    private static TimeSourceFailure AssertFailure(TimeSourceResult result, TimeSourceFailureReason reason)
    {
        var failure = Assert.IsType<TimeSourceFailure>(result);
        Assert.Equal(reason, failure.Reason);
        Assert.Equal("time.now", failure.Source);
        return failure;
    }

    private static void AssertInvalid(TimeSourceResult result, string detailContains)
    {
        var failure = AssertFailure(result, TimeSourceFailureReason.InvalidResponse);
        Assert.Equal("invalid_response", failure.OutcomeCode);
        Assert.Contains(detailContains, failure.Detail, StringComparison.OrdinalIgnoreCase);
    }

    private static Instant Utc(string iso) => InstantPattern.ExtendedIso.Parse(iso).GetValueOrThrow();

    /// <summary>A response stream that never yields data until cancelled.</summary>
    private sealed class StallingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException("sync read");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
    }
}
