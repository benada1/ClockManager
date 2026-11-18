using System.Runtime.CompilerServices;

namespace ClockManager.Tests.Infrastructure;

/// <summary>
/// Hosts that are not "Development" refuse to start without <c>Auth:SigningKey</c>. Tests give every host one through the
/// environment variable, like production would. A test that wants it missing sets an empty in-memory value (it wins).
/// </summary>
internal static class TestAuthKey
{
    public const string Value = "test-only-signing-key-0123456789-abcdefghijklmnop";

    [ModuleInitializer]
    public static void Init() => Environment.SetEnvironmentVariable("Auth__SigningKey", Value);
}
