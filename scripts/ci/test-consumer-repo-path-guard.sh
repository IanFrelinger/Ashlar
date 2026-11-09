#!/usr/bin/env bash
# Exercise the same guard the packed consumer harness calls, without requiring a pack.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GUARD="${ROOT}/scripts/verify-consumer-repo-paths.sh"
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT
mkdir -p "${WORK}/clean" "${WORK}/forbidden"
printf 'ordinary consumer text\n' > "${WORK}/clean/input.txt"
printf '\0src/Ashlar\0' > "${WORK}/clean/binary.bin"
printf 'src/Ashlar/Unexpected.csproj\n' > "${WORK}/forbidden/input.txt"

bash "${GUARD}" "${WORK}/clean"
echo "consumer-path-guard-test: clean-text-and-binary OK"
if bash "${GUARD}" "${WORK}/forbidden" >"${WORK}/match.log" 2>&1; then
  echo "Guard accepted a forbidden path." >&2; exit 1
fi
grep -q 'contains repo-relative Ashlar paths' "${WORK}/match.log"
echo "consumer-path-guard-test: forbidden-text OK"
if bash "${GUARD}" "${WORK}/absent" >"${WORK}/absent.log" 2>&1; then
  echo "Guard accepted missing input." >&2; exit 1
fi
grep -q 'not a directory' "${WORK}/absent.log"
echo "consumer-path-guard-test: missing-input OK"

# Reproduce an actual grep read failure independently of root's permission bypass.
if ( grep() { return 2; }; export -f grep; bash "${GUARD}" "${WORK}/clean" ) >"${WORK}/error.log" 2>&1; then
  echo "Guard accepted a scan error." >&2; exit 1
fi
grep -q 'scan failed (grep exit 2)' "${WORK}/error.log"
echo "consumer-path-guard-test: scan-error OK"

# A missing scanner must also fail, rather than acting like a grep no-match result.
if ( grep() { return 127; }; export -f grep; bash "${GUARD}" "${WORK}/clean" ) >"${WORK}/missing-tool.log" 2>&1; then
  echo "Guard accepted an unavailable scanner." >&2; exit 1
fi
grep -q 'scan failed (grep exit 127)' "${WORK}/missing-tool.log"
echo "consumer-path-guard-test: missing-scanner OK"

# The standalone developer journey must use the same tested, fail-closed guard.
JOURNEY="${ROOT}/scripts/verify-standalone-brick-authoring.sh"
grep -Fq 'bash "${ROOT}/scripts/verify-consumer-repo-paths.sh" "${BRICK_OUT}"' "${JOURNEY}"
grep -Fq '2>&1 | tee "${RESULTS}/repo-relative-paths.log"' "${JOURNEY}"
if grep -q 'if rg ' "${JOURNEY}"; then
  echo 'Standalone journey still has the unchecked ripgrep guard.' >&2; exit 1
fi
echo "consumer-path-guard-test: standalone-journey-wiring OK"
echo "consumer-path-guard-test: 6 controls passed"
