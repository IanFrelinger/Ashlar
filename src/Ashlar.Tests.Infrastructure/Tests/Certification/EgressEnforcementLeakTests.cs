using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Embeddings;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Models;
using Ashlar.AI.Pipeline.Rag;
using Ashlar.BackgroundAgents.Agents;
using Ashlar.BackgroundAgents.RAG;
using Ashlar.Hosting.Meai;
using Ashlar.Infrastructure.Egress;
using Ashlar.Runtime;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>SPEC-007 4.11: a real labelled retrieval must stop the subsequent model export.</summary>
[Collection("EnvironmentVariables")]
[Trait("Category", "Certification")]
public sealed class EgressEnforcementLeakTests : IDisposable
{
    private readonly EnvironmentVariableScope _profile = EnvironmentVariableScope.Unset("ASHLAR_DEPLOYMENT_PROFILE");
    private readonly EnvironmentVariableScope _mode = EnvironmentVariableScope.Unset("ASHLAR_EGRESS_MODE");
    private readonly EgressProcessStateScope _state = new(reset: true);

    [Theory]
    [InlineData("Secret", "Secret", "secure-workstation", 1, "egress_refused", AccessDenialReason.LevelTooLow)]
    [InlineData("Internal", "Secret", "secure-workstation", 2, "empty", AccessDenialReason.None)]
    [InlineData("", "TopSecret", "secure-workstation", 1, "egress_refused", AccessDenialReason.SystemHighData)]
    [InlineData("Restricted", "TopSecret", "secure-workstation", 1, "egress_refused", AccessDenialReason.SystemHighData)]
    [InlineData("Secret", "Secret", "full", 2, "empty", AccessDenialReason.LevelTooLow)]
    public async Task Scenario_A_and_C0_through_C3_pin_the_real_labelled_model_export(
        string tier, string clearance, string profile, int sends, string stop, AccessDenialReason reason)
    {
        var run = await RunRagAsync(tier, clearance, profile, enterSubject: true);
        run.Cycle.StoppedReason.Should().Be(stop);
        run.Sent.Count.Should().Be(sends);
        run.Decisions.Should().HaveCount(2);
        var first = run.Decisions[0];
        first.Current.Should().Be(SecurityLabel.Public);
        first.Access.Allowed.Should().BeTrue("C0: before retrieval the same model export is permitted");
        first.Refused.Should().BeFalse();
        run.RagResults.Should().ContainSingle().Which.Should().Contain("results=1",
            "unknown-tier refusals must follow a real retrieval, not an empty search");
        var second = run.Decisions[1];
        second.CurrentBasis.Should().Be("subject:" + run.Subject);
        second.Family.Should().Be(EgressFamilies.ModelMeai);
        second.Site.Should().Be("EG-MDL-01");
        second.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
        second.DestinationLabel.Should().Be(new SecurityLabel(SecurityLevel.Internal));
        second.Access.Reason.Should().Be(reason);
        second.Current.Should().Be(tier switch
        {
            "Secret" => new SecurityLabel(SecurityLevel.Secret),
            "Internal" => new SecurityLabel(SecurityLevel.Internal),
            _ => SecurityLabel.SystemHigh,
        });
        second.Mode.Should().Be(profile == "full" ? "report" : "enforce");
        second.ModeBasis.Should().Be("profile:" + profile);
        second.Refused.Should().Be(sends == 1);
        if (sends == 1)
        {
            run.Sent.Should().OnlyContain(text => !text.Contains(run.Canary, StringComparison.Ordinal));
            run.Cycle.EgressRefusals.Should().Be(1);
            var refusal = run.Memory.Where(e => e.EventType == "egress.refused").Should().ContainSingle().Which.Message;
            refusal.Should().Contain(reason.ToString()).And.Contain("EG-MDL-01").And.Contain(second.Ref);
            refusal.Should().NotContain(run.Canary).And.NotContain("Secret").And.NotContain("Internal");
            run.Warnings.Should().ContainSingle(message => message.Contains(second.Ref, StringComparison.Ordinal));
        }
        else
        {
            run.Sent[1].Should().Contain(run.Canary, "the allowed/report controls really carry the retrieved text");
            run.Cycle.EgressRefusals.Should().Be(0);
        }
    }

    [Fact]
    public async Task C6_without_a_runner_frame_the_first_model_call_is_refused()
    {
        var run = await RunRagAsync("Secret", "Secret", "secure-workstation", enterSubject: false);
        run.Sent.Should().BeEmpty();
        run.Cycle.StoppedReason.Should().Be("egress_refused");
        var decision = run.Decisions.Should().ContainSingle().Which;
        decision.CurrentBasis.Should().Be("no-subject");
        decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        run.RagResults.Should().BeEmpty("no retrieval ran before the first refusal");
    }

    private static async Task<LeakRun> RunRagAsync(string tier, string clearance, string profile, bool enterSubject)
    {
        var id = Guid.NewGuid().ToString("N");
        var subject = "agent:leak-" + id;
        var canary = "CANARY-" + id;
        var host = "gpu-" + id + ".example";
        var decisions = new ConcurrentQueue<EgressDecision>();
        using var subscription = EgressDecisionLog.Subscribe(new Sink(d =>
        {
            if (d.Destination.Contains(host, StringComparison.Ordinal)) decisions.Enqueue(d);
        }));
        var capture = new WarningCapture();
        using var loggerFactory = LoggerFactory.Create(logging => logging.AddProvider(capture));
        using var decisionLogging = new EgressDecisionLoggerSubscription(loggerFactory);
        var auditor = new InMemoryChatInvocationAuditor();
        using var embeddings = new TokenHashEmbeddingGenerator();
        var rag = new VectorDataRagService(new InProcessChunkCollection("leak-" + id), embeddings, auditor);
        await rag.IndexAsync(id, canary + " lives in the vault", trustTier: tier);
        var chat = new ScriptedChatClient(new Uri("http://" + host + ":11434"),
            Calls(RAGTool.DefaultId, new { query = canary + " lives in the vault", minScore = 0 }), Done());
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(new EgressGuard(profile));
        services.AddSingleton<IChatInvocationAuditor>(auditor);
        services.AddSingleton(rag);
        services.AddAshlarMeaiPipeline(ollamaInnerFactory: _ => chat,
            onnxInnerFactory: _ => new FakeChatClient(), registerDefaultRouter: false);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);
        var model = new MeaiBackedModel(client, NullLogger<MeaiBackedModel>.Instance);
        var tools = new CapabilityRegistry();
        tools.Register(new RAGTool(new MeaiVectorDataRagAdapter(rag)));
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>
        {
            ["agentId"] = "leak-" + id,
            ["maxDataSensitivity"] = clearance,
        });
        var memory = new InMemoryAgentMemory();
        using var frame = enterSubject ? EgressSubject.Enter(subject, new HighWaterMark(SecurityLabel.Public)) : null;
        var cycle = await new ToolCallingAgent("leak", model, NullLogger<ToolCallingAgent>.Instance)
            .RunCycleAsync(snapshot, tools, new PolicyEngine([]), null, memory, CancellationToken.None);
        return new(subject, canary, cycle, decisions.ToArray(), chat.Sent.ToArray(), memory.Query("", 100),
            auditor.Records.Where(r => r.TargetKey == "rag:search").Select(r => string.Join(";", r.PolicyDecisions)).ToArray(),
            capture.Messages.ToArray());
    }

    private sealed record LeakRun(string Subject, string Canary, AgentCycleResult Cycle,
        IReadOnlyList<EgressDecision> Decisions, IReadOnlyList<string> Sent, IReadOnlyList<EventRecord> Memory,
        IReadOnlyList<string> RagResults, IReadOnlyList<string> Warnings);

    private static string Calls(string id, object args) => JsonSerializer.Serialize(new
    {
        tool_calls = new[] { new { id, arguments = args } }, rationale = "scripted",
    });
    private static string Done() => JsonSerializer.Serialize(new { tool_calls = Array.Empty<object>(), rationale = "done" });

    private sealed class ScriptedChatClient(Uri provider, params string[] turns) : IChatClient
    {
        public List<string> Sent { get; } = [];
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Sent.Add(string.Join("\n", messages.Select(m => m.Text)));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, turns[Math.Min(Sent.Count - 1, turns.Length - 1)])));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var response = await GetResponseAsync(messages, options, cancellationToken);
            yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", provider, "scripted-model") : null;
        public void Dispose() { }
    }
    private sealed class Sink(Action<EgressDecision> record) : IEgressDecisionSink
    {
        public void Record(EgressDecision decision) => record(decision);
    }
    private sealed class WarningCapture : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public bool IsEnabled(LogLevel level) => true;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (id.Id == 7301 && level == LogLevel.Warning) Messages.Enqueue(formatter(state, exception));
        }
        public void Dispose() { }
    }
    public void Dispose()
    {
        _state.Dispose();
        _mode.Dispose();
        _profile.Dispose();
    }
}
