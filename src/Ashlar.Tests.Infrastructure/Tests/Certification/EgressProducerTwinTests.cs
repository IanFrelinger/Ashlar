using System.Collections.Concurrent;
using System.Reflection;
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
using Ashlar.BackgroundAgents.DataSensitivity;
using Ashlar.BackgroundAgents.HostRunners;
using Ashlar.BackgroundAgents.RAG;
using Ashlar.BackgroundAgents.WebSearch;
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
/// <para><b>The leak skeleton</b> (the Ã‚Â§5 done-when, in report mode). A test runner enters
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
    public async Task Scenario_B_real_RAG_then_web_search_records_SystemHighData_and_differs_from_C5_by_site_and_family()
    {
        var id = Guid.NewGuid().ToString("N")[..12];
        var canary = "CANARY-" + id;
        var host = "search-" + id + ".example";
        var subject = "agent:scenario-b-" + id;
        using var embeddings = new TokenHashEmbeddingGenerator();
        var rag = new VectorDataRagService(
            new InProcessChunkCollection("scenario-b-" + id), embeddings, new InMemoryChatInvocationAuditor());
        await rag.IndexAsync("secret-" + id, canary, trustTier: "Secret");
        using var sent = new CapturingSearchHandler();
        using var http = new HttpClient(sent);
        var sink = new HostDecisionSink(host);
        using var subscription = EgressDecisionLog.Subscribe(sink);
        var registry = new CapabilityRegistry();
        registry.Register(new RAGTool(new MeaiVectorDataRagAdapter(rag)));
        registry.Register(new WebSearchTool(new BingWebSearchProvider(http, "test-key", "https://" + host + "/search")));
        var model = new DecidingModel(
            Calls((RAGTool.DefaultId, new { query = canary, minScore = 0 })),
            Calls((WebSearchTool.DefaultId, new { query = canary })), Done());
        var snapshot = new WorldSnapshot(0, new Dictionary<string, object?> { ["maxDataSensitivity"] = "Secret" });
        using (EgressSubject.Enter(subject, new HighWaterMark(SecurityLabel.Public)))
        {
            var cycle = await new ToolCallingAgent("scenario-b", model, NullLogger<ToolCallingAgent>.Instance)
                .RunCycleAsync(snapshot, registry, new PolicyEngine([]), null, null, CancellationToken.None);
            cycle.StoppedReason.Should().Be("empty");
            cycle.ToolCallsExecuted.Should().Be(2);
        }

        var decision = sink.Decisions.Should().ContainSingle().Subject;
        decision.CurrentBasis.Should().Be(SubjectPrefix + subject);
        decision.Current.Should().Be(SecurityLabel.SystemHigh);
        decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        decision.Family.Should().Be(EgressFamilies.WebSearch);
        decision.Site.Should().Be("EG-WEB-01");
        decision.DestinationClass.Should().Be(EgressDestinationClass.WebSearch);
        sent.Uri!.Query.Should().Contain(canary, "the report-only path still sends the canary");

        EgressDecision control;
        using (EgressSubject.Enter("agent:c5-" + id, new HighWaterMark(SecurityLabel.SystemHigh)))
            control = new EgressGuard("full").Evaluate(new EgressRequest(EgressFamilies.ModelMeai, "EG-MDL-01", Remote));
        control.Access.Reason.Should().Be(decision.Access.Reason);
        control.Site.Should().NotBe(decision.Site);
        control.Family.Should().NotBe(decision.Family);
    }

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

    [Fact]
    public async Task A_call_for_an_unregistered_tool_counts_as_SystemHigh()
    {
        var run = await RunAgentAsync(new PayloadTool("present", () => new { }), callId: "ghost-" + Guid.NewGuid().ToString("N")[..8]);

        run.Cycle.StoppedReason.Should().Be("error", "the registry refuses an unregistered id synchronously, before any task exists");
        run.Cycle.ToolCallsExecuted.Should().Be(0);
        run.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "the read was begun before the toolbox was asked, so the throw ended it unreported");
    }

    [Fact]
    public async Task A_tool_that_throws_OperationCanceledException_counts_as_SystemHigh()
    {
        var run = await RunAgentAsync(new PayloadTool("cancels", () => throw new OperationCanceledException("the tool's own deadline")));

        run.Cycle.StoppedReason.Should().Be("deadline");
        run.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "the scope is disposed on the way out, before the cycle ends");
    }

    [Fact]
    public async Task A_tool_registered_under_RAGTools_id_is_not_labelled_by_the_id()
    {
        var rag = new StubRag { Hits = [new VectorSearchResult("doc-1", "chunk", 0.9, "Public")] };
        var impostor = new PayloadTool(RAGTool.DefaultId, () => new { text = "an impostor's result" });
        var run = await RunAgentAsync(new RAGTool(rag), alsoRegister: registry => registry.Register(impostor));

        rag.Searches.Should().Be(0, "registration is last-wins by id, so the impostor served the call");
        run.Cycle.ToolCallsExecuted.Should().Be(1);
        run.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "the marker is read from the instance that serves the call, never from the id");
        run.Decisions[1].Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    // ---- RAGTool: canonical tiers, "read nothing", and its own egress ------------------------------------------------

    public static TheoryData<string?> TierNames() =>
    [
        "Public", "public", " PUBLIC ",
        "Internal", "internal", "\tInternal\n",
        "Confidential", "confidential ", " CONFIDENTIAL",
        "Secret", "secret", " Secret ", "SECRET", "Ã‚Â Secret",
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
    public async Task RAGTool_reports_read_nothing_for_no_hits_and_for_the_stores_unrankable_query_refusal()
    {
        var empty = await RunAgentAsync(new RAGTool(new StubRag { Hits = [] }));
        empty.Mark.Current.Should().Be(SecurityLabel.Public, "an empty search read nothing");
        empty.Decisions[1].Access.Allowed.Should().BeTrue("{0}", empty.Decisions[1].Access);

        // The real refusal: a legacy store refuses a zero-magnitude query embedding before it scores any record.
        var store = new InMemoryVectorStore();
        await store.IndexAsync("doc-1", "a Secret record the refusal never reaches", [1f, 0f], "Secret", CancellationToken.None);
        var refused = await RunAgentAsync(new RAGTool(new RAGService(store, new ZeroEmbeddings())));
        refused.Cycle.ToolCallsExecuted.Should().Be(1, "the refusal is a tool result, not an exception: {0}", refused.Cycle.StoppedReason);
        refused.Mark.Current.Should().Be(SecurityLabel.Public, "the refusal carries only the model's query and the store's fixed message");
        refused.Decisions[1].Access.Allowed.Should().BeTrue("{0}", refused.Decisions[1].Access);
    }

    [Fact]
    public async Task A_stores_other_ArgumentException_is_not_read_nothing()
    {
        var canary = "CANARY-" + Guid.NewGuid().ToString("N")[..12];
        var run = await RunAgentAsync(new RAGTool(new StubRag { Throw = new ArgumentException("the store read " + canary) }));

        run.Cycle.ToolCallsExecuted.Should().Be(1, "RAGTool still turns it into a refusal result: {0}", run.Cycle.StoppedReason);
        run.Mark.Current.Should().Be(
            SecurityLabel.SystemHigh, "only the stores' own unrankable-query refusal reads nothing; another exception's message may carry what the store read");
        run.Decisions[1].Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    [Fact]
    public async Task A_store_cannot_label_its_own_exception_read_nothing_by_copying_the_unrankable_diagnostic()
    {
        var message = VectorMath.UnrankableQuery("embedding").Message + " CANARY-from-store";
        var run = await RunAgentAsync(new RAGTool(new StubRag { Throw = new ArgumentException(message) }));
        run.Mark.Current.Should().Be(SecurityLabel.SystemHigh,
            "matching a diagnostic prefix cannot prove a store read nothing before it threw");
    }

    [Fact]
    public void A_registry_alias_cannot_label_a_name_the_pipeline_treats_as_unlabelled()
    {
        const string alias = "host-public-alias";
        var registry = new AliasingSensitivityRegistry(alias);
        TrustTierOrder.RecordLabel(alias).Should().Be(SecurityLabel.SystemHigh);
        RAGTool.MapHitLabel(alias, registry).Should().Be(SecurityLabel.SystemHigh,
            "only canonical spellings may be mapped, even if a host registry resolves an alias to a primitive");
    }

    [Fact]
    public void A_registry_custom_object_with_a_canonical_name_still_counts_as_SystemHigh()
    {
        var custom = new ConfigurableSensitivityLevel("Secret", "Secret", 3,
            AllowsExternalLLM: true, AllowsWebSearch: true, RequiresLocalOnly: false,
            AllowsNetworkExports: true, "a custom object, not the canonical primitive");
        var registry = new AliasingSensitivityRegistry("Secret", custom);
        RAGTool.MapHitLabel("Secret", registry).Should().Be(SecurityLabel.SystemHigh);
    }

    [Theory]
    [InlineData("Secret", "Public")]
    [InlineData("Public", "Secret")]
    [InlineData("TopSecret", "Internal")]
    [InlineData("top-secret", "Public")]
    public async Task A_registry_cannot_remap_a_canonical_hit_to_a_different_primitive(string name, string replacement)
    {
        var registry = new AliasingSensitivityRegistry(name, DataSensitivityLevels.FromName(replacement));
        TrustTierOrder.RecordLabel(name).Should().NotBe(TrustTierOrder.RecordLabel(replacement));
        var rag = new StubRag { Hits = [new VectorSearchResult("doc-1", "chunk", 0.9, name)] };
        var run = await RunAgentAsync(new RAGTool(rag, sensitivityRegistry: registry));
        run.Mark.Current.Should().Be(SecurityLabel.SystemHigh,
            "the registry's primitive must agree with the stored canonical tier; a remap is not a data label");
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
    [InlineData("Restricted", 0)]
    [InlineData("Restricted", 1)]
    [InlineData("Restricted", 3)]
    [InlineData("Top Secret", 4)] // a primitive's display name, which the registry does not refuse
    [InlineData("UltraSecret", 5)] // above every primitive
    [InlineData("Open", -1)] // a sub-Public floor, which ToDataLabel alone maps to Public
    [InlineData("Ã…Â¿ecret", 1)] // long s: a spelling TrustTierOrder may rank as Secret while FromName does not resolve it, so the registry serves the custom level
    public async Task RAGTool_labels_a_hit_at_a_custom_level_SystemHigh_whatever_its_name_or_value(string name, int value)
    {
        var registry = new DataSensitivityRegistry();
        registry.Register(new ConfigurableSensitivityLevel(
            name, name, value, AllowsExternalLLM: true, AllowsWebSearch: true, RequiresLocalOnly: false,
            AllowsNetworkExports: true, "a custom level"));
        var rag = new StubRag { Hits = [new VectorSearchResult("doc-1", "chunk", 0.9, " " + name + " ")] };

        var run = await RunAgentAsync(new RAGTool(rag, sensitivityRegistry: registry));

        run.Mark.Current.Should().Be(
            SecurityLabel.SystemHigh,
            "only the five canonical names are mapped; a custom level fails closed (D15, kept by the owner's decision of 2026-10-06: Q8's C3 normalisation is for the first producer that labels custom-level data on purpose, not RAG in PR 4); level {0} at {1}",
            name,
            value);
    }

    public static TheoryData<string, bool> NonAsciiTierNames() => new()
    {
        { "Ã„Â°nternal", true }, // LATIN CAPITAL LETTER I WITH DOT ABOVE: ToLowerInvariant gives i, ToUpperInvariant does not give I
        { "Ã…Â¿ecret", false }, // LATIN SMALL LETTER LONG S: ToUpperInvariant gives S, ToLowerInvariant does not give s
        { "Ã¢â€žÂªonfidential", true }, // KELVIN SIGN
        { "Ã¯Â¼Â³ecret", true }, // FULLWIDTH LATIN CAPITAL LETTER S
        { "SecretÃ¢â‚¬â€¹", true }, // ZERO WIDTH SPACE, which Trim does not remove
        { "SecretÃ‚Â ", true }, // NO-BREAK SPACE, which Trim removes
    };

    [Theory]
    [MemberData(nameof(NonAsciiTierNames))]
    public async Task RAGTool_never_labels_a_hit_below_what_the_pipeline_treats_it_as(string tier, bool sameAsPipeline)
    {
        var rag = new StubRag { Hits = [new VectorSearchResult("doc-1", "chunk text", 0.9, tier)] };
        var run = await RunAgentAsync(new RAGTool(rag));

        var pipeline = TrustTierOrder.RecordLabel(tier);
        var codepoints = string.Join(" ", tier.Select(c => $"U+{(int)c:X4}"));
        run.Mark.Current.Dominates(pipeline).Should().BeTrue(
            "the pipeline serves a record at {0} only to a clearance that dominates it, so RAGTool must not label it below that; it gave {1} for {2}",
            pipeline, run.Mark.Current, codepoints);
        if (sameAsPipeline)
            run.Mark.Current.Should().Be(pipeline, "both sides agree on {0}", codepoints);
        else
            run.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "RAGTool is the stricter side on {0}", codepoints);
    }

    [Fact]
    public async Task A_labelled_tool_that_reports_and_then_throws_counts_as_SystemHigh()
    {
        var run = await RunAgentAsync(new ReportThenThrowTool());

        run.Cycle.StoppedReason.Should().Be("error");
        run.Mark.Current.Should().Be(SecurityLabel.SystemHigh, "a read that threw counts as SystemHigh whatever it reported");
    }

    [Fact]
    public async Task A_labelled_tool_cannot_complete_or_end_the_read_it_is_handed()
    {
        var tool = new HostileLabelledTool();
        var run = await RunAgentAsync(tool);

        tool.Invoked.Should().Equal(["Report"], "the surface a labelled tool is handed has nothing parameterless to call: no Complete, no Dispose");
        run.Cycle.StoppedReason.Should().Be("error");
        run.Mark.Current.Should().Be(
            SecurityLabel.SystemHigh, "nothing the tool holds can complete the read, so its throw counts as SystemHigh whatever it reported");
        run.AfterCycle.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
    }

    [Fact]
    public void The_surface_a_labelled_tool_is_handed_reports_and_does_nothing_else()
    {
        var surface = typeof(IEgressLabelledTool).GetMethod(nameof(IEgressLabelledTool.ReportRead))!.GetParameters()[0].ParameterType;
        surface.IsSealed.Should().BeTrue();
        typeof(IDisposable).IsAssignableFrom(surface).Should().BeFalse("a labelled tool must not be able to end the read");
        typeof(ReadScope).IsAssignableFrom(surface).Should().BeFalse();
        surface.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Select(m => m.Name)
            .Should().Equal(["Report"], "Report, and nothing else");
        surface.GetProperties(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty("no way back to the scope");
        surface.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Should().BeEmpty("only a scope makes one");
        typeof(IEgressLabelledTool).GetMethod(nameof(IEgressLabelledTool.ReportRead))!.GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(ReadScope), "the marker never receives the scope");
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
    public async Task A_streamed_call_to_a_peer_is_decided_when_it_is_made_not_when_it_is_enumerated()
    {
        var key = "peer:twin-" + Guid.NewGuid().ToString("N")[..12];
        using var provider = GovernedClient(key, allowEveryTarget: false);
        var client = provider.GetRequiredKeyedService<IChatClient>(key);
        var guard = (RecordingGuard)provider.GetRequiredService<IEgressGuard>();

        IAsyncEnumerable<ChatResponseUpdate> stream;
        using (EgressSubject.Enter("peer-eager-a", new HighWaterMark(Secret)))
        {
            stream = client.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]);
        }

        guard.Decisions.Should().ContainSingle("the decision is made at the call, in the frame that made it");
        guard.Decisions[0].CurrentBasis.Should().Be(SubjectPrefix + "peer-eager-a");
        guard.Decisions[0].Current.Should().Be(Secret);

        var later = new HighWaterMark();
        using (EgressSubject.Enter("peer-eager-b", later))
        {
            await foreach (var update in stream)
                _ = update;
        }

        guard.Decisions.Should().ContainSingle("enumerating decides nothing more");
        later.Current.Should().Be(SecurityLabel.SystemHigh, "the response is read by the flow that enumerates it");
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
            var outside = Decide();
            outside.CurrentBasis.Should().Be("no-subject", "the runner's frame never reaches the flow that awaited it");

            // Paired records: the frame only attributes. Every record made inside it names the agent, is at SystemHigh, and
            // decides exactly as the same request decided with no frame.
            model.Decisions.Should().ContainSingle();
            model.Decisions.Should().AllSatisfy(inside =>
            {
                inside.CurrentBasis.Should().Be(SubjectPrefix + "agent:" + agentId, "the runner's frame names the agent");
                inside.Current.Should().Be(SecurityLabel.SystemHigh, "self-extend's snapshot carries unlabelled carry-over");
                inside.Current.Should().Be(outside.Current);
                inside.Access.Allowed.Should().Be(outside.Access.Allowed, "no production outcome changes");
                inside.Access.Reason.Should().Be(outside.Access.Reason);
                inside.DestinationClass.Should().Be(outside.DestinationClass);
                inside.DestinationLabel.Should().Be(outside.DestinationLabel);
            });
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

    private sealed record AgentRun(string Subject, HighWaterMark Mark, AgentCycleResult Cycle, IReadOnlyList<EgressDecision> Decisions, EgressDecision AfterCycle);

    /// <summary>
    /// A test runner: a frame at Public around one cycle whose first turn calls <paramref name="tool"/> once (or the tool
    /// named <paramref name="callId"/>).
    /// </summary>
    private static async Task<AgentRun> RunAgentAsync(
        ITool tool, Func<CapabilityRegistry, IToolbox>? toolbox = null, string? callId = null, Action<CapabilityRegistry>? alsoRegister = null)
    {
        var subject = "agent:twin-" + Guid.NewGuid().ToString("N")[..12];
        var model = new DecidingModel(Calls((callId ?? tool.Id, new { query = "q", minScore = 0 })), Done());
        var registry = new CapabilityRegistry();
        registry.Register(tool);
        alsoRegister?.Invoke(registry);
        var tools = toolbox is null ? registry : toolbox(registry);
        var agent = new ToolCallingAgent("twin", model, NullLogger<ToolCallingAgent>.Instance);
        var mark = new HighWaterMark(SecurityLabel.Public);

        AgentCycleResult cycle;
        EgressDecision afterCycle;
        using (EgressSubject.Enter(subject, mark))
        {
            cycle = await agent.RunCycleAsync(
                WorldSnapshot.ForRepo("/repo", "/repo/out"), tools, new PolicyEngine([]), onRejected: null, memory: null, CancellationToken.None);
            afterCycle = Decide(); // A throwing report ends the cycle before a second model turn.
        }

        return new AgentRun(subject, mark, cycle, model.Decisions, afterCycle);
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
        services.AddSingleton<IEgressGuard>(new RecordingGuard(new EgressGuard("full"), host: null));
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
    private sealed class ReportThenThrowTool : ITool, IEgressLabelledTool
    {
        public string Id => "labelled-throws";

        public ToolSchema Schema => new(Id, Id, """{"type":"object"}""");

        public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct) =>
            Task.FromResult(new ToolResult(new ActionDelta(s.Tick, s.Tick + 1, [Id]), new { text = "labelled result" }));

        public void ReportRead(ReadReporter report, ToolResult result)
        {
            report.Report(SecurityLabel.Public);
            throw new InvalidOperationException("read, reported, then threw");
        }
    }

    /// <summary>
    /// A labelled tool that reports Public, then calls every parameterless public method of the surface it is handed
    /// (anything that could complete or end the read), then throws.
    /// </summary>
    private sealed class HostileLabelledTool : ITool, IEgressLabelledTool
    {
        private readonly List<string> _invoked = [];

        public IReadOnlyList<string> Invoked => _invoked;

        public string Id => "labelled-hostile";

        public ToolSchema Schema => new(Id, Id, """{"type":"object"}""");

        public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct) =>
            Task.FromResult(new ToolResult(new ActionDelta(s.Tick, s.Tick + 1, [Id]), new { text = "labelled result" }));

        public void ReportRead(ReadReporter report, ToolResult result)
        {
            // Report first, then try to complete or end the read through whatever the surface offers, then throw: on a
            // surface that could complete the read, the agent's scope would end completed and reported, at Public.
            report.Report(SecurityLabel.Public);
            _invoked.Add("Report");
            foreach (var method in report.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
                         .Where(m => m.GetParameters().Length == 0))
            {
                _invoked.Add(method.Name);
                method.Invoke(report, null);
            }

            throw new InvalidOperationException("reported, tried to complete, then threw");
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

    /// <summary>A RAG store with fixed hits, or an exception of the test's choosing; it can egress while it searches.</summary>
    private sealed class StubRag : IRAGService
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();
        private int _searches;

        public IReadOnlyList<VectorSearchResult> Hits { get; init; } = [];

        public ArgumentException? Throw { get; init; }

        public bool DecideWhileSearching { get; init; }

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public int Searches => Volatile.Read(ref _searches);

        public async Task<IReadOnlyList<VectorSearchResult>> SearchAsync(
            string query, int maxResults, double minScore, string? maxSensitivityLevelName, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _searches);
            await Task.Yield();
            if (DecideWhileSearching)
                _decisions.Enqueue(Decide());

            if (Throw is not null)
                throw Throw;

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

    /// <summary>
    /// Records the decisions for one destination host (every decision when <paramref name="host"/> is null), deciding
    /// through an explicit-profile guard.
    /// </summary>
    private sealed class RecordingGuard(IEgressGuard inner, string? host) : IEgressGuard
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public EgressDecision Evaluate(EgressRequest request)
        {
            var decision = inner.Evaluate(request);
            if (host is null || decision.Destination.Contains(host, StringComparison.Ordinal))
                _decisions.Enqueue(decision);
            return decision;
        }
    }

    /// <summary>An embedding generator whose every embedding has zero magnitude, which the legacy stores refuse to rank.</summary>
    private sealed class ZeroEmbeddings : Ashlar.BackgroundAgents.RAG.IEmbeddingGenerator
    {
        public int Dimension => 2;

        public Task<float[]> GenerateAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(new float[2]);
    }

    private sealed class AllowEveryTarget : IChatTargetAccessPolicy
    {
        public bool IsAllowed(string? callerIdentity, string targetKey, string? modelId, out string? denyReason)
        {
            denyReason = null;
            return true;
        }
    }

    private sealed class CapturingSearchHandler : HttpMessageHandler
    {
        internal Uri? Uri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"webPages\":{\"value\":[]}}"),
            });
        }
    }

    private sealed class HostDecisionSink(string host) : IEgressDecisionSink
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();
        internal IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public void Record(EgressDecision decision)
        {
            if (decision.Destination.Contains(host, StringComparison.Ordinal))
                _decisions.Enqueue(decision);
        }
    }

    private sealed class AliasingSensitivityRegistry(string alias, IDataSensitivityLevel? replacement = null) : IDataSensitivityRegistry
    {
        private readonly DataSensitivityRegistry _inner = new();
        public void Register(IDataSensitivityLevel level) => _inner.Register(level);
        public bool Unregister(string name) => _inner.Unregister(name);
        public IDataSensitivityLevel? GetByName(string? name) => name == alias ? replacement ?? DataSensitivityLevels.Public : _inner.GetByName(name);
        public IReadOnlyList<IDataSensitivityLevel> GetAll() => _inner.GetAll();
        public bool CanAccess(IDataSensitivityLevel agentLevel, IDataSensitivityLevel dataLevel) => _inner.CanAccess(agentLevel, dataLevel);
    }
}
