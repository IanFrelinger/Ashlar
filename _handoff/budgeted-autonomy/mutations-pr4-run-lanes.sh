# Sourced by run.sh: lane A (metrics.py) and lane B (evidence.py) mutations.
run a1-consumed-total-includes-estimate "$P/metrics.py" \
  '            "consumed_total": runs_consumed,' \
  '            "consumed_total": runs_consumed + sum(estimated_by_provider.values()),'
run a2-active-states-drop-running "$P/metrics.py" \
  'ACTIVE_RUN_STATES = ("reserved", "launching", "running")' \
  'ACTIVE_RUN_STATES = ("reserved", "launching")'
run a3-estimate-caveat-dropped "$P/metrics.py" \
  '    out.append(ESTIMATE_CAVEAT)' \
  '    pass'
run a4-pr-outcomes-counted-by-state "$P/metrics.py" \
  '            "by_outcome": _count_by(store, "prs", "outcome", "outcome IS NOT NULL"),' \
  '            "by_outcome": _count_by(store, "prs", "state"),'
run a5-report-drops-evidence "$P/metrics.py" \
  '    if evidence:
        lines.append("- Evidence:")' \
  '    if False:
        lines.append("- Evidence:")'
run b1-unknown-schema-accepted "$P/evidence.py" \
  'KNOWN_SCHEMA_VERSIONS = ("1", "2")' \
  'KNOWN_SCHEMA_VERSIONS = ("1", "2", "99")'
run b2-stale-sidecars-kept "$P/evidence.py" \
  '        _remove_sidecars(dest_path)' \
  '        pass'
run b3-restore-ignores-replace "$P/evidence.py" \
  '    if dest_exists and not replace:' \
  '    if False:'
run b4-archive-overwrites "$P/evidence.py" \
  '        raise StateCorrupt(f"refusing to archive onto {out_path}: it already exists")' \
  '        pass'
run b5-ledger-drops-the-task-pr "$P/evidence.py" \
  '    if pr is None:
        pr = store.row("SELECT * FROM prs WHERE task_id=? ORDER BY rowid DESC LIMIT 1", (task_id,))' \
  '    if False:
        pr = store.row("SELECT * FROM prs WHERE task_id=? ORDER BY rowid DESC LIMIT 1", (task_id,))'
run b6-transcript-claimed-when-absent "$P/evidence.py" \
  '            "transcript_included": transcript is not None, "pr_number": pr_number}' \
  '            "transcript_included": True, "pr_number": pr_number}'
run b7-backup-overwrites "$P/evidence.py" \
  '        raise StateCorrupt(f"refusing to back up onto {dest_path}: it already exists")' \
  '        pass'
