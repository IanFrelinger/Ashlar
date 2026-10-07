# Operations & dogfood readiness v1

**Status: OPS GATE GREEN** (Tiers A–C, E default profile, 2026-05-19)
[corrected 2026-09-30: UNEVIDENCED. The Ops Gate workflow could only be dispatched by hand. Its
first run was 2026-08-16 and it failed. See [Corrections](#corrections-2026-09-30).]

Track after [Ship readiness v1](ShipReadiness-v1.md). **Plan:** [Ops hardening plan v1](OpsHardeningPlan-v1.md)

## Command

```bash
make ops-gate-full
make ashlar-ready-gate   # full stack (use ASHLAR_READY_SKIP_DOCKER=1 locally for speed)
```

## Record

| Date | Gate | Result | Notes |
|------|------|--------|-------|
| 2026-05-19 | A–C, E local | **PASS** [corrected 2026-09-30: UNEVIDENCED. The Ops Gate workflow first ran on 2026-08-16.] | Tier D mesh/chaos optional |

## Tier summary

| Tier | Focus | Status |
|------|--------|--------|
| A | Dogfood blocks 1–6 | **PASS** [corrected 2026-09-30: UNEVIDENCED. Each block's filter selects one test method; no CI run found.] |
| B | Dogfood blocks 7–9 + IPC mesh | **PASS** [corrected 2026-09-30: UNEVIDENCED. Each filter selects one test method; no CI run found.] |
| C | Closed-loop self-improvement | **PASS** [corrected 2026-09-30: UNEVIDENCED. The filter selects one test method; no CI run found.] |
| D | Mesh deep / chaos-lite | optional |
| E | Oh-shit demo (quick) | **PASS** [corrected 2026-09-30: UNEVIDENCED. No CI run found.] |

## Next: security & trust

```bash
make security-gate-full
```

See [Security readiness v1](SecurityReadiness-v1.md).

## Sign-off

- [x] `ops-gate` default tiers green (2026-05-19) [corrected 2026-09-30: UNEVIDENCED; see
      [Corrections](#corrections-2026-09-30).]
- [x] Ship gate green same week [corrected 2026-09-30: only Tier A is backed; see
      [Ship corrections](ShipReadiness-v1.md#corrections-2026-09-30).]

## Corrections (2026-09-30)

**Evidence rule from 2026-09-30.** A row may read PASS only if it names a run id or check run on
the SHA in this sheet's header. Any other row reads NOT RUN, or CARRIED FORWARD with both SHAs. A
readiness PASS must cite a `Readiness summary` run whose verdict is `verified`. The full rule and
the four labels are in the [production-readiness README](README.md#evidence-rule-for-readiness-records-added-2026-09-30).

**Which SHA.** This sheet names none. It was committed in `0ecb5bfc` (#110, 2026-05-22 01:17 UTC).
No commit contained the ops-gate scripts on 2026-05-19. At `0ecb5bfc`,
`.github/workflows/ops-gate.yml` triggers only on `workflow_dispatch`. Its first run was 31972469016
on 2026-08-16, which failed. Of its 97 runs, 3 succeeded. The workflow has no May run of any tier.

**Could the tiers have run tests then?** Yes. At `0ecb5bfc` each dogfood filter (DogfoodBlock1Tests
through DogfoodBlock9Tests, DogfoodBlock9LocalIpcTests, DogfoodClosedLoopTests) selects exactly one
xUnit test method in the net8.0 build. These rows are therefore not disproven. They are UNEVIDENCED:
the local run left no artifact, and no other run found covers these tiers. Under the rule above,
they read NOT RUN. Tier E (`oh-shit-demo.sh --quick --no-build`) has no CI run that was found either.
