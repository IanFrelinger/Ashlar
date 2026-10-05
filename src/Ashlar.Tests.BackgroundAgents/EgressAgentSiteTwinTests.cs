using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.BackgroundAgents.Autonomy;
using Ashlar.BackgroundAgents.Forge;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.Core.Application.Autonomy;
using Ashlar.Manifest.Admission;
using Ashlar.Manifest.Signing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents;

/// <summary>
/// SPEC-007 PR 3b, behavioural twin of the background agents' explicit guard calls: the self-extend auto-share door
/// (EG-MESH-01) and the experimental Ollama proposer (EG-MDL-11).
/// </summary>
/// <remarks>
/// <para><b>What is pinned.</b> A successful auto-share records one <c>mesh.publish</c> decision naming the store
/// directory it resolves, before it packs; a share that then fails still records its attempt, and the cycle still
/// stands. <c>OllamaProposalSource.ProposeAsync</c> records one <c>model.legacy</c> decision for the daemon it posts
/// to, by scheme, host and port: a localhost daemon is Host, any other is ExternalModel, and the request still reaches
/// the daemon's <c>/api/generate</c>.</para>
/// <para><b>Isolation.</b> Each case enters its own <see cref="EgressSubject"/> frame and keeps only decisions whose
/// basis is that frame. The frame is an <c>AsyncLocal</c>, so it would reach a guard placed after an <c>await</c> too.
/// No network: the proposer's client sends to a stub handler, and the mesh store is a temporary directory passed
/// explicitly (never the operator's).</para>
/// </remarks>
public sealed class EgressAgentSiteTwinTests : IDisposable
{
    private readonly string _repo;

    public EgressAgentSiteTwinTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "egress-agent-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_repo);
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-MESH-01: self-extend auto-share
    // ---------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task TryAutoShare_records_one_mesh_publish_decision_for_the_store_it_writes_to()
    {
        var (record, forge, forgeId, signer) = await AdmittedAndAppliedAsync();
        var mesh = Path.Combine(_repo, "mesh");
        using var observed = Observe();

        var note = SelfExtendAdmissionBridge.TryAutoShare(
            record, forge, [forgeId], signer, autoShare: true, meshDir: mesh, NullLogger.Instance);

        note.Should().Contain("shared");
        Directory.EnumerateFiles(mesh, "*.ashpkg").Should().ContainSingle();
        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MESH-01");
        decision.Family.Should().Be(EgressFamilies.MeshPublish);
        decision.Destination.Should().Be("file:" + Path.GetFullPath(mesh), "an explicit mesh directory is the store itself");
        decision.DestinationClass.Should().Be(EgressDestinationClass.NetworkExport);
        decision.Fault.Should().BeNull();
    }

    [Fact]
    public async Task TryAutoShare_records_the_attempt_when_the_publish_then_fails()
    {
        // R12, attempt semantics: a file where the store directory should be makes Publish fail after the decision.
        var (record, forge, forgeId, signer) = await AdmittedAndAppliedAsync();
        var blocked = Path.Combine(_repo, "not-a-dir");
        await File.WriteAllTextAsync(blocked, "occupied");
        using var observed = Observe();

        var note = SelfExtendAdmissionBridge.TryAutoShare(
            record, forge, [forgeId], signer, autoShare: true, meshDir: blocked, NullLogger.Instance);

        note.Should().Contain("auto-share failed");
        observed.Sink.Seen.Should().ContainSingle().Which.Destination.Should().Be("file:" + Path.GetFullPath(blocked));
    }

    [Fact]
    public async Task TryAutoShare_records_nothing_when_auto_share_is_off()
    {
        var (record, forge, forgeId, signer) = await AdmittedAndAppliedAsync();
        using var observed = Observe();

        SelfExtendAdmissionBridge.TryAutoShare(
            record, forge, [forgeId], signer, autoShare: false, meshDir: Path.Combine(_repo, "mesh"), NullLogger.Instance)
            .Should().BeEmpty();

        observed.Sink.Seen.Should().BeEmpty("nothing leaves the project, so nothing is decided");
    }

    // ---------------------------------------------------------------------------------------------------------
    // EG-MDL-11: the experimental Ollama proposer
    // ---------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("http://localhost:11434", "http://localhost:11434", EgressDestinationClass.Host)]
    [InlineData("http://localhost:11434/", "http://localhost:11434", EgressDestinationClass.Host)]
    [InlineData("http://gpu-box.example:11434", "http://gpu-box.example:11434", EgressDestinationClass.ExternalModel)]
    public async Task OllamaProposalSource_records_one_model_legacy_decision_for_the_daemon_it_posts_to(
        string baseUrl, string recorded, EgressDestinationClass expectedClass)
    {
        var fake = new FakeOllama("```csharp\nnamespace P;\npublic sealed class TwinBrick { }\n```\n");
        var source = new OllamaProposalSource(new HttpClient(fake), new OllamaProposalOptions { BaseUrl = baseUrl });
        using var observed = Observe();

        var proposed = await source.ProposeAsync(new ProposalRequest("twin-objective", "t", "Contract:\n- x", null));

        proposed.Should().NotBeNull();
        var decision = observed.Sink.Seen.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MDL-11");
        decision.Family.Should().Be(EgressFamilies.ModelLegacy);
        decision.Destination.Should().Be(recorded, "only scheme, host and port are recorded");
        decision.DestinationClass.Should().Be(expectedClass);
        fake.Requests.Should().ContainSingle().Which.Should().Be(recorded + "/api/generate",
            "the guard records; the request still goes to the daemon's generate endpoint");
    }

    // ---------------------------------------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>An admitted, signed gate record over one parked forge write that has been applied, as the cycle leaves it.</summary>
    private async Task<(GateRecord Record, ChangeProposalStore Forge, string ForgeId, SigningIdentity Signer)> AdmittedAndAppliedAsync()
    {
        var signer = OperatorKey.Generate(Path.Combine(_repo, "keys"));
        var forge = AshlarProjectMediation.ProjectStore(_repo);
        var forgeId = forge.Add(new ChangeProposal
        {
            Id = "forge-" + Guid.NewGuid().ToString("N")[..8],
            TargetPath = "src/Twin.cs",
            NewContent = "// twin",
            Summary = "parked by the cycle",
            CreatedAt = DateTimeOffset.UtcNow,
        }).Id;
        var proposal = SelfExtendAdmissionBridge.BuildProposal("night-agent", "co-produce", [], 1, 0, [forgeId], forge);
        var store = new GateStore(Path.Combine(_repo, ".ashlar"), signer);
        var record = await store.RecordAsync(
            proposal, new AdmissionOutcome { State = ProposalState.Admitted, Reason = "within budget" }, DateTimeOffset.UtcNow);
        ForgeApplier.ApplyAll(forge, [forgeId], _repo, "gate");
        return (record, forge, forgeId, signer);
    }

    /// <summary>Subscribes a sink and enters a fresh subject frame; disposing leaves the frame and unsubscribes.</summary>
    private static Observation Observe()
    {
        var id = "egress-twin-" + Guid.NewGuid().ToString("N");
        var sink = new SubjectSink("subject:" + id);
        var subscription = EgressDecisionLog.Subscribe(sink);
        var frame = EgressSubject.Enter(id, new HighWaterMark());
        return new Observation(sink, frame, subscription);
    }

    private sealed class Observation(SubjectSink sink, IDisposable frame, IDisposable subscription) : IDisposable
    {
        public SubjectSink Sink { get; } = sink;

        public void Dispose()
        {
            frame.Dispose();
            subscription.Dispose();
        }
    }

    private sealed class SubjectSink(string basis) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _seen = new();

        public IReadOnlyList<EgressDecision> Seen => _seen.ToArray();

        public void Record(EgressDecision decision)
        {
            if (string.Equals(decision.CurrentBasis, basis, StringComparison.Ordinal))
                _seen.Enqueue(decision);
        }
    }

    private sealed class FakeOllama(string response) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<string> _requests = new();

        public IReadOnlyList<string> Requests => _requests.ToArray();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Enqueue(request.RequestUri!.AbsoluteUri);
            var payload = JsonSerializer.Serialize(new { response });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, Encoding.UTF8, "application/json"),
            });
        }
    }
}
