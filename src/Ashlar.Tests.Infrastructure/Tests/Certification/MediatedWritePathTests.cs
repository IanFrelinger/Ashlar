using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// <see cref="MediatedWritePath"/> is the single governance-floor authority every mediated writer
/// (forge apply, package import, shared-adaptation adopt) routes through. These assertions pin the
/// floor directly, so a regression is caught here regardless of which writer calls it. In
/// <c>...Tests.Certification</c> so it rides cert-gate (ci/cert-gate-assertions.md). Hermetic:
/// pure string logic plus a temp dir for the containment cases.
/// </summary>
[Trait("Category", "Certification")]
public sealed class MediatedWritePathTests : IDisposable
{
    private readonly string _root;

    public MediatedWritePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ashlar-mwp-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    public static TheoryData<string> GovernancePaths() => new()
    {
        "ashlar.yaml", "ashlar.policy.yaml",
        ".ashlar/gates/g1.json", ".ashlar/ledger.jsonl",
        "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props",
        "Directory.Solution.props", "Directory.Solution.targets",
        "after.Ashlar.sln.targets", "before.Ashlar.sln.targets",
        "nested/dir/Directory.Build.targets", "build/custom.props", "x/y/z.targets",
        "nuget.config", "global.json", "Makefile", "GNUmakefile",
        ".editorconfig", "src/.editorconfig", ".pre-commit-config.yaml",
        ".globalconfig", ".config/dotnet-tools.json",
        "src/Foo/Foo.csproj", "src/Foo/Foo.fsproj", "src/Foo/Foo.vbproj", "tools/x.proj",
        "Ashlar.sln", "Ashlar.slnx",
        ".git/config", ".github/workflows/ci.yml", ".vscode/tasks.json",
        ".devcontainer/devcontainer.json", "scripts/run.sh",
    };

    public static TheoryData<string> BenignPaths() => new()
    {
        "src/Feature/Handler.cs", "docs/guide.md", "README.md", "bricks/b/logic.cs",
        "src/scripts.cs", "notes/global.jsonc", "src/data.csproj.txt",
        "docs/solution.md", "src/editorconfig.cs", "src/propshelper.cs",
    };

    /// <summary>
    /// The paths the AUTHORING edge must still be able to write. A self-extend cycle scaffolds
    /// projects — MockScaffoldingResponder's UI demo emits four repo.fs.write calls for .csproj
    /// files under docs/UiDomainDemoGenerated, and SelfExtendWorkflowSpec.UiSmokeProjectPath
    /// depends on one of them existing — so project and solution files are governance for a
    /// MEDIATED write and authorable directly.
    /// </summary>
    public static TheoryData<string> GovernanceButAuthorablePaths() => new()
    {
        "src/Foo/Foo.csproj", "src/Foo/Foo.fsproj", "src/Foo/Foo.vbproj", "tools/x.proj",
        "Ashlar.sln", "Ashlar.slnx",
        "docs/UiDomainDemoGenerated/avalonia/Ashlar.Ui.AvaloniaHost/Ashlar.Ui.AvaloniaHost.csproj",
    };

    /// <summary>
    /// Governance on BOTH edges. Build imports are ancestor-discovered, so one dropped anywhere
    /// above a project changes how that project builds; .ashlar/ is the admission ledger that
    /// bounds how often a cycle may extend the system, and a cycle that can write it has no budget.
    /// </summary>
    public static TheoryData<string> GovernanceOnBothEdgesPaths() => new()
    {
        "ashlar.yaml", "ashlar.policy.yaml",
        ".ashlar/gates/g1.json", ".ashlar/ledger.jsonl",
        ".ashlar/tools/cache/nuget/evil/analyzers/dotnet/cs/A.dll",
        ".ashlar/host_apps/projects/a.cs",
        ".ashlar/agents/workspaces/w/x.cs",
        "Directory.Build.props", "build/custom.props", "x/y/z.targets",
        "after.Ashlar.sln.targets",
        "nuget.config", "global.json", "Makefile",
        "src/.editorconfig", "src/.globalconfig", ".config/dotnet-tools.json",
        ".git/config", ".github/workflows/ci.yml", "scripts/run.sh",
    };

    [Theory]
    [MemberData(nameof(GovernanceOnBothEdgesPaths))]
    public void The_authoring_floor_refuses_everything_governance_on_both_edges(string path)
    {
        MediatedWritePath.IsAuthoringGovernancePath(path).Should().BeTrue(path);
        MediatedWritePath.IsGovernancePath(path).Should().BeTrue(path);
    }

    [Theory]
    [MemberData(nameof(GovernanceButAuthorablePaths))]
    public void A_project_file_is_mediated_governance_but_authorable_directly(string path)
    {
        MediatedWritePath.IsGovernancePath(path).Should().BeTrue(path);
        MediatedWritePath.IsAuthoringGovernancePath(path).Should().BeFalse(path);
    }

    /// <summary>
    /// THE FREEZE. The authoring floor must be a SUBSET of the mediated floor: anything the
    /// mediated edge lets through, the authoring edge must also let through, and every authoring
    /// refusal must also be a mediated refusal.
    ///
    /// <para>This is the assertion that stops the one mutation that matters. The tempting way to
    /// let a cycle scaffold a .csproj is to delete it from the mediated list — which silently
    /// weakens the cert-gated forge floor and the package-import and shared-adaptation floors at
    /// the same time, while looking like a change to the tool edge. IsGovernancePath is written as
    /// IsAuthoringGovernancePath plus a project-file leg precisely so that cannot happen quietly,
    /// and this test fails if someone unpicks that nesting and lets the two lists drift.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(GovernancePaths))]
    [MemberData(nameof(BenignPaths))]
    [MemberData(nameof(SafePaths))]
    [MemberData(nameof(GovernanceButAuthorablePaths))]
    [MemberData(nameof(GovernanceOnBothEdgesPaths))]
    public void The_authoring_floor_is_a_subset_of_the_mediated_floor(string path)
    {
        if (MediatedWritePath.IsAuthoringGovernancePath(path))
        {
            MediatedWritePath.IsGovernancePath(path).Should().BeTrue(
                $"'{path}' is refused at the authoring edge, so the mediated edge must refuse it too "
                + "— the authoring floor may never be wider than the mediated one");
        }
    }

    /// <summary>
    /// The two floors differ on project and solution files and on NOTHING else. Stated as an
    /// exhaustive difference rather than as a list of examples, so adding a third asymmetry
    /// requires editing this assertion and saying why.
    /// </summary>
    [Theory]
    [MemberData(nameof(GovernancePaths))]
    [MemberData(nameof(BenignPaths))]
    [MemberData(nameof(GovernanceButAuthorablePaths))]
    [MemberData(nameof(GovernanceOnBothEdgesPaths))]
    public void The_only_difference_between_the_floors_is_a_project_or_solution_file(string path)
    {
        var mediated = MediatedWritePath.IsGovernancePath(path);
        var authoring = MediatedWritePath.IsAuthoringGovernancePath(path);
        if (mediated == authoring)
        {
            return;
        }

        var leaf = path.Split('/')[^1];
        var isProjectOrSolution =
            leaf.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".fsproj", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".vbproj", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".proj", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".sln", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

        isProjectOrSolution.Should().BeTrue(
            $"'{path}' is treated differently by the two floors but is not a project or solution "
            + "file; a new asymmetry needs a reason recorded here");
    }

    public static TheoryData<string> UnsafePaths() => new()
    {
        "", "   ", ".", "..", "a/../b", "./a", "/rooted", "\\rooted",
        "C:/x", "a:b", "x::$DATA", "a/./b", "trail. /x", "x/con", "nul", "COM1", "a/b.",
    };

    public static TheoryData<string> SafePaths() => new()
    {
        "a", "a/b/c.cs", "src/Feature/Handler.cs", "config/settings.json", "console.cs",
    };

    [Theory]
    [MemberData(nameof(GovernancePaths))]
    public void IsGovernancePath_True(string p) => MediatedWritePath.IsGovernancePath(p).Should().BeTrue(p);

    [Theory]
    [MemberData(nameof(BenignPaths))]
    public void IsGovernancePath_False(string p) => MediatedWritePath.IsGovernancePath(p).Should().BeFalse(p);

    [Theory]
    [MemberData(nameof(UnsafePaths))]
    public void IsSafeRelativePath_False(string p) => MediatedWritePath.IsSafeRelativePath(p).Should().BeFalse(p);

    [Theory]
    [MemberData(nameof(SafePaths))]
    public void IsSafeRelativePath_True(string p) => MediatedWritePath.IsSafeRelativePath(p).Should().BeTrue(p);

    [Fact]
    public void Refuse_AllowsAnOrdinaryContentPath()
        => MediatedWritePath.Refuse(_root, "src/Feature/x.cs").Should().BeNull();

    [Theory]
    [InlineData("ashlar.policy.yaml")]
    [InlineData("./ashlar.policy.yaml")]
    [InlineData("a/../.ashlar/steal.json")]
    [InlineData("Directory.Solution.targets")]
    [InlineData("../outside.txt")]
    public void Refuse_RejectsGovernanceAndEscapes(string target)
        => MediatedWritePath.Refuse(_root, target).Should().NotBeNull();

    [Fact]
    public void Refuse_Allowlist_RejectsOutside_AdmitsInside_AndHonoursEveryEntry()
    {
        MediatedWritePath.Refuse(_root, "docs/x.md", new[] { "src" }).Should().Contain("allowlist");
        MediatedWritePath.Refuse(_root, "src-evil/x.cs", new[] { "src" }).Should().Contain("allowlist"); // prefix sibling
        MediatedWritePath.Refuse(_root, "src/deep/x.cs", new[] { "src" }).Should().BeNull();
        MediatedWritePath.Refuse(_root, "docs/x.md", new[] { "src", "docs" }).Should().BeNull();       // 2nd entry admits
    }

    /// <summary>
    /// The authoring floor is the mediated floor with one predicate swapped: a project file
    /// passes, and every other leg — the ledger, the ADS colon, the escape — refuses with the
    /// same vocabulary, because there is one private core rather than a second copy.
    /// </summary>
    [Fact]
    public void RefuseAuthoringWrite_AdmitsAProjectFile_AndRefusesEverythingElseTheMediatedFloorDoes()
    {
        MediatedWritePath.RefuseAuthoringWrite(_root, "src/Foo/Foo.csproj").Should().BeNull();
        MediatedWritePath.Refuse(_root, "src/Foo/Foo.csproj").Should().Contain("governance");

        MediatedWritePath.RefuseAuthoringWrite(_root, "src/Feature/x.cs").Should().BeNull();
        MediatedWritePath.RefuseAuthoringWrite(_root, ".ashlar/gates/g1.json").Should().Contain("governance");
        MediatedWritePath.RefuseAuthoringWrite(_root, "src/build/Directory.Build.props").Should().Contain("governance");
        MediatedWritePath.RefuseAuthoringWrite(_root, "src/x.cs:evil").Should().Contain("':'");
        MediatedWritePath.RefuseAuthoringWrite(_root, "../outside.txt").Should().Contain("escapes");
        MediatedWritePath.RefuseAuthoringWrite(_root, "src/./x.cs").Should().Contain("not a safe");
    }

    [Fact]
    public void Refuse_RejectsAWriteThroughALeafSymlink()
    {
        var docs = Path.Combine(_root, "docs");
        Directory.CreateDirectory(docs);
        try
        {
            File.CreateSymbolicLink(Path.Combine(docs, "site.yaml"), Path.Combine("..", "ashlar.policy.yaml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // platform refuses unprivileged symlinks — nothing to assert
        }
        MediatedWritePath.Refuse(_root, "docs/site.yaml").Should().Contain("symlink");
    }
}
