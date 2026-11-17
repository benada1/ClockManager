using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace ClockManager.Tests.Infrastructure;

/// <summary>
/// E16 (plans/01-trusted-clock.md §5): "the server process in another time zone must change nothing".
/// On Linux, <c>TZ=Pacific/Kiritimati dotnet test</c> is enough. On Windows .NET ignores <c>TZ</c>, so this module
/// initializer lets the WHOLE suite run under an exotic local zone and culture on any OS:
/// <code>
/// CLOCKMANAGER_TEST_TZ=Pacific/Kiritimati CLOCKMANAGER_TEST_CULTURE=th-TH dotnet test backend/ClockManager.Tests
/// </code>
/// Without the variables it does nothing. Test code only; production never reads these variables.
/// </summary>
internal static class HostEnvironmentOverride
{
    public const string ZoneVariable = "CLOCKMANAGER_TEST_TZ";
    public const string CultureVariable = "CLOCKMANAGER_TEST_CULTURE";

    [ModuleInitializer]
    internal static void Apply()
    {
        if (Environment.GetEnvironmentVariable(ZoneVariable) is { Length: > 0 } zoneId)
        {
            OverrideLocalZone(TimeZoneInfo.FindSystemTimeZoneById(zoneId));
        }

        if (Environment.GetEnvironmentVariable(CultureVariable) is { Length: > 0 } cultureName)
        {
            var culture = CultureInfo.GetCultureInfo(cultureName);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
        }
    }

    /// <summary>
    /// Replaces the cached <see cref="TimeZoneInfo.Local"/> (private field <c>s_cachedData._localTimeZone</c> in .NET 8).
    /// Fails loudly if the runtime layout changes, so a TZ run can never silently test the real zone.
    /// </summary>
    private static void OverrideLocalZone(TimeZoneInfo zone)
    {
        TimeZoneInfo.ClearCachedData();
        var cachedDataField = typeof(TimeZoneInfo).GetField("s_cachedData", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new InvalidOperationException("TimeZoneInfo.s_cachedData not found: cannot override the local zone.");
        var cachedData = cachedDataField.GetValue(null)!;
        var localField = cachedData.GetType().GetField("_localTimeZone", BindingFlags.NonPublic | BindingFlags.Instance)
            ?? throw new InvalidOperationException("CachedData._localTimeZone not found: cannot override the local zone.");
        localField.SetValue(cachedData, zone);

        if (TimeZoneInfo.Local.BaseUtcOffset != zone.BaseUtcOffset)
        {
            throw new InvalidOperationException($"Local zone override did not take effect (still {TimeZoneInfo.Local.Id}).");
        }
    }
}
