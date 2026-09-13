using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Nothing may append to a file on a path that never rotates.
///
/// <para><b>Why this blocks a merge.</b> A long-lived node writes to an SD card or a laptop SSD
/// for weeks unattended. Every unbounded appender is a file that grows until the card dies or the
/// disk fills, and the failure arrives long after the change that caused it, on hardware nobody is
/// watching. There are seven such writers today. The number is allowed to go DOWN — as
/// <c>CLOSING-PLAN.md</c> Phase 5 bounds them at the write path — and it is not allowed to go up
/// by accident.</para>
///
/// <para>The allowlist is a frozen inventory, not an approval. Each entry is a known unbounded
/// appender awaiting a bounded writer. Removing an entry (because the writer now rotates, or is
/// gone) is the point; adding one requires saying so here, in a diff a reviewer sees.</para>
///
/// <para>Hermetic: pure file reads, no build, no network, no SDK — the same discipline as
/// <see cref="TestOwnershipConventionTests"/>, and the same directory pruning, because a nested
/// git worktree contains a second copy of every one of these files and counting those turned a
/// required check on master red on developers' machines while CI stayed green.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class AppendOnlyWriterConventionTests
{
    /// <summary>The append APIs that grow a file without bound.</summary>
    private static readonly string[] AppendCalls =
    [
        "File.AppendAllText",
        "File.AppendAllLines",
        "File.AppendText",
        "File.AppendAllTextAsync",
        "File.AppendAllLinesAsync",
    ];

    /// <summary>Production trees. Test projects are pruned separately — a test appending to its own temp file is not a node's disk.</summary>
    private static readonly string[] ProductionRoots = ["src", "application", "applications", "commercial"];

    /// <summary>
    /// Every unbounded appender known on 2026-08-28, repo-root-relative. This list is expected to
    /// SHRINK. If a row here no longer appends, delete it — a stale row overstates the debt just
    /// as surely as a missing row hides it.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.Ordinal)
    {
        "application/src/Ashlar.CLI/Runtime/AdaptiveRuntimeExecutionHistoryStore.cs",
        "application/src/Ashlar.CLI/Runtime/WorkflowLabHistoryStore.cs",
        "src/Ashlar.BackgroundAgents.HostRunners/PlannerScratchpad.cs",
        "src/Ashlar.BackgroundAgents/Observations/JsonlObservationStore.cs",
        "src/Ashlar.BackgroundAgents/Telemetry/CycleEventStore.cs",
        "src/Ashlar.Tools.Dev/DocsUpdateTool.cs",
        "src/Ashlar.Tools.Dev/RepoGitCommitTool.cs",
    };

    [Fact]
    public void No_unlisted_file_appends_without_bound()
    {
        var root = RepoPathResolver.FindRepoRoot();

        var unlisted = Appenders(root)
            .Where(path => !Allowed.Contains(path))
            .ToList();

        unlisted.Should().BeEmpty(
            "an appender on a path that never rotates grows until the disk does not, weeks later, "
            + "on a node nobody is watching. Bound it at the write path (CLOSING-PLAN.md Phase 5 "
            + "step 5), or add it to the allowlist in this file and say why in the pull request. "
            + "Unlisted: {0}",
            string.Join(", ", unlisted));
    }

    /// <summary>
    /// A stale allowlist row is its own failure: it reads as accounted-for debt that is in fact
    /// gone, which is how a shrinking inventory stops meaning anything.
    /// </summary>
    [Fact]
    public void No_allowlisted_file_has_stopped_appending()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var actual = Appenders(root).ToHashSet(StringComparer.Ordinal);

        var stale = Allowed.Where(a => !actual.Contains(a)).OrderBy(a => a, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer append, so their allowlist rows overstate the remaining debt. "
            + "Delete the rows with the writers. Stale: {0}",
            string.Join(", ", stale));
    }

    /// <summary>Repo-root-relative paths of production files containing an unbounded append call.</summary>
    private static IEnumerable<string> Appenders(string root)
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
            if (AppendCalls.Any(call => text.Contains(call, StringComparison.Ordinal)))
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
    /// nested clone has a .git DIRECTORY — so a vendored copy this repository never names is
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

        // A test project's own appends are not a node's disk.
        if (name.Contains("Tests", StringComparison.Ordinal))
            return true;

        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
