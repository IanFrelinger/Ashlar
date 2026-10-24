using System.Text;
using System.Text.Json;
using Ashlar.Core.Domain.Execution;

namespace Ashlar.Hosting;

/// <summary>
/// Standard output formatters for BrickOutput display in CLI and other consumers.
/// Provides consistent formatting across applications.
/// </summary>
public static class BrickOutputFormatters
{
    /// <summary>
    /// Formats BrickOutput as JSON with optional indentation.
    /// </summary>
    /// <param name="output">The brick output to format.</param>
    /// <param name="indented">Whether to use indented formatting (default: true).</param>
    /// <returns>JSON representation of the output.</returns>
    public static string ToJson(this BrickOutput output, bool indented = true)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = indented,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        var data = new Dictionary<string, object>();
        
        if (output.Summary != null)
            data["summary"] = output.Summary;

        foreach (var key in output.Keys)
        {
            data[key] = output.TryGet<object>(key, out var value) ? value! : null!;
        }

        return JsonSerializer.Serialize(data, options);
    }

    /// <summary>
    /// Formats BrickOutput as Markdown with headers and formatting.
    /// </summary>
    /// <param name="output">The brick output to format.</param>
    /// <param name="title">Optional title for the output section.</param>
    /// <returns>Markdown representation of the output.</returns>
    public static string ToMarkdown(this BrickOutput output, string? title = null)
    {
        var sb = new StringBuilder();

        if (!string.IsNullOrEmpty(title))
        {
            sb.AppendLine($"## {title}");
            sb.AppendLine();
        }

        if (output.Summary != null)
        {
            sb.AppendLine("### Summary");
            sb.AppendLine();
            sb.AppendLine(output.Summary);
            sb.AppendLine();
        }

        if (output.Keys.Any())
        {
            sb.AppendLine("### Outputs");
            sb.AppendLine();

            foreach (var key in output.Keys.OrderBy(k => k))
            {
                if (output.TryGet<object>(key, out var value))
                {
                    sb.AppendLine($"**{key}**: {FormatValueForMarkdown(value)}");
                    sb.AppendLine();
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats BrickOutput as plain text with key-value pairs.
    /// </summary>
    /// <param name="output">The brick output to format.</param>
    /// <param name="includeEmpty">Whether to include keys with null/empty values (default: false).</param>
    /// <returns>Plain text representation of the output.</returns>
    public static string ToPlainText(this BrickOutput output, bool includeEmpty = false)
    {
        var sb = new StringBuilder();

        if (output.Summary != null)
        {
            sb.AppendLine("Summary:");
            sb.AppendLine($"  {output.Summary}");
            sb.AppendLine();
        }

        if (output.Keys.Any())
        {
            sb.AppendLine("Outputs:");

            foreach (var key in output.Keys.OrderBy(k => k))
            {
                if (output.TryGet<object>(key, out var value))
                {
                    var stringValue = FormatValueForPlainText(value);
                    
                    if (includeEmpty || !string.IsNullOrWhiteSpace(stringValue))
                    {
                        sb.AppendLine($"  {key}: {stringValue}");
                    }
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats BrickOutput as a table with aligned columns.
    /// </summary>
    /// <param name="output">The brick output to format.</param>
    /// <returns>Table representation of the output.</returns>
    public static string ToTable(this BrickOutput output)
    {
        var sb = new StringBuilder();

        if (output.Summary != null)
        {
            sb.AppendLine($"Summary: {output.Summary}");
            sb.AppendLine();
        }

        if (!output.Keys.Any())
            return sb.ToString();

        var keys = output.Keys.OrderBy(k => k).ToList();
        var maxKeyLength = keys.Max(k => k.Length);
        var columnWidth = Math.Max(maxKeyLength + 2, 15);

        sb.AppendLine("┌" + new string('─', columnWidth) + "┬" + new string('─', 50) + "┐");
        sb.AppendLine($"│ {"Key".PadRight(columnWidth - 2)} │ {"Value".PadRight(48)} │");
        sb.AppendLine("├" + new string('─', columnWidth) + "┼" + new string('─', 50) + "┤");

        foreach (var key in keys)
        {
            if (output.TryGet<object>(key, out var value))
            {
                var displayValue = FormatValueForTable(value);
                var paddedKey = key.PadRight(columnWidth - 2);
                
                if (displayValue.Length <= 48)
                {
                    sb.AppendLine($"│ {paddedKey} │ {displayValue.PadRight(48)} │");
                }
                else
                {
                    sb.AppendLine($"│ {paddedKey} │ {displayValue.Substring(0, 45)}... │");
                }
            }
        }

        sb.AppendLine("└" + new string('─', columnWidth) + "┴" + new string('─', 50) + "┘");

        return sb.ToString();
    }

    private static string FormatValueForMarkdown(object? value)
    {
        return value switch
        {
            null => "*null*",
            string s => s,
            bool b => b ? "✅ true" : "❌ false",
            System.Collections.IEnumerable enumerable and not string =>
                FormatEnumerableForMarkdown(enumerable),
            _ => value.ToString() ?? "*null*"
        };
    }

    private static string FormatValueForPlainText(object? value)
    {
        return value switch
        {
            null => "(null)",
            string s => s,
            bool b => b.ToString().ToLower(),
            System.Collections.IEnumerable enumerable and not string =>
                FormatEnumerableForPlainText(enumerable),
            _ => value.ToString() ?? "(null)"
        };
    }

    private static string FormatValueForTable(object? value)
    {
        return value switch
        {
            null => "(null)",
            string s => s,
            bool b => b ? "✓" : "✗",
            System.Collections.IEnumerable enumerable and not string =>
                $"[{CountEnumerable(enumerable)} items]",
            _ => value.ToString() ?? "(null)"
        };
    }

    private static string FormatEnumerableForMarkdown(System.Collections.IEnumerable enumerable)
    {
        var items = new List<string>();
        foreach (var item in enumerable)
        {
            items.Add(item?.ToString() ?? "null");
        }
        return items.Count == 0 ? "[]" : $"[{string.Join(", ", items)}]";
    }

    private static string FormatEnumerableForPlainText(System.Collections.IEnumerable enumerable)
    {
        var items = new List<string>();
        foreach (var item in enumerable)
        {
            items.Add(item?.ToString() ?? "null");
        }
        return string.Join(", ", items);
    }

    private static int CountEnumerable(System.Collections.IEnumerable enumerable)
    {
        int count = 0;
        foreach (var _ in enumerable)
        {
            count++;
        }
        return count;
    }
}
