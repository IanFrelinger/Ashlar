# Rollback drill v1

## Procedure

1. Record current deploy: image tag/digest or package version, git SHA.
2. Deploy previous known-good artifact to staging (or local mesh-lab stack).
3. Run smoke: `dotnet run --project application/src/Ashlar.CLI -- doctor`, pipeline validate, API `/health`.
4. If stateful: restore LiteDB/volume from backup taken before step 1.
5. Document elapsed time and data loss window.

## Last drill

### 0.3.0 closeout (2026-10-22)

| Field | Value |
|-------|-------|
| Date | 2026-10-22 |
| Operator | Cursor closeout agent (owner-authorized) |
| Environment | records-only sitting; no Docker on the agent VM |
| From version | package pin / tip `0.3.0` / `f1e0193e1c7abe37365260d6f5de0d14c98d5896` (not yet tagged) |
| To version | N/A for a new observed restore — see honesty note |
| RPO observed | Not re-measured this sitting |
| RTO observed | Not re-measured this sitting |
| Result | **RECORDED — no new local restore observed** |

Honesty note: no owner decisions-log row required a version-downgrade drill for 0.3.0. The closeout
agent could not run `make dr-gate-full` or mesh-lab (Docker unavailable). Prior restore-in-place
evidence remains the 2026-05-22 drill below. Package rollback path for this cut is: keep consuming
nuget.org `0.2.0` until `0.3.0` / `0.3.0-rc1` publish, then unpin / retarget if needed. Do not read
this row as a PASS of a fresh restore.

### Prior drill (2026-05-22)

| Field | Value |
|-------|-------|
| Date | 2026-05-22 |
| Operator | Ashlar DR gate (`make dr-gate-full`) |
| Environment | local + mesh-lab (Docker, `.env.mesh-lab`) |
| From version | `bec2a6ed` (then `master`) |
| To version | N/A (restore-in-place drill, not version downgrade) |
| RPO observed | 0 (LiteDB file copy; no data loss in pipeline DR tier A) |
| RTO observed | ~2 min (pipeline LiteDB restore + resume); ~95 s (mesh peer-a restart + verify) |
| Result | PASS |

#### Evidence (2026-05-22)

- **Pipeline LiteDB:** `dr-gate-tier-a` — backup → wipe → restore → `resume-run-id` completed successfully.
- **User knowledge store:** `dr-gate-tier-b` — `LiteDbUserKnowledgeLogStoreTests` passed.
- **Mesh director:** `dr-gate-tier-c` — fleet node and task `20260522001559314-7ab02c300811416c8669755002ae77f7` survived peer-a container restart.

Artifacts: `.ashlar/dr-gate/pipeline-restore.json`, `.ashlar/dr-gate/mesh-persistence.json`

## References

- [Release and promotion](ReleaseAndPromotion.md)
- `make dr-gate-full`
