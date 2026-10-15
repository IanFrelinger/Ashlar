# Testing review guide v1

For reviewers enforcing [Testing strategy pivot v1](TestingStrategyPivot-v1.md).

## Quick decisions

| PR changes | Approve if | Request changes if |
|------------|------------|-------------------|
| `Ashlar.Core.Domain` | Domain tests present; `kernel-coverage` green | Missing tests or coverage drop on domain |
| Small Infrastructure adapter | Focused unit/gap tests in **existing** files | New `*GapCoverageTests.cs` without `gap-coverage-justify:` in PR body |
| `ProviderFactory`, Docker, Postgres, mesh executors | ProdStyle / virtual NCR / mesh-lab evidence | Large gap-only diff with no wiring test |
| API / `AddAshlar` / barriers / routing | `make test-prod-style` or WAF tests run in CI | Only gap tests; no ProdStyle |
| Mesh / fleet / trust | `mesh-lab-gate` or composition-mesh path green | Unit tests only |
| Docs only | `docs-link-check` | N/A |

## Reject patterns

1. **New `*GapCoverageTests.cs` file** — unless PR body contains `gap-coverage-justify: <reason>` and reason is narrow (e.g. audit sink with no DI).
2. **Megaclass gap-only PR** — files on [allow list](TestingStrategyTracking-v1.md#megaclass-allow-list-extend-in-place-only-prefer-prodstyle); prefer one ProdStyle test over dozens of gap lines.
3. **Lowering coverage thresholds** in `scripts/ci/kernel-coverage-gate.sh` without release sign-off.
4. **“CI is green”** when only default workflows ran — check path filters; hosting PRs need kernel/application/mesh gates.

## Approve patterns

1. **Domain PR** — 100% line coverage maintained.
2. **Feature PR** — ProdStyle or integration test proves real DI graph.
3. **Bugfix PR** — Regression test at lowest appropriate layer (domain > unit > ProdStyle).

## Escapes (use sparingly)

| Token / env | Effect |
|-------------|--------|
| `[skip-prod-style]` in PR description | Skips ProdStyle delta check in `testing-strategy-gate` for real code changes that need an exception |
| (automatic) comment-only / XML-doc diffs under ProdStyle trigger paths | Do not demand a ProdStyle test delta; `[skip-prod-style]` is unnecessary for those |
| `gap-coverage-justify: reason` in PR description | Allows new `*GapCoverageTests.cs` file |
| `ALLOW_NEW_GAP_COVERAGE=1` | Local/script only — not for routine PRs |

## CI checks to expect

| Check | Meaning |
|-------|---------|
| `testing-strategy` | Pivot policy (gap freeze, ProdStyle wiring) |
| `kernel-coverage` | Composite coverage floors (Domain 100% line leg runs first; the separate `domain-coverage` check was folded in 2026-08-16) |
| `kernel-gate` | Hosting/pipeline slice (path-filtered) |
| `application-gate` | Product layer (path-filtered) |
| `mesh-lab-gate` | Mesh changes (path-filtered) |

## Release reviewers

RC merges require evidence from [Release candidate checklist v1](../ReleaseCandidateChecklist-v1.md), not only coverage. Confirm `rc-gate` / `production-readiness-gate-v1` artifacts exist for the release issue.
