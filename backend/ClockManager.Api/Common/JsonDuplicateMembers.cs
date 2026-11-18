using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClockManager.Api.Common;

/// <summary>Refuses a JSON object member that appears twice (QA-17, QA-41, D-041): the last would win silently.</summary>
public static class JsonDuplicateMembers
{
    /// <summary>Marker the 400 factory looks for in the exception text; never sent to the client.</summary>
    public const string Marker = "duplicate JSON member";

    /// <summary>Scans a COPY of the reader (by value). Names are compared case-insensitively, like the binder.</summary>
    public static void ThrowIfAny(Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) return; // other shapes: the normal errors apply
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString()!; // unescaped, so "intent" is the same name
                if (!seen.Add(name)) throw new JsonException(Marker, "$." + name, null, null);
                reader.Read();
                reader.Skip();
            }
        }
        catch (JsonException ex) when (ex.Message != Marker)
        {
            // Malformed JSON: stop scanning; the real read reports it with the usual (sanitised) 400.
        }
    }
}

/// <summary>Registered in the MVC JSON options for a body type: scans for duplicates, then reads it normally.</summary>
public sealed class DuplicateFreeJsonConverter<T> : JsonConverter<T> where T : class
{
    private JsonSerializerOptions? _inner; // the same options minus this converter (else it would call itself)

    public override T? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        JsonDuplicateMembers.ThrowIfAny(reader);
        return JsonSerializer.Deserialize<T>(ref reader, Inner(options));
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) =>
        JsonSerializer.Serialize(writer, value, Inner(options));

    private JsonSerializerOptions Inner(JsonSerializerOptions options)
    {
        if (_inner is not null) return _inner;
        var copy = new JsonSerializerOptions(options);
        for (var i = copy.Converters.Count - 1; i >= 0; i--)
        {
            if (copy.Converters[i] is DuplicateFreeJsonConverter<T>) copy.Converters.RemoveAt(i);
        }
        return _inner = copy;
    }
}
