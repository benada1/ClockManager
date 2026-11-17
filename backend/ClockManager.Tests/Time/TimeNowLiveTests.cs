using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace ClockManager.Tests.Time;

/// <summary>
/// Opt-in check against the real time.now API (skipped unless CLOCKMANAGER_LIVE_TESTS=1). Shows that today's
/// real response still passes our strict validation, i.e. the provider has not changed its format.
/// </summary>
[Trait("Category", "Live")]
public sealed class TimeNowLiveTests(ITestOutputHelper output)
{
    [LiveFact]
    public async Task Real_time_now_response_passes_validation()
    {
        var options = Options.Create(new TrustedClockOptions
        {
            Primary = new TimeProviderEndpoint { Name = "time.now", BaseUrl = "https://time.now/" },
            Secondary = new TimeProviderEndpoint { Name = "timeapi.io", BaseUrl = "https://timeapi.io/" },
        });
        using var http = new HttpClient { BaseAddress = new Uri(options.Value.Primary!.BaseUrl) };
        var source = new TimeNowSource(http, new StopwatchElapsedTimer(), options, ZurichTime.FromTzdb());

        var result = await source.FetchAsync(CancellationToken.None);

        output.WriteLine(result.ToString());
        var reading = Assert.IsType<TimeSourceReading>(result);
        Assert.DoesNotContain("client_ip", reading.RawPayload);
    }
}
