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
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task An_opaque_redirect_refusal_disposes_the_response_and_warns_that_the_body_may_have_gone(bool sync, bool loggerThrows)
    {
        var inner = new OpaqueRedirectHandler();
        using var handler = EgressHttp.Wrap(inner, EgressFamilies.Http, "route-opaque", new EgressGuard("full", "enforce"));
        var warnings = new List<string>();
        Action<string> warn = message =>
        {
            warnings.Add(message);
            if (loggerThrows) throw new InvalidOperationException("logger failure");
        };
        handler.GetType().GetProperty("UnmediatedRedirectWarning",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.SetValue(handler, warn);
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Post, "http://localhost/allowed");
        var error = await Record.ExceptionAsync(async () =>
        {
            using var response = sync ? invoker.Send(request, CancellationToken.None)
                : await invoker.SendAsync(request, CancellationToken.None);
        });
        error.Should().BeOfType<EgressRefusedException>();
        inner.Sends.Should().Be(1, "the opaque transport has already sent before its final URI is known");
        inner.Body.Disposed.Should().BeTrue();
        warnings.Should().ContainSingle().Which.Should().Contain("the body may already have gone");
    }

    private sealed class OpaqueRedirectHandler : HttpMessageHandler
    {
        public int Sends { get; private set; }
        public DisposalContent Body { get; } = new();
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Sends++;
            return new(HttpStatusCode.OK) { RequestMessage = new HttpRequestMessage(HttpMethod.Get, Remote), Content = Body };
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(Send(request, cancellationToken));
    }

    private sealed class DisposalContent : ByteArrayContent
    {
        public DisposalContent() : base(Array.Empty<byte>()) { }
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    [Fact]
    public void A_reentrant_publish_skip_is_counted_and_cannot_bypass_enforcement()
    {
        var inner = new CountingHandler();
        using var client = EgressHttp.CreateClient(inner, EgressFamilies.Http, "route-reentrant-inner",
            new EgressGuard("full", "enforce"));
        var counter = typeof(EgressDecisionLog).GetProperty("ReentrantRefusalSkips",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        counter.Should().NotBeNull();
        var before = (long)counter!.GetValue(null)!;
        Exception? refusal = null;
        var calls = 0;
        using var subscription = EgressDecisionLog.Subscribe(new CallbackSink(decision =>
        {
            if (decision.Site != "route-reentrant-outer") return;
            calls++;
            using var request = new HttpRequestMessage(HttpMethod.Get, Remote);
            refusal = Record.Exception(() => { using var response = client.Send(request); });
        }));
        new EgressGuard("full", "report").Evaluate(new EgressRequest(EgressFamilies.Http, "route-reentrant-outer", Remote));
        calls.Should().Be(1);
        refusal.Should().BeOfType<EgressRefusedException>();
        inner.Sends.Should().Be(0);
        ((long)counter.GetValue(null)!).Should().BeGreaterThan(before);
    }

    private sealed class CallbackSink(Action<EgressDecision> callback) : IEgressDecisionSink
    {
        public void Record(EgressDecision decision) => callback(decision);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Chat_refusal_reaches_the_auditor_and_an_audit_fault_cannot_mask_it(bool streaming, bool auditFault)
    {
        var inner = new CountingChatClient();
        var guard = new CapturingGuard(new EgressGuard("full", "enforce"));
        var auditor = new CapturingAuditor(auditFault);
        using var client = new EgressGuardChatClient(inner,
            new EgressRequest(EgressFamilies.ModelMeai, "route-audit", Remote), guard, "cloud:test", auditor);
        Action call = () =>
        {
            if (streaming) _ = client.GetStreamingResponseAsync(Array.Empty<ChatMessage>());
            else _ = client.GetResponseAsync(Array.Empty<ChatMessage>());
        };
        call.Should().Throw<EgressRefusedException>().Which.Decision.Should().BeSameAs(guard.Decision);
        inner.Calls.Should().Be(0);
        var record = auditor.Seen.Should().ContainSingle().Which;
        record.Outcome.Should().Be("denied");
        record.ReasonCode.Should().Be("egress_refused");
        record.TargetKey.Should().Be("cloud:test");
        record.EgressDecision.Should().BeSameAs(guard.Decision);
    }

    [Fact]
    public async Task Http_async_refusal_is_a_faulted_task_even_when_a_decision_sink_throws()
    {
        using var subscription = EgressDecisionLog.Subscribe(new ThrowingSink());
        var inner = new CountingHandler();
        using var handler = EgressHttp.Wrap(inner, EgressFamilies.Http, "route-async-fault",
            new EgressGuard("full", "enforce"));
        using var invoker = new HttpMessageInvoker(handler);
        using var request = new HttpRequestMessage(HttpMethod.Get, Remote);
        Task<HttpResponseMessage>? task = null;
        Action call = () => task = invoker.SendAsync(request, CancellationToken.None);
        call.Should().NotThrow();
        task.Should().NotBeNull();
        Func<Task> awaitCall = async () => await task!;
        await awaitCall.Should().ThrowAsync<EgressRefusedException>();
        inner.Sends.Should().Be(0);
    }

    private sealed class ThrowingSink : IEgressDecisionSink
    {
        public void Record(EgressDecision decision) => throw new InvalidOperationException("sink failure");
    }

    private sealed class CapturingAuditor(bool throws) : IChatInvocationAuditor
    {
        public List<ChatInvocationAuditRecord> Seen { get; } = [];
        public void Record(ChatInvocationAuditRecord record)
        {
            Seen.Add(record);
            if (throws) throw new InvalidOperationException("audit failure");
        }
    }

    [Theory]
    [InlineData(false, "enforce", 0)]
    [InlineData(true, "enforce", 0)]
    [InlineData(false, "report", 1)]
    [InlineData(true, "report", 1)]
    public async Task Http_refuses_before_the_inner_handler_while_report_mode_delegates(bool sync, string mode, int sends)
    {
        var transport = new CountingHandler();
        var guard = new CapturingGuard(new EgressGuard("full", mode));
        using var client = EgressHttp.CreateClient(transport, EgressFamilies.Http, "route-http", guard);
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
        var normallyAllowed = new Uri("http://127.0.0.1:11434");
        Exception? error;
        if (chat)
        {
            var inner = new CountingChatClient();
            using var client = new EgressGuardChatClient(inner,
                new EgressRequest(EgressFamilies.ModelMeai, "route-fault-chat", normallyAllowed), faulty);
            error = await Record.ExceptionAsync(() => client.GetResponseAsync(Array.Empty<ChatMessage>()));
            inner.Calls.Should().Be(calls);
        }
        else
        {
            var inner = new CountingHandler();
            using var client = EgressHttp.CreateClient(inner, EgressFamilies.Http, "route-fault-http", faulty);
            error = await Record.ExceptionAsync(async () => { using var response = await client.GetAsync(normallyAllowed); });
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
