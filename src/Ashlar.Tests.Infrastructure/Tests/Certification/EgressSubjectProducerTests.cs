using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.BackgroundAgents.Agents;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.BackgroundAgents.RAG;
using Ashlar.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.5, report-only: a subject frame and a labelled read change what the next model call records.
/// Nothing refuses. The send still happens; the record says what would be refused once the switch (4.11) is on.
/// </summary>
/// <remarks>
/// <para><b>What this composes.</b> The leak facts run the real <see cref="ToolCallingAgent"/>, the real
/// <see cref="RAGTool"/> and the real <see cref="EgressGuard.Evaluate"/>, inside a real
/// <see cref="EgressSubject.Enter"/> / <see cref="EgressSubject.BeginRead"/> / <see cref="ReadScope.Report"/> path.
/// The self-extend fact runs the real <see cref="SelfExtendRunnerAdapter"/>, whose production floor is
/// <see cref="SecurityLabel.SystemHigh"/>. The leak facts enter at <see cref="SecurityLabel.Public"/> so a Secret
/// hit is <see cref="AccessDenialReason.LevelTooLow"/> rather than <see cref="AccessDenialReason.SystemHighData"/>,
/// which is what the production floor records.</para>
/// <para><b>What this does not compose.</b> Not the full <c>EgressEnforcementLeakTests</c> switch (that is 4.11).
/// Not <c>MeaiBackedModel</c> or <c>AddAshlarMeaiPipeline</c>, not <c>VectorDataRagService</c>, not enforcing mode,
/// not the Bing scenario and not the redirect scenario. The model and the RAG store are fakes. An open read scope
/// still observes only when it ends (the merged 4.4 rule); a within-call egress is a separate fact.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressSubjectProducerTests
{
    private const string Remote = "https://models.example/v1";
    private static readonly Uri RemoteUri = new(Remote);

    private static readonly SecurityLabel Secret = new(SecurityLevel.Secret);
    private static readonly SecurityLabel Internal = new(SecurityLevel.Internal);

    [Fact]
    public async Task After_a_Secret_RAG_hit_the_next_model_call_records_the_subject_and_would_refuse_LevelTooLow()
    {
        var guard = new EgressGuard("full");
        var decisions = await RunRagCycleAsync(
            guard,
            "agent:leak-secret",
            new FixedRag([new VectorSearchResult("doc-1", "the plan", 0.91, "Secret")]));

        decisions.Should().HaveCount(2);
        var before = decisions[0];
        before.Current.Should().Be(SecurityLabel.Public, "the model call before the tool has not read the hit yet");
        before.Access.Allowed.Should().BeTrue();

        var next = decisions[1];
        next.CurrentBasis.Should().Be("subject:agent:leak-secret");
        next.Current.Should().Be(Secret);
        next.Mode.Should().Be("report");
        next.Refused.Should().BeFalse("report mode still allows the send");
        next.Access.Allowed.Should().BeFalse();
        next.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public async Task After_an_Internal_RAG_hit_the_next_model_call_is_allowed()
    {
        var guard = new EgressGuard("full");
        var decisions = await RunRagCycleAsync(
            guard,
            "agent:leak-internal",
            new FixedRag([new VectorSearchResult("doc-2", "the note", 0.8, "Internal")]));

        decisions.Should().HaveCount(2);
        var next = decisions[1];
        next.CurrentBasis.Should().Be("subject:agent:leak-internal");
        next.Current.Should().Be(Internal);
        next.Mode.Should().Be("report");
        next.Refused.Should().BeFalse();
        next.Access.Allowed.Should().BeTrue();
        next.Access.Reason.Should().Be(AccessDenialReason.None);
    }

    [Fact]
    public async Task An_unlabelled_tool_is_SystemHigh_on_the_next_model_call()
    {
        var guard = new EgressGuard("full");
        var model = Scripted(guard, Call("plain"), Done());
        var tools = new CapabilityRegistry();
        tools.Register(new PlainTool());

        using (EgressSubject.Enter("agent:plain", new HighWaterMark(SecurityLabel.Public)))
        {
            var cycle = await CycleAsync(model, tools);
            cycle.StoppedReason.Should().Be("empty");
            cycle.ToolCallsExecuted.Should().Be(1);
        }

        model.Decisions.Should().HaveCount(2);
        var next = model.Decisions[1];
        next.CurrentBasis.Should().Be("subject:agent:plain");
        next.Current.Should().Be(SecurityLabel.SystemHigh);
        next.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        next.Refused.Should().BeFalse();
        next.Mode.Should().Be("report");
    }

    [Fact]
    public async Task A_tool_that_throws_is_SystemHigh_when_the_scope_ends()
    {
        var guard = new EgressGuard("full");
        var model = Scripted(guard, Call("boom"));
        var tools = new CapabilityRegistry();
        tools.Register(new ThrowingTool());

        EgressDecision after;
        using (EgressSubject.Enter("agent:throws", new HighWaterMark(SecurityLabel.Public)))
        {
            var cycle = await CycleAsync(model, tools);
            cycle.StoppedReason.Should().Be("error");
            after = guard.Evaluate(new EgressRequest(EgressFamilies.ModelMeai, "after-throw", RemoteUri));
        }

        after.CurrentBasis.Should().Be("subject:agent:throws");
        after.Current.Should().Be(SecurityLabel.SystemHigh);
        after.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        after.Refused.Should().BeFalse();
        after.Mode.Should().Be("report");
    }

    [Fact]
    public async Task Production_self_extend_records_subject_agent_at_SystemHigh()
    {
        var repo = Path.Combine(Path.GetTempPath(), "sx-egress-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(repo);
        try
        {
            var guard = new EgressGuard("full");
            var model = new OneShotModel(guard);
            var runner = new SelfExtendRunnerAdapter(
                model,
                NullLogger<SelfExtendRunnerAdapter>.Instance,
                NullLoggerFactory.Instance);

            var result = await runner.RunAsync(
                repo,
                objective: "record the subject",
                agentName: "planner",
                modelProvider: null,
                modelName: null,
                agentId: "planner-9");

            result.Success.Should().BeTrue(result.Summary);
            result.StoppedReason.Should().Be("empty");
            model.Decision.Should().NotBeNull();
            var decision = model.Decision!;
            decision!.CurrentBasis.Should().Be("subject:agent:planner-9");
            decision.Current.Should().Be(SecurityLabel.SystemHigh,
                "the production floor is SystemHigh: the snapshot carries unlabelled carry-over");
            decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
            decision.Access.Allowed.Should().BeFalse();
            decision.Refused.Should().BeFalse("report mode still allows the send");
            decision.Mode.Should().Be("report");
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch { /* best effort */ }
        }
    }

    private static async Task<IReadOnlyList<EgressDecision>> RunRagCycleAsync(
        EgressGuard guard,
        string subjectId,
        IRAGService rag)
    {
        var model = Scripted(guard, Call("rag_search", """{"query":"plans"}"""), Done());
        var tools = new CapabilityRegistry();
        tools.Register(new RAGTool(rag));
        using (EgressSubject.Enter(subjectId, new HighWaterMark(SecurityLabel.Public)))
        {
            var cycle = await CycleAsync(model, tools);
            cycle.StoppedReason.Should().Be("empty");
            cycle.ToolCallsExecuted.Should().Be(1);
        }

        return model.Decisions;
    }

    private static async Task<AgentCycleResult> CycleAsync(DecidingModel model, CapabilityRegistry tools)
    {
        var agent = new ToolCallingAgent("leak", model, NullLogger<ToolCallingAgent>.Instance, maxIterations: 3);
        return await agent.RunCycleAsync(
            WorldSnapshot.ForRepo("/repo", "/repo/out"),
            tools,
            new PolicyEngine([new AllowEverything()]),
            onRejected: null,
            memory: null,
            CancellationToken.None);
    }

    private static DecidingModel Scripted(EgressGuard guard, params string[] turns) => new(guard, turns);

    private static string Call(string id, string argumentsJson = "{}") =>
        $$"""{"tool_calls":[{"id":"{{id}}","arguments":{{argumentsJson}}}],"rationale":"scripted"}""";

    private static string Done() => """{"tool_calls":[],"rationale":"done"}""";

    private sealed class DecidingModel : IModel
    {
        private readonly EgressGuard _guard;
        private readonly Queue<string> _turns;

        public DecidingModel(EgressGuard guard, IEnumerable<string> turns)
        {
            _guard = guard;
            _turns = new Queue<string>(turns);
        }

        public List<EgressDecision> Decisions { get; } = [];

        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            Decisions.Add(_guard.Evaluate(new EgressRequest(EgressFamilies.ModelMeai, "producer-twin", RemoteUri)));
            var text = _turns.Count > 0 ? _turns.Dequeue() : Done();
            return Task.FromResult(new ModelOutput(text));
        }
    }

    private sealed class OneShotModel : IModel
    {
        private readonly EgressGuard _guard;

        public OneShotModel(EgressGuard guard) => _guard = guard;

        public EgressDecision? Decision { get; private set; }

        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            Decision = _guard.Evaluate(new EgressRequest(EgressFamilies.ModelMeai, "self-extend-model", RemoteUri));
            return Task.FromResult(new ModelOutput(Done()));
        }
    }

    private sealed class FixedRag : IRAGService
    {
        private readonly IReadOnlyList<VectorSearchResult> _hits;

        public FixedRag(IReadOnlyList<VectorSearchResult> hits) => _hits = hits;

        public Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            string query, int maxResults, double minScore, string? maxSensitivityLevelName, CancellationToken cancellationToken = default)
            => Task.FromResult(_hits);

        public Task IndexAsync(string id, string text, string? sensitivityLevelName, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task RemoveAsync(string id, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task ClearAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class PlainTool : ITool
    {
        public string Id => "plain";

        public ToolSchema Schema => new(Id, "unlabelled", "{}");

        public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct) =>
            Task.FromResult(new ToolResult(new ActionDelta(s.Tick, s.Tick + 1, ["plain"]), new { value = "data" }));
    }

    private sealed class ThrowingTool : ITool
    {
        public string Id => "boom";

        public ToolSchema Schema => new(Id, "throws", "{}");

        public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot s, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private sealed class AllowEverything : IPolicy
    {
        public bool Approve(ToolCall toolCall, WorldSnapshot s, out string reason)
        {
            reason = "OK";
            return true;
        }
    }
}
