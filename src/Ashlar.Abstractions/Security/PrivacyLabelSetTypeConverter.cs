using System.ComponentModel;
using System.Globalization;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// Converts a <see cref="PrivacyLabelSet"/> to and from its canonical text for reflection-based serializers and
/// binders (TypeDescriptor), so they also treat a set as a string rather than as an object with fields.
/// </summary>
/// <remarks>
/// Converting from anything other than canonical text throws, as the JSON converter does, and so does converting a
/// null set to text: a null set is neither <c>none</c> nor <c>local-only</c>, and the converter does not pick one.
/// </remarks>
internal sealed class PrivacyLabelSetTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text && PrivacyLabelSet.TryParse(text, out var labels))
            return labels;

        throw new FormatException(
            "Not a canonical privacy label set: " + SecurityLabel.Quote(value as string ?? value?.GetType().Name) + ".");
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (destinationType == typeof(string))
        {
            if (value is PrivacyLabelSet labels)
                return labels.ToString();
            if (value is null)
                throw new NotSupportedException("A null privacy label set has no text; label unlabelled data local-only.");
        }

        return base.ConvertTo(context, culture, value, destinationType);
    }
}
