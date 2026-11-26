using LiteDB;
using Ashlar.Commercial.Fleet.Contracts.Models;

namespace Ashlar.Commercial.Fleet.Infrastructure;

/// <summary>Mesh task doc.</summary>
internal sealed class MeshTaskDoc
{
    /// <summary>LiteDB primary key and mesh task identifier.</summary>
    [BsonId]
    public string TaskId { get; set; } = "";

    /// <summary>Name.</summary>
    public string? Name { get; set; }
    /// <summary>Steps.</summary>
    public int Steps { get; set; }
    /// <summary>Required brick ids.</summary>
    public List<string> RequiredBrickIds { get; set; } = new();
    /// <summary>Affinity.</summary>
    public Dictionary<string, string> Affinity { get; set; } = new();
    /// <summary>Priority.</summary>
    public int Priority { get; set; }
    /// <summary>Deadline utc.</summary>
    public DateTimeOffset? DeadlineUtc { get; set; }
    /// <summary>Status.</summary>
    public int Status { get; set; }
    /// <summary>Assigned peer id.</summary>
    public string? AssignedPeerId { get; set; }
    /// <summary>Assigned api base url.</summary>
    public string? AssignedApiBaseUrl { get; set; }
    /// <summary>Placement reason.</summary>
    public string? PlacementReason { get; set; }
    /// <summary>Attempt count.</summary>
    public int AttemptCount { get; set; }
    /// <summary>Created at utc.</summary>
    public DateTimeOffset CreatedAtUtc { get; set; }
    /// <summary>Last scheduled at utc.</summary>
    public DateTimeOffset? LastScheduledAtUtc { get; set; }
    /// <summary>Correlation id.</summary>
    public string? CorrelationId { get; set; }
    /// <summary>Idempotency key.</summary>
    public string? IdempotencyKey { get; set; }
    /// <summary>Last schedule idempotency key.</summary>
    public string? LastScheduleIdempotencyKey { get; set; }
    /// <summary>Result summary.</summary>
    public string? ResultSummary { get; set; }
    /// <summary>Result handle.</summary>
    public string? ResultHandle { get; set; }
    /// <summary>Inline worker result, stored in the same transaction as terminal status.</summary>
    public byte[]? ResultPayload { get; set; }
    /// <summary>Lease token.</summary>
    public string? LeaseToken { get; set; }
    /// <summary>Lease owner peer id.</summary>
    public string? LeaseOwnerPeerId { get; set; }
    /// <summary>Lease expires utc.</summary>
    public DateTimeOffset? LeaseExpiresUtc { get; set; }
    /// <summary>Checkpoint handle.</summary>
    public string? CheckpointHandle { get; set; }

    /// <summary>From state.</summary>
    /// <param name="t">T.</param>
    public static MeshTaskDoc FromState(MeshTaskState t) => new()
    {
        TaskId = t.TaskId,
        Name = t.Name,
        Steps = t.Steps,
        RequiredBrickIds = t.RequiredBrickIds.ToList(),
        Affinity = t.Affinity.ToDictionary(static kv => kv.Key, static kv => kv.Value),
        Priority = t.Priority,
        DeadlineUtc = t.DeadlineUtc,
        Status = (int)t.Status,
        AssignedPeerId = t.AssignedPeerId,
        AssignedApiBaseUrl = t.AssignedApiBaseUrl,
        PlacementReason = t.PlacementReason,
        AttemptCount = t.AttemptCount,
        CreatedAtUtc = t.CreatedAtUtc,
        LastScheduledAtUtc = t.LastScheduledAtUtc,
        CorrelationId = t.CorrelationId,
        IdempotencyKey = t.IdempotencyKey,
        LastScheduleIdempotencyKey = t.LastScheduleIdempotencyKey,
        ResultSummary = t.ResultSummary,
        ResultHandle = t.ResultHandle,
        ResultPayload = t.ResultPayload,
        LeaseToken = t.LeaseToken,
        LeaseOwnerPeerId = t.LeaseOwnerPeerId,
        LeaseExpiresUtc = t.LeaseExpiresUtc,
        CheckpointHandle = t.CheckpointHandle,
    };

    /// <summary>To state.</summary>
    public MeshTaskState ToState() => new(
        TaskId,
        Name,
        Steps,
        RequiredBrickIds,
        Affinity,
        Priority,
        DeadlineUtc,
        (MeshTaskStatus)Status,
        AssignedPeerId,
        AssignedApiBaseUrl,
        PlacementReason,
        AttemptCount,
        CreatedAtUtc,
        LastScheduledAtUtc,
        CorrelationId,
        IdempotencyKey,
        LastScheduleIdempotencyKey,
        ResultSummary,
        ResultHandle,
        LeaseToken,
        LeaseOwnerPeerId,
        LeaseExpiresUtc,
        CheckpointHandle) { ResultPayload = ResultPayload };
}
