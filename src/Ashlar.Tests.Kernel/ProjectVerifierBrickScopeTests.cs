using FluentAssertions;
using Ashlar.Manifest;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// The defect: <c>ashlar verify</c> printed CERTIFIED and signed a ledger entry for a project with
/// no source code at all, whose declared brick existed nowhere on disk. Two enforcement points were
/// absent, and both absences read as a pass — the composition course never resolved a brick, and
/// no verdict ever named what it covered.
///
/// <para>These pin both halves: a declared brick must resolve or the course fails by name, and
/// every verification reports the scope it earned.</para>
/// </summary>
public sealed class ProjectVerifierBrickScopeTests : IDisposable
{
    private readonly string _dir;

    public ProjectVerifierBrickScopeTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "brickscope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static (string manifest, string policy) Scaffolded()
    {
        ProjectScaffold.TryScaffold("brick-demo", out var m, out var p, out var reason)
            .Should().BeTrue(reason);
        return (m, p);
    }

    private static string WithBrick(string manifest, string id, string version = "0.1.0") =>
        manifest.Replace("bricks: []", $"bricks:\n  - id: {id}\n    version: {version}");

    [Fact]
    public void A_declared_brick_that_exists_nowhere_fails_composition()
    {
        var (m, p) = Scaffolded();

        var result = ProjectVerifier.Verify(WithBrick(m, "does-not-exist"), p, _dir);

        result.Verified.Should().BeFalse(
            "a certification cannot cover a dependency that is not present — this exact project, "
            + "a bare `ashlar init` plus one edited line, used to print CERTIFIED and sign a ledger entry");
        var composition = result.Courses.Single(c => c.Name == "composition");
        composition.Passed.Should().BeFalse();
        composition.Detail.Should().Contain("does-not-exist");
    }

    [Fact]
    public void The_refusal_names_the_fix_and_where_it_looked()
    {
        // The delight bar: a refusal names the FIX, not just the fault. A brick author reading
        // this must be able to act on it without opening the source.
        var (m, p) = Scaffolded();

        var detail = ProjectVerifier.Verify(WithBrick(m, "invoice-classifier"), p, _dir)
            .Courses.Single(c => c.Name == "composition").Detail;

        detail.Should().Contain("InvoiceClassifier", "the refusal spells out the file names it looked for");
        detail.Should().Contain("InvoiceClassifierBrick.cs");
        detail.Should().Contain("bricks:", "it names the manifest list to delete the entry from");
        detail.Should().Contain("Add the brick's source");
    }

    [Fact]
    public void A_brick_backed_by_a_matching_file_resolves()
    {
        // The course must WORK, not merely refuse: a brick that is actually here still certifies.
        var (m, p) = Scaffolded();
        File.WriteAllText(Path.Combine(_dir, "InvoiceClassifierBrick.cs"), "// brick\n");

        var result = ProjectVerifier.Verify(WithBrick(m, "invoice-classifier"), p, _dir);

        result.Verified.Should().BeTrue(string.Join(" | ", result.Courses.Select(c => c.Detail)));
        result.Courses.Single(c => c.Name == "composition").Detail.Should().Contain("1/1");
        result.Scope.ResolvedBricks.Should().Be(1);
    }

    [Fact]
    public void A_brick_backed_by_a_directory_of_source_resolves()
    {
        var (m, p) = Scaffolded();
        var brickDir = Path.Combine(_dir, "src", "invoice-classifier");
        Directory.CreateDirectory(brickDir);
        File.WriteAllText(Path.Combine(brickDir, "Anything.cs"), "// brick\n");

        ProjectVerifier.Verify(WithBrick(m, "invoice-classifier"), p, _dir).Verified.Should().BeTrue();
    }

    [Fact]
    public void An_empty_directory_named_after_the_brick_does_not_resolve_it()
    {
        // A folder is not an implementation. Accepting one would reopen the hole from the other side.
        var (m, p) = Scaffolded();
        Directory.CreateDirectory(Path.Combine(_dir, "src", "invoice-classifier"));

        ProjectVerifier.Verify(WithBrick(m, "invoice-classifier"), p, _dir).Verified.Should().BeFalse();
    }

    [Fact]
    public void Build_output_cannot_resolve_a_brick()
    {
        // A stale obj/ copy of a deleted brick would otherwise certify a composition standing on a
        // build artefact.
        var (m, p) = Scaffolded();
        var objDir = Path.Combine(_dir, "obj", "Debug");
        Directory.CreateDirectory(objDir);
        File.WriteAllText(Path.Combine(objDir, "InvoiceClassifierBrick.cs"), "// stale\n");

        ProjectVerifier.Verify(WithBrick(m, "invoice-classifier"), p, _dir).Verified.Should().BeFalse();
    }

    [Fact]
    public void A_project_with_no_source_says_so_in_its_scope()
    {
        var (m, p) = Scaffolded();

        var scope = ProjectVerifier.Verify(m, p, _dir).Scope;

        scope.CoversCode.Should().BeFalse();
        scope.SourceFiles.Should().Be(0);
        scope.Summary.Should().Contain("no source code");
        scope.Summary.Should().Contain("ONLY");
    }

    [Fact]
    public void A_project_with_source_names_what_the_verdict_covers()
    {
        var (m, p) = Scaffolded();
        File.WriteAllText(Path.Combine(_dir, "Program.cs"), "// code\n");

        var scope = ProjectVerifier.Verify(m, p, _dir).Scope;

        scope.CoversCode.Should().BeTrue();
        scope.SourceFiles.Should().Be(1);
        scope.Summary.Should().Contain("1 source file");
    }

    [Fact]
    public void A_verdict_that_could_not_load_the_contract_still_names_its_scope()
    {
        // Scope is never null: a caller must always be able to say what a verdict was about,
        // including when the answer is "we never got that far".
        var result = ProjectVerifier.Verify("not: a manifest", "also not a policy", _dir);

        result.Verified.Should().BeFalse();
        result.Scope.Should().NotBeNull();
        result.Scope.CoversCode.Should().BeFalse();
    }
}
