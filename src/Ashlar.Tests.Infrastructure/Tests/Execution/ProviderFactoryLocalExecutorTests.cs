using Ashlar.Abstractions.Security.Egress;
using Ashlar.Agents.TestKit;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Execution.Routing;
using Xunit;
using IProviderFactory = Ashlar.Infrastructure.Execution.IProviderFactory;

namespace Ashlar.Tests.Infrastructure.Tests.Execution;

/// <summary>
/// Card #809: a wrapped refusal must keep <c>EGRESS_REFUSED</c> and the redacted message/ref in the
/// local-executor result; ordinary failures keep <c>local.execution_failed</c>.
/// </summary>
public sealed class ProviderFactoryLocalExecutorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refusal_preserves_EGRESS_REFUSED_and_redacted_message_even_when_wrapped(bool wrapped)
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "local-executor-refusal", new Uri("https://remote.example"))));
        var thrown = wrapped
            ? (Exception)new InvalidOperationException("wrapper-secret-canary", refusal)
            : refusal;
        var factory = new Mock<IProviderFactory>(MockBehavior.Strict);
        factory.Setup(f => f.ExecuteLLMAsync("ollama", string.Empty, "prompt", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(thrown);
        var sut = new ProviderFactoryLocalExecutor(factory.Object, NullLogger<ProviderFactoryLocalExecutor>.Instance);

        var result = await sut.ExecuteAsync(
            new RunPodJobPayload { ModelId = "m1", Prompt = "prompt" },
            new JobRequirements { ModelId = "m1" },
            new FakeExecutionContext { Provider = "ollama" });

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("EGRESS_REFUSED");
        result.Error.Message.Should().Be("Local execution refused by egress policy.");
        result.Error.Detail.Should().Be(refusal.Message)
            .And.Contain(refusal.Ref)
            .And.NotContain("wrapper-secret-canary");
        factory.VerifyAll();
    }

    [Fact]
    public async Task Ordinary_failure_keeps_local_execution_failed()
    {
        var factory = new Mock<IProviderFactory>(MockBehavior.Strict);
        factory.Setup(f => f.ExecuteLLMAsync("ollama", string.Empty, "prompt", It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("ordinary-outage"));
        var sut = new ProviderFactoryLocalExecutor(factory.Object, NullLogger<ProviderFactoryLocalExecutor>.Instance);

        var result = await sut.ExecuteAsync(
            new RunPodJobPayload { ModelId = "m1", Prompt = "prompt" },
            new JobRequirements { ModelId = "m1" },
            new FakeExecutionContext { Provider = "ollama" });

        result.IsFailure.Should().BeTrue();
        result.Error!.Code.Should().Be("local.execution_failed");
        result.Error.Message.Should().Be("Local execution failed.");
        result.Error.Detail.Should().Be("ordinary-outage");
        factory.VerifyAll();
    }
}
