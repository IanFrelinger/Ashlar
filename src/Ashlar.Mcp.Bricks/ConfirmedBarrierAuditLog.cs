using Ashlar.Abstractions.Barriers;
using Ashlar.Runtime.Barriers.Sinks;
using Microsoft.Extensions.Logging;

namespace Ashlar.Mcp.Bricks;

/// <summary>
/// The audit log behind <see cref="BrickMcpToolContributor"/>: writes each record to every registered sink and
/// throws when any of them did not write it, so the contributor can refuse to hand back a result it cannot show a
/// record for. A sink that implements <see cref="IConfirmingBarrierAuditSink"/> (the File sink does) is awaited until
/// the record is flushed; any other sink is called through <see cref="IBarrierAuditSink.WriteAsync"/>, and only an
/// exception from it counts as a failure.
/// </summary>
/// <remarks>
/// <c>StructuredBarrierAuditLog</c> is not used here: it logs and swallows every sink failure, and the File sink only
/// enqueues, so a record lost to a full disk or an unwritable directory would never reach the contributor as one.
/// </remarks>
internal sealed class ConfirmedBarrierAuditLog : IBarrierAuditLog
{
    private readonly IReadOnlyList<IBarrierAuditSink> _sinks;

    public ConfirmedBarrierAuditLog(ILogger<ConfirmedBarrierAuditLog> logger, IEnumerable<IBarrierAuditSink> sinks)
    {
        _sinks = sinks.ToList();
        if (_sinks.Count == 0 || _sinks.All(static sink => sink is NoOpBarrierAuditSink))
        {
            logger.LogWarning("No IBarrierAuditSink registered. MCP brick audit records will be discarded.");
        }
    }

    public async ValueTask RecordAsync(BarrierAuditEvent auditEvent, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        List<Exception>? failures = null;
        foreach (var sink in _sinks)
        {
            try
            {
                if (sink is IConfirmingBarrierAuditSink confirming)
                {
                    await confirming.WriteConfirmedAsync(auditEvent, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await sink.WriteAsync(auditEvent, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                (failures ??= new List<Exception>()).Add(ex);
            }
        }

        if (failures is not null)
        {
            throw new AggregateException(
                $"The audit record {auditEvent.CorrelationId} was not written by {failures.Count} of {_sinks.Count} sink(s).",
                failures);
        }
    }
}
