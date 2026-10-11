## Summary

PR 1 of the budgeted autonomous development workflow (card #755, workstream `budgeted-autonomy`, phase A): the operator policy and the durable controller state, with a fake adapter and **no live model call**. The controller takes an eligible task, reserves budget for one bounded launch, records every attempt in SQLite, and stops spending when the policy says so. Operator doc: `tools/autonomy-controller/README.md`.

Base master `c0d110183` (the planning snapshot; master had not moved).

## Changes

- `tools/autonomy-controller/ashlar_controller/` (new, standard library only):
  - `policy.py`: validates the operator policy and **refuses one that lives inside the controller's repository or any registered worker checkout, or is group/world-writable**. The committed `policy.example.json` is therefore an example, never the active policy.
  - `store.py`: the SQLite schema (tasks, phases, prs, runs, budget pools, failure retries, usage observations, events, outbox, lease, controls, journal, policy versions, worker checkouts); `open` refuses a missing file (`StateMissing`, exit 3) and a corrupt one (integrity check, required tables, schema version); `init` is explicit and refuses to overwrite. The writer lease is taken over after expiry **only** when the holder released it, its pid is gone on this host, or an operator ran `lease break --confirm-stopped`. A partial unique index refuses a second active implementation run.
  - `budget.py`: pools are per task, cumulative, and **shared by a split child**; reservation and run row in one transaction; pool ceilings frozen at creation so a policy can only tighten; unknown consumption charged at the reservation; overshoot charged and recorded; usage kept by kind (`subscription_allowance`, `api_grant_balance`, `paid_spend`, `estimated_api_value`) with `fresh|stale|unavailable`, cumulative session figures turned into per-run deltas; the 25% interactive reserve applied only to a fresh, quantified allowance.
  - `events.py`: an event id is an idempotency key; repeats and out-of-order deliveries are no-ops.
  - `outbox.py`: milestone posts with a dedup key and a marker; an interrupted send is looked up on the target before any resend (duplicate-safe, not exactly-once). `GhIssuePoster` exists but is **not exercised live** in this PR.
  - `controller.py`: one action per tick, journaled with its exact stop reason: refuse on missing/corrupt state, paused, lease not held; reconcile unknown launches before anything new; settle finished runs; one writer across providers; idle on an empty queue with zero adapter calls; allowance check; reserve then launch. Quota exhaustion checkpoints and waits on the same provider, never a fallback route; the same diagnosed failure is retried once; auth/permission/decision gaps block with the exact blocker; cancellation keeps the run row and workspace; a worker that exits without a final result parks for reconciliation.
  - `cli.py`: `init`, `status`, `pause`, `resume`, `tick [--dry-run]` (read-only store, no adapter), `task`, `event ingest`, `run settle|cancel`, `lease`, `outbox`, `worker register`, `provider disable|enable`, `policy`.
  - `adapters/`: the adapter protocol with declared capabilities, and `FakeAdapter` (scripted, counts every call).
- `tests/scripts/autonomy-controller.test.sh` (new): runs the 79-test suite under `scripts/ci/run-repo-gates.sh` (discovered, so it is part of required `shell-lint`), with a test-count floor of 70 so an empty discovery cannot pass. `tools/autonomy-controller/mutation-runner.sh`: `ASHLAR_MUTATION_RUNNER` for `scripts/mutation-check.sh`.
- Records: `CHANGELOG.md` `[Unreleased]`, `docs/DocsIndex.md`. `docs/knowledge-graph.{json,md}` regenerated after `git add`: unchanged by these files.

No source under `src/`, `application/`, `commercial/` or any readiness path is touched; `Readiness summary` should pass as diff-skipped (partial), which is not platform verification and is not claimed as such.

## Integrator decisions (no owner rows exist for this workstream)

- Operating defaults in `policy.example.json`: paid inference off; no new paid infrastructure; one implementation writer across providers; one verifier; one readiness fixer; one same-failure retry; two repair rounds; one premium escalation and one provider transfer per PR; 25% interactive reserve; `unobservable_allowance: task-ceiling-only`; invocation ceiling 20 turns, task ceiling 80, four attempts.
- Model ids in the example are candidates only (`claude-sonnet-5-5`, Codex `sol`, Cursor `composer-standard`); PR 2 verifies installed availability before any live launch.
- Package location `tools/autonomy-controller/`, JSON policy (the devtest image's Python has no `tomllib`), one action per tick.

## Testing

- `bash scripts/ci/run-repo-gates.sh` on the host: `repo-gates: all 29 gate(s) passed` (28 before this PR plus `tests/scripts/autonomy-controller.test.sh`, which reports `79 tests passed`).
- `bash scripts/check-docs-phantom-paths.sh`: `All referenced paths exist (0 missing)`. `bash -n` over every `*.sh` under `scripts/`, `tests/`, `.claude/hooks/`: clean. `shellcheck` was not available in this environment; CI runs it.
- Devtest container: `bash scripts/test-in-container.sh --repo <clone> -- 'bash tests/scripts/autonomy-controller.test.sh'` at `c8e6304a` **refuses**: the image's `python3` is `python3-minimal` plus `python3` (`.docker/Dockerfile.devtest:49`) and has **no `sqlite3` module** (`ModuleNotFoundError: No module named 'sqlite3'`). The suite's CI path is the `shell-lint` runner (`ubuntu-24.04`, full Python), the same path every other `tests/scripts/*.test.sh` uses. No `dotnet` was run on the host. Container parity needs `python3` with `libsqlite3` in the devtest image, which is a readiness-path change (`.docker/Dockerfile.*`) and is queued for the owner rather than widened into this PR.
- Mutation checks (`scripts/mutation-check.sh` in a clone of `c8e6304a` with `ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh`; each line is the script's verbatim summary; `runner=override` marks the Python suite on the host, not the container; `git status --porcelain` was empty after every restore):

```
mutation dedup-insert-or-ignore: KILLED red=failed:3/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation lease-takeover-unconfirmed: KILLED red=failed:2/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation reservation-not-counted: KILLED red=failed:28/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation corrupt-state-tables-unchecked: KILLED red=failed:1/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation worker-policy-raises-ceiling: KILLED red=failed:1/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation policy-in-checkout-accepted: KILLED red=failed:4/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation dry-run-dispatches: KILLED red=failed:3/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation quota-requeues-immediately: KILLED red=failed:1/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation idle-calls-adapter: KILLED red=failed:2/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation cancel-drops-workspace: KILLED red=failed:1/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation same-failure-retried-forever: KILLED red=failed:2/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation second-writer-allowed: KILLED red=failed:1/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation unknown-launch-not-investigated: KILLED red=failed:2/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
mutation outbox-reconcile-blind: KILLED red=failed:1/79 green=passed:79/79 ref=c8e6304a77361280deae5249bb24168c51b50bc2 runner=override
```

  Acceptance criterion to mutation: duplicate events / duplicate workers → `dedup-insert-or-ignore`; restart preserves the reservation → `reservation-not-counted`; expired lock needs a confirmed stop → `lease-takeover-unconfirmed`; missing or corrupt state stops dispatch → `corrupt-state-tables-unchecked` (the missing-file case is a refusal before any file exists; its test asserts no file is created); worker-side policy edit cannot raise limits → `policy-in-checkout-accepted` and `worker-policy-raises-ceiling`; dry run makes no requests and no mutations → `dry-run-dispatches`; quota stop without paid fallback → `quota-requeues-immediately`; zero model calls on an empty queue → `idle-calls-adapter`; cancellation and restart recovery → `cancel-drops-workspace`, `unknown-launch-not-investigated`; single writer → `second-writer-allowed`; one same-failure retry → `same-failure-retried-forever`; outbox reconciliation → `outbox-reconcile-blind`.

- What only CI can prove: `shellcheck -S error` on the two new shell files; the five required checks on this head.

### Testing strategy (blast radius)

See [Testing strategy pivot v1](docs/architecture/TestingStrategyPivot-v1.md).

| Change type | Minimum proof (check what applies) |
|-------------|-------------------------------------|
| Repository tooling (Python + shell under `tools/` and `tests/scripts/`), no .NET code | `scripts/ci/run-repo-gates.sh` (the required `shell-lint` path) + mutation checks above |

- [ ] `make kernel-coverage-gate` — not applicable (no `src/Ashlar.Core.*` or `src/Ashlar.Infrastructure` change)
- [ ] `make kernel-gate` — not applicable
- [ ] `make test-prod-style` — not applicable

## Checklist

- [ ] `make test` passes locally — not run: no .NET change; `CLAUDE.md` forbids host `dotnet`, and the repo gates are the lane for this change
- [x] Documentation updated (`tools/autonomy-controller/README.md`, `docs/DocsIndex.md`, `CHANGELOG.md`)
- [x] No `TODO` or `NotImplementedException` left unresolved
- [x] Breaking changes are documented — none

## Known limits

- The devtest image cannot run the suite (no `sqlite3` module); see Testing. Queued as an owner choice.
- `GhIssuePoster` is unexercised live; the fake poster covers the outbox logic.
- Branch protection was read from `docs/GitHubBranchProtection.md` and `CONTRIBUTING.md` (five required checks, strict, enforce_admins); this session has no `gh` token to read it from the API, so the effective rule is verified by the PR's check list before merge.

## Release (only when this PR ships a **versioned** NuGet/GHCR release)

- [x] Not a versioned release — skip

🤖 Generated with [Claude Code](https://claude.com/claude-code)

https://claude.ai/code/session_01Dyk9j7jV8YU3bcVi79MXTJ
