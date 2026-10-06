using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Routing;
using Ashlar.Infrastructure.Execution.Ollama;
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
/// <para><b>Where a target is recorded (SPEC-007 PR 4.1, gap 3).</b> The destination comes from the inner client, not
/// from the key: an absolute <see cref="ChatClientMetadata.ProviderUri"/> is the destination; <c>local:onnx</c>
/// records nothing only when the inner client is the in-process <see cref="LlamaSharpChatClient"/>; a Bedrock target
/// with no URI keeps the region reconstruction; anything else fails closed to <c>meai:&lt;key&gt;</c>, an external
/// model. The default <see cref="OllamaHttpChatClient"/> reports its base address, which
/// <see cref="OllamaEndpointResolver"/> resolved, so the twins that pin the resolver's precedence build that client
/// over a stub handler (<see cref="StubOllama"/>) with exactly the base address the default factory gives it, and one
/// twin compares the recorded authority with the real default client's.</para>
/// <para><b>Ollama cloud models (PR 4.1, D34).</b> A call whose model id ends in <c>-cloud</c> or <c>:cloud</c> (any
/// case), from the call's options or, without one, the inner client's default model, is recorded as an external
/// model at <c>https://ollama.com</c>: the local daemon relays it there.</para>
/// <para>Uses <c>AddAshlarMeaiPipeline</c> and <c>AddAshlarGovernedChatClient</c> directly, never <c>AddAshlar</c>: the
/// kernel registers the governance defaults (an empty cloud allow-list) before it adds the pipeline
/// (<c>AshlarKernelRegistrar.Phases.cs</c>), so <c>cloud:bedrock:*</c> is always denied there and a twin built on it
/// would never see an allowed cloud call. The deny case reproduces that ordering on purpose.</para>
/// <para>Isolation: each provider gets a recording <see cref="IEgressGuard"/> that delegates to
/// <see cref="EgressGuard.ProcessDefault"/>, so classification is the real one and only this provider's decisions are
/// counted. Where the twin must prove the fallback is <see cref="EgressGuard.ProcessDefault"/> itself, it reads the
/// process-wide <see cref="EgressDecisionLog"/>, filtered by a unique <c>egress-twin-&lt;guid&gt;</c> host.</para>
/// <para>The class sets and clears <c>ASHLAR_OLLAMA_BASE_URL</c>, <c>OLLAMA_BASE_URL</c>, <c>ASHLAR_OLLAMA_MODEL</c>
/// and <c>OLLAMA_MODEL</c>, each through an <see cref="EnvironmentVariableScope"/> that restores it, so it runs in the
/// serial <c>EnvironmentVariables</c> collection (<c>DisableParallelization</c>), as
/// <see cref="ProcessGlobalEnvironmentConventionTests"/> requires of an environment writer. The redirect twin listens
/// on a loopback port of its own; nothing else reaches a socket.</para>
/// </remarks>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressGuardChatClientTwinTests
{
    private const string OllamaCom = "https://ollama.com";

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
            ollamaInnerFactory: sp => StubOllama(sp, "ollama"),
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

    /// <summary>
    /// The recorded destination follows <see cref="OllamaEndpointResolver"/>'s precedence, environment included, and is
    /// the authority the default <see cref="OllamaHttpChatClient"/> dials: <c>ASHLAR_OLLAMA_BASE_URL</c> outranks the
    /// configured URL, and the legacy <c>OLLAMA_BASE_URL</c> (what the compose stacks set) outranks the default.
    /// </summary>
    [Theory]
    [InlineData(MeaiPipelineOptions.OllamaBaseUrlEnvVar, true)]
    [InlineData(MeaiPipelineOptions.LegacyOllamaBaseUrlEnvVar, false)]
    public async Task LocalOllama_RecordsTheEnvironmentUrl_ThatTheDefaultClientDials(string variable, bool configureUrl)
    {
        var id = Guid.NewGuid().ToString("N");
        var configured = configureUrl ? $"http://egress-twin-{id}.localhost:11434" : null;
        var fromEnvironment = $"http://egress-twin-{id}.example:11434";
        using var unsetAshlar = EnvironmentVariableScope.Unset(MeaiPipelineOptions.OllamaBaseUrlEnvVar);
        using var unsetLegacy = EnvironmentVariableScope.Unset(MeaiPipelineOptions.LegacyOllamaBaseUrlEnvVar);
        using var set = new EnvironmentVariableScope(variable, fromEnvironment);

        var guard = new RecordingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarMeaiPipeline(
            configure: o => o.OllamaBaseUrl = configured,
            ollamaInnerFactory: sp => StubOllama(sp, "ollama"),
            onnxInnerFactory: _ => new FakeChatClient());
        using (var provider = services.BuildServiceProvider())
        {
            var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);
            (await client.GetResponseAsync("hello")).Text.Should().Be("ollama");
        }

        var decision = guard.Decisions.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MDL-01");
        decision.Destination.Should().Be(fromEnvironment, "{0} outranks {1}", variable, configured ?? "the default");
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
        decision.Access.Allowed.Should().BeFalse();

        // The default inner client, built and never called: its base address is what the guard layer reports.
        var real = new ServiceCollection();
        real.AddAshlarMeaiPipeline(
            configure: o => o.OllamaBaseUrl = configured,
            onnxInnerFactory: _ => new FakeChatClient());
        using var realProvider = real.BuildServiceProvider();
        var dialling = realProvider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);
        var recorded = dialling.Should().BeOfType<EgressGuardChatClient>().Which.Request?.Destination;
        var dialled = dialling.GetService<ChatClientMetadata>()?.ProviderUri;
        recorded.Should().NotBeNull();
        dialled.Should().NotBeNull();
        dialled!.GetLeftPart(UriPartial.Authority).Should().Be(fromEnvironment);
        recorded!.GetLeftPart(UriPartial.Authority).Should().Be(dialled.GetLeftPart(UriPartial.Authority));
    }

    /// <summary>
    /// PR 4.1, gap 3: a custom inner client under <c>local:ollama</c> is recorded where it says it dials
    /// (<see cref="ChatClientMetadata.ProviderUri"/>), not as the resolved Ollama URL. Before, the key decided: the
    /// record read <c>http://localhost:11434</c>, Host, while the client sent to a remote host.
    /// </summary>
    [Fact]
    public async Task LocalOllama_WithACustomInnerClient_RecordsWhereThatClientDials()
    {
        using var unsetAshlar = EnvironmentVariableScope.Unset(MeaiPipelineOptions.OllamaBaseUrlEnvVar);
        using var unsetLegacy = EnvironmentVariableScope.Unset(MeaiPipelineOptions.LegacyOllamaBaseUrlEnvVar);
        var remote = $"http://gpu-{Guid.NewGuid():N}.example:11434";
        var guard = new RecordingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarMeaiPipeline(
            ollamaInnerFactory: _ => new MetadataChatClient("custom", new Uri(remote + "/v1/")),
            onnxInnerFactory: _ => new FakeChatClient());

        using var provider = services.BuildServiceProvider();
        (await provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama).GetResponseAsync("hello"))
            .Text.Should().Be("custom");

        var decision = guard.Decisions.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MDL-01");
        decision.Destination.Should().Be(remote, "the inner client names where it dials");
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel, "a remote Ollama is not the host");
        decision.Access.Allowed.Should().BeFalse();
    }

    /// <summary>
    /// PR 4.1, gap 3: an inner client under <c>local:ollama</c> that names no URI fails closed to
    /// <c>meai:local:ollama</c>, an external model. Before, it was recorded as the resolved URL (by default
    /// <c>http://localhost:11434</c>, Host) whatever it dialled. This also holds with no pipeline options registered.
    /// </summary>
    [Fact]
    public async Task LocalOllama_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey()
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
        guard.Decisions[0].Site.Should().Be("meai:local:ollama", "the inner client names no URI, so the key is all there is");
        guard.Decisions[0].Destination.Should().Be("meai:local:ollama");
        guard.Decisions[0].DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
        guard.Decisions[0].DestinationLabel.Should().NotBe(SecurityLabel.SystemHigh);
        guard.Decisions[1].Site.Should().Be("meai:peer:node-7");
        guard.Decisions[1].Destination.Should().Be("meai:peer:node-7");
        guard.Decisions[1].DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
    }

    /// <summary>
    /// PR 4.1, gap 3: <c>local:onnx</c> records nothing only for the in-process <see cref="LlamaSharpChatClient"/>, the
    /// default and when a host registers it itself. The check is a type check, not the provider name.
    /// </summary>
    [Fact]
    public void LocalOnnx_WithTheLlamaSharpClient_RecordsNoDecision()
    {
        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(ollamaInnerFactory: _ => new FakeChatClient());
        services.AddAshlarGovernedChatClient(
            "local:onnx-own", sp => new LlamaSharpChatClient(sp.GetRequiredService<IOptions<MeaiPipelineOptions>>()));
        var own = new ServiceCollection();
        own.AddAshlarGovernedChatClient(
            MeaiTargetKeys.LocalOnnx, _ => new LlamaSharpChatClient(Options.Create(new MeaiPipelineOptions())));

        using var provider = services.BuildServiceProvider();
        using var ownProvider = own.BuildServiceProvider();

        provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOnnx)
            .Should().BeOfType<EgressGuardChatClient>().Which.Request.Should().BeNull("the default local:onnx client runs in process");
        ownProvider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOnnx)
            .Should().BeOfType<EgressGuardChatClient>().Which.Request.Should().BeNull("a host's own LLamaSharp client runs in process too");
        var other = provider.GetRequiredKeyedService<IChatClient>("local:onnx-own").Should().BeOfType<EgressGuardChatClient>().Which;
        other.Request.Should().NotBeNull("only the local:onnx key may skip the record, whatever the inner client");
        other.Request!.DestinationName.Should().Be("meai:local:onnx-own");
    }

    /// <summary>
    /// PR 4.1, gap 3: a custom inner client under <c>local:onnx</c> that names a URI is recorded there. Before, the key
    /// alone decided, and <c>local:onnx</c> recorded nothing at all.
    /// </summary>
    [Fact]
    public async Task LocalOnnx_WithACustomInnerClient_RecordsItsProviderUri()
    {
        var remote = $"https://api-{Guid.NewGuid():N}.example";
        var guard = new RecordingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarMeaiPipeline(
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new MetadataChatClient("onnx", new Uri(remote + "/v1/chat")));

        using var provider = services.BuildServiceProvider();
        (await provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOnnx).GetResponseAsync("hello"))
            .Text.Should().Be("onnx");

        var decision = guard.Decisions.Should().ContainSingle("a custom local:onnx client is not in process").Which;
        decision.Site.Should().Be("meai:local:onnx");
        decision.Destination.Should().Be(remote);
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
    }

    /// <summary>
    /// PR 4.1, gap 3: a custom inner client under <c>local:onnx</c> that names no URI fails closed to
    /// <c>meai:local:onnx</c>, an external model, on both paths. Before, nothing was recorded.
    /// </summary>
    [Fact]
    public async Task LocalOnnx_WithACustomInnerClientThatNamesNoUri_FailsClosedToItsKey()
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

        client.Should().BeOfType<EgressGuardChatClient>().Which.Request.Should().NotBeNull();
        guard.Decisions.Should().HaveCount(2, "one decision per call, on both paths");
        guard.Decisions.Should().OnlyContain(d =>
            d.Site == "meai:local:onnx"
            && d.Destination == "meai:local:onnx"
            && d.DestinationClass == EgressDestinationClass.ExternalModel);
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
            ollamaInnerFactory: sp => StubOllama(sp, "streamed"),
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
            ollamaInnerFactory: sp => StubOllama(sp, "ollama"),
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

        // An inner client whose GetService throws: the client is still built, and the target is recorded by name.
        var faulted = new ServiceCollection();
        faulted.AddAshlarGovernedChatClient(MeaiTargetKeys.LocalOllama, _ => new ThrowingMetadataChatClient("named"));

        using var faultedProvider = faulted.BuildServiceProvider();
        var named = faultedProvider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama)
            .Should().BeOfType<EgressGuardChatClient>().Which;
        named.Request.Should().NotBeNull();
        named.Request!.Site.Should().Be("meai:" + MeaiTargetKeys.LocalOllama);
        named.Request.DestinationName.Should().Be("meai:" + MeaiTargetKeys.LocalOllama);
        (await named.GetResponseAsync("hello")).Text.Should().Be("named");

        // Options that throw while a Bedrock region is read: the client is still built, and recorded by name.
        var broken = new ServiceCollection();
        broken.AddSingleton<IOptions<MeaiPipelineOptions>>(_ => throw new InvalidOperationException("broken options"));
        broken.AddAshlarGovernedChatClient(DefaultRouteCandidateTable.CloudBedrockFast, _ => new FakeChatClient("named"));

        using var brokenProvider = broken.BuildServiceProvider();
        var bedrock = brokenProvider.GetRequiredKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockFast)
            .Should().BeOfType<EgressGuardChatClient>().Which;
        bedrock.Request!.DestinationName.Should().Be("meai:" + DefaultRouteCandidateTable.CloudBedrockFast);
    }

    /// <summary>
    /// A key's slashes are escaped in its <c>meai:&lt;key&gt;</c> name, so a key cannot pass the target off as the host:
    /// <c>meai://127.0.0.1</c> would be read as a URL whose host is the loopback address (Host, SystemHigh). PolicyGate
    /// knows no trust tier for such a key and denies the call, after the guard layer has recorded it.
    /// </summary>
    [Theory]
    [InlineData("//127.0.0.1", "meai:%2F%2F127.0.0.1")]
    [InlineData(@"\\127.0.0.1", "meai:%5C%5C127.0.0.1")]
    [InlineData("//localhost:11434/api", "meai:%2F%2Flocalhost:11434%2Fapi")]
    public void AKeyWithSlashes_IsRecordedByItsEscapedName_NeverAsTheHost(string key, string expected)
    {
        var guard = new RecordingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarGovernedChatClient(key, _ => new FakeChatClient("named"));

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(key);
        Action call = () => _ = client.GetResponseAsync("hello");
        call.Should().Throw<PolicyViolationException>("PolicyGate knows no trust tier for '{0}'", key)
            .Which.Code.Should().Be("target_denied");

        var decision = guard.Decisions.Should().ContainSingle("the guard layer records the attempt before PolicyGate").Which;
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel, "key '{0}' must not name the host", key);
        decision.DestinationLabel.Should().NotBe(SecurityLabel.SystemHigh, "key '{0}' must not name the host", key);
        decision.DestinationLabel.Should().Be(new SecurityLabel(SecurityLevel.Internal));
        decision.Site.Should().Be(expected);
        decision.Destination.Should().Be(expected);
        if (Uri.TryCreate(expected, UriKind.Absolute, out var parsed))
            parsed.Authority.Should().BeEmpty("the name '{0}' must not parse with a URI authority", expected);
    }

    [Fact]
    public void EveryKeyedTarget_IsOutermostEgressGuard_WithItsSite()
    {
        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(
            configure: o => o.Bedrock.Enabled = true,
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
            // The default Ollama and LLamaSharp inner clients are built here and never called.
            var outer = provider.GetRequiredKeyedService<IChatClient>(key).Should().BeOfType<EgressGuardChatClient>().Which;
            var expected = key == MeaiTargetKeys.LocalOnnx ? null : key == MeaiTargetKeys.LocalOllama ? "EG-MDL-01" : "EG-MDL-02";
            outer.Request?.Site.Should().Be(expected);
            (outer.Request is null).Should().Be(expected is null);
        }
    }

    [Fact]
    public async Task AThrowingCustomGuard_NeverReachesTheCaller_OnGetResponseAsync()
    {
        var guard = new ThrowingGuard();
        using var provider = OllamaWithGuard(guard);
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        (await client.GetResponseAsync("hello")).Text.Should().Be("ollama");
        guard.Calls.Should().Be(1, "the registered guard is the one evaluated, and its fault is swallowed");
    }

    [Fact]
    public async Task AThrowingCustomGuard_NeverReachesTheCaller_OnStreaming()
    {
        var guard = new ThrowingGuard();
        using var provider = OllamaWithGuard(guard);
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        var text = string.Empty;
        await foreach (var update in client.GetStreamingResponseAsync("hello"))
            text += update.Text;
        text.Should().Be("ollama");
        guard.Calls.Should().Be(1, "the registered guard is the one evaluated, and its fault is swallowed");
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

    /// <summary>
    /// PR 4.1, gap 3, rule 1 on a Bedrock key: an inner client that reports an absolute
    /// <see cref="ChatClientMetadata.ProviderUri"/> is recorded at that URI, ahead of the region reconstruction, and the
    /// site stays Bedrock's <c>EG-MDL-02</c> rather than the key's name.
    /// </summary>
    [Fact]
    public async Task CloudBedrock_WithAnInnerClientThatNamesItsUri_IsRecordedThere_UnderItsSite()
    {
        var guard = new RecordingGuard();
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(guard);
        services.AddAshlarMeaiPipeline(
            configure: o =>
            {
                o.Bedrock.Enabled = true;
                o.Bedrock.Region = "us-west-2";
            },
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient(),
            bedrockInnerFactory: (_, _) => new MetadataChatClient("bedrock", new Uri("https://bedrock.example/model/invoke")));
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockFast);

        (await client.GetResponseAsync("hello")).Text.Should().Be("bedrock");
        var decision = guard.Decisions.Should().ContainSingle().Which;
        decision.Site.Should().Be("EG-MDL-02", "a cloud:bedrock:* key keeps Bedrock's inventory site");
        decision.Destination.Should().Be("https://bedrock.example", "the inner client names where it dials");
        decision.DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
    }

    /// <summary>
    /// The production Bedrock inner client (<see cref="AwsBedrockChatClientFactory"/> over the AWS SDK's MEAI adapter)
    /// reports no <see cref="ChatClientMetadata.ProviderUri"/>, so rule 1 never applies to it and the default
    /// <c>cloud:bedrock:*</c> record stays the region reconstruction. Building the client sends nothing; no call is made.
    /// If an SDK update starts reporting a URI, this fails and the Bedrock known limit must be restated.
    /// </summary>
    [Fact]
    public void CloudBedrock_TheAwsClientReportsNoProviderUri_SoTheRegionIsReconstructed()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IEgressGuard>(new RecordingGuard());
        services.AddAshlarMeaiPipeline(
            configure: o =>
            {
                o.Bedrock.Enabled = true;
                o.Bedrock.Region = "us-west-2";
            },
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient());
        using var provider = services.BuildServiceProvider();

        var client = provider.GetRequiredKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockFast);

        var metadata = client.GetService(typeof(ChatClientMetadata)).Should().BeOfType<ChatClientMetadata>().Which;
        metadata.ProviderName.Should().Be("aws.bedrock", "the inner client is the AWS SDK's");
        metadata.ProviderUri.Should().BeNull("the AWS MEAI adapter names no endpoint");
        var request = client.Should().BeOfType<EgressGuardChatClient>().Which.Request;
        request.Should().NotBeNull();
        request!.Site.Should().Be("EG-MDL-02");
        request.Destination.Should().Be(new Uri("https://bedrock-runtime.us-west-2.amazonaws.com"));
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

    // ---------------------------------------------------------------------------------------------------------
    // PR 4.1 (D34): Ollama cloud models are relayed by the local daemon to ollama.com
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// A model whose id ends in <c>-cloud</c> or <c>:cloud</c>, in any case, runs on ollama.com, relayed by the local
    /// daemon, so a call to <c>localhost:11434</c> with it is recorded as an external model at
    /// <c>https://ollama.com</c>, on both paths. Before, it was recorded as the loopback daemon: Host.
    /// </summary>
    [Theory]
    [InlineData("llama3:cloud")]
    [InlineData("gpt-oss:120b-cloud")]
    [InlineData("GLM-4.6:CLOUD")]
    [InlineData("qwen3-coder:480b-Cloud")]
    [InlineData(" deepseek-v3.1:671b-cloud ")]
    public async Task AnOllamaCloudModel_IsRecordedAsAnExternalModelAtOllamaCom(string model)
    {
        using var environment = UnsetOllamaEnvironment();
        var guard = new RecordingGuard();
        using var provider = LocalOllamaAt(guard, MeaiPipelineOptions.DefaultOllamaBaseUrl);
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);
        var options = new ChatOptions { ModelId = model };

        (await client.GetResponseAsync("hello", options)).Text.Should().Be("ollama");
        await foreach (var _ in client.GetStreamingResponseAsync("hello", options))
        {
        }

        guard.Decisions.Should().HaveCount(2, "one decision per call, on both paths");
        guard.Decisions.Should().OnlyContain(d =>
            d.Site == "EG-MDL-01"
            && d.Family == EgressFamilies.ModelMeai
            && d.Destination == OllamaCom
            && d.DestinationClass == EgressDestinationClass.ExternalModel
            && !d.Access.Allowed,
            "model '{0}' runs on ollama.com, relayed by the local daemon", model);
    }

    /// <summary>
    /// With no model in the call's options, the inner client's default model is the one sent, so it decides: a
    /// default cloud model is recorded at ollama.com too.
    /// </summary>
    [Fact]
    public async Task AnOllamaCloudModel_AsTheDefaultModel_IsRecordedAtOllamaCom()
    {
        using var environment = UnsetOllamaEnvironment();
        var guard = new RecordingGuard();
        using var provider = LocalOllamaAt(guard, MeaiPipelineOptions.DefaultOllamaBaseUrl, defaultModel: "gpt-oss:20b-cloud");
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        await client.GetResponseAsync("hello");
        await client.GetResponseAsync("hello", new ChatOptions { ModelId = "llama3.1:latest" });

        guard.Decisions.Should().HaveCount(2);
        guard.Decisions[0].Destination.Should().Be(OllamaCom, "the default model gpt-oss:20b-cloud is the one sent");
        guard.Decisions[0].DestinationClass.Should().Be(EgressDestinationClass.ExternalModel);
        guard.Decisions[1].Destination.Should().Be(MeaiPipelineOptions.DefaultOllamaBaseUrl, "the call names a local model");
        guard.Decisions[1].DestinationClass.Should().Be(EgressDestinationClass.Host);
    }

    /// <summary>Only the <c>-cloud</c> and <c>:cloud</c> endings count; a local model stays where its daemon is.</summary>
    [Theory]
    [InlineData("llama3")]
    [InlineData("llama3.1:latest")]
    [InlineData("cloudy:7b")]
    [InlineData("cloud-llama:7b")]
    [InlineData("llama3:cloudy")]
    [InlineData("")]
    public async Task ALocalModel_IsRecordedAtTheDaemon(string model)
    {
        using var environment = UnsetOllamaEnvironment();
        var guard = new RecordingGuard();
        using var provider = LocalOllamaAt(guard, MeaiPipelineOptions.DefaultOllamaBaseUrl);
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        await client.GetResponseAsync("hello", new ChatOptions { ModelId = model });

        var decision = guard.Decisions.Should().ContainSingle().Which;
        decision.Destination.Should().Be(MeaiPipelineOptions.DefaultOllamaBaseUrl, "'{0}' is not a cloud model", model);
        decision.DestinationClass.Should().Be(EgressDestinationClass.Host);
    }

    /// <summary>
    /// The MEAI layer and <see cref="OllamaProvider"/> each hold a copy of the rule (the two assemblies share no internal
    /// helper); the same ids are cloud models on both routes.
    /// </summary>
    [Theory]
    [InlineData("llama3:cloud", true)]
    [InlineData("gpt-oss:120b-cloud", true)]
    [InlineData("GLM-4.6:CLOUD", true)]
    [InlineData(" kimi-k2:1t-Cloud\t", true)]
    [InlineData("foo-cloud", true)]
    [InlineData("llama3", false)]
    [InlineData("llama3.1:latest", false)]
    [InlineData("cloudy:7b", false)]
    [InlineData("llama3:cloudy", false)]
    [InlineData("cloud", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void TheCloudModelRule_IsTheSameOnBothOllamaRoutes(string? model, bool cloud)
    {
        var meai = typeof(EgressGuardChatClient).GetMethod("IsOllamaCloudModel", BindingFlags.NonPublic | BindingFlags.Static);
        meai.Should().NotBeNull("EgressGuardChatClient holds the MEAI copy of the rule");

        ((bool)meai!.Invoke(null, [model])!).Should().Be(cloud, "the MEAI route, for '{0}'", model);
        OllamaProvider.IsOllamaCloudModel(model).Should().Be(cloud, "OllamaProvider, for '{0}'", model);
    }

    // ---------------------------------------------------------------------------------------------------------
    // PR 4.1: the default Ollama client does not follow redirects
    // ---------------------------------------------------------------------------------------------------------

    /// <summary>
    /// The default <see cref="OllamaHttpChatClient"/> no longer follows a redirect: its record names the first hop
    /// only, and a 307 would re-send the whole conversation to wherever <c>Location</c> points, unrecorded. The 3xx
    /// now reaches the caller as a failed call, and the redirect target never sees the conversation. Before, the
    /// client followed the 307 and the target got the request.
    /// </summary>
    [Fact]
    public async Task TheDefaultOllamaClient_DoesNotFollowARedirect()
    {
        using var environment = UnsetOllamaEnvironment();
        using var server = RedirectingOllama.Start();
        using var client = new OllamaHttpChatClient(Options.Create(new MeaiPipelineOptions { OllamaBaseUrl = server.BaseUrl }));

        var call = () => client.GetResponseAsync("hello");

        (await call.Should().ThrowAsync<HttpRequestException>("the 307 is returned, not followed"))
            .Which.StatusCode.Should().Be(HttpStatusCode.TemporaryRedirect);
        server.RedirectsServed.Should().Be(1);
        server.MovedRequests.Should().Be(0, "the redirect target never sees the conversation");
    }

    /// <summary>Unsets the four Ollama endpoint and model variables until disposed, restoring each.</summary>
    private static IDisposable UnsetOllamaEnvironment() => new Scopes(
        EnvironmentVariableScope.Unset(MeaiPipelineOptions.OllamaBaseUrlEnvVar),
        EnvironmentVariableScope.Unset(MeaiPipelineOptions.LegacyOllamaBaseUrlEnvVar),
        EnvironmentVariableScope.Unset(MeaiPipelineOptions.OllamaModelEnvVar),
        EnvironmentVariableScope.Unset(MeaiPipelineOptions.LegacyOllamaModelEnvVar));

    private sealed class Scopes(params IDisposable[] scopes) : IDisposable
    {
        public void Dispose()
        {
            for (var i = scopes.Length - 1; i >= 0; i--)
                scopes[i].Dispose();
        }
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

    /// <summary>The <c>local:ollama</c> target over an Ollama client whose base address is <paramref name="url"/>.</summary>
    private static ServiceProvider LocalOllamaAt(IEgressGuard guard, string url, string defaultModel = "twin-model")
    {
        var services = new ServiceCollection();
        services.AddSingleton(guard);
        services.AddAshlarMeaiPipeline(
            ollamaInnerFactory: _ => new OllamaHttpChatClient(
                new HttpClient(new OllamaStubHandler("ollama")) { BaseAddress = new Uri(url + "/") }, defaultModel),
            onnxInnerFactory: _ => new FakeChatClient());
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The default Ollama client's shape over a stub handler: the base address is exactly the one the default factory
    /// gives it (<see cref="OllamaEndpointResolver.ResolveBaseUrl"/> plus <c>/</c>), so the record follows the
    /// resolver's precedence, and nothing reaches a socket.
    /// </summary>
    private static OllamaHttpChatClient StubOllama(IServiceProvider services, string text)
    {
        var options = services.GetService<IOptions<MeaiPipelineOptions>>()?.Value;
        var http = new HttpClient(new OllamaStubHandler(text))
        {
            BaseAddress = new Uri(OllamaEndpointResolver.ResolveBaseUrl(options) + "/"),
        };
        return new OllamaHttpChatClient(http, "twin-model", ownsHttp: true);
    }

    /// <summary>Answers every request with one Ollama <c>/api/chat</c> line, which both the plain and streaming paths read.</summary>
    private sealed class OllamaStubHandler(string text) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = "{\"model\":\"twin-model\",\"message\":{\"role\":\"assistant\",\"content\":\"" + text + "\"},\"done\":true}\n";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>An inner client that names where it dials through <see cref="ChatClientMetadata"/>, as a host's own might.</summary>
    private sealed class MetadataChatClient(string text, Uri? providerUri) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, text);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            serviceType == typeof(ChatClientMetadata)
                ? new ChatClientMetadata("twin", providerUri, "twin-model")
                : serviceType.IsInstanceOfType(this) ? this : null;

        public void Dispose()
        {
        }
    }

    /// <summary>An inner client whose <c>GetService</c> throws, so resolving its destination faults.</summary>
    private sealed class ThrowingMetadataChatClient(string text) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, text)));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            throw new InvalidOperationException("broken metadata");

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A loopback Ollama that answers <c>POST /api/chat</c> with a 307 to <c>/moved/api/chat</c>, and that path with a
    /// normal reply, counting each.
    /// </summary>
    private sealed class RedirectingOllama : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        private int _redirects;
        private int _moved;

        private RedirectingOllama(int port)
        {
            BaseUrl = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(BaseUrl + "/");
        }

        public string BaseUrl { get; }

        public int RedirectsServed => Volatile.Read(ref _redirects);

        public int MovedRequests => Volatile.Read(ref _moved);

        /// <summary>
        /// Listens on a free loopback port. <see cref="HttpListener"/> cannot bind port 0 and report it, so a port is
        /// probed and released first; another socket in the process can take it before the listener binds it, so a
        /// failed bind retries on a freshly probed port.
        /// </summary>
        public static RedirectingOllama Start()
        {
            const int attempts = 10;
            for (var attempt = 1; ; attempt++)
            {
                var server = new RedirectingOllama(FreeLoopbackPort());
                try
                {
                    server._listener.Start();
                }
                catch (Exception ex) when (ex is HttpListenerException or SocketException)
                {
                    server.Dispose();
                    if (attempt == attempts)
                        throw;

                    continue;
                }

                _ = Task.Run(server.ServeAsync);
                return server;
            }
        }

        private static int FreeLoopbackPort()
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            try
            {
                return ((IPEndPoint)probe.LocalEndpoint).Port;
            }
            finally
            {
                probe.Stop();
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Close();
            _stop.Dispose();
        }

        private async Task ServeAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
                {
                    return;
                }

                using var response = context.Response;
                if (context.Request.Url!.AbsolutePath.StartsWith("/moved/", StringComparison.Ordinal))
                {
                    Interlocked.Increment(ref _moved);
                    var body = Encoding.UTF8.GetBytes(
                        "{\"model\":\"m\",\"message\":{\"role\":\"assistant\",\"content\":\"moved\"},\"done\":true}");
                    response.StatusCode = 200;
                    response.ContentType = "application/json";
                    await response.OutputStream.WriteAsync(body).ConfigureAwait(false);
                }
                else
                {
                    Interlocked.Increment(ref _redirects);
                    response.StatusCode = 307;
                    response.RedirectLocation = BaseUrl + "/moved" + context.Request.Url.AbsolutePath;
                }
            }
        }
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
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public EgressDecision Evaluate(EgressRequest request)
        {
            Interlocked.Increment(ref _calls);
            throw new InvalidOperationException("custom guard fault");
        }
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
