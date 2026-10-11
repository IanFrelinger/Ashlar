#!/usr/bin/env bash
# Batch of mutation checks for PR 4, each through scripts/mutation-check.sh against the committed SHA.
# Summary lines are appended to summary.txt; full logs to logs/<id>.log.
#   bash run.sh <sha> [ids...]     (no ids: every mutation)
set -uo pipefail
REPO="C:/Users/icfre/Documents/Codex/2026-09-12/f-2/work/ashlar-record-correction"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REF="${1:?commit sha}"; shift || true
ONLY=("$@")
export PATH="/c/Users/icfre/AppData/Local/Temp/claude/C--Users-icfre-Documents-Codex-2026-09-12-f-2-work-ashlar-record-correction/bacf9380-784e-43f2-a8be-bb8d20a25b64/scratchpad/pybin:$PATH"
export ASHLAR_MUTATION_RUNNER="bash tools/autonomy-controller/mutation-runner.sh"
mkdir -p "$HERE/logs"
[[ ${#ONLY[@]} -eq 0 ]] && : > "$HERE/summary.txt"
P=tools/autonomy-controller/ashlar_controller

want() { [[ ${#ONLY[@]} -eq 0 ]] && return 0; local x; for x in "${ONLY[@]}"; do [[ "$x" == "$1" ]] && return 0; done; return 1; }

run() {
  local id="$1" file="$2" old="$3" new="$4"
  want "$id" || return 0
  echo "=== $id" >&2
  ( cd "$REPO" && bash scripts/mutation-check.sh --id "$id" --ref "$REF" --file "$file" --old "$old" --new "$new" ) \
    >"$HERE/logs/$id.log" 2>&1
  grep -E "^mutation $id:" "$HERE/logs/$id.log" | tail -1 >>"$HERE/summary.txt" || echo "mutation $id: NO-SUMMARY (see log)" >>"$HERE/summary.txt"
  tail -1 "$HERE/summary.txt" >&2
}

# ---- lane C: controls at the next tick, evidence in status
run c1-pause-ignored-by-tick "$P/controller.py" \
  '        if paused:
            return TickReport("paused", StopReason.PAUSED, details=paused)' \
  '        if False:
            return TickReport("paused", StopReason.PAUSED, details=paused)'
run c2-disabled-provider-still-repairs "$P/lifecycle.py" \
  '        return not (disabled and task_row["provider"] in json.loads(disabled))' \
  '        return True'
run c3-cancelled-task-may-launch-repair "$P/lifecycle.py" \
  '                                 TaskState.BLOCKED_PERMISSION, TaskState.CANCELLED, TaskState.DONE):' \
  '                                 TaskState.BLOCKED_PERMISSION, TaskState.DONE):'
run c4-cancel-skips-the-adapter "$P/controller.py" \
  '        if adapter is not None and run["native_run_handle"]:
            adapter.cancel(run["native_run_handle"])' \
  '        if False:
            adapter.cancel(run["native_run_handle"])'
run c5-evidence-transcript-not-located "$P/controller.py" \
  '                transcript = locate_transcript(manifest.get("projects_dir"), manifest.get("session_id"),
                                               manifest.get("transcript_path"))' \
  '                transcript = manifest.get("transcript_path") if os.path.isfile(str(manifest.get("transcript_path"))) else None'
run c6-evidence-archive-reported-when-absent "$P/controller.py" \
  '                "archive": archive if os.path.isfile(archive) else None}' \
  '                "archive": archive}'
run c7-status-last-run-by-run-id "$P/controller.py" \
  '            run = self.store.row("SELECT * FROM runs WHERE task_id=? ORDER BY reserved_at DESC, rowid DESC LIMIT 1", (t["task_id"],))' \
  '            run = self.store.row("SELECT * FROM runs WHERE task_id=? ORDER BY reserved_at DESC, run_id DESC LIMIT 1", (t["task_id"],))'
run c8-restore-replaces-without-flag "$P/cli.py" \
  '        _print(evidence.restore(args.source, args.state, replace=args.replace), args.json)' \
  '        _print(evidence.restore(args.source, args.state, replace=True), args.json)'

# ---- lane A: metrics and the report (ids filled in after the lane landed; see run-lanes.sh)
# ---- lane B: backup, restore, archives
if [[ -f "$HERE/run-lanes.sh" ]]; then source "$HERE/run-lanes.sh"; fi

echo "=== done" >&2
cat "$HERE/summary.txt" >&2
