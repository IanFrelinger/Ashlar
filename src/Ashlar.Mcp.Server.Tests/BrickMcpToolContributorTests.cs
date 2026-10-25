using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ashlar.Abstractions.Barriers;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Mcp.Bricks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;
using DomainBrick = Ashlar.Core.Domain.Bricks.Brick;

namespace Ashlar.Mcp.Server.Tests;

public sealed class BrickMcpToolContributorTests
{
    private sealed class GreetBrick : DomainBrick
    {
        public GreetBrick()
        {
            Id = "test.greet";
            Name = "Greet";
            Version = "2.1.0";
            Icon = "G";
            Category = BrickCategory.Transform;
            Description = "Greets someone.";
            Interface = new BrickInterface
            {
                Inputs =
                [
                    new BrickInputDefinition("name", "string", "Who to greet"),
                    new BrickInputDefinition("times", "int", "Repeat count", required: true, defaultValue: 1L),
                    new BrickInputDefinition("shout", "boolean", "Upper-case", required: false),
                    new BrickInputDefinition("tags", "string[]", "Tags", required: false),
                    new BrickInputDefinition("extra", "whatever", null, required: false),
                ],
                Outputs = [new BrickOutputDefinition("greeting", "string", "The greeting")],
            };
        }

        public int Executions { get; private set; }

        public IExecutionContext? LastContext { get; private set; }

        public ImplementationType? LastImplementation { get; private set; }

        public Exception? Throw { get; init; }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
        {
            Executions++;
            LastContext = context;
            LastImplementation = implementation;
            if (Throw is not null)
            {
                throw Throw;
            }

            var times = input.Get<long>("times");
            var output = new BrickOutput { Summary = $"greeted {input.Get<string>("name")}" };
            output.Set("greeting", string.Concat(Enumerable.Repeat("hi " + input.Get<string>("name"), (int)times)));
            return Task.FromResult(output);
        }
    }

    private sealed class Registry(params DomainBrick[] bricks) : IBrickRegistry
    {
        public DomainBrick? GetBrick(string id) => bricks.FirstOrDefault(b => b.Id == id);

        public IReadOnlyList<DomainBrick> GetAllBricks() => bricks;
    }

    private sealed class CapturingAudit : IBarrierAuditLog
    {
        public List<BarrierAuditEvent> Events { get; } = new();

        public Exception? Throw { get; init; }

        public ValueTask RecordAsync(BarrierAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (Throw is not null)
            {
                throw Throw;
            }

            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CapturingSink : IBarrierAuditSink
    {
        public List<BarrierAuditEvent> Events { get; } = new();

        public ValueTask WriteAsync(BarrierAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DiscardingAuditLog : IBarrierAuditLog
    {
        public ValueTask RecordAsync(BarrierAuditEvent auditEvent, CancellationToken cancellationToken = default)
            => ValueTask.CompletedTask;
    }

    private static BrickMcpToolContributor Create(
        IBrickRegistry registry, IBarrierAuditLog audit, params string[] exposed)
    {
        var options = new AshlarMcpBrickOptions();
        foreach (var id in exposed)
        {
            options.ExposedBrickIds.Add(id);
        }

        return new BrickMcpToolContributor(
            registry, Options.Create(options), audit, NullLogger<BrickMcpToolContributor>.Instance);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static JsonElement Detail(BarrierAuditEvent e) => JsonDocument.Parse(e.Detail!).RootElement;

    [Fact]
    public async Task Describe_lists_only_allowlisted_bricks_with_a_schema_built_from_the_interface()
    {
        var sut = Create(new Registry(new GreetBrick(), new OtherBrick()), new CapturingAudit(), "test.greet");

        var descriptors = await sut.DescribeAsync(CancellationToken.None);

        descriptors.Should().ContainSingle();
        var d = descriptors[0];
        d.McpName.Should().Be("test_greet");
        d.CanonicalId.Should().Be("test.greet");
        d.Description.Should().Be("Greets someone.");
        var schema = d.InputSchema;
        schema.GetProperty("type").GetString().Should().Be("object");
        var props = schema.GetProperty("properties");
        props.GetProperty("name").GetProperty("type").GetString().Should().Be("string");
        props.GetProperty("name").GetProperty("description").GetString().Should().Be("Who to greet");
        props.GetProperty("times").GetProperty("type").GetString().Should().Be("integer");
        props.GetProperty("shout").GetProperty("type").GetString().Should().Be("boolean");
        props.GetProperty("tags").GetProperty("type").GetString().Should().Be("array");
        props.GetProperty("extra").TryGetProperty("type", out _).Should().BeFalse("an unknown brick type must not be guessed");
        schema.GetProperty("required").EnumerateArray().Select(e => e.GetString())
            .Should().Equal(new[] { "name" }, "a required input with a default is satisfied by the default");
    }

    [Fact]
    public async Task Describe_fails_loudly_for_an_allowlisted_id_with_no_registered_brick()
    {
        var sut = Create(new Registry(new GreetBrick()), new CapturingAudit(), "test.missing");

        var act = () => sut.DescribeAsync(CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidOperationException>()).WithMessage("*test.missing*IBrickRegistry*");
    }

    [Fact]
    public async Task Invoke_executes_deterministically_returns_outputs_and_writes_one_matching_audit_record()
    {
        var brick = new GreetBrick();
        var audit = new CapturingAudit();
        var sut = Create(new Registry(brick), audit, "test.greet");
        const string raw = """{"name":"ada","times":2}""";

        var outcome = await sut.InvokeAsync("test.greet", Args(raw), CancellationToken.None);

        outcome.IsError.Should().BeFalse(outcome.Text);
        brick.Executions.Should().Be(1);
        brick.LastImplementation.Should().Be(ImplementationType.Deterministic);
        brick.LastContext!.AgentId.Should().Be(BrickMcpToolContributor.McpAgentId);
        brick.LastContext.BehaviorId.Should().Be("test.greet");
        brick.LastContext.AuditMode.Should().BeTrue();

        using var payload = JsonDocument.Parse(outcome.Text);
        payload.RootElement.GetProperty("summary").GetString().Should().Be("greeted ada");
        payload.RootElement.GetProperty("outputs").GetProperty("greeting").GetString().Should().Be("hi adahi ada");
        payload.RootElement.GetProperty("brickVersion").GetString().Should().Be("2.1.0");
        var auditId = payload.RootElement.GetProperty("auditId").GetString();

        audit.Events.Should().ContainSingle();
        var e = audit.Events[0];
        e.EventType.Should().Be(BrickMcpToolContributor.EventType);
        e.AgentName.Should().Be("test.greet");
        e.CorrelationId.Should().Be(auditId);
        var detail = Detail(e);
        detail.GetProperty("outcome").GetString().Should().Be("succeeded");
        detail.GetProperty("isError").GetBoolean().Should().BeFalse();
        detail.GetProperty("brickVersion").GetString().Should().Be("2.1.0");
        detail.GetProperty("inputSha256").GetString().Should().Be(Sha256(raw));
        detail.GetProperty("resultSha256").GetString().Should().Be(Sha256(outcome.Text));
        e.Detail.Should().NotContain("ada", "argument and output values stay out of the record");
    }

    [Fact]
    public async Task Invoke_applies_input_defaults_before_the_brick_runs()
    {
        var sut = Create(new Registry(new GreetBrick()), new CapturingAudit(), "test.greet");

        var outcome = await sut.InvokeAsync("test.greet", Args("""{"name":"bo"}"""), CancellationToken.None);

        outcome.IsError.Should().BeFalse(outcome.Text);
        JsonDocument.Parse(outcome.Text).RootElement.GetProperty("outputs").GetProperty("greeting").GetString()
            .Should().Be("hi bo");
    }

    [Fact]
    public async Task Invoke_refuses_missing_required_inputs_without_running_the_brick_and_audits_the_refusal()
    {
        var brick = new GreetBrick();
        var audit = new CapturingAudit();
        var sut = Create(new Registry(brick), audit, "test.greet");

        var outcome = await sut.InvokeAsync("test.greet", Args("""{"times":3}"""), CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().Contain("Missing required input(s): name");
        brick.Executions.Should().Be(0);
        audit.Events.Should().ContainSingle();
        Detail(audit.Events[0]).GetProperty("outcome").GetString().Should().Be("refused");
        outcome.Text.Should().Contain(audit.Events[0].CorrelationId);
    }

    [Fact]
    public async Task Invoke_refuses_a_brick_that_is_registered_but_not_allowlisted()
    {
        var other = new OtherBrick();
        var audit = new CapturingAudit();
        var sut = Create(new Registry(new GreetBrick(), other), audit, "test.greet");

        var outcome = await sut.InvokeAsync("test.other", Args("{}"), CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        other.Executions.Should().Be(0);
        Detail(audit.Events.Single()).GetProperty("outcome").GetString().Should().Be("refused");
    }

    [Fact]
    public async Task Invoke_refuses_non_object_arguments()
    {
        var brick = new GreetBrick();
        var sut = Create(new Registry(brick), new CapturingAudit(), "test.greet");

        var outcome = await sut.InvokeAsync("test.greet", Args("[1,2]"), CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().Contain("JSON object");
        brick.Executions.Should().Be(0);
    }

    [Fact]
    public async Task Invoke_reports_a_throwing_brick_as_a_tool_error_and_audits_it_as_failed()
    {
        var audit = new CapturingAudit();
        var sut = Create(new Registry(new GreetBrick { Throw = new InvalidOperationException("kaboom") }), audit, "test.greet");

        var outcome = await sut.InvokeAsync("test.greet", Args("""{"name":"x"}"""), CancellationToken.None);

        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().Contain("kaboom");
        var detail = Detail(audit.Events.Single());
        detail.GetProperty("outcome").GetString().Should().Be("failed");
        detail.GetProperty("isError").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Invoke_does_not_claim_success_when_the_audit_record_cannot_be_written()
    {
        var brick = new GreetBrick();
        var sut = Create(new Registry(brick), new CapturingAudit { Throw = new IOException("disk full") }, "test.greet");

        var outcome = await sut.InvokeAsync("test.greet", Args("""{"name":"x"}"""), CancellationToken.None);

        brick.Executions.Should().Be(1);
        outcome.IsError.Should().BeTrue();
        outcome.Text.Should().Contain("audit record could not be written");
    }

    [Fact(Timeout = 60000)]
    public async Task A_real_MCP_client_lists_and_calls_an_allowlisted_brick_through_the_bridge()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ct = cts.Token;
        var sink = new CapturingSink();
        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AshlarMcpServerOptions.SectionPath}:Enabled"] = "true",
            [$"{AshlarMcpBrickOptions.SectionPath}:ExposedBrickIds:0"] = "test.greet",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBrickRegistry>(new Registry(new GreetBrick(), new OtherBrick()));
        // What AddAshlar() registers: a log that discards. Records must still reach the sinks.
        services.AddSingleton<IBarrierAuditLog>(new DiscardingAuditLog());
        services.AddSingleton<IBarrierAuditSink>(sink);
        services.AddAshlarMcpBricks(configuration);
        services.AddAshlarMcpServer(configuration)
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream());
        await using var provider = services.BuildServiceProvider();
        var hosted = provider.GetServices<IHostedService>().ToList();
        foreach (var h in hosted)
        {
            await h.StartAsync(ct);
        }

        try
        {
            var transport = new StreamClientTransport(
                clientToServer.Writer.AsStream(), serverToClient.Reader.AsStream(), NullLoggerFactory.Instance);
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);

            var tools = await client.ListToolsAsync(cancellationToken: ct);
            tools.Select(t => t.Name).Should().Equal("test_greet");

            var result = await client.CallToolAsync(
                "test_greet", new Dictionary<string, object?> { ["name"] = "mcp" }, cancellationToken: ct);

            result.IsError.Should().NotBe(true);
            var text = result.Content.OfType<TextContentBlock>().Single().Text;
            using var payload = JsonDocument.Parse(text);
            payload.RootElement.GetProperty("outputs").GetProperty("greeting").GetString().Should().Be("hi mcp");
            result.StructuredContent.Should().NotBeNull();
            sink.Events.Should().ContainSingle()
                .Which.CorrelationId.Should().Be(payload.RootElement.GetProperty("auditId").GetString());
        }
        finally
        {
            foreach (var h in Enumerable.Reverse(hosted))
            {
                try
                {
                    await h.StopAsync(CancellationToken.None);
                }
                catch
                {
                    // Best-effort shutdown of the in-memory session.
                }
            }
        }
    }

    private sealed class OtherBrick : DomainBrick
    {
        public OtherBrick()
        {
            Id = "test.other";
            Name = "Other";
            Version = "1.0.0";
            Icon = "O";
            Category = BrickCategory.Transform;
            Description = "Not allowlisted.";
            Interface = new BrickInterface { Inputs = [], Outputs = [] };
        }

        public int Executions { get; private set; }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
        {
            Executions++;
            return Task.FromResult(new BrickOutput());
        }
    }
}
