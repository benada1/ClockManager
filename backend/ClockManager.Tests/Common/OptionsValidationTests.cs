using ClockManager.Tests.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace ClockManager.Tests.Common;

public sealed class OptionsValidationTests
{
    [Theory]
    [InlineData("ShiftRules:MaxOpenShiftHours", "0")]
    [InlineData("ShiftRules:MaxOpenShiftHours", "49")]
    [InlineData("ShiftRules:ShortShiftSeconds", "0")]
    [InlineData("ShiftRules:ShortShiftSeconds", "3601")]
    public void Host_fails_to_start_with_out_of_range_shift_rules(string key, string value)
    {
        using var factory = new ApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })));

        var validation = HostStartup.FindStartupValidationFailure(factory);
        Assert.NotNull(validation);
        Assert.Contains(key.Split(':')[1], validation.Message);
    }

    [Fact]
    public void Host_starts_with_default_shift_rules()
    {
        using var factory = new ApiFactory();

        var ex = Record.Exception(() => factory.CreateClient());

        Assert.Null(ex);
    }
}
