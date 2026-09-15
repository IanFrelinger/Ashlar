using System.Text.RegularExpressions;
using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every gate record leaves the store through one funnel, and the funnel refuses a missing
/// signature (SPEC-006 S-6).
///
/// <para><b>Why this blocks a merge.</b> <see cref="Ashlar.Manifest.Admission.GateStore"/> decides
/// whether a record with no signature is a stripped one by resolving a
/// <see cref="Ashlar.Manifest.Admission.GateSignatureExpectation"/> from the store's other
/// records and its activation marker — knowledge a second reader would not have. A production
/// file that deserializes a <c>GateRecord</c> for itself bypasses that posture check entirely,
/// and every functional fact in <see cref="GateSignatureExpectationTests"/> stays green while it
/// does. Likewise, the stripped-signature arm of <c>Refuse</c> can be deleted while the other two
/// legs keep the file looking correct; the second fact is the phrase that proves the arm exists.
/// The third fact freezes which production constructions of the store pass no signer, so a new
/// keyless writer cannot appear without saying so in a diff.</para>
///
/// <para><b>These are tripwires, not proofs.</b> Text scans: an alias, a <c>using static</c>, or a
/// <c>JsonNode</c> walk that rebuilds a record by hand would defeat them. Framing a scan as a proof
/// is how a gate goes quiet (<c>docs/HowGatesGoQuiet.md</c>).</para>
///
/// <para>Hermetic: pure file reads, no build, no SDK, no network, no environment variable, and the
/// same structural pruning as <see cref="ProcessGlobalEnvironmentConventionTests"/>, because a
/// nested checkout holds a second copy of every file here.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed partial class GateRecordReadFunnelConventionTests
{
    private static readonly string[] Roots = ["src", "application", "commercial"];

    /// <summary>A project is a test project if it pulls in the test SDK. Nothing else is reliable.</summary>
    private const string TestSdkMarker = "Microsoft.NET.Test.Sdk";

    private const string StorePath = "src/Ashlar.Manifest/Admission/GateStore.cs";
    private const string ExpectationPath = "src/Ashlar.Manifest/Admission/GateSignatureExpectation.cs";

    /// <summary>Spelled with the generic close and the open parenthesis so prose does not count.</summary>
    private static readonly string[] DeserializeMarkers =
    [
        "Deserialize<GateRecord>(",
        "DeserializeAsync<GateRecord>(",
    ];

    /// <summary>
    /// The stripped-signature arm, as the operator reads it. If this phrase leaves the expectation
    /// type, either the arm was deleted or its wording changed; both need a reviewer to look.
    /// </summary>
    private const string StrippedArm = "carries no signature, but this store is signed";

    /// <summary>The store must judge through the expectation, not around it.</summary>
    private const string FunnelCall = ".Refuse(";

    private const string Construction = "new GateStore(";

    /// <summary>
    /// Production files known on 2026-09-15 to construct a <c>GateStore</c> WITHOUT a signer,
    /// repo-root-relative, each with why. Expected to SHRINK: a row whose file has since gained a
    /// signer on every construction fails <see cref="No_allowlisted_keyless_construction_has_gained_a_signer"/>.
    /// </summary>
    private static readonly Dictionary<string, string> KeylessConstructions = new(StringComparer.Ordinal)
    {
        ["application/src/Ashlar.CLI/Commands/GatesCommand.cs"] =
            "gates list/show read with PUBLIC key material only: the store loads the pinning set and the "
            + "activation marker itself, so the read resolves the full expectation, and a corrupt operator.key "
            + "must never block an operator from seeing the queue (e2e: read-survives-a-corrupt-operator-key)",
    };

    [Fact]
    public void Only_the_store_deserializes_a_gate_record()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var readers = ProductionSources(root)
            .Where(s => DeserializeMarkers.Any(m => s.Code.Contains(m, StringComparison.Ordinal)))
            .Select(s => s.Path)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        readers.Should().Equal(
            new[] { StorePath },
            "a GateRecord is judged against a signature expectation the store resolves from its OTHER "
            + "records and its activation marker; a second reader has no such posture and would accept a "
            + "stripped signature. Read through GateStore.GetAsync or ListAsync instead.");
    }

    [Fact]
    public void The_store_refuses_a_missing_signature_by_name()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var expectation = File.ReadAllText(Path.Combine(root, ExpectationPath));
        var store = File.ReadAllText(Path.Combine(root, StorePath));

        expectation.Should().Contain(StrippedArm,
            "the stripped-signature arm of GateSignatureExpectation.Refuse is the half of S-1 the rule "
            + "itself is silent about; deleting it leaves the verify and pin legs looking complete");
        store.Should().Contain(FunnelCall,
            "GateStore must judge every record it lets out through the expectation's Refuse, not "
            + "through an inline check that cannot see the stripped arm");
    }

    [Fact]
    public void Every_production_store_construction_passes_a_signer_or_is_listed()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var keyless = KeylessConstructionSites(root)
            .Where(p => !KeylessConstructions.ContainsKey(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        keyless.Should().BeEmpty(
            "a GateStore constructed without a signer writes unsigned records, which a signed store "
            + "refuses (S-6) and an unsigned one cannot vouch for. Pass OperatorKey.TryLoad() or the "
            + "identity the caller already holds; a READ-only site may be listed here with its reason. "
            + "Unlisted keyless constructions: {0}",
            string.Join(", ", keyless));

        var targetTyped = TargetTypedConstructionSites(root)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        targetTyped.Should().BeEmpty(
            "a target-typed `new(` for a GateStore is invisible to the construction scan above, so "
            + "it would let a keyless writer through unlisted. Spell it `new GateStore(`. Sites: {0}",
            string.Join(", ", targetTyped));
    }

    /// <summary>
    /// A stale row is its own failure: it reads as accounted-for debt that is in fact gone, which
    /// is how a shrinking inventory stops meaning anything.
    /// </summary>
    [Fact]
    public void No_allowlisted_keyless_construction_has_gained_a_signer()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var actual = KeylessConstructionSites(root).ToHashSet(StringComparer.Ordinal);

        var stale = KeylessConstructions.Keys
            .Where(k => !actual.Contains(k))
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        stale.Should().BeEmpty(
            "these files no longer construct a GateStore without a signer, so their rows overstate "
            + "the remaining keyless surface. Delete the rows with the fix. Stale: {0}",
            string.Join(", ", stale));
    }

    // ─────────────────────────── the scan ───────────────────────────

    /// <summary>Repo-root-relative production files with at least one <c>new GateStore(</c> that passes one argument.</summary>
    private static IEnumerable<string> KeylessConstructionSites(string root)
    {
        foreach (var (path, code) in ProductionSources(root))
        {
            var i = code.IndexOf(Construction, StringComparison.Ordinal);
            var keyless = false;
            while (i >= 0 && !keyless)
            {
                keyless = ArgumentCount(code, i + Construction.Length) < 2;
                i = code.IndexOf(Construction, i + Construction.Length, StringComparison.Ordinal);
            }

            if (keyless)
                yield return path;
        }
    }

    /// <summary>
    /// Repo-root-relative production files where a <c>new(</c> sits in a statement or declaration
    /// that names <c>GateStore</c> — the target-typed spelling the construction scan cannot see.
    /// </summary>
    private static IEnumerable<string> TargetTypedConstructionSites(string root)
    {
        foreach (var (path, code) in ProductionSources(root))
        {
            foreach (Match m in TargetTypedNew().Matches(code))
            {
                var start = Math.Max(
                    code.LastIndexOf(';', m.Index),
                    Math.Max(code.LastIndexOf('{', m.Index), code.LastIndexOf('}', m.Index)));
                var statement = code[(start + 1)..m.Index];
                if (statement.Contains("GateStore", StringComparison.Ordinal))
                {
                    yield return path;
                    break;
                }
            }
        }
    }

    [GeneratedRegex(@"(?<![\w.])new\(")]
    private static partial Regex TargetTypedNew();

    /// <summary>Depth-0 arguments between <paramref name="open"/> (just past the paren) and its match.</summary>
    private static int ArgumentCount(string code, int open)
    {
        var depth = 0;
        var commas = 0;
        var any = false;
        for (var i = open; i < code.Length; i++)
        {
            var c = code[i];
            if (c == '(') depth++;
            else if (c == ')')
            {
                if (depth == 0) return any ? commas + 1 : 0;
                depth--;
            }
            else if (c == ',' && depth == 0) commas++;
            else if (!char.IsWhiteSpace(c)) any = true;
        }
        return any ? commas + 1 : 0;
    }

    /// <summary>
    /// Every C# source under the scanned roots that is NOT inside a test project, with comment
    /// lines removed so prose about the funnel cannot satisfy or trip the scan.
    /// </summary>
    private static IEnumerable<(string Path, string Code)> ProductionSources(string root)
    {
        var testDirs = new List<string>();
        foreach (var scanRoot in Roots)
        {
            var dir = Path.Combine(root, scanRoot);
            if (Directory.Exists(dir))
                CollectTestProjectDirs(dir, testDirs);
        }

        foreach (var scanRoot in Roots)
        {
            var dir = Path.Combine(root, scanRoot);
            if (!Directory.Exists(dir))
                continue;

            foreach (var file in Sources(dir))
            {
                if (testDirs.Any(t => file.StartsWith(t + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                    continue;

                yield return (Normalize(Path.GetRelativePath(root, file)), StripCommentLines(File.ReadAllText(file)));
            }
        }
    }

    private static string StripCommentLines(string text)
    {
        var kept = text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal));
        return string.Join('\n', kept);
    }

    private static IEnumerable<string> Sources(string directory)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
            yield return file;

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            foreach (var file in Sources(child))
                yield return file;
        }
    }

    private static void CollectTestProjectDirs(string directory, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.csproj"))
        {
            if (File.ReadAllText(file).Contains(TestSdkMarker, StringComparison.Ordinal))
                found.Add(directory);
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            CollectTestProjectDirs(child, found);
        }
    }

    /// <summary>
    /// Build output, agent scratch space, and the root of any nested checkout — the structural
    /// rule the other convention tests use, so a vendored copy this repository never names is
    /// caught too. Only ever called on directories below the repo root.
    /// </summary>
    private static bool IsPruned(string directory)
    {
        var name = Path.GetFileName(directory);

        if (string.Equals(name, "bin", StringComparison.Ordinal)
            || string.Equals(name, "obj", StringComparison.Ordinal)
            || string.Equals(name, ".claude", StringComparison.Ordinal))
        {
            return true;
        }

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
