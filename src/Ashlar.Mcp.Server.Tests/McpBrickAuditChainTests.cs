using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ashlar.Abstractions.Barriers;
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
using Ashlar.Mcp.Bricks;
using Ashlar.Runtime;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using DomainBrick = Ashlar.Core.Domain.Bricks.Brick;

namespace Ashlar.Mcp.Server.Tests;

/// <summary>
/// The brick audit records of one server process form a hash chain, so a record edited, removed or reordered in the
/// audit file is detected by <see cref="McpBrickAuditChain.Verify"/>.
/// </summary>
public sealed class McpBrickAuditChainTests
{
    [Fact]
    public async Task Each_record_links_to_the_previous_ones_detail_and_carries_its_own_audit_id()
    {
        var audit = new CapturingAudit();
        var sut = Create(audit);

        await sut.InvokeAsync("test.echo", Args("{}"), CancellationToken.None);
        await sut.InvokeAsync("test.echo", Args("{}"), CancellationToken.None);

        var first = Detail(audit.Events[0]);
        var second = Detail(audit.Events[1]);
        first.GetProperty("seq").GetInt64().Should().Be(1);
        first.GetProperty("prevSha256").GetString().Should().Be(McpBrickAuditChain.Genesis);
        second.GetProperty("seq").GetInt64().Should().Be(2);
        second.GetProperty("prevSha256").GetString().Should().Be(Sha256(audit.Events[0].Detail!));
        second.GetProperty("session").GetString().Should().Be(first.GetProperty("session").GetString());
        first.GetProperty("auditId").GetString().Should().Be(audit.Events[0].CorrelationId);
    }

    [Fact]
    public async Task The_File_sink_trail_of_sequential_and_concurrent_calls_verifies_intact()
    {
        var lines = await FileTrailAsync(sequential: 5, concurrent: 40);

        var result = McpBrickAuditChain.Verify(lines);

        result.Problems.Should().BeEmpty();
        result.Intact.Should().BeTrue();
        result.Records.Should().Be(45);
        result.Sessions.Should().Be(1);
        result.MissingSequenceNumbers.Should().BeEmpty();
    }

    [Fact]
    public async Task An_edited_record_is_detected()
    {
        var lines = (await FileTrailAsync(sequential: 4, concurrent: 0)).ToList();
        lines[1] = EditDetail(lines[1], d => d["outcome"] = "refused");

        McpBrickAuditChain.Verify(lines).Intact.Should().BeFalse();
    }

    [Fact]
    public async Task A_removed_record_is_detected()
    {
        var lines = (await FileTrailAsync(sequential: 4, concurrent: 0)).ToList();
        lines.RemoveAt(1);

        var result = McpBrickAuditChain.Verify(lines);

        result.Intact.Should().BeFalse();
        result.Problems.Should().Contain(p => p.Contains("changed, removed or reordered"));
    }

    [Fact]
    public async Task Reordered_records_are_detected()
    {
        var lines = (await FileTrailAsync(sequential: 4, concurrent: 0)).ToList();
        (lines[1], lines[2]) = (lines[2], lines[1]);

        McpBrickAuditChain.Verify(lines).Intact.Should().BeFalse();
    }

    [Fact]
    public async Task Records_missing_from_the_start_of_a_session_are_detected()
    {
        var lines = (await FileTrailAsync(sequential: 3, concurrent: 0)).Skip(1).ToList();

        McpBrickAuditChain.Verify(lines).Problems.Should().ContainSingle().Which.Should().Contain("genesis");
    }

    [Fact]
    public async Task A_record_moved_to_another_calls_audit_id_is_detected()
    {
        var lines = (await FileTrailAsync(sequential: 2, concurrent: 0)).ToList();
        var line = JsonNode.Parse(lines[0])!;
        line["correlationId"] = "someone-else";
        lines[0] = line.ToJsonString();

        McpBrickAuditChain.Verify(lines).Problems.Should().Contain(p => p.Contains("correlationId"));
    }

    [Fact]
    public async Task A_record_the_server_could_not_write_is_a_gap_not_a_break()
    {
        var audit = new CapturingAudit { FailOnCall = 2 };
        var sut = Create(audit);

        for (var i = 0; i < 3; i++)
        {
            await sut.InvokeAsync("test.echo", Args("{}"), CancellationToken.None);
        }

        var result = McpBrickAuditChain.Verify(audit.Events.Select(AsFileLine));
        result.Intact.Should().BeTrue(string.Join("; ", result.Problems));
        result.Records.Should().Be(2);
        result.MissingSequenceNumbers.Should().Equal(2L);
    }

    private static async Task<IReadOnlyList<string>> FileTrailAsync(int sequential, int concurrent)
    {
        var dir = Path.Combine(Path.GetTempPath(), "ashlar-mcp-chain-tests", Guid.NewGuid().ToString("N"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{AshlarMcpBrickOptions.SectionPath}:ExposedBrickIds:0"] = "test.echo",
            ["Ashlar:Audit:Sinks:0"] = "File",
            ["Ashlar:Audit:File:Directory"] = dir,
            ["Ashlar:Audit:File:FlushEveryEvent"] = "true",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBrickRegistry>(new Registry(new EchoBrick()));
        services.AddBarrierAuditSinks(configuration);
        services.AddAshlarMcpBricks(configuration);
        await using (var provider = services.BuildServiceProvider())
        {
            var sut = provider.GetServices<IMcpToolContributor>().OfType<BrickMcpToolContributor>().Single();
            for (var i = 0; i < sequential; i++)
            {
                (await sut.InvokeAsync("test.echo", Args("{}"), CancellationToken.None)).IsError.Should().BeFalse();
            }

            await Task.WhenAll(Enumerable.Range(0, concurrent).Select(_ =>
                Task.Run(() => sut.InvokeAsync("test.echo", Args("{}"), CancellationToken.None))));
        }

        return Directory.GetFiles(dir, "*.ndjson").OrderBy(f => f, StringComparer.Ordinal).SelectMany(File.ReadAllLines).ToList();
    }

    private static string EditDetail(string line, Action<JsonObject> edit)
    {
        var node = JsonNode.Parse(line)!;
        var detail = JsonNode.Parse(node["detail"]!.GetValue<string>())!.AsObject();
        edit(detail);
        node["detail"] = detail.ToJsonString();
        return node.ToJsonString();
    }

    private static string AsFileLine(BarrierAuditEvent e) => JsonSerializer.Serialize(new
    {
        eventType = e.EventType,
        correlationId = e.CorrelationId,
        detail = e.Detail,
    });

    private static BrickMcpToolContributor Create(IBarrierAuditLog audit)
    {
        var options = new AshlarMcpBrickOptions();
        options.ExposedBrickIds.Add("test.echo");
        return new BrickMcpToolContributor(
            new Registry(new EchoBrick()), Options.Create(options), audit, NullLogger<BrickMcpToolContributor>.Instance);
    }

    private static JsonElement Args(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static JsonElement Detail(BarrierAuditEvent e) => JsonDocument.Parse(e.Detail!).RootElement;

    private static string Sha256(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private sealed class CapturingAudit : IBarrierAuditLog
    {
        private int _calls;

        public List<BarrierAuditEvent> Events { get; } = new();

        public int FailOnCall { get; init; }

        public ValueTask RecordAsync(BarrierAuditEvent auditEvent, CancellationToken cancellationToken = default)
        {
            if (++_calls == FailOnCall)
            {
                throw new IOException("disk full");
            }

            Events.Add(auditEvent);
            return ValueTask.CompletedTask;
        }
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
