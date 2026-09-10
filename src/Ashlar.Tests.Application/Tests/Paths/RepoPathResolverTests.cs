using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Application.Tests.Paths;

/// <summary>
/// <see cref="RepoPathResolver"/> locates the repo root and the Block 1 observation
/// folder. Both methods fall back to the current directory when they find nothing,
/// which makes their PRECEDENCE and their failure modes the things worth pinning —
/// a silent wrong-directory answer is far worse than a throw.
///
/// Every test passes an explicit start/root and works inside its own temp tree, so
/// nothing here mutates the process-global current directory. That matters: doing
/// so is exactly what made Ashlar.Tests.CLI flaky (see ConsoleCapture / 5fc20270).
/// </summary>
public sealed class RepoPathResolverTests : IDisposable
{
    private readonly string _root;

    public RepoPathResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"ashlar-repopath-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------ FindRepoRoot

    [Fact]
    public void Finds_the_directory_containing_Ashlar_sln()
    {
        var repo = Dir("repo");
        File.WriteAllText(Path.Combine(repo, "Ashlar.sln"), "");

        RepoPathResolver.FindRepoRoot(repo).Should().Be(repo);
    }

    [Fact]
    public void Walks_upward_from_a_nested_directory()
    {
        var repo = Dir("repo");
        File.WriteAllText(Path.Combine(repo, "Ashlar.sln"), "");
        var nested = Dir("repo", "src", "Some.Project", "Deep");

        RepoPathResolver.FindRepoRoot(nested).Should().Be(repo,
            "the resolver must find the root from anywhere inside the tree, not just the top");
    }

    [Fact]
    public void Falls_back_to_the_current_directory_when_no_solution_is_found()
    {
        // A temp tree with no Ashlar.sln anywhere above it. The documented fallback is
        // the CWD — asserted explicitly so a future change to throw (or to return
        // the start dir) is a visible decision rather than a silent one.
        var orphan = Dir("no-solution-here", "nested");

        RepoPathResolver.FindRepoRoot(orphan).Should().Be(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void Stops_at_the_nearest_solution_when_trees_are_nested()
    {
        var outer = Dir("outer");
        File.WriteAllText(Path.Combine(outer, "Ashlar.sln"), "");
        var inner = Dir("outer", "vendored", "inner");
        File.WriteAllText(Path.Combine(inner, "Ashlar.sln"), "");
        var deep = Dir("outer", "vendored", "inner", "src");

        RepoPathResolver.FindRepoRoot(deep).Should().Be(inner,
            "the nearest enclosing solution wins; walking past it would resolve a vendored copy to the host repo");
    }

    // ------------------------------------------- FindBlock1ObservationPath

    [Fact]
    public void Prefers_the_Infrastructure_observation_folder()
    {
        var repo = RepoWithSln();
        var infra = Dir("repo", "src", "Ashlar.Infrastructure", "Observation");
        Dir("repo", "src", "Ashlar.BackgroundAgents", "Observation");

        RepoPathResolver.FindBlock1ObservationPath(repo).Should().Be(infra,
            "Infrastructure is documented as the preferred source when both exist");
    }

    [Fact]
    public void Falls_back_to_the_BackgroundAgents_observation_folder()
    {
        var repo = RepoWithSln();
        var bg = Dir("repo", "src", "Ashlar.BackgroundAgents", "Observation");

        RepoPathResolver.FindBlock1ObservationPath(repo).Should().Be(bg);
    }

    [Fact]
    public void Falls_back_to_the_current_directory_when_neither_folder_exists()
    {
        var repo = RepoWithSln();

        RepoPathResolver.FindBlock1ObservationPath(repo)
            .Should().Be(Directory.GetCurrentDirectory());
    }

    [Fact]
    public void Returns_the_current_directory_when_the_root_is_not_a_Ashlar_repo()
    {
        // No Ashlar.sln at the supplied root: the resolver must not go on to hand back
        // a plausible-looking src/...\Observation path from an unrelated tree.
        var notARepo = Dir("not-a-repo");
        Dir("not-a-repo", "src", "Ashlar.Infrastructure", "Observation");

        RepoPathResolver.FindBlock1ObservationPath(notARepo)
            .Should().Be(Directory.GetCurrentDirectory());
    }

    // ------------------------------------------------- ResolveStateDirectory

    [Fact]
    public void State_defaults_to_dot_ashlar_state_under_the_root_and_creates_it()
    {
        var repo = RepoWithSln();

        var state = RepoPathResolver.ResolveStateDirectory(repo, configuredStateDirectory: null);

        state.Should().Be(Path.Combine(repo, ".ashlar", "state"),
            "LiteDB stores must not land in the CWD / repo root (they used to litter it and get lost on container recreate)");
        Directory.Exists(state).Should().BeTrue("LiteDB does not create parent directories, so the resolver must");
    }

    [Fact]
    public void Configured_state_directory_wins_absolute_or_relative_to_the_root()
    {
        var repo = RepoWithSln();
        var elsewhere = Dir("elsewhere");

        RepoPathResolver.ResolveStateDirectory(repo, elsewhere).Should().Be(elsewhere,
            "ASHLAR_STATE_DIR is how compose/k8s point state at a mounted volume");
        RepoPathResolver.ResolveStateDirectory(repo, "var/state").Should().Be(Path.Combine(repo, "var", "state"),
            "a relative override hangs off the root, not the CWD");
    }

    [Fact]
    public void Legacy_root_layout_is_kept_until_the_new_directory_exists()
    {
        var repo = RepoWithSln();
        File.WriteAllText(Path.Combine(repo, "ashlar-patterns.db"), "");

        RepoPathResolver.ResolveStateDirectory(repo, configuredStateDirectory: null).Should().Be(repo,
            "an install with ashlar-*.db already at the root must keep reading its data instead of silently starting fresh");
        Directory.Exists(Path.Combine(repo, ".ashlar", "state")).Should().BeFalse("the legacy branch must not create the new directory, or the next call would flip");

        Directory.CreateDirectory(Path.Combine(repo, ".ashlar", "state"));
        RepoPathResolver.ResolveStateDirectory(repo, configuredStateDirectory: null).Should().Be(Path.Combine(repo, ".ashlar", "state"),
            "once the operator has migrated (created .ashlar/state), the new layout wins even if stray root files remain");
    }

    [Fact]
    public void Configured_state_directory_ignores_legacy_root_files()
    {
        var repo = RepoWithSln();
        File.WriteAllText(Path.Combine(repo, "ashlar-adaptation.db"), "");
        var mounted = Dir("mounted-volume");

        RepoPathResolver.ResolveStateDirectory(repo, mounted).Should().Be(mounted,
            "an explicit location is an explicit decision; the legacy fallback only applies to the default");
    }

    // -------------------------------------------------------- ResolveDocsRoot

    [Fact]
    public void Docs_root_defaults_to_the_root_itself_and_creates_nothing()
    {
        var repo = RepoWithSln();

        RepoPathResolver.ResolveDocsRoot(repo, configuredDocsRoot: null).Should().Be(repo,
            "documenting a promotion in the repo's own docs/bricks/ is the dogfooding behaviour, so unset means the root");
        Directory.Exists(Path.Combine(repo, "docs")).Should().BeFalse(
            "resolving a path is not a reason to touch the tree; the writer creates docs/bricks/ when it has something to put there");
    }

    [Fact]
    public void Configured_docs_root_wins_absolute_or_relative_to_the_root()
    {
        var repo = RepoWithSln();
        var elsewhere = Dir("elsewhere");

        RepoPathResolver.ResolveDocsRoot(repo, elsewhere).Should().Be(elsewhere,
            "ASHLAR_DOCS_ROOT is how a test host keeps a child `ashlar improve` from documenting a promotion into the checkout (#580)");
        RepoPathResolver.ResolveDocsRoot(repo, "  ").Should().Be(repo,
            "a blank override is no override, matching ASHLAR_STATE_DIR");
        RepoPathResolver.ResolveDocsRoot(repo, "build/docs").Should().Be(Path.Combine(repo, "build", "docs"),
            "a relative override hangs off the root, not the CWD");
    }

    private string RepoWithSln()
    {
        var repo = Dir("repo");
        File.WriteAllText(Path.Combine(repo, "Ashlar.sln"), "");
        return repo;
    }

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _root }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }
}
