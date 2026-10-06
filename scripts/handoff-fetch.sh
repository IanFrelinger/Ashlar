#!/usr/bin/env bash
# Print, or save, the latest handoff of a workstream from its storage branch.
#
# The counterpart of scripts/handoff-publish.sh. A new thread runs this first (through /start-phase) to get
# the handoff the previous phase left on claude/<workstream>-workspace.
#
# Usage:
#   scripts/handoff-fetch.sh --workstream <name> [--phase <P>] [--out <file>] [--list]
#                            [--repo <dir>] [--remote <name>]
#
#   --phase  read handoff-phase-<P>.md instead of the latest handoff.md
#   --out    write the handoff to <file> instead of stdout
#   --list   list the files stored for the workstream instead
#
# Exit: 0 found, 2 usage error, 4 no storage branch or no such handoff.

set -euo pipefail

WS="" PHASE="" OUT="" LIST=0 REPO="." REMOTE="origin"

die() { echo "handoff-fetch: $1" >&2; exit "${2:-2}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --workstream) WS="${2:?--workstream needs a value}"; shift 2 ;;
    --phase) PHASE="${2:?--phase needs a value}"; shift 2 ;;
    --out) OUT="${2:?--out needs a path}"; shift 2 ;;
    --list) LIST=1; shift ;;
    --repo) REPO="${2:?--repo needs a directory}"; shift 2 ;;
    --remote) REMOTE="${2:?--remote needs a name}"; shift 2 ;;
    -h|--help) sed -n '2,16p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ -n "$WS" ]] || die "--workstream is required"
[[ "$WS" =~ ^[a-z0-9]+(-[a-z0-9]+)*$ ]] || die "workstream '$WS' must be lowercase letters, digits and single dashes"
[[ -z "$PHASE" || "$PHASE" =~ ^[A-Za-z0-9]+$ ]] || die "phase '$PHASE' must be letters and digits"

cd "$REPO" || die "no such repo directory: $REPO"
BRANCH="claude/${WS}-workspace"
DIR="_handoff/${WS}"

REF=""
if git remote get-url "$REMOTE" >/dev/null 2>&1 \
  && git fetch -q "$REMOTE" "refs/heads/${BRANCH}:refs/remotes/${REMOTE}/${BRANCH}" 2>/dev/null; then
  REF="refs/remotes/${REMOTE}/${BRANCH}"
elif git rev-parse -q --verify "refs/heads/${BRANCH}" >/dev/null; then
  # Say so: a local branch can be behind the remote one, and the reader would take it for current.
  echo "handoff-fetch: warning: could not fetch ${BRANCH} from ${REMOTE}; reading the LOCAL branch, which may be stale" >&2
  REF="refs/heads/${BRANCH}"
else
  die "no storage branch $BRANCH (on $REMOTE or locally)" 4
fi

if [[ $LIST -eq 1 ]]; then
  git ls-tree --name-only "${REF}:${DIR}" 2>/dev/null || die "$BRANCH has no $DIR" 4
  exit 0
fi

NAME="handoff.md"
[[ -n "$PHASE" ]] && NAME="handoff-phase-${PHASE}.md"
git cat-file -e "${REF}:${DIR}/${NAME}" 2>/dev/null || die "$BRANCH has no ${DIR}/${NAME}" 4
if [[ -n "$OUT" ]]; then
  git show "${REF}:${DIR}/${NAME}" > "$OUT"
  echo "handoff-fetch: ${DIR}/${NAME} from ${BRANCH} @ $(git rev-parse --short "$REF") -> $OUT" >&2
else
  git show "${REF}:${DIR}/${NAME}"
fi
