using Ashlar.BackgroundAgents.Trust;
using Ashlar.Core.Application.Trust.Ports;
using FluentAssertions;
using Xunit;

namespace Ashlar.Tests.BackgroundAgents.Trust;

/// <summary>All sanitization audit backends honor the same read limit and timestamp filter.</summary>
public sealed class SanitizationAuditLogContractTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ashlar-sanitization-contract-{Guid.NewGuid():N}.db");
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("sanitization-memory")]
    [InlineData("decision-memory")]
    [InlineData("litedb")]
    public void GetRecent_limits_matching_entries_and_orders_newest_first(string backend)
    {
        var log = Create(backend);
        Seed(log);

        var entries = log.GetRecent(2, Start.AddMinutes(2));

        entries.Select(e => e.FieldOrType).Should().Equal("field-5", "field-4");
        entries.Should().OnlyContain(e => e.RuleVersion == "v1" && e.Disposition == "redacted" && e.Reason == "test");
        log.GetRecent(10, Start.AddMinutes(3)).Select(e => e.FieldOrType)
            .Should().Equal("field-5", "field-4", "field-3", "field-2");
    }

    [Theory]
    [InlineData("sanitization-memory")]
    [InlineData("decision-memory")]
    [InlineData("litedb")]
    public void GetRecent_nonpositive_limits_are_empty(string backend)
    {
        var log = Create(backend);
        Seed(log);

        log.GetRecent(0).Should().BeEmpty();
        log.GetRecent(-1).Should().BeEmpty();
        log.GetRecent(int.MinValue).Should().BeEmpty();
    }

    [Theory]
    [InlineData("sanitization-memory")]
    [InlineData("decision-memory")]
    [InlineData("litedb")]
    public void GetRecent_large_limits_do_not_overflow(string backend)
    {
        var log = Create(backend);
        Seed(log);

        log.GetRecent(int.MaxValue / 3 + 1).Should().HaveCount(6);
        log.GetRecent(int.MaxValue).Should().HaveCount(6);
    }

    private ISanitizationAuditLog Create(string backend) => backend switch
    {
        "sanitization-memory" => new InMemorySanitizationAuditLog(),
        "decision-memory" => new DataDecisionAuditLog(),
        "litedb" => new LiteDbDataDecisionAuditLog(_dbPath),
        _ => throw new ArgumentOutOfRangeException(nameof(backend)),
    };

    private static void Seed(ISanitizationAuditLog log)
    {
        // Interleave unrelated events so a limit applied before the event-type filter fails.
        for (var i = 0; i < 6; i++)
        {
            log.LogRedaction(Start.AddMinutes(i + 1), "v1", $"field-{i}", "redacted", "test");
            if (log is IDataDecisionAuditLog decisions)
                decisions.LogClassification("other", "public", "not a sanitization event");
        }
    }

    /// <summary>Removes the per-test database after all handles have closed.</summary>
    public void Dispose()
    {
        if (File.Exists(_dbPath))
            File.Delete(_dbPath);
        var logPath = Path.Combine(Path.GetDirectoryName(_dbPath)!, Path.GetFileNameWithoutExtension(_dbPath) + "-log.db");
        if (File.Exists(logPath))
            File.Delete(logPath);
    }
}
