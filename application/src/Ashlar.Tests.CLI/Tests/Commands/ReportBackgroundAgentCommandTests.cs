using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Ashlar.BackgroundAgents.Telemetry;
using Ashlar.CLI.Commands.BackgroundAgent;
using Ashlar.Manifest;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;
using System.Text.Json;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

/// <summary>
/// A4 overnight report: joins cycle activity (cycles.jsonl) with admission-gate outcomes
/// (&lt;project&gt;/.ashlar/gates) over a window. Pins the activity roll-up, the held/rejected outcome
/// counts, and graceful degradation when there are no gate records.
/// </summary>
[Collection("MeshIntegration")]
public sealed class ReportBackgroundAgentCommandTests : IDisposable
{
    private readonly string _dir;
    private readonly string _project;
    private readonly CycleEventStore _cycles;
    private readonly string? _previousKeys = Environment.GetEnvironmentVariable("ASHLAR_KEY_DIR");
    private readonly string _keys;

    public ReportBackgroundAgentCommandTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ashlar-report-tests-" + Guid.NewGuid().ToString("N"));
        _project = Path.Combine(_dir, "project");
        Directory.CreateDirectory(_project);
        _cycles = new CycleEventStore(Path.Combine(_dir, "cycles.jsonl"));
        _keys = Path.Combine(_dir, "keys");
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", _keys);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("ASHLAR_KEY_DIR", _previousKeys);
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private ReportBackgroundAgentCommand NewCmd() =>
        new(_cycles, NullLogger<ReportBackgroundAgentCommand>.Instance);

    private static async Task<string> RunAsync(ReportBackgroundAgentCommand cmd, double? since, string? project)
    {
        var sb = new StringWriter();
        var rc = await cmd.ExecuteAsync(since, project, formatJson: false, sb, TextWriter.Null);
        rc.Should().Be(0);
        return sb.ToString();
    }

    private void SeedCycle(string agent, bool success) =>
        _cycles.Append(new CycleEvent(
            ts: DateTimeOffset.UtcNow, agent: agent, cycle: 1, role: "extender",
            duration_ms: 100, iterations: 2, tools_executed: 3, tools_denied: 1,
            model: "qwen", provider: "ollama", rationale: null, stopped_reason: "empty",
            success: success, error: success ? null : "boom"));

    private async Task SeedGateAsync(string id, string gatesRequired, params (string name, bool passed)[] courses)
    {
        var yaml = $"""
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
              gatesRequired: {gatesRequired}
            never:
              - modify_gate
              - widen_sandbox
              - access_signing_keys
              - truncate_ledger
              - grant_capability
            """;
        PolicyLoader.TryLoad(yaml, out var policy, out var reason).Should().BeTrue(reason);
        var proposal = new ExtensionProposal
        {
            Id = id,
            Kind = "brick",
            Summary = "test " + id,
            ProposedBy = "night-agent",
            ProposedAt = DateTimeOffset.UtcNow,
            Courses = courses.Select(c => new CourseResult { Name = c.name, Passed = c.passed, Detail = "test" }).ToList(),
        };
        await new GateStore(Path.Combine(_project, ".ashlar")).ProposeAsync(policy!, proposal, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Rolls_up_cycle_activity_in_the_window()
    {
        SeedCycle("night-agent", success: true);
        SeedCycle("night-agent", success: false);

        var output = await RunAsync(NewCmd(), since: 48, _project);

        output.Should().Contain("night-agent");
        output.Should().Contain("2 cycle(s)");
        output.Should().Contain("1 ok / 1 fail");
    }

    [Fact]
    public async Task Summarises_gate_outcomes_held_and_rejected()
    {
        // Held: the one required course passed. Rejected: a required course ('tests') never ran.
        await SeedGateAsync("ext-held01", "[sandbox]", ("sandbox", true));
        await SeedGateAsync("ext-rej01", "[sandbox, tests]", ("sandbox", true));

        var output = await RunAsync(NewCmd(), since: 48, _project);

        output.Should().Contain("1 held");
        output.Should().Contain("1 rejected");
        output.Should().Contain("ext-rej01");
    }

    [Fact]
    public async Task NoGateRecords_degradesGracefully()
    {
        SeedCycle("night-agent", success: true);

        var output = await RunAsync(NewCmd(), since: 48, _project);

        output.Should().Contain("no gate records");
    }

    [Fact]
    public async Task Window_excludesOlderCycles()
    {
        _cycles.Append(new CycleEvent(
            ts: DateTimeOffset.UtcNow - TimeSpan.FromHours(48), agent: "old-agent", cycle: 1, role: "extender",
            duration_ms: 10, iterations: 1, tools_executed: 0, tools_denied: 0, success: true));

        var output = await RunAsync(NewCmd(), since: 1, _project);

        output.Should().Contain("No cycles in the window");
        output.Should().NotContain("old-agent");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Human_report_defuses_historical_rejection_reason_without_changing_record(bool signed)
    {
        const string unicode = "révision-日本語-e\u0301-👩‍💻";
        const string reason = unicode + "\u001b]0;forged\u0007\rFORGED\nLINE\u009b\u061c\u202e\u2066\u2028\u2029";
        var record = new GateRecord
        {
            Proposal = new ExtensionProposal
            {
                Id = "historical-rejection",
                Kind = "brick",
                Summary = "Historical record",
                ProposedBy = "historical-agent",
                ProposedAt = DateTimeOffset.UtcNow,
                Courses = [],
            },
            State = ProposalState.Rejected,
            Actor = "historical\nactor",
            Reason = reason,
            DecidedAt = DateTimeOffset.UtcNow,
        };
        if (signed)
        {
            var signer = OperatorKey.Generate(_keys);
            record = record with { Sig = signer.Sign(CanonicalJson.Bytes(record)), Signer = signer.PublicKeyBase64 };
        }

        // Historical records bypass today's write validation, and their signed bytes must survive display.
        var gates = Path.Combine(_project, ".ashlar", "gates");
        Directory.CreateDirectory(gates);
        var path = Path.Combine(gates, record.Proposal.Id + ".json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(record));
        var before = await File.ReadAllBytesAsync(path);
        var canonical = CanonicalJson.Bytes(record);

        var output = await RunAsync(NewCmd(), since: 48, _project);

        output.Should().Contain("1 rejected").And.Contain(unicode);
        output.Should().Contain("�]0;forged��FORGED�LINE");
        foreach (var control in new[] { '\u001b', '\u0007', '\u009b', '\u061c', '\u202e', '\u2066', '\u2028', '\u2029' })
            output.Should().NotContain(control.ToString());
        output.Split('\n').Count(line => line.Contains("FORGED") || line.Contains("LINE"))
            .Should().Be(1, "a historical rejection reason must not manufacture report rows");
        (await File.ReadAllBytesAsync(path)).Should().Equal(before);
        var reread = await new GateStore(Path.Combine(_project, ".ashlar")).GetAsync(record.Proposal.Id);
        reread.Should().NotBeNull();
        reread!.Reason.Should().Be(reason);
        reread.Actor.Should().Be(record.Actor);
        reread.Sig.Should().Be(record.Sig);
        CanonicalJson.Bytes(reread).Should().Equal(canonical);
    }

    [Fact]
    public async Task Human_report_defuses_historical_gate_error_while_json_preserves_its_data()
    {
        const string fileName = "historical-\u202e-forged.json";
        var gates = Path.Combine(_project, ".ashlar", "gates");
        Directory.CreateDirectory(gates);
        var path = Path.Combine(gates, fileName);
        await File.WriteAllTextAsync(path, "null");
        var before = await File.ReadAllBytesAsync(path);

        var human = await RunAsync(NewCmd(), since: 48, _project);
        human.Should().Contain("could not read gate store").And.Contain("historical-�-forged.json");
        human.Should().NotContain("\u202e");

        var json = new StringWriter();
        var code = await NewCmd().ExecuteAsync(48, _project, formatJson: true, json, TextWriter.Null);
        code.Should().Be(0);
        using var document = JsonDocument.Parse(json.ToString());
        document.RootElement.GetProperty("gates").GetProperty("error").GetString()
            .Should().Contain(fileName, "machine-readable data must retain the original filename");
        json.ToString().Should().NotContain("\u202e", "JSON escaping keeps the serialized document safe without changing its value");
        (await File.ReadAllBytesAsync(path)).Should().Equal(before);
    }
}
