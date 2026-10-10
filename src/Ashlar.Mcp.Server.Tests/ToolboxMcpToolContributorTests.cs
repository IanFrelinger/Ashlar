using Ashlar.Abstractions.Security.Egress;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Ashlar.Abstractions;
using Xunit;

namespace Ashlar.Mcp.Server.Tests;

public sealed class ToolboxMcpToolContributorTests
{
    private sealed class FixedSnapshotFactory : IMcpWorldSnapshotFactory
    {
        public WorldSnapshot Snapshot { get; } = WorldSnapshot.ForRepo("X:/repo");

        public WorldSnapshot Create(string canonicalToolId) => Snapshot;
    }

    private static ToolboxMcpToolContributor Create(
        IEnumerable<ITool> tools,
        AshlarMcpServerOptions? options = null,
        FixedSnapshotFactory? snapshots = null)
        => new(
            tools,
            Options.Create(options ?? new AshlarMcpServerOptions()),
            snapshots ?? new FixedSnapshotFactory(),
            NullLogger<ToolboxMcpToolContributor>.Instance);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Egress_refusal_crosses_the_Mcp_boundary_as_fixed_text_only(bool wrapped)
    {
        var refusal = new EgressRefusedException(new EgressGuard("full", "enforce").Evaluate(
            new EgressRequest(EgressFamilies.Http, "site-canary", new Uri("https://remote.example/private-canary"))));
        Exception error = wrapped ? new AggregateException(new IOException("unrelated-canary"),
            new HttpRequestException("wrapper-canary", refusal)) : refusal;
        var contributor = Create(new[] { new FakeTool("tool-canary", invoke: (_, _, _) => throw error) });
        var outcome = await contributor.InvokeAsync("tool-canary", Json.Object(), CancellationToken.None);
        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().Be($"egress refused by policy (ref {refusal.Ref})");
        outcome.Payload.Should().BeNull();
    }

    [Fact]
    public async Task Empty_allowlist_describes_zero_tools_even_when_tools_are_registered()
    {
        var contributor = Create(new[] { new FakeTool("repo.fs.read") });

        var descriptors = await contributor.DescribeAsync(CancellationToken.None);

        descriptors.Should().BeEmpty("exposure is deny-by-default");
    }

    [Fact]
    public async Task Allowlisted_tool_is_described_with_sanitized_name_and_canonical_id()
    {
        var options = new AshlarMcpServerOptions();
        options.ExposedToolIds.Add("repo.fs.read");

        var contributor = Create(new[] { new FakeTool("repo.fs.read", description: "reads files") }, options);

        var descriptors = await contributor.DescribeAsync(CancellationToken.None);

        var descriptor = descriptors.Should().ContainSingle().Subject;
        descriptor.McpName.Should().Be("repo_fs_read");
        descriptor.CanonicalId.Should().Be("repo.fs.read");
        descriptor.Description.Should().Be("reads files");
        descriptor.InputSchema.GetProperty("type").GetString().Should().Be("object");
    }

    [Fact]
    public async Task Allowlisted_tool_missing_from_di_fails_loudly()
    {
        var options = new AshlarMcpServerOptions();
        options.ExposedToolIds.Add("ghost.tool");

        var contributor = Create(Array.Empty<ITool>(), options);

        var act = () => contributor.DescribeAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ghost.tool*");
    }

    [Fact]
    public async Task Null_input_schema_defaults_to_empty_object_schema()
    {
        var options = new AshlarMcpServerOptions();
        options.ExposedToolIds.Add("schemaless");

        var contributor = Create(new[] { new FakeTool("schemaless", inputJsonSchema: null) }, options);

        var descriptors = await contributor.DescribeAsync(CancellationToken.None);

        descriptors.Single().InputSchema.GetProperty("type").GetString().Should().Be("object");
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("""{"type":"string"}""")]
    [InlineData("""["array","schema"]""")]
    public async Task Invalid_or_non_object_schema_fails_description(string schema)
    {
        var options = new AshlarMcpServerOptions();
        options.ExposedToolIds.Add("bad.schema");

        var contributor = Create(new[] { new FakeTool("bad.schema", inputJsonSchema: schema) }, options);

        var act = () => contributor.DescribeAsync(CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*bad.schema*");
    }

    [Fact]
    public async Task Invoke_passes_arguments_and_options_seeded_snapshot_to_the_tool()
    {
        var tool = new FakeTool("echo");
        var snapshots = new FixedSnapshotFactory();
        var contributor = Create(new[] { tool }, snapshots: snapshots);

        await contributor.InvokeAsync("echo", Json.Object("""{"value":42}"""), CancellationToken.None);

        tool.LastCall!.Id.Should().Be("echo");
        tool.LastCall.Arguments.GetProperty("value").GetInt32().Should().Be(42);
        tool.LastSnapshot.Should().BeSameAs(snapshots.Snapshot, "MCP callers never influence the world snapshot");
    }

    [Fact]
    public async Task String_payload_becomes_text_without_structured_payload()
    {
        var tool = new FakeTool("echo", invoke: (_, _, _) =>
            Task.FromResult(new ToolResult(new FakeDelta(), "hello world")));
        var contributor = Create(new[] { tool });

        var outcome = await contributor.InvokeAsync("echo", Json.Object(), CancellationToken.None);

        outcome.IsError.Should().BeFalse();
        outcome.Text.Should().Be("hello world");
        outcome.Payload.Should().BeNull();
    }

    [Fact]
    public async Task Object_payload_is_serialized_and_kept_as_structured_payload()
    {
        var payload = new { path = "a.txt", exists = true };
        var tool = new FakeTool("echo", invoke: (_, _, _) =>
            Task.FromResult(new ToolResult(new FakeDelta(), payload)));
        var contributor = Create(new[] { tool });

        var outcome = await contributor.InvokeAsync("echo", Json.Object(), CancellationToken.None);

        outcome.IsError.Should().BeFalse();
        outcome.Payload.Should().BeSameAs(payload);
        using var doc = JsonDocument.Parse(outcome.Text);
        doc.RootElement.GetProperty("exists").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Tool_exception_surfaces_as_error_outcome_not_exception()
    {
        var tool = new FakeTool("boom", invoke: (_, _, _) => throw new InvalidDataException("bad input shape"));
        var contributor = Create(new[] { tool });

        var outcome = await contributor.InvokeAsync("boom", Json.Object(), CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().Contain("boom").And.Contain("bad input shape");
    }

    [Fact]
    public async Task Cancellation_is_not_swallowed()
    {
        var tool = new FakeTool("slow", invoke: (_, _, ct) => throw new OperationCanceledException(ct));
        var contributor = Create(new[] { tool });

        var act = () => contributor.InvokeAsync("slow", Json.Object(), new CancellationToken(canceled: true));

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
