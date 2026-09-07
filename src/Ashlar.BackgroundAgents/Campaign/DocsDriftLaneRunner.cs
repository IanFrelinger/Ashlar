using System.Text.RegularExpressions;

namespace Ashlar.BackgroundAgents.Campaign;

/// <summary>
/// Specialist that hunts documentation drift: stale in-repo paths for extracted
/// apps, unpublished version pins sold as installable, leftover verify markers,
/// and a missing campaign surface in the North Star docs.
/// </summary>
public sealed class DocsDriftLaneRunner : ICampaignLaneRunner
{
    private static readonly Regex ExtractedReleaseManagerPath = new(
        @"apps/release-manager",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly string[] LineAwarenessTokens =
    {
        "extracted",
        "extraction",
        "graduated",
        "archive/"
    };

    /// <inheritdoc />
    public CampaignLane Lane => CampaignLane.DocsDrift;

    /// <inheritdoc />
    public Task<CampaignAgentReport> RunAsync(CampaignRunContext context, CancellationToken cancellationToken = default)
    {
        var started = DateTimeOffset.UtcNow;
        var findings = new List<CampaignFinding>();
        var repoRoot = context.RepoRoot;

        if (string.IsNullOrWhiteSpace(repoRoot) || !Directory.Exists(repoRoot))
        {
            return Task.FromResult(Error(context, started, "Repo root is missing.", "missing-repo"));
        }

        var exemptions = LoadPathExemptions(repoRoot);
        ScanStaleExtractedPaths(repoRoot, exemptions, findings);
        ScanUnpublishedVersionPins(repoRoot, exemptions, findings);
        ScanVerifyMarkers(repoRoot, exemptions, findings);
        ScanCampaignSurface(repoRoot, findings);

        var blockers = findings.Count(f => string.Equals(f.Severity, "error", StringComparison.OrdinalIgnoreCase));
        var verdict = blockers == 0 ? CampaignVerdictKind.Pass : CampaignVerdictKind.Fail;
        var summary = blockers == 0
            ? $"No documentation drift blockers ({findings.Count} note(s))."
            : $"{blockers} documentation drift blocker(s).";

        return Task.FromResult(new CampaignAgentReport(
            context.AgentId,
            context.Role,
            Lane,
            verdict,
            summary,
            findings,
            started,
            DateTimeOffset.UtcNow,
            new Dictionary<string, string>
            {
                ["finding_count"] = findings.Count.ToString(),
                ["blocker_count"] = blockers.ToString()
            }));
    }

    private static CampaignAgentReport Error(CampaignRunContext context, DateTimeOffset started, string message, string code)
    {
        return new CampaignAgentReport(
            context.AgentId,
            context.Role,
            CampaignLane.DocsDrift,
            CampaignVerdictKind.Error,
            message,
            new[] { new CampaignFinding(code, message) },
            started,
            DateTimeOffset.UtcNow);
    }

    private static void ScanStaleExtractedPaths(
        string repoRoot,
        IReadOnlyList<Regex> exemptions,
        List<CampaignFinding> findings)
    {
        var inRepo = Directory.Exists(Path.Combine(repoRoot, "apps", "release-manager"));
        if (inRepo)
            return;

        foreach (var file in EnumerateDocFiles(repoRoot))
        {
            var relative = ToRelative(repoRoot, file);
            if (IsExempt(relative, exemptions))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!ExtractedReleaseManagerPath.IsMatch(line))
                    continue;
                if (LineAwarenessTokens.Any(token => line.Contains(token, StringComparison.OrdinalIgnoreCase)))
                    continue;

                findings.Add(new CampaignFinding(
                    "stale-extracted-path",
                    "Documents `apps/release-manager/` as a current in-repo path, but that vertical was extracted.",
                    relative,
                    i + 1));
            }
        }
    }

    private static void ScanUnpublishedVersionPins(
        string repoRoot,
        IReadOnlyList<Regex> exemptions,
        List<CampaignFinding> findings)
    {
        var versionPath = Path.Combine(repoRoot, "VERSION");
        var publishedPath = Path.Combine(repoRoot, "ci", "published-version");
        if (!File.Exists(versionPath) || !File.Exists(publishedPath))
        {
            if (!File.Exists(publishedPath))
            {
                findings.Add(new CampaignFinding(
                    "missing-published-version",
                    "ci/published-version is missing; docs cannot be pinned to the nuget.org line."));
            }

            return;
        }

        var repoVersion = File.ReadAllText(versionPath).Trim();
        var published = File.ReadAllText(publishedPath).Trim();
        if (string.Equals(repoVersion, published, StringComparison.Ordinal))
            return;

        var packagePin = new Regex(
            $@"PackageReference\s+Include=""Ashlar\.[^""]+""[^>]*Version=""{Regex.Escape(repoVersion)}""",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var addPackage = new Regex(
            $@"dotnet\s+add\s+package\s+Ashlar\.[^\s""]+[^\n]*{Regex.Escape(repoVersion)}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        foreach (var file in EnumerateDocFiles(repoRoot).Concat(EnumerateConsumerFiles(repoRoot)))
        {
            var relative = ToRelative(repoRoot, file);
            if (IsExempt(relative, exemptions))
                continue;

            var text = File.ReadAllText(file);
            if (packagePin.IsMatch(text) || addPackage.IsMatch(text))
            {
                findings.Add(new CampaignFinding(
                    "unpublished-version-pin",
                    $"Pins Ashlar packages at repo VERSION {repoVersion}, but nuget.org is {published}.",
                    relative));
            }
        }
    }

    private static void ScanVerifyMarkers(
        string repoRoot,
        IReadOnlyList<Regex> exemptions,
        List<CampaignFinding> findings)
    {
        foreach (var file in EnumerateDocFiles(repoRoot))
        {
            var relative = ToRelative(repoRoot, file);
            if (IsExempt(relative, exemptions))
                continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains("<!-- verify:", StringComparison.OrdinalIgnoreCase))
                {
                    findings.Add(new CampaignFinding(
                        "leftover-verify-marker",
                        "Unresolved documentation verify marker.",
                        relative,
                        i + 1));
                }
            }
        }
    }

    private static void ScanCampaignSurface(string repoRoot, List<CampaignFinding> findings)
    {
        var campaignDoc = Path.Combine(repoRoot, "docs", "DogfoodCampaign.md");
        if (!File.Exists(campaignDoc))
        {
            findings.Add(new CampaignFinding(
                "missing-campaign-doc",
                "docs/DogfoodCampaign.md is required so the automated campaign is operator-discoverable."));
        }

        var validation = Path.Combine(repoRoot, "docs", "DogfoodValidation.md");
        if (File.Exists(validation) &&
            !File.ReadAllText(validation).Contains("dogfood campaign", StringComparison.OrdinalIgnoreCase))
        {
            findings.Add(new CampaignFinding(
                "campaign-undocumented-in-north-star",
                "docs/DogfoodValidation.md does not mention the automated dogfood campaign."));
        }
    }

    private static IEnumerable<string> EnumerateDocFiles(string repoRoot)
    {
        var roots = new[]
        {
            Path.Combine(repoRoot, "docs"),
            Path.Combine(repoRoot, "samples"),
            Path.Combine(repoRoot, "consumer-template")
        };

        if (File.Exists(Path.Combine(repoRoot, "README.md")))
            yield return Path.Combine(repoRoot, "README.md");

        foreach (var root in roots.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.md", SearchOption.AllDirectories))
                yield return file;
        }
    }

    private static IEnumerable<string> EnumerateConsumerFiles(string repoRoot)
    {
        var consumer = Path.Combine(repoRoot, "consumer-template");
        if (!Directory.Exists(consumer))
            yield break;

        foreach (var file in Directory.EnumerateFiles(consumer, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            if (ext is ".csproj" or ".props" or ".md" or ".json")
                yield return file;
        }
    }

    private static IReadOnlyList<Regex> LoadPathExemptions(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "docs", "background-agents", "dogfood-campaign-doc-exceptions.tsv");
        if (!File.Exists(path))
            return Array.Empty<Regex>();

        var regexes = new List<Regex>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("path-regex", StringComparison.OrdinalIgnoreCase))
                continue;

            var pattern = line.Split('\t')[0].Trim();
            if (pattern.Length == 0)
                continue;
            regexes.Add(new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled));
        }

        return regexes;
    }

    private static bool IsExempt(string relative, IReadOnlyList<Regex> exemptions)
        => exemptions.Any(rx => rx.IsMatch(relative.Replace('\\', '/')));

    private static string ToRelative(string repoRoot, string path)
        => Path.GetRelativePath(repoRoot, path).Replace('\\', '/');
}
