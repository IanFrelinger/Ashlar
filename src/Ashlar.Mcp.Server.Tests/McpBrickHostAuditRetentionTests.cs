using System.Text.Json;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Mcp.Bricks;
using Ashlar.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using DomainBrick = Ashlar.Core.Domain.Bricks.Brick;

namespace Ashlar.Mcp.Server.Tests;

/// <summary>
/// The host's audit defaults keep every record. Each server start opens a new audit file, and the File sink used to
/// prune to its default of 10 files, so an IDE that starts the host once per session silently deleted the records of
/// the 11th-oldest session onwards.
/// </summary>
public sealed class McpBrickHostAuditRetentionTests
{
    [Fact]
    public void The_host_defaults_name_the_File_sink_flushed_per_event_and_never_pruned()
    {
        var defaults = AshlarMcpBrickHostDefaults.Audit("/home/u/.local/share");

        defaults["Ashlar:Audit:Sinks:0"].Should().Be("File");
        defaults["Ashlar:Audit:File:Directory"].Should().Be(Path.Combine("/home/u/.local/share", "ashlar", "mcp-audit"));
        defaults["Ashlar:Audit:File:FlushEveryEvent"].Should().Be("true");
        defaults["Ashlar:Audit:File:MaxRotatedFiles"].Should().Be("0");
    }

    [Fact]
    public async Task Under_the_host_defaults_the_first_sessions_record_survives_twelve_server_starts()
    {
        var root = Path.Combine(Path.GetTempPath(), "ashlar-mcp-retention-tests", Guid.NewGuid().ToString("N"));
        var defaults = AshlarMcpBrickHostDefaults.Audit(root);
        string? firstAuditId = null;

        for (var start = 0; start < 12; start++)
        {
            await using var provider = Compose(defaults);
            var sut = provider.GetServices<IMcpToolContributor>().OfType<BrickMcpToolContributor>().Single();
            var outcome = await sut.InvokeAsync("test.echo", JsonDocument.Parse("{}").RootElement, CancellationToken.None);
            outcome.IsError.Should().BeFalse(outcome.Text);
            firstAuditId ??= JsonDocument.Parse(outcome.Text).RootElement.GetProperty("auditId").GetString();
        }

        var dir = defaults["Ashlar:Audit:File:Directory"]!;
        var files = Directory.GetFiles(dir, "mcp-brick-calls-*.ndjson");
        files.Should().HaveCount(12);
        string.Concat(files.Select(File.ReadAllText)).Should().Contain(firstAuditId);
    }

    private static ServiceProvider Compose(IReadOnlyDictionary<string, string?> defaults)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(defaults)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"{AshlarMcpBrickOptions.SectionPath}:ExposedBrickIds:0"] = "test.echo",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBrickRegistry>(new Registry(new EchoBrick()));
        services.AddBarrierAuditSinks(configuration);
        services.AddAshlarMcpBricks(configuration);
        return services.BuildServiceProvider();
    }

    private sealed class Registry(params DomainBrick[] bricks) : IBrickRegistry
    {
        public DomainBrick? GetBrick(string id) => bricks.FirstOrDefault(b => b.Id == id);

        public IReadOnlyList<DomainBrick> GetAllBricks() => bricks;
    }

    private sealed class EchoBrick : DomainBrick
    {
        public EchoBrick()
        {
            Id = "test.echo";
            Name = "Echo";
            Version = "1.0.0";
            Icon = "E";
            Category = BrickCategory.Transform;
            Description = "Echoes.";
            Interface = new BrickInterface { Inputs = [], Outputs = [] };
        }

        public override Task<BrickOutput> ExecuteAsync(
            BrickInput input, ImplementationType implementation, IExecutionContext context, CancellationToken cancellationToken = default)
            => Task.FromResult(new BrickOutput { Summary = "echo" });
    }
}
