using System.Globalization;
using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every test project must be registered in <c>ci/test-ownership.tsv</c>.
///
/// <para><b>The incident this exists to prevent.</b> On 2026-08-28,
/// <c>Ashlar.Commercial.Tests.Fleet.Host</c> was found to be in no solution, named by no gate
/// script, and absent from <c>Ashlar.sln</c>. Its four tests had been failing for at least ten
/// days across twenty consecutive runs of a manual-first weekly gate, and no pull request had
/// ever run them. Nothing in the repository could answer "which tests does no gate run?", so
/// nothing noticed.</para>
///
/// <para><b>Why this class is in this namespace.</b> <c>cert-gate</c> is one of five required
/// status checks on master (CONTRIBUTING.md; the others are build-core, shell-lint, lychee and
/// Readiness summary), and alone among them it runs on every pull request with no path filter,
/// and it selects tests by the substring
/// <c>FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification</c>
/// (scripts/cert-gate-config.sh). Placing the registry assertion here makes it merge-blocking
/// with no branch-protection change. Moving or renaming this namespace silently disarms it.</para>
///
/// <para>These assertions are hermetic — pure file reads, no build, no network, no SDK — so they
/// cost the gate milliseconds and cannot flake.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class TestOwnershipConventionTests
{
    private const string RegistryRelativePath = "ci/test-ownership.tsv";

    /// <summary>A project is a test project if it pulls in the test SDK. Nothing else is reliable.</summary>
    private const string TestSdkMarker = "Microsoft.NET.Test.Sdk";

    [Fact]
    public void EveryTestProject_IsRegistered()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var registered = ReadRegistry(root);
        var discovered = DiscoverTestProjects(root);

        var unregistered = new List<string>();
        foreach (var project in discovered)
        {
            if (!registered.ContainsKey(project))
                unregistered.Add(project);
        }

        unregistered.Should().BeEmpty(
            "every project containing {0} must have a row in {1}. Add one naming the gate that "
            + "runs it, or UNOWNED with an expiry date. A test project no gate runs will rot "
            + "silently — that is exactly how Ashlar.Commercial.Tests.Fleet.Host went ten days "
            + "red without a single pull request noticing. Unregistered: {2}",
            TestSdkMarker, RegistryRelativePath, string.Join(", ", unregistered));
    }

    /// <summary>
    /// A nested checkout carries a full copy of every test project, and those copies belong to
    /// another tree. Reporting them made a single <c>git worktree</c> inside the repository turn
    /// a required check red on the developer's machine, while CI — which has no
    /// worktrees — stayed green. Pruning is by structure (a <c>.git</c> entry), so a vendored
    /// clone this repository never names is caught too.
    /// </summary>
    [Fact]
    public void DiscoverTestProjects_SkipsNestedCheckoutsAndBuildOutput()
    {
        const string TestProject =
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>"
            + "<PackageReference Include=\"" + TestSdkMarker + "\" Version=\"17.0.0\" />"
            + "</ItemGroup></Project>";

        var root = Path.Combine(
            Path.GetTempPath(), "ashlar-ownership-walk-" + Guid.NewGuid().ToString("n"));

        try
        {
            // The only project that genuinely belongs to this tree.
            Write(Path.Combine(root, "src", "Real.Tests", "Real.Tests.csproj"), TestProject);

            // A git worktree under .claude, as the repository's own tooling creates.
            var agentWorktree = Path.Combine(root, ".claude", "worktrees", "copy");
            Write(Path.Combine(agentWorktree, ".git"), "gitdir: /elsewhere/.git/worktrees/copy");
            Write(Path.Combine(agentWorktree, "src", "Real.Tests", "Real.Tests.csproj"), TestProject);

            // A worktree outside .claude, so the .git file is what prunes it, not the name.
            var sibling = Path.Combine(root, "scratch", "sibling-worktree");
            Write(Path.Combine(sibling, ".git"), "gitdir: /elsewhere/.git/worktrees/sibling");
            Write(Path.Combine(sibling, "src", "Real.Tests", "Real.Tests.csproj"), TestProject);

            // A nested clone, which carries a .git directory rather than a file.
            var clone = Path.Combine(root, "vendor", "thirdparty");
            Directory.CreateDirectory(Path.Combine(clone, ".git"));
            Write(Path.Combine(clone, "Their.Tests", "Their.Tests.csproj"), TestProject);

            // Build output stays excluded, as it always was.
            Write(Path.Combine(root, "src", "Real.Tests", "obj", "Real.Tests.csproj"), TestProject);
            Write(Path.Combine(root, "src", "Real.Tests", "bin", "Real.Tests.csproj"), TestProject);

            var discovered = DiscoverTestProjects(root);

            discovered.Should().BeEquivalentTo(
                new[] { "src/Real.Tests/Real.Tests.csproj" },
                "a copy of a project inside another checkout is not this repository's project, "
                + "and build output is not a project at all");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        static void Write(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }

    [Fact]
    public void EveryRegisteredProject_StillExists()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var registered = ReadRegistry(root);

        var missing = new List<string>();
        foreach (var entry in registered)
        {
            if (!File.Exists(Path.Combine(root, entry.Key.Replace('/', Path.DirectorySeparatorChar))))
                missing.Add(entry.Key);
        }

        missing.Should().BeEmpty(
            "a stale row in {0} silently overstates coverage: it reads as though a project is "
            + "accounted for when the project is gone. Delete the row with the project. Missing: {1}",
            RegistryRelativePath, string.Join(", ", missing));
    }

    [Fact]
    public void NoUnownedRow_IsPastItsExpiry()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var registered = ReadRegistry(root);
        var today = DateTime.UtcNow.Date;

        var expired = new List<string>();
        foreach (var entry in registered)
        {
            var row = entry.Value;
            if (!string.Equals(row.Runner, "UNOWNED", StringComparison.Ordinal))
                continue;

            if (!DateTime.TryParseExact(row.Expires, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var expires))
            {
                expired.Add($"{entry.Key} (unparseable expiry '{row.Expires}'; use YYYY-MM-DD)");
                continue;
            }

            if (expires.Date < today)
                expired.Add($"{entry.Key} (expired {row.Expires})");
        }

        expired.Should().BeEmpty(
            "an UNOWNED row is a dated debt, not a permanent state — it means no pull-request "
            + "check runs that project's tests. Either wire the project to a gate and replace "
            + "UNOWNED with the gate name, or make a deliberate decision to extend the date and "
            + "say why in the note column. Do not extend a date in the same change that trips it. "
            + "Expired: {0}", string.Join(", ", expired));
    }

    private sealed record OwnershipRow(string Runner, string Expires, string Note);

    /// <summary>Parses the registry. Blank lines, comments, and the header row are skipped.</summary>
    private static Dictionary<string, OwnershipRow> ReadRegistry(string root)
    {
        var path = Path.Combine(root, RegistryRelativePath.Replace('/', Path.DirectorySeparatorChar));
        File.Exists(path).Should().BeTrue($"{RegistryRelativePath} is the registry these tests enforce; it must exist at {path}");

        var rows = new Dictionary<string, OwnershipRow>(StringComparer.Ordinal);
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal))
                continue;

            var parts = line.Split('\t');
            if (parts.Length < 4)
                continue;
            if (string.Equals(parts[0], "project", StringComparison.Ordinal))
                continue; // header

            rows[Normalize(parts[0])] = new OwnershipRow(parts[1].Trim(), parts[2].Trim(), parts[3].Trim());
        }

        return rows;
    }

    /// <summary>
    /// Every csproj under the repo that pulls in the test SDK. Enumerated from the working tree
    /// rather than from a solution file — enumerating from a solution is precisely what could
    /// never have found the project that was in no solution.
    ///
    /// <para>The walk descends a directory at a time rather than using
    /// <c>SearchOption.AllDirectories</c>, so whole subtrees can be pruned. It prunes build
    /// output and nested checkouts. A nested checkout holds a second copy of every test project
    /// in the repository, and those copies are not this repository's projects: the registry
    /// stores repo-root-relative paths, so a copy can never match a row. Counting them turned
    /// a required check red on any working tree containing a worktree, while
    /// CI — which has none — stayed green.</para>
    /// </summary>
    private static List<string> DiscoverTestProjects(string root)
    {
        var found = new List<string>();
        Collect(root, root, found);
        found.Sort(StringComparer.Ordinal);
        return found;
    }

    private static void Collect(string root, string directory, List<string> found)
    {
        foreach (var file in Directory.EnumerateFiles(directory, "*.csproj"))
        {
            if (File.ReadAllText(file).Contains(TestSdkMarker, StringComparison.Ordinal))
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
    /// True for a directory the walk must not enter: build output, agent scratch space, or the
    /// root of a nested checkout. Only ever called on directories below the repo root, so the
    /// root's own <c>.git</c> can never prune the entire walk.
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

        // `git worktree add` writes a .git FILE; a nested clone has a .git DIRECTORY. Either
        // marks a tree that is not this one, including vendored copies this repo never names.
        var git = Path.Combine(directory, ".git");
        return File.Exists(git) || Directory.Exists(git);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
