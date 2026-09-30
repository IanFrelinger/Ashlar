# Composition & mesh readiness v1

**Status: COMPOSITION & MESH GATE GREEN** (Tiers A–D, 2026-05-19)
[corrected 2026-09-30: all four tiers are SUPPORTED by CI, each carried forward from another
commit. Tiers A–C ran on 2026-05-22 on PR #110's head `ee8befa7`, whose tree is identical to this
sheet's commit `0ecb5bfc`. Tier D ran on `7457e6c5` on 2026-05-19. See [Corrections](#corrections-2026-09-30).]

Track after [Application Readiness v1](ApplicationReadiness-v1.md). **Plan:** [Composition & mesh hardening plan v1](CompositionMeshHardeningPlan-v1.md)

## Command

```bash
make composition-mesh-gate-full   # A–D (~2–5 min in-process; +10–15 min Docker mesh lab)
```

## Record

| Date | Gate | Result | Notes |
|------|------|--------|-------|
| 2026-05-19 | A–D local | **PASS** [corrected 2026-09-30: the local run left no artifact. CI backs each tier; see the table below.] | 44 pipeline + 6 CLI + 18 fleet tests; mesh-lab workers E2E |

## Tier summary

| Tier | Focus | Command | Status |
|------|--------|---------|--------|
| A | Pipeline composition | `make composition-mesh-gate-tier-a` | **PASS** (44 tests) [corrected 2026-09-30: SUPPORTED, carried forward ee8befa7 → 0ecb5bfc (PR #110 head, identical tree f1e22dfd), by Composition Mesh Gate run 26262666838 (2026-05-22). The filter selects exactly 44 test methods at 0ecb5bfc.] |
| B | CLI pipeline + mesh | `make composition-mesh-gate-tier-b` | **PASS** (6 tests) [corrected 2026-09-30: SUPPORTED, carried forward ee8befa7 → 0ecb5bfc (identical tree f1e22dfd), by the same run's "Tier A–C (PR default)" step. The filter selects exactly 6 test cases at 0ecb5bfc.] |
| C | Mesh fleet in-process | `make composition-mesh-gate-tier-c` | **PASS** (18 tests) [corrected 2026-09-30: SUPPORTED, carried forward ee8befa7 → 0ecb5bfc (identical tree f1e22dfd), by the same step. The filter selects exactly 18 test cases at 0ecb5bfc.] |
| D | Docker mesh workers | `make composition-mesh-gate-tier-d` | **PASS** [corrected 2026-09-30: SUPPORTED, carried forward 7457e6c5 → 0ecb5bfc. Mesh virtual lab gate run 26129468841 (2026-05-19) ran the workers profile and mesh-lab-verify.sh.] |

## Next: ship readiness

```bash
make ship-gate-full
```

See [Ship readiness v1](ShipReadiness-v1.md).

## Sign-off

- [x] `composition-mesh-gate-full` green (2026-05-19) [corrected 2026-09-30: backed by CI runs
      26262666838 and 26129468841, both carried forward from other commits; see
      [Corrections](#corrections-2026-09-30).]
- [x] Application + kernel gates green same week [corrected 2026-09-30: not fully backed; see
      [Application](ApplicationReadiness-v1.md#corrections-2026-09-30) and [Kernel](KernelReadiness-v1.md#corrections-2026-09-30) corrections.]

## Corrections (2026-09-30)

**Evidence rule from 2026-09-30.** A row may read PASS only if it names a run id or check run on
the SHA in this sheet's header. Any other row reads NOT RUN, or CARRIED FORWARD with both SHAs. A
readiness PASS must cite a `Readiness summary` run whose verdict is `verified`. The full rule and
the four labels are in the [production-readiness README](README.md#evidence-rule-for-readiness-records-added-2026-09-30).

**Which SHA.** This sheet names none. It was committed in `0ecb5bfc` (#110, 2026-05-22 01:17 UTC).
Its tree, `f1e22dfd`, is identical to PR #110's head `ee8befa7`. No commit contained these gate scripts on
2026-05-19. May CI logs have expired (HTTP 410), so only run and step conclusions survive. Counts
here are static counts of the xUnit test cases the filter selects in the net8.0 build at `0ecb5bfc`,
counting each theory row. All three match the counts this sheet recorded.

| Row | Label | Evidence |
|-----|-------|----------|
| Tier A | SUPPORTED, carried forward `ee8befa7` → `0ecb5bfc` (identical tree `f1e22dfd`) | Composition Mesh Gate run 26262666838 (pull_request, `ee8befa7`, 2026-05-22), steps "Tier A (pipeline composition)" and "Tier A–C (PR default)" success. The filter `FullyQualifiedName~…Tests.Pipelines` selects 44 test methods at `0ecb5bfc`. None is a theory, so that is 44 test cases, matching the sheet's 44. |
| Tier B | SUPPORTED, carried forward `ee8befa7` → `0ecb5bfc` (identical tree `f1e22dfd`) | The same run's "Tier A–C (PR default)" step runs `make composition-mesh-gate`, which covers Tiers A, B and C. At `0ecb5bfc` the filter selects 6 test cases, matching the sheet's 6. MeshDirectorCommandUriTests contributes 3: a two-row theory and one fact. `UnitTestBridgeTests` contributes 3 rows, one each for MeshCommandTests, OptimizeAgentClusterScriptTests and PipelineCommandTests. These are the only `UnitTestBase` suites whose names match the DisplayName clause. |
| Tier C | SUPPORTED, carried forward `ee8befa7` → `0ecb5bfc` (identical tree `f1e22dfd`) | The same step. The filter selects 16 test methods at `0ecb5bfc`. One is a theory with 3 inline rows (MeshLabWorkerExecutorClientTests), so the total is 18 test cases, matching the sheet's 18. |
| Tier D | SUPPORTED, carried forward `7457e6c5` → `0ecb5bfc` | Mesh virtual lab gate run 26129468841 (push, `7457e6c5`, 2026-05-19) ran `docker compose --profile workers … up` and then passed mesh-lab-verify.sh, the same steps as `MESH_LAB_E2E_WORKERS=1` run-mesh-lab-e2e.sh. The compose file and scripts are byte-identical at `0ecb5bfc`. Without Docker, this tier prints a skip line and exits 0 without printing PASS, so a local "PASS" alone would not show that it ran. |
