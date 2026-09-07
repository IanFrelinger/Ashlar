using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>Writes the release manager's campaign report as JSON and Markdown.</summary>
public static class CampaignReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    /// <summary>Default output directory relative to the repository root.</summary>
    public const string DefaultRelativeOutputDirectory = ".ashlar/dogfood-campaign";

    /// <summary>Serialize <paramref name="report"/> to JSON.</summary>
    public static string ToJson(CampaignReport report)
        => JsonSerializer.Serialize(report, JsonOptions);

    /// <summary>Render a human-readable Markdown summary.</summary>
    public static string ToMarkdown(CampaignReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Dogfood campaign — {report.Verdict}");
        sb.AppendLine();
        sb.AppendLine($"- Campaign: `{report.CampaignId}`");
        sb.AppendLine($"- Commit: `{report.CommitSha}`");
        sb.AppendLine($"- Mode: {(report.Full ? "full" : "fast")}");
        sb.AppendLine($"- Generated: {report.GeneratedAt:O}");
        sb.AppendLine($"- Summary: {report.Summary}");
        sb.AppendLine();

        if (report.MissingReports.Count > 0)
        {
            sb.AppendLine("## Missing reports (fail-closed)");
            sb.AppendLine();
            foreach (var missing in report.MissingReports)
                sb.AppendLine($"- `{missing}` did not report back to the release manager.");
            sb.AppendLine();
        }

        foreach (var agentReport in report.Reports)
        {
            sb.AppendLine($"## {agentReport.Lane} — {agentReport.AgentId} ({agentReport.Verdict})");
            sb.AppendLine();
            sb.AppendLine(agentReport.Summary);
            sb.AppendLine();
            if (agentReport.Findings.Count == 0)
            {
                sb.AppendLine("_No findings._");
                sb.AppendLine();
                continue;
            }

            foreach (var finding in agentReport.Findings)
            {
                var location = finding.Path is null
                    ? string.Empty
                    : finding.Line is int line
                        ? $" (`{finding.Path}:{line}`)"
                        : $" (`{finding.Path}`)";
                sb.AppendLine($"- **{finding.Severity}** `{finding.Code}` {finding.Message}{location}");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Write <c>report.json</c>, <c>report.md</c>, and <c>latest.json</c> / <c>latest.md</c>
    /// copies under <paramref name="outputDirectory"/>.
    /// </summary>
    public static void Write(CampaignReport report, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var json = ToJson(report);
        var markdown = ToMarkdown(report);
        File.WriteAllText(Path.Combine(outputDirectory, "report.json"), json);
        File.WriteAllText(Path.Combine(outputDirectory, "report.md"), markdown);
        File.WriteAllText(Path.Combine(outputDirectory, "latest.json"), json);
        File.WriteAllText(Path.Combine(outputDirectory, "latest.md"), markdown);
    }
}
