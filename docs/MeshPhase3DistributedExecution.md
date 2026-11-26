# Phase 3 — Distributed execution semantics (MVP)

Phase 3 adds **correlation propagation**, **idempotency** for mesh task creation and scheduling, **optional result handles** for completed tasks, and a minimal **result download** path on the director host.

**Depends on:** [MeshPhase0NorthStar.md](MeshPhase0NorthStar.md), [MeshPhase1ControlPlane.md](MeshPhase1ControlPlane.md), [MeshPhase2TransportAndAuth.md](MeshPhase2TransportAndAuth.md).

## Correlation

- **`X-Ashlar-Correlation-Id`** — If the client omits it on **`/api/mesh/*`** or **`POST /api/bricks/*/execute`**, **`MeshCorrelationMiddleware`** generates one and **echoes** it on the response. The value is also used when creating/updating mesh tasks if **`CorrelationId`** is not set in the JSON body.
- Task state stores **`CorrelationId`**; placement updates it when a non-empty correlation is passed to schedule/retry.

## Idempotency

| Operation | Behavior |
|-----------|----------|
| **`POST /api/mesh/tasks`** with **`IdempotencyKey`** | Returns the **existing** task with the same key (HTTP 200, same `taskId`). |
| **`POST /api/mesh/tasks/{id}/schedule`** with **`ScheduleIdempotencyKey`** | If the task is already **Assigned** and the key matches **`LastScheduleIdempotencyKey`**, returns the same assignment (no second placement). If the task is **Assigned** but a **different** key is sent, returns **409 Conflict** with the current task body. |
| **`POST .../retry`** | Same schedule key semantics after reassignment. |

Request bodies for schedule/retry are optional JSON: **`{ "scheduleIdempotencyKey": "...", "correlationId": "...", "leaseSeconds": 600 }`** (Phase 6 **`leaseSeconds`** optional, 60–86400).

Task responses include Phase 6 lease fields when assigned: **`leaseToken`**, **`leaseOwnerPeerId`**, **`leaseExpiresUtc`**, **`checkpointHandle`** (see [MeshPhase6LeasesAndCheckpoints.md](MeshPhase6LeasesAndCheckpoints.md)).

## Results and artifact pointers

- **`PATCH /api/mesh/tasks/{id}/status`** accepts optional **`resultSummary`** and **`resultHandle`** when moving to **Succeeded** or **Failed**. When the task is **Assigned** or **Running** and has a **`leaseToken`**, the body must include a matching **`leaseToken`** (otherwise **409 Conflict**).
- **`GET /api/mesh/tasks/{id}/result`** — If **`resultHandle`** is an **absolute file path** on the **director host** and the file exists, streams it as `application/octet-stream`. This is a **convenience** for small artifacts; large blobs should use object storage outside Ashlar (Phase 3 scope).
- Workers can instead include **`resultPayload`** (a JSON base64 string, at most **256 KiB** decoded) in a **Succeeded** status PATCH. This requires the current **`leaseToken`**, even for an otherwise authorized operator. Payload and terminal status are committed atomically in the task registry, including LiteDB; `/result` returns those bytes as `application/octet-stream` without a shared worker/director filesystem. Inline bytes are omitted from task-list/status responses.
- The optional mesh-lab executor forwards a successful brick's binary `output.payload` bytes, or its serialized output object when there is no binary payload. HTTP, malformed-response, brick-failure, and oversized-result errors produce **Failed**, not a synthetic success summary. The executor's existing no-brick lifecycle-only mode remains available and does not create a payload. The two worker implementations use the same 256 KiB bound.

## What is still not done

- **Knowledge replication** — see [MeshPhase4KnowledgeSync.md](MeshPhase4KnowledgeSync.md) for adaptation + pattern sync between peers.
- **Automatic** brick execution from the director after schedule (caller still dispatches to **`assignedApiBaseUrl`**).
- **Distributed** idempotency or correlation store (in-memory only).
- **Brick execute** request body correlation injection (clients should forward **`X-Ashlar-Correlation-Id`** to workers).

## Revision history

| Date | Change |
|------|--------|
| 2026-04-22 | Initial Phase 3 doc and API extensions. |
