using System.Runtime.CompilerServices;

namespace ClockManager.Tests.Infrastructure;

/// <summary>
/// The provider warm-up (QA-11) is on by default in the app, but no test host may touch the network (CI rule).
/// A process-wide environment variable beats appsettings.json for EVERY factory, including bare
/// <c>WebApplicationFactory&lt;Program&gt;</c> ones; tests that exercise the warm-up switch it on in memory (that
/// source is added later and wins).
/// </summary>
internal static class NoNetworkByDefault
{
    [ModuleInitializer]
    internal static void Init() =>
        Environment.SetEnvironmentVariable("TrustedClock__Warmup__Enabled", "false");
}
