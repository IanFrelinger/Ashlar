using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;
using UglyToad.PdfPig;

namespace JobSearch.Materials;

/// <summary>Attachment-ready PDFs (QuestPDF, as Ashlar's own workflow exporter uses) and the extractable-text check (PdfPig).</summary>
public static class Pdf
{
    static Pdf() => QuestPDF.Settings.License = LicenseType.Community;

    /// <summary>Renders a draft as a one-column, text-only PDF.</summary>
    public static byte[] Render(Draft draft, string title) =>
        Document.Create(doc => doc.Page(page =>
        {
            page.Size(PageSizes.Letter);
            page.Margin(54);
            // Ligatures off: an "fi" glyph extracts as one character and splits words for a parser.
            page.DefaultTextStyle(t => t.FontSize(10.5f).DisableFontFeature(FontFeatures.StandardLigatures));
            page.Content().Column(col =>
            {
                col.Spacing(3);
                foreach (var (style, text) in draft.Lines)
                {
                    switch (style)
                    {
                        case "h": col.Item().PaddingTop(6).Text(text).FontSize(13).Bold(); break;
                        case "b": col.Item().PaddingTop(3).Text(text).Bold(); break;
                        case "li": col.Item().PaddingLeft(12).Text("- " + text); break;
                        default: col.Item().Text(text.Length == 0 ? " " : text); break;
                    }
                }
            });
        }))
        .WithMetadata(new DocumentMetadata { Title = title, Producer = "jobsearch on Ashlar" })
        .GeneratePdf();

    private static IEnumerable<string> Words(string text) =>
        Regex.Matches(text.Normalize(System.Text.NormalizationForm.FormKC).ToLowerInvariant(), "[a-z0-9]+", RegexOptions.None, TimeSpan.FromSeconds(1)).Select(m => m.Value);

    /// <summary>Reads the PDF's text back and checks that at least <paramref name="minRatio"/> of the expected words are present.</summary>
    public static PdfCheck CheckExtractable(byte[] pdf, string expectedText, double minRatio = 0.95)
    {
        try
        {
            using var doc = PdfDocument.Open(pdf);
            var extracted = string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
            var have = Words(extracted).ToHashSet(StringComparer.Ordinal);
            var want = Words(expectedText).ToList();
            var ratio = want.Count == 0 ? 0 : (double)want.Count(have.Contains) / want.Count;
            return new PdfCheck(ratio >= minRatio, Math.Round(ratio, 4), doc.NumberOfPages, want.Count);
        }
#pragma warning disable CA1031 // a malformed PDF is a failed check, not a crash
        catch (Exception ex)
#pragma warning restore CA1031
        {
            return new PdfCheck(false, 0, 0, 0, ex.Message);
        }
    }
}
