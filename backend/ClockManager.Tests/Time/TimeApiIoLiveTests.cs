using ClockManager.Api.Application.Time;
using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit.Abstractions;

namespace ClockManager.Tests.Time;

/// <summary>
/// Opt-in checks against the real timeapi.io API and both real providers together (skipped unless
/// CLOCKMANAGER_LIVE_TESTS=1). They show that today's real responses still pass our strict validation and that the two
/// providers agree within the tolerance through the real orchestrator.
/// </summary>
[Trait("Category", "Live")]
public sealed class TimeApiIoLiveTests(ITestOutputHelper output)
{
    private static readonly IOptions<TrustedClockOptions> Defaults = Options.Create(new TrustedClockOptions
    {
        Primary = new TimeProviderEndpoint { Name = "time.now", BaseUrl = "https://time.now/" },
        Secondary = new TimeProviderEndpoint { Name = "timeapi.io", BaseUrl = "https://timeapi.io/" },
    });

    [LiveFact]
    public async Task Real_timeapi_io_response_passes_validation()
    {
        using var http = new HttpClient { BaseAddress = new Uri("https://timeapi.io/") };
        var source = new TimeApiIoSource(http, new StopwatchElapsedTimer(), Defaults, ZurichTime.FromTzdb());

        var result = await source.FetchAsync(CancellationToken.None);

        output.WriteLine(result.ToString());
        Assert.IsType<TimeSourceReading>(result);
    }

    [LiveFact]
    public async Task Real_providers_agree_through_the_orchestrator()
    {
        var timer = new StopwatchElapsedTimer();
        var zurich = ZurichTime.FromTzdb();
        using var timeNowHttp = new HttpClient { BaseAddress = new Uri("https://time.now/") };
        using var timeApiHttp = new HttpClient { BaseAddress = new Uri("https://timeapi.io/") };
        var clock = new TrustedClock(
            new TimeNowSource(timeNowHttp, timer, Defaults, zurich),
            new TimeApiIoSource(timeApiHttp, timer, Defaults, zurich),
            Defaults, timer, NullLogger<TrustedClock>.Instance);

        var result = await clock.GetNowAsync(CancellationToken.None);

        output.WriteLine(result.ToString());
        Assert.Equal("time.now", result.Provider);
        Assert.Equal("timeapi.io", result.CrossCheckedBy);
    }
}
