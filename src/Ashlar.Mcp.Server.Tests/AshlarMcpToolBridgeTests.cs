using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using Ashlar.Abstractions;
using Xunit;

namespace Ashlar.Mcp.Server.Tests;

public sealed class AshlarMcpToolBridgeTests
{
    private sealed class FakeContributor : IMcpToolContributor
    {
        private readonly List<AshlarMcpToolDescriptor> _descriptors = new();
        private readonly Func<string, JsonElement, Task<AshlarMcpToolOutcome>> _invoke;

        public FakeContributor(Func<string, JsonElement, Task<AshlarMcpToolOutcome>>? invoke = null)
            => _invoke = invoke ?? ((id, _) => Task.FromResult(new AshlarMcpToolOutcome(false, $"ran {id}")));

        public JsonElement? LastArguments { get; private set; }

        public FakeContributor Offering(string mcpName, string canonicalId, string description = "d")
        {
            _descriptors.Add(new AshlarMcpToolDescriptor(mcpName, canonicalId, description, Json.Object("""{"type":"object"}""")));
            return this;
        }

        public Task<IReadOnlyList<AshlarMcpToolDescriptor>> DescribeAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<AshlarMcpToolDescriptor>>(_descriptors);

        public Task<AshlarMcpToolOutcome> InvokeAsync(string canonicalId, JsonElement arguments, CancellationToken ct)
        {
            LastArguments = arguments;
            return _invoke(canonicalId, arguments);
        }
    }

    private sealed class RecordingGate : IMcpInvocationGate
    {
        private readonly McpGateDecision _decision;

        public RecordingGate(McpGateDecision? decision = null) => _decision = decision ?? McpGateDecision.Allow();

        public ToolCall? LastCall { get; private set; }

        public McpGateDecision Authorize(ToolCall call, WorldSnapshot snapshot)
        {
            LastCall = call;
            return _decision;
        }
    }

    private sealed class FixedSnapshotFactory : IMcpWorldSnapshotFactory
    {
        public WorldSnapshot Create(string canonicalToolId) => WorldSnapshot.ForRepo("X:/repo");
    }

    private sealed class RecordingAuditor : IMcpRefusalAuditor
    {
        public List<(string Name, string? CanonicalId, string Arguments, string Message)> Refusals { get; } = new();

        public Exception? Throw { get; init; }

        public Task<string> RecordRefusalAsync(string requestedName, string? canonicalId, JsonElement arguments, string message, CancellationToken ct)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Refusals.Add((requestedName, canonicalId, arguments.GetRawText(), message));
            return Task.FromResult($"{message} (auditId a{Refusals.Count})");
        }
    }

    private static AshlarMcpToolBridge Create(
        IEnumerable<IMcpToolContributor> contributors,
        IMcpInvocationGate? gate = null,
        AshlarMcpServerOptions? options = null,
        IMcpRefusalAuditor? auditor = null)
        => new(
            contributors,
            gate ?? new RecordingGate(),
            new FixedSnapshotFactory(),
            Options.Create(options ?? new AshlarMcpServerOptions()),
            NullLogger<AshlarMcpToolBridge>.Instance,
            auditor is null ? null : [auditor]);

    private static CallToolRequestParams Params(string name, Dictionary<string, JsonElement>? args = null)
        => new() { Name = name, Arguments = args };

    [Fact]
    public async Task ListTools_projects_descriptors_onto_protocol_tools()
    {
        var bridge = Create(new[] { new FakeContributor().Offering("repo_fs_read", "repo.fs.read", "reads") });

        var result = await bridge.ListToolsAsync(CancellationToken.None);

        var tool = result.Tools.Should().ContainSingle().Subject;
        tool.Name.Should().Be("repo_fs_read");
        tool.Title.Should().Be("repo.fs.read", "sanitized names surface the canonical id as display title");
        tool.Description.Should().Be("reads");
    }

    [Fact]
    public async Task ListTools_title_is_omitted_when_name_is_unchanged()
    {
        var bridge = Create(new[] { new FakeContributor().Offering("echo", "echo") });

        var result = await bridge.ListToolsAsync(CancellationToken.None);

        result.Tools.Single().Title.Should().BeNull();
    }

    [Fact]
    public async Task Name_collisions_across_contributors_fail_validation()
    {
        var bridge = Create(new IMcpToolContributor[]
        {
            new FakeContributor().Offering("repo_fs_read", "repo.fs.read"),
            new FakeContributor().Offering("repo_fs_read", "repo.fs_read"),
        });

        var act = () => bridge.ValidateCatalogAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*repo_fs_read*", "ambiguous catalogs must not boot");
    }

    [Fact]
    public async Task Unknown_tool_is_a_protocol_error()
    {
        var bridge = Create(new[] { new FakeContributor().Offering("known", "known") });

        var act = () => bridge.CallToolAsync(Params("unknown"), user: null, CancellationToken.None).AsTask();

        (await act.Should().ThrowAsync<McpProtocolException>())
            .Which.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
    }

    [Fact]
    public async Task An_unknown_tool_is_recorded_by_the_refusal_auditor_and_the_error_carries_its_id()
    {
        var auditor = new RecordingAuditor();
        var bridge = Create(new[] { new FakeContributor().Offering("known", "known") }, auditor: auditor);

        var act = () => bridge.CallToolAsync(
            Params("github_pr-status", new() { ["url"] = Json.Object("\"x\"") }), user: null, CancellationToken.None).AsTask();

        var error = (await act.Should().ThrowAsync<McpProtocolException>()).Which;
        error.ErrorCode.Should().Be(McpErrorCode.InvalidParams);
        error.Message.Should().Contain("(auditId a1)");
        auditor.Refusals.Should().ContainSingle().Which.Should().Be(
            ("github_pr-status", (string?)null, """{"url":"x"}""", "Unknown tool 'github_pr-status'."));
    }

    [Fact]
    public async Task A_gate_denial_is_recorded_by_the_refusal_auditor_with_the_canonical_id()
    {
        var auditor = new RecordingAuditor();
        var gate = new RecordingGate(McpGateDecision.Deny("policy said no"));
        var bridge = Create(new[] { new FakeContributor().Offering("repo_fs_read", "repo.fs.read") }, gate, auditor: auditor);

        var result = await bridge.CallToolAsync(Params("repo_fs_read"), user: null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("policy said no").And.Contain("(auditId a1)");
        auditor.Refusals.Should().ContainSingle().Which.CanonicalId.Should().Be("repo.fs.read");
    }

    [Fact]
    public async Task A_refusal_whose_record_fails_is_still_refused_and_says_it_is_unaudited()
    {
        var auditor = new RecordingAuditor { Throw = new IOException("disk full") };
        var bridge = Create(new[] { new FakeContributor().Offering("known", "known") }, auditor: auditor);

        var act = () => bridge.CallToolAsync(Params("nope"), user: null, CancellationToken.None).AsTask();

        (await act.Should().ThrowAsync<McpProtocolException>()).Which.Message.Should().Contain("could not be written");
    }

    [Fact]
    public async Task An_oversized_unknown_name_is_cut_in_the_error_but_reaches_the_auditor_whole()
    {
        var auditor = new RecordingAuditor();
        var bridge = Create(new[] { new FakeContributor().Offering("known", "known") }, auditor: auditor);
        var name = "x\n" + new string('a', 100_000);

        var act = () => bridge.CallToolAsync(Params(name), user: null, CancellationToken.None).AsTask();

        var error = (await act.Should().ThrowAsync<McpProtocolException>()).Which;
        error.Message.Length.Should().BeLessThan(200);
        error.Message.Should().NotContain("\n");
        auditor.Refusals.Single().Name.Should().Be(name);
    }

    [Fact]
    public async Task Gate_denial_returns_isError_result_not_protocol_error()
    {
        var gate = new RecordingGate(McpGateDecision.Deny("policy said no"));
        var bridge = Create(new[] { new FakeContributor().Offering("echo", "echo") }, gate);

        var result = await bridge.CallToolAsync(Params("echo"), user: null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().Contain("policy said no");
    }

    [Fact]
    public async Task Gate_sees_the_canonical_tool_id_not_the_sanitized_name()
    {
        var gate = new RecordingGate();
        var bridge = Create(new[] { new FakeContributor().Offering("repo_fs_read", "repo.fs.read") }, gate);

        await bridge.CallToolAsync(Params("repo_fs_read"), user: null, CancellationToken.None);

        gate.LastCall!.Id.Should().Be("repo.fs.read", "policies are written against canonical Ashlar ids");
    }

    [Fact]
    public async Task Success_with_structured_payload_sets_structured_content()
    {
        var contributor = new FakeContributor((_, _) => Task.FromResult(
            new AshlarMcpToolOutcome(false, """{"answer":42}""", new { answer = 42 })))
            .Offering("echo", "echo");
        var bridge = Create(new[] { contributor });

        var result = await bridge.CallToolAsync(Params("echo"), user: null, CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("42");
        result.StructuredContent.HasValue.Should().BeTrue();
        result.StructuredContent!.Value.GetProperty("answer").GetInt32().Should().Be(42);
    }

    [Fact]
    public async Task Success_with_plain_text_has_no_structured_content()
    {
        var contributor = new FakeContributor((_, _) => Task.FromResult(
            new AshlarMcpToolOutcome(false, "plain text"))).Offering("echo", "echo");
        var bridge = Create(new[] { contributor });

        var result = await bridge.CallToolAsync(Params("echo"), user: null, CancellationToken.None);

        result.StructuredContent.HasValue.Should().BeFalse();
    }

    [Fact]
    public async Task Contributor_crash_returns_generic_isError_result()
    {
        var contributor = new FakeContributor((_, _) => throw new InvalidOperationException("secret internals"))
            .Offering("echo", "echo");
        var bridge = Create(new[] { contributor });

        var result = await bridge.CallToolAsync(Params("echo"), user: null, CancellationToken.None);

        result.IsError.Should().BeTrue();
        var text = result.Content.OfType<TextContentBlock>().Single().Text;
        text.Should().NotContain("secret internals", "internal details stay in the host log");
    }

    [Fact]
    public async Task Argument_overrides_replace_caller_supplied_values()
    {
        var options = new AshlarMcpServerOptions();
        options.ArgumentOverrides["repo.fs.read"] = new Dictionary<string, string> { ["root"] = "X:/pinned" };
        var contributor = new FakeContributor().Offering("repo_fs_read", "repo.fs.read");
        var bridge = Create(new[] { contributor }, options: options);

        var args = new Dictionary<string, JsonElement>
        {
            ["root"] = Json.Object("""{"ignored":true}"""),
            ["path"] = JsonSerializer.SerializeToElement("readme.md"),
        };
        await bridge.CallToolAsync(Params("repo_fs_read", args), user: null, CancellationToken.None);

        var seen = contributor.LastArguments!.Value;
        seen.GetProperty("root").GetString().Should().Be("X:/pinned", "operator pins beat caller-supplied values");
        seen.GetProperty("path").GetString().Should().Be("readme.md", "untouched arguments pass through");
    }

    [Fact]
    public async Task Null_arguments_become_empty_object_and_still_receive_overrides()
    {
        var options = new AshlarMcpServerOptions();
        options.ArgumentOverrides["echo"] = new Dictionary<string, string> { ["root"] = "X:/pinned" };
        var contributor = new FakeContributor().Offering("echo", "echo");
        var bridge = Create(new[] { contributor }, options: options);

        await bridge.CallToolAsync(Params("echo"), user: null, CancellationToken.None);

        contributor.LastArguments!.Value.GetProperty("root").GetString().Should().Be("X:/pinned");
    }
}
