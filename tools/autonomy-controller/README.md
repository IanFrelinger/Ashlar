# Budgeted autonomy controller

A small controller that takes an eligible task card, reserves budget for one bounded worker launch,
records every attempt durably, and stops spending when the operator's policy says so. It is the
foundation of the budgeted autonomous development workflow (workstream `budgeted-autonomy`, card
#755). **PR 1 ships the policy and the durable state only**: a fake provider adapter, no live model
call, no PR lifecycle. The roadmap (PR 2 Claude adapter and bounded readiness, PR 3 PR lifecycle and
fresh context, PR 4 operator controls and pilot, PR 5 Codex and Cursor adapters) is in the card.

Python 3.8+ and the standard library only (SQLite through `sqlite3`). Nothing here runs `dotnet`,
and nothing here runs outside the host the operator starts it on.

## Layout

| Path | What |
|---|---|
| `controller.py` | Entry point: `python3 tools/autonomy-controller/controller.py --state <db> --policy <json> <command>` |
| `ashlar_controller/policy.py` | Loads and validates the operator policy; refuses one that lives inside a checkout |
| `ashlar_controller/store.py` | The SQLite schema, integrity checks and the single-writer lease |
| `ashlar_controller/budget.py` | Budget pools, atomic reservations, settlement, usage deltas, the interactive reserve |
| `ashlar_controller/events.py` | Event deduplication (an event id is an idempotency key) |
| `ashlar_controller/outbox.py` | Milestone posts (claim / handoff / done) with duplicate-safe recovery |
| `ashlar_controller/controller.py` | The tick: settle, reconcile, then at most one launch |
| `ashlar_controller/adapters/` | The adapter protocol, declared capabilities, and the fake adapter |
| `policy.example.json` | The operator policy example (copy it outside every checkout) |
| `tests/` | The unit suite; `tests/scripts/autonomy-controller.test.sh` runs it in the required `shell-lint` check |
| `mutation-runner.sh` | `ASHLAR_MUTATION_RUNNER` for `scripts/mutation-check.sh` |

## Operator setup

1. Copy `policy.example.json` to a path **outside every checkout**, for example
   `/etc/ashlar/controller-policy.json` or `~/.config/ashlar/controller-policy.json`, and make it
   `0600`. The loader refuses a policy that is inside the repository holding the controller, inside
   any registered worker checkout (`worker register --checkout <path>`), or group/world-writable.
   This is the trust boundary: a worker can edit its checkout, so nothing in a checkout may set the
   limits it runs under.
2. Create the state once: `controller.py --state ~/.local/state/ashlar/controller.db init`.
   The controller never recreates a missing database on its own; a missing or corrupt file stops
   dispatch (exit 3) rather than starting from an empty budget. Back the file up like any ledger.
3. Register tasks (`task add --task-id <id> --card <n> --workstream <w> --provider fake ...`) and run
   `tick`. In PR 1 the only adapter is `fake`; the loop is for proving the state machine.

`ASHLAR_CONTROLLER_STATE` and `ASHLAR_CONTROLLER_POLICY` stand in for `--state` and `--policy`.

## Commands

| Command | Does |
|---|---|
| `status [--json]` | Owner, task, PR, state, stop reason, blocker, budget consumed/reserved/remaining, attempts remaining, funding pool, lease, latest usage per provider |
| `pause --reason <text>` / `resume` | Stop launching before the next tick; allow it again |
| `tick [--dry-run]` | One step. `--dry-run` opens the state read-only, touches no adapter, and says what would happen |
| `task add|list|show|set-state` | Task records; `--parent` makes a child that **shares** the parent's budget pool |
| `event ingest --event-id ...` | Record an event once; a repeated id is reported as a duplicate and does nothing |
| `run list|settle|cancel` | Run records; `settle` is the operator's manual reconciliation; `cancel` keeps the workspace |
| `lease show|release|break` | The writer lease; `break --confirm-stopped` is the operator asserting the old holder is gone |
| `outbox list|enqueue|flush` | Milestone posts; `flush --poster fake` in PR 1 (`gh` poster present, not exercised live) |
| `worker register|list` | Worker checkout roots, which the policy may never live under |
| `provider disable|enable <name>` | Take a provider out of dispatch without editing the policy |
| `policy` | Show the validated active policy and its hash |

Exit codes: 0 ok; 1 other error; 2 usage; 3 state missing or corrupt; 4 policy refused; 5 writer
lease not held; 6 budget refused.

## What one tick does

One tick does **one** thing, the first that applies, and journals it with its exact stop reason:

1. refuse if the state is missing or corrupt, report `paused` if paused, refuse (`lease-not-held`)
   if another controller holds the writer lease;
2. reconcile runs whose launch outcome is unknown (`launching` with no handle, or `unknown`),
   asking the adapter what became of them; while any stays unknown, nothing new launches;
3. poll running runs and settle the finished ones (release the reservation, charge what was
   consumed, move the task to the state its stop reason demands); a tick that settled anything
   stops here;
4. if an implementation writer is already active (one across every provider), stop (`writer-busy`);
5. pick the first eligible task, by priority then age; none means `idle: empty-queue` with **zero**
   adapter calls;
6. check the provider's latest allowance observation against the 25 percent interactive reserve;
7. reserve the invocation ceiling against the task's pool and record the run, in one transaction;
   then launch, and record the handle, or the uncertainty, at once.

### Stop reasons and the states they produce

| Run stop reason | Task state afterwards | Then |
|---|---|---|
| `completed` | `done` | |
| `quota-exhausted` | `waiting-quota` | checkpoint and wait `idle.quota_wait_seconds`; the same provider is retried; **no fallback launch on another route** |
| `rate-limited`, `transient` | `waiting-rate-limit` | bounded backoff from `idle.rate_limit_backoff_seconds`, inside the task budget |
| `task-failed` with a failure identity | `queued` once, then `parked-failure` | the same diagnosed failure is retried **once** across all cycles and providers (`failure_retries` table) |
| `auth-missing`, `permission-missing` | `blocked-auth`, `blocked-permission` | the exact blocker is recorded; an operator step is required |
| `cancelled` | `cancelled` | the run row and its workspace path are preserved |
| `no-final-result` | `needs-reconcile` | process, workspace, transcript and remote state are reconciled before any retry |
| `launch-outcome-unknown` | `needs-reconcile` | the original attempt is investigated before another launch |
| `decision-missing` | `parked-decision` | the decision needed is recorded verbatim |
| budget or attempts exhausted at reservation | `parked-budget` | with the remaining counts in the blocker text |

### Budget accounting

- A pool is per task and cumulative. Its ceilings are frozen when it is created; a later policy can
  only tighten them. A child task made with `--parent` uses the parent's pool, so splitting a task
  never resets an exhausted budget.
- A reservation is the invocation ceiling (20 turns in the example). Settlement charges what the
  adapter reports; unknown consumption is charged at the full reservation; consumption above the
  reservation is charged in full and recorded as `overshoot`, because a provider's turn limit is an
  estimate.
- Usage observations are stored by kind and never summed across kinds: `subscription_allowance`
  (the provider's own units), `api_grant_balance`, `paid_spend`, `estimated_api_value`. An estimated
  API dollar figure is an estimate, not a charge. Each observation carries `fresh`, `stale` or
  `unavailable`. A provider's cumulative session figure is turned into a per-run delta against the
  previous observation of the same session.
- With a fresh, quantified allowance, a launch needs `remaining - 25% of total >= one invocation`.
  With a stale or unavailable allowance, `funding.unobservable_allowance` decides: rely on the task
  ceilings (default) or wait.

### Single writer and recovery

- The writer lease lives in the state file with a heartbeat. An **expired** lease is not free: it is
  taken only when the previous holder released it, or ran on this host and its pid is gone, or the
  operator ran `lease break --holder <id> --confirm-stopped`.
- The database refuses a second active implementation run through a partial unique index, whatever
  the code believes.
- A run is `reserved` before launch, `launching` while the launch is in flight, `running` once a
  handle is recorded. A controller that dies in between leaves a row recovery can see; the next
  tick reconciles it before launching anything.
- The outbox writes a post with a dedup key before sending it, and a post interrupted mid-send is
  checked for on the target (by its `<!-- ashlar-controller:dedup:... -->` marker) before it is sent
  again. This is duplicate-safe recovery, not exactly-once delivery.

## Operating policy defaults (integrator decisions)

These are the conservative defaults in `policy.example.json`, taken because the owner has not
decided otherwise. They are **integrator decisions**, recorded here and in the PR body, not owner
rows in any decisions log: paid inference off; no new paid infrastructure; one implementation writer
across providers; one scoped verifier; one readiness fixer; one retry of the same diagnosed failure;
at most two PR repair rounds; at most one premium escalation and one provider transfer per PR; a
25 percent interactive reserve where allowance is observable; idle on an empty queue; checkpoint and
wait on an exhausted allowance. The model ids in the example (`claude-sonnet-5-5`, Codex `sol`,
Cursor `composer-standard`) are candidates; PR 2 verifies installed availability before any live
launch and may change them.

## Verification

```bash
bash tests/scripts/autonomy-controller.test.sh          # the unit suite, as shell-lint runs it
bash scripts/ci/run-repo-gates.sh                        # every discovered repo gate
ASHLAR_MUTATION_RUNNER="bash tools/autonomy-controller/mutation-runner.sh" \
  scripts/mutation-check.sh --id <name> --file tools/autonomy-controller/ashlar_controller/<f>.py \
  --old '<text>' --new '<text>'                           # red / restore / clean / green, in a clone
```

## Not in PR 1

- No live adapter. The Claude adapter, exact session resume, structured output, usage observation and
  the readiness fixer are PR 2; Codex and Cursor are PR 5.
- No PR lifecycle: current-head CI observation, revisions, merges, merge-SHA recording and the next
  task's fresh session are PR 3. The `prs` and `phases` tables exist so that work has somewhere to land.
- `GhIssuePoster` is present but has not been exercised live (the session that wrote it had no
  authenticated `gh`). Tests use the fake poster.
- Operator account steps that later PRs need, and that the controller never performs: an
  authenticated `gh` with issue write for the bus and cards; the provider CLIs installed and logged
  in on the controller host. The controller purchases nothing, enables no paid overflow, changes no
  subscription and provisions no infrastructure.
