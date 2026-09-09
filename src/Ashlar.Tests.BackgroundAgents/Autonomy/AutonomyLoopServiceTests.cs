using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Ashlar.Agents.TestKit;
using Ashlar.BackgroundAgents.Autonomy;
using Ashlar.BackgroundAgents.Objectives;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Core.Application.Certification.Models;
using Ashlar.Core.Application.Certification.Ports;
using Ashlar.Core.Application.Execution.Ports;
using Ashlar.Infrastructure.Autonomy;
using Ashlar.Infrastructure.Certification.HotSwap;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Autonomy;

/// <summary>
/// The standing loop's obedience to host options (M23) and its hygiene (L19/M24): it runs
/// on the timer only under <c>Ashlar:Autonomy:Enabled</c>; it opens a session only when the
/// host said <c>UseSandboxSessions</c>, with the loop's image, else the host's; the
/// keepalive outlives the iteration ceiling; the temp project file does not outlive the
/// iteration; and an in-process refusal by the identity handle is reported as host wiring,
/// never handed to a proposer as something to repair. No model, no container, no real gate:
/// a scripted session runner and a scripted gate make the harness's inputs observable.
/// </summary>
[Collection(AutonomyTempProjectFilesCollection.Name)]
public sealed class AutonomyLoopServiceTests : IDisposable
{
    private const string ObjectiveId = "loop-probe";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "ashlar-loop-tests-" + Guid.NewGuid().ToString("N"));

    public AutonomyLoopServiceTests() => SeedObjective(_root, ObjectiveId);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Sweep_UnderHostOptionsWithoutSessions_OpensNoSession_EvenWhenTheLoopNamesAnImage()
    {
        // The host said no sessions. A loop-level image must not smuggle one in: the spec
        // the harness receives is null, so nothing is started.
        var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Failure("sh: dotnet: not found", 127));
        var loop = Loop(
            Options(o => o.UseSandboxSessions = false),
            new AutonomyLoopSettings { SessionImage = "loop:image" },
            harness: HarnessThatBuildsInSession(sandbox));

        var attempted = await loop.SweepAsync();

        attempted.Should().Be(1, "the objective is eligible: witness and recorded proposal beside it");
        sandbox.Sessions.Should().BeEmpty("UseSandboxSessions=false means no SessionSpec reaches the harness");
    }

    [Fact]
    public async Task Sweep_UnderHostOptionsWithSessions_FallsBackToTheHostImage_AndKeepsAliveBeyondTheCeiling()
    {
        var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Failure("sh: dotnet: not found", 127));
        var loop = Loop(
            Options(o =>
            {
                o.UseSandboxSessions = true;
                o.SessionImage = "host:image";
                o.IterationCeilingSeconds = 120;
            }),
            new AutonomyLoopSettings { SessionImage = null },
            harness: HarnessThatBuildsInSession(sandbox));

        await loop.SweepAsync();

        var session = sandbox.Sessions.Should().ContainSingle().Subject;
        session.Spec.Image.Should().Be("host:image", "the loop's image is null, so the host options' image is the fallback");
        session.Spec.Command.Should().Equal(
            new[] { "sleep", (120 + AutonomyLoopService.SessionKeepaliveMarginSeconds).ToString() },
            "the keepalive must outlive the ceiling, never equal it");
        session.Spec.Network.Should().Be(NetworkAccess.None);
        session.Spec.ScratchPaths.Should().BeEquivalentTo(SessionScratchPaths.Default);
        sandbox.ActiveSessions.Should().Be(0, "the harness tears the session down with the iteration");
    }

    [Fact]
    public async Task Sweep_LoopImage_WinsOverTheHostImage()
    {
        var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Failure("sh: dotnet: not found", 127));
        var loop = Loop(
            Options(o =>
            {
                o.UseSandboxSessions = true;
                o.SessionImage = "host:image";
            }),
            new AutonomyLoopSettings { SessionImage = "loop:image" },
            harness: HarnessThatBuildsInSession(sandbox));

        await loop.SweepAsync();

        sandbox.Sessions.Should().ContainSingle().Which.Spec.Image.Should().Be("loop:image");
    }

    [Fact]
    public async Task HandComposedLoop_WithNoHostOptions_OpensASessionOnlyWhenItsSettingsNameAnImage()
    {
        // The first-flight spike composes the loop by hand and sweeps it directly; it names
        // an image and expects a session. Without an image there is nothing to attest, so
        // there is no session — never a session with a null image that the runner rejects.
        var withImage = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Failure("sh: dotnet: not found", 127));
        await Loop(null, new AutonomyLoopSettings { SessionImage = "hand:image" }, HarnessThatBuildsInSession(withImage))
            .SweepAsync();
        var session = withImage.Sessions.Should().ContainSingle().Subject;
        session.Spec.Image.Should().Be("hand:image");
        session.Spec.Command.Should().Equal(
            new[] { "sleep", (new AshlarAutonomyOptions().IterationCeilingSeconds + AutonomyLoopService.SessionKeepaliveMarginSeconds).ToString() },
            "no host options means the option DEFAULT ceiling plus the margin");

        var withoutImage = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Failure("sh: dotnet: not found", 127));
        await Loop(null, new AutonomyLoopSettings { SessionImage = null }, HarnessThatBuildsInSession(withoutImage))
            .SweepAsync();
        withoutImage.Sessions.Should().BeEmpty();
    }

    [Fact]
    public async Task TimerLoop_DoesNotStart_WhenAutonomyIsDisabled_OrWhenNoHostOptionsExist()
    {
        // The standing loop's one master switch is Ashlar:Autonomy:Enabled. Off means the timer
        // never starts, whatever IntervalSeconds says; no options at all means the same, because
        // a background loop with no host switch is not fail-closed.
        foreach (var options in new[] { Options(o => o.Enabled = false), null })
        {
            var sandbox = new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success());
            var logger = new ListLogger<AutonomyLoopService>();
            var loop = Loop(options, new AutonomyLoopSettings { IntervalSeconds = 1 }, HarnessThatBuildsInSession(sandbox), logger: logger);

            await loop.StartAsync(CancellationToken.None);
            var finished = await Task.WhenAny(loop.ExecuteTask!, Task.Delay(TimeSpan.FromSeconds(10)));
            await loop.StopAsync(CancellationToken.None);

            finished.Should().BeSameAs(loop.ExecuteTask, "the loop must return immediately, not wait for a tick");
            sandbox.Sessions.Should().BeEmpty("nothing was swept");
            logger.Messages.Should().Contain(m => m.Contains("Autonomy loop not started"));
        }
    }

    [Fact]
    public async Task TimerLoop_Starts_UnderEnabledOptions_AndReportsTheEnforcedHold()
    {
        var logger = new ListLogger<AutonomyLoopService>();
        var loop = Loop(
            Options(o => { o.Enabled = true; o.HoldAdmission = true; }),
            new AutonomyLoopSettings { IntervalSeconds = 3600 },
            HarnessThatBuildsInSession(new FakeSandboxedSessionRunner(FakeSandboxedSessionRunner.Success())),
            logger: logger);

        await loop.StartAsync(CancellationToken.None);
        // StartAsync returns before ExecuteAsync is scheduled. A fixed 50ms wait loses
        // the start log on a loaded macOS runner when StopAsync cancels first.
        await WaitUntilAsync(
            () => logger.Messages.Any(m =>
                m.Contains("Autonomy loop starting") &&
                m.Contains("holdAdmission=True") &&
                m.Contains("enforced by the harness")),
            TimeSpan.FromSeconds(5));
        await loop.StopAsync(CancellationToken.None);

        logger.Messages.Should().Contain(m => m.Contains("Autonomy loop starting") && m.Contains("holdAdmission=True") && m.Contains("enforced by the harness"),
            "the hold the log reports is the option the harness enforces — there is no second dial to disagree with it");
    }

    [Fact]
    public async Task InProcessRefusalByTheIdentityHandle_IsTerminal_AndNeverBecomesRepairFeedback()
    {
        // With no execution backend the gate runs the loop's identity handle in-process and
        // it throws on every case, as it must. That rejection is host wiring, not a candidate
        // defect: the proposer must be asked exactly once (the initial proposal) and never
        // for a repair. A genuine correctness rejection, by contrast, IS repaired.
        var refusal = new ScriptedGate(RejectAtCorrectness(
            Enumerable.Range(0, 2).Select(i => new WitnessFinding(
                i, WitnessFindingKind.Threw,
                Detail: $"Proposed brick '{ObjectiveId}' {ProposedBrickHandle.InProcessRefusal}."))));
        var proposer = new CountingProposer();
        var logger = new ListLogger<AutonomyLoopService>();
        var loop = Loop(Options(o => o.UseSandboxSessions = false), new AutonomyLoopSettings(), Harness(refusal), proposer, logger);

        await loop.SweepAsync();

        proposer.Calls.Should().Be(1, "the initial proposal only; a wiring refusal is not something a model can fix");
        proposer.RepairRequests.Should().BeEmpty();
        logger.Messages.Should().Contain(m => m.Contains("IN-PROCESS") && m.Contains("ExecuteCandidateInSession"),
            "the operator is told which switch is wrong");

        // Control: a real mismatch on the same objective goes to the proposer as repair feedback.
        var mismatch = new ScriptedGate(RejectAtCorrectness(new[]
        {
            new WitnessFinding(0, WitnessFindingKind.Mismatch, "isValid", "true", "false"),
        }));
        var repairedProposer = new CountingProposer();
        await Loop(Options(o => o.UseSandboxSessions = false), new AutonomyLoopSettings(), Harness(mismatch), repairedProposer)
            .SweepAsync();

        repairedProposer.RepairRequests.Should().NotBeEmpty("a genuine correctness rejection is repairable");
    }

    [Fact]
    public async Task Sweep_DoesNotLeakTheTemporaryProjectFile()
    {
        var before = TempProjectFiles();
        var loop = Loop(Options(o => o.UseSandboxSessions = false), new AutonomyLoopSettings(), Harness(new ScriptedGate(RejectAtCorrectness(new[]
        {
            new WitnessFinding(0, WitnessFindingKind.Mismatch, "isValid", "true", "false"),
        }))));

        (await loop.SweepAsync()).Should().Be(1);

        TempProjectFiles().Should().BeEquivalentTo(before, "the compile-time project file is deleted with the iteration");
    }

    [Fact]
    public void LoopSettings_DefaultCompilationReferences_AreTheBrickContractAssemblies()
    {
        // Empty references cannot compile any candidate; the default is the minimum every
        // candidate needs, so a host that forgets the setting still gets a compiling loop.
        var settings = new AutonomyLoopSettings();

        settings.CompilationReferences.Should().NotBeEmpty();
        settings.CompilationReferences.Should().OnlyContain(p => File.Exists(p));
        settings.CompilationReferences.Should().Contain(p => p.EndsWith("Ashlar.Brick.Contracts.dll", StringComparison.OrdinalIgnoreCase));
    }

    // --- helpers -------------------------------------------------------------------------

    private static IOptions<AshlarAutonomyOptions> Options(Action<AshlarAutonomyOptions> configure)
    {
        var options = new AshlarAutonomyOptions { Enabled = true };
        configure(options);
        return Microsoft.Extensions.Options.Options.Create(options);
    }

    private AutonomyLoopService Loop(
        IOptions<AshlarAutonomyOptions>? options,
        AutonomyLoopSettings settings,
        AutonomousIterationHarness harness,
        IProposalSource? proposals = null,
        ListLogger<AutonomyLoopService>? logger = null) =>
        new(new ObjectiveStore(_root), harness, settings, logger ?? new ListLogger<AutonomyLoopService>(), proposals, options);

    private static AutonomousIterationHarness HarnessThatBuildsInSession(ISandboxedSessionRunner sandbox) =>
        // The build leg is the observation instrument: with a SessionSpec it starts the session
        // (recorded by the fake) and fails at the toolchain probe, ending the iteration before
        // any gate; without one it refuses fail-closed and starts nothing. Either way the test
        // learns exactly what spec the loop handed over, cheaply and deterministically.
        new(new ScriptedGate(RejectAtCorrectness(Array.Empty<WitnessFinding>())), SwapHost(), sandbox: sandbox, buildCandidateInSession: true, holdAdmission: true);

    private static AutonomousIterationHarness Harness(ICertificationGate gate) =>
        new(gate, SwapHost(), holdAdmission: true);

    private static CertifiedBrickHotSwapHost SwapHost() =>
        new(hmacKey: null, drainTimeout: TimeSpan.FromSeconds(5), revocations: new InMemoryCertificateRevocationList());

    private static CertificationDecision RejectAtCorrectness(IEnumerable<WitnessFinding> findings) => new()
    {
        Admitted = false,
        FailureCheck = "correctness",
        WitnessFindings = findings.ToList(),
        Record = new CertificationRecord
        {
            Status = "rejected",
            Stage = "correctness",
            Admitted = false,
            Signed = false,
            Timestamp = DateTimeOffset.UtcNow,
            BrickId = ObjectiveId,
            Reason = "Correctness check failed (scripted)",
        },
    };

    private static async Task WaitUntilAsync(Func<bool> ready, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!ready())
        {
            if (DateTime.UtcNow >= deadline)
            {
                return;
            }

            await Task.Delay(25);
        }
    }

    private static IReadOnlyList<string> TempProjectFiles() =>
        Directory.EnumerateFiles(Path.GetTempPath(), "ashlar-objective-*.csproj").OrderBy(p => p, StringComparer.Ordinal).ToList();

    private static void SeedObjective(string root, string id)
    {
        var pending = Path.Combine(root, "pending");
        Directory.CreateDirectory(pending);
        File.WriteAllText(Path.Combine(pending, id + ".md"),
            "---\n" +
            $"id: {id}\n" +
            "title: Loop probe objective\n" +
            "status: pending\n" +
            "source: Human\n" +
            "priority: 10\n" +
            "---\n\nA probe objective for the loop tests.\n");
        File.WriteAllText(Path.Combine(pending, id + ".witness.json"),
            "{\"brickId\":\"" + id + "\",\"cases\":[{\"input\":{\"payload\":\"a\"},\"expectedOutput\":{\"isValid\":true}}," +
            "{\"input\":{\"payload\":\"b\"},\"expectedOutput\":{\"isValid\":false}}]}");
        File.WriteAllText(Path.Combine(pending, id + ".proposal.json"),
            "{\"sourceCode\":\"namespace Probe; public sealed class ProbeBrick { }\",\"typeName\":\"Probe.ProbeBrick\",\"proposerSignature\":\"recorded:test\"}");
    }

    private sealed class ScriptedGate : ICertificationGate
    {
        private readonly CertificationDecision _decision;
        public ScriptedGate(CertificationDecision decision) => _decision = decision;
        public Task<CertificationDecision> CertifyAsync(CertificationRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(_decision);
    }

    private sealed class CountingProposer : IProposalSource
    {
        public int Calls { get; private set; }
        public List<RepairContext> RepairRequests { get; } = new();

        public Task<ProposedSource?> ProposeAsync(ProposalRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (request.Repair is { } repair)
                RepairRequests.Add(repair);
            return Task.FromResult<ProposedSource?>(new ProposedSource(
                "namespace Probe; public sealed class ProbeBrick { }", "Probe.ProbeBrick", "test:proposer"));
        }
    }

    private sealed class ListLogger<T> : ILogger<T>
    {
        private readonly List<string> _messages = new();
        private readonly object _sync = new();

        public IReadOnlyList<string> Messages
        {
            get { lock (_sync) { return _messages.ToList(); } }
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_sync) { _messages.Add(formatter(state, exception)); }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}
