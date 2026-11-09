#!/usr/bin/env bash
# Bootstrap without Python or dirname dependencies; checker probes are bounded.
set -euo pipefail
for candidate in python3 python py; do
  if command -v "$candidate" >/dev/null 2>&1; then
    # exec preserves failure, including broken interpreter shims; do not mask it.
    exec "$candidate" "${BASH_SOURCE[0]%/*}/check-prerequisites.py" "$@"
  fi
done
echo 'preflight: FAIL no Python interpreter found (python3, python, py). Install Python 3.11+ and put it on PATH, then retry.' >&2
exit 1
