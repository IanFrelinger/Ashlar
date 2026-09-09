# Coverage gates v1

Automated line-coverage floors for kernel assemblies. These gates complement functional workflows (`kernel-gate`, `rc-gate`, `production-readiness-gate-v1`) and block coverage regressions on merge.

**Strategy context:** floors are one layer in the [testing strategy pivot v1](../architecture/TestingStrategyPivot-v1.md) — not a substitute for ProdStyle, mesh-lab, or RC evidence.

## CI workflows

| Workflow | Job id | Assemblies | Line threshold |
|----------|--------|------------|----------------|
| [Kernel coverage gate](../../.github/workflows/kernel-coverage-gate.yml) | `kernel-coverage` | Domain + Infrastructure + Core.Application | **100% / 80% / 67%** |

The Infrastructure floor is **80%** as enforced by `scripts/ci/kernel-coverage-gate.sh` (`INFRA_COVERAGE_THRESHOLD` default; measured ~80.3%, target 83 — the earlier 83% figure was never measured against a completed run, see [KernelCoverageGate-Findings.md](KernelCoverageGate-Findings.md)). `kernel-coverage` is not currently a required status check on `master`; branch protection requires `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, and `Readiness summary` (see [CiGateInventory.md](../CiGateInventory.md)). The coverage gate is path-filtered, so it needs an always-report job before it can be required (see [TestingModel.md](../architecture/TestingModel.md)).

## Local verification

```bash
bash scripts/ci/kernel-coverage-gate.sh
# or
make kernel-coverage-gate
```

Override thresholds for experiments:

```bash
INFRA_COVERAGE_THRESHOLD=90 APP_COVERAGE_THRESHOLD=70 bash scripts/ci/kernel-coverage-gate.sh
```

## Scope and exclusions

- **In scope:** deterministic unit-testable kernel code under `src/Ashlar.Core.Domain`, `src/Ashlar.Core.Application`, and `src/Ashlar.Infrastructure`.
- **Out of scope for 100% line coverage (integration / environment):** Docker engine adapters, Postgres ephemeral provisioners, Ollama/RunPod live routing, Playwright hosts. These paths are exercised by tiered gates (`kernel-gate-tier-e`, mesh-lab, prod-style) rather than line-coverage alone.
- **Ratchet policy:** Raise `INFRA_COVERAGE_THRESHOLD` and `APP_COVERAGE_THRESHOLD` in `scripts/ci/kernel-coverage-gate.sh` when gap tests land; do not lower floors without release sign-off.

## Release readiness link

For the full RC checklist (workflows, evidence, rollback), see [Release candidate checklist v1](../ReleaseCandidateChecklist-v1.md) and [Testing and quality gates](TestingAndQualityGates.md).
