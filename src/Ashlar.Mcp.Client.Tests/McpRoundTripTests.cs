using System.IO.Pipelines;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Ashlar.Abstractions;
using Ashlar.Mcp.Server;
using Xunit;

namespace Ashlar.Mcp.Client.Tests;

/// <summary>
/// Full protocol round trips: a real <c>McpClient</c> talking to the real Ashlar MCP server bridge
/// over in-memory pipes — no sockets, no mocked protocol. This is the compatibility proof for
/// both adapter directions at once.
/// </summary>
[Collection(DeploymentProfileEnvironmentCollection.Name)]
public sealed class McpRoundTripTests
{
    private sealed class EchoDelta : IActionDelta
    {
        public int TickFrom => 0;
        public int TickTo => 1;
        public IReadOnlyList<byte>? Signature { get; set; }
        public IReadOnlyList<string> Log { get; } = new[] { "echo" };
    }

    private sealed class EchoTool : ITool
    {
        public string Id => "echo.tool";

        public ToolSchema Schema => new(
            Id,
            "Echoes the supplied value back.",
            """{"type":"object","required":["value"],"properties":{"value":{"type":"string"}}}""");

        public Task<ToolResult> InvokeAsync(ToolCall toolCall, WorldSnapshot s, CancellationToken ct)
        {
            var value = toolCall.Arguments.GetProperty("value").GetString();
            return Task.FromResult(new ToolResult(new EchoDelta(), new { echoed = value }));
        }
    }

    private sealed class ServerHarness : IAsyncDisposable
    {
        private readonly ServiceProvider _provider;
        public Pipe ClientToServer { get; } = new();
        public Pipe ServerToClient { get; } = new();

        public ServerHarness()
        {
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AshlarMcpServerOptions.SectionPath}:Enabled"] = "true",
                [$"{AshlarMcpServerOptions.SectionPath}:ServerName"] = "ashlar-test",
                [$"{AshlarMcpServerOptions.SectionPath}:ExposedToolIds:0"] = "echo.tool",
            }).Build();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<ITool>(new EchoTool());
            services.AddAshlarMcpServer(configuration)
                .WithStreamServerTransport(ClientToServer.Reader.AsStream(), ServerToClient.Writer.AsStream());
            _provider = services.BuildServiceProvider();
        }

        public async Task StartAsync(CancellationToken ct)
        {
            foreach (var hosted in _provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(ct);
            }
        }

        public async Task<McpClient> ConnectClientAsync(CancellationToken ct)
        {
            var transport = new StreamClientTransport(
                serverInput: ClientToServer.Writer.AsStream(),
                serverOutput: ServerToClient.Reader.AsStream(),
                NullLoggerFactory.Instance);
            return await McpClient.CreateAsync(transport, clientOptions: null, loggerFactory: NullLoggerFactory.Instance, cancellationToken: ct);
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var hosted in _provider.GetServices<IHostedService>().Reverse())
            {
                try
                {
                    await hosted.StopAsync(CancellationToken.None);
                }
                catch
                {
                    // Best-effort shutdown of the in-memory session.
                }
            }

            await _provider.DisposeAsync();
        }
    }

    private static CancellationToken TestTimeout() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Fact(Timeout = 60000)]
    public async Task Client_lists_the_allowlisted_tool_with_sanitized_name_and_pinned_schema()
    {
        var ct = TestTimeout();
        await using var harness = new ServerHarness();
        await harness.StartAsync(ct);
        await using var client = await harness.ConnectClientAsync(ct);

        var tools = await client.ListToolsAsync((ModelContextProtocol.RequestOptions?)null, ct);

        var tool = tools.Should().ContainSingle().Subject;
        tool.Name.Should().Be("echo_tool", "dots are outside the MCP-strict name charset");
        tool.JsonSchema.GetProperty("type").GetString().Should().Be("object");
        tool.JsonSchema.GetProperty("required")[0].GetString().Should().Be("value");
    }

    [Fact(Timeout = 60000)]
    public async Task Client_calls_the_tool_and_receives_text_and_structured_content()
    {
        var ct = TestTimeout();
        await using var harness = new ServerHarness();
        await harness.StartAsync(ct);
        await using var client = await harness.ConnectClientAsync(ct);

        var result = await client.CallToolAsync(
            "echo_tool",
            new Dictionary<string, object?> { ["value"] = "ping" },
            cancellationToken: ct);

        result.IsError.Should().NotBe(true);
        result.Content.OfType<TextContentBlock>().Single().Text.Should().Contain("ping");
        result.StructuredContent.HasValue.Should().BeTrue();
        result.StructuredContent!.Value.GetProperty("echoed").GetString().Should().Be("ping");
    }

    [Fact(Timeout = 60000)]
    public async Task Unknown_tool_surfaces_as_a_protocol_error_on_the_client()
    {
        var ct = TestTimeout();
        await using var harness = new ServerHarness();
        await harness.StartAsync(ct);
        await using var client = await harness.ConnectClientAsync(ct);

        var act = () => client.CallToolAsync("no_such_tool", cancellationToken: ct).AsTask();

        (await act.Should().ThrowAsync<McpException>()).Which.Message.Should().Contain("no_such_tool");
    }

    [Fact(Timeout = 60000)]
    public async Task Connection_manager_pins_remote_tools_and_proxies_calls_end_to_end()
    {
        var ct = TestTimeout();
        await using var harness = new ServerHarness();
        await harness.StartAsync(ct);

        var options = new AshlarMcpClientOptions { Enabled = true };
        options.Servers.Add(new McpServerEndpointOptions
        {
            Name = "test",
            // Never dialed — the factory override below hands back the in-memory client.
            Url = "https://in-memory.invalid/mcp",
        });

        await using var manager = new McpClientConnectionManager(
            Options.Create(options),
            NullLoggerFactory.Instance,
            NullLogger<McpClientConnectionManager>.Instance)
        {
            ClientFactoryOverride = (_, token) => harness.ConnectClientAsync(token),
        };
        await manager.StartAsync(ct);

        var tool = manager.GetTools().Should().ContainSingle().Subject;
        tool.Id.Should().Be("mcp:test:echo_tool", "remote tools are namespaced against id shadowing");
        tool.Schema.InputJsonSchema.Should().Contain("\"value\"");

        using var argsDoc = JsonDocument.Parse("""{"value":"pong"}""");
        var result = await tool.InvokeAsync(
            new ToolCall(tool.Id, argsDoc.RootElement),
            WorldSnapshot.ForRepo("X:/repo"),
            ct);

        result.Payload.Should().BeOfType<JsonElement>()
            .Which.GetProperty("echoed").GetString().Should().Be("pong");
    }
}
