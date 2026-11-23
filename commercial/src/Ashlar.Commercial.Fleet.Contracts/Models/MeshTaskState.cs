namespace Ashlar.Commercial.Fleet.Contracts.Models;

/// <summary>
/// A schedulable task tracked by the Phase 1 director (in-memory); Phase 3 adds correlation, schedule idempotency, and result handles;
/// Phase 6 adds optional execution lease and checkpoint handle for migrate flows.
/// </summary>
public sealed record MeshTaskState(
    string TaskId,
    string? Name,
    int Steps,
    IReadOnlyList<string> RequiredBrickIds,
    IReadOnlyDictionary<string, string> Affinity,
    int Priority,
    DateTimeOffset? DeadlineUtc,
    MeshTaskStatus Status,
    string? AssignedPeerId,
    string? AssignedApiBaseUrl,
    string? PlacementReason,
    int AttemptCount,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? LastScheduledAtUtc,
    string? CorrelationId,
    string? IdempotencyKey,
    string? LastScheduleIdempotencyKey,
    string? ResultSummary,
    string? ResultHandle,
    string? LeaseToken,
    string? LeaseOwnerPeerId,
    DateTimeOffset? LeaseExpiresUtc,
    string? CheckpointHandle)
{
    /// <summary>Maximum inline result size; larger artifacts use the existing result-handle path.</summary>
    public const int MaxResultPayloadBytes = 256 * 1024;

    /// <summary>Worker-produced bytes committed atomically with successful leased completion.</summary>
    public byte[]? ResultPayload { get; init; }
}
