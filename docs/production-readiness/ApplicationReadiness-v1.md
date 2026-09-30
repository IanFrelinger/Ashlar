# Application Readiness v1

**Status: APPLICATION GATE GREEN** (Tiers A–D, 2026-05-19)
[corrected 2026-09-30: Tiers A and D are SUPPORTED, carried forward from CI on PR #110's head
`ee8befa7`, whose tree is identical to this sheet's commit `0ecb5bfc`. Tier B's test step
could reach only one of the four suites it names. No CI run of Tier C was found, and it now
selects no tests. See [Corrections](#corrections-2026-09-30).]

Track after [Kernel Readiness v1](KernelReadiness-v1.md). **Plan:** [Application Hardening Plan v1](ApplicationHardeningPlan-v1.md)

## Command

```bash
make application-gate-full   # after make kernel-gate-full (~2–3 min without Docker rebuild)
```

## Record

| Date | Gate | Result | Notes |
|------|------|--------|-------|
| 2026-05-19 | A–D local | **PASS** [corrected 2026-09-30: no single run backs this. Evidence per tier is in the table below.] | agent-server `/health` + `/api/status` |

## Tier summary

| Tier | Focus | Command | Status |
|------|--------|---------|--------|
| A | Product build + CLI validate | `make application-gate-tier-a` | **PASS** [corrected 2026-09-30: SUPPORTED, carried forward ee8befa7 → 0ecb5bfc (PR #110 head, identical tree f1e22dfd), by Application Gate run 26262666898 (2026-05-22, step Tier A).] |
| B | CLI tests + doctor | `make application-gate-tier-b` | **PASS** [corrected 2026-09-30: UNSUPPORTED-AT-THE-TIME in part. Three of the four suites its filter names had no xUnit test the filter could select. The doctor leg cannot fail the tier. No CI run found.] |
| C | In-process API HTTP | `make application-gate-tier-c` | **PASS** [corrected 2026-09-30: UNEVIDENCED then (4 test methods selectable; no CI run found). LATER-DECAYED: it has selected no tests since 2026-08-17 and still prints PASS.] |
| D | Agent-server Compose | `make application-gate-tier-d` | **PASS** [corrected 2026-09-30: SUPPORTED, carried forward ee8befa7 → 0ecb5bfc (PR #110 head, identical tree f1e22dfd), by Prod dry run (Compose) run 26262666791, "agent-server stack" (2026-05-22).] |

## Next: composition & mesh

```bash
make composition-mesh-gate-full
```

See [Composition & mesh readiness v1](CompositionMeshReadiness-v1.md).

## Sign-off

- [x] `application-gate-full` green (2026-05-19) [corrected 2026-09-30: see the tier table. B and C
      are not backed.]
- [x] Kernel gate green same day [corrected 2026-09-30: see [Kernel readiness corrections](KernelReadiness-v1.md#corrections-2026-09-30).]

## Corrections (2026-09-30)

**Evidence rule from 2026-09-30.** A row may read PASS only if it names a run id or check run on
the SHA in this sheet's header. Any other row reads NOT RUN, or CARRIED FORWARD with both SHAs. A
readiness PASS must cite a `Readiness summary` run whose verdict is `verified`. The full rule and
the four labels are in the [production-readiness README](README.md#evidence-rule-for-readiness-records-added-2026-09-30).

**Which SHA.** This sheet names none. It was committed in `0ecb5bfc` (#110, 2026-05-22 01:17 UTC).
Its tree, `f1e22dfd`, is identical to PR #110's head `ee8befa7`. The gate scripts it cites did not exist in
any commit on 2026-05-19; [Kernel readiness](KernelReadiness-v1.md#corrections-2026-09-30) gives
the dates. May CI logs have expired (HTTP 410), so counts are static counts of xUnit test methods
the filter selects in the net8.0 build at `0ecb5bfc`.

| Row | Label | Evidence |
|-----|-------|----------|
| Tier A | SUPPORTED, carried forward `ee8befa7` → `0ecb5bfc` (identical tree `f1e22dfd`) | Application Gate run 26262666898 (pull_request, `ee8befa7`, 2026-05-22), step "Tier A" success. At `0ecb5bfc` the workflow ran Tiers B–D only on `workflow_dispatch`, and none of its 468 runs is a dispatch. |
| Tier B | UNSUPPORTED-AT-THE-TIME (test leg, in part) | The filter names ValidateCommandTests, MeshDirectorCommandUriTests, TrustCommandTests and WorkflowCommandTests. At `0ecb5bfc`, three of these (ValidateCommandTests, TrustCommandTests, WorkflowCommandTests) derive from `UnitTestBase` and declare no xUnit fact. The CLI test project runs such suites only through `UnitTestBridgeTests.Framework_unit_test_passes`, whose fully qualified name contains none of those class names. So only MeshDirectorCommandUriTests (2 methods) could run. Security Tier C shows the same mechanism in CI: run 31982500018 (2026-08-17) printed "No test matches the given testcase filter `FullyQualifiedName~TrustCommandTests`" and then PASS. The `doctor --json` leg fails the tier only when `APPLICATION_GATE_STRICT_DOCTOR=1`. No CI run of this tier from May was found. |
| Tier C | UNEVIDENCED then; LATER-DECAYED | At `0ecb5bfc` the filter selects 4 methods: ApiDevelopmentHostDiTests (1) and FrameworkVirtualProdDemosTests (3). No CI run of it was found: the Application Gate workflow ran it only on dispatch and was never dispatched. The net8.0 build this tier uses dropped Tests/VirtualProduction on 2026-05-23 (`9579c813`) and Tests/API on 2026-08-17 (`e6b5579f`). On 2026-09-30 the script ran at `876cc527` in the Linux devtest container. It printed "No test matches the given testcase filter `FullyQualifiedName~ApiDevelopmentHostDiTests\|FullyQualifiedName~FrameworkVirtualProdDemosTests`", then `application-gate-tier-c: PASS`, and exited 0. |
| Tier D | SUPPORTED, carried forward `ee8befa7` → `0ecb5bfc` (identical tree `f1e22dfd`) | Prod dry run (Compose) run 26262666791 (pull_request, `ee8befa7`, 2026-05-22), job "agent-server stack" success. It runs the same prod-dry-run script with `--agent-server`. The tier itself prints PASS without running anything when Docker is absent. |
