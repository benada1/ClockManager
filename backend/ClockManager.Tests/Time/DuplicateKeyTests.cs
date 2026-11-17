using System.Net;
using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Time;

/// <summary>
/// QA-07: a body that repeats a key has no single meaning (System.Text.Json keeps one of the values silently), so both
/// sources reject it as <c>invalid_response</c>, whichever value comes first and even when both values are equal.
/// Bodies are built as raw strings because JsonObject can't hold a duplicate key.
/// </summary>
public sealed class DuplicateKeyTests
{
    private static readonly IOptions<TrustedClockOptions> Options = Microsoft.Extensions.Options.Options.Create(new TrustedClockOptions
    {
        Primary = new TimeProviderEndpoint { Name = "time.now", BaseUrl = "https://time.now/" },
        Secondary = new TimeProviderEndpoint { Name = "timeapi.io", BaseUrl = "https://timeapi.io/" },
    });

    [Theory]
    [InlineData("\"timeZone\":\"UTC\"")]                       // a second zone
    [InlineData("\"timeZone\":\"Europe/Zurich\"")]             // same value twice: still rejected
    [InlineData("\"dstActive\":true")]                         // the flag that picks the offset
    [InlineData("\"dateTime\":\"2026-01-12T09:00:00\"")]       // another local time
    [InlineData("\"date\":\"01/12/2026\"")]                    // a field we don't read: any duplicate is rejected
    public async Task Timeapi_io_rejects_a_duplicate_key_QA07(string extraMember)
    {
        var body = WithMember(Fixtures.Read("TimeApiIo/t1-winter.json"), extraMember);
        Assert.Equal(TimeSourceFailureReason.InvalidResponse,
            TimeSourceFailure(await TimeApiIo(body).FetchAsync(CancellationToken.None)).Reason);
    }

    [Theory]
    [InlineData("\"timezone\":\"UTC\"")]
    [InlineData("\"utc_datetime\":\"2026-01-12T08:00:00.000000Z\"")]
    [InlineData("\"utc_offset\":\"+01:00\"")]                  // same value twice
    [InlineData("\"unixtime\":1768204800")]
    [InlineData("\"client_ip\":\"203.0.113.8\"")]              // would otherwise leave one IP in RawPayload
    public async Task Time_now_rejects_a_duplicate_key_QA07(string extraMember)
    {
        var body = WithMember(Fixtures.Read("TimeNow/t1-winter.json"), extraMember);
        Assert.Equal(TimeSourceFailureReason.InvalidResponse,
            TimeSourceFailure(await TimeNow(body).FetchAsync(CancellationToken.None)).Reason);
    }

    [Fact]
    public async Task Duplicate_detail_names_the_key_for_the_log_QA07()
    {
        var body = WithMember(Fixtures.Read("TimeApiIo/t1-winter.json"), "\"dstActive\":true", first: false);

        var failure = TimeSourceFailure(await TimeApiIo(body).FetchAsync(CancellationToken.None));

        Assert.Contains("duplicate", failure.Detail, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("dstActive", failure.Detail);
    }

    [Fact]
    public async Task Fixtures_without_duplicates_still_pass()
    {
        Assert.IsType<TimeSourceReading>(await TimeApiIo(Fixtures.Read("TimeApiIo/t1-winter.json")).FetchAsync(CancellationToken.None));
        Assert.IsType<TimeSourceReading>(await TimeNow(Fixtures.Read("TimeNow/t1-winter.json")).FetchAsync(CancellationToken.None));
    }

    /// <summary>Adds <paramref name="member"/> as the first (or last) member of the top-level object.</summary>
    private static string WithMember(string json, string member, bool first = true)
    {
        var trimmed = json.Trim();
        return first
            ? "{" + member + "," + trimmed[1..]
            : trimmed[..^1] + "," + member + "}";
    }

    private static TimeApiIoSource TimeApiIo(string body) =>
        new(Http(body, "https://timeapi.io/"), new FakeElapsedTimer(), Options, ZurichTime.FromTzdb());

    private static TimeNowSource TimeNow(string body) =>
        new(Http(body, "https://time.now/"), new FakeElapsedTimer(), Options, ZurichTime.FromTzdb());

    private static HttpClient Http(string body, string baseUrl) =>
        new(StubHttpMessageHandler.Returning(HttpStatusCode.OK, body)) { BaseAddress = new Uri(baseUrl) };

    private static TimeSourceFailure TimeSourceFailure(TimeSourceResult result) =>
        result as TimeSourceFailure ?? throw new Xunit.Sdk.XunitException($"Expected a failure, got {result}");
}
