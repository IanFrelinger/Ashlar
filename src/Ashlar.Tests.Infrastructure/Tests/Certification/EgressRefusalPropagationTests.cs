using System.Reflection;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline;
using Ashlar.AI.Pipeline.Routing;
using RoutingChatClient = Ashlar.AI.Pipeline.Routing.RoutingChatClient;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Infrastructure.Execution.Models;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressRefusalPropagationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Unkeyed_DI_router_audits_refused_calls_and_stream_creation_or_iteration(int stage)
    {
        var refusal = Refusal();
        var audit = new Audit(false);
        var router = new Mock<IChatRouter>();
        router.Setup(x => x.Select(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<RouteCapabilityTier>(), It.IsAny<string>()))
            .Returns(new RouteDecision { ChosenTargetKey = MeaiTargetKeys.LocalOllama,
                ReasonCode = RouteReasonCodes.PreferredLocal, CandidatesConsidered = [] });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatRouter>(router.Object);
        services.AddSingleton<IChatInvocationAuditor>(audit);
        services.AddSingleton<IEgressGuard>(new EgressGuard("full", "report"));
        services.AddAshlarMeaiPipeline(ollamaInnerFactory: _ => new RefusingClient(stage, Wrap(refusal)),
            onnxInnerFactory: _ => new RefusingClient(stage, Wrap(refusal)));
        using var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IChatClient>();
        var error = await Record.ExceptionAsync(async () =>
        {
            if (stage == 0) await client.GetResponseAsync(Array.Empty<ChatMessage>());
            else await foreach (var unused in client.GetStreamingResponseAsync(Array.Empty<ChatMessage>())) { }
        });
        error.Should().BeSameAs(refusal);
        var record = audit.Records.Where(r => r.TargetKey == RoutingChatClient.RouterTargetKey && r.Outcome != "routed")
            .Should().ContainSingle().Subject;
        record.Outcome.Should().Be("denied");
        record.ReasonCode.Should().Be("egress_refused");
        record.EgressDecision.Should().BeSameAs(refusal.Decision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Echo_fallback_never_masks_a_direct_or_wrapped_refusal(bool deterministic, bool wrapped)
    {
        var refusal = Refusal();
        var inner = new Mock<IModel>();
        inner.Setup(m => m.CompleteAsync(It.IsAny<ModelInput>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(wrapped ? Wrap(refusal) : refusal);
        var model = new HotSwappableModel(inner.Object, NullLogger<HotSwappableModel>.Instance);
        var previous = Environment.GetEnvironmentVariable("ASHLAR_ALLOW_MOCK");
        try
        {
            Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", "1");
            var directives = "ashlar.model.provider=openai"
                + (deterministic ? "\nashlar.model.prefer=deterministic" : "");
            var error = await Record.ExceptionAsync(() => model.CompleteAsync(
                new ModelInput(new[] { ("system", directives), ("user", "payload-canary") }), CancellationToken.None));
            error.Should().BeSameAs(refusal);
            inner.Verify(m => m.CompleteAsync(It.IsAny<ModelInput>(), It.IsAny<CancellationToken>()), Times.Once);
        }
        finally { Environment.SetEnvironmentVariable("ASHLAR_ALLOW_MOCK", previous); }
    }

    [Fact]
    public void Network_classifier_checks_every_exception_branch_before_retrying()
    {
        var refusal = Refusal();
        TransientClassifiers.Network(refusal).Should().BeFalse();
        TransientClassifiers.Network(Wrap(refusal)).Should().BeFalse();
        TransientClassifiers.Network(new IOException("outer", Wrap(refusal))).Should().BeFalse();
        TransientClassifiers.Network(new HttpRequestException("ordinary")).Should().BeTrue();
        TransientClassifiers.Network(new IOException("ordinary")).Should().BeTrue();
        TransientClassifiers.Network(new TimeoutException("ordinary")).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(0, true)]
    [InlineData(1, false)]
    [InlineData(1, true)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    [InlineData(4, true)]
    public async Task Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(int stage, bool auditThrows)
    {
        var refusal = Refusal();
        var inner = new RefusingClient(stage, Wrap(refusal));
        var audit = new Audit(auditThrows);
        using var client = new AuditingChatClient(inner, audit, "router");
        var error = await Record.ExceptionAsync(async () =>
        {
            if (stage == 0) await client.GetResponseAsync(Array.Empty<ChatMessage>());
            else await foreach (var unused in client.GetStreamingResponseAsync(Array.Empty<ChatMessage>())) { }
        });
        error.Should().BeSameAs(refusal);
        audit.Records.Should().ContainSingle();
        var record = audit.Records.Single();
        record.Outcome.Should().Be("denied");
        record.ReasonCode.Should().Be("egress_refused");
        record.EgressDecision.Should().BeSameAs(refusal.Decision);
        inner.DisposedEnumerator.Should().Be(stage >= 3);
    }

    [Fact]
    public void Remote_text_contains_only_fixed_text_and_a_canonical_random_reference()
    {
        var refusal = Refusal();
        var helper = typeof(EgressGuard).Assembly.GetType("Ashlar.Abstractions.Security.Egress.EgressRefusal", true)!;
        var format = helper.GetMethod("RemoteMessage", BindingFlags.Static | BindingFlags.NonPublic)!;
        format.Invoke(null, new object?[] { refusal.Ref }).Should().Be($"egress refused by policy (ref {refusal.Ref})");
        var malformed = (string)format.Invoke(null, new object?[] { "secret-site-label-sequence-canary" })!;
        malformed.Should().MatchRegex(@"^egress refused by policy \(ref [0-9a-f]{16}\)$");
        malformed.Should().NotContain("canary");
    }

    private static EgressRefusedException Refusal() => new(new EgressGuard("full", "enforce").Evaluate(
        new EgressRequest(EgressFamilies.Http, "site-canary", new Uri("https://remote.example/private-canary"))));

    private static Exception Wrap(EgressRefusedException refusal) => new HttpRequestException("wrapper-canary",
        new AggregateException(new InvalidOperationException("unrelated-canary"),
            new InvalidOperationException("nested-canary", refusal)));

    private sealed class Audit(bool throws) : IChatInvocationAuditor
    {
        internal List<ChatInvocationAuditRecord> Records { get; } = new();
        public void Record(ChatInvocationAuditRecord record)
        {
            Records.Add(record);
            if (throws) throw new InvalidOperationException("audit-canary");
        }
    }

    private sealed class RefusingClient(int stage, Exception refusal) : IChatClient,
        IAsyncEnumerable<ChatResponseUpdate>, IAsyncEnumerator<ChatResponseUpdate>
    {
        internal bool DisposedEnumerator { get; private set; }
        public ChatResponseUpdate Current => throw new InvalidOperationException("No updates expected");
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => throw refusal;
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default) => stage == 1 ? throw refusal : this;
        public IAsyncEnumerator<ChatResponseUpdate> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            stage == 2 ? throw refusal : this;
        public ValueTask<bool> MoveNextAsync() => stage == 3 ? throw refusal : new(false);
        public ValueTask DisposeAsync()
        {
            DisposedEnumerator = true;
            if (stage == 4) throw refusal;
            // Must not mask the iteration refusal, even when disposal also fails.
            throw new InvalidOperationException("dispose-canary");
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
