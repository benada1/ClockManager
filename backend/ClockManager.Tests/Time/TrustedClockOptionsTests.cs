using ClockManager.Api.Infrastructure.Time;
using ClockManager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Time;

/// <summary>The host must refuse to start with a bad <c>TrustedClock</c> section (D-010, plan 01 §4.5).</summary>
public sealed class TrustedClockOptionsTests
{
    [Theory]
    [InlineData("TrustedClock:TimeoutMs", "499", "TimeoutMs")]
    [InlineData("TrustedClock:TimeoutMs", "10001", "TimeoutMs")]
    [InlineData("TrustedClock:CrossCheckToleranceMs", "99", "CrossCheckToleranceMs")]
    [InlineData("TrustedClock:CrossCheckToleranceMs", "30001", "CrossCheckToleranceMs")]
    [InlineData("TrustedClock:LowPrecisionThresholdMs", "99", "LowPrecisionThresholdMs")]
    [InlineData("TrustedClock:LowPrecisionThresholdMs", "3000", "must be less than TimeoutMs")]   // == timeout
    [InlineData("TrustedClock:LowPrecisionThresholdMs", "4000", "must be less than TimeoutMs")]   // > timeout
    [InlineData("TrustedClock:Primary:Name", "", "Primary.Name")]
    [InlineData("TrustedClock:Secondary:Name", "time.now", "must differ")]
    [InlineData("TrustedClock:Primary:BaseUrl", "not a url", "Primary.BaseUrl")]
    [InlineData("TrustedClock:Primary:BaseUrl", "ftp://time.now/", "Primary.BaseUrl")]
    [InlineData("TrustedClock:Primary:BaseUrl", "https://time.now", "ending in '/'")]
    [InlineData("TrustedClock:Secondary:BaseUrl", "/relative/", "Secondary.BaseUrl")]
    public void Host_fails_to_start_with_invalid_trusted_clock_options(string key, string value, string messageContains)
    {
        using var factory = WithConfig(new Dictionary<string, string?> { [key] = value });

        var validation = HostStartup.FindStartupValidationFailure(factory);
        Assert.NotNull(validation);
        Assert.Contains(messageContains, validation.Message);
    }

    [Fact]
    public void Host_fails_to_start_when_lowering_timeout_below_the_default_threshold()
    {
        // TimeoutMs alone is in range, but now 1500 (threshold) >= 1000 (timeout).
        using var factory = WithConfig(new Dictionary<string, string?> { ["TrustedClock:TimeoutMs"] = "1000" });

        var validation = HostStartup.FindStartupValidationFailure(factory);

        Assert.NotNull(validation);
        Assert.Contains("must be less than TimeoutMs", validation.Message);
    }

    [Fact]
    public void Defaults_from_appsettings_match_the_plan()
    {
        using var factory = new ApiFactory();

        var options = factory.Services.GetRequiredService<IOptions<TrustedClockOptions>>().Value;

        Assert.Equal(3000, options.TimeoutMs);
        Assert.Equal(2000, options.CrossCheckToleranceMs);
        Assert.Equal(1500, options.LowPrecisionThresholdMs);
        Assert.Equal("time.now", options.Primary!.Name);
        Assert.Equal("https://time.now/", options.Primary.BaseUrl);
        Assert.Equal("timeapi.io", options.Secondary!.Name);
        Assert.Equal("https://timeapi.io/", options.Secondary.BaseUrl);
    }

    [Fact]
    public void Registers_both_providers_as_time_sources()
    {
        // Step 1 registered only time.now; step 2 adds timeapi.io. Exactly one source per known provider name.
        using var factory = new ApiFactory();

        var sources = factory.Services.GetServices<ITimeSource>().ToList();

        Assert.Equal(2, sources.Count);
        Assert.Contains(sources, s => s is TimeNowSource && s.Name == "time.now");
        Assert.Contains(sources, s => s is TimeApiIoSource && s.Name == "timeapi.io");
    }

    private static WebApplicationFactory<Program> WithConfig(Dictionary<string, string?> values) =>
        new ApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(values)));
}
