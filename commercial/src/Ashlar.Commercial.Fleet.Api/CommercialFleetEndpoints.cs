using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Ashlar.API.Security;
using Ashlar.Commercial.Fleet.Contracts.Models;
using Ashlar.Commercial.Fleet.Contracts.Ports;
using Ashlar.Commercial.Fleet.Infrastructure;

namespace Ashlar.Commercial.Fleet.Api;

/// <summary>
/// Commercial fleet/director HTTP endpoints for mesh node registration, task placement, leases, and knowledge replication.
/// </summary>
public static class CommercialFleetEndpoints
{
    private const string CommercialMeshCorrelationItemKey = "Ashlar.Mesh.CorrelationId";

    /// <summary>Maps commercial fleet HTTP endpoints under <c>/api/mesh</c>.</summary>
    public static IEndpointRouteBuilder MapCommercialFleetEndpoints(this IEndpointRouteBuilder app)
    {
        var mesh = app.MapGroup("/api/mesh").WithTags("Commercial Mesh Fleet");

        mesh.MapGet("/fleet/nodes", ListFleetNodesAsync)
            .WithName("ListFleetNodes")
            .WithSummary("Phase 1: list registered mesh worker nodes (in-memory director)")
            .Produces<IReadOnlyList<MeshFleetNodeResponse>>(StatusCodes.Status200OK);

        mesh.MapPost("/fleet/nodes", RegisterFleetNodeAsync)
            .WithName("RegisterFleetNode")
            .WithSummary("Phase 1: register or update a worker node")
            .Produces<MeshFleetNodeResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        mesh.MapDelete("/fleet/nodes/{peerId}", RemoveFleetNodeAsync)
            .WithName("RemoveFleetNode")
            .WithSummary("Phase 1: remove a worker from the fleet registry")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapPost("/fleet/nodes/{peerId}/heartbeat", FleetNodeHeartbeatAsync)
            .WithName("FleetNodeHeartbeat")
            .WithSummary("Phase 1/5: record worker heartbeat; optional body queueDepth for elastic placement")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapGet("/elastic/status", GetMeshElasticStatusAsync)
            .WithName("GetMeshElasticStatus")
            .WithSummary("Phase 5: mesh task counts and fleet queue snapshot")
            .Produces<MeshElasticStatusResponse>(StatusCodes.Status200OK);

        mesh.MapPost("/fleet/nodes/{peerId}/drain", SetFleetNodeDrainAsync)
            .WithName("SetFleetNodeDrain")
            .WithSummary("Phase 1: set drained flag (excluded from new placements)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapPost("/fleet/nodes/{peerId}/admit", AdmitFleetNodeAsync)
            .WithName("AdmitFleetNode")
            .WithSummary("Phase 6: mark peer admitted (eligible for placement)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapPost("/fleet/nodes/{peerId}/revoke", RevokeFleetNodeAsync)
            .WithName("RevokeFleetNode")
            .WithSummary("Phase 6: revoke peer admission (excluded from new placements)")
            .Produces(StatusCodes.Status204NoContent)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapGet("/tasks", ListMeshTasksAsync)
            .WithName("ListMeshTasks")
            .WithSummary("Phase 1: list mesh tasks")
            .Produces<IReadOnlyList<MeshTaskResponse>>(StatusCodes.Status200OK);

        mesh.MapPost("/tasks", CreateMeshTaskAsync)
            .WithName("CreateMeshTask")
            .WithSummary("Phase 1: create a pending mesh task")
            .Produces<MeshTaskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        mesh.MapGet("/tasks/{taskId}", GetMeshTaskAsync)
            .WithName("GetMeshTask")
            .WithSummary("Phase 1: get one mesh task")
            .Produces<MeshTaskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapPost("/tasks/{taskId}/schedule", ScheduleMeshTaskAsync)
            .WithName("ScheduleMeshTask")
            .WithSummary("Phase 1/6: assign task to an eligible worker (optional leaseSeconds in body)")
            .Produces<MeshTaskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        mesh.MapPost("/tasks/{taskId}/retry", RetryMeshTaskAsync)
            .WithName("RetryMeshTask")
            .WithSummary("Phase 1/6: re-place task on a different worker when possible (optional leaseSeconds in body)")
            .Produces<MeshTaskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        mesh.MapPost("/tasks/{taskId}/lease/extend", ExtendMeshTaskLeaseAsync)
            .WithName("ExtendMeshTaskLease")
            .WithSummary("Phase 6: extend execution lease for an assigned task (requires lease token)")
            .Produces<MeshTaskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapPost("/tasks/{taskId}/migrate-for-checkpoint", MigrateMeshTaskForCheckpointAsync)
            .WithName("MigrateMeshTaskForCheckpoint")
            .WithSummary("Phase 6: record checkpoint handle and release assignment for re-scheduling")
            .Produces<MeshTaskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapGet("/tasks/{taskId}/result", DownloadMeshTaskResultAsync)
            .WithName("DownloadMeshTaskResult")
            .WithSummary("Phase 3: download result bytes when ResultHandle is a file path on this host")
            .Produces(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound);

        mesh.MapPatch("/tasks/{taskId}/status", PatchMeshTaskStatusAsync)
            .WithName("PatchMeshTaskStatus")
            .WithSummary("Phase 1/6: update task status; Assigned/Running updates require matching leaseToken")
            .Produces<MeshTaskResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict);

        mesh.MapGet("/knowledge/export", ExportMeshKnowledgeAsync)
            .WithName("ExportMeshKnowledge")
            .WithSummary("Phase 4: export adaptation + pattern records for peer sync")
            .Produces<Ashlar.Commercial.Fleet.Contracts.Models.MeshKnowledgeExportPayload>(StatusCodes.Status200OK);

        mesh.MapPost("/knowledge/import", ImportMeshKnowledgeAsync)
            .WithName("ImportMeshKnowledge")
            .WithSummary("Phase 4: import adaptation + pattern payload from a peer")
            .Produces<MeshKnowledgeImportResponse>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest);

        return app;
    }

    private static async Task<IResult> ListFleetNodesAsync(
        [FromServices] IFleetNodeRegistry registry,
        CancellationToken cancellationToken)
    {
        var nodes = await registry.ListAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(nodes.Select(ToFleetResponse).ToList());
    }

    private static async Task<IResult> RegisterFleetNodeAsync(
        [FromBody] MeshFleetNodeRequest? body,
        [FromServices] IFleetNodeRegistry registry,
        [FromServices] IOptions<MeshFleetRegistrationOptions> registrationOptions,
        [FromServices] IOptions<AshlarSecurityOptions> securityOptions,
        CancellationToken cancellationToken)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.PeerId) || string.IsNullOrWhiteSpace(body.ApiBaseUrl))
            return Results.BadRequest(new ProblemDetails { Title = "PeerId and ApiBaseUrl are required" });

        if (!Uri.TryCreate(body.ApiBaseUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return Results.BadRequest(new ProblemDetails { Title = "ApiBaseUrl must be an absolute http or https URL" });
        }

        var regOpts = registrationOptions.Value;
        string? suppliedFingerprint = null;
        if (regOpts.RequirePeerRegistrationKey)
        {
            if (string.IsNullOrWhiteSpace(body.PeerRegistrationKey) ||
                body.PeerRegistrationKey.Trim().Length < regOpts.MinRegistrationKeyLength)
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "peerRegistrationKey is required and must meet minimum length when fleet registration policy is enabled."
                });
            }

            if (!MeshFleetRegistrationKeys.IsDistinctFromDirectorKey(body.PeerRegistrationKey, securityOptions.Value.ApiKey))
            {
                return Results.BadRequest(new ProblemDetails
                {
                    Title = "peerRegistrationKey must differ from the director operator API key."
                });
            }

            suppliedFingerprint = MeshFleetRegistrationKeys.Fingerprint(body.PeerRegistrationKey);
        }
        else if (!string.IsNullOrWhiteSpace(body.PeerRegistrationKey))
        {
            suppliedFingerprint = MeshFleetRegistrationKeys.Fingerprint(body.PeerRegistrationKey);
        }

        var peerId = body.PeerId.Trim();
        var apiBaseUrl = body.ApiBaseUrl.Trim();
        var requestedTrustTier = ParseFleetTrustTier(body.TrustTier);

        // Every "null means leave the stored value" fallback below runs INSIDE the registry's write
        // transaction. It used to run here, off a `registry.GetAsync` a few lines up, on a database
        // the store had already closed -- so a `POST /fleet/nodes/{peerId}/revoke` that committed in
        // the window was written straight back as `Admitted = true` from the snapshot, and placement
        // selects on `Admitted && !Drained`. A node reconnects on a timer; an operator revokes once.
        var state = await registry.RegisterOrMergeAsync(
            peerId,
            existing =>
            {
                var queue = body.ReportedQueueDepth ?? existing?.ReportedQueueDepth ?? 0;
                if (queue < 0) queue = 0;

                return new MeshFleetNodeState(
                    PeerId: peerId,
                    ApiBaseUrl: apiBaseUrl,
                    // Unchanged semantics: an omitted labels/bricks list REPLACES, it does not
                    // merge. That is not a race (it needs no concurrent writer at all) and is a
                    // separate question from the one this change answers.
                    Labels: body.Labels ?? new Dictionary<string, string>(),
                    AdvertisedBrickIds: body.AdvertisedBrickIds ?? Array.Empty<string>(),
                    Drained: body.Drained ?? existing?.Drained ?? false,
                    LastHeartbeatUtc: DateTimeOffset.UtcNow,
                    RegisteredAtUtc: existing?.RegisteredAtUtc ?? DateTimeOffset.UtcNow,
                    ReportedQueueDepth: queue,
                    TrustTier: requestedTrustTier ?? existing?.TrustTier ?? MeshFleetTrustTier.Trusted,
                    Admitted: body.Admitted ?? existing?.Admitted ?? true,
                    RegistrationKeyFingerprint: suppliedFingerprint ?? existing?.RegistrationKeyFingerprint);
            },
            cancellationToken).ConfigureAwait(false);

        return Results.Ok(ToFleetResponse(state));
    }

    private static async Task<IResult> AdmitFleetNodeAsync(
        string peerId,
        [FromServices] IFleetNodeRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            return Results.BadRequest(new ProblemDetails { Title = "peerId is required" });
        var ok = await registry.SetAdmittedAsync(peerId, admitted: true, cancellationToken).ConfigureAwait(false);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RevokeFleetNodeAsync(
        string peerId,
        [FromServices] IFleetNodeRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            return Results.BadRequest(new ProblemDetails { Title = "peerId is required" });
        var ok = await registry.SetAdmittedAsync(peerId, admitted: false, cancellationToken).ConfigureAwait(false);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> RemoveFleetNodeAsync(
        string peerId,
        [FromServices] IFleetNodeRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            return Results.BadRequest(new ProblemDetails { Title = "peerId is required" });
        var ok = await registry.RemoveAsync(peerId, cancellationToken).ConfigureAwait(false);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> FleetNodeHeartbeatAsync(
        string peerId,
        [FromBody] MeshHeartbeatRequest? body,
        [FromServices] IFleetNodeRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            return Results.BadRequest(new ProblemDetails { Title = "peerId is required" });
        var existing = await registry.GetAsync(peerId, cancellationToken).ConfigureAwait(false);
        if (existing is null)
            return Results.NotFound();
        await registry.HeartbeatAsync(peerId, body?.QueueDepth, cancellationToken).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> GetMeshElasticStatusAsync(
        [FromServices] IFleetNodeRegistry fleet,
        [FromServices] IMeshTaskRegistry tasks,
        CancellationToken cancellationToken)
    {
        var nodes = await fleet.ListAsync(cancellationToken).ConfigureAwait(false);
        var taskList = await tasks.ListAsync(cancellationToken).ConfigureAwait(false);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in Enum.GetNames<MeshTaskStatus>())
            counts[s] = 0;
        foreach (var t in taskList)
        {
            var key = t.Status.ToString();
            counts[key] = counts.GetValueOrDefault(key) + 1;
        }

        var workers = nodes
            .Where(n => !n.Drained)
            .Select(n => new MeshElasticWorkerSnapshot(
                n.PeerId,
                n.ReportedQueueDepth,
                n.LastHeartbeatUtc))
            .ToList();

        return Results.Ok(new MeshElasticStatusResponse(
            counts,
            workers,
            DateTimeOffset.UtcNow));
    }

    private static async Task<IResult> SetFleetNodeDrainAsync(
        string peerId,
        [FromBody] MeshFleetDrainRequest? body,
        [FromServices] IFleetNodeRegistry registry,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(peerId))
            return Results.BadRequest(new ProblemDetails { Title = "peerId is required" });
        if (body is null)
            return Results.BadRequest(new ProblemDetails { Title = "Request body with Drained boolean is required" });
        var ok = await registry.SetDrainedAsync(peerId, body.Drained, cancellationToken).ConfigureAwait(false);
        return ok ? Results.NoContent() : Results.NotFound();
    }

    private static async Task<IResult> ListMeshTasksAsync(
        [FromServices] IMeshTaskRegistry tasks,
        CancellationToken cancellationToken)
    {
        var list = await tasks.ListAsync(cancellationToken).ConfigureAwait(false);
        return Results.Ok(list.Select(ToTaskResponse).ToList());
    }

    private static async Task<IResult> CreateMeshTaskAsync(
        [FromBody] MeshTaskCreateRequest? body,
        [FromServices] IMeshTaskRegistry tasks,
        [FromServices] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        if (body is null)
            return Results.BadRequest(new ProblemDetails { Title = "Request body is required" });
        if (body.Steps < 1)
            return Results.BadRequest(new ProblemDetails { Title = "Steps must be at least 1" });

        var correlation = ResolveMeshCorrelationId(httpContextAccessor, body.CorrelationId);
        if (!string.IsNullOrWhiteSpace(body.IdempotencyKey))
        {
            var existing = await tasks.TryGetByIdempotencyKeyAsync(body.IdempotencyKey, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
                return Results.Ok(ToTaskResponse(existing with { CorrelationId = correlation ?? existing.CorrelationId }));
        }

        var spec = new MeshTaskCreateSpec(
            Name: body.Name,
            Steps: body.Steps,
            RequiredBrickIds: body.RequiredBrickIds ?? Array.Empty<string>(),
            Affinity: body.Affinity,
            Priority: body.Priority,
            DeadlineUtc: body.DeadlineUtc,
            CorrelationId: correlation,
            IdempotencyKey: string.IsNullOrWhiteSpace(body.IdempotencyKey) ? null : body.IdempotencyKey.Trim());

        var created = await tasks.CreateAsync(spec, cancellationToken).ConfigureAwait(false);
        return Results.Ok(ToTaskResponse(created));
    }

    private static async Task<IResult> GetMeshTaskAsync(
        string taskId,
        [FromServices] IMeshTaskRegistry tasks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Results.BadRequest(new ProblemDetails { Title = "taskId is required" });
        var t = await tasks.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        return t is null ? Results.NotFound() : Results.Ok(ToTaskResponse(t));
    }

    private static async Task<IResult> ScheduleMeshTaskAsync(
        string taskId,
        [FromBody] MeshScheduleRequest? body,
        [FromServices] IMeshTaskPlacementService placement,
        [FromServices] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Results.BadRequest(new ProblemDetails { Title = "taskId is required" });
        var correlation = ResolveMeshCorrelationId(httpContextAccessor, body?.CorrelationId);
        var (ok, task, error) = await placement.TryScheduleAsync(
            taskId,
            body?.ScheduleIdempotencyKey,
            correlation,
            body?.LeaseSeconds,
            cancellationToken).ConfigureAwait(false);
        return ToPlacementResult(ok, task, error);
    }

    private static async Task<IResult> RetryMeshTaskAsync(
        string taskId,
        [FromBody] MeshScheduleRequest? body,
        [FromServices] IMeshTaskPlacementService placement,
        [FromServices] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Results.BadRequest(new ProblemDetails { Title = "taskId is required" });
        var correlation = ResolveMeshCorrelationId(httpContextAccessor, body?.CorrelationId);
        var (ok, task, error) = await placement.TryRetryAsync(
            taskId,
            body?.ScheduleIdempotencyKey,
            correlation,
            body?.LeaseSeconds,
            cancellationToken).ConfigureAwait(false);
        return ToPlacementResult(ok, task, error);
    }

    /// <summary>
    /// Placement error codes that mean "the state moved, try again" rather than "your request was
    /// malformed", and are therefore 409 rather than 400.
    /// </summary>
    /// <remarks>
    /// <para><c>schedule.conflict</c> is what <c>MeshTaskPlacementService</c> returns when a
    /// placement's precondition no longer held by the time the store applied it - two overlapping
    /// schedules of one task, or the timer-driven
    /// <c>MeshPendingTaskRebalancerBackgroundService</c> crossing an operator's call. Its first
    /// revision fell through to <c>BadRequest</c>, because only the idempotency conflict was
    /// special-cased, so a caller whose request was perfectly well formed was told not to retry.
    /// A single list and a single mapping helper, shared by both endpoints, is what stops the two
    /// drifting: they had identical five-line copies of this before.</para>
    /// </remarks>
    private static readonly string[] RetryableScheduleConflicts =
    [
        "schedule.idempotency_conflict",
        "schedule.conflict",
    ];

    /// <summary>Maps a placement outcome onto a status code.</summary>
    /// <param name="ok">Whether the placement succeeded.</param>
    /// <param name="task">The state the store holds, or null when the task does not exist.</param>
    /// <param name="error">The placement error code, when there is one.</param>
    /// <returns>200 with the task, 404, 409 for a retryable conflict, or 400.</returns>
    private static IResult ToPlacementResult(bool ok, MeshTaskState? task, string? error)
    {
        if (task is null)
            return Results.NotFound();
        if (ok)
            return Results.Ok(ToTaskResponse(task));
        if (error is not null && RetryableScheduleConflicts.Contains(error, StringComparer.Ordinal))
            return Results.Conflict(ToTaskResponse(task));
        return Results.BadRequest(new ProblemDetails { Title = error ?? "placement.failed", Detail = error });
    }

    private static async Task<IResult> ExportMeshKnowledgeAsync(
        [FromServices] MeshKnowledgeExportService export,
        [FromQuery] DateTimeOffset? since = null,
        [FromQuery] int maxAdaptations = 500,
        [FromQuery] int maxPatterns = 500,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await export.ExportAsync(since, maxAdaptations, maxPatterns, cancellationToken).ConfigureAwait(false);
        return Results.Json(payload);
    }

    private static async Task<IResult> ImportMeshKnowledgeAsync(
        [FromBody] Ashlar.Commercial.Fleet.Contracts.Models.MeshKnowledgeExportPayload? body,
        [FromServices] MeshKnowledgeImportService import,
        CancellationToken cancellationToken)
    {
        if (body is null)
            return Results.BadRequest(new ProblemDetails { Title = "Request body is required" });
        var result = await import.ImportAsync(body, cancellationToken).ConfigureAwait(false);
        return Results.Ok(new MeshKnowledgeImportResponse(
            result.AdaptationsApplied,
            result.AdaptationsSkipped,
            result.PatternsApplied,
            result.PatternsSkipped));
    }

    private static async Task<IResult> DownloadMeshTaskResultAsync(
        string taskId,
        [FromServices] IMeshTaskRegistry tasks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Results.BadRequest(new ProblemDetails { Title = "taskId is required" });
        var t = await tasks.GetAsync(taskId, cancellationToken).ConfigureAwait(false);
        if (t is null)
            return Results.NotFound();
        if (string.IsNullOrWhiteSpace(t.ResultHandle))
            return Results.BadRequest(new ProblemDetails { Title = "No result handle recorded for this task" });

        var path = Path.GetFullPath(t.ResultHandle.Trim());
        if (!File.Exists(path))
            return Results.NotFound();

        return Results.File(path, contentType: "application/octet-stream", fileDownloadName: Path.GetFileName(path));
    }

    private static async Task<IResult> ExtendMeshTaskLeaseAsync(
        string taskId,
        [FromBody] MeshLeaseExtendRequest? body,
        [FromServices] MeshTaskExecutionService execution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Results.BadRequest(new ProblemDetails { Title = "taskId is required" });
        if (body is null || string.IsNullOrWhiteSpace(body.LeaseToken))
            return Results.BadRequest(new ProblemDetails { Title = "leaseToken is required" });

        var (ok, task, error) = await execution.ExtendLeaseAsync(taskId, body.LeaseToken, body.ExtendSeconds, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return Results.NotFound();
        if (!ok)
            return Results.BadRequest(new ProblemDetails { Title = error ?? "lease.extend_failed" });
        return Results.Ok(ToTaskResponse(task));
    }

    private static async Task<IResult> MigrateMeshTaskForCheckpointAsync(
        string taskId,
        [FromBody] MeshMigrateForCheckpointRequest? body,
        [FromServices] MeshTaskExecutionService execution,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Results.BadRequest(new ProblemDetails { Title = "taskId is required" });
        if (body is null || string.IsNullOrWhiteSpace(body.LeaseToken) || string.IsNullOrWhiteSpace(body.CheckpointHandle))
            return Results.BadRequest(new ProblemDetails { Title = "leaseToken and checkpointHandle are required" });

        var (ok, task, error) = await execution.MigrateForCheckpointAsync(taskId, body.LeaseToken, body.CheckpointHandle, cancellationToken).ConfigureAwait(false);
        if (task is null)
            return Results.NotFound();
        if (!ok)
            return Results.BadRequest(new ProblemDetails { Title = error ?? "migrate.failed" });
        return Results.Ok(ToTaskResponse(task));
    }

    private static async Task<IResult> PatchMeshTaskStatusAsync(
        string taskId,
        [FromBody] MeshTaskStatusPatchRequest? body,
        [FromServices] MeshTaskExecutionService execution,
        [FromServices] IHttpContextAccessor httpContextAccessor,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(taskId))
            return Results.BadRequest(new ProblemDetails { Title = "taskId is required" });
        if (body is null || !Enum.IsDefined(typeof(MeshTaskStatus), body.Status))
            return Results.BadRequest(new ProblemDetails { Title = "Valid Status is required" });

        // The read, the lease comparison and the write used to live here, in that order, with the
        // database opened and closed between each of them. They now live in one transform the store
        // applies inside its write transaction; this method resolves the correlation id (which needs
        // the HttpContext) and maps the outcome.
        var correlation = ResolveMeshCorrelationId(httpContextAccessor, body.CorrelationId);
        var (ok, task, error) = await execution.ApplyStatusAsync(
            taskId,
            body.Status,
            body.LeaseToken,
            body.Reason,
            correlation,
            body.ResultSummary,
            body.ResultHandle,
            cancellationToken).ConfigureAwait(false);

        if (task is null)
            return Results.NotFound();
        if (!ok)
            return Results.Conflict(new ProblemDetails { Title = error ?? "lease.token_mismatch_or_missing" });
        return Results.Ok(ToTaskResponse(task));
    }

    private static string? ResolveMeshCorrelationId(IHttpContextAccessor? accessor, string? fromBody)
    {
        if (!string.IsNullOrWhiteSpace(fromBody))
            return fromBody.Trim();
        var ctx = accessor?.HttpContext;
        if (ctx?.Items.TryGetValue(CommercialMeshCorrelationItemKey, out var v) == true && v is string s && !string.IsNullOrWhiteSpace(s))
            return s.Trim();
        return null;
    }

    private static MeshFleetTrustTier? ParseFleetTrustTier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        return Enum.TryParse<MeshFleetTrustTier>(value.Trim(), true, out var tier) ? tier : null;
    }

    private static MeshFleetNodeResponse ToFleetResponse(MeshFleetNodeState n) =>
        new(
            n.PeerId,
            n.ApiBaseUrl,
            n.Labels,
            n.AdvertisedBrickIds,
            n.Drained,
            n.LastHeartbeatUtc,
            n.RegisteredAtUtc,
            n.ReportedQueueDepth,
            n.TrustTier.ToString(),
            n.Admitted,
            n.RegistrationKeyFingerprint);

    private static MeshTaskResponse ToTaskResponse(MeshTaskState t) =>
        new(
            t.TaskId,
            t.Name,
            t.Steps,
            t.RequiredBrickIds,
            t.Affinity,
            t.Priority,
            t.DeadlineUtc,
            t.Status.ToString(),
            t.AssignedPeerId,
            t.AssignedApiBaseUrl,
            t.PlacementReason,
            t.AttemptCount,
            t.CreatedAtUtc,
            t.LastScheduledAtUtc,
            t.CorrelationId,
            t.IdempotencyKey,
            t.LastScheduleIdempotencyKey,
            t.ResultSummary,
            t.ResultHandle,
            t.LeaseToken,
            t.LeaseOwnerPeerId,
            t.LeaseExpiresUtc,
            t.CheckpointHandle);

}
