using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Governance;
using Xunit;

namespace Ashlar.Tests.AI.Pipeline;

public sealed class GovernanceMiddlewareTests
{
    private sealed class ForceRedactPolicy : ITargetSanitizePolicy
    {
        public SanitizeDisposition GetDisposition(string targetKey) => SanitizeDisposition.Redact;
    }

    private sealed class ForceBlockPiiPolicy : ITargetSanitizePolicy
    {
        public SanitizeDisposition GetDisposition(string targetKey) => SanitizeDisposition.BlockOnPiiOrSecret;
    }

    private sealed class DenyCloudPolicy : IChatTargetAccessPolicy
    {
        public bool IsAllowed(string? callerIdentity, string targetKey, string? modelId, out string? denyReason)
        {
            if (targetKey.StartsWith("cloud:", StringComparison.OrdinalIgnoreCase))
            {
                denyReason = "cloud forbidden";
                return false;
            }

            denyReason = null;
            return true;
        }
    }

    [Fact]
    public async Task Policy_deny_short_circuits_provider()
    {
        var spy = new SpyChatClient();
        var auditor = new InMemoryChatInvocationAuditor();
        var services = new ServiceCollection();
        services.AddSingleton<IChatTargetAccessPolicy, DenyCloudPolicy>();
        services.AddSingleton<IChatMessageSanitizer, DefaultChatMessageSanitizer>();
        services.AddSingleton<ITargetSanitizePolicy, DefaultTargetSanitizePolicy>();
        services.AddSingleton<IChatInvocationAuditor>(auditor);
        services.AddKeyedChatClient("cloud:bedrock:fast", _ => spy)
            .UseAshlarGovernance("cloud:bedrock:fast");

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>("cloud:bedrock:fast");

        var act = async () => await client.GetResponseAsync("hello");
        var ex = await act.Should().ThrowAsync<PolicyViolationException>();
        ex.Which.Code.Should().Be("target_denied");
        spy.CallCount.Should().Be(0);
        auditor.Records.Should().Contain(r => r.Outcome == "denied" && r.ReasonCode == "target_denied");
    }

    [Fact]
    public async Task Pii_is_redacted_before_reaching_spy()
    {
        var spy = new SpyChatClient();
        var services = new ServiceCollection();
        services.AddSingleton<IChatTargetAccessPolicy, DefaultChatTargetAccessPolicy>();
        services.AddSingleton<IChatMessageSanitizer, DefaultChatMessageSanitizer>();
        services.AddSingleton<ITargetSanitizePolicy, ForceRedactPolicy>();
        services.AddSingleton<IChatInvocationAuditor, InMemoryChatInvocationAuditor>();
        services.AddKeyedChatClient(MeaiTargetKeys.LocalOllama, _ => spy)
            .UseAshlarGovernance(MeaiTargetKeys.LocalOllama);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        await client.GetResponseAsync("Contact me at alice@example.com please");

        spy.CallCount.Should().Be(1);
        spy.Calls[0][0].Text.Should().Contain("[REDACTED_EMAIL]");
        spy.Calls[0][0].Text.Should().NotContain("alice@example.com");
    }

    [Fact]
    public async Task Audit_record_produced_on_success()
    {
        var spy = new SpyChatClient("ok");
        var auditor = new InMemoryChatInvocationAuditor();
        var services = new ServiceCollection();
        services.AddSingleton<IChatTargetAccessPolicy, DefaultChatTargetAccessPolicy>();
        services.AddSingleton<IChatMessageSanitizer, DefaultChatMessageSanitizer>();
        services.AddSingleton<ITargetSanitizePolicy, DefaultTargetSanitizePolicy>();
        services.AddSingleton<IChatInvocationAuditor>(auditor);
        services.AddKeyedChatClient(MeaiTargetKeys.LocalOllama, _ => spy)
            .UseAshlarGovernance(MeaiTargetKeys.LocalOllama);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);
        await client.GetResponseAsync("hello");

        auditor.Records.Should().ContainSingle(r => r.Outcome == "success" && r.TargetKey == MeaiTargetKeys.LocalOllama);
    }

    [Fact]
    public async Task Audit_record_produced_on_fault()
    {
        var failing = new ThrowingChatClient();
        var auditor = new InMemoryChatInvocationAuditor();
        var services = new ServiceCollection();
        services.AddSingleton<IChatTargetAccessPolicy, DefaultChatTargetAccessPolicy>();
        services.AddSingleton<IChatMessageSanitizer, DefaultChatMessageSanitizer>();
        services.AddSingleton<ITargetSanitizePolicy, DefaultTargetSanitizePolicy>();
        services.AddSingleton<IChatInvocationAuditor>(auditor);
        services.AddKeyedChatClient(MeaiTargetKeys.LocalOllama, _ => failing)
            .UseAshlarGovernance(MeaiTargetKeys.LocalOllama);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        var act = async () => await client.GetResponseAsync("hello");
        await act.Should().ThrowAsync<InvalidOperationException>();
        auditor.Records.Should().Contain(r => r.Outcome == "fault");
    }

    [Fact]
    public async Task Audit_record_produced_on_cancellation()
    {
        var hanging = new HangingChatClient();
        var auditor = new InMemoryChatInvocationAuditor();
        var services = new ServiceCollection();
        services.AddSingleton<IChatTargetAccessPolicy, DefaultChatTargetAccessPolicy>();
        services.AddSingleton<IChatMessageSanitizer, DefaultChatMessageSanitizer>();
        services.AddSingleton<ITargetSanitizePolicy, DefaultTargetSanitizePolicy>();
        services.AddSingleton<IChatInvocationAuditor>(auditor);
        services.AddKeyedChatClient(MeaiTargetKeys.LocalOllama, _ => hanging)
            .UseAshlarGovernance(MeaiTargetKeys.LocalOllama);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        using var cts = new CancellationTokenSource();
        var call = client.GetResponseAsync("hello", cancellationToken: cts.Token);
        await Task.Delay(50);
        cts.Cancel();

        var act = async () => await call;
        await act.Should().ThrowAsync<OperationCanceledException>();
        auditor.Records.Should().Contain(r => r.Outcome == "cancelled");
    }

    [Fact]
    public void Governance_composition_order_is_egress_then_policy_then_sanitize_then_audit()
    {
        var services = new ServiceCollection();
        services.RegisterGovernanceDefaults();
        services.AddKeyedChatClient(MeaiTargetKeys.LocalOllama, _ => new FakeChatClient())
            .UseAshlarGovernance(MeaiTargetKeys.LocalOllama);

        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        client.Should().BeOfType<EgressGuardChatClient>();

        var types = WalkInnerClients(client).Select(c => c.GetType()).ToList();
        types.Should().Equal(
            typeof(EgressGuardChatClient),
            typeof(PolicyGateChatClient),
            typeof(SanitizingChatClient),
            typeof(AuditingChatClient),
            typeof(FakeChatClient));
    }

    private static IEnumerable<IChatClient> WalkInnerClients(IChatClient root)
    {
        yield return root;
        var current = root;
        var prop = typeof(DelegatingChatClient).GetProperty(
            "InnerClient",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        while (current is DelegatingChatClient)
        {
            current = (IChatClient)prop!.GetValue(current)!;
            yield return current;
        }
    }

    [Fact]
    public void Architecture_resolved_clients_are_governed_not_raw_providers()
    {
        var services = new ServiceCollection();
        services.AddAshlarMeaiPipeline(
            ollamaInnerFactory: _ => new FakeChatClient("a"),
            onnxInnerFactory: _ => new FakeChatClient("b"));

        using var provider = services.BuildServiceProvider();

        foreach (var key in new[] { MeaiTargetKeys.LocalOllama, MeaiTargetKeys.LocalOnnx })
        {
            var client = provider.GetRequiredKeyedService<IChatClient>(key);
            client.Should().BeOfType<EgressGuardChatClient>();
            client.Should().NotBeOfType<OllamaHttpChatClient>();
            client.Should().NotBeOfType<LlamaSharpChatClient>();
            client.Should().NotBeOfType<FakeChatClient>();
        }

        provider.GetService<OllamaHttpChatClient>().Should().BeNull();
        provider.GetService<LlamaSharpChatClient>().Should().BeNull();
    }

    [Fact]
    public async Task Sanitization_block_does_not_call_provider()
    {
        var spy = new SpyChatClient();
        var auditor = new InMemoryChatInvocationAuditor();
        var services = new ServiceCollection();
        services.AddSingleton<IChatTargetAccessPolicy, DefaultChatTargetAccessPolicy>();
        services.AddSingleton<IChatMessageSanitizer, DefaultChatMessageSanitizer>();
        services.AddSingleton<ITargetSanitizePolicy, ForceBlockPiiPolicy>();
        services.AddSingleton<IChatInvocationAuditor>(auditor);
        services.AddKeyedChatClient(MeaiTargetKeys.LocalOllama, _ => spy)
            .UseAshlarGovernance(MeaiTargetKeys.LocalOllama);

        await using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredKeyedService<IChatClient>(MeaiTargetKeys.LocalOllama);

        var act = async () => await client.GetResponseAsync("email alice@example.com");
        await act.Should().ThrowAsync<PolicyViolationException>();
        spy.CallCount.Should().Be(0);
        auditor.Records.Should().Contain(r => r.ReasonCode == "sanitization_blocked");
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("boom");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }

    private sealed class HangingChatClient : IChatClient
    {
        public async Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ChatResponse();
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            GetStreaming(cancellationToken);

        private static async IAsyncEnumerable<ChatResponseUpdate> GetStreaming(
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            yield break;
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
