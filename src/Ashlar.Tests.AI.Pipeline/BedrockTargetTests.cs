using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Routing;
using Xunit;
// MEAI 10.9.0 ships its own Microsoft.Extensions.AI.RoutingChatClient; alias the Ashlar router
// explicitly so the simple name keeps binding to ours (CS0104 otherwise).
using RoutingChatClient = Ashlar.AI.Pipeline.Routing.RoutingChatClient;

namespace Ashlar.Tests.AI.Pipeline;

public sealed class BedrockTargetTests
{
    [Fact]
    public async Task Policy_permitting_cloud_routes_to_bedrock_through_sanitization_with_audit()
    {
        var localSpy = new SpyChatClient("local");
        var bedrockSpy = new SpyChatClient("bedrock");
        var auditor = new InMemoryChatInvocationAuditor();
        var availability = new InMemoryTargetAvailability()
            .Set(MeaiTargetKeys.LocalOllama, false)
            .Set(MeaiTargetKeys.LocalOnnx, false)
            .Set(DefaultRouteCandidateTable.CloudBedrockBalanced, true);

        var services = new ServiceCollection();
        services.AddSingleton<ITargetAvailability>(availability);
        services.AddSingleton<IChatInvocationAuditor>(auditor);
        services.AddAshlarMeaiPipeline(
            configure: o =>
            {
                o.Bedrock.Enabled = true;
                o.Bedrock.BalancedModelId = "test-balanced-model";
            },
            ollamaInnerFactory: _ => localSpy,
            onnxInnerFactory: _ => new SpyChatClient("onnx"),
            bedrockInnerFactory: (_, modelId) =>
            {
                modelId.Should().Be("test-balanced-model");
                return bedrockSpy;
            },
            registerDefaultRouter: true);

        // Replace default availability with our forced-down locals.
        services.AddSingleton<ITargetAvailability>(availability);

        await using var provider = services.BuildServiceProvider();
        // Resolve router via Auditing wrapper
        var client = provider.GetRequiredService<IChatClient>();

        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RouteRequestHints.CapabilityTierKey] = "balanced",
            },
        };

        // Force redact disposition for cloud via options already default BlockOnSecretRedactOnPii —
        // prompt with PII should be redacted (not blocked) for cloud policy before bedrock spy.
        var response = await client.GetResponseAsync("Reach alice@example.com", options);
        response.Text.Should().Be("bedrock");
        localSpy.CallCount.Should().Be(0);
        bedrockSpy.CallCount.Should().Be(1);
        bedrockSpy.Calls[0][0].Text.Should().Contain("[REDACTED_EMAIL]");
        auditor.Records.Should().Contain(r =>
            r.TargetKey == RoutingChatClient.RouterTargetKey
            && r.ReasonCode == RouteReasonCodes.Fallback);
        auditor.Records.Should().Contain(r =>
            r.TargetKey == DefaultRouteCandidateTable.CloudBedrockBalanced
            && r.Outcome == "success"
            && r.RedactionCount >= 1);
    }

    [Fact]
    public async Task Policy_forbidding_cloud_keeps_prompt_local()
    {
        var localSpy = new SpyChatClient("local-ok");
        var bedrockSpy = new SpyChatClient("bedrock");
        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(
            configure: o =>
            {
                o.Bedrock.Enabled = false; // do not auto-allow cloud
            },
            ollamaInnerFactory: _ => localSpy,
            onnxInnerFactory: _ => new SpyChatClient("onnx"),
            bedrockInnerFactory: (_, _) => bedrockSpy);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();
        var response = await client.GetResponseAsync("hello");
        response.Text.Should().Be("local-ok");
        bedrockSpy.CallCount.Should().Be(0);
        provider.GetKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockBalanced)
            .Should().BeNull();
    }

    [Fact]
    public void Raw_bedrock_runtime_client_is_not_resolvable()
    {
        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(
            configure: o => o.Bedrock.Enabled = true,
            ollamaInnerFactory: _ => new FakeChatClient(),
            onnxInnerFactory: _ => new FakeChatClient(),
            bedrockInnerFactory: (_, _) => new FakeChatClient("b"));

        using var provider = services.BuildServiceProvider();
        provider.GetService<Amazon.BedrockRuntime.IAmazonBedrockRuntime>().Should().BeNull();
        provider.GetService<Amazon.BedrockRuntime.AmazonBedrockRuntimeClient>().Should().BeNull();
        provider.GetRequiredKeyedService<IChatClient>(DefaultRouteCandidateTable.CloudBedrockFast)
            .Should().BeOfType<EgressGuardChatClient>();
    }

    [Fact]
    public async Task Integration_bedrock_live_gated_by_env()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ASHLAR_TEST_BEDROCK"), "1", StringComparison.Ordinal))
        {
            return; // gated — no AWS calls in CI by default
        }

        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(configure: o =>
        {
            o.Bedrock.Enabled = true;
            // Model ids from env overrides if present
            var balanced = Environment.GetEnvironmentVariable("ASHLAR_BEDROCK_BALANCED_MODEL");
            if (!string.IsNullOrWhiteSpace(balanced))
            {
                o.Bedrock.BalancedModelId = balanced;
            }
        });

        // Mark locals unavailable so router escalates.
        services.AddSingleton<ITargetAvailability>(
            new InMemoryTargetAvailability()
                .Set(MeaiTargetKeys.LocalOllama, false)
                .Set(MeaiTargetKeys.LocalOnnx, false));

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();
        var options = new ChatOptions
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                [RouteRequestHints.CapabilityTierKey] = "balanced",
            },
        };

        var response = await client.GetResponseAsync("Reply with the single word ok.", options);
        response.Text.Should().NotBeNullOrWhiteSpace();
    }
}
