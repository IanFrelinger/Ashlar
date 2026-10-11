# Pilot of PR 4: three bounded real tasks through the full lifecycle (prepared 2026-10-11, not executed)

Run from a terminal that is NOT a Claude Code session (the session's classifier refuses `tick --adapter claude`),
at the root of a clone of Ashlar at or after the PR 4 merge. Prerequisites: `gh` authenticated with `repo` scope,
`claude auth status --json` says `loggedIn: true` and `authMethod: claude.ai`, no `ANTHROPIC_API_KEY` in the
environment. Cost: about one worker run per task plus one repair run per red head (ceilings below), one CI run per
push. Nothing here buys anything or enables overage: paid inference is off in the policy.

## 0. Decide the merge question first (owner question Q4 of the phase C handoff)

- **Guarded** (default if undecided): keep the sixth required check `owner-approval (never runs: live-run guard)`
  in `pr.required_checks`. The controller observes the five real checks, repairs a red head, and never merges
  (the verdict stays `pending`). You review and merge each PR by hand; the next tick records the merge as
  `merged` with its SHA and the task is `done`; the next task starts from that merge SHA. Full lifecycle minus
  the controller's own merge.
- **Unguarded**: remove the sixth check. The controller merges (squash, head pinned) when the five are green.
  Every merge makes every other open PR BEHIND (CLAUDE.md), so run the pilot while no other PR is waiting.

## 1. Operator files (outside every checkout)

```bash
mkdir -p "$LOCALAPPDATA/ashlar-controller/pilot"
cp "$LOCALAPPDATA/ashlar-controller/policy.json" "$LOCALAPPDATA/ashlar-controller/pilot/policy.json"
# edit pilot/policy.json: "policy_version": "2026-10-11.pilot-pr4"; limits invocation_turn_ceiling 12,
# task_turn_ceiling 36, max_attempts_per_task 3, repair_rounds 1; keep or drop the guard check per section 0.
export ASHLAR_CONTROLLER_STATE="$LOCALAPPDATA/ashlar-controller/pilot/state/controller.db"
export ASHLAR_CONTROLLER_POLICY="$LOCALAPPDATA/ashlar-controller/pilot/policy.json"
C="python tools/autonomy-controller/controller.py --holder pilot-pr4"
$C init --controller-id ctl-pilot-pr4
$C backup "$LOCALAPPDATA/ashlar-controller/pilot/backups/00-empty.db"
```

## 2. The three tasks

Pick three open `agent-task` cards whose diff touches no readiness path (docs, records, tooling), so
`Readiness summary` is a diff-skipped pass and the five checks finish in a few minutes. Candidates on
2026-10-11: #762 (citation lines in `docs/EgressInventory.md`), #761 (pre-switch wording), #757 (testing-strategy
gate comment-only diffs). Or three throwaway tasks shaped like `live-pr3` (one file under
`tools/autonomy-controller/`) if the first pilot should risk nothing. Register them with the card number so the PR
body and the title carry it; priority decides order:

```bash
$C task add --task-id pilot-1 --card <n1> --workstream budgeted-autonomy --provider claude --priority 10 \
   --title "<the card title, then the exact bounded instruction: which file, what to change, change nothing else>"
$C task add --task-id pilot-2 --card <n2> --workstream budgeted-autonomy --provider claude --priority 20 --title "..."
$C task add --task-id pilot-3 --card <n3> --workstream budgeted-autonomy --provider claude --priority 30 --title "..."
$C tick --dry-run     # would-launch pilot-1, reservation 12
```

## 3. Drive it

One tick does one thing. Repeat until `status` shows every task `done` or parked:

```bash
$C --json tick --adapter claude --gh cli      # launched -> (worker runs) -> settled: pushed, PR opened
$C --json tick --adapter claude --gh cli      # blocked: pr-open-blocks-dispatch while the checks run
$C pr list; $C pr observe <n> --gh cli        # on demand
# guarded: when the five real checks are green, review and `gh pr merge <n> --squash`; the next tick records it
# unguarded: the next tick merges on green
$C --json tick --adapter claude --gh cli      # pr: merged -> task done; then: launched pilot-2 from the merge SHA
$C report                                     # the LLM-free report; paste it into the phase handoff
```

A red head: the next tick launches one repair round (`launched: repair`), settles it (`repair-pushed`), and the
following ticks observe the new head. A second red head parks the task (`repair-rounds-exhausted`); fix or close by
hand, then `tick` records the closure (`pr-closed-unmerged`, never success) and moves on.

## 4. Controls to exercise once each (they take effect at the next tick)

```bash
$C pause --reason "pilot: pause check"; $C tick --adapter claude --gh cli   # paused
$C resume
$C provider disable claude; $C tick --adapter claude --gh cli              # idle or pr-open-blocks-dispatch, no launch
$C provider enable claude
$C run list; $C run cancel <a running run id>                               # only if one is running; cancelled, workspace kept
```

## 5. Evidence to keep (paste into the phase handoff)

```bash
$C status --json > pilot-status.json
$C metrics --json > pilot-metrics.json
$C report > pilot-report.md
for r in $($C --json run list | python -c "import json,sys; print(' '.join(x['run_id'] for x in json.load(sys.stdin)))"); do
  $C evidence archive "$r"; done
$C evidence list
$C backup "$LOCALAPPDATA/ashlar-controller/pilot/backups/99-end.db"
sqlite3 "$ASHLAR_CONTROLLER_STATE" "SELECT at, kind, task_id, detail FROM journal ORDER BY id" > pilot-journal.txt
```

Record: every `launched` report (reservation, attempt), every `settled` report's `lifecycle` dict, each PR URL,
every `pr-observed` row (green, pending, red by head SHA), each merge SHA, the `report` text, and anything the real
`gh` or CLI returned that the parsers did not expect (a phase E finding, fixed in a small PR with a test).
