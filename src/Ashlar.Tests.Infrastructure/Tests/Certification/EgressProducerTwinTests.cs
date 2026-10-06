using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Models;
using Ashlar.AI.Pipeline.Rag;
using Ashlar.BackgroundAgents.Agents;
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.BackgroundAgents.RAG;
using Ashlar.Hosting.Meai;
using Ashlar.Runtime;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007 PR 4.5, the subject producers, report-only: the runner declares the frame, <see cref="ToolCallingAgent"/>
/// scopes every tool call as a read, <see cref="RAGTool"/> reports the canonical tier of each hit (or "read nothing"),
/// a response from an agent-backed chat target counts as <see cref="SecurityLabel.SystemHigh"/>, and production
/// self-extend records its agent as the subject at <see cref="SecurityLabel.SystemHigh"/>.
/// </summary>
/// <remarks>
/// <para><b>The leak skeleton</b> (the §5 done-when, in report mode). A test runner enters
/// <c>agent:leak-&lt;guid&gt;</c> at <see cref="SecurityLabel.Public"/> and runs a real <see cref="ToolCallingAgent"/>
/// over a real <see cref="RAGTool"/>, <see cref="MeaiVectorDataRagAdapter"/> and <see cref="VectorDataRagService"/>,
/// with the model behind the governed <c>local:ollama</c> MEAI client, whose scripted inner client says it dials an
/// external host (ExternalModel, <c>Internal</c>, EG-MDL-01). After a <c>Secret</c> hit the next model call records
/// the agent as its subject at <c>Secret</c> and would be refused <c>LevelTooLow</c>; the guard reports, so the call
/// still goes. After an <c>Internal</c> hit it would be allowed.</para>
/// <para><b>Process-global state.</b> Every guard here has an explicit profile, so no decision reads the environment,
/// and each decision is read from a recording guard or the value <c>Evaluate</c> returns. Frames live on each test's
/// own async flow. Hermetic: no network (the chat clients are scripted), a temporary directory for self-extend.</para>
/// </remarks>
[Trait("Category", "Certification")]
public sealed class EgressProducerTwinTests
{
    private const string SubjectPrefix = "subject:";
    private const string Remote = "https://remote.example/v1/chat";

    private static readonly SecurityLabel Internal = new(SecurityLevel.Internal);
    private static readonly SecurityLabel Secret = new(SecurityLevel.Secret);

    // ---- The leak skeleton ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Leak_skeleton_after_a_Secret_hit_the_next_model_call_records_the_agent_at_Secret_and_would_be_refused_LevelTooLow()
    {
        var run = await RunLeakSkeletonAsync("Secret");

        run.Cycle.StoppedReason.Should().Be("empty");
        run.Decisions.Should().HaveCount(2, "two model calls, each decided once by the outermost governance layer");

        var first = run.Decisions[0];
        first.CurrentBasis.Should().Be(SubjectPrefix + run.Subject, "the runner's frame names the agent");
        first.Current.Should().Be(SecurityLabel.Public, "before the read the agent holds only the runner's floor");
        first.Access.Allowed.Should().BeTrue("Public data may go to an Internal model: {0}", first.Access);

        var second = run.Decisions[1];
        second.Family.Should().Be(EgressFamilies.ModelMeai);
        second.Site.Should().Be("EG-MDL-01");
        second.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
        second.DestinationLabel.Should().Be(Internal);
        second.CurrentBasis.Should().Be(SubjectPrefix + run.Subject);
        second.Current.Should().Be(Secret, "the agent read a Secret chunk, and RAGTool reported its tier");
        second.Access.Allowed.Should().BeFalse();
        second.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        second.Mode.Should().Be("report", "every profile still reports until the switch (PR 4.11)");
        second.Refused.Should().BeFalse("a report-mode decision never refuses");

        run.ChatCalls.Should().Be(2, "report mode: the call still goes");
        run.SecondCallText.Should().Contain(run.Canary, "the Secret chunk is in the conversation the second call carries");
    }

    [Fact]
    public async Task Leak_skeleton_after_an_Internal_hit_the_next_model_call_would_be_allowed()
    {
        var run = await RunLeakSkeletonAsync("Internal");

        run.Decisions.Should().HaveCount(2);
        var second = run.Decisions[1];
        second.CurrentBasis.Should().Be(SubjectPrefix + run.Subject);
        second.Current.Should().Be(Internal, "the agent read an Internal chunk");
        second.Access.Allowed.Should().BeTrue("Internal data may go to an Internal model: {0}", second.Access);
        run.ChatCalls.Should().Be(2);
    }

    // ---- ToolCallingAgent: every tool call is a read ----------------------------------------------------------------

    [Fact]
    public async Task An_unlabelled_tool_result_counts_as_SystemHigh_for_the_next_model_call()
    {
        var run = await RunAgentAsync(new PayloadTool("unlabelled", () => new { text = "a file the agent read" }));

        run.Decisions.Should().HaveCount(2);
        run.Decisions[0].Current.Should().Be(SecurityLabel.Public);
        run.Decisions[1].CurrentBasis.Should().Be(SubjectPrefix + run.Subject);
        run.Decisions[1].Current.Should().Be(SecurityLabel.SystemHigh, "an unreported read is unlabelled data");
        run.Decisions[1].Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    [Fact]
    public async Task Observe_on_a_side_value_does_not_label_a_tool_result()
    {
        var run = await RunAgentAsync(new PayloadTool("side-observe", () =>
        {
            EgressSubject.Observe(SecurityLabel.Public);
            return new { text = "unlabelled text" };
        }));

        run.Decisions[1].Current.Should().Be(SecurityLabel.SystemHigh, "Observe only raises; it never reports the read");
        run.Decisions[1].Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    [Fact]
    public async Task A_tool_that_throws_counts_as_SystemHigh()
    {
        var run = await RunAgentAsync(new PayloadTool("throws", () => throw new InvalidOperationException("read, then threw")));

        run.Cycle.StoppedReason.Should().Be("error");
        run.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "a tool that threw may have read data its message carries");
    }

    [Fact]
    public async Task A_tools_own_egress_during_its_call_is_decided_at_SystemHigh()
    {
        var tool = new EgressingTool();
        var run = await RunAgentAsync(tool);

        tool.Decisions.Should().HaveCount(2);
        tool.Decisions.Should().AllSatisfy(d =>
        {
            d.CurrentBasis.Should().Be(SubjectPrefix + run.Subject, "the tool runs inside the agent's frame");
            d.Current.Should().Be(SecurityLabel.SystemHigh, "a read that has not ended counts as SystemHigh");
            d.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        });
    }

    // ---- RAGTool: canonical tiers, "read nothing", and its own egress ------------------------------------------------

    public static TheoryData<string?> TierNames() =>
    [
        "Public", "public", " PUBLIC ",
        "Internal", "internal", "\tInternal\n",
        "Confidential", "confidential ", " CONFIDENTIAL",
        "Secret", "secret", " Secret ", "SECRET", " Secret",
        "TopSecret", "topsecret", " TOPSECRET ",
        "top-secret", "Top-Secret", " top-secret ",
        "Top Secret", "top_secret", "Unclassified", "Restricted", "SystemHigh",
        "", "   ", null,
    ];

    [Theory]
    [MemberData(nameof(TierNames))]
    public async Task RAGTool_labels_a_hit_exactly_as_TrustTierOrder_RecordLabel_does(string? tier)
    {
        var rag = new StubRag { Hits = [new VectorSearchResult("doc-1", "chunk text", 0.9, tier)] };
        var run = await RunAgentAsync(new RAGTool(rag));

        run.Mark.Current.Should().Be(
            TrustTierOrder.RecordLabel(tier),
            "RAGTool trims the name and maps only the five canonical names (and top-secret), as the RAG pipeline's record side does; tier {0}",
            tier is null ? "null" : "'" + tier + "'");
    }

    [Fact]
    public async Task RAGTool_reports_the_join_of_its_hits()
    {
        var rag = new StubRag
        {
            Hits =
            [
                new VectorSearchResult("doc-1", "a", 0.9, "Internal"),
                new VectorSearchResult("doc-2", "b", 0.8, "Secret"),
                new VectorSearchResult("doc-3", "c", 0.7, "Public"),
            ],
        };
        var run = await RunAgentAsync(new RAGTool(rag));

        run.Mark.Current.Should().Be(Secret);
        run.Decisions[1].Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
    }

    [Fact]
    public async Task RAGTool_reports_read_nothing_for_no_hits_and_for_its_unrankable_query_refusal()
    {
        var empty = await RunAgentAsync(new RAGTool(new StubRag { Hits = [] }));
        empty.Mark.Current.Should().Be(SecurityLabel.Public, "an empty search read nothing");
        empty.Decisions[1].Access.Allowed.Should().BeTrue("{0}", empty.Decisions[1].Access);

        var refused = await RunAgentAsync(new RAGTool(new StubRag { Refuse = true }));
        refused.Mark.Current.Should().Be(SecurityLabel.Public, "the refusal carries only the model's query and the store's message");
        refused.Decisions[1].Access.Allowed.Should().BeTrue("{0}", refused.Decisions[1].Access);
    }

    [Fact]
    public async Task RAGTools_own_egress_during_its_call_is_decided_at_SystemHigh()
    {
        var rag = new StubRag { Hits = [new VectorSearchResult("doc-1", "chunk", 0.9, "Public")], DecideWhileSearching = true };
        var run = await RunAgentAsync(new RAGTool(rag));

        rag.Decisions.Should().ContainSingle();
        rag.Decisions[0].CurrentBasis.Should().Be(SubjectPrefix + run.Subject);
        rag.Decisions[0].Current.Should().Be(SecurityLabel.SystemHigh, "no labelled-tool exemption: the read has not ended");
        rag.Decisions[0].Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        run.Mark.Current.Should().Be(SecurityLabel.Public, "the read ended reporting a Public hit");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task RAGTool_labels_a_hit_at_a_custom_level_SystemHigh_whatever_its_value(int value)
    {
        var registry = new DataSensitivityRegistry();
        registry.Register(new ConfigurableSensitivityLevel(
            "Restricted", "Restricted", value, AllowsExternalLLM: true, AllowsWebSearch: true, RequiresLocalOnly: false,
            AllowsNetworkExports: true, "a custom level"));
        var rag = new StubRag { Hits = [new VectorSearchResult("doc-1", "chunk", 0.9, " Restricted ")] };

        var run = await RunAgentAsync(new RAGTool(rag, sensitivityRegistry: registry));

        run.Mark.Current.Should().Be(
            SecurityLabel.SystemHigh, "only the five canonical names are mapped; a custom level fails closed (the owner's answer to Q8)");
    }

    [Fact]
    public async Task A_labelled_tool_that_reports_and_then_throws_counts_as_SystemHigh()
    {
        var run = await RunAgentAsync(new ReportThenThrowTool());

        run.Cycle.StoppedReason.Should().Be("error");
        run.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "a read that threw counts as SystemHigh whatever it reported");
    }

    [Fact]
    public async Task A_labelled_tool_behind_a_decorator_or_another_toolbox_is_not_labelled()
    {
        var hit = new VectorSearchResult("doc-1", "chunk", 0.9, "Public");

        var decorated = await RunAgentAsync(new Forwarding(new RAGTool(new StubRag { Hits = [hit] })));
        decorated.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "the decorator does not declare itself labelled");

        var other = await RunAgentAsync(new RAGTool(new StubRag { Hits = [hit] }), tools => new OtherToolbox(tools));
        other.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "only a CapabilityRegistry says which tool serves a call");

        var direct = await RunAgentAsync(new RAGTool(new StubRag { Hits = [hit] }));
        direct.Mark.Current.Should().Be(SecurityLabel.Public, "control: served directly, its Public hit is reported");
    }

    // ---- Agent-backed chat targets ----------------------------------------------------------------------------------

    [Fact]
    public async Task A_response_from_a_peer_counts_as_SystemHigh()
    {
        var key = "peer:twin-" + Guid.NewGuid().ToString("N")[..12];
        using var provider = GovernedClient(key, allowEveryTarget: false);
        var client = provider.GetRequiredKeyedService<IChatClient>(key);

        var mark = new HighWaterMark();
        using (EgressSubject.Enter("peer-caller", mark))
        {
            var response = await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
            response.Text.Should().Be("scripted");
            mark.Current.Should().Be(SecurityLabel.SystemHigh, "a peer's own data is not derived from our prompt");
        }
    }

    [Fact]
    public async Task A_streamed_response_from_a_peer_counts_as_SystemHigh_before_the_caller_sees_it()
    {
        var key = "peer:twin-" + Guid.NewGuid().ToString("N")[..12];
        using var provider = GovernedClient(key, allowEveryTarget: false);
        var client = provider.GetRequiredKeyedService<IChatClient>(key);

        var mark = new HighWaterMark();
        using (EgressSubject.Enter("peer-stream-caller", mark))
        {
            var seen = new List<SecurityLabel>();
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                _ = update;
                seen.Add(mark.Current);
            }

            seen.Should().NotBeEmpty();
            seen.Should().AllSatisfy(l => l.Should().Be(SecurityLabel.SystemHigh));
        }
    }

    [Fact]
    public async Task A_streamed_response_with_no_updates_from_a_peer_still_counts_as_SystemHigh()
    {
        var key = "peer:twin-" + Guid.NewGuid().ToString("N")[..12];
        using var provider = GovernedClient(key, allowEveryTarget: false, emptyStream: true);
        var client = provider.GetRequiredKeyedService<IChatClient>(key);

        var mark = new HighWaterMark();
        using (EgressSubject.Enter("peer-empty-stream-caller", mark))
        {
            var updates = 0;
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
            {
                _ = update;
                updates++;
            }

            updates.Should().Be(0, "the scripted peer streams nothing");
            mark.Current.Should().Be(SecurityLabel.SystemHigh, "the end of the stream is read too, with no update before it");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_call_to_a_peer_that_fails_still_counts_as_SystemHigh(bool streamed)
    {
        var key = "peer:twin-" + Guid.NewGuid().ToString("N")[..12];
        using var provider = GovernedClient(key, allowEveryTarget: false, fault: true);
        var client = provider.GetRequiredKeyedService<IChatClient>(key);

        var mark = new HighWaterMark();
        using (EgressSubject.Enter("peer-failed-caller", mark))
        {
            (await Call(client, streamed).Should().ThrowAsync<InvalidOperationException>()).WithMessage("peer unreachable*");
            mark.Current.Should().Be(
                SecurityLabel.SystemHigh, "however the call ends, whatever the peer sent before it failed has been read (streamed {0})", streamed);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_call_to_an_agent_backed_target_that_the_policy_denies_still_counts_as_SystemHigh(bool streamed)
    {
        // The default policy denies a key that is neither local: nor peer:, and PolicyGateChatClient, the layer directly
        // under the guard, throws synchronously from the call itself (not a faulted task): the guard's own catch path.
        var key = "a2a:twin-" + Guid.NewGuid().ToString("N")[..12];
        using var provider = GovernedClient(key, allowEveryTarget: false);
        var client = provider.GetRequiredKeyedService<IChatClient>(key);

        var mark = new HighWaterMark();
        using (EgressSubject.Enter("agent-target-denied-caller", mark))
        {
            await Call(client, streamed).Should().ThrowAsync<PolicyViolationException>();
            mark.Current.Should().Be(
                SecurityLabel.SystemHigh,
                "a call that ends by a synchronous throw under the guard is observed too (fail closed; streamed {0})",
                streamed);
        }
    }

    [Fact]
    public async Task A_response_from_a_target_that_names_no_model_endpoint_counts_as_SystemHigh()
    {
        var key = "a2a:twin-" + Guid.NewGuid().ToString("N")[..12];
        using var provider = GovernedClient(key, allowEveryTarget: true);
        var client = provider.GetRequiredKeyedService<IChatClient>(key);

        var mark = new HighWaterMark();
        using (EgressSubject.Enter("agent-target-caller", mark))
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
            mark.Current.Should().Be(SecurityLabel.SystemHigh, "a key that is not local: or cloud: may be backed by an agent (fail closed)");
        }
    }

    [Fact]
    public async Task A_model_response_is_not_a_read()
    {
        var key = "local:twin-" + Guid.NewGuid().ToString("N")[..12];
        using var provider = GovernedClient(key, allowEveryTarget: false);
        var client = provider.GetRequiredKeyedService<IChatClient>(key);

        var mark = new HighWaterMark();
        using (EgressSubject.Enter("model-caller", mark))
        {
            await client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
                _ = update;

            mark.Current.Should().Be(SecurityLabel.Public, "a model's response is derived from the prompt, which the subject already holds");
        }
    }

    // ---- Production self-extend --------------------------------------------------------------------------------------

    [Fact]
    public async Task Production_self_extend_records_its_agent_as_the_subject_at_SystemHigh()
    {
        var repo = Path.Combine(Path.GetTempPath(), "sx-egress-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(repo);
        try
        {
            var agentId = "sx-" + Guid.NewGuid().ToString("N")[..12];
            var model = new DecidingModel(Done());
            var runner = new SelfExtendRunnerAdapter(
                model, NullLogger<SelfExtendRunnerAdapter>.Instance, NullLoggerFactory.Instance);

            var result = await runner.RunAsync(
                repo, "an objective", "planner", modelProvider: null, modelName: null, agentId, CancellationToken.None);

            result.Iterations.Should().Be(1, "the scripted model stops at once: {0}", result.Summary);
            model.Decisions.Should().ContainSingle();
            model.Decisions[0].CurrentBasis.Should().Be(SubjectPrefix + "agent:" + agentId, "the runner's frame names the agent");
            model.Decisions[0].Current.Should().Be(SecurityLabel.SystemHigh, "self-extend's snapshot carries unlabelled carry-over");

            var outside = Decide();
            outside.CurrentBasis.Should().Be("no-subject", "the runner's frame never reaches the flow that awaited it");
        }
        finally
        {
            try { Directory.Delete(repo, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    // ---- Harness ----------------------------------------------------------------------------------------------------

    private static EgressDecision Decide() =>
        new EgressGuard("full").Evaluate(new EgressRequest(EgressFamilies.ModelMeai, "twin:producer:" + Guid.NewGuid().ToString("N"), new Uri(Remote)));

    private static string Calls(params (string Id, object Args)[] calls) =>
        JsonSerializer.Serialize(new
        {
            tool_calls = calls.Select(c => new { id = c.Id, arguments = c.Args }).ToArray(),
            rationale = "scripted",
        });

    private static string Done() => JsonSerializer.Serialize(new { tool_calls = Array.Empty<object>(), rationale = "done" });

    private sealed record AgentRun(string Subject, HighWaterMark Mark, AgentCycleResult Cycle, IReadOnlyList<EgressDecision> Decisions);

    /// <summary>A test runner: a frame at Public around one cycle whose first turn calls <paramref name="tool"/> once.</summary>
    private static async Task<AgentRun> RunAgentAsync(ITool tool, Func<CapabilityRegistry, IToolbox>? toolbox = null)
    {
        var subject = "agent:twin-" + Guid.NewGuid().ToString("N")[..12];
        var model = new DecidingModel(Calls((tool.Id, new { query = "q", minScore = 0 })), Done());
        var registry = new CapabilityRegistry();
        registry.Register(tool);
        var tools = toolbox is null ? registry : toolbox(registry);
        var agent = new ToolCallingAgent("twin", model, NullLogger<ToolCallingAgent>.Instance);
        var mark = new HighWaterMark(SecurityLabel.Public);

        AgentCycleResult cycle;
        using (EgressSubject.Enter(subject, mark))
        {
            cycle = await agent.RunCycleAsync(
                WorldSnapshot.ForRepo("/repo", "/repo/out"), tools, new PolicyEngine([]), onRejected: null, memory: null, CancellationToken.None);
        }

        return new AgentRun(subject, mark, cycle, model.Decisions);
    }

    private sealed record LeakRun(
        string Subject, string Canary, AgentCycleResult Cycle, IReadOnlyList<EgressDecision> Decisions, int ChatCalls, string SecondCallText);

    private static async Task<LeakRun> RunLeakSkeletonAsync(string tier)
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var subject = "agent:leak-" + id;
        var canary = "CANARY-" + id;
        var chunk = canary + " lives in the vault";
        var host = "gpu-" + id + ".example";

        var guard = new RecordingGuard(new EgressGuard("secure-workstation"), host);
        var chat = new ScriptedChatClient(
            new Uri("http://" + host + ":11434"),
            Calls((RAGTool.DefaultId, new { query = chunk, minScore = 0 })),
            Done());

        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarMeaiPipeline(
            ollamaInnerFactory: _ => chat,
            onnxInnerFactory: _ => new FakeChatClient(),
            registerDefaultRouter: false);
        await using var provider = services.BuildServiceProvider();

        var rag = provider.GetRequiredService<VectorDataRagService>();
        await rag.IndexAsync("leak-" + id, chunk, trustTier: tier);

        var model = new MeaiBackedModel(
            provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama), NullLogger<MeaiBackedModel>.Instance);
        var tools = new CapabilityRegistry();
        tools.Register(new RAGTool(new MeaiVectorDataRagAdapter(rag)));
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?>
        {
            ["agentId"] = "leak-" + id,
            ["maxDataSensitivity"] = "Secret",
        });

        AgentCycleResult cycle;
        using (EgressSubject.Enter(subject, new HighWaterMark(SecurityLabel.Public)))
        {
            cycle = await new ToolCallingAgent("leak", model, NullLogger<ToolCallingAgent>.Instance)
                .RunCycleAsync(snapshot, tools, new PolicyEngine([]), onRejected: null, memory: null, CancellationToken.None);
        }

        return new LeakRun(subject, canary, cycle, guard.Decisions, chat.Calls, chat.CallText(1));
    }

    private static ServiceProvider GovernedClient(string key, bool allowEveryTarget, bool emptyStream = false, bool fault = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(new EgressGuard("full"));
        if (allowEveryTarget)
            services.AddSingleton<IChatTargetAccessPolicy>(new AllowEveryTarget());

        services.AddAshlarGovernedChatClient(key, _ => new ScriptedChatClient(new Uri("https://agent.example/"), "scripted")
        {
            EmptyStream = emptyStream,
            Fault = fault,
        });
        return services.BuildServiceProvider();
    }

    /// <summary>One call on <paramref name="client"/>, streamed or not, as a task that completes when the call has ended.</summary>
    private static Func<Task> Call(IChatClient client, bool streamed) => streamed
        ? async () =>
        {
            await foreach (var update in client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
                _ = update;
        }
        : () => client.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

    /// <summary>A model that decides one egress per call on the calling flow, then returns its next scripted turn.</summary>
    private sealed class DecidingModel(params string[] turns) : IModel
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();
        private int _next;

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public Task<ModelOutput> CompleteAsync(ModelInput input, CancellationToken ct)
        {
            _decisions.Enqueue(Decide());
            var turn = _next < turns.Length ? turns[_next++] : Done();
            return Task.FromResult(new ModelOutput(turn));
        }
    }

    /// <summary>An unlabelled tool: its result is whatever <c>payload</c> returns.</summary>
    private sealed class PayloadTool(string id, Func<object> payload) : ITool
    {
        public string Id => id;

        public ToolSchema Schema => new(id, id, """{"type":"object"}""");

        public async Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct)
        {
            await Task.Yield();
            return new ToolResult(new ActionDelta(s.Tick, s.Tick + 1, [id]), payload());
        }
    }

    /// <summary>A tool that egresses during its call, before and after an await.</summary>
    private sealed class EgressingTool : ITool
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public string Id => "egresses";

        public ToolSchema Schema => new(Id, Id, """{"type":"object"}""");

        public async Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct)
        {
            _decisions.Enqueue(Decide());
            await Task.Yield();
            _decisions.Enqueue(Decide());
            return new ToolResult(new ActionDelta(s.Tick, s.Tick + 1, [Id]), new { sent = true });
        }
    }

    /// <summary>A labelled tool that reports "read nothing" and then throws.</summary>
    private sealed class ReportThenThrowTool : ILabelledTool
    {
        public string Id => "labelled-throws";

        public ToolSchema Schema => new(Id, Id, """{"type":"object"}""");

        public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct) =>
            throw new InvalidOperationException("unlabelled path");

        public async Task<ToolResult> InvokeLabelledAsync(ToolCall toolCall, WorldSnapshot s, ReadScope read, CancellationToken ct)
        {
            read.Report(SecurityLabel.Public);
            await Task.Yield();
            throw new InvalidOperationException("read, reported, then threw");
        }
    }

    /// <summary>A decorator that forwards to a tool and does not declare itself labelled.</summary>
    private sealed class Forwarding(ITool inner) : ITool
    {
        public string Id => inner.Id;

        public ToolSchema Schema => inner.Schema;

        public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct) => inner.InvokeAsync(toolCall, s, ct);
    }

    /// <summary>A toolbox that forwards to a registry, so it does not say which tool serves a call.</summary>
    private sealed class OtherToolbox(CapabilityRegistry inner) : IToolbox
    {
        public IEnumerable<ToolSchema> Schemas() => inner.Schemas();

        public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct) => inner.InvokeAsync(toolCall, s, ct);

        public IAgentMemory MemoryFor(IAgent agent) => inner.MemoryFor(agent);
    }

    /// <summary>A RAG store with fixed hits, or the unrankable-query refusal; it can egress while it searches.</summary>
    private sealed class StubRag : IRAGService
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public IReadOnlyList<VectorSearchResult> Hits { get; init; } = [];

        public bool Refuse { get; init; }

        public bool DecideWhileSearching { get; init; }

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            string query, int maxResults, double minScore, string? maxSensitivityLevelName, CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (DecideWhileSearching)
                _decisions.Enqueue(Decide());

            if (Refuse)
                throw new ArgumentException("The query has no magnitude and cannot be ranked.", nameof(query));

            return Hits;
        }

        public Task IndexAsync(string id, string text, string? sensitivityLevelName, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task RemoveAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task ClearAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> GetDocumentCountAsync(CancellationToken cancellationToken = default) => Task.FromResult(Hits.Count);
    }

    /// <summary>
    /// An inner chat client that says where it dials and returns its scripted turns in order; it can stream nothing,
    /// or fail as a faulted task or a faulted stream.
    /// </summary>
    private sealed class ScriptedChatClient(Uri dials, params string[] turns) : IChatClient
    {
        private readonly ConcurrentQueue<string> _calls = new();
        private int _next;

        public bool EmptyStream { get; init; }

        public bool Fault { get; init; }

        public int Calls => _calls.Count;

        public string CallText(int index) => _calls.ElementAtOrDefault(index) ?? string.Empty;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(string.Join("\n", messages.Select(m => m.Text)));
            return Fault
                ? Task.FromException<ChatResponse>(new InvalidOperationException("peer unreachable (faulted task)"))
                : Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, Next())));
        }

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            _calls.Enqueue(string.Join("\n", messages.Select(m => m.Text)));
            await Task.Yield();
            if (Fault)
                throw new InvalidOperationException("peer unreachable (faulted stream)");
            if (EmptyStream)
                yield break;

            var text = Next();
            foreach (var part in new[] { text[..(text.Length / 2)], text[(text.Length / 2)..] })
            {
                await Task.Yield();
                yield return new ChatResponseUpdate(ChatRole.Assistant, part);
            }
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata) ? new ChatClientMetadata("scripted", dials, "scripted-model") : null;

        public void Dispose()
        {
        }

        private string Next()
        {
            var i = Interlocked.Increment(ref _next) - 1;
            return i < turns.Length ? turns[i] : turns.Length > 0 ? turns[^1] : string.Empty;
        }
    }

    /// <summary>Records the decisions for one destination host, deciding through an explicit-profile guard.</summary>
    private sealed class RecordingGuard(IEgressGuard inner, string host) : IEgressGuard
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public EgressDecision Evaluate(EgressRequest request)
        {
            var decision = inner.Evaluate(request);
            if (decision.Destination.Contains(host, StringComparison.Ordinal))
                _decisions.Enqueue(decision);
            return decision;
        }
    }

    private sealed class AllowEveryTarget : IChatTargetAccessPolicy
    {
        public bool IsAllowed(string? callerIdentity, string targetKey, string? modelId, out string? denyReason)
        {
            denyReason = null;
            return true;
        }
    }
}
