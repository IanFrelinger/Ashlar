using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// Writes a <see cref="SecurityLabel"/> as its canonical text, the only wire form, and reads only canonical text
/// back.
/// </summary>
/// <remarks>
/// A structural projection (level, lists, flag) could not rebuild <see cref="SecurityLabel.SystemHigh"/> through
/// the public constructor, so it would come back as a lower label. Reading refuses non-canonical text instead of
/// guessing: a converter cannot tell a label on data (which fails closed to SystemHigh) from a clearance or a
/// destination (which fails closed to Public).
/// </remarks>
internal sealed class SecurityLabelJsonConverter : JsonConverter<SecurityLabel>
{
    public override SecurityLabel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("A security label must be a JSON string in canonical form, for example \"Secret//C:ALPHA\".");

        var text = reader.GetString();
        if (!SecurityLabel.TryParse(text, out var label))
            throw new JsonException("Not a canonical security label: " + SecurityLabel.Quote(text) + ".");

        return label;
    }

    public override void Write(Utf8JsonWriter writer, SecurityLabel value, JsonSerializerOptions options)
    {
        SecurityGuard.ThrowIfNull(writer, nameof(writer));
        SecurityGuard.ThrowIfNull(value, nameof(value));
        writer.WriteStringValue(value.ToString());
    }
}
