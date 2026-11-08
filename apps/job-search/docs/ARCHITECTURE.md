# Architecture

This page is the job-search core: runs, screening, materials, the tracker. The hosted product, Job Search Studio, wraps
this core in a web host, a shared step library, a forge and an isolated step host; its architecture is in
[STUDIO.md](STUDIO.md) §4.

## Shape

```
                          ┌──────────────────────────── jobsearch (apps/job-search) ─────────────────────────────┐
  CLI / dispatcher ──►    │ RunEngine (per user: lease, idempotency key, steps, retries, summary)                │
  (every 15 min)          │   │                                                                                  │
                          │   ├─► TrackerService ◄──── user's Google Sheets (via that user's token) ───────────┐ │
                          │   │      import edits first; write system columns; conflicts                       │ │
                          │   ├─► jobs.list-board brick ──┐                                                    │ │
                          │   ├─► Ashlar WorkflowExecutor, one workflow per posting:                           │ │
                          │   │     jobs.get-posting → jobsearch.screen → jobsearch.recommendation* →          │ │
                          │   │     jobsearch.decide → jobsearch.tailor (→ generative-artifact) → .validate*   │ │
                          │   │           │                                                                     │ │
                          │   ├─► drafts → user's Google Drive (Docs + PDFs, app keys) ─────────────────────────┤ │
                          │   ├─► staleness, cleanup (move to Archive), freeze Submitted                        │ │
                          │   └─► SQLite system of record (every row has user_id)                               │ │
                          └───────────────│───────────────────────────────────────────────────────────────────┘ │
                                          ▼ EG-HTTP-09 (Ashlar.WebOperations: declared specs, request host evaluated) │
                                   employer APIs (Greenhouse, Lever, Ashby) and career pages (schema.org JobPosting)
```

Everything the brief calls an automation runs on Ashlar:

- **Bricks.** `web.call`, `web.learn`, `jobs.list-board` and `jobs.get-posting` live in `src/Ashlar.WebOperations`, Ashlar's native web-operations capability (declared, versioned site specs run over plain HTTP, with learning and repair). `jobsearch.screen`, `jobsearch.decide`, `jobsearch.tailor` and `jobsearch.validate-materials` live in the app and are registered through `AddAshlarSdk(...RegisterBrick<T>())` into the real `AddAshlar()` host; `generative-artifact` comes from `AddAshlarGenerativeAgents()` with the app's `jobsearch.cover-letter` agent profile. The host refuses to start if any is missing.
- **Certified bricks** (`*` above). `jobsearch.recommendation` and `jobsearch.claims-check` are not compiled by the app. `certified/` holds each one's source, witness, signed certification record and gate-emitted DLL; `CertifiedBricks` verifies the record against the pinned Ed25519 key and loads that DLL, and `CertifiedOverlayRegistry` puts the instances in front of the executor (F10). Screening only counts; the certified rule decides, and the assessment records its content hash. The claims check judges every draft in `jobsearch.validate-materials`, every model draft in the generative repair loop, and every refreshed PDF.
- **Generation.** With the user's opt-in, `jobsearch.tailor` asks the `generative-artifact` brick for a cover-letter body. It is called from the tailor brick, not placed as a node, because it declares no implementation (F9). The profile's post-validator is the certified claims check; a model draft is used only if the pipeline marks it verified, and it always requires the user's review.
- **Workflow.** Each posting is a `WorkflowDefinition` executed by Ashlar's `WorkflowExecutor` in a per-run DI scope (`Runs/PostingWorkflow.cs`). The executor's node events are written into the run summary and the user's activity log.
- **Egress.** Web operations go through `EgressHttp`, and each request's own host is evaluated before it is sent (EG-HTTP-09). The Google client is an `EgressHttp` client too.

The app supplies what Ashlar's workflow engine does not (see DOGFOOD-FINDINGS): durable per-user state, leases, idempotency, retries, the per-user schedule, and Drive/Sheets I/O.

## Data model (SQLite, `Store/Db.cs`)

| Table | Holds | Key points |
|---|---|---|
| `users` | id, email, name, time zone | |
| `profile_fields` | one row per field: value (NULL = unknown), `source` (resume/user), `status` (derived/confirmed/rejected), evidence | A new résumé never overwrites a field the user set, confirmed or rejected |
| `preferences` | JSON: targets, locations, arrangements, salary floor, boards, links, schedule, screening thresholds, draft limits | |
| `resume_versions` | text + sha per version, one current | Drafts record the sha they came from |
| `connections` | provider, account, `token_ref`, status (active / needs_reauth) | The token is in `secrets.json` (mode 0600), never in the database or snapshots |
| `access_grants` | manager, scopes, expiry, revocation | Created and revoked only by the user |
| `workspaces` | Drive root, folder ids, tracker and activity sheet ids | |
| `jobs` | posting facts, `source_ref` (the getJob call), `dedupe_key` UNIQUE per user, listing status + detail, first seen, last checked, last verified open, archived | |
| `job_checks` | every read: found / gone / error, detail, run | Evidence for each listing-status decision |
| `assessments` | recommendation, score, full assessment JSON (requirements with quotes, evidence, gaps, unknowns, blockers, notes) | |
| `applications` | **user-owned**: status, evidence, who set it and how, applied date, next steps, notes, submitted snapshot | Created as `not_applied`; the system never advances it |
| `documents` | per job and kind: editable doc and PDF, Drive id, content sha, the editable sha the PDF was rendered from, résumé sha, status (draft/stale/submitted/archived), reviewed at, validation JSON, `idempotency_key` UNIQUE | |
| `runs`, `run_locks` | run per idempotency key and attempt, status, summary; per-user lease | |
| `activity` | append-only per-user audit trail, exported to the Activity Log sheet | |
| `sheet_sync` | last value written or accepted per tracker cell | The baseline for detecting user edits |
| `conflicts` | cell, sheet value, system value, resolution | |

## What is authoritative where

| Information | Authority | How the other side follows |
|---|---|---|
| Profile, preferences, grants, connections | Database | The Drive profile doc is a dated, read-only export |
| Listing facts, fit, evidence, gaps, validation, links | Database | Written to system-owned tracker columns, **never over a user's edit** |
| Application status, applied date, next steps, notes | **The user**: sheet or CLI | Sheet edits are imported each run, before anything is written |
| Draft content | **The user's Doc in Drive** | The app hashes the exported text; a change makes the PDF stale |
| PDFs | Rendered by the app from the Doc | `refresh-pdf` re-renders from the current Doc; Drive keeps prior revisions |
| Submitted materials | Frozen copies in `Submitted` | Never moved, archived or regenerated |
| Version history | Drive revisions, plus optional git snapshots | |

## Reconciling the sheet (`Workspace/Tracker.cs`)

For every cell the app stores the last value it wrote or accepted (`sheet_sync`). On each sync, user edits are read first:

| Column owner | Sheet ≠ baseline? | App ≠ baseline? | Action |
|---|---|---|---|
| user | yes | no | import the sheet value (status needs a valid value; the edit is recorded as the user's own statement) |
| user | no | yes | write the app's value (e.g. a note set via the CLI) |
| user | yes | yes, and they differ | **conflict**: neither side is overwritten; the row is flagged |
| system | yes | — | **conflict**: the user's value stays; the app stops writing that cell until resolved |
| system | no | value changed | write |

`jobsearch conflicts --resolve ID --keep sheet|system` settles one. `keep sheet` on a system cell pins it permanently.
A row the user deletes is re-added and flagged for a week. Columns the user adds are kept and never reordered.

## Runs (`Runs/RunEngine.cs`)

1. **Lease and idempotency**, in one `BEGIN IMMEDIATE` transaction. A live lease means skip. A key that already succeeded means skip. A key that failed or partially failed runs again as attempt N+1. A `running` row whose lease expired is marked `abandoned`.
2. **Import sheet edits**; freeze materials for jobs the user just marked applied.
3. **Discover**: `jobs.list-board` per board, title filter, pasted links. Known jobs are re-checked about daily, and jobs owed drafts are re-queued at once. Reads are capped per run.
4. **Per posting**: the Ashlar workflow, then persist (dedupe, check row, listing-status rule, assessment), then publish drafts within the draft budget. Every Drive file has an app key, so a retry adopts what an interrupted attempt uploaded.
5. **Staleness**, **cleanup**, **tracker write**, **activity export**.

Failure handling: expired access stops the run as `blocked_auth` and marks the connection, and the dispatcher skips that user until they reconnect. Google `429`/rate-limit errors are retried 3 times with exponential backoff. A site's `rate` answer stops further reads from that site for the run. Any other provider error is recorded under "could not complete" and the run continues (`partial`). The summary records boards read, postings read, new jobs, status changes, drafts, stale PDFs, archives, frozen applications, imported edits, conflicts, and what could not be completed. Isolation comes from: each run using only its user's own token; every query filtering on `user_id`; each run having its own Ashlar DI scope (so workflow event streams never mix); and Drive refusing another account's file id.

## GitHub snapshots (optional, `Runs/Snapshots.cs`)

- One repository per user. A shared repository with a folder per user gives no separate access control, so it is never used.
- Contents: `tracker.csv`, `profile.json` (name, email, phone and location redacted unless asked), `documents.json` (ids, kinds, statuses, hashes; no content), `runs.json`, and a `.gitignore` that excludes token and secret files. Never: OAuth tokens, the secret store, document bodies, résumé text (opt-in only).
- A commit is made only when something changed; its message counts jobs, application activity and documents.
- Push: only when the caller says the remote is confirmed private, which it checks with the GitHub API under the user's own authorization. A URL carrying a credential is refused; git's credential helper supplies access.
- Restore: `restore --commit` prints the user-owned fields that differ (status, applied date, next steps, notes), and `--apply` applies them as the user, logged. System-owned fields are re-derived by the next run, not restored. Document bodies are restored from Drive revision history or the `Submitted` copies.
