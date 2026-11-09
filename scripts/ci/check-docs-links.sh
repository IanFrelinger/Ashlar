#!/usr/bin/env bash
# Keep the action's Markdown report, empty-input guard and real lychee exit status.
set -euo pipefail
mkdir -p lychee
rm -f lychee/out.md
status=0
lychee --mode task --format markdown --output lychee/out.md \
  --no-progress --max-concurrency 8 --user-agent "ashlar-docs-link-check" \
  README.md "docs/**/*.md" || status=$?
if [[ -f lychee/out.md ]]; then
  cat lychee/out.md
  if [[ -n "${GITHUB_STEP_SUMMARY:-}" ]]; then
    cat lychee/out.md >> "${GITHUB_STEP_SUMMARY}"
  fi
  if grep -Eq 'Total\s+\|\s+0' lychee/out.md; then
    echo "No links were found. Refusing an empty link check." >&2
    [[ "${status}" -ne 0 ]] || status=1
  fi
else
  echo "lychee did not produce a report." >&2
  [[ "${status}" -ne 0 ]] || status=1
fi
if [[ -n "${GITHUB_OUTPUT:-}" ]]; then
  echo "exit_code=${status}" >> "${GITHUB_OUTPUT}"
fi
exit "${status}"
