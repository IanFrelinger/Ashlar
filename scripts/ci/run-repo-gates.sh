#!/usr/bin/env bash
# Run every repo-hygiene gate that is a bare invocation, discovered rather than listed.
#
# WHY THIS EXISTS. shell-lint.yml grew one step per gate, each step a single `bash …` or
# `python3 …` line appended to the same list. That list became the repository's worst merge
# conflict: four consecutive pull requests conflicted on it, and every resolution was identical -
# keep both. An append-only list in one file is a structural conflict generator, and the conflict
# carries real risk, because resolving a YAML conflict by hand is how a step gets silently dropped.
#
# Adding a gate is now adding a FILE. Nothing shared is edited, so nothing conflicts.
#
# WHAT IS DISCOVERED
#   tests/scripts/*.test.sh                        script-level tests
#   scripts/ci/test-*.sh, scripts/ci/test-*.py     tests of a checker
#   scripts/ci/verify-*.sh, scripts/ci/verify-*.py  repository invariants
#   scripts/knowledge-graph/verify-*-current.py    derived-artifact drift
#
# `--list` prints the discovered set and exits, after the floor check, so a caller can assert a
# particular gate is reached rather than assume a glob covers it.
#
# Anything needing arguments, fixtures or a working directory is NOT discoverable and keeps its own
# step in the workflow. EXCLUDED lists those explicitly, with the reason, so an exclusion is a
# decision someone reads rather than a silent omission.
#
# EVERY gate runs even after one fails. A gate runner that stops at the first failure turns a
# ten-minute CI cycle into ten of them.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${ROOT}"

# Probe by RUNNING each candidate, never by `command -v`: on Windows an App Execution Alias stub
# answers `command -v python3` and then exits 49 without executing anything, so every python gate
# below would report a failure it had never run. CI is Linux and takes python3 on the first try.
PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'print(1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "repo-gates: ERROR: no working python interpreter (tried python3, python, py)." >&2
  exit 1
fi

# Discovered by pattern but deliberately not run bare.
EXCLUDED=(
  # Takes fixture paths as arguments; exercised by scripts/ci/test-commercial-receipts.py.
  "scripts/ci/verify-commercial-receipts.py"

  # Deliberately NOT discovered. CommercialReceiptRoutingTests asserts this runs as a direct,
  # unconditional command in a required job, and checks that job cannot be skipped, made
  # continue-on-error, or path-filtered into silence. Discovery is a weaker guarantee: a rename
  # outside the glob would drop it while the gate count stayed above its floor. For a commercial
  # evidence control that trade is wrong, so this one keeps its own step in shell-lint.yml.
  "scripts/ci/test-commercial-receipts.py"
)

# Below this, a glob has stopped matching and this runner is green having run almost nothing -
# the exact shape these gates exist to prevent. Chosen under the count at the time of writing (13).
MINIMUM_GATES=11

is_excluded() {
  local candidate="$1" e
  for e in "${EXCLUDED[@]}"; do [[ "${candidate}" == "${e}" ]] && return 0; done
  return 1
}

GATES=()
while IFS= read -r f; do
  [[ -n "${f}" ]] || continue
  is_excluded "${f}" || GATES+=("${f}")
done < <(
  {
    ls -1 tests/scripts/*.test.sh
    ls -1 scripts/ci/test-*.sh scripts/ci/test-*.py
    ls -1 scripts/ci/verify-*.sh scripts/ci/verify-*.py
    ls -1 scripts/knowledge-graph/verify-*-current.py
  } 2>/dev/null | sort -u
)

LIST_ONLY=0
[[ "${1:-}" == "--list" ]] && LIST_ONLY=1

echo "repo-gates: discovered ${#GATES[@]} gate(s)"

if [[ "${#GATES[@]}" -lt "${MINIMUM_GATES}" ]]; then
  echo "repo-gates: ERROR: discovered ${#GATES[@]} gate(s), expected at least ${MINIMUM_GATES}." >&2
  echo "            A glob has stopped matching, so this runner would pass having checked almost" >&2
  echo "            nothing. Fix the discovery; do not lower the bound." >&2
  exit 1
fi

# --list prints what discovery reached and stops. It runs AFTER the floor check above, so a
# listing is only produced by a discovery that is actually working.
if [[ "${LIST_ONLY}" -eq 1 ]]; then
  printf '%s
' "${GATES[@]}"
  exit 0
fi

FAILED=()
for g in "${GATES[@]}"; do
  echo
  echo "::group::${g}"
  case "${g}" in
    *.py) "${PY_BIN}" "${g}" ;;
    *)    bash "${g}" ;;
  esac
  rc=$?
  echo "::endgroup::"
  if [[ "${rc}" -ne 0 ]]; then
    echo "::error::${g} failed (exit ${rc})"
    FAILED+=("${g}")
  fi
done

echo
if [[ "${#FAILED[@]}" -gt 0 ]]; then
  echo "repo-gates: ${#FAILED[@]} of ${#GATES[@]} gate(s) failed:" >&2
  printf '  %s\n' "${FAILED[@]}" >&2
  exit 1
fi
echo "repo-gates: all ${#GATES[@]} gate(s) passed"
