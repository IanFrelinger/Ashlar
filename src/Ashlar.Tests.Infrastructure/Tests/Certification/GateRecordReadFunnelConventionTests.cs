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
/// <para><b>And the funnel's environment.</b> The store's pinning set is not a parameter: the
/// constructor resolves <c>ASHLAR_KEY_DIR</c> / <c>~/.ashlar/keys</c> through
/// <c>OperatorKey.TrustedPublicKeysBase64</c>, so which keys a reader vouches for is process-global
/// state. <see cref="Gate_store_kernel_facts_pin_the_key_directory"/> keeps the suite honest about
/// that: a kernel fact that constructs a store on a developer machine which has run
/// <c>ashlar keys init</c> otherwise runs against that machine's <c>operator.pub</c> while CI stays
/// green. Injecting the key directory is the real fix and is deferred, because
/// <see cref="KeylessConstructionSites"/> identifies a keyless construction by counting constructor
/// arguments — a second argument would blind this gate silently, so the injection MUST replace it
/// before it lands.</para>
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

    /// <summary>The kernel test project: unit facts over the store itself, one per class.</summary>
    private const string KernelTestProject = "src/Ashlar.Tests.Kernel";

    /// <summary>The variable <c>OperatorKey.ResolveKeyDir</c> reads, and the store through it.</summary>
    private const string KeyDirVariable = "ASHLAR_KEY_DIR";

    /// <summary>Spelled with the quotes so a mention of the collection in prose does not satisfy it.</summary>
    private const string SerializingCollection = "[Collection(\"EnvironmentSensitive\")]";

    /// <summary>Only a file that declares facts is scheduled into a collection of its own.</summary>
    private static readonly string[] FactMarkers = ["[Fact", "[Theory"];

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

    /// <summary>
    /// One posture per store operation, resolved in ONE place.
    ///
    /// <para><b>Why the counts are exact.</b> A "resolves at least once" assertion is vacuous —
    /// the shape this fact exists to refuse resolved THREE times (<c>GetAsync</c> from the marker
    /// alone, <c>ListAsync</c> from the marker and the records, the keyless write guard again from
    /// scratch), and the funnels disagreed: with <c>gate-signing.json</c> deleted, <c>GetAsync</c>
    /// returned a stripped, state-flipped record that <c>ListAsync</c> beside it refused, and
    /// <c>DecideAsync</c> reads through <c>GetAsync</c>. So: the empty-anchor resolution is gone
    /// outright; <c>ParseAllAsync</c> is spoken exactly twice — its declaration and the single call
    /// inside <c>ReadStoreAsync</c>; the marker is read exactly once per operation; and
    /// <c>ReadStoreAsync</c> serves at least four call sites.</para>
    /// </summary>
    [Fact]
    public void One_resolution_point_serves_every_read()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var store = StripCommentLines(File.ReadAllText(Path.Combine(root, StorePath)));

        Occurrences(store, "Array.Empty<GateRecord>()").Should().Be(0,
            "resolving an expectation from NO anchors is a second, weaker posture for a single-record "
            + "read; it is what let a keyed GetAsync return a record ListAsync refused");
        Occurrences(store, "ParseAllAsync(").Should().Be(2,
            "the declaration and the one call inside ReadStoreAsync. A third occurrence is a second "
            + "parse of the same directory, and two parses are two postures");
        Occurrences(store, "ReadStoreAsync(").Should().BeGreaterThanOrEqualTo(4,
            "GetAsync, ListAsync, AdmittedInWindowAsync and the keyless write guard all resolve "
            + "through it; a reader that does not is a reader with its own posture");
        Occurrences(store, "GateSigningActivation.TryRead(").Should().Be(1,
            "the marker is read where the posture is resolved and nowhere else — a second read is a "
            + "second answer, and the marker is a file the attacker can delete between the two");
    }

    /// <summary>
    /// A kernel fact that constructs a <c>GateStore</c> is reading the machine's key directory,
    /// whatever its name says. Three facts here — the two keyless readers in
    /// <c>SignedGateStoreTests</c> and the bundle consumer in <c>ExtensionPackagingTests</c> — once
    /// failed on any developer box that had run <c>ashlar keys init</c> and passed in CI, which is
    /// the worst shape a suite can have: green where nobody is watching, red where someone is.
    /// </summary>
    [Fact]
    public void Gate_store_kernel_facts_pin_the_key_directory()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var kernel = Path.Combine(root, KernelTestProject);
        Directory.Exists(kernel).Should().BeTrue(
            "this scan is vacuous if the project moved; point {0} at the new path", nameof(KernelTestProject));

        var constructors = new List<string>();
        var unpinned = new List<string>();
        foreach (var file in Sources(kernel))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains(Construction, StringComparison.Ordinal)
                || !FactMarkers.Any(m => text.Contains(m, StringComparison.Ordinal)))
            {
                continue;
            }

            var path = Normalize(Path.GetRelativePath(root, file));
            constructors.Add(path);
            if (!text.Contains(SerializingCollection, StringComparison.Ordinal)
                || !text.Contains(KeyDirVariable, StringComparison.Ordinal))
            {
                unpinned.Add(path);
            }
        }

        constructors.Should().NotBeEmpty(
            "the scan found no kernel fact constructing a GateStore at all, which means the marker "
            + "'{0}' no longer matches how the store is built", Construction);

        unpinned.OrderBy(p => p, StringComparer.Ordinal).Should().BeEmpty(
            "GateStore's constructor resolves its signer-pinning set from {0} (or ~/.ashlar/keys), so "
            + "a fact that constructs one runs against whatever key material the machine happens to "
            + "hold. Join {1} and point {0} at a FRESH EMPTY directory in the fixture — not at the "
            + "key directory the fact generates into, which would make a reader the fact calls "
            + "keyless silently keyed. Unpinned: {2}",
            KeyDirVariable,
            SerializingCollection,
            string.Join(", ", unpinned));
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

    /// <summary>Non-overlapping occurrences of <paramref name="needle"/>, ordinally.</summary>
    private static int Occurrences(string haystack, string needle)
    {
        var count = 0;
        var i = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (i >= 0)
        {
            count++;
            i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal);
        }
        return count;
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
