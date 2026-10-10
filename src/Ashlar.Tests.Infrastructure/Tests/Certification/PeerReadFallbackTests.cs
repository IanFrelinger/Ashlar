using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline.Clients;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.AI.Pipeline.Models;
using Ashlar.Core.Application.Resilience.Ports;
using Ashlar.Infrastructure.Execution;
using Ashlar.Infrastructure.Execution.LoadPolicy;
using Ashlar.Infrastructure.Execution.Models;
using Ashlar.Infrastructure.Resilience;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;
using IProviderFactory = Ashlar.Infrastructure.Execution.IProviderFactory;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class PeerReadFallbackTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_governed_MEAI_read_refusal_survives_the_model_adapter_and_echo_fallback(bool deterministic)
    {
        using var mock = new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", "1");
        using var client = new EgressGuardChatClient(new FakeChatClient("unread-canary"), null, new EgressGuard("full"), "peer:test");
        var meai = new MeaiBackedModel(client, NullLogger<MeaiBackedModel>.Instance);
        var model = new HotSwappableModel(meai, NullLogger<HotSwappableModel>.Instance);
        var directives = "ashlar.model.provider=openai" + (deterministic ? "\nashlar.model.prefer=deterministic" : "");
        Func<Task> call = () => model.CompleteAsync(new ModelInput(new[] { ("system", directives), ("user", "hello") }), CancellationToken.None);
        var error = await call.Should().ThrowAsync<ReadRefusedException>();
        error.Which.ToString().Should().NotContain("unread-canary");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrapped_read_refusals_do_not_trigger_echo_fallback(bool deterministic)
    {
        using var mock = new EnvironmentVariableScope("ASHLAR_ALLOW_MOCK", "1");
        var refusal = new ReadRefusedException(AccessDenialReason.SystemHighData);
        var inner = new Mock<IModel>();
        inner.Setup(x => x.CompleteAsync(It.IsAny<ModelInput>(), It.IsAny<CancellationToken>())).ThrowsAsync(Wrapped(refusal));
        var model = new HotSwappableModel(inner.Object, NullLogger<HotSwappableModel>.Instance);
        var directives = "ashlar.model.provider=openai" + (deterministic ? "\nashlar.model.prefer=deterministic" : "");
        var error = await Record.ExceptionAsync(() => model.CompleteAsync(new ModelInput(new[] { ("system", directives) }), CancellationToken.None));
        error.Should().BeSameAs(refusal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task A_read_refusal_stops_adaptive_provider_escalation(int kind)
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "full");
        var refusal = new ReadRefusedException(AccessDenialReason.SystemHighData);
        var inner = new Mock<IProviderFactory>();
        inner.Setup(x => x.IsProviderAvailable(It.IsAny<string>())).Returns(true);
        var attempts = 0;
        Task<string> Fail() { attempts++; return Task.FromException<string>(Wrapped(refusal)); }
        inner.Setup(x => x.ExecuteLLMAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(() => Fail());
        inner.Setup(x => x.ExecuteVisionAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(() => Fail());
        inner.Setup(x => x.ExecuteVisionMultiFrameAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<IReadOnlyList<byte[]>>(), It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Returns(() => Fail());
        var policy = new Mock<ILoadPolicy>();
        policy.Setup(x => x.ResolveProvider(inner.Object)).Returns("ollama");
        var factory = new AdaptiveProviderFactory(inner.Object, policy.Object);
        var error = await Record.ExceptionAsync(() => kind switch
        {
            0 => factory.ExecuteLLMAsync("ignored", "system", "user", new object()),
            1 => factory.ExecuteVisionAsync("ignored", "system", "user", [], new object()),
            _ => factory.ExecuteVisionMultiFrameAsync("ignored", "system", "user", [], new object()),
        });
        error.Should().BeSameAs(refusal);
        attempts.Should().Be(1);
    }

    [Fact]
    public async Task Read_refusal_never_retries_even_with_an_always_transient_host_classifier()
    {
        var refusal = new ReadRefusedException(AccessDenialReason.SystemHighData);
        var calls = 0;
        var executor = new ResilientExecutor();
        var error = await Record.ExceptionAsync(() => executor.ExecuteAsync<string>(_ =>
        {
            calls++;
            return Task.FromException<string>(Wrapped(refusal));
        }, new RetryPolicy(3, TimeSpan.Zero, _ => true)));
        error.Should().BeSameAs(refusal);
        calls.Should().Be(1);
        TransientClassifiers.Network(new IOException("outer", Wrapped(refusal))).Should().BeFalse();
        TransientClassifiers.Network(new IOException("ordinary")).Should().BeTrue();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Audit_failure_cannot_hide_a_wrapped_read_refusal(bool stream)
    {
        var refusal = new ReadRefusedException(AccessDenialReason.SystemHighData);
        var inner = new Mock<IChatClient>();
        inner.Setup(x => x.GetResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(Wrapped(refusal));
        inner.Setup(x => x.GetStreamingResponseAsync(It.IsAny<IEnumerable<ChatMessage>>(), It.IsAny<ChatOptions?>(), It.IsAny<CancellationToken>()))
            .Throws(Wrapped(refusal));
        var audit = new Mock<IChatInvocationAuditor>();
        audit.Setup(x => x.Record(It.IsAny<ChatInvocationAuditRecord>())).Throws(new InvalidOperationException("audit-canary"));
        using var client = new AuditingChatClient(inner.Object, audit.Object, "router:test");
        var error = await Record.ExceptionAsync(async () =>
        {
            if (stream) { await foreach (var _ in client.GetStreamingResponseAsync("hello")) { } }
            else await client.GetResponseAsync("hello");
        });
        error.Should().BeSameAs(refusal);
        audit.Verify(x => x.Record(It.Is<ChatInvocationAuditRecord>(r => r.Outcome == "denied" && r.ReasonCode == "read_refused")), Times.Once);
    }

    private static Exception Wrapped(ReadRefusedException refusal) => new AggregateException(
        new InvalidOperationException("unrelated-canary"), new IOException("private-wrapper-canary", refusal));
}
