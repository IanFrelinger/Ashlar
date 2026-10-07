using System.Text.Json;
using FluentAssertions;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Paths;
using Ashlar.Policies;
using Ashlar.Policies.Dev;
using Ashlar.Tools.Dev;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// Two adversarial tables of containment spellings: one run against every call site that admits an
/// ABSOLUTE candidate path, and one run against the sites that resolve a model-supplied RELATIVE
/// path against a sandbox root.
///
/// <para><b>The escape this pins.</b> <c>OutputPathSandboxed</c> compared
/// <c>Path.GetFullPath(output).StartsWith(Path.GetFullPath(OutputRoot), OrdinalIgnoreCase)</c> with no
/// directory separator after the root, so with <c>OutputRoot = .../app/out</c> the policy approved
/// <c>.../app/out-evil/x</c>, <c>.../app/outx</c> and <c>.../app/out.dll</c>: every sibling of the
/// output directory whose name extends <c>out</c>. It is live: <c>AgentExecutorAdapter</c> composes it
/// with <c>AllowAllPolicy</c> and sets <c>OutputRoot</c> to <c>&lt;cwd&gt;/out</c>, and
/// <c>assembly.decompile</c> creates and writes its <c>output</c> directory. On Linux the same
/// ignore-case comparison also admitted a case variant of the root, which there names a different
/// directory; <c>PathAllowlist</c>'s absolute-path leg had that half of the defect too.</para>
///
/// <para>Rows of the absolute table are lexical: nothing is created on disk, so the table is hermetic
/// and runs identically everywhere, except the one row whose answer depends on the platform's default
/// case rule, which is computed here from the OS, never from the code under test. The relative table
/// builds a real sandbox with real siblings beside it, because the tools it runs against read and
/// list what they resolve.</para>
/// </summary>
[Trait("Category", "Unit")]
public sealed class PathContainmentTests
{
    private static readonly char Sep = Path.DirectorySeparatorChar;

    /// <summary>A root that never exists: every row is decided lexically.</summary>
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ashlar-containment-table", "app", "out");

    private static readonly string Parent = Path.GetDirectoryName(Root)!;

    private static bool CaseFoldingPlatform => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    /// <summary>One spelling: the root the site is configured with, the candidate, and the verdicts.</summary>
    public sealed record Case(string Name, string RootSpelling, string Candidate, bool Inside, bool StrictlyInside);

    /// <summary>
    /// The shared table. <see cref="Case.Inside"/> counts the root itself as inside (what every
    /// converted call site means); <see cref="Case.StrictlyInside"/> does not (what
    /// <c>MediatedWritePath</c> means).
    /// </summary>
    public static IReadOnlyList<Case> Table { get; } =
    [
        new("root-itself", Root, Root, true, false),
        new("root-with-trailing-separator-on-candidate", Root, Root + Sep, true, false),
        new("root-with-trailing-separator-on-root", Root + Sep, Root, true, false),
        new("child", Root, Path.Combine(Root, "child.txt"), true, true),
        new("child-of-root-with-trailing-separator", Root + Sep, Path.Combine(Root, "child.txt"), true, true),
        new("dotdot-that-stays-inside", Root, Path.Combine(Root, "a", "b", "..", "c.txt"), true, true),
        new("doubled-separator", Root, Root + Sep + Sep + "child.txt", true, true),
        new("root-spelled-with-dotdot", Path.Combine(Root, "sub", ".."), Path.Combine(Root, "child.txt"), true, true),
        new("dotdot-to-parent", Root, Path.Combine(Root, ".."), false, false),
        new("dotdot-to-sibling", Root, Path.Combine(Root, "..", "out-evil", "x"), false, false),
        new("deep-dotdot-escape", Root, Path.Combine(Root, "..", "..", "..", "..", "elsewhere"), false, false),
        new("sibling-prefix-directory", Root, Path.Combine(Parent, "out-evil", "x"), false, false),
        new("sibling-prefix-file", Root, Path.Combine(Parent, "out.dll"), false, false),
        new("sibling-prefix-bare", Root, Path.Combine(Parent, "outx"), false, false),
        // Windows: parent\evil. Elsewhere '\' is an ordinary file-name character, so this names the
        // file "out\..\evil" beside the root: a sibling prefix again. Outside on every platform.
        new("backslash-dotdot-suffix", Root, Root + "\\..\\evil", false, false),
        // Windows does not normalise a \\?\ path, so '..' survives GetFullPath and a prefix test would
        // read this as inside; the helper refuses a surviving dot segment. Elsewhere the prefix is
        // ordinary characters and this is the sibling "out\..\evil" again. Outside everywhere; only a
        // Windows run exercises the dot-segment leg.
        new("extended-length-dotdot", @"\\?\" + Root, @"\\?\" + Root + @"\..\evil", false, false),
        new("case-variant-of-root", Root, Root.ToUpperInvariant() + Sep + "x", CaseFoldingPlatform, CaseFoldingPlatform),
        new("unrelated-absolute", Root, Path.Combine(Path.GetPathRoot(Root)!, "elsewhere", "x"), false, false),
        new("filesystem-root-as-root", Path.GetPathRoot(Root)!, Path.Combine(Root, "child.txt"), true, true),
        new("empty-candidate", Root, "", false, false),
    ];

    public static TheoryData<string> CaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in Table)
            data.Add(c.Name);
        return data;
    }

    private static Case Row(string name) => Table.Single(c => c.Name == name);

    /// <summary>The arrange step must have produced the spelling it claims, or a row proves nothing.</summary>
    [Fact]
    public void The_table_is_what_it_says()
    {
        Table.Select(c => c.Name).Should().OnlyHaveUniqueItems();
        Path.IsPathRooted(Root).Should().BeTrue();
        Directory.Exists(Root).Should().BeFalse("the table is lexical; a real directory would hide a filesystem dependence");

        var caseVariant = Row("case-variant-of-root").Candidate;
        caseVariant.Should().NotStartWith(Root, "the case-variant row must differ from the root ordinally or it tests nothing");
        caseVariant.StartsWith(Root, StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        Row("sibling-prefix-directory").Candidate.Should().StartWith(Root, "a sibling-prefix row must share the root's characters, which is the whole attack");
        Row("sibling-prefix-file").Candidate.Should().StartWith(Root);
        Row("sibling-prefix-bare").Candidate.Should().StartWith(Root);
        Row("backslash-dotdot-suffix").Candidate.Should().StartWith(Root);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void IsWithin_answers_the_table(string name)
    {
        var c = Row(name);

        PathContainment.IsWithin(c.Candidate, c.RootSpelling).Should().Be(c.Inside,
            "{0}: '{1}' within '{2}'", c.Name, c.Candidate, c.RootSpelling);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void IsStrictlyWithin_answers_the_table(string name)
    {
        var c = Row(name);

        PathContainment.IsStrictlyWithin(c.Candidate, c.RootSpelling).Should().Be(c.StrictlyInside,
            "{0}: '{1}' strictly within '{2}'", c.Name, c.Candidate, c.RootSpelling);
    }

    [Fact]
    public void The_platform_comparison_is_the_default_file_system_case_rule()
    {
        PathContainment.PlatformComparison.Should().Be(
            CaseFoldingPlatform ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    [Fact]
    public void An_explicit_comparison_overrides_the_platform_rule_on_every_platform()
    {
        var caseVariant = Row("case-variant-of-root").Candidate;

        PathContainment.IsWithin(caseVariant, Root, StringComparison.Ordinal).Should().BeFalse();
        PathContainment.IsStrictlyWithin(caseVariant, Root, StringComparison.Ordinal).Should().BeFalse();
        PathContainment.IsWithin(caseVariant, Root, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
        PathContainment.IsStrictlyWithin(caseVariant, Root, StringComparison.OrdinalIgnoreCase).Should().BeTrue();

        // The separator rule does not depend on the comparison.
        var sibling = Row("sibling-prefix-directory").Candidate;
        PathContainment.IsWithin(sibling, Root, StringComparison.Ordinal).Should().BeFalse();
        PathContainment.IsWithin(sibling, Root, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Theory]
    [InlineData(StringComparison.CurrentCulture)]
    [InlineData(StringComparison.CurrentCultureIgnoreCase)]
    [InlineData(StringComparison.InvariantCulture)]
    [InlineData(StringComparison.InvariantCultureIgnoreCase)]
    public void A_culture_aware_comparison_is_refused(StringComparison comparison)
    {
        var child = Path.Combine(Root, "child.txt");

        var within = () => PathContainment.IsWithin(child, Root, comparison);
        var strictly = () => PathContainment.IsStrictlyWithin(child, Root, comparison);

        within.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("comparison");
        strictly.Should().Throw<ArgumentException>().Which.ParamName.Should().Be("comparison");
    }

    public static TheoryData<string?, string?> Unresolvable() => new()
    {
        { null, Root },
        { "", Root },
        { "   ", Root },
        { Path.Combine(Root, "child.txt"), null },
        { Path.Combine(Root, "child.txt"), "" },
        { Path.Combine(Root, "child.txt"), "   " },
        { Path.Combine(Root, "bad\0name"), Root },
        { Path.Combine(Root, "child.txt"), Root + "\0" },
    };

    /// <summary>A path that cannot be resolved is never inside anything, and asking never throws.</summary>
    [Theory]
    [MemberData(nameof(Unresolvable))]
    public void An_unresolvable_argument_is_never_within(string? candidate, string? root)
    {
        PathContainment.IsWithin(candidate, root).Should().BeFalse();
        PathContainment.IsStrictlyWithin(candidate, root).Should().BeFalse();
        PathContainment.IsWithin(candidate, root, StringComparison.Ordinal).Should().BeFalse();
        PathContainment.IsStrictlyWithin(candidate, root, StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    /// <summary>
    /// Pins what the helper deliberately does NOT do: it is lexical, so a link inside the root that
    /// points outside it reads as inside. A writer must probe for links itself, as
    /// <c>MediatedWritePath</c> does after its containment leg.
    /// </summary>
    [Fact]
    public void It_is_lexical_and_does_not_follow_a_link_out_of_the_root()
    {
        var stem = Path.Combine(Path.GetTempPath(), "ashlar-containment-link-" + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(stem, "root");
        var outside = Path.Combine(stem, "outside");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        try
        {
            var link = Path.Combine(root, "link");
            try
            {
                Directory.CreateSymbolicLink(link, outside);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return; // platform refuses unprivileged symlinks — nothing to assert
            }

            var throughLink = Path.Combine(link, "x.txt");
            File.WriteAllText(throughLink, "lands outside");
            File.Exists(Path.Combine(outside, "x.txt")).Should().BeTrue("the arrange step must really route a write outside the root");

            PathContainment.IsWithin(throughLink, root).Should().BeTrue(
                "containment is lexical by contract; this fact exists so nobody mistakes it for a link check");
        }
        finally
        {
            try { Directory.Delete(stem, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void OutputPathSandboxed_admits_exactly_the_table(string name)
    {
        var c = Row(name);
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?> { ["OutputRoot"] = c.RootSpelling });
        var call = new ToolCall("assembly.decompile", JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["output"] = c.Candidate }));

        var approved = new OutputPathSandboxed().Approve(call, snapshot, out var reason);

        approved.Should().Be(c.Inside, "{0}: output '{1}' against OutputRoot '{2}' ({3})", c.Name, c.Candidate, c.RootSpelling, reason);
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void PathAllowlist_absolute_leg_admits_exactly_the_table(string name)
    {
        var c = Row(name);
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?> { ["SandboxRoot"] = c.RootSpelling });
        var call = new ToolCall("repo.fs.write", JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["path"] = c.Candidate }));

        var approved = new PathAllowlist().Approve(call, snapshot, out var reason);

        approved.Should().Be(c.Inside, "{0}: path '{1}' against SandboxRoot '{2}' ({3})", c.Name, c.Candidate, c.RootSpelling, reason);
    }

    /// <summary>One model-supplied path, relative to the sandbox root, and whether it lands inside.</summary>
    public sealed record RelativeCase(string Name, string Candidate, bool Inside);

    /// <summary>
    /// The RELATIVE table, for the sites that resolve a model-supplied path against a sandbox root:
    /// <c>ToolSandbox.TryResolvePath</c> (the read resolver) and, through it, <c>repo.fs.read</c> and
    /// <c>repo.fs.list</c>; <c>ToolSandbox.TryResolveWritePath</c> for the escapes. The root is
    /// <c>&lt;stem&gt;/repo</c>, and every sibling row names something that really exists beside it
    /// with the root's name as a prefix (<c>repo-evil/</c>, <c>repox</c>, <c>repo.dll</c>).
    ///
    /// <para>This table exists because the absolute one could not see it: a separator-less helper
    /// put back into <c>ToolSandbox</c> (<c>here.StartsWith(there, PathContainment.PlatformComparison)</c>)
    /// left every existing suite green, and the read and list tools then read and listed the
    /// siblings. The write resolver stays shut even then, because <c>MediatedWritePath</c> checks
    /// containment again; its rows pin that it does.</para>
    /// </summary>
    public static IReadOnlyList<RelativeCase> RelativeTable { get; } =
    [
        new("child", "src/x.cs", true),
        new("root-itself", ".", true),
        new("dotdot-back-inside", "../repo/src/x.cs", true),
        new("parent", "..", false),
        new("sibling-prefix-directory", "../repo-evil", false),
        new("sibling-prefix-directory-child", "../repo-evil/x.cs", false),
        new("sibling-prefix-bare", "../repox", false),
        new("sibling-prefix-file", "../repo.dll", false),
        new("dotdot-to-sibling-through-a-child", "src/../../repo-evil/x.cs", false),
        new("case-variant-of-root", "../REPO/src/x.cs", CaseFoldingPlatform),
    ];

    public static TheoryData<string> RelativeCaseNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in RelativeTable)
            data.Add(c.Name);
        return data;
    }

    /// <summary>The rows that must be refused on this platform (the case variant only where case is significant).</summary>
    public static TheoryData<string> RelativeEscapeNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in RelativeTable.Where(c => !c.Inside))
            data.Add(c.Name);
        return data;
    }

    private static RelativeCase RelativeRow(string name) => RelativeTable.Single(c => c.Name == name);

    /// <summary>A sandbox at <c>&lt;stem&gt;/repo</c> with a file inside it and three siblings that extend its name.</summary>
    private sealed class RelativeSandbox : IDisposable
    {
        public const string Secret = "SECRET-beside-the-sandbox";

        private readonly string _stem;

        public RelativeSandbox()
        {
            _stem = Path.Combine(Path.GetTempPath(), "ashlar-containment-rel-" + Guid.NewGuid().ToString("N"));
            Root = Path.Combine(_stem, "repo");
            Directory.CreateDirectory(Path.Combine(Root, "src"));
            File.WriteAllText(Path.Combine(Root, "src", "x.cs"), "inside");
            Directory.CreateDirectory(Path.Combine(_stem, "repo-evil"));
            File.WriteAllText(Path.Combine(_stem, "repo-evil", "x.cs"), Secret);
            File.WriteAllText(Path.Combine(_stem, "repox"), Secret);
            File.WriteAllText(Path.Combine(_stem, "repo.dll"), Secret);
        }

        public string Root { get; }

        public WorldSnapshot Snapshot => WorldSnapshot.ForRepo(Root);

        public void Dispose()
        {
            try { Directory.Delete(_stem, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static ToolCall PathCall(string tool, string path)
        => new(tool, JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["path"] = path }));

    /// <summary>Every sibling row must really share the root's characters and really exist, or a refusal proves nothing.</summary>
    [Fact]
    public void The_relative_table_is_what_it_says()
    {
        using var sandbox = new RelativeSandbox();

        RelativeTable.Select(c => c.Name).Should().OnlyHaveUniqueItems();
        File.Exists(Path.Combine(sandbox.Root, "src", "x.cs")).Should().BeTrue();

        var siblings = RelativeTable.Where(c => c.Name.Contains("sibling", StringComparison.Ordinal)).ToList();
        siblings.Should().HaveCount(5);
        foreach (var c in siblings)
        {
            var resolved = Path.GetFullPath(Path.Combine(sandbox.Root, c.Candidate));
            resolved.Should().StartWith(sandbox.Root,
                "{0}: a sibling-prefix row must share the root's characters, which is the whole attack", c.Name);
            (File.Exists(resolved) || Directory.Exists(resolved)).Should().BeTrue(
                "{0}: '{1}' must really exist, or refusing it proves nothing", c.Name, resolved);
        }
    }

    [Theory]
    [MemberData(nameof(RelativeCaseNames))]
    public void ToolSandbox_read_resolver_admits_exactly_the_relative_table(string name)
    {
        var c = RelativeRow(name);
        using var sandbox = new RelativeSandbox();

        var admitted = ToolSandbox.TryResolvePath(sandbox.Snapshot, c.Candidate, out var full, out var reason);

        admitted.Should().Be(c.Inside, "{0}: '{1}' against RepoRoot '{2}' resolved to '{3}' ({4})",
            c.Name, c.Candidate, sandbox.Root, full, reason);
    }

    [Theory]
    [MemberData(nameof(RelativeEscapeNames))]
    public void ToolSandbox_write_resolver_refuses_every_escape_in_the_relative_table(string name)
    {
        var c = RelativeRow(name);
        using var sandbox = new RelativeSandbox();

        var admitted = ToolSandbox.TryResolveWritePath(sandbox.Snapshot, c.Candidate, out var full, out var reason);

        admitted.Should().BeFalse("{0}: '{1}' against RepoRoot '{2}' resolved to '{3}'", c.Name, c.Candidate, sandbox.Root, full);
        reason.Should().StartWith("REJECTED");
    }

    /// <summary>The control for the row above: the write resolver still admits an ordinary file.</summary>
    [Fact]
    public void ToolSandbox_write_resolver_admits_an_ordinary_child()
    {
        using var sandbox = new RelativeSandbox();

        ToolSandbox.TryResolveWritePath(sandbox.Snapshot, "src/x.cs", out var full, out var reason).Should().BeTrue(reason);
        full.Should().Be(Path.Combine(sandbox.Root, "src", "x.cs"));
    }

    [Theory]
    [MemberData(nameof(RelativeCaseNames))]
    public async Task RepoFsReadTool_reads_nothing_beside_the_sandbox(string name)
    {
        var c = RelativeRow(name);
        using var sandbox = new RelativeSandbox();

        var result = await new RepoFsReadTool().InvokeAsync(PathCall("repo.fs.read", c.Candidate), sandbox.Snapshot, CancellationToken.None);

        var payload = JsonSerializer.Serialize(result.Payload);
        result.Delta.Log.Any(l => l.Contains("REJECTED", StringComparison.Ordinal)).Should().Be(!c.Inside,
            "{0}: repo.fs.read of '{1}' ({2})", c.Name, c.Candidate, payload);
        payload.Should().NotContain(RelativeSandbox.Secret, "{0}: nothing beside the sandbox may be read", c.Name);
    }

    [Theory]
    [MemberData(nameof(RelativeCaseNames))]
    public async Task RepoFsListTool_lists_nothing_beside_the_sandbox(string name)
    {
        var c = RelativeRow(name);
        using var sandbox = new RelativeSandbox();

        var result = await new RepoFsListTool().InvokeAsync(PathCall("repo.fs.list", c.Candidate), sandbox.Snapshot, CancellationToken.None);

        var payload = JsonSerializer.Serialize(result.Payload);
        result.Delta.Log.Any(l => l.Contains("REJECTED", StringComparison.Ordinal)).Should().Be(!c.Inside,
            "{0}: repo.fs.list of '{1}' ({2})", c.Name, c.Candidate, payload);
        using var document = JsonDocument.Parse(payload);
        var listed = document.RootElement.TryGetProperty("entries", out var entries)
            ? entries.EnumerateArray().Select(e => e.GetProperty("path").GetString() ?? "").ToList()
            : new List<string>();
        listed.Should().NotContain(p => p.StartsWith("..", StringComparison.Ordinal),
            "{0}: nothing beside the sandbox may be listed ({1})", c.Name, payload);
    }
}
