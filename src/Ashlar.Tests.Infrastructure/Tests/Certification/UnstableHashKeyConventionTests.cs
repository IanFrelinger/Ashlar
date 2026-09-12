using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every production call to <c>.GetHashCode()</c> that is not implementing an equality contract is
/// listed here, with a written verdict on whether its value escapes the process.
///
/// <para><b>Why this blocks a merge.</b> <c>string.GetHashCode()</c> is randomized per process:
/// .NET seeds Marvin at startup, so the same string hashes differently in every launch. Measured
/// in the devtest container, five launches of the same binary over the five call sites that
/// existed on 2026-09-12 produced five distinct values each. A hash like that is fine as a
/// dictionary bucket and wrong as a key that will be read back by anyone else — and the failure is
/// always silent, because a cache that never hits looks exactly like a cache that is cold. #582
/// hit the same class of defect from the other direction: a per-process hash seeding an embedding
/// meant a vector written in one run scored as noise in the next, and the CI symptom was
/// "Expected collection to contain 1 item(s), but found 0".</para>
///
/// <para><b>Why a convention test for what is currently three rows.</b> Because the rows are not
/// the point; the next site is. Six such call sites have accumulated across five files in four
/// assemblies with nothing to slow them down — #582 pinned its two generators with golden
/// constants, which protects those two files and nothing else, and nothing in <c>.editorconfig</c>,
/// <c>scripts/</c>, or any workflow bans the shape. Each of the three rows below is a judgement a
/// person made after reading the call site, and those judgements are exactly what a reviewer of
/// the seventh site needs and will not otherwise have.</para>
///
/// <para><b>What this guard cannot check, stated plainly.</b> The invariant that matters is "does
/// this value cross a process or a disk boundary", and that is not decidable from text. This scan
/// decides something narrower and syntactic: "a non-equality <c>.GetHashCode()</c> call exists in
/// this file". So a new site that is genuinely process-local and harmless still fails here. That
/// is the intended behaviour, not a false positive: the cost is one inventory row carrying the
/// reasoning, and the benefit is that the reasoning happens at all. Conversely, this cannot see a
/// site that reaches a per-process hash by some other route — <c>HashCode.Combine</c> stored to
/// disk, say — so it is a forcing function for review, never a proof of correctness.</para>
///
/// <para><b>Three facts.</b> <see cref="No_unlisted_production_file_hashes_outside_an_equality_override"/>
/// stops the next site arriving unnoticed. <see cref="No_inventory_row_has_stopped_hashing"/> makes
/// the inventory shrink honestly, because a stale row reads as accounted-for debt that is in fact
/// gone. <see cref="Classifier_sees_an_offender_and_exempts_an_equality_override"/> is the positive
/// control the other two cannot supply: they are both satisfied by a classifier that has quietly
/// stopped matching anything, so the classifier is exercised directly against a sample carrying
/// both shapes.</para>
///
/// <para>Hermetic: pure file reads, no build, no network, no SDK. Same directory pruning as
/// <see cref="LiteDbSharedModeConventionTests"/>, whose shape this mirrors.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class UnstableHashKeyConventionTests
{
    /// <summary>
    /// Production trees. Wider than strictly needed today, because covering a tree with no hits
    /// costs zero rows now and closes the blind spot that a new site landing in <c>tools</c> or
    /// <c>products</c> would otherwise walk straight through.
    /// </summary>
    private static readonly string[] ProductionRoots =
    [
        "src", "application", "applications", "commercial",
        "tools", "products", "extensions", "apps", "samples", "spikes", "consumer-template",
    ];

    /// <summary>
    /// Every production file computing a <c>.GetHashCode()</c> outside an equality override, known
    /// on 2026-09-12, repo-root-relative. This list is allowed to go DOWN; it is not allowed to go
    /// up by accident.
    ///
    /// <para>Each row's verdict, and what would overturn it — the full argument lives in a remark
    /// on the member itself, which is where someone changing the code will actually read it:</para>
    /// <list type="bullet">
    /// <item><description><c>SynthesisEngine.GenerateCacheKey</c> — reaches only
    /// <c>ICacheStrategy</c>, whose sole implementation is an in-process dictionary discarded at
    /// shutdown. A restart is a total cache miss, which costs warmth and not correctness. Becomes
    /// a defect the moment any Redis-, disk-, or SQL-backed <c>ICacheStrategy</c> is registered,
    /// which a consumer can do from outside this repository. The 24-hour TTL on the matching
    /// <c>SetAsync</c> already assumes a durability the store cannot provide.</description></item>
    /// <item><description><c>ConflictDetector.GetSchemaKey</c> — keys a <c>Dictionary</c> local to
    /// one method, whose keys are discarded before it returns. Becomes a defect if the key is
    /// ever put into the emitted <c>Conflict</c> or the grouping is cached across calls. Note that
    /// a stable hash is NOT what is wrong at that site: the hash branch is nearly dead, and the
    /// key is taken over raw text so two spellings of one schema already land in different
    /// groups.</description></item>
    /// <item><description><c>InMemoryCompositionCache.StoreAsync</c> — keys a
    /// <c>ConcurrentDictionary</c> in a singleton that dies with the process. Becomes a defect
    /// with any persistent <c>ICompositionCache</c>. The larger bug there is that the derivation
    /// is not on the port, so no caller can construct a key that hits — which a stable hash would
    /// not fix.</description></item>
    /// </list>
    ///
    /// <para><c>BehaviorExecutor</c> and <c>ClusterExecutor</c> were on this list and are not any
    /// more: both now derive their key through <c>SemanticCacheKey</c>. Their 32-bit key could
    /// return another input's <c>BrickOutput</c> on a collision, which is a wrong answer inside a
    /// single process and so was not a candidate for a row here.</para>
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "src/Ashlar.Infrastructure/Composition/InMemoryCompositionCache.cs",
        "src/Ashlar.Orchestration/Coordination/Conflicts/ConflictDetector.cs",
        "src/Ashlar.Orchestration/Negotiation/SynthesisEngine.cs",
    };

    [Fact]
    public void No_unlisted_production_file_hashes_outside_an_equality_override()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var unlisted = Hashers(root)
            .Where(path => !Allowed.Contains(path))
            .ToList();

        unlisted.Should().BeEmpty(
            "string.GetHashCode() is randomized per process, so any value derived from it that is "
            + "persisted, serialized, or compared against a value computed elsewhere is silently "
            + "wrong after a restart. If the value stays inside one process, add the file to the "
            + "inventory in this test WITH the reasoning on the member itself, saying what would "
            + "make it a defect. If it does not, derive the key with "
            + "Ashlar.Infrastructure.Caching.CacheKeyGenerator (SHA-256, Base64) instead. "
            + "Unlisted: {0}",
            string.Join(", ", unlisted));
    }

    /// <summary>
    /// A stale inventory row is its own failure: it reads as a reviewed, understood call site that
    /// is in fact gone, which is how a frozen inventory turns into a blanket approval.
    /// </summary>
    [Fact]
    public void No_inventory_row_has_stopped_hashing()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var actual = Hashers(root).ToHashSet(StringComparer.Ordinal);

        var stale = Allowed.Where(a => !actual.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer compute a non-equality hash, so their rows describe call sites "
            + "that are not there and the verdicts attached to them are unanchored. Delete the row "
            + "with the call. Stale: {0}",
            string.Join(", ", stale));
    }

    /// <summary>
    /// The control the inventory cannot supply. Both facts above are satisfied by a classifier
    /// that has stopped recognising anything — an over-broad exemption, a changed comment
    /// convention, a refactor of the scan — and the inventory would then be empty, matching an
    /// empty allowlist, green forever. This drives the classifier directly over a sample holding
    /// both shapes and requires it to separate them.
    /// </summary>
    [Fact]
    public void Classifier_sees_an_offender_and_exempts_an_equality_override()
    {
        // An equality contract: the whole point of GetHashCode, and never a defect.
        HashesOutsideAnEqualityOverride(
        [
            "public override int GetHashCode() => Value.GetHashCode();",
        ]).Should().BeFalse("an inline equality override is the sanctioned use");

        // The same, wrapped: both real forms in this repository put the call on the next line.
        HashesOutsideAnEqualityOverride(
        [
            "    public override int GetHashCode()",
            "        => Rank.GetHashCode();",
        ]).Should().BeFalse("an expression-bodied override spanning two lines is still an override");

        // A doc comment naming the hazard must not be mistaken for committing it. #582 left two
        // such references behind on purpose, warning future readers off the very thing.
        HashesOutsideAnEqualityOverride(
        [
            "/// Hand-rolled on purpose: <see cref=\"string.GetHashCode()\"/> is randomized per process",
        ]).Should().BeFalse("prose about the hazard is not the hazard");

        // The shape this guard exists for.
        HashesOutsideAnEqualityOverride(
        [
            "var key = description.Trim().ToLowerInvariant().GetHashCode().ToString();",
        ]).Should().BeTrue("a string-derived hash used as a key is exactly what must be reviewed");

        HashesOutsideAnEqualityOverride(
        [
            "return $\"synthesis:{conflict.ConflictType}:{positionKeys.GetHashCode()}\";",
        ]).Should().BeTrue("interpolating the hash into a key string is the same defect");

        // The offender sitting in a file that ALSO has a legitimate override: the exemption must
        // be per-occurrence, not per-file, or one override launders the whole file. This is the
        // case that rejected the first version of the classifier, which used a fixed two-line
        // lookback and therefore treated the call below as part of the override above it.
        HashesOutsideAnEqualityOverride(
        [
            "public override int GetHashCode() => Value.GetHashCode();",
            "",
            "private string Key() => payload.GetHashCode().ToString();",
        ]).Should().BeTrue("an override elsewhere in the file must not exempt an unrelated call");

        // The same, with no blank line to separate them: the ';' ending the override is itself
        // the boundary, so the lookback must stop there too.
        HashesOutsideAnEqualityOverride(
        [
            "public override int GetHashCode() => Value.GetHashCode();",
            "private string Key() => payload.GetHashCode().ToString();",
        ]).Should().BeTrue("a terminated statement ends the member, blank line or not");

        // A doc comment sits between members, so the lookback must not walk through one into a
        // preceding override.
        HashesOutsideAnEqualityOverride(
        [
            "public override int GetHashCode()",
            "    => Value.GetHashCode();",
            "/// <summary>Derives the cache key.</summary>",
            "private string Key() => payload.GetHashCode().ToString();",
        ]).Should().BeTrue("a doc comment separates members and ends the lookback");
    }

    /// <summary>
    /// The classifier, as a pure function over a file's lines so that
    /// <see cref="Classifier_sees_an_offender_and_exempts_an_equality_override"/> can drive it
    /// without touching the tree.
    ///
    /// <para>An occurrence of <c>.GetHashCode()</c> is EXEMPT when it appears in an XML doc
    /// comment, where it is prose rather than code, or when it is inside an equality override.
    /// Everything else counts and has to be argued for in the inventory.</para>
    ///
    /// <para>"Inside an equality override" is decided by walking BACK from the occurrence to the
    /// nearest statement boundary — a blank line, a line ending in <c>;</c>, <c>{</c> or
    /// <c>}</c>, or a doc comment — and asking whether a line declaring
    /// <c>override int GetHashCode()</c> was reached first. That covers the two forms this
    /// repository actually uses (the call on the declaration's own line, and the call on a
    /// continuation line of an expression body) without a fixed line window. A window was tried
    /// first and the control test below rejected it: with a two-line lookback, an unrelated call
    /// two lines under a legitimate override was laundered by it.</para>
    ///
    /// <para>It fails CLOSED. An override whose body is separated from its declaration by a
    /// statement boundary is flagged and needs an inventory row — annoying, safe, and visible.</para>
    /// </summary>
    internal static bool HashesOutsideAnEqualityOverride(IReadOnlyList<string> lines)
    {
        const string Call = ".GetHashCode()";
        const string OverrideDeclaration = "override int GetHashCode()";

        for (var i = 0; i < lines.Count; i++)
        {
            if (!lines[i].Contains(Call, StringComparison.Ordinal))
                continue;

            // Prose, not code.
            if (IsDocComment(lines[i]))
                continue;

            var insideOverride = false;
            for (var back = i; back >= 0; back--)
            {
                var candidate = lines[back].TrimEnd();

                // The boundary test comes FIRST on every line above the occurrence, and that
                // ordering is the whole rule. A line that both declares an override and
                // terminates it -- `public override int GetHashCode() => Value.GetHashCode();` --
                // is a boundary, not an enclosing declaration, for anything below it. Checking
                // the declaration first made that line exempt the NEXT member's call.
                if (back < i)
                {
                    if (candidate.Length == 0 || IsDocComment(candidate))
                        break;

                    if (candidate[^1] is ';' or '{' or '}')
                        break;
                }

                if (candidate.Contains(OverrideDeclaration, StringComparison.Ordinal))
                {
                    insideOverride = true;
                    break;
                }
            }

            if (!insideOverride)
                return true;
        }

        return false;
    }

    private static bool IsDocComment(string line) =>
        line.TrimStart().StartsWith("///", StringComparison.Ordinal);

    /// <summary>Repo-root-relative paths of production files that hash outside an equality override.</summary>
    private static IEnumerable<string> Hashers(string root)
    {
        var found = new List<string>();
        foreach (var top in ProductionRoots)
        {
            var dir = Path.Combine(root, top);
            if (Directory.Exists(dir))
                Collect(root, dir, found);
        }

        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void Collect(string root, string directory, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs"))
        {
            if (HashesOutsideAnEqualityOverride(File.ReadAllLines(file)))
                found.Add(Normalize(Path.GetRelativePath(root, file)));
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (IsPruned(child))
                continue;

            Collect(root, child, found);
        }
    }

    /// <summary>
    /// Build output, agent scratch space, test projects, and the root of any nested checkout. The
    /// nested-checkout rule is structural — <c>git worktree add</c> writes a .git FILE and a
    /// nested clone has a .git DIRECTORY — because a second copy of every file in a worktree would
    /// turn a required check on master red on a developer's machine while CI stayed green. Only
    /// ever called on directories below the repo root.
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

        // Test projects hash freely: a test's key never leaves the test.
        if (name.Contains("Test", StringComparison.OrdinalIgnoreCase))
            return true;

        return Directory.Exists(Path.Combine(directory, ".git"));
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/');
}
