#!/usr/bin/env bash
# Tests for scripts/handoff-publish.sh and scripts/handoff-fetch.sh.
#
# WHY A TEST. A phase ends by publishing its handoff, and the next thread starts by fetching it
# (_handoff/phases/README.md). If publish silently writes nothing, writes the wrong branch, drops an earlier
# phase's file, or moves the caller's checkout, the next thread starts from a stale or missing handoff and no
# one notices until work is lost. Each such shape is driven here against a real git repository and a bare
# "remote", and every refusal case asserts its exit code and message, so "it always refuses" cannot pass.
#
# Run:  bash tests/scripts/handoff-scripts.test.sh
# Bash and git only: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
PUB="${ROOT}/scripts/handoff-publish.sh"
FETCH="${ROOT}/scripts/handoff-fetch.sh"

# Bump when you add an assertion; the check at the bottom says why.
EXPECTED_ASSERTIONS=45

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   - $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL - $1"; echo "         $2"; }
expect_eq() { if [[ "$2" == "$3" ]]; then ok "$1"; else bad "$1" "expected [$3], got [$2]"; fi; }
expect_has() { if [[ "$2" == *"$3"* ]]; then ok "$1"; else bad "$1" "expected output to contain [$3], got [$2]"; fi; }

for s in "$PUB" "$FETCH"; do
  if [[ -f "$s" ]]; then ok "script exists: ${s#"$ROOT"/}"; else bad "script exists: ${s#"$ROOT"/}" "missing"; fi
done

T="$(mktemp -d)"
trap 'rm -rf "$T"' EXIT
export GIT_AUTHOR_NAME=t GIT_AUTHOR_EMAIL=t@example.invalid GIT_COMMITTER_NAME=t GIT_COMMITTER_EMAIL=t@example.invalid
export GIT_CONFIG_NOSYSTEM=1 HOME="$T/home"
mkdir -p "$HOME"
git config --global init.defaultBranch master
git config --global protocol.file.allow always

git init -q --bare "$T/remote.git"
git clone -q "$T/remote.git" "$T/work" 2>/dev/null
( cd "$T/work" && echo "readme" > README.md && git add README.md && git commit -qm init && git push -q origin master )
MASTER="$(git -C "$T/work" rev-parse HEAD)"
WS=spec-007-pr4
BR="claude/${WS}-workspace"
printf 'phase A handoff\n' > "$T/hA.md"
printf 'phase B handoff\n' > "$T/hB.md"
printf 'design notes\n' > "$T/DESIGN.md"
remote_head() { git -C "$T/remote.git" rev-parse -q --verify "refs/heads/$BR" || true; }
show() { git -C "$T/remote.git" show "$1" 2>/dev/null; }

echo "# publish: a new storage branch starts from master"
out="$(bash "$PUB" --repo "$T/work" --workstream "$WS" --phase A --file "$T/hA.md" 2>&1)"; rc=$?
expect_eq "first publish exits 0" "$rc" "0"
expect_has "first publish reports the branch" "$out" "$BR @ "
C1="$(remote_head)"
expect_eq "the storage branch is on the remote" "$([[ -n "$C1" ]] && echo yes)" "yes"
expect_eq "its parent is master" "$(git -C "$T/remote.git" rev-parse "${C1}^")" "$MASTER"
expect_eq "handoff.md holds the phase A text" "$(show "$C1:_handoff/$WS/handoff.md")" "phase A handoff"
expect_eq "handoff-phase-A.md holds the same text" "$(show "$C1:_handoff/$WS/handoff-phase-A.md")" "phase A handoff"
expect_eq "master's own files are kept" "$(show "$C1:README.md")" "readme"
expect_eq "the checkout stays on master" "$(git -C "$T/work" symbolic-ref --short HEAD)" "master"
expect_eq "HEAD does not move" "$(git -C "$T/work" rev-parse HEAD)" "$MASTER"
expect_eq "the working tree stays clean" "$(git -C "$T/work" status --porcelain)" ""
expect_eq "the default message names workstream and phase" "$(git -C "$T/remote.git" log -1 --format=%s "$C1")" "handoff(${WS}): phase A"

echo "# publish: the next phase builds on the remote branch and keeps history"
out="$(bash "$PUB" --repo "$T/work" --workstream "$WS" --phase B --file "$T/hB.md" --attach "$T/DESIGN.md" --message "phase B done" 2>&1)"; rc=$?
expect_eq "second publish exits 0" "$rc" "0"
C2="$(remote_head)"
expect_eq "its parent is the phase A commit" "$(git -C "$T/remote.git" rev-parse "${C2}^")" "$C1"
expect_eq "handoff.md now holds phase B" "$(show "$C2:_handoff/$WS/handoff.md")" "phase B handoff"
expect_eq "handoff-phase-A.md is still there" "$(show "$C2:_handoff/$WS/handoff-phase-A.md")" "phase A handoff"
expect_eq "the attachment is stored by basename" "$(show "$C2:_handoff/$WS/DESIGN.md")" "design notes"
expect_eq "--message is used" "$(git -C "$T/remote.git" log -1 --format=%s "$C2")" "phase B done"

echo "# publish: unchanged content makes no commit"
out="$(bash "$PUB" --repo "$T/work" --workstream "$WS" --phase B --file "$T/hB.md" --attach "$T/DESIGN.md" 2>&1)"; rc=$?
expect_eq "re-publishing the same files exits 0" "$rc" "0"
expect_has "it says nothing changed" "$out" "nothing changed"
expect_eq "the remote branch did not move" "$(remote_head)" "$C2"

echo "# publish: a fresh clone with no local storage branch still builds on the remote one"
git clone -q "$T/remote.git" "$T/fresh" 2>/dev/null
printf 'phase C handoff\n' > "$T/hC.md"
bash "$PUB" --repo "$T/fresh" --workstream "$WS" --phase C --file "$T/hC.md" >/dev/null 2>&1; rc=$?
expect_eq "publish from a fresh clone exits 0" "$rc" "0"
C3="$(remote_head)"
expect_eq "its parent is the remote phase B commit, not master" "$(git -C "$T/remote.git" rev-parse "${C3}^")" "$C2"

echo "# publish: --no-push moves only the local branch"
printf 'phase D handoff\n' > "$T/hD.md"
bash "$PUB" --repo "$T/fresh" --workstream "$WS" --phase D --file "$T/hD.md" --no-push >/dev/null 2>&1; rc=$?
expect_eq "--no-push exits 0" "$rc" "0"
expect_eq "the remote branch did not move" "$(remote_head)" "$C3"
expect_eq "the local branch has phase D" "$(git -C "$T/fresh" show "refs/heads/$BR:_handoff/$WS/handoff.md")" "phase D handoff"

echo "# publish: refusals"
refuse() { # <name> <expected exit> <expected message part> <args...>
  local name="$1" code="$2" msg="$3"; shift 3
  local o r
  o="$(bash "$PUB" "$@" 2>&1)"; r=$?
  if [[ $r -eq $code && "$o" == *"$msg"* ]]; then ok "$name"; else bad "$name" "exit $r (want $code), output: $o"; fi
}
refuse "an uppercase workstream is refused" 2 "lowercase" --repo "$T/work" --workstream Spec7 --phase A --file "$T/hA.md"
refuse "a workstream with a slash is refused" 2 "lowercase" --repo "$T/work" --workstream "a/b" --phase A --file "$T/hA.md"
refuse "a phase with punctuation is refused" 2 "letters and digits" --repo "$T/work" --workstream "$WS" --phase "B-1" --file "$T/hA.md"
refuse "a missing handoff file is refused" 2 "missing or empty" --repo "$T/work" --workstream "$WS" --phase A --file "$T/nope.md"
: > "$T/empty.md"
refuse "an empty handoff file is refused" 2 "missing or empty" --repo "$T/work" --workstream "$WS" --phase A --file "$T/empty.md"
cp "$T/hA.md" "$T/handoff.md"
refuse "an attachment named handoff.md is refused" 2 "overwrite the handoff" --repo "$T/work" --workstream "$WS" --phase A --file "$T/hA.md" --attach "$T/handoff.md"
git -C "$T/work" fetch -q origin "$BR:$BR" && git -C "$T/work" checkout -q "$BR"
refuse "publishing while the storage branch is checked out is refused" 2 "is checked out here" --repo "$T/work" --workstream "$WS" --phase E --file "$T/hA.md"
git -C "$T/work" checkout -q master

echo "# fetch"
out="$(bash "$FETCH" --repo "$T/work" --workstream "$WS" 2>/dev/null)"; rc=$?
expect_eq "fetch exits 0" "$rc" "0"
expect_eq "fetch prints the latest pushed handoff (phase C)" "$out" "phase C handoff"
expect_eq "--phase A prints the phase A copy" "$(bash "$FETCH" --repo "$T/work" --workstream "$WS" --phase A 2>/dev/null)" "phase A handoff"
bash "$FETCH" --repo "$T/work" --workstream "$WS" --out "$T/got.md" 2>/dev/null; rc=$?
expect_eq "--out exits 0" "$rc" "0"
expect_eq "--out writes the file" "$(cat "$T/got.md")" "phase C handoff"
expect_has "--list names the phase files and attachments" "$(bash "$FETCH" --repo "$T/work" --workstream "$WS" --list 2>/dev/null | tr '\n' ' ')" "DESIGN.md handoff-phase-A.md handoff-phase-B.md handoff-phase-C.md handoff.md"
expect_eq "a fetch from the remote prints no warning" "$(bash "$FETCH" --repo "$T/work" --workstream "$WS" 2>&1 >/dev/null)" ""
# $T/fresh holds a local storage branch at phase D that was never pushed; the remote is at phase C.
out="$(bash "$FETCH" --repo "$T/fresh" --remote no-such-remote --workstream "$WS" 2>/dev/null)"
expect_eq "with no reachable remote, fetch falls back to the local branch" "$out" "phase D handoff"
expect_has "and warns that it read the local branch" "$(bash "$FETCH" --repo "$T/fresh" --remote no-such-remote --workstream "$WS" 2>&1 >/dev/null)" "reading the LOCAL branch, which may be stale"
bash "$FETCH" --repo "$T/work" --workstream no-such-ws >/dev/null 2>&1; rc=$?
expect_eq "an unknown workstream exits 4" "$rc" "4"
bash "$FETCH" --repo "$T/work" --workstream "$WS" --phase Z >/dev/null 2>&1; rc=$?
expect_eq "an unknown phase exits 4" "$rc" "4"

echo
echo "handoff-scripts: ${PASS} passed, ${FAIL} failed"
TOTAL=$((PASS + FAIL))
# A test that silently stops early (set -u, a missing tool) would print "0 failed" with fewer assertions.
if [[ $TOTAL -ne $EXPECTED_ASSERTIONS ]]; then
  echo "handoff-scripts: ran ${TOTAL} assertions, expected ${EXPECTED_ASSERTIONS}" >&2
  exit 1
fi
[[ $FAIL -eq 0 ]]
