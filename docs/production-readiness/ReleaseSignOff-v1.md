# Release sign-off v1

Complete before tagging a release candidate.

> **This sheet has signed off one commit, and it is not a released one.** The record below is for
> `bec2a6ed`, dated 2026-05-22. That commit is 746 behind `master` and predates every tag this
> repository has cut — v0.1.0 (2026-08-30), v0.1.1 (2026-08-31) and v0.1.2 (2026-09-04) each
> shipped without a row here. The four ticks are that one sign-off, not a standing state, so do
> not read them as covering anything you can install.
>
> Verified 2026-09-14: all four items still point at something real — `docs/exceptions.yaml`
> exists with `exceptions: []`, [Rollback drill v1](RollbackDrill-v1.md) exists, and
> `waterproofing-gate-full` and `ship-gate-tier-d` are both still targets in the `Makefile`. What
> is stale is the scope, not the references.
>
> Add a row per release rather than re-ticking these boxes. Re-ticking loses the only thing this
> sheet is for, which is saying *which bytes* somebody accepted.

## Checklist

- [x] Product: scope and known limitations accepted (RC gate stack; strict GH workflows pending green on `master`)
- [x] Engineering: all automated gates green for target SHA (local: `waterproofing-gate-full`, `ship-gate-tier-d` with runtime gate)
      [corrected 2026-09-30: UNSUPPORTED-AT-THE-TIME. 8 of the 19 workflow runs on `bec2a6ed`
      concluded failure, including the Full Platform Readiness Gate and its `Readiness summary`
      job. When this box was ticked, 21 minutes after `bec2a6ed`, no run on that SHA had succeeded
      and four had already shown a failure. See [Corrections](#corrections-2026-09-30).]
- [x] Security: exceptions file reviewed (`docs/exceptions.yaml` — no High/Critical entries)
      [corrected 2026-09-30: SUPPORTED. `git show bec2a6ed:docs/exceptions.yaml` reads
      `exceptions: []`.]
- [x] Operations: rollback drill recorded ([Rollback drill v1](RollbackDrill-v1.md))
      [corrected 2026-09-30: the record exists; `fb544764` filled it in with this tick. The checks
      it lists passed CI on `bec2a6ed` only after sign-off (see the DR row below). It records a
      restore in place, not a rollback to an earlier version: "To version" reads N/A. Its local run
      left nothing to inspect. The reports went to gitignored `.nexo/`, and the mesh task id it
      cites does not date the run. See [Corrections](#corrections-2026-09-30).]

## Sign-off record

| Role | Name | Date | SHA |
|------|------|------|-----|
| Product | Ian Frelinger | 2026-05-22 | `bec2a6ed` |
| Engineering | Ashlar readiness gates (automated) [corrected 2026-09-30: see the Engineering item above; CI on this SHA did not come out green] | 2026-05-22 | `bec2a6ed` |
| Security | Ashlar security-gate (automated) [corrected 2026-09-30: SUPPORTED for Tiers A–B only, carried forward `2a1b4d1a` → `bec2a6ed` (PR #114 head, identical tree `b2996d47`), run 26262735412, after sign-off; Tiers C–E not run] | 2026-05-22 | `bec2a6ed` |
| Operations | DR gate + mesh-lab persistence [corrected 2026-09-30: DR Tiers A–B SUPPORTED by dr-gate run 26262738375; mesh director persistence SUPPORTED by Mesh virtual lab gate run 26262738372; both on this SHA, after sign-off] | 2026-05-22 | `bec2a6ed` |

## Local gate evidence (2026-05-22)

| Gate | Command | Result |
|------|---------|--------|
| Waterproofing | `make waterproofing-gate-full` | PASS [corrected 2026-09-30: UNEVIDENCED as a whole. The workflow has never run. Perf, compat and DR later passed CI on this SHA. RC policy has no CI run on this SHA. One compat step has selected zero tests since 2026-06-08.] |
| Ship runtime | `SHIP_GATE_RUN_RUNTIME_GATE=1 make ship-gate-tier-d` | PASS [corrected 2026-09-30: UNEVIDENCED. Ship Gate first ran on 2026-08-16.] |
| DR | `make dr-gate-full` | PASS (mesh director persistence verified) [corrected 2026-09-30: Tiers A–B SUPPORTED by run 26262738375, after sign-off. In that run, Tier C took its placeholder path. Mesh director persistence is SUPPORTED after sign-off by Mesh virtual lab gate run 26262738372 on this SHA, which ran the restart check that Tier C calls. The mesh task id in the rollback drill record does not date this local run. It dates the first creation of a task that the restart check reuses on every run against the same lab volume, so it cannot show which tree this run tested; see the Operations row.] |

GitHub RC workflows: triggered on `master` after merge #114; run `RC_GATE_TRIGGER_GH=1 make rc-gate-tier-d` for strict verification.

## Corrections (2026-09-30)

**Evidence rule from 2026-09-30.** A row may read PASS only if it names a run id or check run on
the SHA in this sheet's header (`bec2a6ed`). Any other row reads NOT RUN, or CARRIED FORWARD with
both SHAs. A readiness PASS must cite a `Readiness summary` run whose verdict is `verified`. The
full rule and the four labels are in the
[production-readiness README](README.md#evidence-rule-for-readiness-records-added-2026-09-30).
Add a new row for a new release; do not re-tick these.

**Timeline.** `bec2a6ed` (#114) was committed at 2026-05-22 01:17:43 UTC. This sign-off was
committed in `fb544764` (#115) at 01:38:36 UTC; PR #115 was opened and merged within two seconds.
May logs have expired (HTTP 410). Run and job conclusions and times survive.

| Item | Label | Evidence |
|------|-------|----------|
| Engineering: all automated gates green for target SHA | UNSUPPORTED-AT-THE-TIME | GitHub lists 19 workflow runs with head SHA `bec2a6ed`. 8 concluded failure: Full Platform Readiness Gate 26262738366, whose `Readiness summary` job also failed; Cross-Platform Tests 26262738322, with Build & Test failing on ubuntu, macOS and windows; Persistence Tests (Multi-OS) 26262738364; Runtime Release Gate 26262738371; Test Caching Multi-Environment 26262738380; Optimize Agent Cluster Gate 26262738370; and runs 26262737929 and 26262737725 of onboarding-quickstart-gate.yml and mapbox-tile-helpers-ci.yml. At 01:38:36 UTC none of the 19 had succeeded (the first success completed at 01:45:21). Four already showed a failure. mapbox-tile-helpers-ci.yml run 26262737725 at 01:17:45 and onboarding-quickstart-gate.yml run 26262737929 at 01:17:46 were each recorded as failed in the second they were created. Cross-Platform Tests 26262738322 had a failed windows job at 01:36:16; the run itself finished at 01:37:36, after its ubuntu job failed at 01:37:35. Persistence Tests 26262738364 had a failed macOS job at 01:36:26. The jobs API lists five Caching Tests jobs under 26262737725, all completed after 02:11, but that run's record already read failure at 01:17:45. |
| Waterproofing, `make waterproofing-gate-full` | UNEVIDENCED as a whole | `.github/workflows/waterproofing-gate.yml` triggers only on `workflow_dispatch`, and `gh run list --workflow=waterproofing-gate.yml` returns no runs (API `total_count` 0). The script runs perf, compat, DR and then rc-gate-tier-e. Three stages ran in CI on this SHA with the same scripts and success: perf-gate 26262738323 (done 01:49:07), dr-gate 26262738375 (01:50:21) and compat-gate 26262738367 (01:56:14). All three finished after the sign-off. The RC-policy stage (rc-gate-tier-e) has no CI run on this SHA: at `bec2a6ed`, rc-gate.yml is dispatch-only and runs Tiers A–D, and its first run was on 2026-05-28. |
| Compat, inside waterproofing | SUPPORTED after sign-off, by compat-gate run 26262738367 on `bec2a6ed`; LATER-DECAYED (one step) | compat-gate run 26262738367 (push, `bec2a6ed`) ran `COMPAT_GATE_SKIP_PRIOR=1 bash scripts/compat-gate.sh`, the line waterproofing-gate.sh runs, and succeeded at 01:56:14, 18 minutes after the sign-off. At `bec2a6ed`, compat Tier A's first step, filter `FullyQualifiedName~MeshTaskExecutionServiceTests.MigrateForCheckpointAsync`, selects 1 test method. `678d0718` (#151, 2026-06-08) moved that test into the commercial test tree, but the script still runs the filter against the infrastructure test project. CI runs 32612595835 (2026-08-23) and 34687917953 (2026-09-12), and a Linux devtest container run at `876cc527` on 2026-09-30, all print "No test matches the given testcase filter" for that step and then `compat-gate-tier-a: PASS`. The other two Tier A steps still ran 1 and 4 tests in each. |
| Ship runtime, `SHIP_GATE_RUN_RUNTIME_GATE=1 make ship-gate-tier-d` | UNEVIDENCED | ship-gate.yml was dispatch-only and first ran on 2026-08-16 (run 31972468094). No run exists on this SHA. |
| DR, `make dr-gate-full` | Tiers A–B SUPPORTED after sign-off; "(mesh director persistence verified)" SUPPORTED after sign-off, by the mesh-lab gate | dr-gate run 26262738375 on `bec2a6ed` ran `DR_GATE_SKIP_PRIOR=1 bash scripts/dr-gate.sh`. That is the same command, because 1 is the default. It succeeded 12 minutes after the sign-off. dr-gate-tier-c.sh checks mesh-director persistence only when .env.mesh-lab exists. That file is not in the tree and dr-gate.yml does not write it, so the CI run took the advisory path: a copy of a placeholder file, reported as "PASS (advisory)". The persistence claim is supported by a different workflow on the same SHA. At `bec2a6ed`, mesh-lab-verify.sh calls mesh-lab-verify-persistence.sh, the script dr-gate-tier-c.sh calls on its full path, unless MESH_LAB_SKIP_PERSISTENCE_VERIFY is set. mesh-lab-gate.yml does not set it. Its "Write lab env" step writes an API key and sets the director persistence provider to LiteDb. The persistence script skips only when the key is missing or the provider is not LiteDb. Otherwise it restarts peer-a and exits 1 if the seeded fleet node or task is gone. Mesh virtual lab gate run 26262738372 (push, `bec2a6ed`) passed its "Run verify scripts" step and succeeded at 02:23:46, 45 minutes after the sign-off. Its log has expired. Run 29668620853 on `d24b2ba9` (2026-07-19), whose persistence script is byte-identical, shows the same step printing "Mesh task survived restart". |
| Security sign-off (security-gate, automated) | SUPPORTED for Tiers A–B, carried forward `2a1b4d1a` → `bec2a6ed`, after sign-off | security-gate does not run on push, so there is no run on `bec2a6ed` itself. Run 26262735412 ran on PR #114's head `2a1b4d1a`, whose tree `b2996d47` is identical to `bec2a6ed`'s. Tiers A and B succeeded, finishing at 01:57:20 and 01:57:29, after the sign-off. The workflow did not run Tiers C–E. |
| Security: exceptions file reviewed | SUPPORTED | `git show bec2a6ed:docs/exceptions.yaml` shows `exceptions: []`. |
| Operations: rollback drill recorded | Record present; its checks SUPPORTED after sign-off; no rollback to an earlier version done | At `bec2a6ed`, [Rollback drill v1](RollbackDrill-v1.md) read "_not yet recorded_". `fb544764`, the commit that ticked this box, filled it in: operator `make dr-gate-full` on a local mesh lab, "From version `bec2a6ed`", "Result PASS". The checks it lists (DR Tiers A–B and the mesh director restart) passed CI on `bec2a6ed` only after sign-off; see the DR row. The local run itself cannot be inspected. The record cites reports under `.nexo/dr-gate/`, `.nexo/` is in `.gitignore` at `bec2a6ed`, and `fb544764` tracks no file under it. Its RPO and RTO figures come only from that run. It also reads "To version: N/A (restore-in-place drill, not version downgrade)", so step 2 of its own procedure, deploying the previous known-good artifact, was not done. The mesh task id it cites, `20260522001559314-7ab02c30…`, does not date the drill. At `bec2a6ed` the id is `DateTimeOffset.UtcNow:yyyyMMddHHmmssfff` plus a GUID (src/Nexo.Infrastructure/Fleet/LiteDbMeshTaskRegistry.cs:22), so peer-a created that task at 00:15:59 UTC by its own clock. That is before `bec2a6ed` (01:17:43) and before any DR gate script was committed (`edb20938`, authored 00:45:13). But mesh-lab-verify-persistence.sh always sends the idempotency key `mesh-lab-persist-idem`. `POST /api/mesh/tasks` returns the existing task for a known key (application/src/Nexo.API/Endpoints/NexoEndpoints.cs:935–940). peer-a keeps tasks in LiteDB on the named volume `mesh_lab_peer_a_data`, which neither `make mesh-lab-up` nor dr-gate-tier-c.sh removes, and the task registry has no delete. So every later run against that volume prints the same id. The id dates the first seeding of the task, not this drill, and cannot show which tree the drill ran on. |

These notes do not re-sign anything. For a release, add a row that names its SHA and the runs on
that SHA, including a `Readiness summary` run with verdict `verified`.
