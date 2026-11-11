using System.CommandLine;
using FluentAssertions;
using Ashlar.CLI.Commands;
using Ashlar.Manifest;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// The staged auto-apply opt-in verb: <c>ashlar policy set self_extend &lt;mode&gt;</c> (the command the
/// gate refusal and the scaffold already tell operators to run). Pins that it flips ONLY the mode,
/// preserves the rest of the policy, validates the result before writing (fail-closed), and refuses
/// anything that would widen the governance floor.
/// </summary>
public sealed class PolicyCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string? _previousKeyDir;

    public PolicyCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-policy-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _previousKeyDir = Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR");
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", Path.Combine(_dir, "no-ambient-keys"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", _previousKeyDir);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private string WritePolicy(string mode, string gates = "[sandbox]", string extra = "", int extensions = 3)
    {
        var yaml = $"""
            apiVersion: ashlar/v1
            kind: Policy
            sandbox:
              root: .
              writable: []
            selfExtend:
              # raise the dial deliberately
              mode: {mode}{extra}
              budget:
                extensions: {extensions}
                window: 24h
              mayAdd: [brick]
              gatesRequired: {gates}
            never:
              - modify_gate
              - widen_sandbox
              - access_signing_keys
              - truncate_ledger
              - grant_capability
            """;
        var path = Path.Combine(_dir, "ashlar.policy.yaml");
        File.WriteAllText(path, yaml);
        return path;
    }

    private (int rc, string stdout, string stderr) Set(string key, string value)
    {
        var so = new StringWriter();
        var se = new StringWriter();
        var rc = PolicyCommand.Set(key, value, _dir, so, se);
        return (rc, so.ToString(), se.ToString());
    }

    private string CurrentMode(string path)
    {
        PolicyLoader.TryLoad(File.ReadAllText(path), out var p, out _).Should().BeTrue();
        return p!.SelfExtend.Mode;
    }

    // ---- --format-json ------------------------------------------------------------------------

    /// <summary>
    /// Runs through the REAL root command. <c>--format-json</c> is declared there as a global option,
    /// and that registration is the only reason the flag reaches `policy show` at all — a bare
    /// RootCommand would reject the token and the test would prove nothing.
    /// </summary>
    private static async Task<(int rc, string stdout, string stderr)> RunCliAsync(params string[] args)
    {
        var so = new StringWriter();  // not disposed: a disposed writer left on Console poisons later tests
        var se = new StringWriter();
        Console.SetOut(so);
        Console.SetError(se);
        try
        {
            var rc = await Ashlar.CLI.Program.BuildRootCommand().InvokeAsync(args).ConfigureAwait(false);
            return (rc, so.ToString(), se.ToString());
        }
        finally
        {
            // Restore to the known-good writers, never the (possibly foreign) inherited ones.
            Console.SetOut(ConsoleCapture.Out);
            Console.SetError(ConsoleCapture.Error);
        }
    }

    [Fact]
    public async Task Show_withFormatJson_refuses_insteadOfPrintingProseOnStdout()
    {
        // `policy show` has no JSON rendering, but --format-json is a global option so it parses
        // anyway. A caller piping this into a parser used to get exit 0 and eight lines of prose — a
        // green light above a broken pipeline. The refusal is what makes that failure visible.
        WritePolicy("proposing");

        var (rc, stdout, stderr) = await RunCliAsync("policy", "show", "--path", _dir, "--format-json");

        rc.Should().NotBe(0, "a flag that cannot be honoured must not exit 0");
        stderr.Should().Contain("--format-json");
        stdout.Should().NotContain("self-extend mode", "prose must not reach a caller that asked for JSON");
    }

    [Fact]
    public async Task Show_withoutFormatJson_stillPrintsTheProse()
    {
        // The refusal is scoped to the flag — the ordinary human path must be untouched by it. This
        // one passes against the pre-fix code by design: it is the guard rail against over-refusing,
        // and it goes red if the refusal is ever pushed down into Show() itself.
        WritePolicy("proposing");

        var (rc, stdout, _) = await RunCliAsync("policy", "show", "--path", _dir);

        rc.Should().Be(0);
        stdout.Should().Contain("self-extend mode : proposing");
    }

    [Fact]
    public async Task Set_withFormatJson_refuses_andWritesNothing()
    {
        // The write half of the same defect: an operator's script reads exit 0 and believes it
        // parsed a result, while the dial moved underneath it. Refusing before the edit also
        // guarantees the policy is left exactly as it was.
        var path = WritePolicy("sealed");
        var before = await File.ReadAllTextAsync(path);

        var (rc, _, stderr) = await RunCliAsync("policy", "set", "self_extend", "proposing", "--path", _dir, "--format-json");

        rc.Should().NotBe(0);
        stderr.Should().Contain("--format-json");
        (await File.ReadAllTextAsync(path)).Should().Be(before, "a refused invocation must not edit the policy");
    }

    [Theory]
    [InlineData("abc", 1)]
    [InlineData("''", 1)]
    [InlineData("null", 1)]
    [InlineData("[24h]", 1)]
    [InlineData("{ hours: 24 }", 1)]
    [InlineData("2147483647d", 1)]
    [InlineData("-1h", 1)]
    [InlineData("24h", -1)]
    [InlineData(null, 1)]
    public async Task Invalid_budget_is_rejected_by_show_set_and_verify_without_writes(string? window, int extensions)
    {
        ProjectScaffold.TryScaffold("budget-demo", out var manifest, out _, out var reason).Should().BeTrue(reason);
        File.WriteAllText(Path.Combine(_dir, "ashlar.yaml"), manifest);
        var path = WritePolicy("sealed", extensions: extensions);
        var yaml = File.ReadAllText(path).Replace("window: 24h", window is null ? "" : "window: " + window);
        File.WriteAllText(path, yaml);

        var show = await RunCliAsync("policy", "show", "--path", _dir);
        show.rc.Should().Be(1);
        show.stderr.Should().Contain("REJECTED:");
        var set = await RunCliAsync("policy", "set", "self_extend", "self-extending", "--path", _dir);
        set.rc.Should().Be(1);
        set.stderr.Should().Contain("REJECTED:");
        var verify = await RunCliAsync("verify", "--path", _dir);
        verify.rc.Should().Be(65);
        (verify.stdout + verify.stderr).Should().Contain("REJECTED:");
        File.ReadAllText(path).Should().Be(yaml);
        Directory.Exists(Path.Combine(_dir, ".ashlar")).Should().BeFalse("invalid policies cannot be certified or recorded");
    }

    [Fact]
    public void Set_sealedToProposing_flipsTheMode()
    {
        var path = WritePolicy("sealed");

        var (rc, stdout, _) = Set("self_extend", "proposing");

        rc.Should().Be(0);
        stdout.Should().Contain("sealed → proposing");
        CurrentMode(path).Should().Be("proposing");
    }

    [Fact]
    public void Set_toSelfExtending_arms_andWarns()
    {
        var path = WritePolicy("proposing");

        var (rc, stdout, _) = Set("self_extend", "self-extending");

        rc.Should().Be(0);
        stdout.Should().Contain("ARMED").And.Contain("AUTO-APPLY");
        CurrentMode(path).Should().Be("self-extending");
    }

    [Fact]
    public void Set_toProposing_withBudgetZero_succeeds_butWarnsLoudly()
    {
        // #460: arming `proposing` with budget.extensions == 0 admits nothing, and `ashlar verify`
        // fails the envelope course on exactly that state. The write must still succeed (the mode did
        // change), but the operator must be warned — silently landing them on verify's red wall is the
        // failure this guards. Mirrors the self-extending budget-0 note.
        var path = WritePolicy("sealed", extensions: 0);

        var (rc, stdout, _) = Set("self_extend", "proposing");

        rc.Should().Be(0, "the mode flip itself is valid and must be written");
        CurrentMode(path).Should().Be("proposing");
        stdout.Should().Contain("WARNING").And.Contain("budget is 0");
        stdout.Should().Contain("verify", "the warning must name what will flag it");
    }

    [Fact]
    public void Set_toProposing_withFundedBudget_doesNotWarn()
    {
        // The warning is specific to budget 0 — a funded budget must not cry wolf.
        WritePolicy("sealed", extensions: 3);

        var (rc, stdout, _) = Set("self_extend", "proposing");

        rc.Should().Be(0);
        stdout.Should().NotContain("WARNING");
    }

    [Fact]
    public void Set_toSelfExtending_withNoGates_isRefused_andWritesNothing()
    {
        // sealed + [] gates loads fine; flipping it to self-extending would need gates, so it must be refused.
        var path = WritePolicy("sealed", gates: "[]");
        var before = File.ReadAllText(path);

        var (rc, _, stderr) = Set("self_extend", "self-extending");

        rc.Should().Be(1);
        stderr.Should().Contain("gatesRequired");
        File.ReadAllText(path).Should().Be(before, "a flip that would make the policy invalid must write nothing");
        CurrentMode(path).Should().Be("sealed");
    }

    [Fact]
    public void Set_unknownMode_isRejected()
    {
        var path = WritePolicy("sealed");
        var (rc, _, stderr) = Set("self_extend", "yolo");
        rc.Should().Be(1);
        stderr.Should().Contain("unknown mode");
        CurrentMode(path).Should().Be("sealed");
    }

    [Fact]
    public void Set_unsupportedKey_isRejected_soTheFloorStaysReadOnly()
    {
        WritePolicy("sealed");
        var (rc, _, stderr) = Set("never", "[]");
        rc.Should().Be(1);
        stderr.Should().Contain("self_extend");
        stderr.Should().Contain("not editable");
    }

    [Fact]
    public void Set_alreadyAtTarget_isIdempotentNoOp()
    {
        var path = WritePolicy("proposing");
        var before = File.ReadAllText(path);

        var (rc, stdout, _) = Set("self_extend", "proposing");

        rc.Should().Be(0);
        stdout.Should().Contain("already");
        File.ReadAllText(path).Should().Be(before, "an idempotent set changes nothing on disk");
    }

    [Fact]
    public void Set_disarmBackToSealed_worksEvenWithGatesPresent()
    {
        var path = WritePolicy("self-extending");
        var (rc, _, _) = Set("self_extend", "sealed");
        rc.Should().Be(0);
        CurrentMode(path).Should().Be("sealed");
    }

    [Fact]
    public void Set_preservesCommentsAndOtherFields()
    {
        var path = WritePolicy("sealed");

        Set("self_extend", "proposing").rc.Should().Be(0);

        var after = File.ReadAllText(path);
        after.Should().Contain("# raise the dial deliberately", "comments must survive the edit");
        after.Should().Contain("extensions: 3").And.Contain("mayAdd: [brick]");
        after.Should().Contain("- modify_gate", "the never-list must be untouched");
    }

    [Fact]
    public void Show_printsTheMode()
    {
        WritePolicy("proposing");
        var so = new StringWriter();
        var rc = PolicyCommand.Show(_dir, so, new StringWriter());
        rc.Should().Be(0);
        so.ToString().Should().Contain("self-extend mode : proposing");
    }

    [Fact]
    public void Set_withDuplicateModeKey_refuses_ratherThanSilentlyLeavingItArmed()
    {
        // A policy with TWO selfExtend.mode entries: YAML resolves last-wins (effective = self-extending),
        // but a naive textual edit would change the FIRST (proposing). Disarming to sealed must NOT
        // silently succeed while the node stays armed — it must refuse and write nothing.
        var path = Path.Combine(_dir, "ashlar.policy.yaml");
        File.WriteAllText(path, """
            apiVersion: ashlar/v1
            kind: Policy
            sandbox:
              root: .
              writable: []
            selfExtend:
              mode: proposing
              budget:
                extensions: 3
                window: 24h
              mayAdd: [brick]
              gatesRequired: [sandbox]
              mode: self-extending
            never:
              - modify_gate
              - widen_sandbox
              - access_signing_keys
              - truncate_ledger
              - grant_capability
            """);
        var before = File.ReadAllText(path);

        var (rc, _, stderr) = Set("self_extend", "sealed");

        rc.Should().Be(1, "a set that cannot be trusted to take effect must refuse");
        File.ReadAllText(path).Should().Be(before, "nothing may be written when the outcome can't be guaranteed");
        stderr.Should().NotBeNullOrWhiteSpace();
    }

    // ---- pure edit logic ----------------------------------------------------------------------

    [Fact]
    public void TryReplace_scopesToSelfExtend_andLeavesADecoyModeUntouched()
    {
        // A `mode:` under a different block must NOT be rewritten — only selfExtend's.
        var text = "sandbox:\n  mode: keepme\nselfExtend:\n  mode: sealed\n  budget:\n    extensions: 1\n";

        PolicyCommand.TryReplaceSelfExtendMode(text, "proposing", out var result).Should().BeTrue();

        result.Should().Contain("mode: keepme", "the decoy under sandbox is out of scope");
        result.Should().Contain("selfExtend:\n  mode: proposing");
    }

    [Fact]
    public void TryReplace_returnsFalse_whenNoSelfExtendBlock()
    {
        PolicyCommand.TryReplaceSelfExtendMode("sandbox:\n  root: .\n", "proposing", out _)
            .Should().BeFalse();
    }

    [Fact]
    public void TryReplace_preservesCrlfNewlines()
    {
        var text = "selfExtend:\r\n  mode: sealed\r\n  mayAdd: [brick]\r\n";
        PolicyCommand.TryReplaceSelfExtendMode(text, "proposing", out var result).Should().BeTrue();
        result.Should().Be("selfExtend:\r\n  mode: proposing\r\n  mayAdd: [brick]\r\n");
    }
}
