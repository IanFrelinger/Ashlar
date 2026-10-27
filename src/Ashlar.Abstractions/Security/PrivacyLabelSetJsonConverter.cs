using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// Writes a <see cref="PrivacyLabelSet"/> as its canonical text, the only wire form, and reads only canonical text
/// back. <see cref="PrivacyLabelSet"/> carries it as its <see cref="JsonConverterAttribute"/>.
/// </summary>
/// <remarks>
/// <para>Reading refuses anything that is not canonical text, JSON <c>null</c> included, instead of repairing it: a set
/// a lenient reader "fixes" is a set nobody wrote. Code that reads label text from a source it does not control and
/// wants the fail-closed value instead calls <see cref="PrivacyLabelSet.ParseOrLocalOnly"/>. Unlabelled data is
/// written as <c>"local-only"</c>, never as <c>null</c>.</para>
/// <para>It is public so that System.Text.Json source generation in other assemblies can instantiate it.</para>
/// </remarks>
[Experimental(SecurityExperimental.DiagnosticId, UrlFormat = SecurityExperimental.UrlFormat)]
public sealed class PrivacyLabelSetJsonConverter : JsonConverter<PrivacyLabelSet>
{
    /// <summary>
    /// <see langword="true"/>, so a JSON <c>null</c> reaches <see cref="Read"/> and is refused rather than read as a
    /// missing set, and a null set reaches <see cref="Write"/> and is refused rather than written as <c>null</c>.
    /// </summary>
    public override bool HandleNull => true;

    /// <summary>Reads a canonical label-set string; throws <see cref="JsonException"/> for anything else.</summary>
    /// <param name="reader">The reader, positioned on the value.</param>
    /// <param name="typeToConvert">The type being converted.</param>
    /// <param name="options">The serializer options.</param>
    /// <returns>The parsed set.</returns>
    public override PrivacyLabelSet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                "A privacy label set must be a JSON string in canonical form, for example \"no-external-model,no-web\" or \"none\".");
        }

        var text = reader.GetString();
        if (!PrivacyLabelSet.TryParse(text, out var labels))
            throw new JsonException("Not a canonical privacy label set: " + SecurityLabel.Quote(text) + ".");

        return labels;
    }

    /// <summary>Writes <paramref name="value"/> as its canonical string; refuses a null set.</summary>
    /// <param name="writer">The writer.</param>
    /// <param name="value">The set to write.</param>
    /// <param name="options">The serializer options.</param>
    public override void Write(Utf8JsonWriter writer, PrivacyLabelSet value, JsonSerializerOptions options)
    {
        SecurityGuard.ThrowIfNull(writer, nameof(writer));
        if (value is null)
            throw new JsonException("A privacy label set cannot be written as JSON null; label unlabelled data local-only.");

        writer.WriteStringValue(value.ToString());
    }
}
