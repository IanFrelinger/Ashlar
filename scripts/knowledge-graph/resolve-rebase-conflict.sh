#!/usr/bin/env bash
# Resolve docs/knowledge-graph.{json,md} during a rebase/merge by regenerating them.
#
# WHY. Those two files are generated from `git ls-files`. Rebases that touch the tree almost
# always conflict them, and hand-merging the JSON is wrong: the next shell-lint run regenerates
# and fails the byte compare. The fix is always the same — take either side, stage the rest of
# the tip, regenerate, stage the outputs.
#
# USAGE (mid-rebase or mid-merge, with KG files conflicted or stale):
#   bash scripts/knowledge-graph/resolve-rebase-conflict.sh
#
# Optional:
#   --dry-run   print what would run; write nothing; exit 0 if a git checkout is usable
#
# Not discovered by scripts/ci/run-repo-gates.sh (outside its globs). Covered by
# tests/scripts/resolve-knowledge-graph-rebase.test.sh.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
cd "${ROOT}"

DRY_RUN=0
for arg in "$@"; do
  case "${arg}" in
    --dry-run) DRY_RUN=1 ;;
    -h|--help)
      sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//'
      exit 0
      ;;
    *)
      echo "resolve-knowledge-graph: unknown argument: ${arg}" >&2
      exit 2
      ;;
  esac
done

if ! git rev-parse --git-dir >/dev/null 2>&1; then
  echo "resolve-knowledge-graph: ERROR: not inside a git checkout." >&2
  exit 1
fi

PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'print(1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "resolve-knowledge-graph: ERROR: no working python interpreter." >&2
  exit 1
fi

BUILDER="${ROOT}/scripts/knowledge-graph/build-knowledge-graph.py"
if [[ ! -f "${BUILDER}" ]]; then
  echo "resolve-knowledge-graph: ERROR: missing ${BUILDER}" >&2
  exit 1
fi

KG_JSON="docs/knowledge-graph.json"
KG_MD="docs/knowledge-graph.md"

in_conflict() {
  git diff --name-only --diff-filter=U 2>/dev/null | grep -qx "$1"
}

if [[ "${DRY_RUN}" -eq 1 ]]; then
  echo "resolve-knowledge-graph: dry-run OK (builder=${BUILDER}, py=${PY_BIN})"
  exit 0
fi

# Unblock conflicted generated files. Either side is fine: we regenerate next.
for f in "${KG_JSON}" "${KG_MD}"; do
  if in_conflict "${f}"; then
    # During rebase, --ours is the branch being rebased onto (usually master).
    if git checkout --ours -- "${f}" 2>/dev/null; then
      git add -- "${f}"
      echo "resolve-knowledge-graph: cleared conflict on ${f} (ours), will regenerate"
    else
      git checkout --theirs -- "${f}" 2>/dev/null || true
      git add -- "${f}" 2>/dev/null || true
      echo "resolve-knowledge-graph: cleared conflict on ${f} (theirs), will regenerate"
    fi
  fi
done

# Stage other paths first so git ls-files sees new tip files the builder must count.
# Do not `git add -A` the whole tree (would scoop .worktrees / build junk); only ensure
# the two outputs are not left conflicted before regenerate.
"${PY_BIN}" "${BUILDER}"
git add -- "${KG_JSON}" "${KG_MD}"

echo "resolve-knowledge-graph: regenerated and staged ${KG_JSON} ${KG_MD}"
echo "resolve-knowledge-graph: if a rebase/merge is in progress, continue it (git rebase --continue / git merge --continue)."
