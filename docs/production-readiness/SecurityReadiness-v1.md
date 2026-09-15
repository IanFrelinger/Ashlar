# Security & trust readiness v1

**Status: SECURITY GATE GREEN** (Tiers A–E, 2026-05-19)

Track after [Ops readiness v1](OpsReadiness-v1.md). **Plan:** [Security hardening plan v1](SecurityHardeningPlan-v1.md)

## Command

```bash
make security-gate-full
```

## Record

| Date | Gate | Result | Notes |
|------|------|--------|-------|
| 2026-05-19 | A–E local | **PASS** | Supply-chain: app + Hosting + Infrastructure; xunit deprecated warning |

## Tier summary

| Tier | Focus | Command | Status |
|------|--------|---------|--------|
| A | Trust core | `make security-gate-tier-a` | **PASS** |
| B | API security middleware | `make security-gate-tier-b` | **PASS** |
| C | Trust CLI | `make security-gate-tier-c` | **PASS** |
| D | Supply chain | `make security-gate-tier-d` | **PASS** |
| E | Air-gapped + safety | `make security-gate-tier-e` | **PASS** |

## Next: release candidate

```bash
make rc-gate-full
```

See [RC hardening plan v1](RCHardeningPlan-v1.md) and [Release candidate checklist v1](../ReleaseCandidateChecklist-v1.md).

## Sign-off

- [x] `security-gate-full` green (2026-05-19)
- [x] Tier D reports in `.ashlar/security-gate/` (no High/Critical in scanned surfaces) — **the
      reports themselves are not reviewable from this repository.** `.ashlar/` is in `.gitignore`
      and nothing under it is tracked, so the "no High/Critical" half of this line rests on a
      2026-05-19 local run that left no artefact anyone can now open. `security-gate-tier-d.sh`
      writes `vulnerable-packages.txt` and `deprecated-packages.txt` under that directory; re-run
      it and read them rather than taking this line's word for it. Re-signing this without a
      re-run would be signing for a scan nobody has seen.
- [x] Air-gapped tier verified (in-process safety + profile tests)
