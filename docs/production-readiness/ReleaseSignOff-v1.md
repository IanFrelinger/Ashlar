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
- [x] Security: exceptions file reviewed (`docs/exceptions.yaml` — no High/Critical entries)
- [x] Operations: rollback drill recorded ([Rollback drill v1](RollbackDrill-v1.md))

## Sign-off record

| Role | Name | Date | SHA |
|------|------|------|-----|
| Product | Ian Frelinger | 2026-05-22 | `bec2a6ed` |
| Engineering | Ashlar readiness gates (automated) | 2026-05-22 | `bec2a6ed` |
| Security | Ashlar security-gate (automated) | 2026-05-22 | `bec2a6ed` |
| Operations | DR gate + mesh-lab persistence | 2026-05-22 | `bec2a6ed` |

## Local gate evidence (2026-05-22)

| Gate | Command | Result |
|------|---------|--------|
| Waterproofing | `make waterproofing-gate-full` | PASS |
| Ship runtime | `SHIP_GATE_RUN_RUNTIME_GATE=1 make ship-gate-tier-d` | PASS |
| DR | `make dr-gate-full` | PASS (mesh director persistence verified) |

GitHub RC workflows: triggered on `master` after merge #114; run `RC_GATE_TRIGGER_GH=1 make rc-gate-tier-d` for strict verification.
