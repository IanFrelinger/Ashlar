#!/usr/bin/env bash
# Print the verdict the Full Platform Readiness Gate recorded for ONE commit, and exit 0 only if it
# is `verified`.
#
# WHY. `Readiness summary` is a required check that must pass when its heavy lanes are skipped, so
# its green tick cannot tell "every platform passed" from "no platform ran". The job computes a
# verdict that can (verified | partial | not-verified | failed), but job outputs are readable only
# inside the run and the step summary is not exposed by the Checks API. The job therefore also
# emits the verdict as a check-run annotation titled "Readiness verdict", and this script reads
# that annotation for a SHA. A release sign-off cites this script's output, not the tick.
#
# Usage:  bash scripts/release/readiness-verdict-for-sha.sh <full 40-character commit SHA>
#
# Exit:   0  VERIFIED      every lane group ran and passed on this exact commit, read from GitHub.
#                          Nothing else exits 0.
#         1  REFUSED       the recorded evidence does not support a sign-off; the last line says why
#                          (no-readiness-run, missing-annotation, conflicting-verdicts, failed,
#                          partial, not-verified, merge-commit-only, in-progress,
#                          malformed-annotation, foreign-check-run, sha-mismatch).
#         2  UNDETERMINED  this script could not read the evidence (usage, no python, gh missing or
#                          unauthenticated, an API error, an unreadable fixture).
#         3  FIXTURE       READINESS_VERDICT_FIXTURE was set and the fixture reads as VERIFIED. A
#                          fixture is never evidence, so this is never 0, and
#                          `READINESS_VERDICT_FIXTURE=... bash <this script> $SHA && git tag` does
#                          not tag. A fixture that reads REFUSED or UNDETERMINED keeps 1 or 2.
#
# `partial` is REFUSED, with no override. On push, schedule and workflow_dispatch every lane group
# runs, so only a pull_request or merge_group run can be partial - and those never build the
# production images. A pull_request run also tests refs/pull/N/merge rather than the SHA its check
# is attached to, so even a `verified` from one is refused (merge-commit-only).
#
# Several runs of one SHA (a push run and a dispatch, say) are each read at their LATEST attempt, as
# branch protection reads a re-run. Any `failed` among them refuses, and if another run passed that
# is reported as conflicting-verdicts; partial or not-verified beside a verified run is weaker
# evidence, not a contradiction.
#
# Repository: whatever gh resolves for `{owner}/{repo}` - the git remote of the current directory,
# or GH_REPO=owner/repo. Read-only: this script only issues GET requests.
#
# Offline: READINESS_VERDICT_FIXTURE=<file.json> reads one evidence document instead of calling
# GitHub (shape in scripts/release/readiness-verdict-for-sha.py). Output then says so on its first
# and last lines, and a verified reading exits 3 rather than 0, because a fixture is never evidence.
set -uo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
EVALUATOR="${HERE}/readiness-verdict-for-sha.py"
PREFIX="readiness-verdict-for-sha"

undetermined() {
  local reason="$1"; shift
  local line
  for line in "$@"; do echo "${line}"; done
  echo
  echo "${PREFIX}: ${SHA:-<no sha>} UNDETERMINED (${reason})"
  exit 2
}

SHA="${1:-}"
if [[ $# -ne 1 || ! "${SHA}" =~ ^[0-9a-f]{40}$ ]]; then
  SHA=""
  undetermined usage \
    "usage: bash scripts/release/readiness-verdict-for-sha.sh <full 40-character commit SHA>" \
    "Pass the commit you are about to tag, in full and lower case: git rev-parse HEAD" \
    "A branch name or short SHA is refused on purpose - the API would resolve it to whatever it" \
    "points at when asked, which is not necessarily the commit being released."
fi

# PROBE the interpreter, do not merely locate it: on Windows `python3` can be an App Execution
# Alias stub that exists, is executable, and exits 49 without running anything.
PY_BIN=""
for candidate in python3 python py; do
  if command -v "${candidate}" >/dev/null 2>&1 && "${candidate}" -c 'print(1)' >/dev/null 2>&1; then
    PY_BIN="${candidate}"
    break
  fi
done
[[ -n "${PY_BIN}" ]] || undetermined no-python "no working python on PATH (tried python3, python, py)."
[[ -f "${EVALUATOR}" ]] || undetermined no-evaluator "missing ${EVALUATOR}"

# The fixture path goes to python as an ARGUMENT, never through the environment: MSYS rewrites a
# POSIX path argument for a native Windows python, but not an environment variable.
if [[ -n "${READINESS_VERDICT_FIXTURE:-}" ]]; then
  "${PY_BIN}" "${EVALUATOR}" evaluate "${SHA}" fixture "${READINESS_VERDICT_FIXTURE}"
  exit $?
fi

command -v gh >/dev/null 2>&1 || undetermined gh-missing \
  "gh (the GitHub CLI) is not on PATH. Install it and run: gh auth login"

WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

if ! gh auth status >"${WORK}/auth.txt" 2>&1; then
  undetermined gh-unauthenticated \
    "gh is not authenticated, so the check runs for ${SHA} cannot be read. gh auth status said:" \
    "$(sed 's/^/    /' "${WORK}/auth.txt")" \
    "Run: gh auth login   (read access to the repository is enough). Refusing to guess."
fi

# fetch <endpoint> <jq filter or ''> <output file>
fetch() {
  local endpoint="$1" filter="$2" out="$3"
  local -a args=(api "${endpoint}")
  [[ -n "${filter}" ]] && args=(api --paginate "${endpoint}" --jq "${filter}")
  if ! gh "${args[@]}" >"${out}" 2>"${WORK}/err.txt"; then
    undetermined api-error \
      "GET ${endpoint} failed. gh said:" \
      "$(sed 's/^/    /' "${WORK}/err.txt")" \
      "A commit GitHub has never seen (not pushed) answers 422 here."
  fi
}

# filter=all, not the default filter=latest: every attempt comes back, so the evaluator can say
# which attempt it read and which it superseded instead of trusting an unseen server-side choice.
fetch "repos/{owner}/{repo}/commits/${SHA}/check-runs?check_name=Readiness%20summary&filter=all&per_page=100" \
  '.check_runs[]' "${WORK}/check-runs.ndjson"
# Only used to explain an absence: a run still in progress has no summary check run yet, and that
# must not be reported as "the path filter skipped this commit". Provenance is checked against the
# per-run fetch below, never against this listing (measured: it can transiently omit a run).
fetch "repos/{owner}/{repo}/actions/workflows/full-platform-readiness-gate.yml/runs?head_sha=${SHA}&per_page=100" \
  '.workflow_runs[]' "${WORK}/workflow-runs.ndjson"

PLAN="$("${PY_BIN}" "${EVALUATOR}" plan "${WORK}")" \
  || undetermined malformed-evidence "could not read the check runs GitHub returned for ${SHA}."
while read -r kind id; do
  id="${id%$'\r'}"
  [[ "${id}" =~ ^[0-9]+$ ]] || continue
  case "${kind}" in
    annotations) fetch "repos/{owner}/{repo}/check-runs/${id}/annotations?per_page=100" '.[]' "${WORK}/annotations-${id}.ndjson" ;;
    run)         fetch "repos/{owner}/{repo}/actions/runs/${id}" '' "${WORK}/run-${id}.json" ;;
  esac
done <<< "${PLAN}"

"${PY_BIN}" "${EVALUATOR}" evaluate "${SHA}" dir "${WORK}"
exit $?
