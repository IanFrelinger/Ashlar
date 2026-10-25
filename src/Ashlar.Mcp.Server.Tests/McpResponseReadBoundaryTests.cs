using System.Security.Claims;
using System.Text.Json;
using Ashlar.Abstractions;
using Ashlar.Abstractions.Security;
using Ashlar.Abstractions.Security.Egress;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Xunit;

#pragma warning disable ASHLAREXP002 // SecurityLabel and EgressSubject exercise denied and authorized MCP response disclosure.

namespace Ashlar.Mcp.Server.Tests;

[Trait("Category", "Security")]
[Trait("Category", "ProdStyle")]
public sealed class McpResponseReadBoundaryTests
{
    private const string ToolId = "private.read";
    private const string ToolName = "private_read";
    private const string Issuer = "https://identity.example/ashlar";
    private const string Canary = "response-secret-canary";

    [Theory(Timeout = 10000)]
    [InlineData(null)]
    [InlineData("invalid-label")]
    [InlineData("Secret")]
    public async Task Absent_or_unreadable_operator_label_denies_before_invocation(string? label)
    {
        var contributor = new Contributor(() => new(false, Canary));
        var bridge = Create(contributor, Options(label));

        var result = await Call(bridge);

        AssertDenied(result);
        contributor.Invocations.Should().Be(0);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observed_classified_read_blocks_text_structured_payload_and_errors_before_serialization(bool isError)
    {
        var payload = new SerializationSentinel();
        var contributor = new Contributor(() =>
        {
            EgressSubject.Observe(Label("Secret"));
            return new(isError, Canary, payload);
        });
        var bridge = Create(contributor, Options("Public"));

        var result = await Call(bridge);

        contributor.Invocations.Should().Be(1);
        AssertDenied(result);
        payload.SerializationCount.Should().Be(0, "denied payload getters must not execute");
    }

    [Fact(Timeout = 10000)]
    public async Task Authenticated_subject_with_matching_operator_clearance_receives_both_representations()
    {
        var payload = new SerializationSentinel();
        var options = Options("Secret//C:PROJECT_A//K:NOWEB", "Secret//C:PROJECT_A//K:NOWEB");
        var bridge = Create(new Contributor(() => new(false, Canary, payload)), options);

        var result = await Call(bridge, Principal());

        result.IsError.Should().BeFalse();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be(Canary);
        result.StructuredContent!.Value.GetProperty("Value").GetString().Should().Be(Canary);
        payload.SerializationCount.Should().Be(1);
    }

    [Theory(Timeout = 10000)]
    [InlineData("Secret//C:PROJECT_B//K:NOWEB")]
    [InlineData("Secret//C:PROJECT_A")]
    [InlineData("Confidential//C:PROJECT_A//K:NOWEB")]
    [InlineData("not-a-clearance")]
    public async Task Level_compartments_caveats_and_malformed_clearances_fail_closed(string clearance)
    {
        var contributor = new Contributor(() => new(false, Canary));
        var bridge = Create(contributor, Options("Secret//C:PROJECT_A//K:NOWEB", clearance));

        AssertDenied(await Call(bridge, Principal()));
        contributor.Invocations.Should().Be(0);
    }

    [Theory(Timeout = 10000)]
    [InlineData(false, Issuer, "alice")]
    [InlineData(true, "https://foreign.example", "alice")]
    [InlineData(true, Issuer, "unmapped-subject")]
    public async Task Arguments_clearance_claims_and_untrusted_identity_cannot_grant_clearance(
        bool authenticated, string issuer, string subject)
    {
        var contributor = new Contributor(() => new(false, Canary));
        var bridge = Create(contributor, Options("Secret", "SystemHigh"));
        var principal = Principal(authenticated, issuer, subject);
        principal.Identities.Single().AddClaim(new Claim("clearance", "SystemHigh"));

        var result = await bridge.CallToolAsync(new CallToolRequestParams
        {
            Name = ToolName,
            Arguments = new Dictionary<string, JsonElement>
            {
                ["clearance"] = JsonSerializer.SerializeToElement("SystemHigh"),
                ["subject"] = JsonSerializer.SerializeToElement("alice"),
                ["issuer"] = JsonSerializer.SerializeToElement(Issuer),
            },
        }, principal, CancellationToken.None);

        AssertDenied(result);
        contributor.Invocations.Should().Be(0);
    }

    [Fact(Timeout = 10000)]
    public async Task Missing_trusted_issuer_and_ambiguous_authenticated_subjects_remain_public()
    {
        var contributor = new Contributor(() => new(false, Canary));
        var options = Options("Secret", "SystemHigh");
        options.CallerIdentityIssuer = null;
        AssertDenied(await Call(Create(contributor, options), Principal()));

        options.CallerIdentityIssuer = Issuer;
        var principal = Principal();
        principal.Identities.Single().AddClaim(new Claim(ClaimTypes.NameIdentifier, "bob", ClaimValueTypes.String, Issuer));
        AssertDenied(await Call(Create(contributor, options), principal));
        contributor.Invocations.Should().Be(0);
    }

    [Fact(Timeout = 10000)]
    public async Task Public_tool_cannot_lower_an_enclosing_subject_label()
    {
        using var subject = EgressSubject.Enter("enclosing-private-operation", new HighWaterMark(Label("Secret")));
        var contributor = new Contributor(() => new(false, Canary));

        AssertDenied(await Call(Create(contributor, Options("Public"))));
        contributor.Invocations.Should().Be(0);
    }

    [Fact(Timeout = 10000)]
    public async Task Read_during_serialization_is_rechecked_before_either_representation_is_released()
    {
        var payload = new SerializationSentinel(observeOnSerialize: true);
        var bridge = Create(new Contributor(() => new(false, Canary, payload)), Options("Public"));

        var result = await Call(bridge);

        payload.SerializationCount.Should().Be(1, "the newly observed label is only known after the getter runs");
        AssertDenied(result);
    }

    [Fact(Timeout = 10000)]
    public async Task Built_in_toolbox_defers_serialization_until_after_the_read_decision()
    {
        var payload = new SerializationSentinel();
        var options = Options("Public");
        options.ExposedToolIds.Add(ToolId);
        var tool = new FakeTool(ToolId, invoke: (_, _, _) =>
        {
            EgressSubject.Observe(Label("Secret"));
            return Task.FromResult(new ToolResult(new FakeDelta(), payload));
        });
        var contributor = new ToolboxMcpToolContributor([tool], Microsoft.Extensions.Options.Options.Create(options),
            new SnapshotFactory(), NullLogger<ToolboxMcpToolContributor>.Instance);

        var result = await Call(Create(contributor, options));

        AssertDenied(result);
        payload.SerializationCount.Should().Be(0);
    }

    [Fact(Timeout = 10000)]
    public async Task Authorized_toolbox_payload_retains_json_text_and_structured_wire_shape()
    {
        var options = Options("Public");
        options.ExposedToolIds.Add(ToolId);
        var tool = new FakeTool(ToolId, invoke: (_, _, _) =>
            Task.FromResult(new ToolResult(new FakeDelta(), new { value = 42 })));
        var contributor = new ToolboxMcpToolContributor([tool], Microsoft.Extensions.Options.Options.Create(options),
            new SnapshotFactory(), NullLogger<ToolboxMcpToolContributor>.Instance);

        var result = await Call(Create(contributor, options));

        result.IsError.Should().BeFalse();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Be("{\"value\":42}");
        result.StructuredContent!.Value.GetProperty("value").GetInt32().Should().Be(42);
    }

    [Theory(Timeout = 10000)]
    [InlineData("Secret", false, false)]
    [InlineData(null, false, false)]
    [InlineData("Public", true, false)]
    [InlineData("Public", false, true)]
    public async Task Labelled_tool_results_and_faults_raise_the_response_mark_before_serialization(
        string? reportedLabel, bool failAfterRead, bool allowed)
    {
        var payload = new SerializationSentinel();
        var options = Options("Public");
        options.ExposedToolIds.Add(ToolId);
        var contributor = new ToolboxMcpToolContributor([new LabelledTool(payload, reportedLabel, failAfterRead)],
            Microsoft.Extensions.Options.Options.Create(options), new SnapshotFactory(),
            NullLogger<ToolboxMcpToolContributor>.Instance);

        var result = await Call(Create(contributor, options));

        if (allowed)
        {
            result.IsError.Should().BeFalse();
            result.StructuredContent!.Value.GetProperty("Value").GetString().Should().Be(Canary);
            payload.SerializationCount.Should().Be(1);
        }
        else
        {
            AssertDenied(result);
            payload.SerializationCount.Should().Be(0);
        }
    }

    [Fact(Timeout = 10000)]
    public async Task Contributor_protocol_exception_cannot_bypass_response_error_redaction()
    {
        var contributor = new Contributor(() => throw new McpProtocolException(Canary, McpErrorCode.InvalidParams));

        var result = await Call(Create(contributor, Options("Public")));

        result.IsError.Should().BeTrue();
        JsonSerializer.Serialize(result).Should().NotContain(Canary);
        result.StructuredContent.Should().BeNull();
    }

    [Theory(Timeout = 10000)]
    [InlineData("list")]
    [InlineData("call")]
    [InlineData("refresh")]
    public async Task Catalog_discovery_exceptions_are_redacted_for_every_request_path(string request)
    {
        var contributor = new FailingCatalogContributor();
        var bridge = Create(contributor, Options("Public"));
        if (request == "refresh")
        {
            contributor.Fail = false;
            await bridge.ListToolsAsync(CancellationToken.None);
            contributor.Fail = true;
        }

        var error = await Assert.ThrowsAsync<McpProtocolException>(async () =>
        {
            if (request == "list")
                await bridge.ListToolsAsync(CancellationToken.None);
            else
                await bridge.CallToolAsync(new CallToolRequestParams
                {
                    Name = request == "refresh" ? "unknown_tool_forces_refresh" : ToolName,
                }, null, CancellationToken.None);
        });

        error.ErrorCode.Should().Be(McpErrorCode.InternalError);
        error.Message.Should().Be("Tool catalog is unavailable.");
        error.ToString().Should().NotContain(Canary);
        error.InnerException.Should().BeNull();
    }

    [Fact(Timeout = 10000)]
    public async Task Startup_catalog_validation_retains_details_for_the_operator()
    {
        var bridge = Create(new FailingCatalogContributor(), Options("Public"));

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => bridge.ValidateCatalogAsync(CancellationToken.None));

        error.Message.Should().Be(Canary);
        error.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
    }

    [Fact(Timeout = 10000)]
    public async Task Gate_denial_reason_is_withheld_when_the_gate_observed_private_data()
    {
        var gate = new PrivateDataGate();
        var bridge = Create(new Contributor(() => new(false, Canary)), Options("Public"), gate);

        AssertDenied(await Call(bridge));
    }

    [Theory(Timeout = 10000)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Gate_and_snapshot_exceptions_are_redacted_before_the_protocol_formatter(bool snapshotFailure)
    {
        var payload = new SerializationSentinel();
        var contributor = new Contributor(() => new(false, Canary, payload));
        var bridge = Create(contributor, Options("Public"),
            snapshotFailure ? null : new ThrowingGate(), snapshotFailure ? new ThrowingSnapshotFactory() : null);

        var result = await Call(bridge);

        result.IsError.Should().BeTrue();
        result.StructuredContent.Should().BeNull();
        JsonSerializer.Serialize(result).Should().NotContain(Canary);
        contributor.Invocations.Should().Be(0);
        payload.SerializationCount.Should().Be(0);
    }

    [Fact(Timeout = 10000)]
    public async Task Unexpected_cancellation_exception_cannot_disclose_contributor_text()
    {
        var bridge = Create(new Contributor(() => throw new OperationCanceledException(Canary)), Options("Public"));

        var result = await Call(bridge);

        result.IsError.Should().BeTrue();
        JsonSerializer.Serialize(result).Should().NotContain(Canary);
    }

    [Fact(Timeout = 10000)]
    public async Task Requested_cancellation_preserves_cancellation_without_forwarding_contributor_text()
    {
        using var source = new CancellationTokenSource();
        var bridge = Create(new Contributor(() =>
        {
            source.Cancel();
            throw new OperationCanceledException(Canary, source.Token);
        }), Options("Public"));

        var error = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            bridge.CallToolAsync(new CallToolRequestParams { Name = ToolName }, null, source.Token).AsTask());

        error.Message.Should().NotContain(Canary);
        error.CancellationToken.Should().Be(source.Token);
    }

    [Theory]
    [InlineData("invalid", "Public", Issuer)]
    [InlineData("Public", "invalid", Issuer)]
    [InlineData("Public", "Secret", null)]
    public void Invalid_response_policy_fails_startup_validation(string label, string clearance, string? issuer)
    {
        var options = Options(label, clearance);
        options.Enabled = true;
        options.CallerIdentityIssuer = issuer;

        new ValidateAshlarMcpServerOptions().Validate(null, options).Failed.Should().BeTrue();
    }

    private static AshlarMcpServerOptions Options(string? label, string? clearance = null)
    {
        var options = new AshlarMcpServerOptions { CallerIdentityIssuer = Issuer };
        if (label is not null)
            options.ResponseLabels[ToolId] = label;
        if (clearance is not null)
            options.CallerClearances["alice"] = clearance;
        return options;
    }

    private static ClaimsPrincipal Principal(bool authenticated = true, string issuer = Issuer, string subject = "alice")
        => new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, subject, ClaimValueTypes.String, issuer)],
            authenticated ? "trusted-test-authentication-handler" : null));

    private static SecurityLabel Label(string label) => SecurityLabel.ParseOrSystemHigh(label);

    private static AshlarMcpToolBridge Create(IMcpToolContributor contributor, AshlarMcpServerOptions options,
        IMcpInvocationGate? gate = null, IMcpWorldSnapshotFactory? snapshots = null)
        => new([contributor], gate ?? new AllowGate(), snapshots ?? new SnapshotFactory(), Microsoft.Extensions.Options.Options.Create(options),
            NullLogger<AshlarMcpToolBridge>.Instance);

    private static Task<CallToolResult> Call(AshlarMcpToolBridge bridge, ClaimsPrincipal? principal = null)
        => bridge.CallToolAsync(new CallToolRequestParams { Name = ToolName }, principal, CancellationToken.None).AsTask();

    private static void AssertDenied(CallToolResult result)
    {
        result.IsError.Should().BeTrue();
        result.Content.Should().ContainSingle().Which.Should().BeOfType<TextContentBlock>()
            .Which.Text.Should().Be("Response denied by data access policy.");
        result.StructuredContent.Should().BeNull();
        JsonSerializer.Serialize(result).Should().NotContain(Canary);
    }

    private sealed class SerializationSentinel(bool observeOnSerialize = false)
    {
        private int _serializationCount;
        [System.Text.Json.Serialization.JsonIgnore]
        public int SerializationCount => _serializationCount;
        public string Value
        {
            get
            {
                _serializationCount++;
                if (observeOnSerialize)
                    EgressSubject.Observe(Label("Secret"));
                return Canary;
            }
        }
    }

    private sealed class Contributor(Func<AshlarMcpToolOutcome> invoke) : IMcpToolContributor
    {
        public int Invocations { get; private set; }
        public Task<IReadOnlyList<AshlarMcpToolDescriptor>> DescribeAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AshlarMcpToolDescriptor>>(
                [new(ToolName, ToolId, "read", Json.Object("{\"type\":\"object\"}"))]);

        public Task<AshlarMcpToolOutcome> InvokeAsync(string canonicalId, JsonElement arguments, CancellationToken ct)
        {
            Invocations++;
            return Task.FromResult(invoke());
        }
    }

    private sealed class FailingCatalogContributor : IMcpToolContributor
    {
        public bool Fail { get; set; } = true;
        public Task<IReadOnlyList<AshlarMcpToolDescriptor>> DescribeAsync(CancellationToken ct)
            => Fail
                ? throw new McpProtocolException(Canary, McpErrorCode.InvalidParams)
                : Task.FromResult<IReadOnlyList<AshlarMcpToolDescriptor>>(
                    [new(ToolName, ToolId, "read", Json.Object("{\"type\":\"object\"}"))]);

        public Task<AshlarMcpToolOutcome> InvokeAsync(string canonicalId, JsonElement arguments, CancellationToken ct)
            => throw new InvalidOperationException("A failing catalog must not invoke a tool.");
    }

    private sealed class AllowGate : IMcpInvocationGate
    {
        public McpGateDecision Authorize(ToolCall call, WorldSnapshot snapshot) => McpGateDecision.Allow();
    }

    private sealed class PrivateDataGate : IMcpInvocationGate
    {
        public McpGateDecision Authorize(ToolCall call, WorldSnapshot snapshot)
        {
            EgressSubject.Observe(Label("Secret"));
            return McpGateDecision.Deny(Canary);
        }
    }

    private sealed class ThrowingGate : IMcpInvocationGate
    {
        public McpGateDecision Authorize(ToolCall call, WorldSnapshot snapshot)
            => throw new McpProtocolException(Canary, McpErrorCode.InternalError);
    }

    private sealed class ThrowingSnapshotFactory : IMcpWorldSnapshotFactory
    {
        public WorldSnapshot Create(string canonicalToolId) => throw new InvalidOperationException(Canary);
    }

    private sealed class LabelledTool(object payload, string? reportedLabel, bool failAfterRead) : ITool, IEgressLabelledTool
    {
        public string Id => ToolId;
        public ToolSchema Schema { get; } = new(ToolId, "labelled", "{\"type\":\"object\"}");
        public Task<ToolResult> InvokeAsync(ToolCall call, WorldSnapshot snapshot, CancellationToken ct)
            => failAfterRead
                ? throw new InvalidOperationException(Canary)
                : Task.FromResult(new ToolResult(new FakeDelta(), payload));
        public void ReportRead(ReadReporter read, ToolResult result)
        {
            if (reportedLabel is not null)
                read.Report(Label(reportedLabel));
        }
    }

    private sealed class SnapshotFactory : IMcpWorldSnapshotFactory
    {
        public WorldSnapshot Create(string canonicalToolId) => WorldSnapshot.ForRepo("X:/repo");
    }
}
