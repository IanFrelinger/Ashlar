using FluentAssertions;
using Microsoft.Extensions.Logging;
using Ashlar.BackgroundAgents.Autonomy;
using Ashlar.BackgroundAgents.Objectives;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Infrastructure.Certification.HotSwap;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Autonomy;

/// <summary>
/// What a sweep is allowed to COST and to HIDE when something is wrong.
///
/// <para>Continuing past a failed objective is what keeps one bad objective from wedging every
/// objective behind it — but continuing is not free. These tests pin the two prices: a sweep may
/// not spend more than its budget just because the spending failed, and an artifact a human wrote
/// that no longer parses may not be reported as an artifact nobody wrote.</para>
/// </summary>
[Collection(AutonomyTempProjectFilesCollection.Name)]
public sealed class AutonomySweepBlastRadiusTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ashlar-blast-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task A_failing_proposer_is_called_once_per_SWEEP_not_once_per_objective()
    {
        // The amplification this guards: MaxObjectivesPerSweep bounds the sweep's work, and the
        // proposer call is the expensive, rate-limited, billable part of that work. If only
        // SUCCESSES charged the budget, a proposer that is down (429, expired credential) would be
        // retried once per pending objective on every sweep — the loop would hit hardest exactly
        // when the dependency is already refusing, and a sweep over a large backlog could run
        // longer than its own interval and starve the timer.
        for (var i = 0; i < 8; i++)
            SeedObjective($"obj-{i}", priority: i);

        var proposer = new ThrowingProposer();
        var loop = Loop(new AutonomyLoopSettings { MaxObjectivesPerSweep = 2 }, proposer);

        var attempted = await loop.SweepAsync();

        attempted.Should().Be(0, "every objective failed before it could run");
        proposer.Calls.Should().Be(2,
            "a failure charges the same per-sweep budget as a success; 8 pending objectives must " +
            "not become 8 calls to a proposer that is already refusing");
    }

    [Fact]
    public async Task A_broken_witness_is_reported_as_broken_and_does_not_stop_the_objective_behind_it()
    {
        // Absent and broken both mean "does not run", but they are opposite operator situations.
        // Reporting a corrupt witness as "no witness beside it" at Debug parks the objective
        // forever with nothing visible said about a file that is sitting right there.
        SeedObjective("obj-broken", priority: 1, witness: "{\"brickId\":\"obj-broken\",\"cases\":[ THIS IS NOT JSON");
        SeedObjective("obj-healthy", priority: 2);

        var logger = new ListLogger<AutonomyLoopService>();
        var loop = Loop(new AutonomyLoopSettings { MaxObjectivesPerSweep = 2 }, proposals: null, logger: logger);

        var attempted = await loop.SweepAsync();

        attempted.Should().Be(1, "the healthy objective behind the broken one still runs");
        logger.Entries.Should().Contain(
            e => e.Level == LogLevel.Warning && e.Message.Contains("obj-broken") && e.Message.Contains("unusable"),
            "a witness someone wrote that no longer parses is an operator error, not a Debug line");
        logger.Entries.Should().NotContain(
            e => e.Message.Contains("obj-broken") && e.Message.Contains("no witness beside it"),
            "the witness IS beside it — saying otherwise sends the operator looking for the wrong thing");
    }

    [Fact]
    public void An_absent_artifact_reports_no_corruption_but_a_malformed_one_names_the_reason()
    {
        SeedObjective("obj-bare", priority: 1);
        var path = Path.Combine(_root, "pending", "obj-bare.md");
        var witnessPath = Path.Combine(_root, "pending", "obj-bare.witness.json");
        File.Delete(witnessPath);

        ObjectiveArtifacts.LoadWitness(path, out var absent).Should().BeNull();
        absent.Should().BeNull("nobody has written acceptance criteria yet; that is the normal pending state");

        File.WriteAllText(witnessPath, "{\"brickId\":\"x\",\"cases\":[]}");
        ObjectiveArtifacts.LoadWitness(path, out var empty).Should().BeNull();
        empty.Should().NotBeNull();
        empty!.Should().Contain("no witness cases", "a certificate minted against zero cases would prove nothing");

        File.WriteAllText(witnessPath, "{ not json");
        ObjectiveArtifacts.LoadWitness(path, out var broken).Should().BeNull();
        broken.Should().NotBeNull();

        File.WriteAllText(Path.Combine(_root, "pending", "obj-bare.proposal.json"), "{ not json");
        ObjectiveArtifacts.LoadRecordedProposal(path, out var brokenProposal).Should().BeNull();
        brokenProposal.Should().NotBeNull("a recorded proposal that cannot be replayed is not the same as none recorded");
    }

    // --- helpers -------------------------------------------------------------------------

    private AutonomyLoopService Loop(
        AutonomyLoopSettings settings,
        IProposalSource? proposals = null,
        ListLogger<AutonomyLoopService>? logger = null) =>
        new(new ObjectiveStore(_root),
            new AutonomousIterationHarness(new ScriptedGate(), SwapHost(), holdAdmission: true),
            settings,
            logger ?? new ListLogger<AutonomyLoopService>(),
            proposals,
            autonomyOptions: null);

    private static CertifiedBrickHotSwapHost SwapHost() =>
        new(hmacKey: null, drainTimeout: TimeSpan.FromSeconds(5), revocations: new InMemoryCertificateRevocationList());

    private void SeedObjective(string id, int priority, string? witness = null)
    {
        var pending = Path.Combine(_root, "pending");
        Directory.CreateDirectory(pending);
        File.WriteAllText(Path.Combine(pending, id + ".md"),
            "---\n" +
            "id: " + id + "\n" +
            "title: Blast radius probe " + id + "\n" +
            "status: pending\n" +
            "source: Human\n" +
            "priority: " + priority + "\n" +
            "---\n\nA probe objective for the sweep blast-radius tests.\n");
        File.WriteAllText(Path.Combine(pending, id + ".witness.json"),
            witness ?? "{\"brickId\":\"" + id + "\",\"cases\":[{\"input\":{\"payload\":\"a\"},\"expectedOutput\":{\"isValid\":true}}]}");
        File.WriteAllText(Path.Combine(pending, id + ".proposal.json"),
            "{\"sourceCode\":\"namespace Probe; public sealed class ProbeBrick { }\",\"typeName\":\"Probe.ProbeBrick\",\"proposerSignature\":\"recorded:test\"}");
    }

    private sealed class ThrowingProposer : IProposalSource
    {
        public int Calls { get; private set; }

        public Task<ProposedSource?> ProposeAsync(ProposalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            throw new HttpRequestException("429 Too Many Requests (scripted)");
        }
    }

    private sealed class ScriptedGate : ICertificationGate
    {
        public Task<CertificationDecision> CertifyAsync(CertificationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new CertificationDecision
            {
                Admitted = false,
                FailureCheck = "correctness",
                WitnessFindings = new List<WitnessFinding>(),
                Record = new CertificationRecord
                {
                    Status = "rejected",
                    Stage = "correctness",
                    Admitted = false,
                    Signed = false,
                    Timestamp = DateTimeOffset.UtcNow,
                    BrickId = "scripted",
                    Reason = "Correctness check failed (scripted)",
                },
            });
    }

    private sealed record Entry(LogLevel Level, string Message);

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<Entry> _entries = new();
        private readonly object _sync = new();

        public IReadOnlyList<Entry> Entries
        {
            get { lock (_sync) { return _entries.ToList(); } }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_sync) { _entries.Add(new Entry(logLevel, formatter(state, exception))); }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
