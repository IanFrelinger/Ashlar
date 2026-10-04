using System.ComponentModel;
using System.Globalization;

namespace Ashlar.Abstractions.Security;

/// <summary>
/// Converts a <see cref="SecurityLabel"/> to and from its canonical text for reflection-based serializers and
/// binders (TypeDescriptor), so they also treat a label as a string rather than as a set of fields.
/// </summary>
/// <remarks>
/// A serializer that binds the public constructor field by field would turn <see cref="SecurityLabel.SystemHigh"/>
/// into TopSecret. Converting from anything other than canonical text throws, as the JSON converter does.
/// </remarks>
internal sealed class SecurityLabelTypeConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) =>
        sourceType == typeof(string);

    public override bool CanConvertTo(ITypeDescriptorContext? context, Type? destinationType) =>
        destinationType == typeof(string);

    public override object ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is string text && SecurityLabel.TryParse(text, out var label))
            return label;

        throw new FormatException(
            "Not a canonical security label: " + SecurityLabel.Quote(value as string ?? value?.GetType().Name) + ".");
    }

    public override object? ConvertTo(ITypeDescriptorContext? context, CultureInfo? culture, object? value, Type destinationType)
    {
        if (destinationType == typeof(string) && value is SecurityLabel label)
            return label.ToString();

        return base.ConvertTo(context, culture, value, destinationType);
    }
}
