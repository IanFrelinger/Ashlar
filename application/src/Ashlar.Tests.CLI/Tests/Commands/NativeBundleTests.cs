using System.Text.Json;
using Ashlar.CLI.Commands;
using Ashlar.Manifest;
using Ashlar.Manifest.Ledger;
using Ashlar.Manifest.Signing;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// Pins the deterministic half of `ashlar export native`: what a project IS (verified / certified)
/// and the portable bundle laid out around it — the launchers, the descriptor, the staged app.
/// The self-contained runtime build is a separate publish step, smoke-tested by the e2e loop.
/// </summary>
[Trait("Category", "CLI")]
public sealed class NativeBundleTests : IDisposable
{
    private readonly string _dir;
    private readonly string _keyDir;

    public NativeBundleTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "native-" + Guid.NewGuid().ToString("N"));
        _keyDir = Path.Combine(_dir, "keys");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private void Scaffold()
    {
        ProjectScaffold.TryScaffold("triage", out var m, out var p, out var reason).Should().BeTrue(reason);
        File.WriteAllText(Path.Combine(_dir, "ashlar.yaml"), m);
        File.WriteAllText(Path.Combine(_dir, "ashlar.policy.yaml"), p);
    }

    private async Task Certify()
    {
        var signer = OperatorKey.Generate(_keyDir);
        var m = File.ReadAllText(Path.Combine(_dir, "ashlar.yaml"));
        var p = File.ReadAllText(Path.Combine(_dir, "ashlar.policy.yaml"));
        await new InstanceLedger(Path.Combine(_dir, ".ashlar")).AppendVerificationAsync(
            signer, InstanceLedger.Subject(m, p), verified: true,
            [new LedgerCourse { Name = "contract", Passed = true, Detail = "ok" }], DateTimeOffset.UtcNow);
    }

    [Fact]
    public void Describe_reads_a_scaffolded_project_as_verified_but_uncertified()
    {
        Scaffold();

        var info = NativeBundle.Describe(_dir, "linux-x64");

        info.Name.Should().Be("triage");
        info.Verified.Should().BeTrue();
        info.Certified.Should().BeFalse("no signed ledger yet");
        info.SignerFingerprint.Should().BeNull();
        info.LedgerEntries.Should().Be(0);
    }

    [Fact]
    public async Task Describe_reads_a_certified_project_with_its_signer_and_ledger()
    {
        Scaffold();
        await Certify();

        var info = NativeBundle.Describe(_dir, "linux-x64");

        info.Certified.Should().BeTrue();
        info.SignerFingerprint.Should().StartWith("ed25519:");
        info.LedgerEntries.Should().Be(1);
    }

    [Fact]
    public async Task Stage_lays_out_a_self_proving_bundle()
    {
        Scaffold();
        await Certify();
        var info = NativeBundle.Describe(_dir, "linux-x64");
        var bundle = Path.Combine(_dir, "out");

        var written = NativeBundle.Stage(_dir, bundle, info);

        File.Exists(Path.Combine(bundle, "app", "ashlar.yaml")).Should().BeTrue();
        File.Exists(Path.Combine(bundle, "app", "ashlar.policy.yaml")).Should().BeTrue();
        Directory.Exists(Path.Combine(bundle, "app", ".ashlar")).Should().BeTrue("the signed ledger travels with the app");
        File.Exists(Path.Combine(bundle, "run.sh")).Should().BeTrue();
        File.Exists(Path.Combine(bundle, "run.cmd")).Should().BeTrue();
        File.Exists(Path.Combine(bundle, "README.md")).Should().BeTrue();
        written.Should().Contain("bundle.json");

        // The launcher verifies BEFORE it runs — that is the self-proving contract.
        var runSh = File.ReadAllText(Path.Combine(bundle, "run.sh"));
        var verifyAt = runSh.IndexOf("verify --path", StringComparison.Ordinal);
        var runAt = runSh.IndexOf("run \"$@\"", StringComparison.Ordinal);
        verifyAt.Should().BeGreaterThan(-1, "the launcher must verify");
        runAt.Should().BeGreaterThan(verifyAt, "verify must come before run");

        var descriptor = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(bundle, "bundle.json")));
        descriptor.GetProperty("format").GetString().Should().Be(NativeBundle.Format);
        descriptor.GetProperty("name").GetString().Should().Be("triage");
        descriptor.GetProperty("rid").GetString().Should().Be("linux-x64");
        descriptor.GetProperty("certified").GetBoolean().Should().BeTrue();
        descriptor.GetProperty("runtime").GetString().Should().Be("ashlar");
    }

    [Fact]
    public async Task Stage_never_ships_keys_forge_state_or_locks()
    {
        // SPEC-006's first rule: the private key never appears inside a bundle. An operator who
        // pointed ASHLAR_KEY_DIR inside the project (.ashlar/keys) must still be safe, and the
        // forge queue (raw held/rejected proposal content) is working state, not cargo.
        Scaffold();
        await Certify();
        var stateDir = Path.Combine(_dir, ".ashlar");
        Directory.CreateDirectory(Path.Combine(stateDir, "keys"));
        File.WriteAllText(Path.Combine(stateDir, "keys", "operator.key"), "SEED-MUST-NOT-TRAVEL");
        File.WriteAllText(Path.Combine(stateDir, "keys", "operator.pub"), "pub");
        Directory.CreateDirectory(Path.Combine(stateDir, "forge", "rejected"));
        File.WriteAllText(Path.Combine(stateDir, "forge", "rejected", "r1.json"), "{ \"NewContent\": \"secret draft\" }");
        File.WriteAllText(Path.Combine(stateDir, ".lock"), "");
        // src/ is inside the project too — an operator can point ASHLAR_KEY_DIR there just as
        // easily, and build output must not ride along either.
        Directory.CreateDirectory(Path.Combine(_dir, "src", "keys"));
        File.WriteAllText(Path.Combine(_dir, "src", "keys", "operator.key"), "SEED-MUST-NOT-TRAVEL");
        Directory.CreateDirectory(Path.Combine(_dir, "src", "bin"));
        File.WriteAllText(Path.Combine(_dir, "src", "bin", "junk.dll"), "not cargo");
        File.WriteAllText(Path.Combine(_dir, "src", "brick.cs"), "// legit cargo");
        var bundle = Path.Combine(_dir, "out-secrets");

        var written = NativeBundle.Stage(_dir, bundle, NativeBundle.Describe(_dir, "linux-x64"));

        Directory.Exists(Path.Combine(bundle, "app", ".ashlar", "keys")).Should().BeFalse("the private key must never travel");
        Directory.Exists(Path.Combine(bundle, "app", ".ashlar", "forge")).Should().BeFalse("forge state is not cargo");
        File.Exists(Path.Combine(bundle, "app", ".ashlar", ".lock")).Should().BeFalse();
        Directory.Exists(Path.Combine(bundle, "app", "src", "keys")).Should().BeFalse("keys under src/ must not travel either");
        Directory.Exists(Path.Combine(bundle, "app", "src", "bin")).Should().BeFalse("build output is not cargo");
        File.Exists(Path.Combine(bundle, "app", "src", "brick.cs")).Should().BeTrue("real source still travels");
        written.Should().NotContain(p => p.Contains("operator.key"));
        Directory.Exists(Path.Combine(bundle, "app", ".ashlar", "ledger")).Should().BeTrue("the signed ledger DOES travel");
    }

    [Fact]
    public async Task Stage_refuses_a_symlink_inside_the_project()
    {
        // A link defeats every name-based filter: it can pull any file on the machine — including
        // a private key — into the bundle under an innocent name. Same rule as ForgeApplier on the
        // write side: refuse, and teach.
        Scaffold();
        await Certify();
        var outside = Path.Combine(Path.GetTempPath(), "outside-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllText(outside, "smuggled");
        try
        {
            Directory.CreateDirectory(Path.Combine(_dir, "src"));
            try
            {
                File.CreateSymbolicLink(Path.Combine(_dir, "src", "innocent.cs"), outside);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return; // creating symlinks needs privilege on Windows — the Linux CI lanes cover this path
            }

            Action act = () => NativeBundle.Stage(_dir, Path.Combine(_dir, "out-link"), NativeBundle.Describe(_dir, "linux-x64"));

            act.Should().Throw<InvalidOperationException>().WithMessage("*symlink or junction*");
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Fact]
    public void Stage_uses_the_windows_exe_name_and_launcher_for_a_win_rid()
    {
        Scaffold();
        var info = NativeBundle.Describe(_dir, "win-x64");
        var bundle = Path.Combine(_dir, "out-win");

        NativeBundle.Stage(_dir, bundle, info);

        var descriptor = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(bundle, "bundle.json")));
        descriptor.GetProperty("runtime").GetString().Should().Be("ashlar.exe");
        File.ReadAllText(Path.Combine(bundle, "run.cmd")).Should().Contain("ashlar.exe");
    }

    [Theory]
    [InlineData("linux-x64", "run.sh")]
    [InlineData("linux-x64", "run.cmd")]
    [InlineData("win-x64", "run.sh")]
    [InlineData("win-x64", "run.cmd")]
    public async Task Stage_native_launchers_verify_read_only_and_stop_on_failure(string rid, string launcher)
    {
        Scaffold();
        await Certify();
        var bundle = Path.Combine(_dir, "out-read-only");
        NativeBundle.Stage(_dir, bundle, NativeBundle.Describe(_dir, rid));

        // This checks generated templates, not execution of a shell or Windows process.
        var script = File.ReadAllText(Path.Combine(bundle, launcher)).Replace("\r\n", "\n");
        var verifyLine = script.Split('\n').Single(line => line.Contains(" verify --path ", StringComparison.Ordinal));
        verifyLine.Should().EndWith(" --read-only", "even an available local key must not recertify a launch");
        var verifyAt = script.IndexOf(verifyLine, StringComparison.Ordinal);
        var runToken = launcher == "run.sh" ? " run \"$@\" --path " : " run %* --path ";
        var runAt = script.IndexOf(runToken, StringComparison.Ordinal);
        verifyAt.Should().BeGreaterThan(-1);
        runAt.Should().BeGreaterThan(verifyAt, "verification must finish before the request runs");

        if (launcher == "run.sh")
        {
            var stopAt = script.IndexOf("\nset -e\n", StringComparison.Ordinal);
            stopAt.Should().BeGreaterThan(-1);
            stopAt.Should().BeLessThan(verifyAt, "a failed verifier must stop the POSIX launcher");
        }
        else
        {
            var stopAt = script.IndexOf("if errorlevel 1 exit /b %errorlevel%", StringComparison.Ordinal);
            stopAt.Should().BeGreaterThan(verifyAt);
            stopAt.Should().BeLessThan(runAt, "a failed verifier must stop the Windows launcher");
        }

        script.Should().Contain("verification passed.").And.NotContain("certified and ready")
            .And.NotContain("verified and ready");
    }

}
