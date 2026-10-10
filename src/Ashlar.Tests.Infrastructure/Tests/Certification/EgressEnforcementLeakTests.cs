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
using Ashlar.BackgroundAgents.WebSearch;
using Ashlar.Hosting;
using Ashlar.Hosting.Meai;
using Ashlar.Infrastructure.Egress;
using Ashlar.Runtime;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
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
        if (reason == AccessDenialReason.LevelTooLow)
            second.Access.Detail.Should().Contain("Secret").And.Contain("Internal");
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

    [Fact]
    public async Task Scenario_B_real_web_search_is_refused_before_transport_and_the_local_model_receives_a_redacted_observation()
    {
        var id = Guid.NewGuid().ToString("N");
        var subject = "agent:web-leak-" + id;
        var canary = "CANARY-" + id;
        var host = "search-" + id + ".example";
        var records = new ConcurrentQueue<EgressDecision>();
        using var subscription = EgressDecisionLog.Subscribe(new Sink(d =>
        {
            if (d.CurrentBasis == "subject:" + subject) records.Enqueue(d);
        }));
        var auditor = new InMemoryChatInvocationAuditor();
        using var embeddings = new TokenHashEmbeddingGenerator();
        var rag = new VectorDataRagService(new InProcessChunkCollection("web-leak-" + id), embeddings, auditor);
        await rag.IndexAsync(id, canary, trustTier: "Secret");
        using var transport = new SearchHandler();
        using var http = new HttpClient(transport);
        var chat = new ScriptedChatClient(new Uri("http://127.0.0.1:11434"),
            Calls(RAGTool.DefaultId, new { query = canary, minScore = 0 }),
            Calls(WebSearchTool.DefaultId, new { query = canary }), Done());
        var services = new ServiceCollection();
        services.AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation);
        services.AddSingleton<IChatInvocationAuditor>(auditor);
        services.AddSingleton(rag);
        services.AddAshlarMeaiPipeline(ollamaInnerFactory: _ => chat,
            onnxInnerFactory: _ => new FakeChatClient(), registerDefaultRouter: false);
        await using var provider = services.BuildServiceProvider();
        var model = new MeaiBackedModel(provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama),
            NullLogger<MeaiBackedModel>.Instance);
        var registry = new CapabilityRegistry();
        registry.Register(new RAGTool(new MeaiVectorDataRagAdapter(rag)));
        registry.Register(new WebSearchTool(new BingWebSearchProvider(http, "test-key", "https://" + host + "/search")));
        var memory = new InMemoryAgentMemory();
        using var frame = EgressSubject.Enter(subject, new HighWaterMark(SecurityLabel.Public));
        var cycle = await new ToolCallingAgent("web-leak", model, NullLogger<ToolCallingAgent>.Instance)
            .RunCycleAsync(new WorldSnapshot(0, new Dictionary<string, object?> { ["maxDataSensitivity"] = "Secret" }),
                registry, new PolicyEngine([]), null, memory, CancellationToken.None);

        cycle.StoppedReason.Should().Be("empty");
        cycle.EgressRefusals.Should().Be(1);
        transport.Sends.Should().Be(0);
        chat.Sent.Should().HaveCount(3);
        chat.Sent[1].Should().Contain(canary, "the Host model really receives the labelled retrieval");
        auditor.Records.Where(r => r.TargetKey == "rag:search").Should().ContainSingle()
            .Which.PolicyDecisions.Should().Contain("results=1");
        var decision = records.Where(d => d.Site == "EG-WEB-01").Should().ContainSingle().Which;
        decision.Family.Should().Be(EgressFamilies.WebSearch);
        decision.DestinationClass.Should().Be(EgressDestinationClass.WebSearch);
        decision.DestinationLabel.Should().Be(new SecurityLabel(SecurityLevel.Confidential));
        decision.Current.Should().Be(SecurityLabel.SystemHigh, "the web tool is still inside an open read");
        decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        decision.Mode.Should().Be("enforce");
        decision.ModeBasis.Should().Be("profile:secure-workstation");
        decision.Refused.Should().BeTrue();
        var refusal = memory.Query("", 100).Where(e => e.EventType == "egress.refused")
            .Should().ContainSingle().Which.Message;
        refusal.Should().Contain("SystemHighData").And.Contain("EG-WEB-01").And.Contain(decision.Ref);
        refusal.Should().NotContain(canary).And.NotContain("Secret").And.NotContain("Confidential");
        chat.Sent[2].Should().Contain("REFUSED by egress policy").And.Contain(decision.Ref);
        records.Where(d => d.Site == "EG-MDL-01").Should().HaveCount(3)
            .And.OnlyContain(d => !d.Refused && d.DestinationClass == EgressDestinationClass.Host);
    }

    [Fact]
    public async Task Scenario_C_factory_refuses_a_Secret_export_before_the_primary_handler()
    {
        var name = "leak-" + Guid.NewGuid().ToString("N");
        var records = new ConcurrentQueue<EgressDecision>();
        using var subscription = EgressDecisionLog.Subscribe(new Sink(d =>
        {
            if (d.Site == "factory:" + name) records.Enqueue(d);
        }));
        var services = new ServiceCollection();
        services.AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation);
        var transport = new SearchHandler();
        services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => transport);
        await using var provider = services.BuildServiceProvider();
        var pipeline = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(name);
        var guards = 0;
        for (var handler = pipeline; handler is DelegatingHandler delegating; handler = delegating.InnerHandler!)
            if (handler.GetType().Name == "EgressGuardHandler") guards++;
        guards.Should().Be(1, "the factory must retain its outer guard even when the redirect follower could refuse as a backstop");
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var frame = EgressSubject.Enter("agent:" + name, new HighWaterMark(new SecurityLabel(SecurityLevel.Secret)));
        var error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync("https://remote-" + name + ".example/"); });
        error.Should().BeOfType<EgressRefusedException>();
        transport.Sends.Should().Be(0);
        var decision = records.Should().ContainSingle().Which;
        decision.Mode.Should().Be("enforce");
        decision.ModeBasis.Should().Be("profile:secure-workstation");
        decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        decision.Current.Should().Be(new SecurityLabel(SecurityLevel.Secret));
        decision.Refused.Should().BeTrue();
    }

    [Fact]
    public async Task Scenario_C_real_loopback_redirect_refuses_the_remote_hop_before_connecting()
    {
        var name = "redirect-leak-" + Guid.NewGuid().ToString("N");
        var destination = "http://remote-" + name + ".example/b";
        var requests = 0;
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(System.Net.IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.Run(context =>
        {
            Interlocked.Increment(ref requests);
            context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
            context.Response.Headers.Location = destination;
            return Task.CompletedTask;
        });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var records = new ConcurrentQueue<EgressDecision>();
        using var subscription = EgressDecisionLog.Subscribe(new Sink(d =>
        {
            if (d.Site == "factory:" + name) records.Enqueue(d);
        }));
        var services = new ServiceCollection();
        services.AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation);
        services.AddHttpClient(name);
        await using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient(name);
        using var frame = EgressSubject.Enter("agent:" + name, new HighWaterMark(new SecurityLabel(SecurityLevel.Secret)));
        var error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync(address + "/a"); });
        error.Should().BeOfType<EgressRefusedException>("a refused remote hop must never reach DNS or connect");
        requests.Should().Be(1);
        records.Should().HaveCount(2);
        var hops = records.ToArray();
        hops[0].DestinationClass.Should().Be(EgressDestinationClass.Host);
        hops[0].Refused.Should().BeFalse();
        hops[1].Destination.Should().Be(new Uri(destination).GetLeftPart(UriPartial.Authority));
        hops[1].ModeBasis.Should().Be("profile:secure-workstation");
        hops[1].Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        hops[1].Refused.Should().BeTrue();
        await app.StopAsync();
    }

    [Theory]
    [InlineData("unlabelled")]
    [InlineData("side-observe")]
    [InlineData("throws")]
    public async Task C5_C11_C12_unlabelled_side_observed_and_thrown_reads_refuse_the_next_model_export(string behavior)
    {
        var id = Guid.NewGuid().ToString("N");
        var subject = "agent:read-" + id;
        var host = "gpu-" + id + ".example";
        var records = new ConcurrentQueue<EgressDecision>();
        using var subscription = EgressDecisionLog.Subscribe(new Sink(d =>
        {
            if (d.Destination.Contains(host, StringComparison.Ordinal)) records.Enqueue(d);
        }));
        var chat = new ScriptedChatClient(new Uri("http://" + host + ":11434"), Calls("read", new { }), Done());
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(new EgressGuard("secure-workstation"));
        services.AddAshlarMeaiPipeline(ollamaInnerFactory: _ => chat,
            onnxInnerFactory: _ => new FakeChatClient(), registerDefaultRouter: false);
        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);
        var registry = new CapabilityRegistry();
        registry.Register(new PayloadTool(() =>
        {
            var text = "unlabelled-CANARY-" + id;
            if (behavior == "side-observe") EgressSubject.Observe(SecurityLabel.Public);
            if (behavior == "throws") throw new InvalidOperationException(text);
            return new { text };
        }));
        using var frame = EgressSubject.Enter(subject, new HighWaterMark(SecurityLabel.Public));
        var cycle = await new ToolCallingAgent("read", new MeaiBackedModel(client, NullLogger<MeaiBackedModel>.Instance),
            NullLogger<ToolCallingAgent>.Instance).RunCycleAsync(new WorldSnapshot(0, new Dictionary<string, object?>()),
                registry, new PolicyEngine([]), null, new InMemoryAgentMemory(), CancellationToken.None);
        if (behavior == "throws")
        {
            cycle.StoppedReason.Should().Be("error", "an ordinary tool exception ends the actual agent cycle");
            // The runner still owns this frame. Prove a subsequent send is refused without pretending
            // ToolCallingAgent naturally makes another turn after an ordinary exception.
            var error = await Record.ExceptionAsync(() => client.GetResponseAsync([new ChatMessage(ChatRole.User, "next turn")]));
            error.Should().BeOfType<EgressRefusedException>();
        }
        else cycle.StoppedReason.Should().Be("egress_refused");
        chat.Sent.Should().ContainSingle();
        records.Should().HaveCount(2);
        var decision = records.Last();
        decision.Current.Should().Be(SecurityLabel.SystemHigh);
        decision.CurrentBasis.Should().Be("subject:" + subject);
        decision.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        decision.Site.Should().Be("EG-MDL-01");
        decision.Family.Should().Be(EgressFamilies.ModelMeai);
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
        decision.DestinationLabel.Should().Be(new SecurityLabel(SecurityLevel.Internal));
        decision.Refused.Should().BeTrue();
    }

    [Fact]
    public async Task A_Public_inner_frame_cannot_lower_a_Secret_outer_frame_at_the_send()
    {
        using var outer = EgressSubject.Enter("outer", new HighWaterMark(new SecurityLabel(SecurityLevel.Secret)));
        using var inner = EgressSubject.Enter("inner", new HighWaterMark(SecurityLabel.Public));
        var transport = new SearchHandler();
        using var client = EgressHttp.CreateClient(transport, EgressFamilies.Http, "nested-leak", new EgressGuard("secure-workstation"));
        var error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync("https://remote.example/"); });
        var refusal = error.Should().BeOfType<EgressRefusedException>().Which;
        refusal.Decision.Current.Should().Be(new SecurityLabel(SecurityLevel.Secret));
        refusal.Decision.CurrentBasis.Should().Be("subject:inner");
        refusal.Decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
        transport.Sends.Should().Be(0);
    }

    [Fact]
    public async Task C4_a_tools_inner_frame_cannot_lower_its_open_read_at_the_send()
    {
        var transport = new SearchHandler();
        using var outbound = EgressHttp.CreateClient(transport, EgressFamilies.Http, "inner-read", new EgressGuard("secure-workstation"));
        EgressDecision? captured = null;
        using var subscription = EgressDecisionLog.Subscribe(new Sink(d => { if (d.Site == "inner-read") captured = d; }));
        var registry = new CapabilityRegistry();
        registry.Register(new InnerSendTool(outbound));
        var chat = new ScriptedChatClient(new Uri("http://127.0.0.1:11434"), Calls("inner-send", new { }), Done());
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(new EgressGuard("secure-workstation"));
        services.AddAshlarMeaiPipeline(ollamaInnerFactory: _ => chat,
            onnxInnerFactory: _ => new FakeChatClient(), registerDefaultRouter: false);
        await using var provider = services.BuildServiceProvider();
        var model = new MeaiBackedModel(provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama), NullLogger<MeaiBackedModel>.Instance);
        using var frame = EgressSubject.Enter("outer-read", new HighWaterMark(new SecurityLabel(SecurityLevel.Secret)));
        var cycle = await new ToolCallingAgent("inner-read", model, NullLogger<ToolCallingAgent>.Instance)
            .RunCycleAsync(new WorldSnapshot(0, new Dictionary<string, object?>()), registry, new PolicyEngine([]), null, null, CancellationToken.None);
        cycle.StoppedReason.Should().Be("empty");
        cycle.EgressRefusals.Should().Be(1);
        transport.Sends.Should().Be(0);
        captured.Should().NotBeNull();
        captured!.Current.Should().Be(SecurityLabel.SystemHigh);
        captured.CurrentBasis.Should().Be("subject:inner-read");
        captured.Access.Reason.Should().Be(AccessDenialReason.SystemHighData);
        captured.Refused.Should().BeTrue();
    }

    private sealed class InnerSendTool(HttpClient client) : ITool
    {
        public string Id => "inner-send";
        public ToolSchema Schema => new(Id, Id, "{\"type\":\"object\"}");
        public async Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot snapshot, CancellationToken ct)
        {
            using var inner = EgressSubject.Enter("inner-read", new HighWaterMark(SecurityLabel.Public));
            using var response = await client.GetAsync("https://remote.example/", ct);
            return new ToolResult(new ActionDelta(snapshot.Tick, snapshot.Tick + 1, [Id]), new { ok = true });
        }
    }

    [Theory]
    [InlineData("air-gapped")]
    [InlineData("classification")]
    [InlineData("host-throws")]
    [InlineData("host-null")]
    public async Task C7_C8_C9_override_classification_and_broken_host_guards_fail_closed(string control)
    {
        using var mode = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", control == "air-gapped" ? "report" : null);
        var services = new ServiceCollection();
        services.AddAshlarProfile(control == "air-gapped" ? AshlarDeploymentProfile.AirGapped : AshlarDeploymentProfile.SecureWorkstation);
        if (control.StartsWith("host-", StringComparison.Ordinal))
            services.AddSingleton<IEgressGuard>(new BrokenGuard(control == "host-null"));
        var transport = new SearchHandler();
        services.AddHttpClient("leak-control").ConfigurePrimaryHttpMessageHandler(() => transport);
        await using var provider = services.BuildServiceProvider();
        using var invoker = new HttpMessageInvoker(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler("leak-control"), false);
        using var request = new HttpRequestMessage(HttpMethod.Get,
            control == "classification" ? new Uri("/relative", UriKind.Relative) : new Uri("https://remote.example/"));
        using var frame = EgressSubject.Enter("agent:control", new HighWaterMark(new SecurityLabel(SecurityLevel.Secret)));
        var error = await Record.ExceptionAsync(async () => { using var response = await invoker.SendAsync(request, CancellationToken.None); });
        var decision = error.Should().BeOfType<EgressRefusedException>().Which.Decision;
        decision.Mode.Should().Be("enforce");
        decision.Refused.Should().BeTrue();
        transport.Sends.Should().Be(0);
        if (control == "air-gapped") decision.ModeBasis.Should().Be("override-ignored");
        else
        {
            decision.Fault.Should().NotBeNullOrEmpty();
            decision.Access.Reason.Should().Be(AccessDenialReason.NoDecision);
        }
    }

    [Fact]
    public async Task C10_dotnet_test_in_a_Secret_frame_is_refused_before_start()
    {
        var directory = Directory.CreateTempSubdirectory("ashlar-leak-process-");
        try
        {
            var services = new ServiceCollection();
            services.AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation);
            using var frame = EgressSubject.Enter("agent:process-leak", new HighWaterMark(new SecurityLabel(SecurityLevel.Secret)));
            var error = await Record.ExceptionAsync(() => Ashlar.Tools.Dev.DotnetTestTool.RunTrxTestsNoBuildAsync(directory.FullName));
            var decision = error.Should().BeOfType<EgressRefusedException>().Which.Decision;
            decision.Access.Reason.Should().Be(AccessDenialReason.LevelTooLow);
            decision.Refused.Should().BeTrue();
            decision.ModeBasis.Should().Be("profile:secure-workstation");
            directory.EnumerateFileSystemInfos().Should().BeEmpty();
        }
        finally { directory.Delete(recursive: true); }
    }

    private sealed class BrokenGuard(bool returnsNull) : IEgressGuard
    {
        public EgressDecision Evaluate(EgressRequest request) => returnsNull ? null! : throw new InvalidOperationException("host failure");
    }

    private sealed class PayloadTool(Func<object> payload) : ITool
    {
        public string Id => "read";
        public ToolSchema Schema => new(Id, Id, "{\"type\":\"object\"}");
        public async Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot snapshot, CancellationToken ct)
        {
            await Task.Yield();
            return new ToolResult(new ActionDelta(snapshot.Tick, snapshot.Tick + 1, [Id]), payload());
        }
    }

    private sealed class SearchHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("{\"webPages\":{\"value\":[]}}"),
            });
        }
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
