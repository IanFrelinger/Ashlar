#!/usr/bin/env bash
# End-to-end scenario suite for the product loop: init → verify → gates.
#
# Every scenario is a behavioural CLAIM about the shipped CLI, exercised against the real
# binaries — no mocks, each invocation a fresh process (which is itself the persistence
# test: state that survives between lines here survived process death). RESULT lines follow
# the UAT convention; the script exits non-zero if any scenario fails.
#
# Usage: bash scripts/e2e-loop.sh   (repo root; builds the CLI once, then --no-build)
set -uo pipefail

CLI_PROJ="${ASHLAR_CLI_PROJ:-application/src/Ashlar.CLI}"
WORK="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/ashlar-e2e-$$"
RESULTS="$WORK/results.tsv"
mkdir -p "$WORK"
: > "$RESULTS"

PASS=0; FAIL=0; N=0

result() { # name PASS|FAIL detail
  N=$((N+1))
  printf 'RESULT\t%d\t%s\t%s\t%s\n' "$N" "$1" "$2" "$3" | tee -a "$RESULTS"
  if [ "$2" = PASS ]; then PASS=$((PASS+1)); else FAIL=$((FAIL+1)); fi
}

# run <name> <expected-rc> <grep-must ...> -- <cli args...>
# Captures stdout+stderr; asserts exit code and that every pattern appears.
OUT=""; RC=0
run_cli() {
  OUT=$(NO_COLOR=1 dotnet run --project "$CLI_PROJ" --no-build -- "$@" 2>&1) && RC=0 || RC=$?
}
claim() { # name expected_rc pattern...
  local name="$1" want="$2"; shift 2
  if [ "$RC" -ne "$want" ]; then
    result "$name" FAIL "exit $RC, expected $want :: $(echo "$OUT" | head -2 | tr '\n' ' ')"
    return
  fi
  local p
  for p in "$@"; do
    if ! echo "$OUT" | grep -qF -- "$p"; then
      result "$name" FAIL "missing '$p' :: $(echo "$OUT" | head -2 | tr '\n' ' ')"
      return
    fi
  done
  result "$name" PASS "exit $want"
}

fresh() { # fresh project dir; prints path
  local d="$WORK/p$RANDOM$RANDOM"
  mkdir -p "$d"; echo "$d"
}

# policy editors (operate on $1/ashlar.policy.yaml)
set_proposing() {
  sed -e "s/mode: sealed/mode: proposing/" \
      -e "s/gatesRequired: \[\]/gatesRequired: [sandbox, tests, security]/" \
      -e "s/extensions: 0/extensions: ${2:-3}/" \
      -e "s/mayAdd: \[\]/mayAdd: [brick]/" "$1/ashlar.policy.yaml" > "$1/.p" && mv "$1/.p" "$1/ashlar.policy.yaml"
}
set_selfextending() {
  set_proposing "$1" "${2:-3}"
  sed -e "s/mode: proposing/mode: self-extending/" "$1/ashlar.policy.yaml" > "$1/.p" && mv "$1/.p" "$1/ashlar.policy.yaml"
}

proposal_json() { # id kind [failedCourse|missing]
  local id="$1" kind="$2" variant="${3:-ok}"
  local tests='{"name":"tests","passed":true,"detail":"14 passed"}'
  [ "$variant" = failtests ] && tests='{"name":"tests","passed":false,"detail":"2 failed"}'
  local courses="{\"name\":\"sandbox\",\"passed\":true,\"detail\":\"confined\"},$tests,{\"name\":\"security\",\"passed\":true,\"detail\":\"0 findings\"}"
  [ "$variant" = missing ] && courses="{\"name\":\"sandbox\",\"passed\":true,\"detail\":\"confined\"},$tests"
  cat <<EOF
{"id":"$id","kind":"$kind","summary":"add brick demo.v2","proposedBy":"classifier",
 "proposedAt":"2026-08-23T02:14:00Z","diff":"+ 34 lines",
 "courses":[$courses]}
EOF
}

echo "== building the CLI once =="
dotnet build "$CLI_PROJ" -v quiet >/dev/null || { echo "build failed"; exit 1; }

# ───────────────────────────── init ─────────────────────────────
D=$(fresh)
run_cli init invoice-triage --path "$D"
claim "init-creates-project" 0 "ashlar.yaml" "the only file"
[ -f "$D/ashlar.yaml" ] && [ -f "$D/ashlar.policy.yaml" ] \
  && result "init-both-files-on-disk" PASS "both present" \
  || result "init-both-files-on-disk" FAIL "missing files"

H1=$(cat "$D/ashlar.yaml" "$D/ashlar.policy.yaml" | cksum)
run_cli init invoice-triage --path "$D"
claim "init-rerun-refuses" 1 "refusing to overwrite"
H2=$(cat "$D/ashlar.yaml" "$D/ashlar.policy.yaml" | cksum)
[ "$H1" = "$H2" ] \
  && result "init-refusal-left-files-untouched" PASS "checksums equal" \
  || result "init-refusal-left-files-untouched" FAIL "files changed on refusal"

for bad in "9lives" "-lead" "has space" "a.b"; do
  D=$(fresh); run_cli init "$bad" --path "$D"
  claim "init-rejects-name[$bad]" 1 "REJECTED"
  [ ! -f "$D/ashlar.yaml" ] \
    && result "init-rejection-writes-nothing[$bad]" PASS "no files" \
    || result "init-rejection-writes-nothing[$bad]" FAIL "wrote files for a rejected name"
done

D=$(fresh); run_cli init a --path "$D"
claim "init-accepts-single-letter" 0 "project contract for 'a'"

D="$WORK/dir with spaces/nested"
run_cli init spaced --path "$D"
claim "init-path-with-spaces" 0 "ashlar.yaml"

# ───────────────────────────── verify ─────────────────────────────
D=$(fresh); run_cli init demo --path "$D"
run_cli verify --path "$D"
claim "verify-fresh-project" 0 "VERIFIED" "unsigned" "course 1" "course 2" "course 3"

echo "$OUT" | grep -q "ed25519" \
  && result "verify-never-fakes-a-signature" FAIL "printed ed25519 with no keys" \
  || result "verify-never-fakes-a-signature" PASS "no signature claimed"

printf '%s' "$OUT" | grep -q $'\x1b' \
  && result "verify-redirected-output-is-plain" FAIL "raw ANSI in redirected output" \
  || result "verify-redirected-output-is-plain" PASS "no ESC bytes"

run_cli verify --path "$WORK"
claim "verify-not-a-project" 1 "not an ashlar project" "ashlar init"

D=$(fresh); run_cli init demo --path "$D"
echo "kind: Nonsense" > "$D/ashlar.yaml"
run_cli verify --path "$D"
claim "verify-broken-manifest-fails-contract" 65 "course 'contract'" "REJECTED"

D=$(fresh); run_cli init demo --path "$D"
printf '\nselfExtend:\n  mode: self-extending\n' >> "$D/ashlar.yaml"
run_cli verify --path "$D"
claim "verify-manifest-grabbing-the-envelope" 65 "policy-owned"

D=$(fresh); run_cli init demo --path "$D"
sed "s/gates: \[tests\]/gates: []/" "$D/ashlar.yaml" > "$D/.m" && mv "$D/.m" "$D/ashlar.yaml"
run_cli verify --path "$D"
claim "verify-ungated-agent" 65 "no gates"

D=$(fresh); run_cli init demo --path "$D"
sed "s|root: .|root: does-not-exist|" "$D/ashlar.policy.yaml" > "$D/.p" && mv "$D/.p" "$D/ashlar.policy.yaml"
run_cli verify --path "$D"
claim "verify-missing-sandbox-root" 65 "does not exist"

D=$(fresh); run_cli init demo --path "$D"
sed "s|writable: \[\]|writable: [../outside]|" "$D/ashlar.policy.yaml" > "$D/.p" && mv "$D/.p" "$D/ashlar.policy.yaml"
run_cli verify --path "$D"
claim "verify-writable-escape" 65 "escapes"

D=$(fresh); run_cli init demo --path "$D"
sed -e "s/mode: sealed/mode: proposing/" -e "s/gatesRequired: \[\]/gatesRequired: [tests]/" \
  "$D/ashlar.policy.yaml" > "$D/.p" && mv "$D/.p" "$D/ashlar.policy.yaml"
run_cli verify --path "$D"
claim "verify-unfunded-proposing" 65 "seal it or fund it"

D=$(fresh); run_cli init demo --path "$D"; set_proposing "$D"
run_cli verify --path "$D"
claim "verify-funded-proposing" 0 "VERIFIED" "mode: proposing"

# --format-json is a GLOBAL option, so it parses on `verify` even though the wall is an ANSI
# rendering with no JSON form. Accepting it and printing the wall anyway returned 0 (VERIFIED) over
# text no parser can read; the refusal is what stops a script concluding the project verified. Same
# project as the claim above, which verifies 0 — so the flag is the only thing that changed.
run_cli verify --path "$D" --format-json
claim "verify-refuses-unsupported-format-json" 1 "--format-json" "no JSON rendering"

# ───────────────────────────── gates ─────────────────────────────
D=$(fresh); run_cli init demo --path "$D"; set_proposing "$D"
run_cli gates --path "$D"
claim "gates-empty-queue" 0 "nothing held"

proposal_json ext-1 brick > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-proposing-holds" 0 "HELD" "a person seats the stone"

run_cli gates --path "$D"      # fresh process: durability is inherent to the assertion
claim "gates-lists-across-processes" 0 "ext-1" "classifier"

run_cli gates --show ext-1 --path "$D"
claim "gates-show-record" 0 "sandbox" "tests" "security" "HELD" "+ 34 lines"

run_cli gates --admit ext-1 --as tester --path "$D"
claim "gates-admit-seats" 0 "seated" "admitted by tester"

run_cli gates --show ext-1 --path "$D"
claim "gates-show-admitted" 0 "ADMITTED by tester"

run_cli gates --admit ext-1 --as anyone --path "$D"
claim "gates-no-second-decision" 1 "no administrative path"

proposal_json ext-1 brick > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-duplicate-id-refused" 1 "append-once"

proposal_json ext-r brick > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D" >/dev/null 2>&1 || true
run_cli gates --refuse ext-r --as tester --path "$D"
claim "refuse-requires-reason" 1 "requires a reason"

run_cli gates --refuse ext-r --as tester --reason "no regression case" --path "$D"
claim "refuse-with-reason-records" 0 "refused" "fed back"

run_cli gates --admit ghost-id --as tester --path "$D"
claim "gates-unknown-id" 1 "No proposal"

proposal_json "../escape" brick > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-illegal-id-fails-closed" 1 "Illegal proposal id"

echo "not json at all" > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-malformed-json" 1 "could not be parsed"

# sealed rejects everything
D=$(fresh); run_cli init demo --path "$D"
proposal_json ext-s brick > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-sealed-rejects" 65 "sealed"

# envelope beats mode and budget
D=$(fresh); run_cli init demo --path "$D"; set_selfextending "$D" 100
proposal_json ext-t tool > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-tool-outside-envelope" 65 "outside the envelope" "never the application's"

# failed and missing courses reject in proposing mode
D=$(fresh); run_cli init demo --path "$D"; set_proposing "$D"
proposal_json ext-f brick failtests > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-failed-course-rejects" 65 "'tests' failed"
proposal_json ext-m brick missing > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-missing-course-rejects" 65 "did not run"

# self-extending: admits within budget, then degrades to held
D=$(fresh); run_cli init demo --path "$D"; set_selfextending "$D" 1
proposal_json ext-a1 brick > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "selfextend-admits-within-budget" 0 "ADMITTED" "1 of 1"
proposal_json ext-a2 brick > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "selfextend-exhausted-degrades-to-held" 0 "HELD" "budget"

# ───────────────────────────── run ─────────────────────────────
D=$(fresh); run_cli init demo --path "$D"
sed -e "s/mode: sealed/mode: proposing/" -e "s/gatesRequired: \[\]/gatesRequired: [tests]/" "$D/ashlar.policy.yaml" > "$D/.p" && mv "$D/.p" "$D/ashlar.policy.yaml"
run_cli run "test request" --path "$D"
claim "run-refuses-unverified" 65 "you cannot run what does not verify"

sed -e "s/mode: proposing/mode: sealed/" -e "s/gatesRequired: \[tests\]/gatesRequired: []/" "$D/ashlar.policy.yaml" > "$D/.p" && mv "$D/.p" "$D/ashlar.policy.yaml"
# The mock provider's reply is an echo, not decomposition JSON, so a plain request becomes the Gameplay
# fallback, GenericAgent does no work, and the run fails with the NO_WORK escalation (#832). A mock run
# that completes has to give a specialized agent something to do: the echo returns the architect prompt,
# which carries the request, so a request holding its own decomposition routes to the Planning agent.
run_cli run "classify one sample invoice" --path "$D"
claim "run-mock-no-work-fails" 2 "provider mock" "Orchestration failed" "Escalations: 1"

PLAN_REQUEST='Plan how to classify one sample invoice. ```json {"agents":[{"agentId":"plan-1","domain":"Planning","goal":"Plan how to classify one sample invoice"}],"reasoning":"one planning step","confidence":0.9} ```'
run_cli run "$PLAN_REQUEST" --path "$D"
claim "run-mock-completes" 0 "provider mock" "Creating agent plan-1 for domain Planning" "Orchestration completed successfully" "Escalations: 0"

run_cli run "x" --path "$WORK"
claim "run-not-a-project" 1 "not an ashlar project"

# ───────────────────────────── keys / signing ─────────────────────────────
# Isolate the operator identity into the scratch tree — never touch the real ~/.ashlar/keys.
# Exported so both `keys` and the `gates` signing path (which loads the machine-global key)
# resolve the SAME directory, which is the whole point of presence-activation.
export ASHLAR_KEY_DIR="$WORK/keys"

run_cli keys show
claim "keys-show-none" 0 "no operator key"
echo "$OUT" | grep -q "ed25519" \
  && result "keys-show-none-hides-fingerprint" FAIL "printed a fingerprint with no key (S-3)" \
  || result "keys-show-none-hides-fingerprint" PASS "no fingerprint for an absent key"

run_cli keys init
claim "keys-init-generates" 0 "operator key ready" "ed25519:"
[ -f "$ASHLAR_KEY_DIR/operator.key" ] && [ -f "$ASHLAR_KEY_DIR/operator.pub" ] \
  && result "keys-init-writes-both-halves" PASS "seed + pub on disk" \
  || result "keys-init-writes-both-halves" FAIL "missing key files"

run_cli keys init
claim "keys-init-refuses-overwrite" 1 "already exists" "rotate"

run_cli keys show
claim "keys-show-fingerprint" 0 "ed25519:" "operator key"

run_cli keys init --rotate
claim "keys-init-rotate" 0 "operator key rotated"
ls "$ASHLAR_KEY_DIR"/trusted/*.pub >/dev/null 2>&1 \
  && result "keys-rotate-retains-old-pub" PASS "old pub kept in trusted/" \
  || result "keys-rotate-retains-old-pub" FAIL "old pub not retained"

# The integration: with a key present, the gate SIGNS the verdicts it records.
D=$(fresh); run_cli init demo --path "$D"; set_proposing "$D"
proposal_json ext-signed brick > "$D/p.json"
run_cli gates propose --file "$D/p.json" --path "$D"
claim "propose-with-key-holds" 0 "HELD"
grep -q '"Sig"' "$D/.ashlar/gates/ext-signed.json" \
  && result "held-record-is-signed" PASS "Sig present" \
  || result "held-record-is-signed" FAIL "no Sig in the held record"
grep -q '"Signer"' "$D/.ashlar/gates/ext-signed.json" \
  && result "held-record-names-signer" PASS "Signer embedded" \
  || result "held-record-names-signer" FAIL "no Signer in the held record"

# Admit re-signs over the NEW content; the show reads it back fail-closed, so a green read is
# the proof the verdict was NOT bricked by a stale signature (the bug the review caught).
run_cli gates --admit ext-signed --as tester --path "$D"
claim "admit-signed-record" 0 "seated"
run_cli gates --show ext-signed --path "$D"
claim "show-signed-admitted-reads-back" 0 "ADMITTED by tester"

# A record signed at rest still reads back after a fresh process — durability plus signature.
run_cli gates --show ext-signed --path "$D"
claim "signed-record-survives-process-death" 0 "ADMITTED"

# ── verify now CERTIFIES when a key is present, signing into the instance ledger ──
DV=$(fresh); run_cli init cert-demo --path "$DV"
run_cli verify --path "$DV"
claim "verify-certifies-with-key" 0 "CERTIFIED" "ed25519:" "ledger #1"
[ -f "$DV/.ashlar/ledger/000001.json" ] \
  && result "verify-writes-a-ledger-entry" PASS "entry on disk" \
  || result "verify-writes-a-ledger-entry" FAIL "no ledger entry written"
grep -q '"Sig"' "$DV/.ashlar/ledger/000001.json" \
  && result "ledger-entry-is-signed" PASS "Sig present" \
  || result "ledger-entry-is-signed" FAIL "ledger entry not signed"

# second verify: the provenance course appears and the chain extends to #2
run_cli verify --path "$DV"
claim "verify-second-run-adds-provenance" 0 "CERTIFIED" "provenance" "ledger #2"

# a tampered ledger fails verification via the provenance course, fail-closed
printf '{ not a valid entry' > "$DV/.ashlar/ledger/000001.json"
run_cli verify --path "$DV"
claim "verify-refuses-a-corrupt-ledger" 65 "provenance" "Corrupt ledger"

# A CORRUPT operator key must fail loud and CLEAN on write paths, and never block reads.
DSIGNED="$D"
printf 'not-valid-base64!!!' > "$ASHLAR_KEY_DIR/operator.key"

run_cli keys show
claim "keys-show-corrupt-fails-clean" 1 "Corrupt operator key"
echo "$OUT" | grep -qE "Unhandled exception|System.FormatException|^   at " \
  && result "keys-corrupt-no-raw-stack-trace" FAIL "leaked a stack trace instead of a clean message" \
  || result "keys-corrupt-no-raw-stack-trace" PASS "clean message, no stack trace"

DC=$(fresh); run_cli init demo --path "$DC"; set_proposing "$DC"
proposal_json ext-corrupt brick > "$DC/p.json"
run_cli gates propose --file "$DC/p.json" --path "$DC"
claim "propose-corrupt-key-fails-clean" 1 "Corrupt operator key"

# Reads verify via the record's OWN embedded key, so a corrupt operator key must NOT block them.
run_cli gates --show ext-signed --path "$DSIGNED"
claim "read-survives-a-corrupt-operator-key" 0 "ADMITTED"

unset ASHLAR_KEY_DIR

# ───────────────────────────── pkg: certified extensions travel ─────────────────────────────
# Two nodes sharing one extension: the origin admits it, seals it into a package; the receiver
# verifies it intrinsically and runs it through ITS OWN gate. Fresh key dir — the corrupt-key
# block above deliberately mangled the previous one.
export ASHLAR_KEY_DIR="$WORK/pkgkeys"
run_cli keys init >/dev/null

A=$(fresh); run_cli init origin-node --path "$A"; set_proposing "$A"
mkdir -p "$A/.ashlar/forge/proposed"
cat > "$A/.ashlar/forge/proposed/fpkg1.json" <<'EOF'
{"Id":"fpkg1","TargetPath":"src/Shared.cs","NewContent":"// shared brick v1","Summary":"add brick shared.classify","CreatedAt":"2026-08-24T06:00:00Z","UpdatedAt":"2026-08-24T06:00:00Z"}
EOF
cat > "$A/p.json" <<'EOF'
{"id":"ext-pkg","kind":"brick","summary":"add brick shared.classify","proposedBy":"night-agent",
 "proposedAt":"2026-08-24T06:00:00Z","diff":"+ 1 file",
 "forgeProposalIds":["fpkg1"],
 "courses":[{"name":"sandbox","passed":true,"detail":"confined"},{"name":"tests","passed":true,"detail":"14 passed"},{"name":"security","passed":true,"detail":"0 findings"}]}
EOF
run_cli gates propose --file "$A/p.json" --path "$A" >/dev/null
run_cli gates --admit ext-pkg --as origin-op --path "$A"
claim "pkg-origin-admit-applies" 0 "seated" "applied"
[ -f "$A/src/Shared.cs" ] \
  && result "pkg-origin-file-on-disk" PASS "the admitted write landed" \
  || result "pkg-origin-file-on-disk" FAIL "admitted write missing"

run_cli pkg export --id ext-pkg --out "$WORK/shared.ashpkg" --path "$A"
claim "pkg-export-seals" 0 "packaged" "ed25519:"

run_cli pkg show "$WORK/shared.ashpkg"
claim "pkg-show-verifies-keyless" 0 "package verifies" "shared.classify" "ed25519:"

# receiver in proposing mode: held, nothing on disk until THIS operator seats it
B=$(fresh); run_cli init receiver-node --path "$B"; set_proposing "$B"
run_cli pkg import "$WORK/shared.ashpkg" --path "$B"
claim "pkg-import-held-under-proposing" 0 "HELD" "origin verdict"
[ ! -f "$B/src/Shared.cs" ] \
  && result "pkg-import-held-not-on-disk" PASS "hold before write, for remote code too" \
  || result "pkg-import-held-not-on-disk" FAIL "imported file landed before admission"

run_cli gates --admit ext-pkg --as receiver-op --path "$B"
claim "pkg-receiver-admit-applies" 0 "seated" "applied"
grep -q "shared brick v1" "$B/src/Shared.cs" 2>/dev/null \
  && result "pkg-received-content-matches" PASS "the sealed bytes landed" \
  || result "pkg-received-content-matches" FAIL "content mismatch or missing"

# a sealed receiver rejects it and nothing ever lands
C=$(fresh); run_cli init sealed-node --path "$C"
run_cli pkg import "$WORK/shared.ashpkg" --path "$C"
claim "pkg-import-sealed-rejects" 65 "REJECTED" "sealed"
[ ! -f "$C/src/Shared.cs" ] \
  && result "pkg-sealed-nothing-lands" PASS "sender certification is not authority" \
  || result "pkg-sealed-nothing-lands" FAIL "sealed project accepted remote code"

# a tampered package is refused as forged, before any gate is consulted
sed 's/shared brick v1/backdoored/' "$WORK/shared.ashpkg" > "$WORK/tampered.ashpkg"
run_cli pkg import "$WORK/tampered.ashpkg" --path "$C"
claim "pkg-import-tampered-refused" 65 "seal does not verify"

# importing the same package twice is idempotent — already-decided is skipped, not re-parked
run_cli pkg import "$WORK/shared.ashpkg" --path "$B"
claim "pkg-import-duplicate-skipped" 0 "already imported"

# exporting without an operator key is refused — a seal is a signature
ASHLAR_KEY_DIR="$WORK/nokeys" run_cli pkg export --id ext-pkg --out "$WORK/x.ashpkg" --path "$A"
claim "pkg-export-keyless-refused" 1 "requires an operator key"

# ── trust root: an untrusted signer's package is REFUSED before it parks (the refusal, on one box) ──
# Every import above shared ONE key, so each was SELF-trusted (a node trusts the key it signs with).
# Here a STRANGER — a separate key dir — seals a package; receiver B refuses it, naming the
# fingerprint, until told to trust that key, then holds it. Deleting the trust comparison in
# PackageImport turns pkg-import-refuses-untrusted-signer red (it becomes HELD, exit 0).
# The refusal is a verification refusal (exit 65), consistent with pkg pull and pkg show.
SK="$WORK/strangerkeys"
XFP=$(ASHLAR_KEY_DIR="$SK" NO_COLOR=1 dotnet run --project "$CLI_PROJ" --no-build -- keys init 2>&1 | grep -oE 'ed25519:[0-9a-f]{16}' | head -1)
XA=$(fresh)
ASHLAR_KEY_DIR="$SK" NO_COLOR=1 dotnet run --project "$CLI_PROJ" --no-build -- init stranger-node --path "$XA" >/dev/null
set_proposing "$XA"
mkdir -p "$XA/.ashlar/forge/proposed"
cat > "$XA/.ashlar/forge/proposed/fstr.json" <<'JSON'
{"Id":"fstr","TargetPath":"src/Stranger.cs","NewContent":"// stranger brick","Summary":"stranger brick","CreatedAt":"2026-08-24T06:00:00Z","UpdatedAt":"2026-08-24T06:00:00Z"}
JSON
cat > "$XA/px.json" <<'JSON'
{"id":"ext-stranger","kind":"brick","summary":"stranger brick","proposedBy":"stranger",
 "proposedAt":"2026-08-24T06:00:00Z","diff":"+ 1 file","forgeProposalIds":["fstr"],
 "courses":[{"name":"sandbox","passed":true,"detail":"confined"},{"name":"tests","passed":true,"detail":"ok"},{"name":"security","passed":true,"detail":"0 findings"}]}
JSON
ASHLAR_KEY_DIR="$SK" NO_COLOR=1 dotnet run --project "$CLI_PROJ" --no-build -- gates propose --file "$XA/px.json" --path "$XA" >/dev/null
ASHLAR_KEY_DIR="$SK" NO_COLOR=1 dotnet run --project "$CLI_PROJ" --no-build -- gates --admit ext-stranger --as stranger-op --path "$XA" >/dev/null
ASHLAR_KEY_DIR="$SK" NO_COLOR=1 dotnet run --project "$CLI_PROJ" --no-build -- pkg export --id ext-stranger --out "$WORK/stranger.ashpkg" --path "$XA" >/dev/null

# B (pkgkeys) does not trust X: refused before parking, naming X's fingerprint
run_cli pkg import "$WORK/stranger.ashpkg" --path "$B"
claim "pkg-import-refuses-untrusted-signer" 65 "not a trusted signer" "$XFP"
[ ! -f "$B/src/Stranger.cs" ] \
  && result "pkg-untrusted-nothing-parks" PASS "an untrusted package parks nothing" \
  || result "pkg-untrusted-nothing-parks" FAIL "untrusted package wrote to disk"

# trust X off the origin fingerprint, and the same package now HELD at B's gate
run_cli keys trust "$XFP" >/dev/null
run_cli pkg import "$WORK/stranger.ashpkg" --path "$B"
claim "pkg-import-held-after-trust" 0 "HELD"
[ ! -f "$B/src/Stranger.cs" ] \
  && result "pkg-trusted-holds-before-write" PASS "trust admits to the gate, not to disk" \
  || result "pkg-trusted-holds-before-write" FAIL "trusted import wrote before admission"
run_cli keys untrust "$XFP" >/dev/null

unset ASHLAR_KEY_DIR

# ───────────────────────────── pkg mesh: publish + pull between peers ─────────────────────────────
# The origin publishes its certified package to a mesh store; a peer pulls the whole store and
# each package faces the peer's OWN gate. Reuses the shared package from the pkg block above.
export ASHLAR_KEY_DIR="$WORK/pkgkeys"
MESH="$WORK/mesh-store"

run_cli pkg publish "$WORK/shared.ashpkg" --store "$MESH"
claim "pkg-publish-to-mesh" 0 "published to the mesh" "ed25519:"
ls "$MESH"/*.ashpkg >/dev/null 2>&1 \
  && result "pkg-publish-lands-in-store" PASS "package in the mesh store" \
  || result "pkg-publish-lands-in-store" FAIL "nothing in the store"

# publishing a tampered package to the mesh is refused at the source
run_cli pkg publish "$WORK/tampered.ashpkg" --store "$MESH"
claim "pkg-publish-refuses-forged" 65 "seal does not verify"

# a proposing peer pulls the store: the package is HELD (nothing on disk until it seats it)
PP=$(fresh); run_cli init pull-node --path "$PP"; set_proposing "$PP"
run_cli pkg pull --from "$MESH" --path "$PP"
claim "pkg-pull-holds-under-proposing" 0 "HELD" "faces YOUR gate" "pulled 1"
[ ! -f "$PP/src/Shared.cs" ] \
  && result "pkg-pull-held-not-on-disk" PASS "pulled code holds before the gate" \
  || result "pkg-pull-held-not-on-disk" FAIL "pulled file landed before admission"

# re-pulling the same store is idempotent: already-decided packages are skipped, not re-parked
run_cli pkg pull --from "$MESH" --path "$PP"
claim "pkg-pull-idempotent" 0 "already have" "already-have 1"
FORGE_COUNT=$(ls "$PP/.ashlar/forge/proposed"/*.json 2>/dev/null | wc -l)
[ "$FORGE_COUNT" -le 1 ] \
  && result "pkg-repull-does-not-leak-forge" PASS "no orphaned parked proposals" \
  || result "pkg-repull-does-not-leak-forge" FAIL "forge queue grew on re-pull ($FORGE_COUNT)"

# Someone with write access plants a forged package straight into the shared folder — the mesh
# store is a plain directory, so no publish is needed. The legitimate package is already-have by
# now, which is the steady state of any fleet pulling on a timer. The refusal must still reach the
# EXIT CODE: an operator watching `pkg pull … && echo ok` learns nothing from a body they never read.
cp "$WORK/tampered.ashpkg" "$MESH/planted.ashpkg"
run_cli pkg pull --from "$MESH" --path "$PP"
claim "pkg-pull-forgery-not-masked-by-already-have" 65 "REFUSED" "already-have 1" "refused/rejected 1"
if grep -rq "backdoored" "$PP" 2>/dev/null; then
  result "pkg-pull-planted-forgery-never-lands" FAIL "forged content reached the project"
else
  result "pkg-pull-planted-forgery-never-lands" PASS "refused before any write"
fi
rm -f "$MESH/planted.ashpkg"

# A .ashpkg is refused on SIZE before it is read, on every path that reads one — not only import
# and show. The mesh store is a plain synced directory, so an oversized file needs no publish to
# get there, and an unbounded read of one is an OutOfMemoryException, not a refusal.
# Sparse, and built with dd rather than `truncate`: truncate is a GNU/FreeBSD tool macOS does not
# ship (Homebrew installs it as gtruncate, not on PATH), and .github/workflows/portability-gate.yml
# runs this script on macos-latest as well as ubuntu and windows. `dd bs=1 count=0 seek=N` is the
# portable way to make an N-byte sparse file: identical on GNU coreutils, BSD/macOS dd, and the dd
# in Git Bash on windows-latest. The fixture gets its own scenario because a silently-missing
# $GIANT is how the two scenarios below would have failed on macOS for a reason nobody could read.
GIANT="$WORK/giant.ashpkg"
dd if=/dev/zero of="$GIANT" bs=1 count=0 seek=$((16*1024*1024+1024)) 2>/dev/null
{ [ -f "$GIANT" ] && [ "$(wc -c < "$GIANT")" -gt $((16*1024*1024)) ]; } \
  && result "pkg-oversized-fixture-built" PASS "sparse fixture is over the ceiling" \
  || result "pkg-oversized-fixture-built" FAIL "could not build the oversized fixture"
MESH_BEFORE=$(ls "$MESH"/*.ashpkg 2>/dev/null | wc -l)
run_cli pkg publish "$GIANT" --store "$MESH"
claim "pkg-publish-refuses-oversized" 65 "refusing before reading it"
MESH_AFTER=$(ls "$MESH"/*.ashpkg 2>/dev/null | wc -l)
[ "$MESH_BEFORE" = "$MESH_AFTER" ] \
  && result "pkg-publish-oversized-not-in-store" PASS "refused at the source" \
  || result "pkg-publish-oversized-not-in-store" FAIL "an oversized package reached the mesh store"

# Planted FIRST in Ordinal order: a pull that ends on it never reaches the legitimate package
# behind it, which is the denial of service the guard exists to prevent.
cp "$GIANT" "$MESH/aaa-planted-giant.ashpkg"
run_cli pkg pull --from "$MESH" --path "$PP"
claim "pkg-pull-oversized-refuses-row-and-continues" 65 "refusing before reading it" "already-have 1" "refused/rejected 1"
rm -f "$MESH/aaa-planted-giant.ashpkg"

# A ceiling read off metadata is not a ceiling, which is why the guard is a bounded READ and not a
# size check. FileInfo.Length reports 0 for a FIFO and for a symlink to a character device, and for
# a symlink to a real file it reports the length of the target PATH — so `ln -s /dev/zero x.ashpkg`
# was an OutOfMemoryException that killed the whole pass, `mkfifo x.ashpkg` was a permanent hang
# that wedged every pull on the fleet, and `ln -s <oversized> x.ashpkg` ignored the limit outright.
# Each is one command for anyone who can write to the share — cheaper than the oversized file above.
#
# LINUX ONLY, and deliberately a `uname` test rather than a capability probe. The probe that used to
# stand here — `mkfifo … && ln -s …`, succeed and proceed — looked like the more careful choice and
# was asking the wrong question. It SUCCEEDS in Git Bash on windows-latest: MSYS emulates both calls
# and reports `prw-rw-rw-` and `lrwxrwxrwx` back. What lands on NTFS is not what it reports:
# `mkfifo x.ashpkg` writes a 130-byte Windows shortcut named `x.ashpkg.lnk`, which the CLI's
# `*.ashpkg` enumeration does not even return, so the row never appears; `ln -s /dev/zero x.ashpkg`
# writes a 32-byte ORDINARY file whose bytes spell `!<symlink>` and whose .NET LinkTarget is null,
# so the symlink gate has nothing to fire on; and `ln -s "$GIANT" x` silently falls back to COPYING.
# A probe tests whether the syscall is available. Every claim below is about the OBJECT the syscall
# leaves on disk, and those are three different objects here — which is why the probe passed and
# five of these scenarios failed.
#
# macOS is excluded for a worse reason than red. There `mkfifo` plants a REAL fifo, `run_cli` has no
# timeout, and GNU `timeout` is not on a mac runner to add one. SafePackageRead's non-blocking open
# is a DllImport("libc") with a documented managed fallback that BLOCKS FOREVER on a fifo, and
# nobody has verified that P/Invoke binds on Darwin. If it does not, macos-latest does not fail — it
# hangs to the six-hour job limit with nothing in the log to read.
#
# Linux is the platform where this behaviour is verified, and where the dev container and the
# primary CI leg run. Nothing takes over on the other two: SafePackageReadTests' non-regular-file
# cases are [UnixOnlyFact] and report as SKIPPED on Windows, so the Windows branch of the guard —
# the \\?\ prefix, the device-namespace refusal, the FILE_TYPE_DISK check — has no automated
# coverage anywhere, and Darwin has none either. Skipping here is a gap, not a delegation.
if [ "$(uname -s)" = "Linux" ]; then
  mkfifo "$MESH/aaa-planted-fifo.ashpkg"
  ln -s /dev/zero "$MESH/aab-planted-zero.ashpkg"
  ln -s "$GIANT" "$MESH/aac-planted-linked-giant.ashpkg"
  # Planted FIRST in Ordinal order, as above. The FIFO used to block here forever, so reaching the
  # summary line at all is half the claim; the other half is the legitimate package behind them
  # still being decided.
  run_cli pkg pull --from "$MESH" --path "$PP"
  claim "pkg-pull-refuses-nonregular-rows-and-continues" 65 "fifo, socket or pipe" "symbolic link" "not a regular file" "already-have 1" "refused/rejected 3"
  rm -f "$MESH/aaa-planted-fifo.ashpkg" "$MESH/aab-planted-zero.ashpkg" "$MESH/aac-planted-linked-giant.ashpkg"

  # publish, import and show take a CALLER-SUPPLIED path, so they are reachable directly — and all
  # three read the same way, including import, which the refactor had treated as the safe reference.
  ln -s /dev/zero "$WORK/zero.ashpkg"
  MESH_BEFORE=$(ls "$MESH"/*.ashpkg 2>/dev/null | wc -l)
  run_cli pkg publish "$WORK/zero.ashpkg" --store "$MESH"
  claim "pkg-publish-refuses-symlink-to-device" 65 "not a regular file"
  MESH_AFTER=$(ls "$MESH"/*.ashpkg 2>/dev/null | wc -l)
  [ "$MESH_BEFORE" = "$MESH_AFTER" ] \
    && result "pkg-publish-nonregular-not-in-store" PASS "refused at the source" \
    || result "pkg-publish-nonregular-not-in-store" FAIL "a link to a device reached the mesh store"
  run_cli pkg import "$WORK/zero.ashpkg" --path "$PP"
  claim "pkg-import-refuses-symlink-to-device" 65 "not a regular file"
  run_cli pkg show "$WORK/zero.ashpkg"
  claim "pkg-show-refuses-symlink-to-device" 65 "not a regular file"
  rm -f "$WORK/zero.ashpkg"

  # A character device stats like an empty regular file and then never stops yielding bytes. Only
  # the cap ON THE READ stops it, and it has to stop it in milliseconds, not by exhausting memory.
  run_cli pkg import /dev/zero --path "$PP"
  claim "pkg-import-caps-endless-device-read" 65 "past the limit"
else
  echo "  (skipping the FIFO/symlink scenarios: Linux only — on MSYS/Windows mkfifo and ln -s"
  echo "   succeed but leave a .lnk shortcut and an ordinary file, so the fixtures do not mean"
  echo "   what the claims say; on macOS the non-blocking open is unverified and a real FIFO"
  echo "   would hang the run rather than fail it. Nothing covers it in their place: the matching"
  echo "   SafePackageReadTests cases are Unix-only and skip on this host too.)"
fi

# a sealed peer pulls the same store: every package rejected, nothing lands, non-zero exit
PS=$(fresh); run_cli init sealed-pull --path "$PS"
run_cli pkg pull --from "$MESH" --path "$PS"
claim "pkg-pull-sealed-rejects-all" 65 "REJECTED" "refused/rejected 1"
[ ! -f "$PS/src/Shared.cs" ] \
  && result "pkg-pull-sealed-nothing-lands" PASS "receiver sovereignty holds over the mesh" \
  || result "pkg-pull-sealed-nothing-lands" FAIL "sealed peer accepted mesh code"

# pulling from an empty store is a clean no-op
run_cli pkg pull --from "$WORK/nostore-$RANDOM" --path "$PP"
claim "pkg-pull-missing-store" 1 "no such peer store"

# A file:// URL names the same folder a plain path does — it is the shape a sync client or a file
# manager hands an operator who copies the store's location. It must reach the store rather than be
# coerced into a nonsense DirectoryInfo and reported as missing. $MESH is absolute, so these two
# slashes plus its own leading one make the empty-authority file:/// form. $PP already decided this
# package above, so already-have is the honest answer for a store it genuinely reached.
run_cli pkg pull --from "file://$MESH" --path "$PP"
claim "pkg-pull-file-url-store" 0 "already have" "already-have 1"

# ftp:/ssh: have no transport behind them here. Naming the scheme back beats "no such peer store",
# which reads as the operator's own store having vanished.
run_cli pkg pull --from "ftp://peer.example/store" --path "$PP"
claim "pkg-pull-unsupported-scheme" 1 "takes a directory" "no ftp transport"

unset ASHLAR_KEY_DIR

# ───────────────────────────── pkg mesh 2: two-node co-production ─────────────────────────────
# The loop the mesh exists for: node A admits and SHARES v1 in one verb; node B pulls it through
# ITS OWN gate, builds v2 on top, admits, shares back; node A pulls v2 through ITS gate. Both
# gates exercised in both directions, and the store dedupes what each side already decided.
export ASHLAR_KEY_DIR="$WORK/pkgkeys"
MESH2="$WORK/mesh-coprod"

CA=$(fresh); run_cli init coprod-a --path "$CA"; set_proposing "$CA"
CB=$(fresh); run_cli init coprod-b --path "$CB"; set_proposing "$CB"

# node A: admit v1 (a parked forge write + its gate proposal), then share = seal + publish
mkdir -p "$CA/.ashlar/forge/proposed"
cat > "$CA/.ashlar/forge/proposed/fco1.json" <<'EOF'
{"Id":"fco1","TargetPath":"src/Coprod.cs","NewContent":"// coprod v1","Summary":"add brick coprod.classify","CreatedAt":"2026-08-25T06:00:00Z","UpdatedAt":"2026-08-25T06:00:00Z"}
EOF
cat > "$CA/pco1.json" <<'EOF'
{"id":"ext-co1","kind":"brick","summary":"add brick coprod.classify","proposedBy":"night-agent",
 "proposedAt":"2026-08-25T06:00:00Z","diff":"+ 1 file",
 "forgeProposalIds":["fco1"],
 "courses":[{"name":"sandbox","passed":true,"detail":"confined"},{"name":"tests","passed":true,"detail":"14 passed"},{"name":"security","passed":true,"detail":"0 findings"}]}
EOF
run_cli gates propose --file "$CA/pco1.json" --path "$CA" >/dev/null
run_cli gates --admit ext-co1 --as coprod-a-op --path "$CA"
claim "coprod-a-admit-applies-v1" 0 "seated" "applied"
run_cli pkg share --id ext-co1 --store "$MESH2" --path "$CA"
claim "coprod-share-one-verb" 0 "shared to the mesh" "ed25519:"
[ "$(ls "$MESH2"/*.ashpkg 2>/dev/null | wc -l)" -eq 1 ] \
  && result "coprod-share-lands-in-store" PASS "seal + publish in one step" \
  || result "coprod-share-lands-in-store" FAIL "share did not land exactly one package"

# node B: pull v1 — held by B's OWN gate, nothing on disk until B seats it
run_cli pkg pull --from "$MESH2" --path "$CB"
claim "coprod-b-pull-holds" 0 "HELD" "pulled 1"
[ ! -f "$CB/src/Coprod.cs" ] \
  && result "coprod-b-held-not-on-disk" PASS "B's gate holds A's work before it lands" \
  || result "coprod-b-held-not-on-disk" FAIL "pulled file landed before admission"
run_cli gates --admit ext-co1 --as coprod-b-op --path "$CB"
claim "coprod-b-admit-applies-v1" 0 "seated" "applied"
grep -q "coprod v1" "$CB/src/Coprod.cs" 2>/dev/null \
  && result "coprod-b-holds-v1" PASS "v1 landed on B through B's gate" \
  || result "coprod-b-holds-v1" FAIL "v1 content missing on B"

# node B: build v2 ON TOP of v1, admit through B's gate, share back to the same store
mkdir -p "$CB/.ashlar/forge/proposed"
cat > "$CB/.ashlar/forge/proposed/fco2.json" <<'EOF'
{"Id":"fco2","TargetPath":"src/Coprod.cs","NewContent":"// coprod v2 (builds on v1)","Summary":"improve brick coprod.classify","CreatedAt":"2026-08-25T07:00:00Z","UpdatedAt":"2026-08-25T07:00:00Z"}
EOF
cat > "$CB/pco2.json" <<'EOF'
{"id":"ext-co2","kind":"brick","summary":"improve brick coprod.classify","proposedBy":"night-agent",
 "proposedAt":"2026-08-25T07:00:00Z","diff":"~ 1 file",
 "forgeProposalIds":["fco2"],
 "courses":[{"name":"sandbox","passed":true,"detail":"confined"},{"name":"tests","passed":true,"detail":"15 passed"},{"name":"security","passed":true,"detail":"0 findings"}]}
EOF
run_cli gates propose --file "$CB/pco2.json" --path "$CB" >/dev/null
run_cli gates --admit ext-co2 --as coprod-b-op --path "$CB" >/dev/null
run_cli pkg share --id ext-co2 --store "$MESH2" --path "$CB"
claim "coprod-b-shares-v2-back" 0 "shared to the mesh"

# node A: pull the store — v1 is already-have (A's gate decided it), v2 holds, A seats it
run_cli pkg pull --from "$MESH2" --path "$CA"
claim "coprod-a-pull-dedupes-and-holds" 0 "already have" "HELD" "pulled 2"
run_cli gates --admit ext-co2 --as coprod-a-op --path "$CA"
claim "coprod-a-admit-applies-v2" 0 "seated" "applied"
grep -q "coprod v2" "$CA/src/Coprod.cs" 2>/dev/null \
  && result "coprod-a-holds-v2" PASS "A's file holds v2 — both gates, both directions" \
  || result "coprod-a-holds-v2" FAIL "v2 did not land on A"

# A THROW from the gate submission costs one row, not the pass. The read was already inside the
# per-row guard; SubmitAsync sat one statement outside it, so anything IT threw still aborted the
# whole pull — the exact denial the guard exists to prevent, moved one line further down and left
# open. The trap is a real failure and not a stub: a DIRECTORY where the gate record's file belongs
# makes GateStore's write-then-move fail — IOException "Is a directory" on Linux, an access denial
# on Windows — and either way it escapes SubmitAsync's own
# `when (ex is InvalidOperationException or ArgumentException)` filter and reaches the caller. It is
# the shape an operator meets for real when the receiving project's .ashlar cannot be written.
#
# ext-co1 sorts before ext-co2 in the store, so a pass that dies on the trap never reaches v2 — the
# file-on-disk check is what makes "the pass continued" a claim rather than a hope, and the counters
# are what keep the refusal from becoming a silent skip. Not in the Linux-only block: a directory is
# a directory on every host, and a rename onto one fails on NTFS exactly as it does on ext4.
CR=$(fresh); run_cli init coprod-r --path "$CR"; set_selfextending "$CR"
mkdir -p "$CR/.ashlar/gates/ext-co1.json"
run_cli pkg pull --from "$MESH2" --path "$CR"
claim "pkg-pull-submit-throw-refuses-row-and-continues" 65 "REFUSED" "ext-co1" "ADMITTED" "pulled 2" "admitted 1" "refused/rejected 1"
grep -q "coprod v2" "$CR/src/Coprod.cs" 2>/dev/null \
  && result "pkg-pull-submit-throw-later-package-still-lands" PASS "one row threw; the package behind it was still decided and applied" \
  || result "pkg-pull-submit-throw-later-package-still-lands" FAIL "a throw at one row denied every package behind it"

# content claims: the admission signed (path, sha256) for its rows at propose time, so a forge
# row edited AFTER the gate decided fails verification — the doctored bytes never travel under
# the origin's signature. Refusal is a 65, the same family as a package that fails its seal.
sed 's|// coprod v1|// tampered after admission|' "$CA/.ashlar/forge/applied/fco1.json" > "$CA/.t" \
  && mv "$CA/.t" "$CA/.ashlar/forge/applied/fco1.json"
run_cli pkg export --id ext-co1 --out "$WORK/tampered-row.ashpkg" --path "$CA"
claim "pkg-claims-refuse-edited-row" 65 "does not match the signed claim"
[ ! -f "$WORK/tampered-row.ashpkg" ] \
  && result "pkg-claims-nothing-written" PASS "a refused export writes no package" \
  || result "pkg-claims-nothing-written" FAIL "a package landed despite the failed claim"

# claims at the SEAT door: a held row edited between propose and admit is refused BEFORE any
# decision is recorded — the proposal stays held, nothing is decided, nothing lands on disk.
cat > "$CA/.ashlar/forge/proposed/fco3.json" <<'EOF'
{"Id":"fco3","TargetPath":"src/Held.cs","NewContent":"// held v1","Summary":"held work","CreatedAt":"2026-08-25T08:00:00Z","UpdatedAt":"2026-08-25T08:00:00Z"}
EOF
cat > "$CA/pco3.json" <<'EOF'
{"id":"ext-co3","kind":"brick","summary":"add brick held.work","proposedBy":"night-agent",
 "proposedAt":"2026-08-25T08:00:00Z","diff":"+ 1 file",
 "forgeProposalIds":["fco3"],
 "courses":[{"name":"sandbox","passed":true,"detail":"confined"},{"name":"tests","passed":true,"detail":"14 passed"},{"name":"security","passed":true,"detail":"0 findings"}]}
EOF
run_cli gates propose --file "$CA/pco3.json" --path "$CA" >/dev/null
sed 's|// held v1|// swapped while held|' "$CA/.ashlar/forge/proposed/fco3.json" > "$CA/.t" \
  && mv "$CA/.t" "$CA/.ashlar/forge/proposed/fco3.json"
run_cli gates --admit ext-co3 --as coprod-a-op --path "$CA"
claim "gates-admit-refuses-edited-row" 65 "does not match the signed claim" "stays Held"
[ ! -f "$CA/src/Held.cs" ] \
  && result "gates-admit-refusal-nothing-lands" PASS "tampered bytes never reach the tree" \
  || result "gates-admit-refusal-nothing-lands" FAIL "tampered content landed"

# a proposal may not reference rows the store does not hold — the claim it would sign would
# cover nothing, and records are append-once, so the refusal comes at propose time.
cat > "$CA/pco4.json" <<'EOF'
{"id":"ext-co4","kind":"brick","summary":"add brick dangling.ref","proposedBy":"night-agent",
 "proposedAt":"2026-08-25T09:00:00Z","diff":"+ 1 file",
 "forgeProposalIds":["fco-nowhere"],
 "courses":[{"name":"sandbox","passed":true,"detail":"confined"}]}
EOF
run_cli gates propose --file "$CA/pco4.json" --path "$CA"
claim "gates-propose-refuses-dangling-row" 1 "not in the store" "park the write first"

unset ASHLAR_KEY_DIR

# ───────────────────────────── export native: the agentic-app bundle ─────────────────────────────
# A certified project becomes a portable, self-proving bundle. The runtime binary is a separate
# (slow) publish, so here we prove the deterministic staging and that the staged app self-verifies
# offline with NO origin key — the download proving its own certification.
export ASHLAR_KEY_DIR="$WORK/exportkeys"
run_cli keys init >/dev/null

DE=$(fresh); run_cli init exportdemo --path "$DE"
run_cli verify --path "$DE" >/dev/null      # certify: ledger #1
run_cli export native --path "$DE" --out "$WORK/bundles" --rid linux-x64 --no-runtime
claim "export-native-stages-bundle" 0 "CERTIFIED bundle" "exportdemo" "linux-x64"

BUN="$WORK/bundles/exportdemo-linux-x64"
{ [ -f "$BUN/app/ashlar.yaml" ] && [ -f "$BUN/app/ashlar.policy.yaml" ] && [ -d "$BUN/app/.ashlar" ] \
  && [ -f "$BUN/run.sh" ] && [ -f "$BUN/run.cmd" ] && [ -f "$BUN/bundle.json" ] && [ -f "$BUN/README.md" ]; } \
  && result "export-native-bundle-complete" PASS "app + ledger + launchers + descriptor" \
  || result "export-native-bundle-complete" FAIL "bundle incomplete"

grep -q '"certified": true' "$BUN/bundle.json" \
  && result "export-native-descriptor-certified" PASS "descriptor records the certification" \
  || result "export-native-descriptor-certified" FAIL "descriptor missing certification"

# the launcher verifies before it runs — the self-proving contract, checked in the staged script
grep -q "verify --path" "$BUN/run.sh" \
  && result "export-native-launcher-self-proves" PASS "run.sh verifies before running" \
  || result "export-native-launcher-self-proves" FAIL "launcher does not self-verify"

# a DOWNLOADER with NO origin key still confirms the app's certification chain, offline
DVOUT=$(ASHLAR_KEY_DIR="$WORK/emptykeys" NO_COLOR=1 dotnet run --project "$CLI_PROJ" --no-build -- verify --path "$BUN/app" 2>&1); DVRC=$?
OUT="$DVOUT"; RC="$DVRC"
claim "export-native-app-self-verifies-keyless" 0 "provenance" "chain intact"

# tamper the staged contract after export: a keyless downloader's verify REFUSES it (the whole
# point of self-proving — an altered app does not run)
echo "# tampered after signing" >> "$BUN/app/ashlar.yaml"
DTOUT=$(ASHLAR_KEY_DIR="$WORK/emptykeys" NO_COLOR=1 dotnet run --project "$CLI_PROJ" --no-build -- verify --path "$BUN/app" 2>&1); DTRC=$?
OUT="$DTOUT"; RC="$DTRC"
claim "export-native-tampered-app-refused" 65 "do not match the certification" "provenance"

# refuses to export a project that does not verify
DBAD=$(fresh); run_cli init badexport --path "$DBAD"
echo "kind: Nonsense" > "$DBAD/ashlar.yaml"
run_cli export native --path "$DBAD" --out "$WORK/bundles" --rid linux-x64 --no-runtime
claim "export-native-refuses-unverified" 65 "does not verify"

# ───────────────────────────── export aws / azure: one-command cloud bundles ─────────────────────────────
# Same certified project, staged for the cloud: the runtime image + this app + a verify-then-run
# entrypoint + a one-command deploy script. Nothing here touches a cloud — the scripts do, when run.
run_cli export aws --path "$DE" --out "$WORK/bundles"
claim "export-aws-stages-bundle" 0 "CERTIFIED cloud bundle" "aws"
AWSB="$WORK/bundles/exportdemo-aws"
{ [ -f "$AWSB/Dockerfile" ] && [ -f "$AWSB/entrypoint.sh" ] && [ -f "$AWSB/deploy-aws.sh" ] \
  && [ -f "$AWSB/app/ashlar.yaml" ] && [ -d "$AWSB/app/.ashlar" ] && [ -f "$AWSB/bundle.json" ]; } \
  && result "export-aws-bundle-complete" PASS "Dockerfile + entrypoint + deploy + app + ledger" \
  || result "export-aws-bundle-complete" FAIL "aws bundle incomplete"
grep -q "verify --path /work/app" "$AWSB/entrypoint.sh" \
  && result "export-aws-container-self-proves" PASS "the container verifies before it runs" \
  || result "export-aws-container-self-proves" FAIL "entrypoint does not verify first"

run_cli export azure --path "$DE" --out "$WORK/bundles"
claim "export-azure-stages-bundle" 0 "CERTIFIED cloud bundle" "azure"
AZB="$WORK/bundles/exportdemo-azure"
{ [ -f "$AZB/deploy-azure.sh" ] && grep -q "az acr build" "$AZB/deploy-azure.sh" && grep -q "az container create" "$AZB/deploy-azure.sh"; } \
  && result "export-azure-deploy-script" PASS "ACR build + ACI one-shot" \
  || result "export-azure-deploy-script" FAIL "azure deploy script wrong"

run_cli export aws --path "$DBAD" --out "$WORK/bundles"
claim "export-aws-refuses-unverified" 65 "does not verify"

unset ASHLAR_KEY_DIR

# ───────────────────────────── verdict ─────────────────────────────
echo
echo "==================== e2e-loop verdict ===================="
echo "scenarios: $N   pass: $PASS   fail: $FAIL"
if [ "$FAIL" -gt 0 ]; then
  echo "FAILED scenarios:"; awk -F'\t' '$4=="FAIL"' "$RESULTS" | sed 's/^/  /'
  exit 1
fi
echo "every behavioural claim under test holds"
