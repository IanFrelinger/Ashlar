using System.Text.RegularExpressions;
using FluentAssertions;
using Ashlar.Core.Application.Paths;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Every tool that mutates the filesystem resolves its target through the WRITE resolver.
///
/// <para><b>Why this blocks a merge.</b> <c>ToolSandbox</c> has two resolvers: the read-intent
/// <c>TryResolvePath</c> (containment only — a read of <c>.ashlar/</c> is legitimate) and the
/// write-intent <c>TryResolveWritePath</c> (containment plus the authoring governance floor). A
/// write tool that calls the read resolver has no floor, and that is the exact history of
/// <c>repo.fs.ensure_file</c>: a real write tool matched by no path policy, calling the read
/// resolver, invisible to the write-path harvester. A floor that can be bypassed by naming a
/// different tool id is not a floor, so the convention is mechanical rather than curated.</para>
///
/// <para>The inventory is a frozen list of the tools that write today, with a stale-row companion
/// so it can only shrink honestly. Hermetic: pure file reads, the same directory discipline as
/// <see cref="AppendOnlyWriterConventionTests"/>.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class WriteToolResolverConventionTests
{
    /// <summary>The calls that change the filesystem. Paren-suffixed so a doc comment naming the API does not count.</summary>
    private static readonly string[] MutatingCalls =
    [
        "File.WriteAllText(",
        "File.WriteAllTextAsync(",
        "File.WriteAllBytes(",
        "File.WriteAllBytesAsync(",
        "File.WriteAllLines(",
        "File.WriteAllLinesAsync(",
        "File.AppendAllText(",
        "File.AppendAllTextAsync(",
        "File.AppendAllLines(",
        "File.AppendAllLinesAsync(",
        "File.AppendText(",
        "File.Create(",
        "File.CreateText(",
        "File.Delete(",
        "File.Move(",
        "File.Copy(",
        "Directory.CreateDirectory(",
        "Directory.Delete(",
        "Directory.Move(",
    ];

    private const string WriteResolver = "ToolSandbox.TryResolveWritePath(";
    private const string ReadResolver = "ToolSandbox.TryResolvePath(";

    private static readonly string ToolsRoot = Path.Combine("src", "Ashlar.Tools.Dev");

    /// <summary>
    /// Every mutating tool known on 2026-09-15, repo-root-relative. A new write tool must be added
    /// here AND call the write resolver; a tool that stops writing must be removed.
    /// </summary>
    private static readonly HashSet<string> MutatingTools = new(StringComparer.Ordinal)
    {
        "src/Ashlar.Tools.Dev/DocsUpdateTool.cs",
        "src/Ashlar.Tools.Dev/RepoFsEnsureFileTool.cs",
        "src/Ashlar.Tools.Dev/RepoFsSearchReplaceTool.cs",
        "src/Ashlar.Tools.Dev/RepoFsWriteTool.cs",
        "src/Ashlar.Tools.Dev/RepoGitCommitTool.cs",
    };

    [Fact]
    public void Every_mutating_tool_resolves_through_the_write_resolver()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var mutating = MutatingFiles(root);

        var unlisted = mutating.Keys.Where(path => !MutatingTools.Contains(path)).ToList();
        unlisted.Should().BeEmpty(
            "a tool that changes the filesystem must be in the frozen inventory in this file and "
            + "resolve through ToolSandbox.TryResolveWritePath. Unlisted: {0}",
            string.Join(", ", unlisted));

        var wrongResolver = mutating
            .Where(kv => !kv.Value.Contains(WriteResolver, StringComparison.Ordinal)
                         || kv.Value.Contains(ReadResolver, StringComparison.Ordinal))
            .Select(kv => kv.Key)
            .ToList();
        wrongResolver.Should().BeEmpty(
            "a mutating tool must call ToolSandbox.TryResolveWritePath and never the read-intent "
            + "ToolSandbox.TryResolvePath — the read resolver has no governance floor. Offenders: {0}",
            string.Join(", ", wrongResolver));
    }

    /// <summary>
    /// A stale inventory row is its own failure: it reads as a floored write tool that no longer
    /// exists, which is how a frozen inventory stops meaning anything.
    /// </summary>
    [Fact]
    public void No_allowlisted_tool_has_stopped_writing()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var actual = MutatingFiles(root).Keys.ToHashSet(StringComparer.Ordinal);

        var stale = MutatingTools.Where(t => !actual.Contains(t)).OrderBy(t => t, StringComparer.Ordinal).ToList();

        stale.Should().BeEmpty(
            "these files no longer mutate the filesystem, so their inventory rows are ghosts. "
            + "Delete the rows with the writers. Stale: {0}",
            string.Join(", ", stale));
    }

    /// <summary>
    /// The policy-chain twin of the floor calls the pure predicates and nothing that touches the
    /// filesystem. Migrating the containment and reparse probes into the policy would deny every
    /// empty-snapshot fixture (the universal fixture across the policy test classes) and go flaky
    /// under the fifty-wide concurrency test; and a probe that throws there ends the whole cycle,
    /// because <c>PolicyEngine.Approve</c> has no try/catch.
    /// </summary>
    [Fact]
    public void The_governance_policy_does_no_filesystem_work()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var policy = Path.Combine(root, "src", "Ashlar.BackgroundAgents.HostRunners", "GovernanceFloorPolicy.cs");
        File.Exists(policy).Should().BeTrue("the policy-chain twin of the floor must exist at {0}", policy);

        var text = File.ReadAllText(policy);
        text.Should().NotContain("Refuse(", "the policy must judge by the pure predicates, not the probing floor");
        text.Should().NotContain("RefuseAuthoringWrite(", "the policy must judge by the pure predicates, not the probing floor");

        var code = CodeLines(File.ReadAllLines(policy));
        code.Should().NotContain("File.", "no filesystem probes in the policy chain");
        code.Should().NotContain("Directory.", "no filesystem probes in the policy chain");
        code.Should().Contain("IsAuthoringGovernancePath(", "the authoring predicate is the one the tool edge applies");
        code.Should().Contain("IsSafeRelativePath(", "a '.' or '..' segment must never reach the predicate un-normalized");
    }

    /// <summary>
    /// The two id-keyed twins of the floor — <c>GovernanceFloorPolicy</c> in the policy chain and
    /// <c>PolicyMcpInvocationGate</c> at the MCP edge — judge by tool id, not by argument shape,
    /// because the read-intent tools carry a string <c>path</c> too. An id list is only as good as
    /// its coupling to the inventory, so every inventory tool's <c>Id</c> must appear, as a string
    /// literal, in both. The tool edge is the floor whatever these lists say; this keeps the counted
    /// denial and the fail-closed MCP default from silently missing a write tool.
    /// </summary>
    [Fact]
    public void Every_write_tool_id_is_named_by_the_policy_twin_and_the_mcp_gate()
    {
        var root = RepoPathResolver.FindRepoRoot();
        var ids = MutatingTools
            .OrderBy(t => t, StringComparer.Ordinal)
            .Select(rel => (File: rel, Id: ToolId(Path.Combine(root, rel))))
            .ToList();
        ids.Should().OnlyContain(t => !string.IsNullOrWhiteSpace(t.Id),
            "every inventory tool declares its id as `Id => \"...\"`, which is what the twins are matched against");

        var twins = new[]
        {
            "src/Ashlar.BackgroundAgents.HostRunners/GovernanceFloorPolicy.cs",
            "src/Ashlar.Mcp.Server/IMcpInvocationGate.cs",
        };
        foreach (var twin in twins)
        {
            var code = CodeLines(File.ReadAllLines(Path.Combine(root, twin)));
            var missing = ids.Where(t => !code.Contains($"\"{t.Id}\"", StringComparison.Ordinal)).Select(t => t.Id).ToList();
            missing.Should().BeEmpty(
                "{0} judges by tool id and must name every write tool in the inventory. Missing: {1}",
                twin, string.Join(", ", missing));
        }
    }

    /// <summary>The <c>Id => "..."</c> literal a tool declares, or empty when it declares none.</summary>
    private static string ToolId(string file)
    {
        var match = Regex.Match(File.ReadAllText(file), @"\bId\s*=>\s*""([^""]+)""");
        return match.Success ? match.Groups[1].Value : string.Empty;
    }

    /// <summary>
    /// Repo-root-relative path → the file's CODE lines (comment lines dropped) for every
    /// <c>.cs</c> under <c>src/Ashlar.Tools.Dev</c> that contains a mutating call.
    /// </summary>
    private static Dictionary<string, string> MutatingFiles(string root)
    {
        var dir = Path.Combine(root, ToolsRoot);
        Directory.Exists(dir).Should().BeTrue("the tools project must exist at {0}", dir);

        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (IsUnderBuildOutput(root, file))
                continue;

            var code = CodeLines(File.ReadAllLines(file));
            if (MutatingCalls.Any(call => code.Contains(call, StringComparison.Ordinal)))
                found[Normalize(Path.GetRelativePath(root, file))] = code;
        }
        return found;
    }

    /// <summary>Comment lines are dropped so a doc comment naming an API is not a call to it.</summary>
    private static string CodeLines(IEnumerable<string> lines) =>
        string.Join("\n", lines.Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    private static bool IsUnderBuildOutput(string root, string file)
    {
        var rel = Normalize(Path.GetRelativePath(root, file));
        return rel.Contains("/bin/", StringComparison.Ordinal) || rel.Contains("/obj/", StringComparison.Ordinal);
    }

    private static string Normalize(string path) => path.Replace('\\', '/').Trim();
}
