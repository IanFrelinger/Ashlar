namespace Ashlar.Core.Application.Execution;

/// <summary>
/// Collects supported provider observations at their declared boundary in the current async flow without changing string-based provider ports.
/// Nested scopes also contribute to their parents, retaining the child's correlation identifier for attribution.
/// Await child work before disposing; disposal freezes this scope.
/// An empty snapshot means no instrumented observations were collected, not that the workflow used zero tokens.
/// </summary>
public sealed class ProviderUsageScope : IDisposable
{
    private static readonly AsyncLocal<ProviderUsageScope?> Current = new();
    private readonly ProviderUsageScope? _parent;
    private readonly string _correlationId;
    private readonly object _gate = new();
    private readonly List<ProviderUsageRecord> _records = new();
    private readonly HashSet<(Guid, int, ProviderUsageBoundary)> _recordIds = new();
    private bool _disposed;

    /// <summary>Starts a collecting scope. Correlation identifiers must not contain prompts or secrets.</summary>
    public ProviderUsageScope(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId)) throw new ArgumentException("A correlation identifier is required.", nameof(correlationId));
        _correlationId = correlationId;
        _parent = Current.Value;
        Current.Value = this;
    }

    /// <summary>Returns a thread-safe copy of terminal observations, including failed and cancelled calls.</summary>
    public IReadOnlyList<ProviderUsageRecord> Snapshot()
    {
        lock (_gate) return _records.ToArray();
    }

    internal static ProviderUsageScope? Capture() => Current.Value;

    /// <summary>
    /// Records a terminal observation from an adapter into the active scope. Use a nonempty invocation ID,
    /// a positive attempt number, and the correct boundary. Repeated terminal events with the same ID,
    /// attempt and boundary are ignored. No active scope means no collection.
    /// </summary>
    public static void RecordCurrent(ProviderUsageRecord record)
    {
        if (record is null) throw new ArgumentNullException(nameof(record));
        if (record.RequestId == Guid.Empty) throw new ArgumentException("A request identifier is required.", nameof(record));
        if (record.Attempt <= 0) throw new ArgumentException("Attempt numbers must be positive.", nameof(record));
        if (!Enum.IsDefined(typeof(ProviderUsageBoundary), record.Boundary)) throw new ArgumentException("Unknown observation boundary.", nameof(record));
        Current.Value?.Record(record with { Usage = ProviderUsageSummary.Normalize(record.Usage) });
    }

    internal void Record(ProviderUsageRecord record)
    {
        var correlated = record with { CorrelationId = _correlationId };
        for (var scope = this; scope is not null; scope = scope._parent)
        {
            lock (scope._gate)
            {
                if (!scope._disposed && scope._recordIds.Add((correlated.RequestId, correlated.Attempt, correlated.Boundary)))
                    scope._records.Add(correlated);
            }
        }
    }

    /// <summary>Stops collecting into this scope and restores its parent in the current async flow.</summary>
    public void Dispose()
    {
        lock (_gate) _disposed = true;
        if (ReferenceEquals(Current.Value, this)) Current.Value = _parent;
    }
}
