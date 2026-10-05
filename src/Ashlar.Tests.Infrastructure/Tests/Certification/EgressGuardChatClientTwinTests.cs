using System.Collections.Concurrent;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Routing;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>
/// SPEC-007, the behavioural twin of the MEAI governance route (EG-MDL-01, EG-MDL-02): every governed chat target
/// records one report-only egress decision per call, from the outermost layer, before PolicyGate decides, and nothing
/// the guard or the destination lookup does can change the call.
/// </summary>
/// <remarks>
/// <para>Uses <c>AddAshlarMeaiPipeline</c> and <c>AddAshlarGovernedChatClient</c> directly, never <c>AddAshlar</c>: the
/// kernel registers the governance defaults (an empty cloud allow-list) before it adds the pipeline
/// (<c>AshlarKernelRegistrar.Phases.cs</c>), so <c>cloud:bedrock:*</c> is always denied there and a twin built on it
/// would never see an allowed cloud call. The deny case reproduces that ordering on purpose.</para>
/// <para>Isolation: each provider gets a recording <see cref="IEgressGuard"/> that delegates to
/// <see cref="EgressGuard.ProcessDefault"/>, so classification is the real one and only this provider's decisions are
/// counted. Where the twin must prove the fallback is <see cref="EgressGuard.ProcessDefault"/> itself, it reads the
/// process-wide <see cref="EgressDecisionLog"/>, filtered by a unique <c>egress-twin-&lt;guid&gt;</c> host. The class
/// clears <c>ASHLAR_OLLAMA_BASE_URL</c>, which outranks the configured URL, so it is serialized with the other
/// environment writers.</para>
/// </remarks>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressGuardChatClientTwinTests
{
    [Fact]
    public void CloudBedrock_DeniedByPolicyGate_StillRecordsOneExternalModelDecision_AndThrowsAtTheCall()
    {
        var region = "eg-" + Guid.NewGuid().ToString("N")[..12];
        var guard = new RecordingGuard();
        var bedrock = new SpyChatClient("bedrock");
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.RegisterGovernanceDefaults(); // the kernel's order: an empty allow-list wins the TryAdd
        services.AddAshlarMeaiPipeline(
            configure: o =>
            {
                o.Bedrock.Enabled = true;
                o.Bedrock.Region = region;
            },
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient(),
            bedrockInnerFactory: (_, _) => bedrock);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockFast);

        Action call = () => _ = client.GetResponseAsync("hello");
        call.Should().Throw<PolicyViolationException>("PolicyGate throws at the call, and the guard layer keeps it there")
            .Which.Code.Should().Be("target_denied");
        Action stream = () => _ = client.GetStreamingResponseAsync("hello");
        stream.Should().Throw<PolicyViolationException>("PolicyGate still throws at the call, not at enumeration");

        bedrock.CallCount.Should().Be(0);
        var internalLabel = new SecurityLabel(SecurityLevel.Internal);
        guard.Decisions.Should().HaveCount(2, "one decision per call, on both paths, though PolicyGate denied both");
        guard.Decisions.Should().OnlyContain(d =>
            d.Site == "EG-MDL-02"
            && d.Family == EgressFamilies.ModelMeai
            && d.Destination == $"https://bedrock-runtime.{region}.amazonaws.com"
            && d.DestinationClass == EgressDestinationClass.ExternalModel
            && d.DestinationLabel == internalLabel
            && !d.Access.Allowed
            && d.Fault == null);
    }

    [Fact]
    public async Task CloudBedrock_Allowed_RecordsOneDecision_AndTheCallIsUnchanged()
    {
        var guard = new RecordingGuard();
        var bedrock = new SpyChatClient("bedrock");
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarMeaiPipeline(
            configure: o => o.Bedrock.Enabled = true,
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient(),
            bedrockInnerFactory: (_, _) => bedrock);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockHeavy);

        (await client.GetResponseAsync("hello")).Text.Should().Be("bedrock");
        bedrock.CallCount.Should().Be(1);
        var decision = guard.Decisions.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MDL-02");
        decision.Family.Should().Be(EgressFamilies.ModelMeai);
        decision.Destination.Should().Be("aws-bedrock", "no region is configured, so the SDK chooses it and it is not probed");
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
    }

    [Theory]
    [InlineData("http://{0}.localhost:11434", EgressDestinationClass.Host, true)]
    [InlineData("http://{0}.example:11434", EgressDestinationClass.ExternalModel, false)]
    public async Task LocalOllama_IsClassifiedByTheResolvedUrl(string urlFormat, EgressDestinationClass expected, bool allowed)
    {
        using var unset = EnvironmentVariableScope.Unset(MeaiPipelineOptions.OllamaBaseUrlEnvVar);
        var host = "egress-twin-" + Guid.NewGuid().ToString("N");
        var url = string.Format(System.Globalization.CultureInfo.InvariantCulture, urlFormat, host);
        var guard = new RecordingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarMeaiPipeline(
            configure: o => o.OllamaBaseUrl = url,
            ollamaInnerFactory: _ => new FakeChatClient("ollama"),
            onnxInnerFactory: _ => new FakeChatClient());

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);
        (await client.GetResponseAsync("hello")).Text.Should().Be("ollama");

        var decision = guard.Decisions.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MDL-01");
        decision.Family.Should().Be(EgressFamilies.ModelMeai);
        decision.Destination.Should().Be(url);
        decision.DestinationClass.Should().Be(expected);
        decision.Access.Allowed.Should().Be(allowed);
    }

    [Fact]
    public async Task LocalOnnx_RecordsNoDecision()
    {
        var guard = new RecordingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarMeaiPipeline(
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient("onnx"));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOnnx);
        (await client.GetResponseAsync("hello")).Text.Should().Be("onnx");
        await foreach (var _ in client.GetStreamingResponseAsync("hello"))
        {
        }

        client.Should().BeOfType<EgressGuardChatClient>().Which.Request.Should().BeNull();
        guard.Decisions.Should().BeEmpty("local:onnx runs in process");
    }

    [Fact]
    public async Task Streaming_RecordsOneDecisionAtTheCall_ToTheProcessWideLog()
    {
        using var unset = EnvironmentVariableScope.Unset(MeaiPipelineOptions.OllamaBaseUrlEnvVar);
        var host = "egress-twin-" + Guid.NewGuid().ToString("N") + ".example";
        var log = new HostRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(log);
        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(
            configure: o => o.OllamaBaseUrl = $"http://{host}:11434",
            ollamaInnerFactory: _ => new FakeChatClient("streamed"),
            onnxInnerFactory: _ => new FakeChatClient());

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        var updates = client.GetStreamingResponseAsync("hello");
        log.Decisions.Should().ContainSingle("the decision is made when the call is made, before enumeration");
        var text = string.Empty;
        await foreach (var update in updates)
            text += update.Text;

        text.Should().Be("streamed");
        log.Decisions.Should().ContainSingle().Which.Site.Should().Be("EG-MDL-01");
    }

    [Fact]
    public async Task AGovernedTargetWithoutThePipelineOptions_StillResolvesTheOllamaEndpoint()
    {
        using var unsetAshlar = EnvironmentVariableScope.Unset(MeaiPipelineOptions.OllamaBaseUrlEnvVar);
        using var unsetLegacy = EnvironmentVariableScope.Unset(MeaiPipelineOptions.LegacyOllamaBaseUrlEnvVar);
        var guard = new RecordingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarGovernedChatClient(MeaiTargetKeys.LocalOllama, _ => new FakeChatClient());
        services.AddAshlarGovernedChatClient("peer:node-7", _ => new FakeChatClient());

        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama).GetResponseAsync("hello");
        await provider.GetRequiredKeyedService<IChatClient>("peer:node-7").GetResponseAsync("hello");

        guard.Decisions.Should().HaveCount(2);
        guard.Decisions[0].Site.Should().Be("EG-MDL-01", "IOptions<MeaiPipelineOptions> is not registered, and the resolver takes null");
        guard.Decisions[0].Destination.Should().Be(MeaiPipelineOptions.DefaultOllamaBaseUrl);
        guard.Decisions[0].DestinationClass.Should().Be(EgressDestinationClass.Host);
        guard.Decisions[1].Site.Should().Be("meai:peer:node-7");
        guard.Decisions[1].Destination.Should().Be("meai:peer:node-7");
        guard.Decisions[1].DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
    }

    [Fact]
    public async Task ResolutionFaults_DoNotStopTheClientBeingBuilt_AndTheGuardFallsBackToProcessDefault()
    {
        using var unset = EnvironmentVariableScope.Unset(MeaiPipelineOptions.OllamaBaseUrlEnvVar);

        // A guard registration that throws: the client is still built, and its decisions go to ProcessDefault.
        var host = "egress-twin-" + Guid.NewGuid().ToString("N") + ".example";
        var log = new HostRecorder(host);
        using var subscription = EgressDecisionLog.Subscribe(log);
        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(
            configure: o => o.OllamaBaseUrl = $"http://{host}:11434",
            ollamaInnerFactory: _ => new FakeChatClient("ollama"),
            onnxInnerFactory: _ => new FakeChatClient());
        services.AddSingleton<IEgressGuard>(_ => throw new InvalidOperationException("broken guard registration"));

        using (var provider = services.BuildServiceProvider())
        {
            var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);
            client.Should().BeOfType<EgressGuardChatClient>();
            (await client.GetResponseAsync("hello")).Text.Should().Be("ollama");
        }

        var decision = log.Decisions.Should().ContainSingle("the null guard is EgressGuard.ProcessDefault, which publishes").Which;
        decision.Site.Should().Be("EG-MDL-01");
        decision.Destination.Should().Be($"http://{host}:11434");

        // Options that throw: the client is still built, and the target is recorded by name (an external model).
        var faulted = new ServiceCollection();
        faulted.AddSingleton<IOptions<MeaiPipelineOptions>>(_ => throw new InvalidOperationException("broken options"));
        faulted.AddAshlarGovernedChatClient(MeaiTargetKeys.LocalOllama, _ => new FakeChatClient("named"));

        using var faultedProvider = faulted.BuildServiceProvider();
        var named = faultedProvider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama)
            .Should().BeOfType<EgressGuardChatClient>().Which;
        named.Request.Should().NotBeNull();
        named.Request!.Site.Should().Be("meai:" + MeaiTargetKeys.LocalOllama);
        named.Request.DestinationName.Should().Be("meai:" + MeaiTargetKeys.LocalOllama);
        (await named.GetResponseAsync("hello")).Text.Should().Be("named");
    }

    [Fact]
    public void EveryKeyedTarget_IsOutermostEgressGuard_WithItsSite()
    {
        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(
            configure: o => o.Bedrock.Enabled = true,
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient(),
            bedrockInnerFactory: (_, _) => new FakeChatClient());
        var keys = services.Where(d => d.ServiceType == typeof(IChatClient) && d.IsKeyedService)
            .Select(d => (string)d.ServiceKey!).ToList();

        using var provider = services.BuildServiceProvider();
        keys.Should().BeEquivalentTo(new[]
        {
            MeaiTargetKeys.LocalOllama, MeaiTargetKeys.LocalOnnx, DefaultRouteCandidateTable.CloudBedrockFast,
            DefaultRouteCandidateTable.CloudBedrockBalanced, DefaultRouteCandidateTable.CloudBedrockHeavy,
        });
        foreach (var key in keys)
        {
            var outer = provider.GetRequiredKeyedService<IChatClient>(key).Should().BeOfType<EgressGuardChatClient>().Which;
            var expected = key == MeaiTargetKeys.LocalOnnx ? null : key == MeaiTargetKeys.LocalOllama ? "EG-MDL-01" : "EG-MDL-02";
            outer.Request?.Site.Should().Be(expected);
            (outer.Request is null).Should().Be(expected is null);
        }
    }

    [Fact]
    public async Task AThrowingCustomGuard_NeverReachesTheCaller_OnGetResponseAsync()
    {
        using var provider = OllamaWithGuard(new ThrowingGuard());
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        (await client.GetResponseAsync("hello")).Text.Should().Be("ollama");
    }

    [Fact]
    public async Task AThrowingCustomGuard_NeverReachesTheCaller_OnStreaming()
    {
        using var provider = OllamaWithGuard(new ThrowingGuard());
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        var text = string.Empty;
        await foreach (var update in client.GetStreamingResponseAsync("hello"))
            text += update.Text;
        text.Should().Be("ollama");
    }

    [Theory]
    [InlineData("x.localhost#")]
    [InlineData("x.localhost/")]
    [InlineData("x@127.0.0.1/")]
    public async Task ARegionThatIsNotARegionName_IsRecordedByName(string region)
    {
        var guard = new RecordingGuard();
        using var provider = BedrockWithRegion(guard, region);
        var client = provider.GetRequiredKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockFast);

        (await client.GetResponseAsync("hello")).Text.Should().Be("bedrock");
        var decision = guard.Decisions.Should().ContainSingle().Which;
        decision.Destination.Should().Be("aws-bedrock", "region '{0}' could move the host of a composed URI", region);
        decision.DestinationClass.Should().Be(
            EgressDestinationClass.ExternalModel, "region '{0}' must not make Bedrock look like the host", region);
    }

    [Fact]
    public async Task ARegionName_IsTrimmedIntoTheRuntimeEndpoint()
    {
        var guard = new RecordingGuard();
        using var provider = BedrockWithRegion(guard, " us-west-2 ");
        var client = provider.GetRequiredKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockFast);

        (await client.GetResponseAsync("hello")).Text.Should().Be("bedrock");
        var decision = guard.Decisions.Should().ContainSingle().Which;
        decision.Destination.Should().Be("https://bedrock-runtime.us-west-2.amazonaws.com");
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
    }

    private static ServiceProvider OllamaWithGuard(IEgressGuard guard)
    {
        var services = new ServiceCollection();
        services.AddSingleton(guard);
        services.AddAshlarMeaiPipeline(
            ollamaInnerFactory: _ => new FakeChatClient("ollama"),
            onnxInnerFactory: _ => new FakeChatClient());
        return services.BuildServiceProvider();
    }

    private static ServiceProvider BedrockWithRegion(IEgressGuard guard, string region)
    {
        var services = new ServiceCollection();
        services.AddSingleton(guard);
        services.AddAshlarMeaiPipeline(
            configure: o =>
            {
                o.Bedrock.Enabled = true;
                o.Bedrock.Region = region;
            },
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient(),
            bedrockInnerFactory: (_, _) => new FakeChatClient("bedrock"));
        return services.BuildServiceProvider();
    }

    private sealed class RecordingGuard : IEgressGuard
    {
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public EgressDecision Evaluate(EgressRequest request)
        {
            var decision = EgressGuard.ProcessDefault.Evaluate(request);
            _decisions.Enqueue(decision);
            return decision;
        }
    }

    private sealed class ThrowingGuard : IEgressGuard
    {
        public EgressDecision Evaluate(EgressRequest request) =>
            throw new InvalidOperationException("custom guard fault");
    }

    private sealed class HostRecorder : IEgressDecisionSink
    {
        private readonly string _host;
        private readonly ConcurrentQueue<EgressDecision> _decisions = new();

        public HostRecorder(string host) => _host = host;

        public IReadOnlyList<EgressDecision> Decisions => _decisions.ToArray();

        public void Record(EgressDecision decision)
        {
            if (decision.Destination.Contains(_host, StringComparison.Ordinal))
                _decisions.Enqueue(decision);
        }
    }
}
