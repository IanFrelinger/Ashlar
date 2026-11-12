using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Ashlar.CLI.Commands;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;
using Ashlar.Tests.CLI.Helpers;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// #478: old decision bytes remain valid evidence, not instructions to the operator's terminal.
/// Fixtures bypass the new-decision API deliberately: historical actors must remain readable.
/// </summary>
[Collection("MeshIntegration")]
public sealed class GateDecisionDisplaySecurityTests : IDisposable
{
    private const string Controls = "\u001b]0;forged\u0007\r\nFORGED-ROW\u009b\u061c\u202e\u2066\u2028\u2029";
    private const string Human = "Zoë Jose\u0301 李 👩🏽‍💻 سمیرا";
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "ashlar-gate-display-" + Guid.NewGuid().ToString("N"));
    private readonly string? _previousKeys = Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR");
    private string StateRoot => Path.Combine(_dir, ".ashlar");
    private string RecordPath => Path.Combine(StateRoot, "gates", "historical.json");

    public GateDecisionDisplaySecurityTests()
    {
        Directory.CreateDirectory(Path.Combine(StateRoot, "gates"));
        File.WriteAllText(Path.Combine(_dir, "ashlar.yaml"), "kind: Application\n");
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", Path.Combine(_dir, "keys"));
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", _previousKeys);
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Hostile(string label) => label + " " + Human + Controls + new string('x', 2400);

    private static GateRecord Historical(ProposalState state) => new()
    {
        Proposal = new ExtensionProposal
        {
            Id = "historical", Kind = Hostile("kind"), Summary = Hostile("summary"),
            ProposedBy = Hostile("proposer"), ProposedAt = Now,
            Courses = [new CourseResult { Name = Hostile("course"), Passed = true, Detail = Hostile("detail") }],
            Diff = "+ " + Human + "\u001b]0;forged\u0007\r\u202e\u2028\n- ordinary second line",
        },
        State = state, Actor = Hostile("actor"), Reason = Hostile("reason"), DecidedAt = Now,
    };

    private async Task<GateRecord> SeedAsync(GateRecord record, bool signed)
    {
        if (signed)
        {
            var signer = OperatorKey.Generate();
            record = record with { Sig = signer.Sign(CanonicalJson.Bytes(record)), Signer = signer.PublicKeyBase64 };
        }
        await File.WriteAllTextAsync(RecordPath, JsonSerializer.Serialize(record, Json));
        return record;
    }

    private static async Task<(int Code, string Output)> RunAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var root = new RootCommand { new GatesCommand(), new PkgCommand() };
            var code = await root.InvokeAsync(args);
            // Strip only the CLI's trusted colour wrapper. OSC, CSI and other hostile bytes stay.
            return (code, Regex.Replace(output.ToString() + error.ToString(), "\u001b\\[[0-9;]+m", string.Empty));
        }
        finally { Console.SetOut(ConsoleCapture.Out); Console.SetError(ConsoleCapture.Error); }
    }

    private static void AssertSafe(string output)
    {
        foreach (var c in new[] { '\u001b', '\u0007', '\u009b', '\u061c', '\u202e', '\u2066', '\u2028', '\u2029' })
            output.Should().NotContain(c.ToString(), "stored text cannot control or reorder the terminal");
        output.Should().NotContain("\nFORGED-ROW").And.NotContain("\rFORGED-ROW");
        output.Length.Should().BeLessThan(18000, "each displayed historical field must be bounded");
    }

    private async Task AssertUnchangedAsync(byte[] bytes, GateRecord record)
    {
        (await File.ReadAllBytesAsync(RecordPath)).Should().Equal(bytes);
        var reread = await new GateStore(StateRoot).GetAsync("historical");
        CanonicalJson.Bytes(reread!).Should().Equal(CanonicalJson.Bytes(record));
        reread!.Actor.Should().Be(record.Actor, "display escaping never edits historical identity bytes");
        if (record.Sig is not null)
            OperatorKey.Verify(record.Signer!, CanonicalJson.Bytes(record with { Sig = null, Signer = null }), record.Sig)
                .Should().BeTrue("the unchanged historical signature must still verify");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task List_historical_held_fields_are_safe_without_rewriting_evidence(bool signed)
    {
        var record = await SeedAsync(Historical(ProposalState.Held), signed);
        var bytes = await File.ReadAllBytesAsync(RecordPath);

        var (code, output) = await RunAsync("gates", "--path", _dir);

        code.Should().Be(0);
        AssertSafe(output);
        output.Should().Contain(Human).And.Contain("truncated:");
        output.Split('\n').Length.Should().BeLessThan(15, "historical fields cannot manufacture rows");
        await AssertUnchangedAsync(bytes, record);
    }

    [Theory]
    [InlineData(ProposalState.Held, false)]
    [InlineData(ProposalState.Admitted, false)]
    [InlineData(ProposalState.Refused, false)]
    [InlineData(ProposalState.Rejected, false)]
    [InlineData(ProposalState.Held, true)]
    [InlineData(ProposalState.Admitted, true)]
    [InlineData(ProposalState.Refused, true)]
    [InlineData(ProposalState.Rejected, true)]
    public async Task Show_historical_fields_are_safe_and_signed_bytes_are_unchanged(ProposalState state, bool signed)
    {
        var record = await SeedAsync(Historical(state), signed);
        var bytes = await File.ReadAllBytesAsync(RecordPath);

        var (code, output) = await RunAsync("gates", "--show", "historical", "--path", _dir);

        code.Should().Be(0);
        AssertSafe(output);
        output.Should().Contain(Human).And.Contain("truncated:").And.Contain("- ordinary second line");
        output.Split('\n').Length.Should().BeLessThan(24, "only the diff owns multiline layout");
        await AssertUnchangedAsync(bytes, record);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Human_decision_confirmation_escapes_fields_but_preserves_accepted_unicode_and_reason(bool admit)
    {
        await SeedAsync(Historical(ProposalState.Held), signed: true);
        var reason = "reviewed " + Human + Controls;

        var (code, output) = await RunAsync("gates", admit ? "--admit" : "--refuse", "historical",
            "--as", Human, "--reason", reason, "--path", _dir);

        code.Should().Be(0);
        AssertSafe(output);
        output.Should().Contain(Human).And.Contain("truncated:");
        var decided = await new GateStore(StateRoot).GetAsync("historical");
        decided!.Actor.Should().Be(Human);
        decided.Reason.Should().Be(reason, "safe presentation cannot change the decision's signed reason");
        decided.State.Should().Be(admit ? ProposalState.Admitted : ProposalState.Refused);
        decided.Sig.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Historical_diff_preserves_its_lines_but_bounds_total_output(bool manyLines)
    {
        var historical = Historical(ProposalState.Held);
        var diff = "+ " + Human + "\n- ordinary second line\n"
            + (manyLines ? string.Concat(Enumerable.Repeat("x\n", 10000)) : new string('x', 30000))
            + "UNRENDERED-DIFF-TAIL";
        var record = await SeedAsync(historical with
        {
            Proposal = historical.Proposal with
            {
                Kind = "brick", Summary = "ordinary summary", ProposedBy = Human,
                Courses = [], Diff = diff,
            },
            Reason = "awaiting review",
        }, signed: true);
        var bytes = await File.ReadAllBytesAsync(RecordPath);

        var (code, output) = await RunAsync("gates", "--show", "historical", "--path", _dir);

        code.Should().Be(0);
        AssertSafe(output);
        output.Should().Contain("+ " + Human).And.Contain("- ordinary second line").And.Contain("truncated:");
        output.Should().NotContain("UNRENDERED-DIFF-TAIL");
        output.Length.Should().BeLessThan(10000, "a historical diff is bounded as a whole, not just per line");
        output.Split('\n').Length.Should().BeLessThan(100, "even short diff lines must have an overall row bound");
        await AssertUnchangedAsync(bytes, record);
    }

    [Theory]
    [InlineData("gate")]
    [InlineData("human\nFORGED-ROW")]
    public async Task Invalid_human_actor_is_refused_without_changing_the_held_record(string actor)
    {
        var record = await SeedAsync(Historical(ProposalState.Held), signed: true);
        var bytes = await File.ReadAllBytesAsync(RecordPath);

        var (code, output) = await RunAsync("gates", "--refuse", "historical", "--as", actor,
            "--reason", "reviewed", "--path", _dir);

        code.Should().NotBe(0);
        AssertSafe(output);
        await AssertUnchangedAsync(bytes, record);
    }

    [Fact]
    public async Task Gate_read_error_cannot_echo_a_terminal_control_from_the_requested_id()
    {
        var (code, output) = await RunAsync("gates", "--show", "invalid" + Controls, "--path", _dir);

        code.Should().NotBe(0);
        AssertSafe(output);
        output.Should().Contain("Illegal proposal id");
    }

    [Theory]
    [InlineData("admit")]
    [InlineData("export")]
    [InlineData("share")]
    public async Task Historical_gate_read_error_is_safe_in_gate_and_package_commands(string operation)
    {
        var record = await SeedAsync(Historical(operation == "admit" ? ProposalState.Held : ProposalState.Admitted), signed: true);
        var bytes = await File.ReadAllBytesAsync(RecordPath);
        var corruptPath = Path.Combine(StateRoot, "gates", "corrupt-\u202e-forged.json");
        await File.WriteAllTextAsync(corruptPath, "null");
        string[] args = operation switch
        {
            "admit" => new[] { "gates", "--admit", "historical", "--as", Human, "--path", _dir },
            "export" => ["pkg", "export", "--id", "historical", "--out", Path.Combine(_dir, "out.ashpkg"), "--path", _dir],
            _ => ["pkg", "share", "--id", "historical", "--store", Path.Combine(_dir, "mesh"), "--path", _dir],
        };

        var (code, output) = await RunAsync(args);

        code.Should().Be(1);
        AssertSafe(output);
        output.Should().Contain("Corrupt gate record").And.Contain("corrupt-�-forged.json");
        File.Exists(Path.Combine(_dir, "out.ashpkg")).Should().BeFalse();
        Directory.Exists(Path.Combine(_dir, "mesh")).Should().BeFalse();
        (await File.ReadAllBytesAsync(RecordPath)).Should().Equal(bytes);
        (await File.ReadAllTextAsync(corruptPath)).Should().Be("null");
        OperatorKey.Verify(record.Signer!, CanonicalJson.Bytes(record with { Sig = null, Signer = null }), record.Sig!)
            .Should().BeTrue();
    }
}
