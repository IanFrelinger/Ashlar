using FluentAssertions;
using Ashlar.Manifest;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;
using Xunit;

namespace Ashlar.Tests.Kernel;

/// <summary>
/// Pins the courses <c>ashlar verify</c> runs: the three that always run (contract, composition,
/// envelope), and the <c>provenance</c> course that joins ONLY once a signed instance ledger
/// exists — checking that history's chain fail-closed, so a tampered ledger fails verification.
/// </summary>
public sealed class ProjectVerifierTests : IDisposable
{
    private readonly string _dir;

    public ProjectVerifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "verify-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private (string manifest, string policy) Scaffolded()
    {
        ProjectScaffold.TryScaffold("verify-demo", out var m, out var p, out var reason)
            .Should().BeTrue(reason);
        return (m, p);
    }

    [Fact]
    public void A_freshly_scaffolded_project_verifies()
    {
        // The init -> verify loop must hold: what init writes, verify accepts.
        var (m, p) = Scaffolded();

        var result = ProjectVerifier.Verify(m, p, _dir);

        result.Verified.Should().BeTrue(string.Join(" | ", result.Courses.Select(c => c.Detail)));
        result.Courses.Select(c => c.Name).Should().Equal("contract", "composition", "envelope");
    }

    [Fact]
    public void A_project_with_no_ledger_has_no_provenance_course()
    {
        // Presence-activated: a keyless, never-certified project stays at three courses and the
        // caller renders it unsigned — provenance appears only once there is a signed history.
        var (m, p) = Scaffolded();

        var result = ProjectVerifier.Verify(m, p, _dir);

        result.Courses.Should().NotContain(c => c.Name == "provenance");
    }

    [Fact]
    public async Task A_clean_ledger_adds_a_passing_provenance_course()
    {
        var (m, p) = Scaffolded();
        var signer = OperatorKey.Generate(Path.Combine(_dir, "keys"));
        var ledger = new InstanceLedger(Path.Combine(_dir, ".ashlar"));
        await ledger.AppendVerificationAsync(signer, InstanceLedger.Subject(m, p), verified: true,
            [new LedgerCourse { Name = "contract", Passed = true, Detail = "ok" }], DateTimeOffset.UtcNow);

        var result = ProjectVerifier.Verify(m, p, _dir);

        result.Verified.Should().BeTrue();
        result.Courses.Should().Contain(c => c.Name == "provenance" && c.Passed);
        result.Courses.Single(c => c.Name == "provenance").Detail.Should().Contain("chain intact");
    }

    [Fact]
    public async Task Editing_the_documents_after_certification_fails_provenance()
    {
        // The integrity binding: the signed ledger attests specific bytes. A project certified for
        // documents D, then edited to D' (still structurally valid), has an intact chain whose head
        // covers D — not D'. That must fail provenance, or "certified" would mean nothing about the
        // contract you are actually running.
        var (m, p) = Scaffolded();
        var signer = OperatorKey.Generate(Path.Combine(_dir, "keys"));
        var ledger = new InstanceLedger(Path.Combine(_dir, ".ashlar"));
        await ledger.AppendVerificationAsync(signer, InstanceLedger.Subject(m, p), verified: true,
            [new LedgerCourse { Name = "contract", Passed = true, Detail = "ok" }], DateTimeOffset.UtcNow);

        // A comment keeps the manifest valid (courses 1-3 still pass) but changes its bytes.
        var edited = m + "\n# a harmless-looking edit after signing\n";

        var result = ProjectVerifier.Verify(edited, p, _dir);

        result.Courses.Where(c => c.Name != "provenance").Should().OnlyContain(c => c.Passed,
            "the edit is structurally valid — only provenance should catch it");
        var provenance = result.Courses.Single(c => c.Name == "provenance");
        provenance.Passed.Should().BeFalse();
        provenance.Detail.Should().Contain("do not match the certification");
        result.Verified.Should().BeFalse("a project whose documents are not the certified ones is not verified");
    }

    [Fact]
    public async Task A_corrupt_ledger_fails_verification_via_the_provenance_course()
    {
        var (m, p) = Scaffolded();
        var signer = OperatorKey.Generate(Path.Combine(_dir, "keys"));
        var ledger = new InstanceLedger(Path.Combine(_dir, ".ashlar"));
        await ledger.AppendVerificationAsync(signer, InstanceLedger.Subject(m, p), verified: true,
            [new LedgerCourse { Name = "contract", Passed = true, Detail = "ok" }], DateTimeOffset.UtcNow);

        // Corrupt the single entry on disk: the provenance course must catch it and fail the whole
        // verification, so a run over a forged history is refused — the ledger is part of "verified".
        var entryFile = Path.Combine(_dir, ".ashlar", "ledger", "000001.json");
        await File.WriteAllTextAsync(entryFile, "{ not a valid ledger entry");

        var result = ProjectVerifier.Verify(m, p, _dir);

        result.Verified.Should().BeFalse("a tampered ledger fails verification");
        result.Courses.Single(c => c.Name == "provenance").Passed.Should().BeFalse();
    }

    [Fact]
    public void Broken_contract_fails_fast_with_the_loader_reason()
    {
        var (_, p) = Scaffolded();

        var result = ProjectVerifier.Verify("kind: Nonsense", p, _dir);

        result.Verified.Should().BeFalse();
        result.Courses.Should().ContainSingle();
        result.Courses[0].Name.Should().Be("contract");
        result.Courses[0].Detail.Should().Contain("REJECTED");
    }

    [Fact]
    public void An_ungated_agent_fails_composition()
    {
        var (m, p) = Scaffolded();
        m = m.Replace("gates: [tests]", "gates: []");

        var result = ProjectVerifier.Verify(m, p, _dir);

        result.Verified.Should().BeFalse();
        result.Courses.Single(c => c.Name == "composition").Detail.Should().Contain("no gates");
    }

    [Fact]
    public void A_missing_sandbox_root_fails_the_envelope()
    {
        var (m, p) = Scaffolded();
        p = p.Replace("root: .", "root: does-not-exist");

        var result = ProjectVerifier.Verify(m, p, _dir);

        result.Verified.Should().BeFalse();
        result.Courses.Single(c => c.Name == "envelope").Detail.Should().Contain("does not exist");
    }

    [Fact]
    public void A_writable_path_escaping_the_root_fails_the_envelope()
    {
        var (m, p) = Scaffolded();
        p = p.Replace("writable: []", "writable: [../outside]");

        var result = ProjectVerifier.Verify(m, p, _dir);

        result.Verified.Should().BeFalse();
        result.Courses.Single(c => c.Name == "envelope").Detail.Should().Contain("escapes");
    }

    [Fact]
    public void An_admitting_mode_with_a_zero_budget_fails_the_envelope()
    {
        var (m, p) = Scaffolded();
        // The scaffold now ships FUNDED terms (so `policy set self_extend proposing` works on a
        // fresh project — see ProjectScaffoldTests). Defund it explicitly, then raise the mode.
        p = p.Replace("mode: sealed", "mode: proposing")
             .Replace("extensions: 1", "extensions: 0");

        var result = ProjectVerifier.Verify(m, p, _dir);

        result.Verified.Should().BeFalse(
            "a mode that can admit extensions with budget 0 can never admit anything");
        result.Courses.Single(c => c.Name == "envelope").Detail.Should().Contain("seal it or fund it");
    }

    [Fact]
    public void Sealed_with_zero_budget_is_fine_because_it_admits_nothing()
    {
        var (m, p) = Scaffolded();
        p = p.Replace("extensions: 1", "extensions: 0");

        ProjectVerifier.Verify(m, p, _dir).Verified.Should().BeTrue();
    }
}
