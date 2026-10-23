using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Ashlar.Orchestration.Coordination.Conflicts;

namespace Ashlar.Orchestration.Coordination;

/// <summary>
/// Manages escalation of conflicts and issues to human operators.
/// 
/// Responsibilities:
/// - Escalates conflicts that cannot be resolved autonomously
/// - Escalates general issues (stalling, resource exhaustion, etc.)
/// - Tracks escalation status (Pending, Resolved, Dismissed)
/// - Provides escalation querying and filtering
/// - Supports resolution and dismissal of escalations
/// 
/// Used by Orchestrator when negotiation fails or issues require human intervention.
/// </summary>
public sealed class EscalationManager
{
    private readonly ILogger<EscalationManager> _logger;
    private readonly ConcurrentQueue<Escalation> _escalations = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="EscalationManager"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public EscalationManager(ILogger<EscalationManager> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Escalates a conflict to human operators.
    /// 
    /// Creates an escalation record with the conflict details and adds it to the escalation list.
    /// The escalation severity is derived from the conflict severity.
    /// </summary>
    /// <param name="conflict">The conflict to escalate.</param>
    /// <param name="context">Optional context information about why the escalation occurred.</param>
    /// <returns>The created escalation record.</returns>
    /// <exception cref="ArgumentNullException">Thrown if conflict is null.</exception>
    public Escalation EscalateConflict(Conflict conflict, string? context = null)
    {
        if (conflict == null)
        {
            throw new ArgumentNullException(nameof(conflict));
        }

        var escalation = new Escalation
        {
            Id = Guid.NewGuid().ToString(),
            Conflict = conflict,
            EscalatedAt = DateTimeOffset.UtcNow,
            Severity = (EscalationSeverity)(int)conflict.Severity,
            Context = context,
            Status = EscalationStatus.Pending
        };

        _escalations.Enqueue(escalation);

        _logger.LogWarning("Escalated conflict {ConflictType} between agents {AgentIds}: {Description}",
            conflict.ConflictType, string.Join(", ", conflict.AgentIds), conflict.Description);

        return escalation;
    }

    /// <summary>
    /// Escalates a general issue (not a conflict).
    /// 
    /// Creates an escalation record for non-conflict issues such as:
    /// - Resource allocation failures
    /// - Agent execution failures
    /// - System errors
    /// - Timeouts
    /// </summary>
    /// <param name="issueType">The type of issue (e.g., "ResourceAllocation", "AgentExecution").</param>
    /// <param name="description">A description of the issue.</param>
    /// <param name="severity">The severity of the issue.</param>
    /// <param name="context">Optional context information (e.g., stack traces, additional details).</param>
    /// <returns>The created escalation record.</returns>
    public Escalation EscalateIssue(string issueType, string description, EscalationSeverity severity, string? context = null)
    {
        var escalation = new Escalation
        {
            Id = Guid.NewGuid().ToString(),
            IssueType = issueType,
            Description = description,
            EscalatedAt = DateTimeOffset.UtcNow,
            Severity = severity,
            Context = context,
            Status = EscalationStatus.Pending
        };

        _escalations.Enqueue(escalation);

        _logger.LogWarning("Escalated issue {IssueType}: {Description}", issueType, description);

        return escalation;
    }

    /// <summary>
    /// Resolves an escalation (human operator has handled it).
    /// 
    /// Updates the escalation status to Resolved and records the resolution details.
    /// If the escalation doesn't exist, logs a warning and returns.
    /// </summary>
    /// <param name="escalationId">The ID of the escalation to resolve.</param>
    /// <param name="resolution">Optional resolution description from the operator.</param>
    public void ResolveEscalation(string escalationId, string? resolution = null)
    {
        var escalation = _escalations.FirstOrDefault(e => e.Id == escalationId);
        if (escalation == null)
        {
            _logger.LogWarning("Attempted to resolve non-existent escalation {EscalationId}", escalationId);
            return;
        }

        escalation.Status = EscalationStatus.Resolved;
        escalation.ResolvedAt = DateTimeOffset.UtcNow;
        escalation.Resolution = resolution;

        _logger.LogInformation("Resolved escalation {EscalationId}: {Resolution}", escalationId, resolution ?? "No resolution provided");
    }

    /// <summary>
    /// Gets all pending escalations.
    /// 
    /// Returns escalations ordered by severity (highest first).
    /// </summary>
    /// <returns>A read-only list of pending escalations.</returns>
    public IReadOnlyList<Escalation> GetPendingEscalations()
    {
        return _escalations
            .Where(e => e.Status == EscalationStatus.Pending)
            .OrderByDescending(e => e.Severity)
            .ToList();
    }

    /// <summary>
    /// Gets escalations by severity.
    /// 
    /// Returns escalations of the specified severity, ordered by escalation time (newest first).
    /// </summary>
    /// <param name="severity">The severity level to filter by.</param>
    /// <returns>A read-only list of escalations with the specified severity.</returns>
    public IReadOnlyList<Escalation> GetEscalationsBySeverity(EscalationSeverity severity)
    {
        return _escalations
            .Where(e => e.Severity == severity)
            .OrderByDescending(e => e.EscalatedAt)
            .ToList();
    }

    /// <summary>
    /// Gets all escalations (pending, resolved, and dismissed).
    /// </summary>
    /// <returns>A read-only list of all escalations.</returns>
    public IReadOnlyList<Escalation> GetAllEscalations()
    {
        return _escalations.ToList();
    }
}
