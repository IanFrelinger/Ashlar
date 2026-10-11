# Live run of PR 3 (prepared 2026-10-11, not executed: the session's classifier refused the launch)

Run from a terminal that is NOT a Claude Code session, at the root of a clone of the Ashlar repository at or
after the #771 merge. Cost: one worker run (about 0.05 USD-estimate at the phase B rate) plus one CI run on
the throwaway PR. The controller can observe the five real checks by head SHA but can never merge, because
the policy names a sixth required check that never runs.

## 1. Operator files (outside every checkout)

Windows, in PowerShell or Git Bash; `%LOCALAPPDATA%` is the real one when run outside the desktop app.

```bash
mkdir -p "$LOCALAPPDATA/ashlar-controller/state"
cat > "$LOCALAPPDATA/ashlar-controller/policy.json" <<'EOF'
{
  "policy_version": "2026-10-11.live-pr3",
  "limits": {"active_writers": 1, "verifiers_per_task": 1, "readiness_fixers": 1, "same_failure_retries": 1,
             "repair_rounds": 1, "premium_escalations_per_pr": 1, "provider_transfers_per_pr": 1,
             "invocation_turn_ceiling": 8, "task_turn_ceiling": 16, "max_attempts_per_task": 3},
  "funding": {"paid_inference_enabled": false, "new_paid_infrastructure": false,
              "interactive_reserve_fraction": 0.25, "unobservable_allowance": "task-ceiling-only"},
  "providers": {
    "claude": {"enabled": true, "model": "claude-sonnet-5", "effort": "medium", "speed": "standard",
               "funding_pool": "claude-subscription", "pool_kind": "subscription_allowance", "unit": "turns"},
    "fake": {"enabled": true, "model": "fake-model", "effort": "medium", "speed": "standard",
             "funding_pool": "fake-pool", "pool_kind": "subscription_allowance", "unit": "turns"}},
  "idle": {"empty_queue_poll_seconds": 900, "quota_wait_seconds": 3600, "rate_limit_backoff_seconds": [60, 300, 900], "lease_ttl_seconds": 300},
  "pr": {"repo": "IanFrelinger/Ashlar", "remote_url": "https://github.com/IanFrelinger/Ashlar.git",
         "base_branch": "master", "branch_prefix": "auto/",
         "required_checks": ["cert-gate", "build-core", "shell-lint", "lychee (README + docs)", "Readiness summary",
                             "owner-approval (never runs: live-run guard)"],
         "reference_checkout": "<absolute path of your clone, forward slashes>"}
}
EOF
export ASHLAR_CONTROLLER_STATE="$LOCALAPPDATA/ashlar-controller/state/controller.db"
export ASHLAR_CONTROLLER_POLICY="$LOCALAPPDATA/ashlar-controller/policy.json"
C="python tools/autonomy-controller/controller.py --holder live-pr3"
$C init --controller-id ctl-live-pr3
$C task add --task-id live-pr3 --workstream budgeted-autonomy --provider claude --title "Add the file tools/autonomy-controller/LIVE-RUN-PR3.md containing one short paragraph saying it was written by the budgeted autonomy controller's live run of PR 3 on 2026-10-11 in the workspace it was given. Create no other file and change nothing else."
$C tick --dry-run          # expect: would-launch: dry-run task live-pr3 ... reservation 8.0
```

(The desktop-app session already did `init`, `task add` and the dry run under the app's virtualized
`%LOCALAPPDATA%`, `...\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\ashlar-controller\`; reuse those two
files by copying them, or redo the three commands above.)

## 2. Launch and observe

```bash
$C --json tick --adapter claude --gh cli        # launched: clones master onto auto/live-pr3 (reference clone), spawns claude -p --max-turns 8
$C --json tick --adapter claude --gh cli        # repeat until action is "settled": the controller commits, pushes, opens the PR
$C pr list                                       # the PR record: number, branch auto/live-pr3, base sha, head sha, state open
$C --json tick --adapter claude --gh cli        # blocked: pr-open-blocks-dispatch while the five checks run; journal has pr-observed rows
$C pr observe <n> --gh cli                       # on demand: the verdict for the current head (green list, pending list)
```

What to record (paste into the phase D handoff): the `launched` report (reservation 8, attempt 1), the
`settled` report's `lifecycle` dict (`action: pr-open`, `pr_number`, `head_sha`), the PR URL, each
`pr-observed` journal row (`green`, `pending`, `red` by `head_sha`), and `status --json` (`pr_record`,
`usage` with the allowance percent and the estimated API value).

## 3. Close without merge, prove it is not success

When the five real checks are green the guard check keeps the verdict `pending` (the controller never merges):

```bash
gh pr close <n> --delete-branch
$C --json tick --adapter claude --gh cli        # pr: pr-closed-unmerged; prs.outcome closed-unmerged; task parked-failure (pr-closed-unmerged)
$C pr show <n>
$C status
```

If anything the real `gh` returns does not match the shapes `prs.py` parses (`gh pr list --json`,
`gh pr view --json`, `gh api .../check-runs --jq`), that is a phase D finding: fix the shape in a small PR
with a test, as phase B did for the three CLI shapes its live run exposed.

## 4. Evidence to keep

`%LOCALAPPDATA%/ashlar-controller/state/controller.db` (the ledger), `.../state/runs/<run>/` (manifest,
stdout.json, stderr.log), `.../state/workspaces/<run>/` (the clone the worker edited), `status --json`,
`pr show <n>`, and `sqlite3 controller.db "SELECT at, kind, detail FROM journal ORDER BY id"`.
