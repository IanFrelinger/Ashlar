#!/usr/bin/env bash
# Gate: scripts/knowledge-graph/resolve-rebase-conflict.sh stays callable and regenerates KG.
#
# Discovered bare by scripts/ci/run-repo-gates.sh (tests/scripts/*.test.sh).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${ROOT}"

SCRIPT="${ROOT}/scripts/knowledge-graph/resolve-rebase-conflict.sh"
BUILDER="${ROOT}/scripts/knowledge-graph/build-knowledge-graph.py"

fail() { echo "FAIL: $*" >&2; exit 1; }

[[ -x "${SCRIPT}" || -f "${SCRIPT}" ]] || fail "missing ${SCRIPT}"
[[ -f "${BUILDER}" ]] || fail "missing ${BUILDER}"

# Executable bit is not required on Windows checkouts; bash can still run it.
bash "${SCRIPT}" --dry-run >/tmp/kg-resolve-dry.out
grep -q 'dry-run OK' /tmp/kg-resolve-dry.out || fail "dry-run did not report OK: $(cat /tmp/kg-resolve-dry.out)"

# Unknown flag must fail closed (not silently ignore).
if bash "${SCRIPT}" --nope >/tmp/kg-resolve-bad.out 2>/tmp/kg-resolve-bad.err; then
  fail "unknown flag should exit non-zero"
fi
grep -qi 'unknown' /tmp/kg-resolve-bad.err || fail "unknown flag stderr should mention unknown"

# Real regenerate in a temp clone-like sandbox: copy committed KG aside, corrupt it, regenerate.
TMP="$(mktemp -d)"
trap 'rm -rf "${TMP}"' EXIT
cp docs/knowledge-graph.json "${TMP}/kg.json.bak"
cp docs/knowledge-graph.md "${TMP}/kg.md.bak"

# Corrupt the committed artifacts, then run the helper (no rebase in progress — just regenerate+stage).
printf '{ "broken": true }\n' > docs/knowledge-graph.json
printf '# broken\n' > docs/knowledge-graph.md

bash "${SCRIPT}"

# Helper must have restored a real graph (schema_version / generated_by).
grep -q 'generated_by' docs/knowledge-graph.json || fail "json not regenerated"
grep -q 'knowledge-graph' docs/knowledge-graph.md || fail "md not regenerated"

# Leave the tree clean for other gates (helper stages the outputs).
git restore --source=HEAD --staged --worktree \
  docs/knowledge-graph.json docs/knowledge-graph.md 2>/dev/null \
  || git checkout HEAD -- docs/knowledge-graph.json docs/knowledge-graph.md

echo "PASS: resolve-knowledge-graph-rebase"
