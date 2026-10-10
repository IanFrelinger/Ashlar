using System.Net;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.Agents.TestKit;
using Ashlar.Core.Application.Execution.Routing;
using Ashlar.Core.Domain.Execution;
using Ashlar.Infrastructure.Execution.Routing;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class PeerBrickResponseReadTests
{
    private const string Canary = "peer-private-response-canary";

    [Theory]
    [InlineData(null, 0, false)]
    [InlineData("TopSecret", 0, false)]
    [InlineData("SystemHigh", 0, true)]
    [InlineData(null, 1, false)]
    [InlineData("TopSecret", 1, false)]
    [InlineData("SystemHigh", 1, true)]
    [InlineData(null, 2, false)]
    [InlineData("TopSecret", 2, false)]
    [InlineData("SystemHigh", 2, true)]
    public async Task Actual_peer_response_status_body_and_error_require_SystemHigh_before_fallback(string? grant, int responseKind, bool admitted)
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(responseKind == 1 ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                ReasonPhrase = Canary,
                Content = new StringContent(responseKind == 2
                    ? "{\"success\":false,\"errorMessage\":\"" + Canary + "\"}"
                    : "{\"success\":true,\"payload\":\"CAk=\",\"summary\":\"" + Canary + "\",\"securityLabel\":\"Public\"}"),
            };
            response.Headers.Add("X-Ashlar-Security-Label", "Public");
            return Task.FromResult(response);
        }));
        var brick = Routed(http, grant);
        var mark = new HighWaterMark();
        using var frame = EgressSubject.Enter("reader", mark);
        if (admitted)
        {
            var result = await brick.ExecuteAsync(Payload(), Requirements(), new FakeExecutionContext());
            if (responseKind == 0)
            {
                result.IsSuccess.Should().BeTrue();
                result.Value!.Payload.Should().Equal(8, 9);
                result.Value.Summary.Should().Be(Canary);
            }
            else result.Error!.Detail.Should().Contain(Canary);
            mark.Current.Should().Be(SecurityLabel.SystemHigh);
        }
        else
        {
            var error = await Record.ExceptionAsync(() => brick.ExecuteAsync(Payload(), Requirements(), new FakeExecutionContext()));
            error.Should().BeOfType<ReadRefusedException>();
            error!.ToString().Should().NotContain(Canary);
            calls.Should().Be(1, "read refusal stops the eligible second peer and survives CapabilityRoutingBrick");
        }
    }

    [Fact]
    public async Task A_peer_transport_exception_cannot_leak_its_payload_or_trigger_an_alternate_peer()
    {
        var calls = 0;
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException(Canary));
        }));
        using var frame = EgressSubject.Enter("reader", new HighWaterMark());
        var error = await Record.ExceptionAsync(() => Routed(http, null).ExecuteAsync(Payload(), Requirements(), new FakeExecutionContext()));
        error.Should().BeOfType<ReadRefusedException>();
        error!.ToString().Should().NotContain(Canary);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_nested_read_refusal_is_preserved_even_when_the_local_caller_has_SystemHigh()
    {
        var calls = 0;
        var refusal = new ReadRefusedException(AccessDenialReason.LevelTooLow);
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            return Task.FromException<HttpResponseMessage>(new HttpRequestException(Canary, refusal));
        }));
        using var frame = EgressSubject.Enter("reader", new HighWaterMark());
        var error = await Record.ExceptionAsync(() => Routed(http, "SystemHigh").ExecuteAsync(Payload(), Requirements(), new FakeExecutionContext()));
        error.Should().BeSameAs(refusal);
        error!.ToString().Should().NotContain(Canary);
        calls.Should().Be(1);
    }

    [Fact]
    public async Task A_peer_response_cannot_keep_a_grant_after_its_runner_expires()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) => response.Task));
        var frame = EgressSubject.Enter("reader", new HighWaterMark());
        var call = Routed(http, "SystemHigh").ExecuteAsync(Payload(), Requirements(), new FakeExecutionContext());
        frame.Dispose();
        response.SetResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Canary) });
        var error = await Record.ExceptionAsync(() => call);
        error.Should().BeOfType<ReadRefusedException>();
        error!.ToString().Should().NotContain(Canary);
    }

    [Fact]
    public async Task Caller_cancellation_stops_dispatch_without_exposing_a_peer_fault_or_trying_another_peer()
    {
        var calls = 0;
        using var cancelled = new CancellationTokenSource();
        using var http = new HttpClient(new StubHttpMessageHandler((_, _) =>
        {
            calls++;
            cancelled.Cancel();
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException(Canary));
        }));
        using var frame = EgressSubject.Enter("reader", new HighWaterMark());
        var error = await Record.ExceptionAsync(() => Routed(http, "SystemHigh").ExecuteAsync(Payload(), Requirements(), new FakeExecutionContext(), cancelled.Token));
        error.Should().BeAssignableTo<OperationCanceledException>();
        error!.ToString().Should().NotContain(Canary);
        calls.Should().Be(1);
    }

    private static CapabilityRoutingBrick Routed(HttpClient http, string? grant)
    {
        var snapshot = new Mock<IPeerCapabilitySnapshot>();
        snapshot.SetupGet(x => x.Candidates).Returns(new[] { Peer("peer-a", 0), Peer("peer-b", 1) });
        var executor = new AshlarPeerBrickExecutor(new Factory(http), NullLogger<AshlarPeerBrickExecutor>.Instance,
            snapshot.Object, Options.Create(new RunPodBrickConfig { QueueDepthThreshold = 10, PeerTrustPolicy = "any" }),
            grant is null ? null : new SubjectClearanceAuthority(new Dictionary<string, string?> { ["reader"] = grant }));
        var router = new Mock<ICapabilityRouter>();
        router.Setup(x => x.ResolveExecutionTarget(It.IsAny<JobRequirements>())).Returns(new ExecutionTarget.Remote(executor, "peer"));
        return new CapabilityRoutingBrick(router.Object, NullLogger<CapabilityRoutingBrick>.Instance);
    }
    private static PeerExecutionCandidate Peer(string id, int queue) => new()
    {
        PeerId = id, Endpoint = "https://" + id + "/", AvailableVramBytes = 8192, ComputeClass = GpuComputeClass.High,
        QueueDepth = queue, CapturedAt = DateTimeOffset.UtcNow,
        TrustTier = Ashlar.Core.Application.Mesh.Models.PeerTrustTier.Trusted,
    };
    private static RunPodJobPayload Payload() => new() { ModelId = "m", Prompt = "hello" };
    private static JobRequirements Requirements() => new() { ModelId = "m", MinimumVramBytes = 1, ComputeClass = GpuComputeClass.Low };
    private sealed class Factory(HttpClient client) : IHttpClientFactory { public HttpClient CreateClient(string name) => client; }
}
