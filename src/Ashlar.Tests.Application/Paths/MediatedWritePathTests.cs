using Ashlar.Core.Application.Paths;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.Application.Paths;

/// <summary>
/// Unit coverage for <see cref="MediatedWritePath"/>, the single authority for whether a write may
/// land under a repository root.
///
/// <para>WHY HERE, WHEN THE FLOOR IS ALSO TESTED IN <c>Ashlar.Tests.Infrastructure</c>. Those
/// assertions live there so they ride <c>cert-gate</c>, whose filter matches only that assembly's
/// <c>Tests.Certification</c> namespace. The consequence went unnoticed until this class was
/// extended: <c>kernel-coverage</c> measures <c>Ashlar.Core.Application</c> using
/// <c>Ashlar.Tests.Application</c> ALONE, and against that project every method of this type —
/// the governance predicate, the containment legs, the reparse probes — had a line rate of zero.
/// The most security-critical type in the assembly was carried entirely by another assembly's
/// tests, and the floor percentage it did not contribute to was the only thing that noticed.</para>
///
/// <para>These are not a copy of the Infrastructure assertions. Those pin the FLOOR as a contract
/// — every governance spelling, the subset relationship between the two edges. These drive the
/// branches of this type from the assembly that owns it, so the number the coverage gate reads
/// reflects tests that actually exercise it.</para>
/// </summary>
[Trait("Category", "ProdStyle")]
public sealed class MediatedWritePathTests : IDisposable
{
    private readonly string _root;

    public MediatedWritePathTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ashlar-mwp-app-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    // ── the two governance predicates ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ashlar.yaml")]                       // contract, root only
    [InlineData("ashlar.policy.yaml")]                // operator policy, root only
    [InlineData(".ashlar/gates/g1.json")]             // the admission ledger
    [InlineData(".git/config")]
    [InlineData(".github/workflows/ci.yml")]
    [InlineData("scripts/run.sh")]
    [InlineData("Directory.Build.props")]
    [InlineData("nested/deep/custom.targets")]        // build imports bite at any depth
    [InlineData("nuget.config")]
    [InlineData("global.json")]
    [InlineData("Makefile")]
    [InlineData("src/.editorconfig")]
    [InlineData(".config/dotnet-tools.json")]
    public void Both_edges_refuse_governance_and_build_tooling(string path)
    {
        MediatedWritePath.IsAuthoringGovernancePath(path).Should().BeTrue(path);
        MediatedWritePath.IsGovernancePath(path).Should().BeTrue(path);
    }

    [Theory]
    [InlineData("src/Foo/Foo.csproj")]
    [InlineData("tools/x.proj")]
    [InlineData("Ashlar.sln")]
    [InlineData("Ashlar.slnx")]
    public void A_project_file_is_mediated_governance_but_authorable(string path)
    {
        MediatedWritePath.IsGovernancePath(path).Should().BeTrue(path);
        MediatedWritePath.IsAuthoringGovernancePath(path).Should().BeFalse(path);
    }

    [Theory]
    [InlineData("src/Feature/Handler.cs")]
    [InlineData("docs/guide.md")]
    [InlineData("src/data.csproj.txt")]               // suffix match is on the leaf, not a substring
    [InlineData("notes/global.jsonc")]
    [InlineData("nested/ashlar.yaml")]                // contract is governance at the ROOT only
    public void Neither_edge_refuses_ordinary_content(string path)
    {
        MediatedWritePath.IsGovernancePath(path).Should().BeFalse(path);
        MediatedWritePath.IsAuthoringGovernancePath(path).Should().BeFalse(path);
    }

    // ── the safe-shape predicate ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("/rooted")]
    [InlineData("C:/x")]
    [InlineData("a:b")]                               // NTFS alternate data stream
    [InlineData("x/con")]                             // Win32 reserved device, denied on every OS
    [InlineData("COM1")]
    [InlineData("a/b.")]                              // Win32 strips a trailing dot, aliasing files
    [InlineData("trail /x")]
    public void An_unsafe_shape_is_refused(string path)
        => MediatedWritePath.IsSafeRelativePath(path).Should().BeFalse(path);

    [Theory]
    [InlineData("a")]
    [InlineData("a/b/c.cs")]
    [InlineData("console.cs")]                        // reserved-stem match is exact, not a prefix
    public void A_safe_shape_is_admitted(string path)
        => MediatedWritePath.IsSafeRelativePath(path).Should().BeTrue(path);

    // ── Refuse: the mediated edge ────────────────────────────────────────────────────────────

    [Fact]
    public void Refuse_admits_an_ordinary_content_path()
        => MediatedWritePath.Refuse(_root, "src/Feature/x.cs").Should().BeNull();

    [Theory]
    [InlineData("", "empty")]
    [InlineData("a:b", "drive letter")]
    [InlineData("/rooted.cs", "rooted")]
    [InlineData("../outside.cs", "escapes")]
    [InlineData("a/../.ashlar/steal.json", "governance")]   // judged on the RESOLVED form
    [InlineData("./ashlar.policy.yaml", "governance")]
    [InlineData("src/con.cs", "safe repo-relative path")]
    public void Refuse_names_the_truest_reason(string target, string expected)
        => MediatedWritePath.Refuse(_root, target).Should().NotBeNull().And.Subject.ToString()
            .Should().Contain(expected);

    [Fact]
    public void Refuse_honours_a_writable_allowlist()
    {
        MediatedWritePath.Refuse(_root, "docs/x.md", new[] { "src" }).Should().Contain("allowlist");
        MediatedWritePath.Refuse(_root, "src-evil/x.cs", new[] { "src" }).Should().Contain("allowlist");
        MediatedWritePath.Refuse(_root, "src/deep/x.cs", new[] { "src" }).Should().BeNull();
        MediatedWritePath.Refuse(_root, "docs/x.md", new[] { "src", "docs" }).Should().BeNull();
    }

    [Fact]
    public void Refuse_rejects_a_project_file_on_the_mediated_edge()
        => MediatedWritePath.Refuse(_root, "src/New/New.csproj").Should().Contain("governance");

    // ── RefuseAuthoringWrite: the tool edge ──────────────────────────────────────────────────

    [Fact]
    public void RefuseAuthoringWrite_admits_a_project_file()
        => MediatedWritePath.RefuseAuthoringWrite(_root, "src/New/New.csproj").Should().BeNull();

    [Theory]
    [InlineData(".ashlar/gates/forged.json")]
    [InlineData("docs/../.ashlar/gates/forged.json")]
    [InlineData("src/build/Directory.Build.props")]
    [InlineData("src/.globalconfig")]
    [InlineData("../outside.cs")]
    [InlineData("src/x.cs:stream")]
    public void RefuseAuthoringWrite_refuses_everything_the_authoring_floor_covers(string target)
        => MediatedWritePath.RefuseAuthoringWrite(_root, target).Should().NotBeNull(target);

    [Fact]
    public void A_leaf_symlink_cannot_carry_a_write_out_of_the_floor()
    {
        var docs = Path.Combine(_root, "docs");
        Directory.CreateDirectory(docs);
        try
        {
            File.CreateSymbolicLink(Path.Combine(docs, "site.yaml"), Path.Combine("..", "ashlar.policy.yaml"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            return; // the platform refuses unprivileged symlinks; nothing to assert
        }

        MediatedWritePath.Refuse(_root, "docs/site.yaml").Should().Contain("symlink");
        MediatedWritePath.RefuseAuthoringWrite(_root, "docs/site.yaml").Should().Contain("symlink");
    }
}
