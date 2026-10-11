## Summary

PR 2 of the budgeted autonomous development workflow (card #768, workstream `budgeted-autonomy`, phase B): the Claude adapter and bounded readiness. The controller from PR 1 (#756) can now launch one bounded run of the installed Claude Code CLI, classify how it stopped, account for what it used, resume it exactly, cancel it without losing anything, and turn readiness-gate failures into persistent failure identities with one bounded retry. Operator doc: `tools/autonomy-controller/README.md`.

Base master `1d8bc7448` (#756). Nothing under `src/`, `application/`, `commercial/`, the specs or any readiness path is touched.

## Changes

- `ashlar_controller/adapters/claude.py` (new): launches `claude.exe` headless (`-p --output-format stream-json --verbose --strict-mcp-config --model --effort --max-turns --permission-prompts none --permission-mode acceptEdits --no-chrome`, `--session-id` or `--resume`), after verifying the funding route with `claude auth status --json` (an `ANTHROPIC_API_KEY` in the environment or a non-`claude.ai` login refuses the launch: `funding-route-mismatch`); strips `CLAUDECODE`/`CLAUDE_CODE_*` from the child; writes a manifest before the spawn; maps the result to separate stop reasons (`completed`, `invocation-ceiling-reached`, `auth-missing`, `quota-exhausted` incl. overage, `rate-limited`, `transient`, `permission-missing` even on a successful result, `model-unrecognized`, `cancelled`, `no-final-result`, `task-failed` with a stable identity); records the stream's `rate_limit_event` as the subscription allowance in percent and `total_cost_usd` as a per-invocation estimated API value; cancels by writing `cancelled_at` first, then `taskkill /T` or `killpg`; locates the transcript by session id. Capabilities are declared from the live probes only.
- `ashlar_controller/readiness.py` (new): parsers for `scripts/readiness-gate-local.sh --json`, `scripts/ci/run-repo-gates.sh` and `scripts/test-in-container.sh` output; failure identities stable across timestamps, durations, paths, temp dirs and hex ids; `run_gate` with no retry and infrastructure errors kept apart; `launch_fixer` with a general-purpose fallback exactly once and only on a recognised missing-role error; `refuted`.
- `controller.py`: `LaunchRefused` mapped by reason (auth and route mismatch block the task, an unrecognised model parks it for a decision); a run that stopped at the turn ceiling requeues and the next launch carries `resume_session_id`; every usage observation recorded; `evaluate_readiness` applies a gate report to the one-retry counter (same `failure_retries` table, so a second cycle or a restart cannot refresh it).
- `budget.py`: a fresh allowance in `percent` permits a launch iff the unused share of the window exceeds the interactive reserve fraction (turns cannot be compared with a percentage; integrator decision).
- `cli.py`: `tick --adapter {fake,claude}`, `readiness run --gate ... --workspace ... [--task-id]`, `readiness evaluate --task-id --report`. Fix: `task set-state` could never work (its positional was named `state` and overwrote the global `--state` path).
- `store.py`: `pid_alive` uses `OpenProcess`/`GetExitCodeProcess` on Windows. CPython's Windows `os.kill(pid, 0)` calls `TerminateProcess`, so PR 1's lease takeover check could kill the previous holder it was probing.
- `models.py`, `adapters/base.py`: three stop reasons, `LaunchRefused`, `LaunchRequest.role/agent/allowed_tools`, `RunStatus.observations/transcript_path`, three capability flags.
- `policy.example.json`: `claude.model` is `claude-sonnet-5` (the CLI refuses `claude-sonnet-5-5` before any request).
- Tests: `tests/test_claude_adapter.py` (42, over `tests/fake_claude_cli.py`, which replays the observed NDJSON shapes), `tests/test_readiness.py` (65), `tests/test_integration_pr2.py` (24), one CLI test; `tests/scripts/autonomy-controller.test.sh` floor 70 to 190 (212 in the suite).
- Records: `CHANGELOG.md` `[Unreleased]`, `docs/DocsIndex.md`, `tools/autonomy-controller/README.md`. `docs/knowledge-graph.{json,md}` regenerated after `git add`: unchanged.

## What was verified against the real CLI (Claude Code 2.1.263, Windows, 2026-10-11)

Bounded probes (one turn each, model `claude-sonnet-5`, report `probe-report-2.json` on the storage branch): the result's `session_id` equals the one passed and `--resume` reuses it; `--max-turns` is enforced (`subtype: error_max_turns`); a denied tool call completes with `is_error: false` and a populated `permission_denials`; an unrecognised model fails with `api_error_status: 404` and no cost; terminating the process mid-request keeps the transcript and `--resume` works afterwards; `--json-schema` returns `structured_output`; `num_turns` and `total_cost_usd` are per invocation; the subscription allowance is the stream's `rate_limit_event` (five-hour window utilization, overage flags); the `Workflow` tool is in the default headless tool set (so the readiness command's tool is reachable through the adapter); `--strict-mcp-config` keeps the operator's claude.ai connectors out of a worker (first-call cache creation 9k tokens instead of 70k).

One live task through `tick --adapter claude` (policy ceilings 6/12/2): the worker wrote the requested `hello.txt`, the run settled `completed` with 2 turns charged and no overshoot, the manifest carried the session id and workspace, the transcript was found, and `status` showed the allowance (73 percent) and the estimated API value (0.053 USD). That run found three defects, all fixed here: the pretty-printed `auth status` output, the truncated project slug, and `task set-state`.

What is still unverified is listed in the README ("What is unverified"). No account was exhausted: quota, rate-limit, transient and overage shapes are simulated by the fake CLI.

## Integrator decisions (no owner rows exist for this workstream)

- `claude.model` `claude-sonnet-5`; permission mode `acceptEdits` with prompts disabled; `--strict-mcp-config` on every launch; expected funding route `claude.ai`, an API key in the environment refuses the launch rather than paying.
- A `percent` allowance permits a launch iff `(100 - value) / 100 - interactive_reserve_fraction > 0`; the task ceilings bound the turns.
- The readiness fixer falls back to a general-purpose run exactly once, only when the named agent role is missing.
- Turn-ceiling exits requeue without touching the same-failure counter (the pool's attempts bound them).

## Testing

- `bash tests/scripts/autonomy-controller.test.sh` on this Windows host: `Ran 212`, `OK (skipped=1)`: `test_world_writable_policy_is_refused` now skips where the loader has no mode-bits check (it failed on master on Windows). Two further host failures from master (`test_expired_lease_with_dead_pid_on_this_host_is_taken`, `test_restart_mid_launch_reconciles_before_launching`) are gone with the `pid_alive` fix.
- `bash scripts/test-in-container.sh -- 'bash tests/scripts/autonomy-controller.test.sh'`: `212 tests passed` in the devtest container (`ashlar-devtest:local`, Python 3.12, which does have `sqlite3`; the phase A handoff's claim that it does not is corrected).
- `bash scripts/ci/run-repo-gates.sh` on the host: 26 of 29; the three failures are the known Windows artefacts (`tests/scripts/mutation-check.test.sh` symlink fixture, `tests/scripts/session-start-hook.test.sh` docker wrapper; `autonomy-controller.test.sh` before the skip). `shell-lint` on ubuntu is the check that counts.
- `bash scripts/check-docs-phantom-paths.sh`: `All referenced paths exist (0 missing)`. `bash -n` on the shell files: clean. `shellcheck` is not on this host; CI runs it.
- Mutation checks (`scripts/mutation-check.sh` in a clone of the named ref, `ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh`; `runner=override` marks the Python suite on the host, not the container; `git status --porcelain` was empty after every restore; a `python3` shim on PATH stood in for the Store stub the script otherwise finds on this host):

```
mutation quota-text-not-recognised: KILLED red=failed:3/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation auth-text-not-recognised: KILLED red=failed:2/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation api-key-route-ignored: KILLED red=failed:1/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation overage-not-quota: KILLED red=failed:1/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation denials-only-when-error: KILLED red=failed:1/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation cancel-not-recorded: KILLED red=failed:4/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation auth-status-last-line-only: KILLED red=failed:43/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation transcript-slug-only: KILLED red=failed:1/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation resume-dropped: KILLED red=failed:2/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation retry-counter-never-bumped: KILLED red=failed:6/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation percent-reserve-boundary: KILLED red=failed:1/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation identity-keeps-timestamps: KILLED red=failed:3/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation fallback-on-any-error: KILLED red=failed:2/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation pid-check-always-alive: KILLED red=failed:3/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
mutation set-state-clobbers-path: KILLED red=failed:1/212
 green=passed:211/212
 ref=d2f8b3336f5af1cb674bd6b3cd239f4ec1ecbd94 runner=override
```

  Acceptance criterion to mutation: quota and auth cause no fallback launch → `quota-text-not-recognised`, `auth-text-not-recognised`, `overage-not-quota`; the funding route is verified → `api-key-route-ignored`; cancellation preserves the workspace and the run record → `cancel-not-recorded`; a recoverable handoff (session, workspace, transcript) → `transcript-slug-only`, `auth-status-last-line-only`; exact resume after the turn ceiling → `resume-dropped`; one retry, and another cycle cannot refresh the counter → `retry-counter-never-bumped`, `identity-keeps-timestamps`; fallback only on a missing role → `fallback-on-any-error`; permission denial is its own stop reason → `denials-only-when-error`; the percent reserve → `percent-reserve-boundary`; the two PR 1 defects → `pid-check-always-alive`, `set-state-clobbers-path`.

- What only CI can prove: `shellcheck -S error` on the shell files; the five required checks on this head.

### Testing strategy (blast radius)

See [Testing strategy pivot v1](docs/architecture/TestingStrategyPivot-v1.md).

| Change type | Minimum proof (check what applies) |
|-------------|-------------------------------------|
| Repository tooling (Python + shell under `tools/` and `tests/scripts/`), no .NET code | `scripts/ci/run-repo-gates.sh` (the required `shell-lint` path) + the container run + mutation checks above |

- [ ] `make kernel-coverage-gate` — not applicable
- [ ] `make kernel-gate` — not applicable
- [ ] `make test-prod-style` — not applicable

## Checklist

- [ ] `make test` passes locally — not run: no .NET change; `CLAUDE.md` forbids host `dotnet`
- [x] Documentation updated
- [x] No `TODO` or `NotImplementedException` left unresolved
- [x] Breaking changes are documented — none

## Known limits

- The readiness fixer loop is not driven by `tick`; `readiness run|evaluate` are operator commands and `launch_fixer` is wired into dispatch by PR 3.
- One live task only; the pilot against real cards is PR 4. `GhIssuePoster` remains unexercised live.
- The CLI's stored login must be current on the controller host: a stale one still reports `loggedIn: true` and every launch then blocks as `auth-missing` (observed; the README names the symptom and the operator step).

## Release (only when this PR ships a **versioned** NuGet/GHCR release)

- [x] Not a versioned release — skip

🤖 Generated with [Claude Code](https://claude.com/claude-code)
