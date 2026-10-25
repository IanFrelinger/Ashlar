using Ashlar.Abstractions.Barriers;
using Ashlar.Runtime.Barriers.Sinks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Barriers.Sinks;

/// <summary>
/// <see cref="FileBarrierAuditSink.WriteConfirmedAsync"/>: done means on disk, and a record that did not land is an
/// exception, not a log line. The MCP brick host relies on this to fail closed when its audit trail cannot be written.
/// </summary>
public sealed class FileBarrierAuditSinkConfirmedWriteTests
{
    [Fact]
    public async Task A_confirmed_write_is_on_disk_when_it_returns_without_waiting_for_the_drain()
    {
        var dir = TempDirectory();
        await using var sink = Sink(dir, flushEveryEvent: false);

        await sink.WriteConfirmedAsync(Event(1));

        var lines = Lines(dir);
        lines.Should().ContainSingle().Which.Should().Contain("corr-1");
    }

    [Fact]
    public async Task A_confirmed_write_throws_when_the_directory_cannot_be_created()
    {
        var file = Path.Combine(TempDirectory(), "not-a-directory");
        await File.WriteAllTextAsync(file, "x");
        await using var sink = Sink(Path.Combine(file, "audit"), flushEveryEvent: true);

        var act = async () => await sink.WriteConfirmedAsync(Event(1));

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task The_unconfirmed_write_on_the_same_broken_directory_still_never_throws()
    {
        var file = Path.Combine(TempDirectory(), "not-a-directory");
        await File.WriteAllTextAsync(file, "x");
        await using var sink = Sink(Path.Combine(file, "audit"), flushEveryEvent: true);

        var act = async () => await sink.WriteAsync(Event(1));

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task A_confirmed_write_after_dispose_throws_instead_of_vanishing()
    {
        var sink = Sink(TempDirectory(), flushEveryEvent: true);
        await sink.DisposeAsync();

        var act = async () => await sink.WriteConfirmedAsync(Event(1));

        await act.Should().ThrowAsync<IOException>();
    }

    [Fact]
    public async Task Confirmed_and_unconfirmed_writes_keep_queue_order_in_the_file()
    {
        var dir = TempDirectory();
        await using var sink = Sink(dir, flushEveryEvent: false);

        await sink.WriteAsync(Event(1));
        await sink.WriteConfirmedAsync(Event(2));

        var lines = Lines(dir);
        lines.Should().HaveCount(2);
        lines[0].Should().Contain("corr-1");
        lines[1].Should().Contain("corr-2");
    }

    private static List<string> Lines(string directory)
    {
        var lines = new List<string>();
        foreach (var path in Directory.GetFiles(directory, "*.ndjson").OrderBy(p => p, StringComparer.Ordinal))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    private static FileBarrierAuditSink Sink(string directory, bool flushEveryEvent) =>
        new(new FileBarrierAuditSinkOptions
        {
            Directory = directory,
            FlushEveryEvent = flushEveryEvent,
            FlushIntervalMs = 3_600_000,
        }, NullLogger<FileBarrierAuditSink>.Instance);

    private static BarrierAuditEvent Event(int index) => new(
        EventType: BarrierAuditEventType.AgentInvoked,
        BarrierLevel: "internal",
        AuthoritySource: BarrierAuthoritySource.Cli,
        AgentName: "agent",
        CorrelationId: $"corr-{index}",
        SpanId: $"span-{index}",
        OccurredAt: DateTimeOffset.UtcNow,
        Detail: null);

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-audit-confirmed-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
