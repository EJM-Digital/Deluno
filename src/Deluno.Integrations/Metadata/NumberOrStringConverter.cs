using System.Text.Json;
using System.Text.Json.Serialization;

namespace Deluno.Integrations.Metadata;

/// <summary>
/// Reads an identifier that a provider sends as a JSON number in one place and
/// a string in another (TMDb's <c>tvdb_id</c> is a number), into a string.
/// </summary>
internal sealed class NumberOrStringConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var whole)
                ? whole.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.Null => null,
            _ => throw new JsonException("Expected an identifier as a string or a number.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
