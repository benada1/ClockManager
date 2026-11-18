using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClockManager.Api.Application.Punches;

/// <summary>
/// Reads the punch body and refuses a member that appears twice (QA-17, D-041).
/// <para>
/// System.Text.Json on .NET 8 has no switch for duplicates: the LAST member silently wins. A gateway, WAF or log that reads
/// the FIRST member would then see another request than the one that ran (<c>{"intent":"clock-in","intent":"clock-out"}</c>).
/// So the object is pre-scanned with a <see cref="Utf8JsonReader"/> copy; a repeated name (compared case-insensitively, like
/// the binder: <c>intent</c> and <c>Intent</c> are the same member) throws a <see cref="JsonException"/> whose
/// <see cref="JsonException.Path"/> is the member, which <c>InvalidRequestResponse</c> turns into a fixed message.
/// </para>
/// <para>
/// The real read is then delegated to a private wire type that keeps the strict unknown-member rule (D-029), so
/// every other 400 behaves exactly as before. Only <see cref="PunchRequest"/> uses this; the API's other bodies are untouched.
/// </para>
/// </summary>
public sealed class PunchRequestJsonConverter : JsonConverter<PunchRequest>
{
    /// <summary>Marker the 400 factory looks for in the exception text; never sent to the client.</summary>
    public const string DuplicateMarker = ClockManager.Api.Common.JsonDuplicateMembers.Marker;

    private static readonly JsonSerializerOptions WireOptions = new(JsonSerializerDefaults.Web);

    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed class Wire
    {
        public string? Intent { get; set; }
    }

    public override PunchRequest? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        ClockManager.Api.Common.JsonDuplicateMembers.ThrowIfAny(reader); // by value: a copy, the real reader does not move
        var wire = JsonSerializer.Deserialize<Wire>(ref reader, WireOptions);
        return wire is null ? null : new PunchRequest { Intent = wire.Intent };
    }

    public override void Write(Utf8JsonWriter writer, PunchRequest value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("intent", value.Intent);
        writer.WriteEndObject();
    }
}
