#!/usr/bin/env bash
# Store a phase handoff on its workstream's storage branch and push it.
#
# Work runs one phase per thread (see _handoff/phases/README.md). A phase ends with a handoff file, and the
# next thread starts from it. A cloud session's scratchpad does not survive the thread, so the handoff
# must live on GitHub. This script commits it to the workstream's storage branch,
# claude/<workstream>-workspace, under _handoff/<workstream>/:
#   handoff.md               the latest handoff (what /start-phase reads)
#   handoff-phase-<P>.md     the same text, kept per phase as history
#   <attachment basenames>   any --attach files (design notes, PR-body drafts, ...)
#
# It writes the commit with git plumbing and a private index, so neither the working tree nor the checked-out
# branch changes. The storage branch is never merged; it only holds notes.
#
# Usage:
#   scripts/handoff-publish.sh --workstream <name> --phase <P> --file <handoff.md>
#                              [--attach <path>]... [--message <text>] [--repo <dir>] [--remote <name>]
#                              [--base <ref>] [--no-push]
#
#   --workstream  lowercase letters, digits and dashes (for example spec-007-pr4)
#   --phase       letters and digits (for example B)
#   --base        where a new storage branch starts (default: <remote>/master, else HEAD)
#   --no-push     update the local branch only; the commit is still printed
#
# Exit: 0 published (or nothing changed), 2 usage or input error, 3 git or push failure.

set -euo pipefail

WS="" PHASE="" FILE="" MESSAGE="" REPO="." REMOTE="origin" BASE="" PUSH=1
ATTACH=()

usage() { sed -n '2,26p' "$0" | sed 's/^# \{0,1\}//'; }
die() { echo "handoff-publish: $1" >&2; exit "${2:-2}"; }

while [[ $# -gt 0 ]]; do
  case "$1" in
    --workstream) WS="${2:?--workstream needs a value}"; shift 2 ;;
    --phase) PHASE="${2:?--phase needs a value}"; shift 2 ;;
    --file) FILE="${2:?--file needs a path}"; shift 2 ;;
    --attach) ATTACH+=("${2:?--attach needs a path}"); shift 2 ;;
    --message) MESSAGE="${2:?--message needs text}"; shift 2 ;;
    --repo) REPO="${2:?--repo needs a directory}"; shift 2 ;;
    --remote) REMOTE="${2:?--remote needs a name}"; shift 2 ;;
    --base) BASE="${2:?--base needs a ref}"; shift 2 ;;
    --no-push) PUSH=0; shift ;;
    -h|--help) usage; exit 0 ;;
    *) die "unknown argument: $1" ;;
  esac
done

[[ -n "$WS" && -n "$PHASE" && -n "$FILE" ]] || die "--workstream, --phase and --file are required"
[[ "$WS" =~ ^[a-z0-9]+(-[a-z0-9]+)*$ ]] || die "workstream '$WS' must be lowercase letters, digits and single dashes"
[[ "$PHASE" =~ ^[A-Za-z0-9]+$ ]] || die "phase '$PHASE' must be letters and digits"
[[ -f "$FILE" && -s "$FILE" ]] || die "handoff file '$FILE' is missing or empty"
for a in "${ATTACH[@]+"${ATTACH[@]}"}"; do
  [[ -f "$a" ]] || die "attachment '$a' is missing"
  case "$(basename "$a")" in
    handoff.md|handoff-phase-*.md) die "attachment '$a' would overwrite the handoff itself" ;;
  esac
done

FILE_ABS="$(cd "$(dirname "$FILE")" && pwd)/$(basename "$FILE")"
ATTACH_ABS=()
for a in "${ATTACH[@]+"${ATTACH[@]}"}"; do ATTACH_ABS+=("$(cd "$(dirname "$a")" && pwd)/$(basename "$a")"); done

cd "$REPO" || die "no such repo directory: $REPO"
git rev-parse --git-dir >/dev/null 2>&1 || die "$REPO is not a git repository"

BRANCH="claude/${WS}-workspace"
DIR="_handoff/${WS}"
[[ "$(git symbolic-ref -q --short HEAD || true)" != "$BRANCH" ]] \
  || die "the storage branch $BRANCH is checked out here; switch away first so its working tree cannot go stale"

# The parent: the remote storage branch when it exists, else the local one, else the base.
PARENT=""
HAS_REMOTE=0
if git remote get-url "$REMOTE" >/dev/null 2>&1; then
  HAS_REMOTE=1
  if git ls-remote --exit-code --heads "$REMOTE" "$BRANCH" >/dev/null 2>&1; then
    git fetch -q "$REMOTE" "refs/heads/${BRANCH}:refs/remotes/${REMOTE}/${BRANCH}" || die "fetch of $BRANCH failed" 3
    PARENT="$(git rev-parse "refs/remotes/${REMOTE}/${BRANCH}")"
  fi
fi
if [[ -z "$PARENT" ]] && git rev-parse -q --verify "refs/heads/${BRANCH}" >/dev/null; then
  PARENT="$(git rev-parse "refs/heads/${BRANCH}")"
fi
if [[ -z "$PARENT" ]]; then
  if [[ -n "$BASE" ]]; then
    PARENT="$(git rev-parse -q --verify "${BASE}^{commit}")" || die "base '$BASE' is not a commit"
  elif [[ $HAS_REMOTE -eq 1 ]] && git rev-parse -q --verify "refs/remotes/${REMOTE}/master" >/dev/null; then
    PARENT="$(git rev-parse "refs/remotes/${REMOTE}/master")"
  else
    PARENT="$(git rev-parse HEAD)"
  fi
fi

INDEX="$(mktemp)"
trap 'rm -f "$INDEX"' EXIT
export GIT_INDEX_FILE="$INDEX"
git read-tree "$PARENT"

add() { # <source> <path in tree>
  local blob
  blob="$(git hash-object -w -- "$1")"
  git update-index --add --cacheinfo "100644,${blob},$2"
}
add "$FILE_ABS" "${DIR}/handoff.md"
add "$FILE_ABS" "${DIR}/handoff-phase-${PHASE}.md"
for a in "${ATTACH_ABS[@]+"${ATTACH_ABS[@]}"}"; do add "$a" "${DIR}/$(basename "$a")"; done

TREE="$(git write-tree)"
if [[ "$TREE" == "$(git rev-parse "${PARENT}^{tree}")" ]]; then
  echo "handoff-publish: nothing changed on $BRANCH (${PARENT:0:9}); not committing"
  exit 0
fi

MESSAGE="${MESSAGE:-handoff(${WS}): phase ${PHASE}}"
COMMIT="$(git commit-tree "$TREE" -p "$PARENT" -m "$MESSAGE")" || die "commit-tree failed" 3
git update-ref "refs/heads/${BRANCH}" "$COMMIT"

if [[ $PUSH -eq 1 ]]; then
  [[ $HAS_REMOTE -eq 1 ]] || die "no remote '$REMOTE' to push to (use --no-push)" 3
  git push -q "$REMOTE" "${COMMIT}:refs/heads/${BRANCH}" || die "push of $BRANCH failed" 3
fi

echo "handoff-publish: ${BRANCH} @ ${COMMIT:0:9}: ${DIR}/handoff.md (phase ${PHASE})$([[ $PUSH -eq 1 ]] && echo ", pushed to ${REMOTE}" || echo ", not pushed")"
