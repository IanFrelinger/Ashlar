using Ashlar.Core.Domain.Bricks;

namespace Ashlar.Core.Domain.Execution.Events;

/// <summary>
/// Emitted when a behavior step fails with an error.
/// </summary>
public class StepErrorEvent : ExecutionEvent
{
    /// <summary>ID of the step that failed.</summary>
    public string StepId { get; init; } = default!;
    /// <summary>Error message.</summary>
    public string Error { get; init; } = default!;
    /// <summary>Optional latency before failure (ms).</summary>
    public long? LatencyMs { get; init; }

    /// <summary>
    /// Optional exception that caused the failure. When the failure is an egress refusal,
    /// this is the <c>EgressRefusedException</c> (or a wrapper that still carries it).
    /// </summary>
    public Exception? Cause { get; init; }

    /// <summary>
    /// Stable refusal error code when this event carries a typed egress refusal
    /// (normally <c>EGRESS_REFUSED</c>). Null when the failure is not a refusal.
    /// </summary>
    public string? RefusalErrorCode { get; init; }

    /// <summary>
    /// Refusal reference suitable for subjects and remote parties when this event carries a typed egress refusal.
    /// </summary>
    public string? RefusalRef { get; init; }

    /// <summary>True when this event names a typed egress refusal by error code.</summary>
    public bool WasRefused => !string.IsNullOrEmpty(RefusalErrorCode);

    public StepErrorEvent(
        string stepId,
        string error,
        long? latencyMs = null,
        Exception? cause = null,
        string? refusalErrorCode = null,
        string? refusalRef = null)
        : base("step_error", DateTime.UtcNow)
    {
        StepId = stepId;
        Error = error;
        LatencyMs = latencyMs;
        Cause = cause;
        RefusalErrorCode = refusalErrorCode;
        RefusalRef = refusalRef;
    }
}
