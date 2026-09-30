# Phase 6 — Execution leases and checkpoint migration (MVP)

Phase 6 adds **time-bounded execution leases** on mesh tasks so a director can reclaim work when a worker stops heartening its lease, and a **migrate-for-checkpoint** path so a worker can voluntarily release placement after persisting progress.

**Depends on:** [MeshPhase1ControlPlane.md](MeshPhase1ControlPlane.md) through [MeshPhase5ElasticScheduling.md](MeshPhase5ElasticScheduling.md).

## Lease fields on tasks

After a successful **`POST /api/mesh/tasks/{id}/schedule`** (or retry), the task response includes:

- **`leaseToken`** — opaque secret the assignee must present to extend the lease, report **Running**, or call migrate.
- **`leaseOwnerPeerId`** — same as **`assignedPeerId`** in this MVP.
- **`leaseExpiresUtc`** — wall-clock expiry (UTC).

Optional **`leaseSeconds`** on schedule/retry bodies overrides the default (clamped **60–86400**). Placement clears **`checkpointHandle`** on each new assignment (fresh worker picks up from director state + worker-side checkpoint store).

## Worker API

| Method | Path | Purpose |
|--------|------|---------|
| `POST` | `/tasks/{taskId}/lease/extend` | Body `{ "leaseToken": "...", "extendSeconds": 120 }` — pushes **`leaseExpiresUtc`** forward (`extendSeconds` optional; clamped like default lease). |
| `POST` | `/tasks/{taskId}/migrate-for-checkpoint` | Body `{ "leaseToken": "...", "checkpointHandle": "..." }` — sets **`checkpointHandle`**, moves task to **Pending**, clears assignment and lease (director may re-place on any eligible node). |
| `PATCH` | `/tasks/{taskId}/status` | When status is **Assigned** or **Running** and a **`leaseToken`** exists on the task, the patch body must include a matching **`leaseToken`** or the API returns **409 Conflict** (`lease.token_mismatch_or_missing`). **Succeeded** / **Failed** clear lease and assignment. |

## Placement and expired leases

- **`TryScheduleAsync`** reclaims **Assigned** or **Running** tasks whose **`leaseExpiresUtc`** is in the past (sets **Pending**, clears lease) before attempting placement.
- While **Running**, schedule idempotency matches **Assigned** (same **`LastScheduleIdempotencyKey`** semantics); a **different** key returns **409** until the lease expires or the worker migrates.

## Background lease sweep

When **`Ashlar:Mesh:Checkpoint:SweepEnabled`** is true, **`MeshLeaseSweepBackgroundService`** periodically moves **Assigned** or **Running** tasks with expired **`leaseExpiresUtc`** back to **Pending** (same fields cleared as reclaim in placement). Disabled by default.

## Configuration (`Ashlar__Mesh__Checkpoint__*`)

| Key | Default | Description |
|-----|---------|-------------|
| `LeaseSeconds` | `1800` | Default lease length after assignment when **`leaseSeconds`** is omitted on schedule |
| `SweepEnabled` | `false` | Run background sweep |
| `SweepIntervalMinutes` | `1` | Delay between sweep rounds |

See **`docs/Configuration.md`**.

## Explicit limitations

- **Lease token in JSON** — transport must be TLS + mesh tokens (Phase 2); tokens are not redacted in **GET** task responses today.
- **No split-brain arbitration** — if two workers somehow hold the same token epoch, last writer wins on status PATCH; prefer short leases + extend.
- **Checkpoint handle** is an opaque string (path, URI, or object key); the director does not validate or load checkpoint bytes.

## Tests

- `Ashlar.Tests.Infrastructure` → `Tests/Fleet/MeshTaskPlacementServiceTests.cs`
- `Ashlar.Commercial.Tests.Fleet` → `MeshTaskExecutionServiceTests.cs` (moved there in #151; corrected 2026-09-30)

## Revision history

| Date | Change |
|------|--------|
| 2026-04-22 | Initial Phase 6: leases, extend/migrate API, running idempotency + expired lease reclaim, sweep. |
