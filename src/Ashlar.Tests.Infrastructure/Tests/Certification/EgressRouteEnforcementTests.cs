using System.Net;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline.Governance;
using Ashlar.Tests.Infrastructure.Helpers;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

/// <summary>SPEC-007 4.7: opting in must stop the real routes before their inner transport runs.</summary>
[Trait("Category", "Certification")]
[Collection("EnvironmentVariables")]
public sealed class EgressRouteEnforcementTests
{
    private static readonly Uri Remote = new("https://remote.example/private?credential=canary");

    [Theory]
    [InlineData(false, "enforce", 0)]
    [InlineData(true, "enforce", 0)]
    [InlineData(false, "report", 1)]
    [InlineData(true, "report", 1)]
    public async Task Http_refuses_before_the_inner_handler_while_report_mode_delegates(bool sync, string mode, int sends)
    {
        var transport = new CountingHandler();
        var guard = new CapturingGuard(new EgressGuard("full", mode));
        using var client = EgressHttp.CreateClient(transport, EgressFamilies.HttpRaw, "route-http", guard);
        using var request = new HttpRequestMessage(HttpMethod.Post, Remote);
        var error = await Record.ExceptionAsync(async () =>
        {
            using var response = sync ? client.Send(request) : await client.SendAsync(request);
        });

        transport.Sends.Should().Be(sends);
        guard.Decision.Should().NotBeNull();
        AssertOutcome(error, mode, guard.Decision!);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Chat_refuses_synchronously_before_call_or_stream_creation(bool streaming)
    {
        var inner = new CountingChatClient();
        var guard = new CapturingGuard(new EgressGuard("full", "enforce"));
        using var client = new EgressGuardChatClient(inner,
            new EgressRequest(EgressFamilies.ModelMeai, "route-chat", Remote), guard);
        var error = Record.Exception(() =>
        {
            if (streaming)
                _ = client.GetStreamingResponseAsync(Array.Empty<ChatMessage>());
            else
                _ = client.GetResponseAsync(Array.Empty<ChatMessage>());
        });

        inner.Calls.Should().Be(0);
        AssertOutcome(error, "enforce", guard.Decision!);
    }

    [Theory]
    [InlineData(false, false, "enforce", 0)]
    [InlineData(false, true, "enforce", 0)]
    [InlineData(true, false, "enforce", 0)]
    [InlineData(true, true, "enforce", 0)]
    [InlineData(false, false, "report", 1)]
    [InlineData(false, true, "report", 1)]
    [InlineData(true, false, "report", 1)]
    [InlineData(true, true, "report", 1)]
    public async Task A_throwing_or_null_returning_host_guard_obeys_the_process_mode(
        bool chat, bool returnsNull, string mode, int calls)
    {
        using var profile = new EnvironmentVariableScope("ASHLAR_DEPLOYMENT_PROFILE", "full");
        using var setting = new EnvironmentVariableScope("ASHLAR_EGRESS_MODE", mode);
        using var state = new EgressProcessStateScope(reset: true);
        var faulty = new FaultyGuard(returnsNull);
        Exception? error;
        if (chat)
        {
            var inner = new CountingChatClient();
            using var client = new EgressGuardChatClient(inner,
                new EgressRequest(EgressFamilies.ModelMeai, "route-fault-chat", Remote), faulty);
            error = await Record.ExceptionAsync(() => client.GetResponseAsync(Array.Empty<ChatMessage>()));
            inner.Calls.Should().Be(calls);
        }
        else
        {
            var inner = new CountingHandler();
            using var client = EgressHttp.CreateClient(inner, EgressFamilies.HttpRaw, "route-fault-http", faulty);
            error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync(Remote); });
            inner.Sends.Should().Be(calls);
        }

        if (mode == "report")
            error.Should().BeNull();
        else
        {
            error.Should().NotBeNull();
            error!.GetType().Name.Should().Be("EgressRefusedException");
            var decision = (EgressDecision)error.GetType().GetProperty("Decision")!.GetValue(error)!;
            decision.Refused.Should().BeTrue();
            decision.Access.Reason.Should().Be(AccessDenialReason.NoDecision);
            decision.Fault.Should().NotBeNullOrWhiteSpace();
            error.Message.Should().NotContain("fault-secret");
        }
    }

    private static void AssertOutcome(Exception? error, string mode, EgressDecision decision)
    {
        if (mode == "report")
        {
            error.Should().BeNull();
            return;
        }

        error.Should().NotBeNull();
        error!.GetType().Name.Should().Be("EgressRefusedException");
        error.GetType().BaseType.Should().Be(typeof(Exception), "a refusal must not be retried as a network fault");
        error.GetType().GetProperty("Decision")!.GetValue(error).Should().BeSameAs(decision);
        error.Message.Should().Contain(decision.Ref).And.Contain(decision.Site);
        error.Message.Should().NotContain("credential").And.NotContain("/private");
        error.Message.Should().NotContain(decision.Access.Detail);
    }

    private sealed class CapturingGuard(IEgressGuard inner) : IEgressGuard
    {
        public EgressDecision? Decision { get; private set; }
        public EgressDecision Evaluate(EgressRequest request) => Decision = inner.Evaluate(request);
    }

    private sealed class FaultyGuard(bool returnsNull) : IEgressGuard
    {
        public EgressDecision Evaluate(EgressRequest request) =>
            returnsNull ? null! : throw new InvalidOperationException("fault-secret");
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return new(HttpStatusCode.OK) { RequestMessage = request };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class CountingChatClient : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
            ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Empty();
        }
        private static async IAsyncEnumerable<ChatResponseUpdate> Empty()
        {
            await Task.CompletedTask;
            yield break;
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
