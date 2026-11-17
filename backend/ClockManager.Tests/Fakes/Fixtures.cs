using System.Text.Json.Nodes;

namespace ClockManager.Tests.Fakes;

/// <summary>
/// Provider response bodies in <c>TestData/</c>. <c>real-*</c> files are real responses (2026-11-17; the
/// client IP replaced with the documentation address 203.0.113.7). The others are hand-made in the same
/// shape, using exact instants from the zurich-time-cases matrix.
/// </summary>
public static class Fixtures
{
    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", relativePath));

    /// <summary>Loads a JSON fixture, lets the test change it, and returns the new body (for "one field wrong" variants).</summary>
    public static string Mutate(string relativePath, Action<JsonObject> change)
    {
        var obj = JsonNode.Parse(Read(relativePath))!.AsObject();
        change(obj);
        return obj.ToJsonString();
    }
}
