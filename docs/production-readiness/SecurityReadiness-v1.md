# Security & trust readiness v1

**Status: SECURITY GATE GREEN** (Tiers A–E, 2026-05-19)
[corrected 2026-09-30: Tiers A and B are SUPPORTED, carried forward from CI on PR #110's head
`ee8befa7`, whose tree is identical to this sheet's commit `0ecb5bfc`. But Tier B has
selected zero tests since 2026-08-17. Tier C's unit-suite step selected zero tests even then. Tiers
D and E are UNEVIDENCED. See [Corrections](#corrections-2026-09-30).]

Track after [Ops readiness v1](OpsReadiness-v1.md). **Plan:** [Security hardening plan v1](SecurityHardeningPlan-v1.md)

## Command

```bash
make security-gate-full
```

## Record

| Date | Gate | Result | Notes |
|------|------|--------|-------|
| 2026-05-19 | A–E local | **PASS** [corrected 2026-09-30: no single run backs this. Evidence per tier is in the table below.] | Supply-chain: app + Hosting + Infrastructure; xunit deprecated warning |

## Tier summary

| Tier | Focus | Command | Status |
|------|--------|---------|--------|
| A | Trust core | `make security-gate-tier-a` | **PASS** [corrected 2026-09-30: SUPPORTED, carried forward ee8befa7 → 0ecb5bfc (PR #110 head, identical tree f1e22dfd), by Security Gate run 26262666843 (2026-05-22, step Tier A). 34 test methods were selectable.] |
| B | API security middleware | `make security-gate-tier-b` | **PASS** [corrected 2026-09-30: SUPPORTED then, carried forward ee8befa7 → 0ecb5bfc (identical tree f1e22dfd), by the same run (step Tier B; 24 methods selectable). LATER-DECAYED: zero tests since e6b5579f (2026-08-17). Run 36642632648 (2026-09-29) prints "No test matches" and then PASS.] |
| C | Trust CLI | `make security-gate-tier-c` | **PASS** [corrected 2026-09-30: UNSUPPORTED-AT-THE-TIME for its unit-suite step, whose filter could select no test. The trust boundary and dashboard JSON smokes are UNEVIDENCED.] |
| D | Supply chain | `make security-gate-tier-d` | **PASS** [corrected 2026-09-30: UNEVIDENCED. The Security Gate workflow first ran it on 2026-08-17, and no report survives; see the sign-off note below.] |
| E | Air-gapped + safety | `make security-gate-tier-e` | **PASS** [corrected 2026-09-30: UNEVIDENCED. 30 test methods were selectable, but the Security Gate workflow first ran it on 2026-08-17.] |

## Next: release candidate

```bash
make rc-gate-full
```

See [RC hardening plan v1](RCHardeningPlan-v1.md) and [Release candidate checklist v1](../ReleaseCandidateChecklist-v1.md).

## Sign-off

- [x] `security-gate-full` green (2026-05-19) [corrected 2026-09-30: only Tiers A and B are
      backed, and B has since decayed; see [Corrections](#corrections-2026-09-30).]
- [x] Tier D reports in `.ashlar/security-gate/` (no High/Critical in scanned surfaces) — **the
      reports themselves are not reviewable from this repository.** `.ashlar/` is in `.gitignore`
      and nothing under it is tracked, so the "no High/Critical" half of this line rests on a
      2026-05-19 local run that left no artefact anyone can now open. `security-gate-tier-d.sh`
      writes `vulnerable-packages.txt` and `deprecated-packages.txt` under that directory; re-run
      it and read them rather than taking this line's word for it. Re-signing this without a
      re-run would be signing for a scan nobody has seen.
- [x] Air-gapped tier verified (in-process safety + profile tests) [corrected 2026-09-30:
      UNEVIDENCED; see Tier E below.]

## Corrections (2026-09-30)

**Evidence rule from 2026-09-30.** A row may read PASS only if it names a run id or check run on
the SHA in this sheet's header. Any other row reads NOT RUN, or CARRIED FORWARD with both SHAs. A
readiness PASS must cite a `Readiness summary` run whose verdict is `verified`. The full rule and
the four labels are in the [production-readiness README](README.md#evidence-rule-for-readiness-records-added-2026-09-30).

**Which SHA.** This sheet names none. It was committed in `0ecb5bfc` (#110, 2026-05-22 01:17 UTC).
Its tree, `f1e22dfd`, is identical to PR #110's head `ee8befa7`. No commit contained the security-gate scripts
on 2026-05-19. At `0ecb5bfc`, pull requests ran only Tiers A and B, and Tiers C–E ran only on
`workflow_dispatch`. The first dispatch was run 31982500018 on 2026-08-17. May CI logs have expired
(HTTP 410). Counts marked "selectable" are static counts of xUnit test methods the filter selects
in the net8.0 build at `0ecb5bfc`. Counts marked "passed" come from CI logs.

| Row | Label | Evidence |
|-----|-------|----------|
| Tier A | SUPPORTED, carried forward `ee8befa7` → `0ecb5bfc` (identical tree `f1e22dfd`) | Security Gate run 26262666843 (pull_request, `ee8befa7`, 2026-05-22), step "Tier A (trust core)" success. 34 methods were selectable at `0ecb5bfc`. For comparison, the tier passed 97 tests in run 31982500018 (2026-08-17) and 97 in run 36642632648 (2026-09-29). |
| Tier B | SUPPORTED then, carried forward `ee8befa7` → `0ecb5bfc` (identical tree `f1e22dfd`); LATER-DECAYED | Same run, step "Tier B (API security)" success. At `0ecb5bfc` the filter selects 24 methods from the four API security suites under Tests/API. Run 31982500018 (2026-08-17) passed 39 tests. `e6b5579f` (committed 2026-08-17 22:27 -04:00) removed Tests/API from the net8.0 build, which is the build this tier tests. Since then, run 32608958552 (2026-08-23) and run 36642632648 (2026-09-29) both print "No test matches the given testcase filter" and then `security-gate-tier-b: PASS`. The fifth class it names, SecurityAnalysisRuleTests, is a `UnitTestBase` suite with no xUnit test. |
| Tier C | UNSUPPORTED-AT-THE-TIME (unit-suite step) | At `0ecb5bfc`, TrustCommandTests derives from `UnitTestBase` and declares no xUnit test. The CLI test project runs such suites only through `UnitTestBridgeTests.Framework_unit_test_passes`, whose fully qualified name does not contain "TrustCommandTests". The script was unchanged on 2026-08-17. Its first CI run, 31982500018, printed "No test matches the given testcase filter `FullyQualifiedName~TrustCommandTests`" and then `security-gate-tier-c: PASS`. The trust boundary and dashboard JSON smokes have no May run (UNEVIDENCED). The filter now selects through `UnitTestBridgeTests` with a DisplayName clause, and run 36642632648 passed 61 tests. |
| Tier D | UNEVIDENCED | The workflow has no May run of it. Its first CI execution was run 31982500018 on `5d0e2fd3` (2026-08-17), a different SHA, which printed `security-gate-tier-d: PASS`. The sign-off note above explains why the 2026-05-19 reports cannot be reviewed. |
| Tier E | UNEVIDENCED | 30 methods were selectable at `0ecb5bfc`, but the workflow has no May run of it. Its first CI execution, run 31982500018 (2026-08-17), passed 50 tests on a different SHA. |
