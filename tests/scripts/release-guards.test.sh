#!/usr/bin/env bash
# Tests for scripts/lib/release-staging-guards.sh and the nuget-visibility poll budget.
#
# WHY THIS EXISTS. These guards are the last thing between a mislabelled package and nuget.org,
# and they had no test of their own: every one of them was only ever exercised as a side effect
# of a real release, which has run three times in this repository's lifetime. A guard that is
# only exercised by the event it guards is indistinguishable from a guard that does nothing.
#
# Each case asserts the EXIT CODE, because that is what the callers branch on, and the message,
# because the message is what a human acts on. A guard that refuses for the wrong stated reason
# sends the reader to the wrong fix.
#
# Run:  bash tests/scripts/release-guards.test.sh
# These are pure bash: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
GUARDS="${ROOT}/scripts/lib/release-staging-guards.sh"

PASS=0
FAIL=0

ok()   { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad()  { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

# Run one guard call inside a throwaway repo root so VERSION can be controlled.
# Echoes: "<exit>|<combined output>"
in_fake_root() {
  local version_content="$1"; shift
  local tmp; tmp="$(mktemp -d)"
  mkdir -p "${tmp}/scripts/lib"
  cp "${GUARDS}" "${tmp}/scripts/lib/"
  cp "${ROOT}/scripts/resolve-canonical-package-version.sh" "${tmp}/scripts/"
  # "__NONE__" means: no VERSION file at all, which is a distinct case from an empty one.
  if [[ "${version_content}" != "__NONE__" ]]; then
    printf '%s' "${version_content}" > "${tmp}/VERSION"
  fi
  local out exit_code
  out="$(cd "${tmp}" && bash -c "source scripts/lib/release-staging-guards.sh; $*" 2>&1)"
  exit_code=$?
  rm -rf "${tmp}"
  printf '%s|%s' "${exit_code}" "${out}"
}

echo "== normalize_semver =="
for pair in "v1.2.3:1.2.3" "V1.2.3:1.2.3" "1.2.3:1.2.3" "v0.2.0-rc.1:0.2.0-rc.1"; do
  input="${pair%%:*}"; want="${pair##*:}"
  got="$(bash -c "source '${GUARDS}'; normalize_semver '${input}'")"
  [[ "${got}" == "${want}" ]] && ok "${input} -> ${want}" || bad "${input} -> ${want}" "got '${got}'"
done

echo "== assert_valid_semver =="
for v in "1.2.3" "v1.2.3" "0.2.0-rc.1" "10.20.30"; do
  r="$(in_fake_root "1.0.0" "assert_valid_semver '${v}'")"
  [[ "${r%%|*}" == "0" ]] && ok "accepts ${v}" || bad "accepts ${v}" "exit ${r%%|*}"
done
for v in "1.2" "1.2.3.4" "abc" "" "1.2.3-"; do
  r="$(in_fake_root "1.0.0" "assert_valid_semver '${v}'")"
  [[ "${r%%|*}" != "0" ]] && ok "rejects '${v}'" || bad "rejects '${v}'" "exit 0, should be nonzero"
done

echo "== assert_version_matches_canonical =="

r="$(in_fake_root "1.2.3" "assert_version_matches_canonical v1.2.3")"
[[ "${r%%|*}" == "0" ]] && ok "matching tag exits 0" || bad "matching tag exits 0" "exit ${r%%|*}: ${r#*|}"

r="$(in_fake_root "1.2.3" "assert_version_matches_canonical v9.9.9")"
if [[ "${r%%|*}" != "0" ]] && grep -q 'does not match' <<<"${r#*|}"; then
  ok "mismatched tag exits nonzero and says so"
else
  bad "mismatched tag exits nonzero and says so" "exit ${r%%|*}: ${r#*|}"
fi

# A missing VERSION file is a SKIP, not a refusal: this guard also runs in trees that have not
# adopted the file, and refusing there would block work it has no opinion about.
r="$(in_fake_root "__NONE__" "assert_version_matches_canonical v1.2.3")"
if [[ "${r%%|*}" == "0" ]] && grep -q 'SKIPPED' <<<"${r#*|}"; then
  ok "absent VERSION exits 0 and says SKIPPED"
else
  bad "absent VERSION exits 0 and says SKIPPED" "exit ${r%%|*}: ${r#*|}"
fi

# An EMPTY VERSION file is the opposite: the file exists, so somebody meant it to say something.
r="$(in_fake_root "" "assert_version_matches_canonical v1.2.3")"
if [[ "${r%%|*}" != "0" ]] && grep -qi 'empty' <<<"${r#*|}"; then
  ok "empty VERSION refuses and says empty"
else
  bad "empty VERSION refuses and says empty" "exit ${r%%|*}: ${r#*|}"
fi

# The tag arrives with a leading v and the file never has one; normalisation is load-bearing.
r="$(in_fake_root "1.2.3" "assert_version_matches_canonical 1.2.3")"
[[ "${r%%|*}" == "0" ]] && ok "bare version matches a v-prefixed tag" || bad "bare version matches" "exit ${r%%|*}"

echo "== nuget visibility poll budget (ASHLAR_NUGET_VERIFY_ALLOW_SHORT) =="
# The budget guard raises a short ATTEMPTS to 40 unless a caller explicitly opts out. nuget.org
# indexing lags publication by minutes; a short budget silently reports "not visible" for a
# package that published fine, which reads as a failed release.
for s in scripts/verify-nuget-org-package-visible.sh \
         scripts/verify-nuget-org-packages-visible.sh \
         scripts/verify-nuget-org-registration-versions.sh; do
  f="${ROOT}/${s}"
  [[ -f "${f}" ]] || { bad "${s} exists" "not found"; continue; }
  if grep -q 'ASHLAR_NUGET_VERIFY_ALLOW_SHORT' "${f}" && grep -q 'ATTEMPTS.*-lt 40' "${f}"; then
    ok "$(basename "${s}") raises a short budget unless ALLOW_SHORT=1"
  else
    bad "$(basename "${s}") raises a short budget unless ALLOW_SHORT=1" "guard text not found"
  fi
done

echo
echo "passed: ${PASS}   failed: ${FAIL}"
[[ "${FAIL}" -eq 0 ]] || exit 1
