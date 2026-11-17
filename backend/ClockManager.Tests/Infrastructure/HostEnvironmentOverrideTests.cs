using System.Globalization;

namespace ClockManager.Tests.Infrastructure;

/// <summary>Proves an E16 run really ran under the requested zone/culture (no-op in a normal run).</summary>
public sealed class HostEnvironmentOverrideTests
{
    [Fact]
    public void Requested_local_zone_and_culture_are_in_effect()
    {
        if (Environment.GetEnvironmentVariable(HostEnvironmentOverride.ZoneVariable) is { Length: > 0 } zoneId)
        {
            var expected = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            Assert.Equal(expected.BaseUtcOffset, TimeZoneInfo.Local.BaseUtcOffset);
            var instant = new DateTime(2026, 1, 12, 7, 0, 0, DateTimeKind.Utc);
            Assert.Equal(expected.GetUtcOffset(instant), TimeZoneInfo.Local.GetUtcOffset(instant));
        }

        if (Environment.GetEnvironmentVariable(HostEnvironmentOverride.CultureVariable) is { Length: > 0 } cultureName)
        {
            Assert.Equal(cultureName, CultureInfo.CurrentCulture.Name);
        }
    }
}
