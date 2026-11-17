using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClockManager.Api.Infrastructure.Time;

/// <summary>
/// Prepares a provider body for the audit trail: drops personal fields (e.g. time.now's <c>client_ip</c>,
/// our server's public IP) and caps the length.
/// </summary>
internal static class PayloadSanitizer
{
    public const int MaxLength = 1024;

    // Relaxed escaping keeps "+01:00" readable instead of "+01:00". The payload is stored and logged,
    // never embedded in HTML, so HTML-sensitive characters don't need escaping.
    private static readonly JsonSerializerOptions Output = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// Removes <paramref name="fieldsToRemove"/> from the top-level JSON object, then truncates to
    /// <see cref="MaxLength"/> characters. A body that is not a JSON object is only truncated.
    /// </summary>
    public static string Sanitize(string body, IReadOnlyCollection<string> fieldsToRemove)
    {
        var result = body;
        if (fieldsToRemove.Count > 0)
        {
            try
            {
                if (JsonNode.Parse(body) is JsonObject obj)
                {
                    foreach (var field in fieldsToRemove) obj.Remove(field);
                    result = obj.ToJsonString(Output);
                }
            }
            catch (JsonException)
            {
                // Not JSON: nothing to remove field-by-field. Callers only store validated (JSON) bodies anyway.
            }
        }

        return Truncate(result);
    }

    private static string Truncate(string value)
    {
        if (value.Length <= MaxLength) return value;
        var cut = MaxLength;
        if (char.IsHighSurrogate(value[cut - 1])) cut--; // don't split a surrogate pair
        return value[..cut];
    }
}
