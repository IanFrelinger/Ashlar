# Ship readiness v1

**Status: SHIP GATE GREEN** (Tiers A–D default profile, 2026-05-19)
[corrected 2026-09-30: the Ship Gate workflow could only be dispatched by hand, and its first run
was 2026-08-16. Tier A is SUPPORTED, carried forward from an equivalent 2026-05-19 run on
`7457e6c5`. Tiers B–D are UNEVIDENCED.
See [Corrections](#corrections-2026-09-30).]

Track after [Composition & mesh readiness v1](CompositionMeshReadiness-v1.md). **Plan:** [Ship hardening plan v1](ShipHardeningPlan-v1.md)

## Command

```bash
make ship-gate-full
```

## Record

| Date | Gate | Result | Notes |
|------|------|--------|-------|
| 2026-05-19 | A–D local | **PASS** [corrected 2026-09-30: only Tier A is backed, by an equivalent run. See the table below.] | Runtime bundle opt-in (`SHIP_GATE_RUN_RUNTIME_GATE=1`) |

## Tier summary

| Tier | Focus | Status |
|------|--------|--------|
| A | Production Readiness Gate v1 CLI + LiteDB resume | **PASS** [corrected 2026-09-30: SUPPORTED, carried forward 7457e6c5 → 0ecb5bfc. Production Readiness Gate v1 run 26129468793 (2026-05-19, three OSes) ran the steps this tier mirrors.] |
| B | ProdStyle + smoke + doctor | **PASS** [corrected 2026-09-30: UNEVIDENCED. No CI run found; 68 + 9 test methods were selectable, and the doctor leg cannot fail the tier.] |
| C | Release preflight (`0.0.0-ship-gate-local`) | **PASS** [corrected 2026-09-30: UNEVIDENCED. No CI run found.] |
| D | Doctor sign-off | **PASS** [corrected 2026-09-30: UNEVIDENCED. No CI run found.] |

## Next: operations & dogfood

```bash
make ops-gate-full
```

See [Ops readiness v1](OpsReadiness-v1.md). Full stack: `make ashlar-ready-gate`.

## Sign-off

- [x] `ship-gate-full` green (2026-05-19) [corrected 2026-09-30: Tiers B–D are UNEVIDENCED; see
      [Corrections](#corrections-2026-09-30).]
- [x] Composition + mesh gates green same week [corrected 2026-09-30: SUPPORTED, carried forward:
      Composition Mesh Gate run 26262666838 (`ee8befa7` → `0ecb5bfc`, identical tree `f1e22dfd`)
      and Mesh virtual lab gate run 26129468841 (`7457e6c5` → `0ecb5bfc`); see
      [Composition & mesh corrections](CompositionMeshReadiness-v1.md#corrections-2026-09-30).]

## Corrections (2026-09-30)

**Evidence rule from 2026-09-30.** A row may read PASS only if it names a run id or check run on
the SHA in this sheet's header. Any other row reads NOT RUN, or CARRIED FORWARD with both SHAs. A
readiness PASS must cite a `Readiness summary` run whose verdict is `verified`. The full rule and
the four labels are in the [production-readiness README](README.md#evidence-rule-for-readiness-records-added-2026-09-30).

**Which SHA.** This sheet names none. It was committed in `0ecb5bfc` (#110, 2026-05-22 01:17 UTC),
whose tree is identical to PR #110's head `ee8befa7`. No commit contained the ship-gate scripts on
2026-05-19. At `0ecb5bfc`, `.github/workflows/ship-gate.yml` triggers only on `workflow_dispatch`.
Its first run was 31972468094 on 2026-08-16, and there are 117 runs in all. The workflow has
no May run of any tier.

| Row | Label | Evidence |
|-----|-------|----------|
| Tier A | SUPPORTED, carried forward `7457e6c5` → `0ecb5bfc` | The tier script says it mirrors Production Readiness Gate v1. Run 26129468793 of that workflow (push, `7457e6c5`, 2026-05-19) passed on ubuntu, macOS and windows. It ran the same three builds, the same two host DI smoke tests, the same CLI pipeline validate/run/fallback and the same LiteDB cross-process resume. Outside docs, gate scripts, workflows and the Makefile, `7457e6c5` and `0ecb5bfc` differ only in global.json, two helper scripts and one test file. |
| Tier B | UNEVIDENCED | No CI run found. At `0ecb5bfc`, `make test-prod-style` selects 68 test methods and BaseFrameworkSmokeTests selects 9. The `doctor --json` leg fails the tier only when `SHIP_GATE_STRICT_DOCTOR=1`. |
| Tier C | UNEVIDENCED | No CI run of the local release preflight was found. |
| Tier D | UNEVIDENCED | No CI run found. In the default profile the sheet records, this tier is `doctor --json`; the release bundle runs only with `SHIP_GATE_RUN_RUNTIME_GATE=1`. |
