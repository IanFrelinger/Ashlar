using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.5: every production <c>EgressSubject.Enter</c> is pinned with its floor and the method that
/// encloses it. A new production call without a pin fails, and a pin that no longer matches fails.
/// </summary>
/// <remarks>
/// The population is the egress scanner's: <c>src</c>, <c>application</c>, <c>applications</c>, <c>commercial</c>,
/// <c>products</c>, <c>tools</c> and <c>consumer-template</c>, pruning build output, dot directories, nested
/// checkouts and test projects by csproj (<see cref="EgressGuardConventionTests.IsTestProjectRoot"/>). Test
/// <c>Enter</c> calls are not production and are not pinned. The call text is split so this file is not a site.
/// A tripwire, not a proof: an alias, a delegate or reflection is not seen.
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressSubjectFloorPinTests
{
    /// <summary>The call as written, split so that this file is not one of the sites it counts.</summary>
    private const string EnterCall = "EgressSubject" + ".Enter(";

    private const string HighWater = "new HighWaterMark(";

    /// <summary>Below this many scanned files the scan is taken to have lost its reach, not to have found nothing.</summary>
    private const int ScannedFileFloor = 1000;

    private static readonly string[] ProductionRoots =
        ["src", "application", "applications", "commercial", "products", "tools", "consumer-template"];

    /// <summary>
    /// The only production frame in PR 4.5. The floor is <c>SystemHigh</c> because the self-extend snapshot carries
    /// unlabelled carry-over. <c>RunAsync</c> is the overload that runs the cycle; the overloads that await it do not
    /// enter.
    /// </summary>
    private static readonly string[] ExpectedSites =
    [
        "src/Ashlar.BackgroundAgents.HostRunners/SelfExtendRunnerAdapter.cs | RunAsync | SecurityLabel.SystemHigh | x1",
    ];

    private static readonly Regex MethodHeader = new(
        @"^[\t ]*(?:(?:public|private|protected|internal)\s+)?(?:(?:static|async|override|virtual|sealed|unsafe|partial|new)\s+)*[\w.<>,?\[\]\s]+\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*\(",
        RegexOptions.Multiline | RegexOptions.CultureInvariant);

    [Fact]
    public void Every_production_Enter_is_pinned_with_its_floor_and_enclosing_method()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var (sites, scanned) = Sites(root);

        scanned.Should().BeGreaterThanOrEqualTo(ScannedFileFloor,
            "an emptied scan must not pass because it found no Enter");
        sites.Should().Equal(ExpectedSites,
            "a production Enter that is not pinned, or a pin whose file, method, floor or count drifted, fails");
    }

    private static (List<string> Sites, int Scanned) Sites(string root)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var scanned = 0;
        foreach (var name in ProductionRoots)
        {
            var directory = Path.Combine(root, name);
            if (Directory.Exists(directory))
                scanned += Collect(root, directory, counts);
        }

        return (counts.Select(pair => pair.Key + " | x" + pair.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList(), scanned);
    }

    private static int Collect(string root, string directory, SortedDictionary<string, int> counts)
    {
        var scanned = 0;
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            scanned++;
            var text = File.ReadAllText(file);
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            for (var i = text.IndexOf(EnterCall, StringComparison.Ordinal);
                 i >= 0;
                 i = text.IndexOf(EnterCall, i + EnterCall.Length, StringComparison.Ordinal))
            {
                var key = relative + " | " + EnclosingMethod(text, i) + " | " + Floor(text, i);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (!IsPruned(child))
                scanned += Collect(root, child, counts);
        }

        return scanned;
    }

    private static string EnclosingMethod(string text, int callAt)
    {
        var prefix = text[..callAt];
        var matches = MethodHeader.Matches(prefix);
        return matches.Count == 0 ? "(no method)" : matches[^1].Groups["name"].Value;
    }

    private static string Floor(string text, int callAt)
    {
        var window = text.Substring(callAt, Math.Min(400, text.Length - callAt));
        var at = window.IndexOf(HighWater, StringComparison.Ordinal);
        if (at < 0)
            return "(no floor)";

        var start = at + HighWater.Length;
        var end = window.IndexOf(')', start);
        if (end < 0)
            return "(no floor)";

        return window[start..end].Trim();
    }

    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);
        if (name is "bin" or "obj" || name.StartsWith('.'))
            return true;

        var git = Path.Combine(directory, ".git");
        if (File.Exists(git) || Directory.Exists(git))
            return true;

        return EgressGuardConventionTests.IsTestProjectRoot(directory);
    }
}
