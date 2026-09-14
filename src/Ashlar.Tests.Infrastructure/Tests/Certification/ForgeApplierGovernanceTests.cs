using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.HostRunners;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// The write floor (CLOSING-PLAN Phase 2). <see cref="ForgeApplier"/> is the single choke point
/// for every mediated write — a local self-extend cycle and an imported package both land there —
/// so a governance or build-integrity target must be refused no matter how it is spelled, and the
/// refusal must be for the WHOLE batch with nothing on disk.
///
/// <para><b>The bypass this pins shut.</b> Before Phase 2, governance was checked on the raw
/// <c>TargetPath</c> against a denylist keyed on the first path segment, so <c>./ashlar.policy.yaml</c>
/// (segments <c>[".", "ashlar.policy.yaml"]</c>) and <c>a/../.ashlar/x</c> slipped straight through
/// and resolved back onto the operator's own policy and gate state. The floor was also only three
/// entries wide, so an admitted <c>Directory.Build.targets</c> with an <c>&lt;Exec&gt;</c> ran on
/// the receiver's next <c>dotnet build</c> — outside the loader, the gate and the registry
/// entirely. This suite is table-driven so a new spelling or a new floor entry is one row.</para>
///
/// <para>In <c>...Tests.Certification</c> so it rides cert-gate, the required check
/// (ci/cert-gate-assertions.md). Hermetic: a temp dir, no build, no network.</para>
/// </summary>
[Trait("Category", "Certification")]
public sealed class ForgeApplierGovernanceTests : IDisposable
{
    private readonly string _repoRoot;
    private readonly string _forgeRoot;
    private readonly ChangeProposalStore _store;

    public ForgeApplierGovernanceTests()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "ashlar-forge-gov-" + Guid.NewGuid().ToString("N")[..12]);
        _repoRoot = Path.Combine(baseDir, "repo");
        _forgeRoot = Path.Combine(baseDir, "forge");
        Directory.CreateDirectory(_repoRoot);
        _store = new ChangeProposalStore(_forgeRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path.GetDirectoryName(_repoRoot)!, recursive: true); } catch { /* best effort */ }
    }

    // ---- The floor, as pure data. IsGovernancePath takes a normalized, root-relative path. -----

    public static TheoryData<string> GovernancePaths() => new()
    {
        // the two originals, now denied at any depth and any spelling
        "ashlar.yaml",
        "ashlar.policy.yaml",
        ".ashlar/gates/g1.json",
        ".ashlar/ledger.jsonl",
        // build files MSBuild/NuGet/SDK walk the tree for — dangerous at any depth
        "Directory.Build.props",
        "Directory.Build.targets",
        "Directory.Packages.props",
        "nested/dir/Directory.Build.targets",
        // MSBuild SOLUTION-level auto-imports — the write-floor's original miss (RCE on next `dotnet build *.sln`)
        "Directory.Solution.props",
        "Directory.Solution.targets",
        "after.Ashlar.sln.targets",
        "before.Ashlar.sln.targets",
        "build/custom.props",           // any <Import>ed .props/.targets, any depth
        "targets/x.targets",
        "nuget.config",
        "global.json",
        "Makefile",
        "GNUmakefile",                  // GNU make prefers it over Makefile; distinct name
        ".editorconfig",                // can set analyzer severity=none, silencing the repo's gates
        "src/.editorconfig",            // ...at any depth
        ".globalconfig",                // SDK-honoured analyzer config; silences gates like .editorconfig
        "src/.globalconfig",            // ...at any depth
        "dotnet-tools.json",            // .NET local-tool manifest, blocked by leaf name at ANY depth (root included)
        ".config/dotnet-tools.json",    // ...canonical location
        "nested/.config/dotnet-tools.json",
        ".pre-commit-config.yaml",      // `repo: local` hook runs on next git commit
        "src/Foo/Foo.csproj",
        "src/Foo/Foo.fsproj",
        "src/Foo/Foo.vbproj",
        "tools/x.proj",
        "Ashlar.sln",
        "Ashlar.slnx",
        // infrastructure directory prefixes
        ".git/config",
        ".github/workflows/ci.yml",
        ".vscode/tasks.json",
        ".devcontainer/devcontainer.json",
        "scripts/run-cert-gate.sh",
    };

    public static TheoryData<string> BenignPaths() => new()
    {
        "src/Feature/Handler.cs",
        "docs/guide.md",
        "README.md",
        "bricks/my-brick/logic.cs",
        // near-misses that must stay writable
        "src/scripts.cs",          // 'scripts' only as a prefix DIRECTORY is governance
        "notes/global.jsonc",      // not global.json
        "src/data.csproj.txt",     // not a .csproj
        "docs/solution.md",        // not a .props/.targets/.sln
        "src/editorconfig.cs",     // not .editorconfig
        "src/globalconfig.cs",     // not .globalconfig
        "config/dotnet-tools.json.bak",  // leaf differs (the .bak suffix), so not the manifest name
        "src/propshelper.cs",
    };

    [Theory]
    [MemberData(nameof(GovernancePaths))]
    public void IsGovernancePath_True_ForEveryFloorEntry(string path)
        => ForgeApplier.IsGovernancePath(path).Should().BeTrue($"'{path}' is on the governance/build floor");

    [Theory]
    [MemberData(nameof(BenignPaths))]
    public void IsGovernancePath_False_ForBenignPaths(string path)
        => ForgeApplier.IsGovernancePath(path).Should().BeFalse($"'{path}' is an ordinary content path");

    // ---- ApplyAll refuses through the choke point, whatever the spelling, and writes nothing ----

    public static TheoryData<string> RefusedTargets() => new()
    {
        "ashlar.policy.yaml",        // direct
        "./ashlar.policy.yaml",      // the dot-prefix bypass
        ".\\ashlar.policy.yaml",     // backslash spelling
        "ashlar.YAML",               // case
        "a/../.ashlar/steal.json",   // normalizes back into .ashlar
        "Directory.Build.targets",   // build-time code execution
        "Directory.Solution.targets",// solution-level auto-import (the adversarial finding)
        ".globalconfig",             // analyzer-severity silencer, SDK-honoured
        ".config/dotnet-tools.json", // local-tool manifest; a restored tool runs on next build
        "sub/dir/Directory.Build.props",
        "src/Evil.csproj",
        "../outside.txt",            // escapes the root entirely
    };

    [Theory]
    [MemberData(nameof(RefusedTargets))]
    public void ApplyAll_RefusesTheWholeBatch_AndWritesNothing(string target)
    {
        var id = Add(target, "malicious");

        var act = () => ForgeApplier.ApplyAll(_store, new[] { id }, _repoRoot, "test");

        act.Should().Throw<InvalidOperationException>();
        Directory.EnumerateFiles(_repoRoot, "*", SearchOption.AllDirectories)
            .Should().BeEmpty($"a refused batch targeting '{target}' must leave nothing on disk");
        _store.Find(id)!.Status.Should().Be(ChangeProposalStatus.Proposed,
            "a refused proposal is neither approved nor applied");
    }

    [Fact]
    public void ApplyAll_AppliesAnOrdinaryContentPath()
    {
        var id = Add("src/Feature/Handler.cs", "// generated");

        var applied = ForgeApplier.ApplyAll(_store, new[] { id }, _repoRoot, "test");

        applied.Should().ContainSingle().Which.Should().Be("src/Feature/Handler.cs");
        File.ReadAllText(Path.Combine(_repoRoot, "src", "Feature", "Handler.cs")).Should().Be("// generated");
        _store.Find(id)!.Status.Should().Be(ChangeProposalStatus.Applied);
    }

    [Fact]
    public void ApplyAll_OneBadTargetInABatch_FailsAllBeforeAnyWrite()
    {
        var good = Add("src/a.cs", "a");
        var bad = Add("Directory.Build.targets", "b");

        var act = () => ForgeApplier.ApplyAll(_store, new[] { good, bad }, _repoRoot, "test");

        act.Should().Throw<InvalidOperationException>();
        Directory.EnumerateFiles(_repoRoot, "*", SearchOption.AllDirectories)
            .Should().BeEmpty("validation runs over the whole batch before any file is written");
        _store.Find(good)!.Status.Should().Be(ChangeProposalStatus.Proposed);
    }

    // ---- The opt-in allowlist (sandbox.enforceWritableAllowlist) --------------------------------

    [Fact]
    public void ApplyAll_WithAllowlist_RefusesOutsideIt()
    {
        var id = Add("docs/guide.md", "x");

        var act = () => ForgeApplier.ApplyAll(_store, new[] { id }, _repoRoot, "test", new[] { "src" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*writable allowlist*");
        Directory.EnumerateFiles(_repoRoot, "*", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public void ApplyAll_WithAllowlist_DoesNotAdmitAPrefixSibling()
    {
        // 'src-evil/x' shares the textual prefix 'src' but is a different directory: an allowlist
        // entry of 'src' must not admit it (the boundary is 'src/', not 'src').
        var id = Add("src-evil/x.cs", "x");

        var act = () => ForgeApplier.ApplyAll(_store, new[] { id }, _repoRoot, "test", new[] { "src" });

        act.Should().Throw<InvalidOperationException>().WithMessage("*writable allowlist*");
    }

    [Fact]
    public void ApplyAll_WithMultiEntryAllowlist_ChecksEveryEntry()
    {
        // A miss on the first entry must not short-circuit to a refusal: the second entry admits it.
        var id = Add("docs/x.md", "x");

        var applied = ForgeApplier.ApplyAll(_store, new[] { id }, _repoRoot, "test", new[] { "src", "docs" });

        applied.Should().ContainSingle();
    }

    [Fact]
    public void ApplyAll_RefusesAWriteThroughALeafSymlink()
    {
        // Finding 3: a pre-planted leaf symlink docs/site.yaml -> ../ashlar.policy.yaml would, without
        // the leaf reparse check, be followed by File.WriteAllText and truncate the operator policy.
        var docs = Path.Combine(_repoRoot, "docs");
        Directory.CreateDirectory(docs);
        var link = Path.Combine(docs, "site.yaml");
        try
        {
            File.CreateSymbolicLink(link, Path.Combine("..", "ashlar.policy.yaml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // platform refuses unprivileged symlinks (e.g. Windows without dev mode) — nothing to assert
        }

        var id = Add("docs/site.yaml", "pwned");
        var act = () => ForgeApplier.ApplyAll(_store, new[] { id }, _repoRoot, "test");

        act.Should().Throw<InvalidOperationException>().WithMessage("*symlink*");
        File.Exists(Path.Combine(_repoRoot, "ashlar.policy.yaml"))
            .Should().BeFalse("the write must not have followed the link onto the operator policy");
    }

    [Fact]
    public void ApplyAll_WithAllowlist_AppliesInsideIt()
    {
        var id = Add("src/deep/x.cs", "x");

        var applied = ForgeApplier.ApplyAll(_store, new[] { id }, _repoRoot, "test", new[] { "src" });

        applied.Should().ContainSingle();
        File.Exists(Path.Combine(_repoRoot, "src", "deep", "x.cs")).Should().BeTrue();
    }

    [Fact]
    public void ApplyAll_NullAllowlist_LeavesTheFloorAsTheOnlyConstraint()
    {
        // The default (no opt-in): an ordinary path outside any 'src' is still writable.
        var id = Add("docs/guide.md", "x");

        var applied = ForgeApplier.ApplyAll(_store, new[] { id }, _repoRoot, "test", writableAllowlist: null);

        applied.Should().ContainSingle();
    }

    private string Add(string target, string content)
    {
        var id = "p-" + Guid.NewGuid().ToString("N")[..16];
        _store.Add(new ChangeProposal
        {
            Id = id,
            TargetPath = target,
            NewContent = content,
            Summary = "test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        return id;
    }
}
