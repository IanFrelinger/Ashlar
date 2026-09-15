# Testing strategy pivot — tracking v1

Living checklist for [Testing strategy pivot v1](TestingStrategyPivot-v1.md). Update checkboxes when items complete.

**Reviewers:** [Testing review guide v1](TestingReviewGuide-v1.md)

---

## Phase 0 — Documentation

- [x] `TestingStrategyPivot-v1.md` linked from `docs/Testing.md`
- [x] Linked from `docs/architecture/TestingModel.md`
- [x] Linked from `docs/production-readiness/CoverageGates-v1.md`
- [x] Linked from `docs/production-readiness/README.md`
- [x] PR template includes blast-radius testing table
- [x] CONTRIBUTING.md references pivot
- [x] [Testing review guide v1](TestingReviewGuide-v1.md)

## Phase 1 — Coverage policy

- [x] `kernel-coverage-gate.yml` + `scripts/ci/kernel-coverage-gate.sh`
- [x] Domain at 100% threshold (`core-domain-coverage`, folded into `kernel-coverage-gate` 2026-08-16)
- [x] `CoverageGates-v1.md` exclusions documented
- [x] Branch protection documented (`docs/GitHubBranchProtection.md`) — enable in GitHub UI
- [x] Doc audit: no spurious “global 100% line” goal (domain-only 100% is explicit)

## Phase 2 — ProdStyle-first

- [x] CONTRIBUTING.md rule for new kernel features
- [x] Review guide: reject gap-only megaclass PRs
- [x] `testing-strategy-gate.yml` enforces ProdStyle test delta or `[skip-prod-style]`
- [x] `make test-prod-style` documented in kernel-gate-tier-c (existing) + pivot doc

## Phase 3 — CI path map

- [ ] Path → workflow table — **rebuilt 2026-09-14 after the previous one went stale.** It was
      recorded "verified 2026-05-28" and then survived the 2026-08 workflow pruning unchanged, so
      it spent months naming manual-only workflows in a column headed "PR workflows" and pointing
      at deleted projects. Left unticked deliberately: the table below is accurate today, and
      nothing keeps it accurate. Re-derive before trusting it — see the command under the table.
- [x] `testing-strategy-gate.yml` on PRs touching `src/` / `application/` — its `paths:` are
      `src/**`, `application/**`, `scripts/**`, `.github/**`, `Makefile` and
      `docs/architecture/TestingStrategy*.md`. Note `commercial/**` is **not** among them.
- [x] Required checks documented in [Testing and quality gates](../production-readiness/TestingAndQualityGates.md)

### What actually blocks a merge

Five contexts are required on `master`, and **none of them appears in the path table below**,
because they run on every pull request regardless of what it touches:

`cert-gate` · `build-core` · `shell-lint` · `lychee (README + docs)` · `Readiness summary`

Everything in the next table is advisory: a path-filtered lane going red does not stop a merge
unless somebody is watching. Read the table as "what else you should expect to see", not as
"what will stop you".

### Path → minimum CI

**Auto** means the workflow has a `pull_request:` trigger whose `paths:` cover that row.
**Manual** means it has only `workflow_dispatch:` and will not run on your PR no matter what you
touch — those rows are the ones that were quietly wrong before.

| Paths | Runs on the PR | Manual only | Local |
|-------|----------------|-------------|-------|
| `src/Ashlar.Core.Domain/**` | `kernel-coverage`, `testing-strategy` | — | `dotnet test src/Ashlar.Tests.Domain` |
| `src/Ashlar.Core.Application/**` | `kernel-coverage`, `kernel-gate`, `testing-strategy` | — | `Ashlar.Tests.Application` |
| `src/Ashlar.Infrastructure/**` | `kernel-coverage`, `kernel-gate`, `testing-strategy`, `orchestration-build-gate` | `cross-platform-tests` | `make kernel-gate` |
| `src/Ashlar.Runtime/**`, `src/Ashlar.Hosting/**` | `kernel-gate`, `testing-strategy`, `distribution-matrix-gate` | `cross-platform-tests` | `make kernel-gate` |
| Production wiring (routing, barriers, API host) | `testing-strategy` | — | `make test-prod-style` · `kernel-gate-tier-c` |
| `application/**` (API, CLI) | `application-gate`, `testing-strategy`, `distribution-matrix-gate`, `mcp-a2a-gate`, `portability-gate` | — | `make application-gate` |
| `src/**/Mesh/**`, `src/**/Fleet/**`, `deploy/compose/docker-compose.mesh*` | *(nothing path-specific)* | `composition-mesh-gate`, `mesh-lab-gate` | `make composition-mesh-gate` |
| Trust / certification / security policy | `security-gate` (for the `Trust/**`, `Certification/**` and `Manifest/**` paths it lists) | `test-trust-multi-env` | `make security-gate` |
| Distribution / CLI packaging | `distribution-matrix-gate` | `ship-gate` | `make ship-gate` |
| `products/**` | `products-gate`, `dependency-boundary` | — | — |
| `commercial/**` | `dependency-boundary`, `layer-boundary` | — | — |
| `docs/**` only | `lychee (README + docs)` (required), `docs-link-check` | — | — |
| Release tag | *(none — these never run on a PR)* | `rc-gate`, `production-readiness-gate-v1`, `runtime-release-gate` (push / schedule / manual) | — |

Re-derive the Auto/Manual split rather than trusting the columns above — that is exactly what the
2026-05 table stopped being able to do:

```bash
for f in .github/workflows/*.yml; do
  printf '%-34s ' "$(basename "$f" .yml)"
  python3 -c "
import yaml, sys
d  = yaml.safe_load(open(sys.argv[1], encoding='utf-8'))
on = d.get(True) or d.get('on')          # a bare 'on:' key parses as the boolean True
if not isinstance(on, dict) or 'pull_request' not in on:
    print('MANUAL/other:', ','.join(sorted(map(str, on))) if isinstance(on, dict) else on)
else:
    # 'pull_request:' with nothing under it is a PRESENT key with a null value, and runs on
    # every path. Testing the value rather than the key reports those lanes as manual - which
    # is how the first draft of this very snippet called cert-gate manual-only.
    print('AUTO ', ', '.join((on['pull_request'] or {}).get('paths', ['(all paths)'])))
" "$f"
done
```

`commercial/src/Ashlar.Commercial.GameDirector.*` and `commercial/tests/Ashlar.Commercial.Tests.GameDirector`
had a row here until this rewrite. Those projects were deleted in `e4138982`, "slim: remove
everything that is not natively Ashlar's responsibility (#446)"; `commercial/` now holds the Fleet
and MeshDirector projects instead.

## Phase 4 — Gap hygiene

- [x] Freeze: `testing-strategy-gate` fails on new `*GapCoverageTests.cs` without `gap-coverage-justify:` in PR body
- [x] Megaclass allow list (below)
- [x] Review guide: prefer ProdStyle over gap megaclass edits
- [x] Redundant gap suite reduction (JWT/middleware/domain/barriers; ProdStyle dedup in Makefile; `WorkflowExecutorEdgeCaseTests`)
- [x] Dogfood Block 8 matrix tests skip on CI (`[NotOnCiFact]` in `Ashlar.Tests.Infrastructure.Helpers`, reported as Skipped rather than a silent pass; nested `dotnet test` is flaky on runners)
- [ ] Optional backlog: PR Coverlet diff script (planned as `coverage-changed-files.sh` under `scripts/ci/`; not yet written)
- [ ] Quarterly ratchet: bump `INFRA_COVERAGE_THRESHOLD` / `APP_COVERAGE_THRESHOLD` when justified

### Megaclass allow list (extend in place only; prefer ProdStyle)

- `Execution/ProviderFactory.cs`
- `Testing/Docker/DockerService.cs`
- `Persistence/PostgresDatabaseProvisioner.cs`
- `Execution/BehaviorExecutor.cs`
- `Execution/ClusterExecutor.cs`
- `Execution/Routing/AshlarPeerBrickExecutor.cs`
- `Knowledge/KnowledgeQueryService.cs`

## Phase 5 — RC linkage

- [x] RC checklist §1 mapped to workflows (below)
- [x] `rc-gate-full` in [RC readiness v1](../production-readiness/RCReadiness-v1.md)
- [x] Monthly `rc-gate` schedule on `master` (workflow_dispatch still available)

### Release candidate checklist → automation

| RC checklist item | Workflow / command |
|------------------|-------------------|
| Production readiness | `production-readiness-gate-v1.yml` · `make ship-gate-full` |
| Environment setup | `environment-setup-gate-v1.yml` |
| Runtime release | `runtime-release-gate.yml` · `dotnet run … release gate` |
| Runtime promotion | `runtime-release-promotion.yml` |
| Installer brute-force | `installer-bruteforce-gate.yml` |
| Container images | `container-image-gate.yml`, `container-image-publish.yml` |
| Onboarding docs | `onboarding-docs-guard.yml` |
| Cross-platform | `cross-platform-tests.yml`, `full-platform-readiness-gate.yml` |
| Local RC stack | `make rc-gate-full` · `ASHLAR_READY_SKIP_DOCKER=1 make ashlar-ready-gate` |
| Kernel coverage evidence | `make kernel-coverage-gate` |

---

## Coverage floor history

| Date | Domain | Infrastructure | Core.Application | Notes |
|------|--------|----------------|------------------|-------|
| 2026-05-28 | 100% | 83% | 67% | `kernel-coverage` floors after gap-suite reduction (CI ~83.5% infra line) |

---

## Manual follow-up (org settings)

- [ ] GitHub **branch protection** on `master`: require `testing-strategy`, `kernel-coverage`
- [ ] Optional: label `needs-prod-style` for reviewer triage (manual label)
