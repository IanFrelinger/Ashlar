#!/usr/bin/env bash
# Private-mesh demo: three Ashlar nodes on one machine share one signed brick package.
#
#   1. node-a seals a package with its own key and serves it.
#   2. node-b trusts node-a's key. It pulls the package, verifies it, and holds it under its own policy until a
#      person approves it; the approval is recorded and signed with node-b's own key.
#   3. node-c does not trust node-a's key, so it refuses the same package.
#   4. A tampered copy fails verification, and node-b refuses it.
#
# The gate results inside the package are supplied by this script. The demo shows signing, sharing, trust and
# each node's own policy, not the gate itself (cert-gate tests that). A receiving node decides on the origin's
# signed results; it does not re-run the tests.
#
# Usage: bash scripts/demo/private-mesh-demo.sh   (needs Docker with the compose plugin)
#   ASHLAR_IMAGE=<image>  run another image; the default is the published 0.4.2 image named in the compose file
#   KEEP=1                leave the nodes running afterwards; stop them with
#                         docker compose -p ashlar-private-mesh-demo down -v
# Walkthrough: docs/PrivateMeshDemo.md. Exits 0 only if every check passes.
set -uo pipefail
export MSYS_NO_PATHCONV=1 MSYS2_ARG_CONV_EXCL='*'   # Git Bash on Windows: keep container paths as written

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
COMPOSE_FILE="$ROOT/deploy/compose/docker-compose.private-mesh-demo.yml"
# Docker on Windows needs a Windows path for the host file (path conversion is off for the container paths).
if command -v cygpath >/dev/null 2>&1; then COMPOSE_FILE="$(cygpath -w "$COMPOSE_FILE")"; fi
COMPOSE=(docker compose -f "$COMPOSE_FILE")
CLI='dotnet /app/Ashlar.CLI.dll'        # inside the seeding scripts
CLI_EXEC=(dotnet /app/Ashlar.CLI.dll)    # for commands run in a live node
PROJECT=/data/state/project
PUBLISHED=/data/state/mesh/published
BRICK_FILE=src/Bricks/HelloMeshBrick.cs
WAIT_SECONDS="${WAIT_SECONDS:-90}"

P=0; F=0
pass() { P=$((P + 1)); printf '  PASS  %-44s %s\n' "$1" "${2:-}"; }
fail() { F=$((F + 1)); printf '  FAIL  %-44s %s\n' "$1" "${2:-}"; }
scene() { printf '\n%s\n' "$1"; }

cleanup() {
  if [ "${KEEP:-0}" != 1 ]; then
    "${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT

# Run a one-off container of a node's service with bash, so whatever it writes lands in that node's volume.
# Compose's progress chatter goes to stderr; it is shown only when the step fails.
seed() {
  local err rc
  err="$(mktemp)"
  "${COMPOSE[@]}" run --rm --no-deps -T "${@:3}" --entrypoint bash "$1" -lc "$2" 2>"$err"
  rc=$?
  [ "$rc" -ne 0 ] && cat "$err" >&2
  rm -f "$err"
  return "$rc"
}
# Run a command inside a running node.
on() { local svc="$1"; shift; "${COMPOSE[@]}" exec -T "$svc" "$@"; }
# Wait until a node's log matches a pattern; prints the last matching line.
wait_log() {
  local svc="$1" pattern="$2" line="" i
  for ((i = 0; i < WAIT_SECONDS; i += 3)); do
    line="$("${COMPOSE[@]}" logs --no-color "$svc" 2>&1 | grep -E "$pattern" | tail -1)"
    [ -n "$line" ] && { printf '%s' "$line"; return 0; }
    sleep 3
  done
  return 1
}

# Projects start sealed and refuse every pulled package. The demo opens each node's policy to "proposing":
# bricks may be added, a person approves each one, and these three gate results are required.
OPEN_POLICY='sed -i -e "s/mode: sealed/mode: proposing/" -e "s/gatesRequired: \[\]/gatesRequired: [sandbox, tests, security]/" -e "s/extensions: 0/extensions: 3/" -e "s/mayAdd: \[\]/mayAdd: [brick]/" '"$PROJECT"'/ashlar.policy.yaml'

PROPOSAL='{"id":"hello-mesh","kind":"brick","summary":"HelloMesh brick","proposedBy":"demo-author","proposedAt":"2026-10-29T00:00:00Z","diff":"+1","forgeProposalIds":["f1"],"courses":[{"name":"sandbox","passed":true,"detail":"supplied by the demo script"},{"name":"tests","passed":true,"detail":"supplied by the demo script"},{"name":"security","passed":true,"detail":"supplied by the demo script"}]}'
FORGE='{"Id":"f1","TargetPath":"'"$BRICK_FILE"'","NewContent":"// HelloMesh: a demo brick shared across a private mesh.\n","Summary":"add the HelloMesh brick","CreatedAt":"2026-10-29T00:00:00Z","UpdatedAt":"2026-10-29T00:00:00Z"}'

echo "Ashlar private-mesh demo (image: ${ASHLAR_IMAGE:-the published 0.4.2 image})"
"${COMPOSE[@]}" down -v --remove-orphans >/dev/null 2>&1 || true

scene "Setup: node-a seals a package; node-b trusts node-a; node-c does not"
AFP="$(seed node-a "
  $CLI keys init >/dev/null 2>&1
  $CLI init node-a --path $PROJECT >/dev/null 2>&1 && $OPEN_POLICY
  mkdir -p $PROJECT/.ashlar/forge/proposed $PUBLISHED
  printf '%s' \"\$FORGE\" > $PROJECT/.ashlar/forge/proposed/f1.json
  printf '%s' \"\$PROPOSAL\" > /tmp/proposal.json
  $CLI gates propose --file /tmp/proposal.json --path $PROJECT >/dev/null 2>&1
  $CLI gates --admit hello-mesh --as node-a-operator --path $PROJECT >/dev/null 2>&1
  $CLI pkg export --id hello-mesh --out $PUBLISHED/hello-mesh.ashpkg --path $PROJECT >/dev/null 2>&1
  $CLI keys show 2>&1 | grep -oE 'ed25519:[0-9a-f]{16}' | head -1" -e PROPOSAL="$PROPOSAL" -e FORGE="$FORGE" | tr -d '\r')"
if [ -n "$AFP" ]; then pass "node-a sealed hello-mesh.ashpkg" "signer $AFP"; else fail "node-a sealed hello-mesh.ashpkg" "no signer fingerprint"; fi

BFP="$(seed node-b "
  $CLI keys init >/dev/null 2>&1
  $CLI init node-b --path $PROJECT >/dev/null 2>&1 && $OPEN_POLICY
  $CLI keys trust $AFP >/dev/null 2>&1
  $CLI keys show 2>&1 | grep -oE 'ed25519:[0-9a-f]{16}' | head -1" | tr -d '\r')"
if [ -n "$BFP" ]; then pass "node-b trusts node-a's key" "node-b's own key $BFP"; else fail "node-b trusts node-a's key" "no key"; fi

CPEERS="$(seed node-c "
  $CLI keys init >/dev/null 2>&1
  $CLI init node-c --path $PROJECT >/dev/null 2>&1 && $OPEN_POLICY
  $CLI keys peers 2>&1" | tr -d '\r')"
if [ -n "$CPEERS" ] && ! printf '%s' "$CPEERS" | grep -q "$AFP"; then
  pass "node-c does not trust node-a's key" "same policy as node-b, no trusted signers"
else
  fail "node-c does not trust node-a's key" "$(printf '%s' "$CPEERS" | head -1)"
fi

"${COMPOSE[@]}" up -d node-a node-b node-c >/dev/null 2>&1 || fail "the three nodes start" "docker compose up failed"

scene "1. node-a serves its package"
INDEX=""
for ((i = 0; i < WAIT_SECONDS; i += 3)); do
  INDEX="$(on node-b bash -c 'exec 3<>/dev/tcp/node-a/7420 && printf "GET /mesh/v1/index HTTP/1.0\r\nHost: node-a\r\n\r\n" >&3 && cat <&3' 2>/dev/null)"
  printf '%s' "$INDEX" | grep -q 'hello-mesh.ashpkg' && break
  sleep 3
done
if printf '%s' "$INDEX" | grep -q 'hello-mesh.ashpkg'; then pass "GET /mesh/v1/index lists the package" "seen from node-b"; else fail "GET /mesh/v1/index lists the package" "node-a never served it"; fi

scene "2. node-b pulls, verifies and holds it for a person"
if LINE="$(wait_log node-b 'auto-pull: scanned .* 1 held')"; then
  pass "node-b holds the package for review" "${LINE##*auto-pull: }"
else
  fail "node-b holds the package for review" "$("${COMPOSE[@]}" logs --no-color node-b 2>&1 | grep -i 'auto-pull' | tail -1)"
fi
ADMIT="$(on node-b "${CLI_EXEC[@]}" gates --admit hello-mesh --as node-b-operator --path $PROJECT 2>&1)"
if printf '%s' "$ADMIT" | grep -q 'seated'; then pass "a person approves it on node-b" "gates --admit hello-mesh"; else fail "a person approves it on node-b" "$(printf '%s' "$ADMIT" | tail -1)"; fi
if on node-b test -f "$PROJECT/$BRICK_FILE"; then pass "the brick lands in node-b's project" "$BRICK_FILE"; else fail "the brick lands in node-b's project" "file missing"; fi
RECORD="$(on node-b "${CLI_EXEC[@]}" gates --show hello-mesh --path $PROJECT 2>&1)"
if [ -n "$BFP" ] && printf '%s' "$RECORD" | grep -q "$BFP"; then
  pass "node-b's decision is signed with node-b's key" "$BFP"
else
  fail "node-b's decision is signed with node-b's key" "$(printf '%s' "$RECORD" | grep -i -m1 'sign')"
fi

scene "3. node-c refuses the same package"
if LINE="$(wait_log node-c 'auto-pull: scanned .* 1 refused')"; then
  pass "node-c refuses a signer it doesn't trust" "${LINE##*auto-pull: }"
else
  fail "node-c refuses a signer it doesn't trust" "$("${COMPOSE[@]}" logs --no-color node-c 2>&1 | grep -i 'auto-pull' | tail -1)"
fi
if on node-c test -f "$PROJECT/ashlar.policy.yaml" && ! on node-c test -e "$PROJECT/$BRICK_FILE"; then
  pass "nothing lands in node-c's project" ""
else
  fail "nothing lands in node-c's project" "the brick file is there, or node-c can't be checked"
fi

scene "4. A tampered copy is refused"
on node-a bash -c "sed 's/HelloMesh brick/HelloMesh brick, edited/' $PUBLISHED/hello-mesh.ashpkg > $PUBLISHED/hello-mesh-tampered.ashpkg"
SHOW="$(on node-a "${CLI_EXEC[@]}" pkg show "$PUBLISHED/hello-mesh-tampered.ashpkg" 2>&1)"; SHOW_RC=$?
echo "$SHOW" | sed -n '1,6p' | sed 's/^/        | /'
if [ "$SHOW_RC" -ne 0 ] && ! printf '%s' "$SHOW" | grep -q 'package verifies' && printf '%s' "$SHOW" | grep -qiE 'refus|not verify|does not|invalid|mismatch|tamper|signature'; then
  pass "the tampered copy fails verification" "pkg show exits $SHOW_RC"
else
  fail "the tampered copy fails verification" "pkg show exits $SHOW_RC"
fi
if LINE="$(wait_log node-b 'auto-pull: scanned 2 .* 1 refused')"; then
  pass "node-b refuses the tampered copy" "${LINE##*auto-pull: }"
else
  fail "node-b refuses the tampered copy" "$("${COMPOSE[@]}" logs --no-color node-b 2>&1 | grep -i 'auto-pull' | tail -1)"
fi

printf '\nprivate-mesh demo: %d passed, %d failed\n' "$P" "$F"
[ "$F" -eq 0 ]
