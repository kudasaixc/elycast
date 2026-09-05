using System.Text.Json;
using System.Text.Json.Serialization;

namespace Elysium_Cast_IPTV.Services;

/// <summary>Xtream identifiers and timestamps may be JSON numbers or strings.</summary>
public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => reader.TokenType switch
    {
        JsonTokenType.String => reader.GetString(),
        JsonTokenType.Number => ReadNumber(ref reader),
        JsonTokenType.Null => null,
        _ => throw new JsonException("Expected a string or numeric identifier.")
    };

    private static string ReadNumber(ref Utf8JsonReader reader)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return value.RootElement.GetRawText();
    }

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
