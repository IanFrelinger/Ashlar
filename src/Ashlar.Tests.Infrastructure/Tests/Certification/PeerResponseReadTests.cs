using System.Runtime.CompilerServices;
using System.Text.Json;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using Ashlar.AI.Pipeline.Governance;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Tests.Certification;

[Trait("Category", "Certification")]
public sealed class PeerResponseReadTests
{
    private const string Canary = "classified-response-canary";
    private static SubjectClearanceAuthority Authority(string label) => new(new Dictionary<string, string?> { ["reader"] = label });
    private static EgressGuardChatClient Guard(IChatClient inner, string clearance = "Public", IChatResponseLabelVerifier? labels = null,
        IChatInvocationAuditor? auditor = null, string? target = "peer:test") =>
        new(inner, null, new EgressGuard("full"), target, auditor, Authority(clearance), labels);

    [Theory]
    [InlineData("Public", false)]
    [InlineData("TopSecret", false)]
    [InlineData("SystemHigh", true)]
    public async Task An_unverified_response_requires_explicit_SystemHigh(string clearance, bool allowed)
    {
        var inner = new Peer();
        // This peer-supplied claim is not an attestation.
        inner.Response.AdditionalProperties = new() { ["securityLabel"] = "Public" };
        using var scope = EgressSubject.Enter("reader", new HighWaterMark());
        using var client = Guard(inner, clearance);
        if (allowed) (await client.GetResponseAsync("hello")).Text.Should().Be(Canary);
        else await Refused(() => client.GetResponseAsync("hello"));
        inner.Calls.Should().Be(1, "the check mediates the real response, not a preflight fixture");
    }

    [Theory]
    [InlineData("Secret", "Secret", true)]
    [InlineData("Public", "Secret", false)]
    [InlineData("TopSecret", null, false)]
    [InlineData("TopSecret", "secret", false)]
    [InlineData("SystemHigh", null, true)]
    public async Task A_verified_per_response_label_is_bounded_by_clearance(string clearance, string? label, bool allowed)
    {
        var inner = new Peer();
        var verifier = new BoundVerifier(inner.Response, label);
        var mark = new HighWaterMark();
        using var scope = EgressSubject.Enter("reader", mark);
        using var client = Guard(inner, clearance, verifier);
        if (allowed)
        {
            (await client.GetResponseAsync("hello")).Text.Should().Be(Canary);
            mark.Current.Should().Be(SecurityLabel.ParseOrSystemHigh(label));
        }
        else await Refused(() => client.GetResponseAsync("hello"));
        verifier.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_substituted_payload_or_target_cannot_reuse_an_attestation(bool payloadChanged)
    {
        var inner = new Peer();
        var verifier = new BoundVerifier(inner.Response, "Public");
        if (payloadChanged) inner.Response.Messages[0].Contents = [new TextContent("substituted " + Canary)];
        using var scope = EgressSubject.Enter("reader", new HighWaterMark());
        using var client = Guard(inner, "Public", verifier, target: payloadChanged ? "peer:test" : "peer:other");
        await Refused(() => client.GetResponseAsync("hello"));
    }

    [Fact]
    public async Task A_verifier_failure_and_a_throwing_auditor_cannot_release_the_response()
    {
        using var scope = EgressSubject.Enter("reader", new HighWaterMark());
        using var client = Guard(new Peer(), "Public", new BrokenVerifier(), new BrokenAuditor());
        await Refused(() => client.GetResponseAsync("hello"));
    }

    [Fact]
    public async Task Every_update_is_verified_before_delivery_and_a_later_unlabelled_update_is_refused()
    {
        var inner = new Peer();
        var verifier = new BoundVerifier(inner.First, "Public");
        using var scope = EgressSubject.Enter("reader", new HighWaterMark());
        using var client = Guard(inner, "Public", verifier);
        var delivered = new List<string>();
        await Refused(async () => { await foreach (var update in client.GetStreamingResponseAsync("hello")) delivered.Add(update.Text); });
        delivered.Should().Equal(Canary);
        verifier.Calls.Should().Be(2);
    }

    [Fact]
    public async Task A_stream_checks_the_consumers_live_scope_for_each_update()
    {
        var inner = new Peer();
        using var client = Guard(inner, "SystemHigh");
        var frame = EgressSubject.Enter("reader", new HighWaterMark());
        await using var iterator = client.GetStreamingResponseAsync("hello").GetAsyncEnumerator();
        (await iterator.MoveNextAsync()).Should().BeTrue();
        frame.Dispose();
        await Refused(async () => { await iterator.MoveNextAsync(); });
    }

    [Fact]
    public async Task A_completion_cannot_keep_an_expired_grant()
    {
        var signal = new TaskCompletionSource<ChatResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var inner = new Peer { Pending = signal.Task };
        using var client = Guard(inner, "SystemHigh");
        var frame = EgressSubject.Enter("reader", new HighWaterMark());
        var response = client.GetResponseAsync("hello");
        frame.Dispose();
        signal.SetResult(inner.Response);
        await Refused(() => response);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Fault_payloads_do_not_bypass_the_read_check(bool stream)
    {
        using var scope = EgressSubject.Enter("reader", new HighWaterMark());
        using var client = Guard(new Peer { Fault = true });
        await Refused(async () =>
        {
            if (stream) { await foreach (var _ in client.GetStreamingResponseAsync("hello")) { } }
            else await client.GetResponseAsync("hello");
        });
    }

    [Theory]
    [InlineData("local:test")]
    [InlineData("cloud:test")]
    public async Task Model_responses_remain_derived_from_the_callers_input(string target)
    {
        using var scope = EgressSubject.Enter("reader", new HighWaterMark());
        using var client = Guard(new Peer(), target: target);
        (await client.GetResponseAsync("hello")).Text.Should().Be(Canary);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("TopSecret", false)]
    [InlineData("SystemHigh", true)]
    public void The_shared_guard_requires_live_explicit_grants(string? clearance, bool allowed)
    {
        using var frame = EgressSubject.Enter("reader", new HighWaterMark(SecurityLabel.SystemHigh));
        Action check = () => SubjectReadGuard.RequireRead(clearance is null ? null : Authority(clearance), SecurityLabel.SystemHigh);
        if (allowed) check.Should().NotThrow();
        else check.Should().Throw<ReadRefusedException>().Which.ErrorCode.Should().Be("read_refused");
    }

    [Fact]
    public async Task An_untrusted_cancellation_payload_is_redacted()
    {
        var inner = new Peer { Pending = Task.FromException<ChatResponse>(new OperationCanceledException(Canary)) };
        using var frame = EgressSubject.Enter("reader", new HighWaterMark());
        using var client = Guard(inner);
        await Refused(() => client.GetResponseAsync("hello"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_prevents_delivery_even_if_the_peer_ignores_the_token(bool stream)
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var frame = EgressSubject.Enter("reader", new HighWaterMark());
        using var client = Guard(new Peer(), "SystemHigh");
        var delivered = 0;
        Func<Task> call = async () =>
        {
            if (stream)
            {
                await foreach (var _ in client.GetStreamingResponseAsync("hello", cancellationToken: cancelled.Token)) delivered++;
            }
            else { await client.GetResponseAsync("hello", cancellationToken: cancelled.Token); delivered++; }
        };
        await call.Should().ThrowAsync<OperationCanceledException>();
        delivered.Should().Be(0);
    }

    [Fact]
    public void SystemHigh_read_clearance_does_not_authorize_model_escalation()
    {
        var inner = new Peer();
        using var frame = EgressSubject.Enter("reader", new HighWaterMark(new SecurityLabel(SecurityLevel.Secret)));
        using var client = new EgressGuardChatClient(inner,
            new EgressRequest(EgressFamilies.ModelMeai, "peer-read-escalation-control", new Uri("https://model.example/")),
            new EgressGuard("full", "enforce"), "cloud:test", null, Authority("SystemHigh"));
        Action call = () => _ = client.GetResponseAsync("hello");
        call.Should().Throw<EgressRefusedException>();
        inner.Calls.Should().Be(0);
    }

    private static async Task Refused(Func<Task> call)
    {
        var result = await call.Should().ThrowAsync<ReadRefusedException>();
        result.Which.ErrorCode.Should().Be("read_refused");
        result.Which.ToString().Should().NotContain(Canary).And.NotContain("Secret");
        result.Which.InnerException.Should().BeNull();
        result.Which.Ref.Should().HaveLength(32);
    }

    // A trusted test adapter pins the exact object and its complete serialized payload, rather than its label claim.
    private sealed class BoundVerifier(object response, string? label) : IChatResponseLabelVerifier
    {
        private readonly string _payload = JsonSerializer.Serialize(response, response.GetType());
        public int Calls;
        private string? Check(string? target, object current)
        {
            Calls++;
            return target == "peer:test" && ReferenceEquals(response, current)
                && _payload == JsonSerializer.Serialize(current, current.GetType()) ? label : null;
        }
        public string? Verify(string? target, ChatResponse response) => Check(target, response);
        public string? Verify(string? target, ChatResponseUpdate response) => Check(target, response);
    }
    private sealed class BrokenVerifier : IChatResponseLabelVerifier
    {
        public string? Verify(string? target, ChatResponse response) => throw new Exception(Canary);
        public string? Verify(string? target, ChatResponseUpdate response) => throw new Exception(Canary);
    }
    private sealed class BrokenAuditor : IChatInvocationAuditor
    {
        public void Record(ChatInvocationAuditRecord record) => throw new Exception(Canary);
    }
    private sealed class Peer : IChatClient
    {
        public ChatResponse Response { get; } = new(new ChatMessage(ChatRole.Assistant, Canary));
        public ChatResponseUpdate First { get; } = new(ChatRole.Assistant, Canary);
        public Task<ChatResponse>? Pending { get; init; }
        public bool Fault { get; init; }
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Fault ? Task.FromException<ChatResponse>(new InvalidOperationException(Canary)) : Pending ?? Task.FromResult(Response);
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            if (Fault) throw new InvalidOperationException(Canary);
            yield return First;
            yield return new ChatResponseUpdate(ChatRole.Assistant, Canary);
        }
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
