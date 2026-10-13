# Budgeted autonomy controller

A small controller that takes an eligible task card, reserves budget for one bounded worker launch,
records every attempt durably, and stops spending when the operator's policy says so. It is the
foundation of the budgeted autonomous development workflow (workstream `budgeted-autonomy`, card
#755). PR 1 shipped the policy and the durable state over a fake adapter. **PR 2 adds the Claude
adapter and bounded readiness**: a live launch through the installed Claude Code CLI, exact session
resume at the turn ceiling, usage observations, a funding-route check before every launch, and a
readiness fixer whose gate failures carry stable identities. **PR 3 adds the PR lifecycle and fresh
context**: the controller clones the base onto a branch for the worker, commits and pushes what the
worker edited, opens the PR once, observes the required checks by head SHA, runs at most
`limits.repair_rounds` repair rounds as new runs briefed by a compact handoff, merges on green with the
head pinned, records the merge SHA, treats closure without merge as not success, and starts the next
task from that merge SHA. **PR 4 adds operator controls and evidence retention**: `status` and `report`
show the operator everything the ledger knows, including each run's evidence paths and the PR record,
without an LLM; `metrics` are counts from the ledger; `backup` and `restore` move the ledger through
the SQLite backup API with a schema check; `evidence archive` keeps a run's manifest, output, transcript
and ledger rows after its workspace is gone; and `pause`, `resume`, `provider disable` and `run cancel`
are proven to take effect at the next tick. **PR 5 adds the Codex and Cursor adapters and the bounded
provider transfer**: each adapter declares only what a probe of the installed CLI showed; `task transfer`
moves a task to another provider, at most `limits.provider_transfers_per_pr` times, keeping its PR, its
attempts and its budget pool, after the previous writer is stopped and settled; the new provider starts a
fresh session briefed by the compact handoff.

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
| `ashlar_controller/adapters/` | The adapter protocol, declared capabilities, the fake adapter, `claude.py` (the Claude Code CLI), `codex.py` (the Codex CLI) and `cursor.py` (the Cursor agent CLI); `locate_run_transcript` finds a run's transcript by the provider that wrote it |
| `ashlar_controller/readiness.py` | The three gate commands, their parsers, stable failure identities, the bounded fixer launch |
| `ashlar_controller/prs.py` | PR records, the `gh` client (`GhCli`) and its in-memory twin (`FakeGh`), checks evaluated by head SHA, the compact handoff |
| `ashlar_controller/gitops.py` | The controller's git: clone the base onto a branch, commit and push a workspace, clone a PR head for a repair |
| `ashlar_controller/lifecycle.py` | The PR phase of a tick: propose, open, observe, repair, merge, park; PR events |
| `ashlar_controller/metrics.py` | Counts and sums from the ledger (`metrics`) and the LLM-free Markdown report (`report`) |
| `ashlar_controller/evidence.py` | `backup` and `restore` through the SQLite backup API; per-run evidence archives |
| `policy.example.json` | The operator policy example (copy it outside every checkout) |
| `tests/` | The unit suite (`tests/fake_claude_cli.py`, `fake_codex_cli.py` and `fake_cursor_cli.py` stand in for the CLIs); `tests/scripts/autonomy-controller.test.sh` runs it in the required `shell-lint` check |
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
3. For live launches, install Claude Code on the controller host and log in once: `claude auth login`.
   The controller never logs in, never stores a credential and never refreshes a token. A stale login
   blocks the task (`blocked-auth`) with the CLI's own message ("OAuth refresh token is no longer
   valid") until the operator repeats this step; `claude auth status --json` still says `loggedIn:
   true` in that state, so the first run after a long idle is the real check. `authMethod` must be
   `claude.ai`.
4. Register tasks (`task add --task-id <id> --card <n> --workstream <w> --provider claude ...`) and run
   `tick --adapter claude`. `tick` without `--adapter` uses the fake, for proving the state machine.
   `--adapter` is repeatable (`--adapter claude --adapter codex`): register every provider whose tasks the
   tick may launch, observe or stop. The Codex and Cursor providers are disabled in the example policy;
   enable one only after its CLI is logged in on this host (see "Codex adapter" and "Cursor adapter").

`ASHLAR_CONTROLLER_STATE` and `ASHLAR_CONTROLLER_POLICY` stand in for `--state` and `--policy`.

## Commands

| Command | Does |
|---|---|
| `status [--json]` | Owner, task, PR record, state, stop reason, blocker, budget consumed/reserved/remaining, attempts remaining, funding pool, lease, latest usage per provider, and the evidence paths of each task's last run (run directory, manifest, stdout, stderr, transcript, workspace, archive) |
| `report [--json]` | The LLM-free status report: `status` plus `metrics` rendered as Markdown from the ledger alone (`--json` gives both dicts and the Markdown) |
| `metrics [--json]` | Counts and sums from the ledger: tasks and runs by state, runs by stop reason and role, turns consumed and reserved, PRs by outcome, repair rounds, retries, latest usage per provider and kind, journal by kind. No model is called |
| `backup <dest>` | Copy the open ledger to a new file through the SQLite online backup API; refuses to overwrite; journals `backup` in the source |
| `restore <source> [--replace]` | Replace the `--state` file with a backup. Refuses a source whose `schema_version` the controller does not know (`1` and `2` are known; `1` is migrated on open) and, without `--replace`, an existing state file; swaps the file atomically, removes stale `-wal`/`-shm` sidecars, opens and verifies the result, and journals `restored` in it |
| `evidence archive <run> [--out <dir>]` / `evidence list` | Tar the run directory (`manifest.json`, `stdout.json`, `stderr.log`, the transcript when it can be located) with the run's ledger rows (`ledger.json`: run, task, pool, PR, journal, usage, policy version) into `<state dir>/evidence/<run>.tar.gz`; never overwrites; journals `evidence-archived` with the archive's sha256 |
| `pause --reason <text>` / `resume` | Stop launching before the next tick; allow it again |
| `tick [--dry-run] [--adapter fake\|claude\|codex\|cursor]... [--gh none\|cli] [--workspaces <dir>]` | One step. `--dry-run` opens the state read-only, calls no adapter and no `gh`, and says what a tick with the same options would do, by the tick's own rules: an implementation or repair run in `reserved`/`launching`/`running` is the writer (`would-block writer-busy`); with `--gh cli` any open PR blocks (`would-block pr-open-blocks-dispatch`); the picked task's provider must have its adapter registered, else it is `would-block provider-disabled`, as the tick reports it, so give the dry run the same `--adapter`s as the real tick. It polls nothing: a run whose process has exited stays the writer until a real tick settles it, and an open PR is not observed. `--adapter` is repeatable; `claude`, `codex` and `cursor` register those CLIs' adapters with their run directories under `<state dir>/runs/` (the fake is always registered). `--gh cli` turns the PR lifecycle on through the `gh` executable (worker clones under `<state dir>/workspaces/`); without it a completed run is `done` and no PR is opened |
| `pr list\|show <n>\|observe <n>` | PR records; `observe` reads the PR's current head and its required checks now and merges, repairs or parks as they demand (`--gh cli`) |
| `readiness run --gate <name> --workspace <path> [--task-id <id>]` | Run one gate command (`repo-gates`, `readiness-local`, `container-tests`) in a workspace and print the parsed report; with `--task-id`, apply it to the task (see "Readiness fixer") |
| `readiness evaluate --task-id <id> --report <json>` | Apply a saved report (the JSON `readiness run` printed) to a task without running anything. A passing report finishes a task only when it has no open PR; while a PR is open it changes nothing (action `pr-open`): the PR phase decides |
| `task add|list|show|set-state` | Task records; `--parent` makes a child that **shares** the parent's budget pool |
| `event ingest --event-id ... [--gh cli]` | Record an event once; a repeated id is reported as a duplicate and does nothing. With `--gh cli`, a **new** PR event (`pr-opened`, `checks-green`, `checks-red`, `merged`, `closed`, `pr-head`) also triggers one idempotent observation of the task's open PR |
| `run list` / `run settle <run> --stop-reason <r>` / `run cancel <run> [--adapter <provider>]...` | Run records. `settle` is the operator's manual reconciliation of an UNSETTLED run: a run already settled is refused (exit 1, naming its state and stop reason; nothing changes; `task set-state` moves its task), and so is `--stop-reason completed` of an implementation or repair run (exit 1): `settle` has no PR lifecycle, so the task would be `done` with nothing proposed; tick with `--adapter <provider> --gh cli` instead. `cancel` stops the run through its provider's adapter and keeps the workspace; a `launching`, `running` or `unknown` run is refused without that provider's `--adapter` (`previous-writer-not-stoppable`, exit 1, nothing changed), and a run that still polls running after the cancel is polled again for up to five seconds (a cancel does nothing to a run whose turn is over, and its process may be exiting). A run that had finished by then is settled with what it did (`cancelled: false`, `finished_before_cancel: true`), except a completed implementation or repair run, which is refused (`transfer-needs-pr-lifecycle`, exit 1, nothing settled): `cancel` has no PR lifecycle, so tick with `--adapter <provider> --gh cli` to propose it |
| `lease show|release|break` | The writer lease; `break --confirm-stopped` is the operator asserting the old holder is gone |
| `outbox list|enqueue|flush` | Milestone posts; `flush --poster fake` in PR 1 (`gh` poster present, not exercised live) |
| `worker register|list` | Worker checkout roots, which the policy may never live under |
| `provider disable|enable <name>` | Take a provider out of dispatch without editing the policy |
| `task transfer <id> --to <provider> [--reason <text>] [--adapter <old provider>]... [--gh none\|cli] [--workspaces <dir>]` | Move a task to another provider (see "Provider transfer (PR 5)"). Refused with exit 6 past `limits.provider_transfers_per_pr`; exit 4 for a target the policy or the operator disabled, or whose unit differs; exit 1 `transfer-needs-pr-lifecycle` when the previous writer has completed and `--gh cli` is not given (nothing changed); exit 1 `previous-writer-not-stoppable` or `reconcile-pending` for a writer it cannot stop or reconcile. Prints `moved: true`, or `moved: false` with `ended` when a settlement ended the transfer |
| `policy` | Show the validated active policy and its hash |

Exit codes: 0 ok; 1 other error; 2 usage; 3 state missing or corrupt; 4 policy refused; 5 writer
lease not held; 6 budget refused.

Output written to a file or a pipe (not a terminal) is UTF-8 whatever the console code page, so `report > file`
keeps blockers that quote model or CLI text (arrows, check marks, U+FFFD). A terminal keeps its own encoding.

## What one tick does

One tick does **one** thing, the first that applies, and journals it with its exact stop reason:

1. refuse if the state is missing or corrupt, report `paused` if paused, refuse (`lease-not-held`)
   if another controller holds the writer lease;
2. reconcile runs whose launch outcome is unknown (`launching` with no handle, or `unknown`),
   asking the adapter what became of them; while any stays unknown, nothing new launches;
3. poll running runs and settle the finished ones (release the reservation, charge what was
   consumed, move the task to the state its stop reason demands); a tick that settled anything
   stops here. With the PR lifecycle on, a **completed** implementation or repair run is settled by
   committing and pushing its workspace (see "PR lifecycle"), not by marking the task done;
4. if a writer is already active (one implementation or repair run, across every provider), stop
   (`writer-busy`);
5. with the PR lifecycle on, observe every open PR by its current head (merge, repair, park, or wait);
   if one acted, stop there; if any PR is still open, stop (`pr-open-blocks-dispatch`: PRs are serial);
6. pick the first eligible task, by priority then age; none means `idle: empty-queue` with **zero**
   adapter calls;
7. check the provider's latest allowance observation against the 25 percent interactive reserve;
8. reserve the invocation ceiling against the task's pool and record the run, in one transaction;
   with the PR lifecycle on, clone the current base onto the task's branch as the workspace; then
   launch, and record the handle, or the uncertainty, at once.

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
| `invocation-ceiling-reached` | `queued` | not a failure and no retry counter is touched; the next launch **resumes the same session** (`resume_session_id`), journalled as `resume`, **in the workspace the capped run was editing** (journalled `workspace-reused`; a missing or empty workspace is cloned afresh); the pool's attempts and task ceiling bound it |
| `model-unrecognized` | `parked-decision` | the CLI refused the model id before any request; the blocker names the id, and the policy must change |
| `funding-route-mismatch` | `blocked-auth` | the launch would have billed a route the policy did not select; nothing was started |
| `completed`, lifecycle on | `pr-open` (`pr-checks-pending`) | the workspace was committed and pushed; the PR is open (or its new head recorded); only a merge on the current head makes the task `done` |
| `completed`, lifecycle on, and the commit, push or PR fails | `needs-reconcile` (`propose-failed`) | the run stays settled and charged; `propose-failed` is journalled with the error and the tick reports the settle normally (no traceback). Nothing launches for ANY task while a task is `needs-reconcile` with `propose-failed` (`propose-failed-blocks-dispatch`): the operator checks its workspace, the branch on the remote and GitHub (the push, or even the PR, may have happened), then moves it with `task set-state` or adopts an open PR with `event ingest --kind pr-opened --task-id <id> --gh cli` |
| `no-change-to-propose` | `queued` once, then `parked-failure` | the worker completed and left no diff; a diagnosed failure through the same retry counter (`pr:no-change:<task>`) |
| `pr-merged` | `done` | every required check green on the current head; merged with the head pinned; `merge_sha` recorded |
| `pr-closed-unmerged` | `parked-failure` | the PR was closed without a merge: never success; nothing from it is the next base |
| `repair-rounds-exhausted` | `parked-failure` | the current head is red and `limits.repair_rounds` are used; operator step required |
| a refusal at launch (`LaunchRefused`) | by the same mapping | the adapter named the reason before anything started: `auth-missing` and `funding-route-mismatch` give `blocked-auth`, `model-unrecognized` gives `parked-decision`, anything else requeues as `launch-failed`; the reservation is released, the attempt stays counted |
| budget or attempts exhausted at reservation | `parked-budget` | with the remaining counts in the blocker text |
| `provider-transferred` (operator, `task transfer`) | `queued`, or `pr-open` while the PR is open | the previous writer was stopped and settled first (a completed one proposed, which needs `--gh cli`); task, PR, attempts and pool are unchanged; the new provider starts a fresh session |

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
- With a fresh, quantified allowance in the pool's own unit, a launch needs
  `remaining - 25% of total >= one invocation`. With a fresh allowance in `percent` (the Claude
  CLI's window utilization), a launch needs the unused share of the window to exceed the reserve
  fraction; the invocation ceiling is not consulted, because turns cannot be compared with a
  percentage. With a stale or unavailable allowance, `funding.unobservable_allowance` decides: rely on
  the task ceilings (default) or wait.

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

## Claude adapter

`ashlar_controller/adapters/claude.py` runs the installed Claude Code CLI headless. Its
`AdapterCapabilities` declare only what was observed; the few things still unverified are flagged
in the code and listed below.

### What was observed

Two probes of Claude Code 2.1.263 on Windows on 2026-10-11, the second after `claude auth login`,
with model `claude-sonnet-5` (the result's `modelUsage` names `canonicalModel` `claude-sonnet-5`).
The shapes below are what the CLI printed; the adapter parses these and nothing else.

- Executable: the npm shim `claude.cmd` wraps `.../node_modules/@anthropic-ai/claude-code/bin/claude.exe`;
  the adapter launches the exe (`cli=None` resolves `shutil.which("claude")`, and a `.cmd` resolves to
  the sibling `bin/claude.exe`).
- Headless: `claude -p <prompt> --output-format json` prints one JSON object (the last stdout line) of
  `type: "result"` with the fields `session_id`, `is_error` (bool), `subtype`, `terminal_reason`,
  `api_error_status` (int or null), `result` (text), `num_turns`, `total_cost_usd`, `duration_ms`,
  `duration_api_ms`,
  `usage{input_tokens,output_tokens,cache_creation_input_tokens,cache_read_input_tokens,service_tier,speed:"standard"}`,
  `modelUsage{}` (per-model figures; a `claude-haiku-4-5` helper call appears beside the main model),
  `permission_denials[]`, `fast_mode_state:"off"`, `stop_reason`, `uuid`.
- Success: `is_error:false, subtype:"success", terminal_reason:"completed", stop_reason:"end_turn"`,
  `result` = the text.
- `--output-format stream-json --verbose` (what the adapter uses) emits NDJSON, in order:
  `{"type":"system","subtype":"init",...}` (fields `model`, `permissionMode`, `apiKeySource:"none"`,
  `claude_code_version`, `session_id`, `cwd`, `tools:[...]`, `mcp_servers:[...]`, `agents:[...]`,
  `slash_commands:[...]`), then
  `{"type":"rate_limit_event","rate_limit_info":{"status":"allowed","resetsAt":<epoch s>,"rateLimitType":"five_hour","overageStatus":"rejected","overageDisabledReason":"org_level_disabled","isUsingOverage":false,"unifiedWindows":{"five_hour":{"utilization":0.69,"resetsAt":1791702600},"seven_day":{"utilization":0.18,"resetsAt":1792285200}}},"session_id":...}`,
  then `assistant` messages, then the `result` object (the same fields as `--output-format json`).
  The adapter takes the last line whose `type` is `result`.
- **The `rate_limit_event` is the subscription allowance source.** `utilization` is the used fraction
  of the window; the adapter records the last event seen as a `subscription_allowance` observation in
  `percent` (`five_hour.utilization * 100`, total 100, fresh, by session), or `unavailable` when no
  event was seen. `isUsingOverage: true` would mean paid overflow: the run is classified
  `quota-exhausted` with the blocker "the provider is billing overage; paid inference is off", and
  there is no fallback.
- `num_turns` and `total_cost_usd` are **per invocation**, not cumulative on the session: a first call
  reported `num_turns` 1 / cost 0.2817, and the `--resume` call on the same session `num_turns` 1 /
  cost 0.0144. `total_cost_usd` has `costBasis:"list"`: an estimate of API value, never a charge on
  the subscription.
- `--session-id <uuid>` is honoured: the result's `session_id` equalled the one passed, and it stayed
  the same across three `--resume` calls.
- `--max-turns N` is enforced. With `--max-turns 1` on a task needing a tool call and an answer:
  `is_error:true, subtype:"error_max_turns", terminal_reason:"max_turns", num_turns:2, stop_reason:"tool_use"`.
- Permission denial (`--permission-prompts none`, manual mode, a Bash call): the run **completes** with
  `is_error:false, subtype:"success"` and
  `permission_denials:[{"tool_name":"Bash","tool_input":{...},"tool_use_id":"toolu_..."}]`; the result
  text says the call was automatically denied. So `permission_denials` is checked before a success
  counts as completed.
- Unrecognised model (`claude-no-such-model-0`): stderr `[claude-code:unrecognized_model] {"model":"<id>","query_source":"sdk"}`,
  result `is_error:true, terminal_reason:"api_error", api_error_status:404, total_cost_usd:0,
  result:"There's an issue with the selected model (...). It may not exist or you may not have access to it."`,
  exit 1. Recognised ids include `sonnet`, `opus`, `haiku`, `fable`, `claude-sonnet-5`,
  `claude-sonnet-4-5`, `claude-opus-4-1`, `claude-haiku-4-5`, `claude-fable-5`, `claude-fable-5-1`,
  `claude-opus-5`; not recognised: `claude-sonnet-5-5`, `claude-opus-5-5`, `claude-haiku-5-5`.
- Cancellation: terminating `claude.exe` while the model request was in flight exited the process
  (code 1) with no `result` event; the transcript `.jsonl` was persisted (12 lines), and a later
  `--resume <same id>` succeeded with the same `session_id` (`num_turns` 1). Cancel, and resume after
  cancel, are verified.
- `--json-schema <schema>`: the result carries `structured_output: {"answer":"4"}` and `result` is the
  JSON text (`num_turns` 2). `--no-session-persistence`: no transcript file is written.
- Transcript: `<projectsDirectory>/<slug>/<session_id>.jsonl`, where `projectsDirectory` comes from
  `claude auth status --json` and `slug` is the cwd with every character outside `[A-Za-z0-9]`
  replaced by `-`.
- Tools: with `--tools default`, headless tools include `Workflow`, `Task`, `Bash`, `Edit`, `Read`,
  `Skill` and the rest. **The Workflow tool is available headless**, which answers the handoff's
  question: the converge-readiness command's Workflow tool can be driven through this adapter.
  `--tools ""` disables tools; `--tools Read,Bash` restricts them; `--agent <name>` selects a named
  agent role; `--add-dir` is accepted.
- MCP: with `--strict-mcp-config` and no `--mcp-config`, `mcp_servers` is `[]` and the first call's
  cache creation dropped from 70k tokens to 9k (cost 0.28 to 0.043). Without it, the operator's
  claude.ai connectors (Gmail, Drive, Slack, Docs) load into every worker and their tools are exposed
  to it. **Every controller launch passes `--strict-mcp-config`.**
- `--effort low|medium|high|xhigh|max` is accepted. There is no speed flag; `usage.speed` is always
  `"standard"` and `fast_mode_state` is `"off"`.
- Auth: `claude auth status --json` prints `{loggedIn, authMethod:"claude.ai", apiProvider:"firstParty",
  subscriptionType:"max", projectsDirectory, email, orgId, orgName}`. A stale login looked like this in
  the result: `is_error:true, terminal_reason:"api_error", api_error_status:null,
  result:"Failed to authenticate: OAuth session expired and could not be refreshed"` (under `--bare`:
  `result:"Not logged in · Please run /login"`), exit code 1, with the debug log saying
  `API auth_error: OAuth refresh token is no longer valid; run /login to re-authenticate`.
  **`auth status` still reported `loggedIn: true` in that state**, so the route check does not catch
  it; the run itself does, as `auth-missing`. The operator step is `claude auth login`.

### What is unverified

- The shape of a `permission_denials` entry for tools other than Bash (one Bash denial was observed).
- `num_turns` under a cancelled run (no result object is written, so nothing is charged beyond the
  reservation: the conservative reading).
- The seven-day window as a limiting factor: the adapter records the five-hour utilization, which was
  the higher of the two in every probe.
- **What `num_turns` counted in one pilot run.** In the PR 4 pilot (2026-10-11) the resumed run
  `run-3225ebdb59dd` (attempt 2 of `pilot-1`, `--resume`, `--max-turns 12`) finished `success` with
  `num_turns` 48; the controller charged 48 (overshoot 36) and the task's pool closed at 61 of a 36-turn
  ceiling. The other two resumed runs of the pilot reported 13 with `error_max_turns`, and a probe on
  2026-10-12 (fresh session 5 turns; `--resume` with `--max-turns 2` stopped at `error_max_turns`,
  `num_turns` 3; `--resume` again, `num_turns` 1) showed the limit enforced and the count per invocation.
  The 48 is not explained by subagents (`subagent_stats.spawned` 0 in that run; 4 in its capped attempt 1,
  which also called `ScheduleWakeup` once) and is not reproduced. Until it is, a resumed run's reported
  turns are charged in full as they are reported (the conservative reading), and the pool's task ceiling
  is what bounds the next launch.

### How a launch runs

- Speed: `request.speed` must be `standard`; anything else is refused before any process starts
  (`launch-failed`, requeued).
- Funding-route check, before every launch: the adapter runs `claude auth status --json`. The route is
  `api-key` if `ANTHROPIC_API_KEY` is in the child environment, otherwise the `authMethod` the CLI
  reports. `loggedIn: false` refuses with `auth-missing`; a route other than the one the policy
  selected (`claude.ai` by default) refuses with `funding-route-mismatch` ("`<observed>` would be billed;
  policy selected `<expected>`"); the command failing to run at all refuses with `auth-missing`. An API
  key in the environment means paid spend, not the subscription, so the launch does not happen.
- Environment: `CLAUDECODE` and every `CLAUDE_CODE_*` variable are stripped from the child environment
  (a nested session changes the CLI's behaviour). `ANTHROPIC_*` variables are kept, so the route check
  can see a key rather than silently launching past it.
- Argv: `-p <prompt> --output-format stream-json --verbose --strict-mcp-config --model <M> --effort <E>
  --max-turns <ceiling> --permission-prompts none --permission-mode acceptEdits --no-chrome`, plus
  `--session-id <S>` for a new session or `--resume <S>` when the task's previous run stopped at the
  turn ceiling, plus `--agent <name>` and `--tools <list>` when the request carries them.
- Run directory `<runs_dir>/<run_id>/`: `manifest.json` is written before the spawn (run id, task id,
  session id, resume_of, argv, cwd, started time, pid, the projects directory and the expected transcript
  path, which is only a hint: the live run of 2026-10-11 showed the CLI truncates a long project slug to
  200 characters plus a six-character suffix, so the transcript is located by its session id), then stdout goes to
  `stdout.json`, stderr to `stderr.log`, the pid into the manifest. The workspace is
  `request.workspace` or `<run dir>/workspace`. A spawn that definitely failed is `launch-failed`; a
  manifest written with the spawn outcome unknown is `launch-outcome-unknown`, and the next tick
  reconciles from the manifest before anything else launches.
- Classification of a finished run, in this order: `[claude-code:unrecognized_model]` on stderr, or
  `api_error_status` 404 with the model text, gives `model-unrecognized`; no result object gives
  `no-final-result` (or `cancelled` when the manifest says so); `subtype` `error_max_turns` gives
  `invocation-ceiling-reached`; the text "another Claude Code process is refreshing it / exited mid-refresh /
  usually transient" with no HTTP status gives `transient` (observed live on 2026-10-11 when the desktop app's
  Claude Code processes were alive on the same host: the worker exited at once, cost 0, and the CLI itself said
  to retry; it mentions OAuth, so it is checked before the auth rule); a 401 or an auth message gives `auth-missing`; a 402, a usage-limit
  message or `isUsingOverage` gives `quota-exhausted`; a 429 or a rate-limit message gives
  `rate-limited`; 408/5xx/529 or a network message gives `transient`; a non-empty `permission_denials`
  gives `permission-missing` even when `is_error` is false; `is_error` false gives `completed`;
  anything else is `task-failed` with the identity `claude:<sha256 of the normalised first 200
  characters of the result text>[:12]`.
- Accounting: `consumed_units` is `num_turns` (per invocation) and `RunStatus.usage` is `None`, so no
  dollar figure ever stands in for turns. Two observations are recorded on every run: the allowance in
  `percent` (by session) and `total_cost_usd` as an `estimated_api_value` in USD keyed by run id, so
  the recorded delta is the per-invocation cost. `status` shows the latest of each.
- Cancel writes `cancelled_at` into the manifest first, then terminates the process tree (`taskkill
  /T /F` on Windows, `killpg` on POSIX). The workspace and the transcript are never touched; the
  session can be resumed afterwards.

## Codex adapter

`ashlar_controller/adapters/codex.py` runs the Codex CLI headless (`codex exec --json`). It declares only what
was observed; the module docstring lists each observation with its probe id. The probes were run on Windows
on 2026-10-12, with reports `probe-codex-report-{1,2,3,4}.json` on the storage branch
`claude/budgeted-autonomy-workspace`.

### What was observed

- **Executable.** It is `codex-cli 0.162.0-alpha.17.2`, the Codex desktop app's binary at
  `%LOCALAPPDATA%\OpenAI\Codex\bin\<hash>\codex.exe`, which has `codex-code-mode-host.exe` beside it.
  The other `codex.exe` on the host, `~/.codex/.sandbox-bin/codex.exe` (0.162.0-alpha.2), has no code-mode
  host beside it. There every tool call fails closed ("Code Mode is unavailable ... host executable was
  not found"), and the model reports that it could not edit. `resolve_cli()` therefore never picks a
  `codex.exe` without the host beside it, including one found on `PATH`. A `codex.cmd` shim is used as
  found. Otherwise it takes the newest app binary that has the host.
- **Login.** `codex login status` exits 0 and prints `Logged in using ChatGPT`, on stderr. The route
  check reads both streams. An `OPENAI_API_KEY` in the child environment means the `api-key` route: paid
  spend, refused as `funding-route-mismatch`. Anything else is refused as `auth-missing`; the logged-out
  text is unobserved.
- **The fresh run**, exactly as passed (report 3, r1):
  `codex exec --json --skip-git-repo-check -C <workspace> -s workspace-write -c approval_policy="never"
  -c model_reasoning_effort=<effort> -c mcp_servers={} -m <model> -o <run dir>/last_message.txt <prompt>`.
  It runs from the workspace with stdin closed; with stdin open the CLI waits ("Reading additional input
  from stdin..."). It prints NDJSON in this order:
  - `thread.started{thread_id}`, then `turn.started`;
  - `item.started` / `item.completed` events whose item is either
    `{type: "agent_message", text}` or
    `{type: "command_execution", command, aggregated_output, exit_code, status}`.
    The file was written by a PowerShell `Set-Content` through `command_execution`;
  - `turn.completed{usage: {input_tokens, cached_input_tokens, cache_write_input_tokens, output_tokens,
    reasoning_output_tokens}}`.

  `-o` writes the last agent message. `--ignore-user-config` is never passed: with it the same write was
  refused ("rejected: blocked by policy", r2). The operator's `config.toml` stays loaded and the command line
  overrides what matters. `mcp_servers={}` keeps the operator's MCP servers out of every run.
- **The resume** (report 4, r5): `codex exec resume --json --skip-git-repo-check -c sandbox_mode="workspace-write"
  -c approval_policy="never" -c model_reasoning_effort=<effort> -c mcp_servers={} -m <model> -o <file>
  <thread_id> <prompt>`. It kept the same `thread_id`, appended to the file in the workspace and wrote the
  `-o` file. `exec resume` has no `-s`. Without the `-c sandbox_mode` override a resume would inherit the
  operator's configured sandbox, which on the probed host is `danger-full-access`. Without
  `--skip-git-repo-check` a resume exits 1 with "Not inside a trusted directory".
- **Models.** `gpt-6-astra` is accepted. An unrecognised id, including the PR 1 candidate `sol`, yields
  `{"type": "error", "message": <JSON with status 400 and "... model is not supported when using Codex
  with a ChatGPT account.">}` and then `turn.failed`, with no `turn.completed`, exit 1. That is
  `model-unrecognized`, charged 0.
- **Cancel.** `taskkill /T /F` mid-run exits 1 with no `turn.completed`. The session file
  `<CODEX_HOME>/sessions/<YYYY>/<MM>/<DD>/rollout-<timestamp>-<thread_id>.jsonl` exists afterwards, and
  `exec resume` of that thread succeeded. The transcript is found by thread id, and `status` and
  `evidence archive` find it through `adapters.locate_run_transcript`.
- **Other flags.** `--output-schema <file>` makes the last message the JSON. `--ephemeral` writes no
  session file.

### What is unverified

- A turn limit. No flag exists, so `turn_limit` is False: a Codex run is bounded by the task budget, the
  attempts and the operator's `run cancel <run> --adapter codex`, never by the CLI.
- The subscription allowance. Nothing is printed. `codex app-server` answered `initialize`, but neither
  `account/rateLimits/read` nor `getAccountRateLimits` replied, so `usage_observation` is False and every
  allowance observation is `unavailable`. With the default `unobservable_allowance: task-ceiling-only`,
  launches rely on the task ceilings.
- The error texts for 401, 402, 429 and 5xx, and the logged-out login text. The generic rules are carried
  over from the Claude adapter.
- A tool-restriction flag. A request with `allowed_tools` set is refused rather than run unrestricted.
  `agent`, a Claude agent role, has no Codex equivalent and is recorded as ignored.

### Accounting (integrator decision)

`consumed_units` is the number of `item.completed` events whose item is not an `error` warning: one
completed model step is one unit of the `turns` pool. A cancelled run, and a run with no final result,
report None and are charged the full reservation. An unrecognised model is charged 0. A turn that
completed with an empty last message is `no-final-result`. The token usage is kept in the run result;
it is not a charge.

## Cursor adapter

`ashlar_controller/adapters/cursor.py` runs the Cursor agent CLI headless:
`cursor-agent -p --output-format stream-json --trust --workspace <ws> --model <m> -f <prompt>`. **The CLI on
the probe host was not logged in**, so no `-p` run produced any output (report `probe-cursor-report-1.json`).
The adapter declares no capability beyond the funding-route check, and it is built to leave evidence of the
real shape on its first logged-in run.

### What was observed

- **Executable.** The Cursor IDE's embedded agent CLI `2026.10.01-e373342`, at
  `%APPDATA%\Cursor\User\globalStorage\anysphere.cursor-agent-worker\agent-cli\.local\share\cursor-agent\versions\<v>\cursor-agent.cmd`.
  `resolve_cli()` takes `cursor-agent` on `PATH`, then the newest of those version directories, compared
  by version and not as text. `cursor --help` (the IDE) has an `agent` subcommand, which is not used.
- **Login.** `cursor-agent status` printed `Not logged in` (exit 0). `about` printed `Subscription Tier
  Unknown`. Logged out, `models` and every `-p` run exit 1 with an `Authentication required` error on
  stderr and empty stdout. The adapter refuses a launch while `status` says `Not logged in`
  (`auth-missing`; the operator step is `cursor-agent login`). A `CURSOR_API_KEY` or `CURSOR_AUTH_TOKEN`
  in the environment is the `api-key` route and is refused as `funding-route-mismatch`. The logged-in
  `status` text is unobserved: any other non-empty text at exit 0 counts as logged in, and the text is
  recorded.
- **Flags, from `--help` only:** `--resume [chatId]`, `--model`, `-f`, `--trust`, `--workspace`,
  `--mode plan|ask`, `--sandbox`. No turn-limit flag, no tool-restriction flag and no schema flag are
  listed.

### What is unverified, and how the adapter treats it

- **Resume** (`exact_resume` False). No chat id was seen, so `--resume` is never passed. A request that
  carries a session to resume launches fresh and records `resume_unsupported`.
- **Cancel** (`cancel` False). Nothing lived long enough to be cancelled. `run cancel <run> --adapter cursor`
  still records `cancelled_at` first and then kills the process tree: the exit recorder, the CLI and everything
  it started. A run whose `exit_code` already exists has finished: `cancel` then records nothing and kills
  nothing, because a live pid then belongs to another process, and the next poll classifies the run from its
  output.
- **Turns and allowance** (`usage_observation` and `turn_limit` False). `consumed_units` is always None,
  so every run is charged the full reservation (`turns_unobserved`). Every allowance observation is
  `unavailable`.
- **Transcript** (`transcript_retention` False). Where the CLI keeps a chat is unknown.
- **What the first logged-in run keeps.** Every settled run keeps the first 2000 characters of stdout,
  the `type` of every JSON line, any `session_id` / `chatId` value seen, and the stderr tail.

### Integrator decisions

- **The launcher.** The `.cmd` runs PowerShell, which runs `node.exe index.js`, so a multi-line prompt
  would be cut at its first line break and `% ! " & | < > ^` rewritten. The adapter therefore launches
  `node.exe index.js` directly when they are beside the `.cmd` (as `claude.py` launches `claude.exe`
  rather than the npm shim). It refuses a bare `.cmd` when an argument holds one of those characters.
- **Classification order.** An exit 0 with output is `completed` before the quota, rate-limit and
  transient texts are looked for. Those texts only classify a run that did not exit 0, because a finished
  run's stdout carries the model's own words.
- **The exit code outlives the tick.** Every `tick` is its own process, and only the process that started a CLI
  can read its exit code; before the phase F fix no later tick saw it, so a clean run settled `no-final-result`
  (or, when the model's own text held a quota, rate-limit or network word, `quota-exhausted`, `rate-limited` or
  `transient`, and was queued again). The CLI now runs under `ashlar_controller/adapters/_exit_recorder.py`
  (`python -I _exit_recorder.py <run dir>/exit_code -- <argv>`), which waits for the CLI (it inherits the run's
  stdout and stderr), writes the exit code to `exit_code` atomically, records a CLI that cannot start as 127
  with the reason in `stderr.log`, ignores a console Ctrl+C while it waits, and exits with the same code. The
  manifest records `exit_file` and `exit_recorder`; its `argv` is the CLI's and its `pid` the recorder's, so the
  tree kill of `run cancel` still takes the CLI. A poll reads `exit_code` before it looks at the pid: once the
  CLI has exited the run is over, whatever process the pid now names. A run with no `exit_code` still settles
  `no-final-result` (its recorder was killed by something other than a cancel).
- **Refused before anything starts:** a speed other than `standard`, and a restricted `allowed_tools`
  (`-f` would widen it silently). `effort`, `invocation_ceiling` and `agent` have no flag and are
  recorded as ignored.
- **Policy.** The example's `cursor` provider counts `requests` and stays disabled. A task in a `turns`
  pool cannot be transferred to it, because units are never converted. `composer-standard` is unverified.

## PR lifecycle

`ashlar_controller/lifecycle.py`, `prs.py` and `gitops.py`; on with `--gh cli`. The worker (a headless CLI
under `acceptEdits`) can only edit files in its workspace, so the controller does every git and `gh`
step and records each one:

1. **Before an implementation launch**, the controller clones `pr.remote_url` at the current base onto
   the branch `pr.branch_prefix + <task id>` as the run's workspace (`gitops.prepare_workspace`; a
   `pr.reference_checkout` speeds the clone up). The base is the newest recorded `merge_sha`, else the
   task's `--base-sha`, else the tip of `pr.base_branch`. `tasks.base_sha` and `tasks.branch` record it.
   The prompt names the workspace, the base, the branch, and carries the **compact handoff**: the
   previous PR's number, outcome and merge SHA. Never a transcript, a session id or a previous prompt:
   the prompt is built from the task row and the PR ledger only.
2. **When the run completes**, the controller runs `git add -A`, commits (author `ashlar-controller`)
   and pushes the branch (`gitops.commit_and_push`). No diff is `no-change-to-propose`, a diagnosed
   failure retried once and then parked. The commit message ends with a `Worker: <provider> <model>`
   trailer from the run row, and only a Claude run adds a `Co-Authored-By: Claude` trailer; the PR body
   names the worker and the run too (`prs.attribution_trailers`, `prs.pr_body`), so a Codex or Cursor PR
   never credits Claude. Then it opens the PR **once** (`prs.open_or_reuse_pr`): an
   existing row for the task is reused with zero `gh` calls, an open PR already on GitHub for the branch
   is adopted, otherwise `gh pr create`. The task is `pr-open`.
3. **On every tick** each open PR is observed **by head SHA**: `gh pr view` gives the current head,
   `GET repos/<repo>/commits/<head>/check-runs` gives the checks for that commit alone, and
   `prs.evaluate_checks` reads `pr.required_checks` (the five on master) against them. Of several runs
   of one check (a re-run), the one with the largest check-run id decides: GitHub lists a commit's
   check runs newest first, so the order of the list is never trusted. A required check
   with no run on the head is pending; `success` or `skipped` is green; `failure`, `cancelled`,
   `timed_out`, `action_required`, `stale`, `startup_failure` is red. A green run on an earlier head
   never counts, because it is never fetched.
4. **Green**: `gh pr merge --squash --match-head-commit <head>`, so a head that moved after the
   observation refuses the merge (`merge-refused`, journalled; the PR stays open). The merge SHA comes
   from a second `gh pr view`; `prs.merge_sha`, `outcome=merged`, the task is `done` (`pr-merged`).
5. **Red**: one repair round, at most `limits.repair_rounds` per PR (the round is counted before the
   launch). The repair is a **new** run (`role=readiness-fixer`, agent `readiness-fixer` with the
   missing-role fallback of `readiness.launch_fixer`, `resume_session_id=None`) in a fresh clone of the
   PR head (`gitops.checkout_pr_head`, which refuses a head that moved). Its entire brief is
   `prs.compact_handoff`: PR, branch, base, head, round `N of M`, and per red check its name,
   conclusion, identity (`check:<name>:<12 hex>` over the check's title and summary, stable across
   heads) and bounded evidence. The repair's push is the new head, observed on the next tick. Rounds
   exhausted on a red head parks the task (`repair-rounds-exhausted`).
6. **Closed without a merge**: `outcome=closed-unmerged`, the task parks (`pr-closed-unmerged`), and
   the next task's handoff says nothing from it landed. A merge done outside the controller is recorded
   as `merged` with its SHA.
7. **PRs are serial.** While any PR is open nothing new launches (`pr-open-blocks-dispatch`), and the
   next task's base is the merge SHA the open one produces. An integrator decision (no owner row),
   matching the repository's serial-merge rule.
8. **PR events** (`event ingest --gh cli`): a new event id of a PR kind triggers one observation of
   the task's open PR, which is idempotent; a repeated id is a duplicate and does nothing. So a repeated
   `pr-opened`, `checks-green`, `merged` or `closed` creates no second PR row, no second merge and no
   second worker.

Schema 2 adds `prs.branch`, `prs.url`, `prs.title`, `tasks.branch` and `runs.pr_number`; a schema-1
ledger is migrated in place (additive columns, journalled `schema-migrated`) on its first read-write
open, and refused read-only until then.

## Operator controls and evidence (PR 4)

Nothing in this section calls a model. `status`, `report` and `metrics` read the ledger and the run
directories; `backup`, `restore` and `evidence archive` move files; the controls write one row that the
next tick reads first.

- **Controls take effect at the next tick.** `pause` is the first thing a tick checks, before the PR
  phase, so a red head gets no repair while paused and the PR is not even observed; `resume` lets the
  next tick act. `provider disable <name>` removes the provider from dispatch and from repair launches
  (the PR stays open and watched, no round is spent); `provider enable` restores it. `run cancel <run> --adapter
  <provider>` of a running repair (without that provider's `--adapter` a live run is refused, because settling it
  would leave the worker running) settles the run `cancelled`, keeps its round spent, keeps the PR open and watched
  (a merge done by hand is still recorded and makes the task `done`), moves the task to `cancelled` and
  launches nothing more for it; the workspace and the transcript are preserved for inspection.
- **The operator view.** `status` adds, per task, the PR record and `evidence`: the last run's directory
  under `<state dir>/runs/<run>/`, its `manifest.json`, `stdout.json` and `stderr.log` when present, the
  transcript located by session id from the manifest (the path in the manifest is only a hint), the
  recorded workspace and whether it is still on disk, and the archive when one exists. A path is `null`
  when the file is not there. The last run is the latest by insertion order, not by run id. `report`
  renders the same dict and the metrics as Markdown, so a status post needs no model.
- **Backups and restore.** `backup <dest>` uses `sqlite3.Connection.backup` on the open ledger (a
  consistent copy under WAL) and journals it in the source. `restore <source>` runs before the state is
  opened: it checks the source's integrity, its tables and its `schema_version` (`1` or `2`; anything
  else is refused with nothing written), refuses an existing `--state` unless `--replace` is given, copies
  into a temporary file beside the destination, removes stale `-wal`/`-shm` sidecars, replaces the file
  atomically, then opens the result with the normal `Store.open` (which migrates schema 1 and runs the
  integrity check) and journals `restored`. A restored ledger therefore accepts exactly what the original
  did; the tests restore a backup and run a task through it.
- **Evidence archives.** `evidence archive <run>` writes `<state dir>/evidence/<run>.tar.gz` with
  `run/<file>` for each file in the run directory, `transcript.jsonl` when the transcript can be located,
  `ledger.json` (the run, task, pool and PR rows, the journal rows of the run and its task, the usage rows,
  the policy version) and `MANIFEST.txt`. It refuses to overwrite, and journals `evidence-archived` with
  the file's sha256. An archive outlives the workspace, which a later run or the operator may remove.
  The archived transcript leaves out the account identifiers a Codex session file carries in its
  `session_meta` record (`creator_user_id`, `creator_account_id`), because an archive may be published;
  `MANIFEST.txt` and the journal row say what was removed, and the file on disk is not changed.
- **Metrics** are the ledger's counts and sums: tasks and runs by state, runs by stop reason and role,
  turns consumed and reserved, overshoot, attempts, PRs by outcome and repair rounds, retries, the latest
  usage per provider and kind, and the per-provider sum of `estimated_api_value` deltas, which stays an
  estimate and is never added to a turns figure.

## Provider transfer (PR 5)

`task transfer <id> --to <provider>` (`Controller.transfer_task`) moves a task from one provider's CLI to
another. It is an operator command: nothing transfers a task on its own (see "Not in PR 5"). In this
order, so that a refusal changes nothing:

1. **The target.** It must be a provider the policy names, `enabled`, not disabled by `provider disable`,
   not the task's current provider, and counting in the same unit as the task's budget pool (provider units
   are never converted, so a `turns` pool is never moved to a `requests` provider). A `done` task is not
   transferred. Refusal: exit 4, `PolicyError`.
2. **The bound.** The transfers already made for the task's pool, counted from the append-only journal over
   every task that shares the pool (so a split never resets it), must be below
   `limits.provider_transfers_per_pr` (1 in the example; the policy allows 0 or 1). Refusal: exit 6,
   `provider-transfers-exhausted`, journalled as `transfer-refused`. The bound is checked before anything
   is stopped.
3. **Stop and reconcile the previous writer.** An uncertain launch of this task (and only this task) is
   reconciled through its own provider's adapter first; one that stays uncertain refuses the transfer
   (`reconcile-pending`). A running run whose provider has no adapter registered refuses it
   (`previous-writer-not-stoppable`: pass `--adapter <that provider>`). Every active run is then polled once,
   before anything is settled. A completed implementation or repair run, polled or found by the reconcile, is
   committed, pushed and proposed as usual **when `--gh cli` is given**; without it the transfer is refused
   (`transfer-needs-pr-lifecycle`, exit 1, nothing changed), because the run would be settled `done` with its
   work never proposed and the move would then overwrite `done` with `queued`. A run that ended otherwise is
   settled with what it did. A run still running is cancelled through its own adapter and settled
   (`cancelled`, the workspace kept), after up to five seconds of polls in case its turn is over and its
   process is still exiting; if it has finished by then, it is settled with what it did (a completed writer is
   proposed with `--gh cli` and refused without it). The transfer
   ends there if a settlement leaves the task `done` (a run that completed between the poll and the cancel,
   without `--gh cli`) or a completed run's proposal failed (`propose-failed`): `transfer-ended` is journalled
   with the stopped runs, nothing moves, no transfer is counted, and the output says `moved: false` with
   `ended` (`task-done-by-settlement` or `propose-failed`). So there is never a second writer: the journal shows
   the old run's `settled` before `provider-transferred`, and the new provider's `launched` after both.
4. **The move**, in one transaction: `tasks.provider` changes; the open PR row's `provider_transfers_used`
   is incremented; `provider-transferred` is journalled with the from and to providers, the transfer number,
   the pool, the PR, the stopped runs (`stopped_runs`) and how each was stopped (`stopped`: run, provider, role,
   `how` = `settled`, `cancelled` or `reconciled`, charged, stop reason); that row is the count, written before
   any launch on the new provider. The task goes back to `pr-open` while its PR is open (the PR phase then
   repairs through the new provider) or to `queued` (`stop_reason` `provider-transferred`); the output says
   `moved: true`.

Kept as they were: the task row (id, card, title, branch, base), its PR row (number, head, base, branch,
repair rounds used), its budget pool (consumed, reserved, overshoot, attempts used and allowed). The next
launch reserves from the same pool and counts as the next attempt.

Fresh on the new provider: a session id recorded by another provider is never resumed (the turn-ceiling
resume applies only when the last run's provider is the task's provider), so the new provider gets a fresh
clone of the current base, not the previous provider's workspace, and its brief is the compact handoff plus
one line of facts: "this task was transferred from `<provider>` after run `<id>` (`<stop reason>`); nothing
from that run is in this workspace". Never the previous transcript, prompt or session.

`status` shows each task's provider and `transfers: {used, max}`; `metrics` counts `transfers.total` and
`transfers.refused`; the report prints both.

## Readiness fixer

`ashlar_controller/readiness.py` turns the output of the three gate commands into a report whose
failures have identities that are stable across runs, so the controller can tell "the same failure
again" from "a different failure".

| Gate | Command | Parsed from |
|---|---|---|
| `repo-gates` | `bash scripts/ci/run-repo-gates.sh` | `::group::<path>` / `::error::<path> failed (exit N)` lines and the final `repo-gates: all N gate(s) passed` or `repo-gates: K of N gate(s) failed:` summary |
| `readiness-local` | `bash scripts/readiness-gate-local.sh --layer application --json <out>` | the JSON `{layer, commit, started_at, failed, gates:[{gate,status,exit_code,duration_s,log,log_tail}]}` |
| `container-tests` | `bash scripts/test-in-container.sh --repo <workspace>` | `Test run for <path>/<name>.dll (<tfm>)` headers, `Failed!  - Failed: F, Passed: P, ...` lines and `  Failed <FullyQualifiedTestName> [<ms> ms]` lines |

- A failure identity is `gate:<name>:` plus the first twelve hex characters of the SHA-256 of the
  normalised evidence. Normalisation strips timestamps, durations, absolute paths (the basename
  stays), hex strings of seven or more characters, temp directories, carriage returns and trailing
  whitespace, so two runs that differ only in those have the same identity and two runs with a
  different failing set do not.
- `run_gate` executes the command once and parses it. It never retries. A script that crashes without
  parsable output raises `GateInfrastructureError`, which the CLI reports as an error, not a verdict.
- `evaluate_readiness` (also `readiness run --task-id`, `readiness evaluate`): no failures means the
  task is `done`, unless it has an open PR: then nothing changes (action `pr-open`), and only a merge on the
  PR's current head finishes it. Failures put the first failure's identity through the **same** retry counter as a
  diagnosed run failure: `queued` once per identity, then `parked-failure`. The counter lives in the
  task's pool, so it survives a second cycle, a restart and a task split; a failure a run reported and
  the same failure a gate reported share one retry.
- The fixer is one bounded launch (`role="readiness-fixer"`, agent `readiness-fixer`, the evidence in
  the prompt, the CLI's default tools). If the adapter refuses because the named agent role does not
  exist (`unknown agent`, `agent ... not found`, `no agent named`, `agent not found`), it is retried
  **once** as a general-purpose run and the request records `fallback="general-purpose"`. Any other
  error propagates with no fallback. `refuted(before, after)` lists the identities present in both
  reports: a fix that left its failure in place.

## Operating policy defaults (integrator decisions)

These are the conservative defaults in `policy.example.json`, taken because the owner has not
decided otherwise. They are **integrator decisions**, recorded here and in the PR body, not owner
rows in the decisions log below: paid inference off; no new paid infrastructure; one implementation writer
across providers; one scoped verifier; one readiness fixer; one retry of the same diagnosed failure;
at most two PR repair rounds; at most one premium escalation and one provider transfer per PR; a
25 percent interactive reserve where allowance is observable; idle on an empty queue; checkpoint and
wait on an exhausted allowance; `claude.model` is `claude-sonnet-5`, an id the installed CLI
recognises and the API accepted in the live probe (`modelUsage.canonicalModel` `claude-sonnet-5`;
`claude-sonnet-5-5`, the PR 1 candidate, is refused before any request); the Claude adapter's
permission mode is `acceptEdits` with prompts disabled and `--strict-mcp-config` on every launch; the
expected funding route is `claude.ai` and an `ANTHROPIC_API_KEY` in the environment refuses the
launch rather than paying for it; a `percent` allowance (the CLI's window utilization) permits a
launch iff `(100 - value) / 100 - interactive_reserve_fraction > 0`, because the invocation ceiling is
in turns and cannot be compared with a percentage, so the reserve is kept and the task ceilings bound
the turns; the readiness fixer falls back to a general-purpose run exactly once, and only when the
named agent role is missing; PRs are serial (nothing new launches while one is open); the worker only
edits and the controller does every git and `gh` step (under `acceptEdits` a worker cannot push);
`success` and `skipped` are the only green check conclusions; a repair round is counted before its
launch; `pr.required_checks` default to the five required checks on master. The Codex `sol` and Cursor
`composer-standard` ids remain candidates for PR 5.

## Decisions log (added 2026-10-12)

The owner's decisions only. This workstream has no spec, so this section is its decisions log. Every other
default in this README and in the PR bodies of #756, #769, #771, #773, #774, #780 and #781 stays an
integrator decision until the owner promotes it here.

| Date | Applies to | Decision |
|---|---|---|
| 2026-10-11 | PR 4 pilot (#772) | **The pilot is guarded.** The controller never merges. Its policy lists a required check that never runs, so the owner reviews each PR and merges or closes it by hand, and the next tick records the outcome. |
| 2026-10-11 | Every live run | **Worker launches stay on the owner's terminal, with no allow rule.** A Claude Code session prepares the ledger, the policy and the exact commands. The owner runs every `tick` that launches a CLI worker. |
| 2026-10-12 | PR 5 (#779) | **Transfers are operator-initiated only.** Nothing moves a task to another provider by itself, for example after `waiting-quota` or a missing login. An exhausted allowance means a wait (phase E open question 2). |
| 2026-10-12 | Records | **Only the rows in this log are owner decisions.** The phases A to E defaults stay integrator decisions until the owner promotes one (open question 3). |
| 2026-10-12 | PR 5 live run (#779) | **Cursor is logged in, and carries one live task** through `--adapter cursor` (open question 1). |
| 2026-10-12 | PR 5 live run (#779) | **The live run happens in one sitting, after the fix for the transfer defect found in its pre-flight has merged.** The live evidence is then on the code that closes #779. |
| 2026-10-12 | PR 5 live run (#779) | **The outbox's `gh` poster is exercised once.** One `status` post on the agent-bus issue (#695), sent from the owner's existing `gh` login and signed `From: claude`. No new credential (open question 4). |
| 2026-10-12 | Card #776 | **#776 is the live run's fourth task, on Claude.** A run that stops at the turn ceiling then exercises #780's workspace reuse live (open question 5). |

## Verification

```bash
bash tests/scripts/autonomy-controller.test.sh          # the unit suite, as shell-lint runs it
bash scripts/ci/run-repo-gates.sh                        # every discovered repo gate
ASHLAR_MUTATION_RUNNER="bash tools/autonomy-controller/mutation-runner.sh" \
  scripts/mutation-check.sh --id <name> --file tools/autonomy-controller/ashlar_controller/<f>.py \
  --old '<text>' --new '<text>'                           # red / restore / clean / green, in a clone
```

## Not in PR 5

- **No live task through either new adapter yet.** A Claude Code session's classifier refuses worker
  launches. The owner's terminal is the place for one small real task through `--adapter codex`, which is
  logged in, and one through `--adapter cursor`, after `cursor-agent login`. The Codex CLI's shapes were
  probed live (the reports above); the controller's own launch through it was not.
- **Transfers are operator-initiated only.** Nothing moves a task to another provider by itself, for
  example when a provider's allowance is exhausted or its login is missing. That would be a fallback
  launch on another provider's allowance, and the owner decided on 2026-10-12 that it stays
  operator-initiated (decisions log). "Quota exhaustion results in a wait" still holds.
- **Workspace after a transfer.** The previous provider's uncommitted edits stay in its run's workspace,
  for inspection. The new provider starts from a fresh clone of the current base, or of the PR head for a
  repair.
- **Neither new CLI has a turn limit.** A Codex or Cursor run is bounded by the task budget, the attempts
  and `run cancel <run> --adapter <provider>`, never by an invocation ceiling. A Cursor run is always charged its full reservation.
- **No allowance figure.** Neither CLI printed one, so both providers' allowance observations are
  `unavailable`.

## Not in PR 4

- **The live run of the PR 3 lifecycle happened on 2026-10-11, from the owner's terminal** (a Claude Code
  session cannot launch the worker: its classifier refuses `tick --adapter claude --gh cli`, "Create Unsafe
  Agents"). Task `live-pr3`, policy ceilings 8/16/3 with a sixth, never-running required check as a guard.
  Attempt 1 (`run-1ced8587a0bb`) died at once on the OAuth refresh contention above, 1 turn charged,
  filed `transient`, requeued by hand. Attempt 2 (`run-561ad9689fe5`) completed in 3 turns (0.0825
  USD-estimate; five-hour window at 32 percent): the controller committed as `ashlar-controller`, pushed
  `auto/live-pr3`, opened PR #775 at head `a84e0e9` on base `d2c11b347`, observed it by head SHA (first
  three green and `cert-gate`, `build-core` pending; then all five green and the guard pending, verdict
  `pending`, no merge), and after the owner closed the PR by hand recorded `pr-closed-unmerged` and parked
  the task (`parked-failure`, "nothing landed, the task is not done"). `status`, `report`, `metrics`,
  `evidence archive` (both runs, transcripts included) and `backup` ran against that ledger. So `gh pr
  create`, `gh pr list --head`, `gh pr view --json`, the check-runs API and a closed PR are exercised live;
  `gh pr merge --match-head-commit` and a repair round are still only exercised against the fake.
- **The pilot ran on 2026-10-11 from the owner's terminal** (ledger `ctl-pilot-pr4`, policy
  `2026-10-11.pilot-pr4`, ceilings 12/36/3, one repair round, guarded: a sixth never-running required check
  kept the controller from merging, so the owner reviewed and merged by hand and the next tick recorded it).
  `pilot-0` (a throwaway file): 2 turns, PR #777, merged `f0fd9d113`, task `done`. `pilot-1` (card #762, the
  citation lines in `docs/EgressInventory.md`): attempt 1 stopped at the ceiling (13 turns, 4 subagents) after
  editing the file; attempt 2 resumed the session in a fresh clone, redid the edits, and reported 48 turns
  (see "What is unverified"); PR #778, merged `ea71db40c`, task `done`, pool 61 of 36. `pilot-2` (card #776):
  two attempts at the ceiling (13 turns each), `parked-budget` with 10 turns left; the second attempt's
  partial edits are in its workspace, nothing pushed. Five runs, two PRs merged, zero repair rounds, zero
  closed-unmerged; `pause`, `resume`, `provider disable|enable`, `report`, `metrics`, `evidence archive`
  (five archives) and `backup` were exercised on that ledger. Findings fixed afterwards: a resumed session
  now keeps the workspace its capped run edited (`workspace-reused`), and the report prints a merged PR's
  state once. Evidence: `live-run-pr4-pilot-evidence/` on the storage branch `claude/budgeted-autonomy-workspace`.
- `task add --title` is both the card title and the worker's instruction, so the PR title and the commit
  subject of a controller-opened PR are the whole instruction (PR #775). A separate short title is a small
  follow-up.
- `restore` replaces the file the operator names; it does not stop a controller that holds the writer
  lease on it. Stop every controller on the ledger first (the lease row travels with the backup).
- Archives are written once per run and never pruned; nothing rotates `<state dir>/evidence/`.
- `premium_escalations_used` is still counted and not driven. `provider_transfers_used` is driven by
  `task transfer` since PR 5.

## Not in PR 3

- The `phases` table exists but nothing writes it yet.
- No Codex or Cursor adapter (PR 5). `tick --adapter` accepts `fake` and `claude` only.
- A repair run that stops at the turn ceiling requeues like any run; the next observation of the still
  red head starts a new round with a fresh session rather than resuming the fixer's session (the rounds
  bound it). Premium escalation and provider transfer per PR are counted in `prs` but not driven.
- The `GhCli` argv shapes are the ones in the module docstring; `pr create`, `pr list --head`, `pr view
  --json`, the check-runs API and a closed PR were exercised live on 2026-10-11 (PR #775, see "Not in PR 4");
  `pr merge --match-head-commit` only against the fake; none against every error GitHub can return.
- One live launch has gone through `tick --adapter claude` (2026-10-11, policy ceilings 6/12/2): a
  task whose title asked for a `hello.txt` in the workspace. The run settled `completed` with 2 turns
  charged, the file was there, the manifest carried the session id and the workspace, the transcript
  was found by session id, and `status` showed the allowance (73 percent of the five-hour window) and
  the estimated API value (0.053 USD) as two observations. That run exposed three defects, fixed in
  the same PR: `auth status --json` is pretty-printed over many lines (the adapter read only the last
  line), the CLI truncates a long project slug (the computed transcript path did not exist), and
  `task set-state` could never work because its positional overwrote the global `--state` path. The
  pilot against a real card is PR 4. Everything else is proven against `tests/fake_claude_cli.py`.
- The readiness fixer loop over the three LOCAL gate commands (`readiness run` / `readiness evaluate`)
  is still operator-driven; `tick` drives repairs from the PR's required checks (PR 3).
- `GhIssuePoster` is present but has not been exercised live (the session that wrote it had no
  authenticated `gh`). Tests use the fake poster.
- Operator account steps that later PRs need, and that the controller never performs: an
  authenticated `gh` with issue write for the bus and cards; the provider CLIs installed and logged
  in on the controller host. The controller purchases nothing, enables no paid overflow, changes no
  subscription and provisions no infrastructure.
