using Ashlar.Abstractions.Security.Egress;
using Ashlar.CLI.Commands;
using Ashlar.Core.Application.Adaptation.Models;
using Ashlar.Core.Application.Adaptation.Ports;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Ashlar.Tests.CLI.Tests.Commands;

public sealed class ImproveEgressAuditTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Promotion_audit_reports_a_refused_broadcast_without_claiming_Promoted(int refusalKind)
    {
        var directory = Path.Combine(Path.GetTempPath(), "improve-egress-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "Fix.cs");
            await File.WriteAllTextAsync(path, "// locally applied change");
            var refusal = new EgressRefusedException(new EgressGuard("secure-workstation", "enforce")
                .Evaluate(new EgressRequest(EgressFamilies.MeshPublish, "EG-MESH-07", "file:/private-share")));
            Exception? error = refusalKind switch
            {
                1 => refusal,
                2 => new AggregateException("wrapper-secret", new Exception("other-secret"), refusal),
                _ => null
            };
            var broadcaster = new Broadcaster(error);
            var audit = new Audit();
            var record = new AdaptationRecord
            {
                Id = "local-fix", Timestamp = DateTimeOffset.UtcNow, FailureType = "EmptyCatch",
                FixApplied = AdaptationFixType.Source, FilePath = path, RegressionPassed = true,
                Promoted = true, Message = "local fix"
            };

            await ImproveCommand.BroadcastAndAuditPromotionAsync(record, directory, "supervised",
                broadcaster, null, audit, NullLogger.Instance);

            broadcaster.Calls.Should().Be(1);
            var entry = audit.Entries.Should().ContainSingle().Which;
            entry.Outcome.Should().Be(refusalKind == 0 ? "Promoted" : "EgressRefused");
            entry.Promoted.Should().Be(refusalKind == 0);
            entry.RegressionPassed.Should().BeTrue();
            entry.Id.Should().Be(record.Id);
            if (refusalKind != 0)
                entry.Message.Should().Contain("Locally applied; broadcast refused:").And.Contain(refusal.Ref)
                    .And.NotContain("wrapper-secret").And.NotContain("private-share");
            (await File.ReadAllTextAsync(path)).Should().Be("// locally applied change");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    private sealed class Broadcaster(Exception? error) : ISharedAdaptationBroadcaster
    {
        public int Calls { get; private set; }
        public Task BroadcastAsync(SharedAdaptationEntry entry, CancellationToken cancellationToken = default)
        {
            Calls++;
            entry.Files.Keys.Should().Equal("Fix.cs");
            if (error is not null) throw error;
            return Task.CompletedTask;
        }
    }

    private sealed class Audit : IAdaptationAuditLog
    {
        public List<AdaptationAuditEntry> Entries { get; } = [];
        public Task LogAsync(AdaptationAuditEntry entry, CancellationToken cancellationToken = default)
        {
            Entries.Add(entry);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<AdaptationAuditEntry>> QueryAsync(DateTimeOffset? since = null, DateTimeOffset? until = null,
            CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<AdaptationAuditEntry>>(Entries);
    }
}
