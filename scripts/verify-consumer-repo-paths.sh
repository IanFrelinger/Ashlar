#!/usr/bin/env bash
# Reject repository paths in generated consumer text; scan errors are failures too.
set -euo pipefail

CONSUMER_ROOT="${1:?usage: verify-consumer-repo-paths.sh <consumer-directory>}"
if [[ ! -d "${CONSUMER_ROOT}" ]]; then
  echo "Consumer path scan input is not a directory: ${CONSUMER_ROOT}" >&2
  exit 2
fi

# -I skips binary payloads, whose SourceLink/PDB data may name the repository.
REPO_PATH_PATTERN='Ashlar\.Core\.Domain\.csproj|/workspace|src/Ashlar'
status=0
grep -rEnI "${REPO_PATH_PATTERN}" "${CONSUMER_ROOT}" || status=$?
case "${status}" in
  0) echo "Generated consumer tree contains repo-relative Ashlar paths." >&2; exit 1 ;;
  1) echo "consumer-repo-paths: clean" ;;
  *) echo "Consumer path scan failed (grep exit ${status})." >&2; exit "${status}" ;;
esac
