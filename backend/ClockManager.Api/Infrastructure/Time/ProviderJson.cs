using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// Strict reading of a provider's JSON body, shared by the time sources. Only exact JSON types are accepted
/// (a number in a string, or <c>0</c> for a bool, is a wrong type), and property names are case-sensitive.
/// </summary>
internal static class ProviderJson
{
    /// <summary>
    /// Parses <paramref name="body"/> as a JSON object whose top-level keys are all distinct.
    /// A duplicate key is rejected (QA-07): <see cref="JsonElement.TryGetProperty(string, out JsonElement)"/> would
    /// silently pick one of the two values, so a body like <c>{"timezone":"Europe/Zurich","timezone":"UTC"}</c>
    /// has no single meaning and must not be trusted.
    /// </summary>
    /// <returns>The document (caller disposes) or null with <paramref name="error"/> set.</returns>
    public static JsonDocument? ParseObject(string body, out string? error)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            error = "body is not JSON";
            return null;
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            error = "body is not a JSON object";
            return null;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                error = $"duplicate key '{property.Name}' in the JSON object"; // read before Dispose
                document.Dispose();
                return null;
            }
        }

        error = null;
        return document;
    }

    public static string Missing(string field) => $"'{field}' is missing or has the wrong type";

    public static bool TryGetString(JsonElement root, string name, [NotNullWhen(true)] out string? value)
    {
        if (root.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.String)
        {
            value = element.GetString()!;
            return true;
        }
        value = null;
        return false;
    }

    /// <summary>A JSON integer that fits in an <see cref="int"/>; <c>8.5</c> or <c>"8"</c> is rejected.</summary>
    public static bool TryGetInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt32(out value);
    }

    /// <summary>A JSON integer that fits in a <see cref="long"/>.</summary>
    public static bool TryGetInt64(JsonElement root, string name, out long value)
    {
        value = 0;
        return root.TryGetProperty(name, out var element)
            && element.ValueKind == JsonValueKind.Number
            && element.TryGetInt64(out value);
    }

    /// <summary>A JSON <c>true</c>/<c>false</c> only.</summary>
    public static bool TryGetBoolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var element)) return false;
        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                value = true;
                return true;
            case JsonValueKind.False:
                return true;
            default:
                return false;
        }
    }
}
