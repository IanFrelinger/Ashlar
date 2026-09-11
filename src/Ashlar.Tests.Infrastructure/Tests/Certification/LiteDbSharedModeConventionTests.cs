using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Nothing in production may open a LiteDB file without going through the one helper that asks for
/// Shared mode.
///
/// <para><b>Why this blocks a merge.</b> LiteDB's default is <c>Connection=Direct</c>, which takes
/// an exclusive file lock for the LIFETIME of a <c>LiteDatabase</c> instance, and every store here
/// opens one per call. Two overlapping calls race. On Windows the loser throws
/// <c>IOException("...used by another process")</c> — that is how UAT tier 10 found it, on a copilot
/// request that had already RUN its task and then lost its record. On Linux the second open is not
/// refused at all: the writers interleave and corrupt each other's pages, so the loss is SILENT and
/// CI never saw it. Measured at 20 trials x 8 threads x 250 inserts against the real stores, Direct
/// persisted 322 to 4,042 of 40,000 writes per store; Shared persisted 40,000 of 40,000.</para>
///
/// <para><b>Why a convention test and not eleven edits.</b> This is the third time this area has
/// been fixed one store at a time — the LINQ mapper race (#586) and the cold-mapper races (#591)
/// both landed with one class converted and the rest left behind, and both had to be finished later.
/// The eleven edits are the smaller half of the fix; this guard is the half that keeps them.</para>
///
/// <para><b>Three facts, because "which files" is not the invariant here — "how" is.</b>
/// <see cref="No_unlisted_file_constructs_a_LiteDatabase"/> freezes the inventory so a new store
/// cannot appear unnoticed. <see cref="No_allowlisted_file_has_stopped_constructing_one"/> makes the
/// inventory shrink honestly, because a stale row reads as accounted-for debt that is in fact gone.
/// <see cref="Every_LiteDatabase_construction_uses_the_shared_helper"/> is the one the other two
/// cannot supply: it requires each of those files to reach
/// <c>LiteDbConnectionString.ForSharedAccess</c> and forbids hand-rolling a <c>Filename=</c> string,
/// so the helper stays the only place the connection mode is chosen.</para>
///
/// <para><b>What this guard does NOT catch.</b> It reads text, so it cannot see behaviour. A store
/// that calls the helper and then appends its own <c>;Connection=Direct</c> passes. So does a
/// deployment that binds <c>Filename=...;Connection=Direct</c> from configuration, which the helper
/// deliberately respects (<c>MeshPersistenceOptions.DatabasePath</c> is config-bound). And a text
/// scan cannot tell code from prose: a doc comment quoting the construction call trips it, which is
/// why <c>LiteDbDocumentMapper</c>'s remarks say "<c>LiteDatabase</c> open" rather than spelling the
/// call. The behavioural half lives in
/// <c>Tests/Persistence/LiteDbStoreSharedModeConcurrencyTests</c>, which races two instances of a
/// store over one file and counts what is readable afterwards — assert on COUNTS there, never on an
/// expected <c>IOException</c>, or the test passes on Windows and asserts nothing in CI.</para>
///
/// <para>Hermetic: pure file reads, no build, no network, no SDK — the same discipline and the same
/// directory pruning as <see cref="AppendOnlyWriterConventionTests"/>, whose shape this mirrors.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class LiteDbSharedModeConventionTests
{
    /// <summary>
    /// Every door LiteDB offers into a file, not just the one this repository happens to use today.
    /// A future store reaching for <c>LiteRepository</c> or an engine directly would bypass a check
    /// that only knew about <c>LiteDatabase</c>.
    /// </summary>
    private static readonly string[] DatabaseConstructions =
    [
        "new LiteDatabase(",
        "new LiteRepository(",
        "new LiteEngine(",
        "new SharedEngine(",
    ];

    /// <summary>The helper that is the only sanctioned way to compose a LiteDB connection string.</summary>
    private const string HelperCall = "LiteDbConnectionString.ForSharedAccess";

    /// <summary>
    /// Hand-rolled connection-string composition. These are the exact forms every store carried
    /// before this guard existed, and each one is a place the mode could be chosen again by accident.
    /// </summary>
    private static readonly string[] HandRolledFilename =
    [
        "$\"Filename=",
        "\"Filename=\" +",
        "\"Filename={",
    ];

    /// <summary>
    /// Production trees. Wider than <see cref="AppendOnlyWriterConventionTests"/>'s four, because
    /// none of the extra roots contains a LiteDB construction today — so covering them costs zero
    /// allowlist rows now and closes the blind spot that a store landing in <c>tools</c> or
    /// <c>products</c> would otherwise walk straight through.
    /// </summary>
    private static readonly string[] ProductionRoots =
    [
        "src", "application", "applications", "commercial",
        "tools", "products", "extensions", "apps", "samples", "spikes", "consumer-template",
    ];

    /// <summary>
    /// Every production file that opens a LiteDB database, known on 2026-09-11, repo-root-relative.
    /// Twelve stores, one row each. This list is allowed to go DOWN — a store that moves to a
    /// different engine deletes its row — and it is not allowed to go up by accident: a new row is a
    /// new file with a new file lock, and it must be argued for in a diff a reviewer sees.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "commercial/src/Ashlar.Commercial.Fleet.Infrastructure/LiteDbFleetNodeRegistry.cs",
        "commercial/src/Ashlar.Commercial.Fleet.Infrastructure/LiteDbMeshTaskRegistry.cs",
        "src/Ashlar.BackgroundAgents/Trust/LiteDbDataDecisionAuditLog.cs",
        "src/Ashlar.Infrastructure/Adaptation/LiteDbAdaptationAuditLog.cs",
        "src/Ashlar.Infrastructure/Adaptation/LiteDbAdaptationLog.cs",
        "src/Ashlar.Infrastructure/Copilot/LiteDbCopilotTaskStore.cs",
        "src/Ashlar.Infrastructure/Observation/LiteDbPatternProcessedStore.cs",
        "src/Ashlar.Infrastructure/Observation/LiteDbPatternStore.cs",
        "src/Ashlar.Infrastructure/Pipelines/LiteDbPipelineRunStore.cs",
        "src/Ashlar.Infrastructure/SelfContext/LiteDbExecutionTracer.cs",
        "src/Ashlar.Infrastructure/SelfContext/LiteDbTestFailureStore.cs",
        "src/Ashlar.Infrastructure/Trust/LiteDbUserKnowledgeLogStore.cs",
    };

    [Fact]
    public void No_unlisted_file_constructs_a_LiteDatabase()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var unlisted = Constructors(root)
            .Where(path => !Allowed.Contains(path))
            .ToList();

        unlisted.Should().BeEmpty(
            "a new LiteDB file is a new exclusive file lock, and LiteDB's default mode loses "
            + "92-99.5% of concurrent writes without throwing. Compose the connection string with "
            + "{0}, then add the file to the allowlist in this test and say why in the pull request. "
            + "Unlisted: {1}",
            HelperCall,
            string.Join(", ", unlisted));
    }

    /// <summary>
    /// A stale allowlist row is its own failure: it reads as a known store that is in fact gone,
    /// which is how a frozen inventory quietly turns into a blanket approval.
    /// </summary>
    [Fact]
    public void No_allowlisted_file_has_stopped_constructing_one()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var actual = Constructors(root).ToHashSet(StringComparer.Ordinal);

        var stale = Allowed.Where(a => !actual.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer open a LiteDB database, so their allowlist rows describe stores "
            + "that are not there. Delete the rows with the stores. Stale: {0}",
            string.Join(", ", stale));
    }

    /// <summary>
    /// The invariant the inventory cannot express. Being on the list is permission to open a file;
    /// it is not permission to choose the mode.
    /// </summary>
    [Fact]
    public void Every_LiteDatabase_construction_uses_the_shared_helper()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var offenders = new List<string>();
        foreach (var relative in Constructors(root))
        {
            var text = File.ReadAllText(Path.Combine(root, relative));

            if (!text.Contains(HelperCall, StringComparison.Ordinal))
                offenders.Add($"{relative} (never calls {HelperCall})");

            foreach (var form in HandRolledFilename)
            {
                if (text.Contains(form, StringComparison.Ordinal))
                    offenders.Add($"{relative} (composes its own connection string: {form})");
            }
        }

        offenders.Should().BeEmpty(
            "the connection mode must be chosen in exactly one place. A store that builds its own "
            + "\"Filename=\" string takes LiteDB's Direct default, which is the bug this guard exists "
            + "to keep fixed — it was found on a copilot request that had already run its task and "
            + "then lost its record. Offenders: {0}",
            string.Join(", ", offenders));
    }

    /// <summary>Repo-root-relative paths of production files that open a LiteDB database.</summary>
    private static IEnumerable<string> Constructors(string root)
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
            var text = File.ReadAllText(file);
            if (DatabaseConstructions.Any(call => text.Contains(call, StringComparison.Ordinal)))
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
    /// nested-checkout rule is structural — <c>git worktree add</c> writes a .git FILE and a nested
    /// clone has a .git DIRECTORY — because a second copy of every store in a worktree would turn
    /// the only required check on master red on a developer's machine while CI stayed green. Only
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

        // A test opening its own temp database is not a shared state directory.
        if (name.Contains("Tests", StringComparison.Ordinal))
            return true;

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
