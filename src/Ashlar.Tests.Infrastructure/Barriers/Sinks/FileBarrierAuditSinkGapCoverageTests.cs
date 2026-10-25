using System.Reflection;
using FluentAssertions;
using Ashlar.Abstractions.Barriers;
using Ashlar.Runtime.Barriers.Sinks;
using Xunit;

namespace Ashlar.Tests.Infrastructure.Barriers.Sinks;

/// <summary>Tests for file barrier audit sink gap coverage.</summary>
[Collection("ProcessCwd")] // one test sets the process cwd — see Helpers/ProcessCwdCollection
public sealed class FileBarrierAuditSinkGapCoverageTests
{
    [Fact]
    public void Constructor_rejects_null_dependencies()
    {
        var logger = new TestLogger<FileBarrierAuditSink>();
        var options = new FileBarrierAuditSinkOptions { Directory = CreateTempDirectory() };

        var actOptions = () => new FileBarrierAuditSink(null!, logger);
        var actLogger = () => new FileBarrierAuditSink(options, null!);

        actOptions.Should().Throw<ArgumentNullException>();
        actLogger.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public async Task DisposeAsync_can_be_called_multiple_times()
    {
        var testDir = CreateTempDirectory();
        var sink = CreateSink(testDir, new TestLogger<FileBarrierAuditSink>());

        await sink.DisposeAsync();
        var act = async () => await sink.DisposeAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task WriteAsync_normalizes_blank_directory_and_prefix()
    {
        var testDir = CreateTempDirectory();
        await using var sink = new FileBarrierAuditSink(
            new FileBarrierAuditSinkOptions
            {
                Directory = "   ",
                FilePrefix = "  ",
                FlushEveryEvent = true,
            },
            new TestLogger<FileBarrierAuditSink>());

        var originalDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(testDir);
            await sink.WriteAsync(new BarrierAuditEvent(
                EventType: BarrierAuditEventType.ContextInitialized,
                BarrierLevel: "public",
                AuthoritySource: BarrierAuthoritySource.Header,
                AgentName: string.Empty,
                CorrelationId: "corr-normalize",
                SpanId: string.Empty,
                OccurredAt: DateTimeOffset.UtcNow,
                Detail: "normalized"));

            // The sink appends on a background drain task; wait for the write to land rather
            // than for a fixed interval that a loaded runner under coverlet can overshoot.
            var auditDir = Path.Combine(testDir, "audit");
            await WaitUntilAsync(() => GetAuditFiles(auditDir, "audit-barriers").Length > 0);
            Directory.Exists(auditDir).Should().BeTrue();
            GetAuditFiles(auditDir, "audit-barriers").Should().NotBeEmpty();
        }
        finally
        {
            Directory.SetCurrentDirectory(originalDirectory);
        }
    }

    [Fact]
    public async Task WriteAsync_null_event_is_discarded_and_logged()
    {
        var testDir = CreateTempDirectory();
        var logger = new TestLogger<FileBarrierAuditSink>();
        await using var sink = CreateSink(testDir, logger);

        await sink.WriteAsync(null!);

        logger.Entries.Should().Contain(entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            entry.Message.Contains("null barrier audit event", StringComparison.OrdinalIgnoreCase));
        GetAuditFiles(testDir, "audit-barriers").Should().BeEmpty();
    }

    [Fact]
    public async Task BuildUniqueFilePath_returns_new_path_when_timestamp_file_exists()
    {
        var testDir = CreateTempDirectory();
        var buildUnique = typeof(FileBarrierAuditSink).GetMethod(
            "BuildUniqueFilePath",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: Type.EmptyTypes, modifiers: null)!;

        await using var sink = CreateSink(testDir, new TestLogger<FileBarrierAuditSink>());
        var firstPath = (string)buildUnique.Invoke(sink, null)!;
        File.WriteAllText(firstPath, "{}");

        string? resolved = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var candidate = (string)buildUnique.Invoke(sink, null)!;
            if (!string.Equals(candidate, firstPath, StringComparison.Ordinal))
            {
                resolved = candidate;
                break;
            }
        }

        resolved.Should().NotBeNull();
        resolved!.Should().NotBe(firstPath);
    }

    [Fact]
    public async Task WriteAsync_logs_when_enqueue_throws()
    {
        var testDir = CreateTempDirectory();
        var logger = new TestLogger<FileBarrierAuditSink>();
        var sink = CreateSink(testDir, logger);

        var channelField = typeof(FileBarrierAuditSink).GetField(
            "_channel",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        channelField.SetValue(sink, null);

        await sink.WriteAsync(new BarrierAuditEvent(
            EventType: BarrierAuditEventType.AgentInvoked,
            BarrierLevel: "internal",
            AuthoritySource: BarrierAuthoritySource.Cli,
            AgentName: "agent",
            CorrelationId: "corr",
            SpanId: "span",
            OccurredAt: DateTimeOffset.UtcNow,
            Detail: "detail"));

        logger.Entries.Should().Contain(entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            entry.Message.Contains("Failed to enqueue barrier audit event", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task BuildUniqueFilePath_uses_numeric_suffix_when_base_name_exists()
    {
        var testDir = CreateTempDirectory();
        var buildUnique = typeof(FileBarrierAuditSink).GetMethod(
            "BuildUniqueFilePath",
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null, types: [typeof(DateTimeOffset)], modifiers: null)!;

        await using var sink = CreateSink(testDir, new TestLogger<FileBarrierAuditSink>());
        // Disk operations under coverage need not finish within one millisecond. Hold the
        // filename's timestamp fixed so both occupied candidates belong to the same collision.
        var now = new DateTimeOffset(2026, 1, 2, 3, 4, 5, 123, TimeSpan.Zero);
        var firstPath = (string)buildUnique.Invoke(sink, [now])!;
        var baseName = Path.GetFileNameWithoutExtension(firstPath);
        var firstSuffix = Path.Combine(testDir, baseName + "-1.ndjson");
        File.WriteAllText(firstPath, "{}");
        File.WriteAllText(firstSuffix, "{}");

        var suffixed = (string)buildUnique.Invoke(sink, [now])!;

        suffixed.Should().Be(Path.Combine(testDir, baseName + "-2.ndjson"));
        File.Exists(suffixed).Should().BeFalse();
        File.ReadAllText(firstPath).Should().Be("{}");
        File.ReadAllText(firstSuffix).Should().Be("{}");
    }

    [Fact]
    public async Task WriteAsync_logs_when_append_fails_for_invalid_directory()
    {
        var testDir = CreateTempDirectory();
        var blockingFile = Path.Combine(testDir, "blocked");
        await File.WriteAllTextAsync(blockingFile, "not-a-directory");
        var logger = new TestLogger<FileBarrierAuditSink>();
        await using var sink = new FileBarrierAuditSink(
            new FileBarrierAuditSinkOptions
            {
                Directory = blockingFile,
                FlushEveryEvent = true,
            },
            logger);

        await sink.WriteAsync(new BarrierAuditEvent(
            EventType: BarrierAuditEventType.ContextInitialized,
            BarrierLevel: "public",
            AuthoritySource: BarrierAuthoritySource.Header,
            AgentName: string.Empty,
            CorrelationId: "corr-append-fail",
            SpanId: string.Empty,
            OccurredAt: DateTimeOffset.UtcNow,
            Detail: "detail"));

        await WaitUntilAsync(() => logger.Entries.Any(entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning));

        logger.Entries.Should().Contain(entry =>
            entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning &&
            entry.Message.Contains("Failed to append barrier audit event", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Polls until <paramref name="condition"/> holds or the timeout elapses; the assertion that
    /// follows reports the failure. The sink does its I/O on a background drain task, so tests
    /// wait for the observable effect, never for a guessed interval.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(25);
    }

    private static FileBarrierAuditSink CreateSink(string directory, TestLogger<FileBarrierAuditSink> logger)
    {
        return new FileBarrierAuditSink(
            new FileBarrierAuditSinkOptions
            {
                Directory = directory,
                FlushEveryEvent = true,
            },
            logger);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "ashlar-audit-gap-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static string[] GetAuditFiles(string directory, string filePrefix)
        => Directory.Exists(directory)
            ? Directory.GetFiles(directory, $"{filePrefix}-*.ndjson")
            : [];
}
