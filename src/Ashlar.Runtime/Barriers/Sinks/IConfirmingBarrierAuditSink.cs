using Ashlar.Abstractions.Barriers;

namespace Ashlar.Runtime.Barriers.Sinks;

/// <summary>
/// A sink that can also report whether one event actually reached its destination. For callers that must not
/// proceed as if audited when the record was lost (a full disk, an unwritable directory, a closed sink).
/// </summary>
public interface IConfirmingBarrierAuditSink : IBarrierAuditSink
{
    /// <summary>
    /// Writes the event and completes only once it is written and flushed to the destination. Unlike
    /// <see cref="IBarrierAuditSink.WriteAsync"/>, this throws when the event cannot be written, and waits for room
    /// instead of dropping the event when the sink's queue is full.
    /// </summary>
    ValueTask WriteConfirmedAsync(BarrierAuditEvent auditEvent, CancellationToken cancellationToken = default);
}
