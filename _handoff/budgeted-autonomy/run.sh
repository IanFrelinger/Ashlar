#!/usr/bin/env bash
# Batch of mutation checks for PR 3, each through scripts/mutation-check.sh against the committed SHA.
# Summary lines are appended to summary.txt; full logs to logs/<id>.log.
set -uo pipefail
REPO="C:/Users/icfre/Documents/Codex/2026-09-12/f-2/work/ashlar-record-correction"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REF="${1:?commit sha}"
export PATH="/c/Users/icfre/AppData/Local/Temp/claude/C--Users-icfre-Documents-Codex-2026-09-12-f-2-work-ashlar-record-correction/bacf9380-784e-43f2-a8be-bb8d20a25b64/scratchpad/pybin:$PATH"
export ASHLAR_MUTATION_RUNNER="bash tools/autonomy-controller/mutation-runner.sh"
mkdir -p "$HERE/logs"
: > "$HERE/summary.txt"
P=tools/autonomy-controller/ashlar_controller

run() {
  local id="$1" file="$2" old="$3" new="$4"
  echo "=== $id" >&2
  ( cd "$REPO" && bash scripts/mutation-check.sh --id "$id" --ref "$REF" --file "$file" --old "$old" --new "$new" ) \
    >"$HERE/logs/$id.log" 2>&1
  grep -E "^mutation $id:" "$HERE/logs/$id.log" | tail -1 >>"$HERE/summary.txt" || echo "mutation $id: NO-SUMMARY (see log)" >>"$HERE/summary.txt"
  tail -1 "$HERE/summary.txt" >&2
}

run m1-pending-head-counts-green "$P/prs.py" \
  '    elif verdict.pending:' \
  '    elif False and verdict.pending:'
run m2-match-head-commit-dropped "$P/prs.py" \
  '"--squash", "--match-head-commit", head_sha])' \
  '"--squash"])'
run m3-red-conclusion-counts-green "$P/prs.py" \
  'GREEN_CONCLUSIONS = ("success", "skipped")' \
  'GREEN_CONCLUSIONS = ("success", "skipped", "failure")'
run m4-merge-current-not-observed-head "$P/lifecycle.py" \
  '                self.gh.pr_merge(repo, number, head)' \
  '                self.gh.pr_merge(repo, number, self.gh.pr_view(repo, number).head_sha)'
run m5-closed-unmerged-is-done "$P/lifecycle.py" \
  '            self.ctl.set_task_state(task_id, TaskState.PARKED_FAILURE, stop_reason=StopReason.PR_CLOSED_UNMERGED,' \
  '            self.ctl.set_task_state(task_id, TaskState.DONE, stop_reason=StopReason.PR_CLOSED_UNMERGED,'
run m6-duplicate-event-acts-again "$P/controller.py" \
  '        if new and self.lifecycle is not None:' \
  '        if self.lifecycle is not None:'
run m7-repair-resumes-previous-session "$P/lifecycle.py" \
  '                                prompt=handoff, resume_session_id=None, role=readiness.FIXER_ROLE,' \
  '                                prompt=handoff, resume_session_id=self.store.row("SELECT native_session_id FROM runs WHERE task_id=? AND native_session_id IS NOT NULL ORDER BY rowid DESC LIMIT 1", (task_id,))["native_session_id"], role=readiness.FIXER_ROLE,'
run m8-repair-prompt-carries-transcript "$P/lifecycle.py" \
  '        handoff = prs.compact_handoff(pr_row, verdict, round_no, rounds_max, self.cfg["base_branch"], workspace)' \
  '        handoff = prs.compact_handoff(pr_row, verdict, round_no, rounds_max, self.cfg["base_branch"], workspace) + "\n" + "\n".join((r["result_json"] or "") for r in self.store.rows("SELECT result_json FROM runs WHERE task_id=?", (task_id,)))'
run m9-repair-rounds-unbounded-by-one "$P/lifecycle.py" \
  '            if used >= rounds_max:' \
  '            if used > rounds_max:'
run m10-round-not-counted "$P/lifecycle.py" \
  '        prs.bump_repair_round(self.store, number, now=now)' \
  '        pass'
run m11-next-base-ignores-merge-sha "$P/lifecycle.py" \
  '        merged = prs.latest_merge_sha(self.store)' \
  '        merged = None'
run m12-prs-not-serial "$P/controller.py" \
  '            if ph["open"] > 0:' \
  '            if False:'
run m13-no-change-still-opens-pr "$P/lifecycle.py" \
  '        if head is None:' \
  '        if False:'
run m14-completed-is-done-not-proposed "$P/controller.py" \
  '            if self.lifecycle is not None and run["role"] in ("implementation", "readiness-fixer"):' \
  '            if False:'
run m15-stale-pr-head-accepted "$P/gitops.py" \
  '    if head != expected_head:' \
  '    if False and head != expected_head:'

echo "=== done" >&2
cat "$HERE/summary.txt" >&2
