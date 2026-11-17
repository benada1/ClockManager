namespace ClockManager.Tests.Fakes;

/// <summary>
/// A test that calls a real external provider. Skipped unless <c>CLOCKMANAGER_LIVE_TESTS=1</c>, so CI and normal
/// runs never touch the network. Combine with <c>[Trait("Category", "Live")]</c>.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "CLOCKMANAGER_LIVE_TESTS";

    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Live provider test; set {EnvironmentVariable}=1 to run it.";
        }
    }
}
