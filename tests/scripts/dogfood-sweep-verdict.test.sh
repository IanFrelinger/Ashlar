#!/usr/bin/env bash
# Tests for classify_sweep_log / sweep_failure_reason in scripts/dogfood-continuous-proof.sh.
#
# WHY THIS EXISTS. On 2026-09-14 the dogfood workflow wrote **PASS** to a ledger that gates an
# autonomy marketing claim, for a sweep in which nothing ran: the sandbox image was absent, the
# single objective died at `docker run` with "No such image", AutonomyLoopService caught it and
# kept sweeping, SweepMode reported "attempted 1 objective(s)" and exited 0, and the workflow
# mapped exit 0 to PASS. Run 34889059104. The row never reached git only because the workflow
# cannot push to a protected branch.
#
# So the verdict logic is the part of this script that most needs proving and was, until it was
# split out, the part that could not be run at all without dotnet, a container engine and an SDK
# image. It is now a pair of pure functions, and case 3 below replays that exact log.
#
# Run:  bash tests/scripts/dogfood-sweep-verdict.test.sh
# Pure bash: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
SCRIPT="${ROOT}/scripts/dogfood-continuous-proof.sh"

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

# Probe in a SUBSHELL first. If the dispatch guard at the foot of that script is ever removed,
# sourcing falls through to its usage branch, and that branch's `exit 1` terminates whatever shell
# is running it — so sourcing directly would kill this file before it could print why. In a
# subshell the exit is contained and the failure can be reported.
# shellcheck disable=SC1090
if ! ( source "${SCRIPT}" >/dev/null 2>&1 && declare -F classify_sweep_log >/dev/null 2>&1 ); then
  echo "FAIL — ${SCRIPT} is not sourceable, or no longer defines classify_sweep_log."
  echo "       Is the BASH_SOURCE dispatch guard still at the foot of that script?"
  exit 1
fi

# shellcheck disable=SC1090
source "${SCRIPT}" >/dev/null 2>&1 || true

# The script sets `-euo pipefail`, and sourcing applies that to THIS shell. Under -e the first
# assertion whose command returns nonzero — which is most of them, since they are testing for
# nonzero — aborts the run mid-file. That is not hypothetical: it happened while this file was
# being written. The run stopped partway through having printed nothing but passes; the exit code
# was nonzero so CI would have caught it, but nothing on screen said "truncated". Hence the
# assertion-count check at the foot of this file.
set +e

if ! declare -F classify_sweep_log >/dev/null; then
  echo "FAIL — classify_sweep_log is not defined after sourcing ${SCRIPT}"
  echo "       (is the BASH_SOURCE dispatch guard still at the foot of that script?)"
  exit 1
fi

# A test file that stops early must fail LOUDLY rather than just exit nonzero, for the same reason
# the sweep must not report a fault as a pass. Bump this when assertions are added.
EXPECTED_ASSERTIONS=28

mklog() { local f; f="$(mktemp)"; printf '%s\n' "$1" > "${f}"; printf '%s' "${f}"; }

verdict_of() {
  local rc=0
  classify_sweep_log "$1" "$2" || rc=$?
  printf '%s' "${rc}"
}

echo "== classify_sweep_log =="

# 1. The happy path: the loop attempted an objective and the iteration reached a verdict.
L="$(mklog 'SWEEP: attempted 1 objective(s) in 41.2s
SWEEP: complete (see the iteration outcome logged above)')"
v="$(verdict_of 0 "${L}")"
[[ "${v}" == "0" ]] && ok "completed iteration -> 0 (PASS)" || bad "completed iteration -> 0" "got ${v}"
rm -f "${L}"

# 2. Nothing attempted: SweepMode already exits nonzero, and that stays a refusal.
L="$(mklog 'SWEEP: no objective was eligible — check for a witness and proposal beside each one')"
v="$(verdict_of 1 "${L}")"
[[ "${v}" == "1" ]] && ok "nothing attempted -> 1 (GAP)" || bad "nothing attempted -> 1" "got ${v}"
rm -f "${L}"

# 3. THE REGRESSION. Verbatim shape of run 34889059104: exit 0, "attempted 1", and an iteration
# that never started because the sandbox image was missing. This MUST NOT be 0.
REAL_LOG='warn: Ashlar.BackgroundAgents.Autonomy.AutonomyLoopService[0] Objective rgb-hex-parse failed (/home/runner/work/Ashlar/Ashlar/.ashlar/runtime-studio/objectives/pending/rgb-hex-parse.md); continuing the sweep System.InvalidOperationException: Sandbox session '"'"'ashlar-session-a208107fb7b2444f8260b15420ed7523'"'"' failed to start (exit 125): docker: Error response from daemon: No such image: mcr.microsoft.com/dotnet/sdk:10.0  Run '"'"'docker run --help'"'"' for more information     at Ashlar.Infrastructure.Execution.Sandbox.DockerSandboxedSessionRunner.StartAsync(SandboxSpec spec, CancellationToken cancellationToken) in /_/src/Ashlar.Infrastructure/Execution/Sandbox/DockerSandboxedSessionRunner.cs:line 82    at Ashlar.BackgroundAgents.Autonomy.AutonomyLoopService.SweepAsync(CancellationToken cancellationToken) in /_/src/Ashlar.BackgroundAgents/Autonomy/AutonomyLoopService.cs:line 254
SWEEP: attempted 1 objective(s) in 0.2s
SWEEP: complete (see the iteration outcome logged above)'
L="$(mklog "${REAL_LOG}")"
v="$(verdict_of 0 "${L}")"
if [[ "${v}" == "2" ]]; then
  ok "run 34889059104's log -> 2 (GAP), not 0"
else
  bad "run 34889059104's log -> 2 (GAP), not 0" "got ${v} — a failed sweep would be recorded as a pass again"
fi

# NOTE: cases 4-6 use ${L2}. ${L} from case 3 is deliberately left alive for the
# sweep_failure_reason section below, and clobbering it made those two assertions fail.
#
# 4. The sweep now says it itself. SweepAsync returns SweepOutcome.Failed and SweepMode maps a
# nonzero one to exit 2, so this no longer depends on any log wording at all.
L2="$(mklog 'SWEEP: attempted 1 objective(s) in 0.2s
SWEEP: 1 of 1 objective(s) reached no verdict — infrastructure fault, not a result; refusing to report this as a pass')"
v="$(verdict_of 2 "${L2}")"
[[ "${v}" == "2" ]] && ok "exit 2 -> 2 (GAP), from the count and not the prose" \
  || bad "exit 2 -> 2 from the count" "got ${v}"
rm -f "${L2}"

# 5. And it must not be downgraded to a refusal. A bare `-ne 0` check would return 1 here, which
# reports the wrong KIND of failure: "nothing was eligible" instead of "an iteration died".
v="$(verdict_of 2 /nonexistent-log-path)"
[[ "${v}" == "2" ]] && ok "exit 2 with no readable log -> still 2, not 1 (refusal)" \
  || bad "exit 2 with no readable log -> 2" "got ${v} — a fault reported as an ineligible sweep"

# 6. The two signals must agree, and a disagreement is a failure. Exit 0 beside a failure in the
# log is either a wrong exit code or a regressed count; both are refusals, and this is also how an
# older binary that cannot exit 2 stays classified correctly.
L2="$(mklog 'warn: Ashlar.BackgroundAgents.Autonomy.AutonomyLoopService[0] Objective x failed (/tmp/x.md); continuing the sweep
SWEEP: attempted 1 objective(s) in 0.3s')"
v="$(verdict_of 0 "${L2}")"
[[ "${v}" == "2" ]] && ok "exit 0 disagreeing with the log -> 2 (the grep still bites)" \
  || bad "exit 0 disagreeing with the log -> 2" "got ${v}"
rm -f "${L2}"

echo "== sweep_failure_reason =="

why="$(sweep_failure_reason "${L}")"
grep -q 'No such image' <<<"${why}" \
  && ok "names the actual cause" || bad "names the actual cause" "got '${why}'"
grep -q 'InvalidOperationException' <<<"${why}" \
  && ok "keeps the exception type" || bad "keeps the exception type" "got '${why}'"
# A ledger cell is read by a human. Stack frames belong in the attached log, not in the table.
if grep -q '   at Ashlar' <<<"${why}"; then
  bad "strips the stack frames" "stack trace leaked into the ledger reason: '${why}'"
else
  ok "strips the stack frames"
fi
rm -f "${L}"

# The cap needs a reason LONGER than the cap to mean anything. Asserting it against the run
# 34889059104 message would be vacuous — that one is well under 400 characters, so deleting the
# cap entirely leaves the assertion green. Exceptions from a build or test failure inside the
# sandbox carry compiler output and run much longer than this.
LONG="$(printf 'x%.0s' $(seq 1 900))"
L="$(mklog "warn: AutonomyLoopService[0] Objective rgb-hex-parse failed (/x.md); continuing the sweep System.Exception: ${LONG}")"
why="$(sweep_failure_reason "${L}")"
if [[ "${#why}" -eq 400 ]]; then
  ok "caps an over-long reason at 400 chars"
else
  bad "caps an over-long reason at 400 chars" "got ${#why} from a reason of ~900"
fi
rm -f "${L}"

# A clean log has no reason to report.
L="$(mklog 'SWEEP: attempted 1 objective(s) in 41.2s')"
why="$(sweep_failure_reason "${L}")"
[[ -z "${why}" ]] && ok "empty on a clean log" || bad "empty on a clean log" "got '${why}'"
rm -f "${L}"

echo "== the marker this depends on still exists in the source =="
# classify_sweep_log reads a log template owned by another file. If that template is reworded the
# tests above keep passing against their own fixtures while production silently returns to
# reporting failures as passes — so assert the real template too.
#
# Match the WHOLE template including its placeholders, not the "; continuing the sweep" fragment.
# The fragment appears in that file's explanatory comment as well as in the log call, so a grep
# for it matches the comment and survives any rewording of the call — which is precisely what it
# did when this assertion was first written: the template was renamed to "; moving on" and this
# stayed green. Only the full literal below is unique to the LogWarning.
SRC="${ROOT}/src/Ashlar.BackgroundAgents/Autonomy/AutonomyLoopService.cs"
TEMPLATE='"Objective {Id} failed ({Path}); continuing the sweep"'
if grep -qF "${TEMPLATE}" "${SRC}"; then
  ok "AutonomyLoopService still logs the exact template classify_sweep_log parses"
else
  bad "AutonomyLoopService still logs the exact template classify_sweep_log parses" \
      "${TEMPLATE} is gone from ${SRC}; failed sweeps will be recorded as passes again"
fi

# The dogfood workflow used to grep CertificationVerifyOptions.cs to decide whether Strict still
# required an Ed25519 signature, and gated the sweep and a GAP row on the answer. That step is gone,
# because the same regression is asserted BEHAVIOURALLY - through the preset, not its source text -
# by the fact named below, which sits in cert-gate's filter and therefore blocks a merge.
#
# This assertion is what makes that delegation safe. If the behavioural fact is deleted or renamed,
# nothing is watching Strict any more, and the removal of the workflow step silently became a loss of
# coverage. A required check failing here says so, and says where to look. It deliberately does NOT
# re-check Strict's source: duplicating the scan is the mistake being undone.
# The full signature, not just the name. A bare substring match on the name is vacuous under the
# cheapest realistic edit: renaming the method by appending to it leaves the old name as a substring,
# so the guard passes while the cross-reference in dogfood-continuous-proof.yml has gone stale.
# Measured — the first version of this assertion survived exactly that mutation. Matching the
# signature means a rename trips this and the fix is to update both places, which is the point.
STRICT_FACT_FILE="${ROOT}/src/Ashlar.Tests.Infrastructure/Tests/Certification/SchemaVersionFloorTests.cs"
STRICT_FACT='public void Strict_RejectsRecordWithoutEd25519()'
if [[ -f "${STRICT_FACT_FILE}" ]] && grep -qF "${STRICT_FACT}" "${STRICT_FACT_FILE}"; then
  ok "the behavioural Strict/Ed25519 fact the workflow now delegates to still exists"
else
  bad "the behavioural Strict/Ed25519 fact the workflow now delegates to still exists" \
      "${STRICT_FACT} is gone from ${STRICT_FACT_FILE}. The dogfood workflow dropped its own Strict
         source-grep on the grounds that this fact covers it inside cert-gate. Restore the fact, or
         put a merge-blocking replacement in the Certification namespace before removing it"
fi

echo "== classify_sweep_evidence =="

# Unlock criterion 3a asks whether the run's certification record was PERSISTED and RE-VERIFIED.
# That question is answered by the archive's verdict sidecar, not by the sweep log, and the answer
# has to fail closed: the shapes below are the ones that must NOT read as a pass.

if ! declare -F classify_sweep_evidence >/dev/null; then
  echo "FAIL — classify_sweep_evidence is not defined after sourcing ${SCRIPT}"
  exit 1
fi

# Writes one sidecar into a fresh directory and echoes the directory.
mkevidence() {
  local d; d="$(mktemp -d)"
  printf '%s\n' "$2" > "${d}/$1.evidence.json"
  printf '%s' "${d}"
}

GOOD_SIDECAR='{
  "verified": true,
  "brickId": "rgb-hex-parse",
  "recordPath": "/tmp/campaign/records/rgb-hex-parse.json",
  "recordSha256": "3f786850e387550fdab836ed7e6dc881de23001b0000000000000000deadbeef",
  "signerFingerprint": "ed25519:0123456789abcdef",
  "pinningEnabled": false,
  "usesDevHmacKey": true,
  "failureCode": null,
  "failureReason": null,
  "verifiedAtUtc": "2026-09-28T06:00:00+00:00"
}'

evidence_of() {
  local rc=0
  classify_sweep_evidence "$1" || rc=$?
  printf '%s' "${rc}"
}

# 1. THE ASSERTION THAT MATTERS MOST. No sidecar at all — an archive that was never wired up, or a
# spike configuration line that was deleted (spikes/ is compiled by nothing, so no gate would say
# so) — must be a GAP. If this ever returns 0, every other guard in this feature is decoration and
# the ledger records PASS for a run with no reviewable record.
D="$(mktemp -d)"
v="$(evidence_of "${D}")"
if [[ "${v}" == "3" ]]; then
  ok "no sidecar -> 3 (GAP)"
else
  bad "no sidecar -> 3 (GAP)" "got ${v} — an UNWIRED archive would be recorded as a pass, and the row would cite nothing"
fi
rmdir "${D}" 2>/dev/null || true

# 2. The happy path, so clause 1 is not simply "always 3".
D="$(mkevidence rgb-hex-parse "${GOOD_SIDECAR}")"
v="$(evidence_of "${D}")"
[[ "${v}" == "0" ]] && ok "a verified sidecar with a fingerprint and a record path -> 0 (PASS)" \
  || bad "a verified sidecar -> 0" "got ${v} — no row could ever count toward 3a"
CITE="$(sweep_evidence_citation "${D}")"
rm -rf "${D}"

# 3. A record that did NOT re-verify is a GAP, and the reason must name the verifier's own code.
#
# Every OTHER cited field is deliberately present and non-empty here, so this fixture can only be
# refused by the "verified" check itself. The first version of this assertion used a sidecar with a
# null signerFingerprint, and deleting the "verified" check left it green — the fingerprint clause
# was catching it. Measured, not assumed.
D="$(mkevidence rgb-hex-parse '{
  "verified": false,
  "brickId": "rgb-hex-parse",
  "recordPath": "/tmp/campaign/records/rgb-hex-parse.json",
  "recordSha256": "3f786850e387550fdab836ed7e6dc881de23001b0000000000000000deadbeef",
  "signerFingerprint": "ed25519:0123456789abcdef",
  "pinningEnabled": true,
  "usesDevHmacKey": true,
  "failureCode": "ed25519-key-not-trusted",
  "failureReason": "Certification record is signed by a key this verifier does not accept.",
  "verifiedAtUtc": "2026-09-28T06:00:00+00:00"
}')"
v="$(evidence_of "${D}")"
[[ "${v}" == "3" ]] && ok "\"verified\": false -> 3 (GAP)" \
  || bad "\"verified\": false -> 3" "got ${v} — a refused record would be reported as verified"
why="$(sweep_evidence_reason "${D}")"
grep -q 'ed25519-key-not-trusted' <<<"${why}" \
  && ok "the reason names the verifier's own failure code" \
  || bad "the reason names the verifier's own failure code" "got '${why}'"
rm -rf "${D}"

# 4. A row cites a signer fingerprint. An empty one is not a citation.
D="$(mkevidence rgb-hex-parse "${GOOD_SIDECAR/\"ed25519:0123456789abcdef\"/\"\"}")"
v="$(evidence_of "${D}")"
[[ "${v}" == "3" ]] && ok "an empty signerFingerprint -> 3" \
  || bad "an empty signerFingerprint -> 3" "got ${v} — the row would cite an empty string"
rm -rf "${D}"

# 5. And an UNQUOTED null is the shape the archive actually writes when there is no key at all, so
# it is asserted separately from the empty string above.
D="$(mkevidence rgb-hex-parse "${GOOD_SIDECAR/\"ed25519:0123456789abcdef\"/null}")"
v="$(evidence_of "${D}")"
[[ "${v}" == "3" ]] && ok "a null signerFingerprint -> 3" \
  || bad "a null signerFingerprint -> 3" "got ${v} — an unsigned record would read as cited"
rm -rf "${D}"

# 6. Same for the record path: a row that cannot name the file is not a 3a row.
D="$(mkevidence rgb-hex-parse "${GOOD_SIDECAR/\"\/tmp\/campaign\/records\/rgb-hex-parse.json\"/\"\"}")"
v="$(evidence_of "${D}")"
[[ "${v}" == "3" ]] && ok "an empty recordPath -> 3" \
  || bad "an empty recordPath -> 3" "got ${v} — the row would cite no file"
rm -rf "${D}"

# 7. Two sidecars in one directory. A campaign directory is reused across reruns, and `head -1`
# over two verdicts would silently let one run decide another run's row.
D="$(mkevidence rgb-hex-parse "${GOOD_SIDECAR}")"
printf '%s\n' "${GOOD_SIDECAR}" > "${D}/some-other-brick.evidence.json"
v="$(evidence_of "${D}")"
[[ "${v}" == "3" ]] && ok "two sidecars in one campaign directory -> 3 (ambiguous evidence is not evidence)" \
  || bad "two sidecars -> 3" "got ${v} — one run's verdict could be read as another's"
rm -rf "${D}"

# 8. The citation is what a 3a-counting row carries, so it has to carry all three cited values.
if grep -q 'record=/tmp/campaign/records/rgb-hex-parse.json' <<<"${CITE}" \
  && grep -q 'sha256=3f786850e387550fdab836ed7e6dc881de23001b0000000000000000deadbeef' <<<"${CITE}" \
  && grep -q 'signer=ed25519:0123456789abcdef' <<<"${CITE}" \
  && grep -q 'pinned=false' <<<"${CITE}" \
  && grep -q 'dev-hmac=true' <<<"${CITE}"; then
  ok "the citation carries the record path, its sha256, the signer, and the pinning/dev-key disclosures"
else
  bad "the citation carries the record path, its sha256, the signer, and the pinning/dev-key disclosures" \
      "got '${CITE}'"
fi

echo "== the sidecar contract this depends on is frozen inside cert-gate =="
# classify_sweep_evidence parses field names owned by a C# type. If they are renamed, every
# assertion above keeps passing against its own hand-written fixtures while production silently
# returns GAP forever. The merge-blocking fact that freezes those names is named here so a
# rename fails a required check and says where to look — the same delegation the Strict/Ed25519
# assertion above uses, for the same reason. Matching the full signature, not the bare name: a
# rename that APPENDS to a method name leaves the old name as a substring and the guard passes.
SIDECAR_FACT_FILE="${ROOT}/src/Ashlar.Tests.Infrastructure/Tests/Certification/CertificationEvidenceArchiveTests.cs"
SIDECAR_FACT='public void PersistAndReverify_WritesASidecarWithTheFieldNamesTheSweepScriptParses()'
if [[ -f "${SIDECAR_FACT_FILE}" ]] && grep -qF "${SIDECAR_FACT}" "${SIDECAR_FACT_FILE}"; then
  ok "the merge-blocking fact that freezes the sidecar's field names still exists"
else
  bad "the merge-blocking fact that freezes the sidecar's field names still exists" \
      "${SIDECAR_FACT} is gone from ${SIDECAR_FACT_FILE}. classify_sweep_evidence parses those
         names; without that fact a rename makes every sweep a GAP with nothing saying why.
         Restore the fact, or put a merge-blocking replacement in the Certification namespace"
fi

echo "== the script emits what the workflow greps for =="
# The sweep script ECHOES a citation line; the workflow separately GREPS it back out of the log and
# interpolates it into the ledger row. Nothing coupled the two, so renaming either side left every
# assertion in this file green and produced a row reading "Counts toward 3a: ." - a 3a-counting PASS
# citing nothing, which is the pre-#655 shape one layer up. Measured before this guard existed:
# renaming the script's echo to 'SWEEP RECORD:' kept 23/23 green.
#
# Both sides are EXTRACTED from the real files. A literal restated here would be a third copy that
# also drifts. Each extraction is guarded for non-emptiness first, because `grep -qF ""` matches
# everything - an extraction that silently stopped matching would otherwise pass forever.
SWEEP_SCRIPT="${ROOT}/scripts/dogfood-continuous-proof.sh"
SWEEP_WF="${ROOT}/.github/workflows/dogfood-continuous-proof.yml"

check_literal_shared() {
  local what="$1" literal="$2"
  if [[ -z "${literal}" ]]; then
    bad "${what}" "the literal could not be extracted from ${SWEEP_SCRIPT}. Either the echo was
         reshaped or this guard's pattern is stale. An empty literal would match the workflow
         trivially, so this is a failure and not a skip"
  elif grep -qF -- "${literal}" "${SWEEP_WF}"; then
    ok "${what}"
  else
    bad "${what}" "the script emits '${literal}' but ${SWEEP_WF} does not grep that string.
         One side was renamed. The row would keep its Pass/Fail cell and lose the reason or the
         citation that makes the cell mean anything"
  fi
}

check_literal_shared "the citation prefix the script echoes is the one the workflow greps" \
  "$(grep -o 'echo "[^"]*\$(sweep_evidence_citation' "${SWEEP_SCRIPT}" | head -1 \
     | sed 's/^echo "//; s/\$(sweep_evidence_citation$//')"

check_literal_shared "the persist-failure prefix the script echoes is the one the workflow greps" \
  "$(grep -o 'echo "[^"]*\$(sweep_evidence_reason' "${SWEEP_SCRIPT}" | head -1 \
     | sed 's/^echo "//; s/\$(sweep_evidence_reason$//')"

check_literal_shared "the precondition prefix the script echoes is the one the workflow greps" \
  "$(grep -o 'PRECONDITION FAILED:' "${SWEEP_SCRIPT}" | head -1)"

# A PASS whose citation is empty must not claim 3a. The workflow's own comment already said a row
# without the citation line does not count; nothing enforced it.
if grep -q 'Does NOT count toward 3a' "${SWEEP_WF}" \
  && grep -q 'z "${EVIDENCE}"' "${SWEEP_WF}"; then
  ok "a PASS with no citation is refused the 3a claim rather than counting with an empty one"
else
  bad "a PASS with no citation is refused the 3a claim rather than counting with an empty one" \
      "${SWEEP_WF} no longer guards the empty-citation case, so exit 0 with no record emits
         'Counts toward 3a: .' — a row asserting evidence it does not have"
fi

# The ephemeral signing key is written to \$GITHUB_ENV, so it is in the environment of the sweep
# step whose stdout is tee'd into an uploaded log. ::add-mask:: is the only thing standing between
# that and a published key, it occurs exactly once in the repository, and nothing asserted it:
# shell-lint's bash -n walks scripts/ and tests/ only, and shellcheck only scripts/**/*.sh, so
# embedded workflow bash is never parsed at all. Deleting the mask line left every check green.
MASK_LINE="$(grep -n '::add-mask::' "${SWEEP_WF}" | head -1 | cut -d: -f1)"
KEY_ENV_LINE="$(grep -n 'ASHLAR_CERT_ED25519_KEY=.*GITHUB_ENV' "${SWEEP_WF}" | head -1 | cut -d: -f1)"
if [[ -n "${MASK_LINE}" && -n "${KEY_ENV_LINE}" && "${MASK_LINE}" -lt "${KEY_ENV_LINE}" ]]; then
  ok "the ephemeral key is masked before it is exported to the job environment"
else
  bad "the ephemeral key is masked before it is exported to the job environment" \
      "mask at line '${MASK_LINE:-none}', \$GITHUB_ENV write at line '${KEY_ENV_LINE:-none}'.
         The key reaches the sweep step's environment and that step's stdout is uploaded; without
         ::add-mask:: ahead of the export, a key that leaks into the log is published verbatim"
fi

echo
echo "passed: ${PASS}   failed: ${FAIL}"

RAN=$((PASS + FAIL))
if [[ "${RAN}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL — ran ${RAN} assertions, expected ${EXPECTED_ASSERTIONS}."
  echo "       Either this file stopped early (see the set +e note at the top) or assertions were"
  echo "       added without bumping EXPECTED_ASSERTIONS. A partial run is not a pass."
  exit 1
fi

[[ "${FAIL}" -eq 0 ]] || exit 1
