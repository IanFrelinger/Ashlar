using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// Writes a <see cref="SecurityLabel"/> as its canonical text, the only wire form, and reads only canonical text
/// back. <see cref="SecurityLabel"/> carries it as its <see cref="JsonConverterAttribute"/>.
/// </summary>
/// <remarks>
/// <para>A structural projection (level, lists, flag) could not rebuild <see cref="SecurityLabel.SystemHigh"/>
/// through the public constructor, so it would come back as a lower label. Reading refuses anything that is not
/// canonical text, JSON <c>null</c> included, instead of guessing: a converter cannot tell a label on data (which
/// fails closed to SystemHigh) from a clearance or a destination (which fails closed to Public). Unlabelled data
/// is written as <c>"SystemHigh"</c>, never as <c>null</c>.</para>
/// <para>It is public so that System.Text.Json source generation in other assemblies can instantiate it.</para>
/// </remarks>
public sealed class SecurityLabelJsonConverter : JsonConverter<SecurityLabel>
{
    /// <summary>
    /// <see langword="true"/>, so a JSON <c>null</c> reaches <see cref="Read"/> and is refused rather than read as
    /// a missing label.
    /// </summary>
    public override bool HandleNull => true;

    /// <summary>Reads a canonical label string; throws <see cref="JsonException"/> for anything else.</summary>
    /// <param name="reader">The reader, positioned on the value.</param>
    /// <param name="typeToConvert">The type being converted.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The parsed label.</returns>
    public override SecurityLabel Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("A security label must be a JSON string in canonical form, for example \"Secret//C:ALPHA\".");

        var text = reader.GetString();
        if (!SecurityLabel.TryParse(text, out var label))
            throw new JsonException("Not a canonical security label: " + SecurityLabel.Quote(text) + ".");

        return label;
    }

    /// <summary>Writes <paramref name="value"/> as its canonical string; refuses a null label.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The label to write.</param>
    /// <param name="options">The serializer options.</param>
    public override void Write(Utf8JsonWriter writer, SecurityLabel value, JsonSerializerOptions options)
    {
        SecurityGuard.ThrowIfNull(writer, nameof(writer));
        if (value is null)
            throw new JsonException("A security label cannot be written as JSON null; label unlabelled data SystemHigh.");

        writer.WriteStringValue(value.ToString());
    }
}
