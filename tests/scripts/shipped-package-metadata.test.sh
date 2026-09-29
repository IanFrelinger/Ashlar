#!/usr/bin/env bash
# Every project a release pushes to nuget.org must say what it is.
#
# Ten of the twenty-two packages this repository publishes carried the SDK's placeholder
# description - the nuget.org page for each showed "Package Description" and nothing else. Nobody
# noticed for three releases, because a missing <Description> is a build WARNING at most and the
# pack step succeeds.
#
# The set is read from docs/knowledge-graph.json, which derives it from the release sources
# themselves (scripts/pack-ashlar-hosting-graph.sh and the inline packs in
# reusable-release-nuget.yml) rather than from <IsPackable> - that property DEFAULTS TO TRUE, so
# testing for it answers a different question and answers it wrongly in both directions.
#
# Run:  bash tests/scripts/shipped-package-metadata.test.sh
# Pure bash + python3: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GRAPH="${ROOT}/docs/knowledge-graph.json"

PY_BIN=""
for candidate in python3 python py; do
  if command -v "${candidate}" >/dev/null 2>&1 && "${candidate}" -c 'print(1)' >/dev/null 2>&1; then
    PY_BIN="${candidate}"; break
  fi
done
[[ -n "${PY_BIN}" ]] || { echo "FAIL - no working python3; this file will not pretend to test anything."; exit 1; }
[[ -f "${GRAPH}" ]] || { echo "FAIL - ${GRAPH} is missing; regenerate the knowledge graph first."; exit 1; }

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

# Trailing  is stripped because a Windows interpreter prints CRLF, and a path carrying 
# fails to open while looking exactly like a missing <Description> - a Windows-only wrong
# answer that CI, being Linux, would never have shown us.
#
# Hand the path through the ENVIRONMENT, and translate it for a Windows interpreter first: under
# Git Bash, ${GRAPH} is an MSYS path (/c/...) that python.exe cannot open, and the failure looks
# exactly like "the graph lists nothing", which is a different bug entirely.
GRAPH_FOR_PY="${GRAPH}"
if command -v cygpath >/dev/null 2>&1; then
  GRAPH_FOR_PY="$(cygpath -m "${GRAPH}")"
fi

SHIPPED=()
while IFS= read -r line; do
  # Strip a trailing CR rather than piping through tr: a Windows interpreter prints CRLF, and
  # a path carrying CR fails to open while looking exactly like a missing <Description> - a
  # Windows-only wrong answer that CI, being Linux, would never have shown us.
  # No CR stripping needed: the producer below emits LF endings explicitly.
  [[ -n "${line}" ]] && SHIPPED+=("${line}")
done < <(ASHLAR_GRAPH="${GRAPH_FOR_PY}" "${PY_BIN}" -c "
import json, os, sys
# Windows text mode would translate each newline to CRLF, and a path carrying a
# trailing CR fails to open while looking exactly like a missing <Description> -
# a Windows-only wrong answer that CI, being Linux, would never surface.
sys.stdout.reconfigure(newline=chr(10))
g = json.load(open(os.environ['ASHLAR_GRAPH'], encoding='utf-8'))
for p in g['projects']:
    if p.get('ships_to_nuget'):
        print(p['path'])
")

# POSITIVE CONTROL. An empty set would make every assertion below vacuous, and the likeliest cause
# is the field being renamed or the graph being stale - neither of which should read as a clean run.
if [[ "${#SHIPPED[@]}" -lt 15 ]]; then
  echo "FAIL - the graph lists ${#SHIPPED[@]} projects as shipping to nuget.org, which is too few."
  echo "       Either ships_to_nuget was renamed or the graph is stale. Regenerate it; do not"
  echo "       lower this number."
  exit 1
fi
echo "== ${#SHIPPED[@]} projects ship to nuget.org =="

for rel in "${SHIPPED[@]}"; do
  name="$(basename "${rel}" .csproj)"
  text="$(cat "${ROOT}/${rel}")"

  # A Description that exists but is blank is the same nothing on the package page.
  if grep -qE '<Description>[[:space:]]*[^[:space:]<]' <<<"${text}"; then
    ok "${name} says what it is"
  else
    bad "${name} says what it is" \
        "no non-empty <Description> in ${rel}. This package is pushed to nuget.org, so its page
         shows the SDK placeholder to anyone deciding whether to depend on it. A missing
         description is only a build warning, which is why this went unnoticed for three releases."
  fi
done

echo
echo "passed: ${PASS}   failed: ${FAIL}"
[[ "${FAIL}" -eq 0 ]] || exit 1
