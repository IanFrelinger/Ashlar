using Microsoft.Extensions.Logging;

namespace Ashlar.Infrastructure.Execution.Sandbox;

/// <summary>
/// Provenance event emitted for every sandbox-session lifecycle transition — started,
/// attested, stopped, teardown-failed, reaped — modeled on the hot-swap provenance events
/// (extension spec Part B session provenance). A session that ran and vanished without a
/// trace is exactly the unattributable execution the trust loop exists to prevent.
/// </summary>
public sealed record SessionProvenanceEvent
{
    /// <summary>Session the event concerns.</summary>
    public required string SessionId { get; init; }

    /// <summary>Event outcome (see <see cref="SessionProvenanceOutcomes"/>).</summary>
    public required string Outcome { get; init; }

    /// <summary>UTC event time.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Image reference the session was started from.</summary>
    public string? Image { get; init; }

    /// <summary>Resolved image identity, when attested.</summary>
    public string? ImageDigest { get; init; }

    /// <summary>Machine-readable failure code on failures.</summary>
    public string? FailureCode { get; init; }

    /// <summary>Human-readable detail.</summary>
    public string? Reason { get; init; }
}

/// <summary>Well-known <see cref="SessionProvenanceEvent.Outcome"/> values.</summary>
internal static class SessionProvenanceOutcomes
{
    /// <summary>The session container started.</summary>
    public const string Started = "started";

    /// <summary>The session's environment was attested.</summary>
    public const string Attested = "attested";

    /// <summary>The session was stopped and its environment destroyed.</summary>
    public const string Stopped = "stopped";

    /// <summary>Teardown ran but the backend reported failure (the reaper is the backstop).</summary>
    public const string TeardownFailed = "teardown-failed";

    /// <summary>The reaper destroyed a leaked session past its deadline.</summary>
    public const string Reaped = "reaped";
}

/// <summary>
/// Receives every session provenance event. The trust-loop plan projects these into the
/// provenance graph; until that projection lands, the default sink writes structured logs.
/// Implementations must not throw — a provenance sink failure must never fail a session
/// operation.
/// </summary>
public interface ISessionProvenanceSink
{
    /// <summary>Records one provenance event.</summary>
    void Record(SessionProvenanceEvent provenanceEvent);
}

/// <summary>Default sink: structured log lines, one per event.</summary>
internal sealed class LoggingSessionProvenanceSink : ISessionProvenanceSink
{
    private readonly ILogger<LoggingSessionProvenanceSink> _logger;

    /// <summary>Initializes the logging sink.</summary>
    public LoggingSessionProvenanceSink(ILogger<LoggingSessionProvenanceSink> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public void Record(SessionProvenanceEvent provenanceEvent)
    {
        _logger.LogInformation(
            "sandbox-session {Outcome} session={SessionId} image={Image} digest={ImageDigest} code={FailureCode} {Reason}",
            provenanceEvent.Outcome,
            provenanceEvent.SessionId,
            provenanceEvent.Image,
            provenanceEvent.ImageDigest,
            provenanceEvent.FailureCode,
            provenanceEvent.Reason);
    }
}
