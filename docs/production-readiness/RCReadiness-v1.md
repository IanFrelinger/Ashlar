# Release candidate readiness v1

Track after [Security readiness v1](SecurityReadiness-v1.md). **Plan:** [RC hardening plan v1](RCHardeningPlan-v1.md)

**Testing strategy:** RC proof is **workflow evidence + gates**, not repo-wide line coverage. See [Testing strategy pivot v1](../architecture/TestingStrategyPivot-v1.md) and the RC → workflow map in [Testing strategy tracking v1](../architecture/TestingStrategyTracking-v1.md#release-candidate-checklist--automation).

## Command

```bash
make rc-gate-full
# Faster local stack (skips Docker tiers):
ASHLAR_READY_SKIP_DOCKER=1 make ashlar-ready-gate
# Kernel coverage evidence before RC:
make kernel-coverage-gate
```

## Sign-off

- [x] `make waterproofing-gate-full` green (local, SHA `bec2a6ed`) [corrected 2026-09-30:
      UNEVIDENCED as a whole. `.github/workflows/waterproofing-gate.yml` only runs when dispatched
      by hand and has never run. Three of its four stages passed CI on `bec2a6ed`, but only after
      this sign-off was committed. See [Release sign-off corrections](ReleaseSignOff-v1.md#corrections-2026-09-30).]
- [x] [Release sign-off v1](ReleaseSignOff-v1.md) and [Rollback drill v1](RollbackDrill-v1.md) recorded
- [ ] `rc-gate-tier-d` strict on `master` (`RC_GATE_TRIGGER_GH=1 make rc-gate-tier-d`)
- [ ] GitHub RC workflows green on `master` (see `.ashlar/rc-gate/github-workflows.txt`)

## Next: post-RC waterproofing

```bash
make waterproofing-gate-full
```

See [Perf readiness](PerfReadiness-v1.md), [Compat readiness](CompatReadiness-v1.md), [DR readiness](DRReadiness-v1.md).

## Evidence rule (added 2026-09-30)

A sign-off line may be ticked as green only if it names a run id or check run on the SHA it
signs. Any other line reads NOT RUN, or CARRIED FORWARD with both SHAs. A readiness PASS must cite
a `Readiness summary` run whose verdict is `verified`. The full rule is in the
[production-readiness README](README.md#evidence-rule-for-readiness-records-added-2026-09-30).
The waterproofing line above is corrected in place. The evidence is set out in
[Release sign-off corrections](ReleaseSignOff-v1.md#corrections-2026-09-30).
