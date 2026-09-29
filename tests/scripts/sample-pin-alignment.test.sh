#!/usr/bin/env bash
# The consumer sample restates package pins that must match the repository's, and nothing checked it.
#
# docs/samples/StableSdkHostSample/package-consumer/Directory.Build.props opts out of central package
# management and pins transitive packages by hand, with a comment reading "Align with repo
# Directory.Packages.props". That comment was the whole enforcement. When the repository bumped
# System.Text.Encodings.Web 10.0.11 -> 10.0.12, the sample kept 10.0.11, and the packed
# Ashlar.Hosting.Bundle then required >= 10.0.12 against a consumer pinning 10.0.11 - NU1605, the
# same downgrade error the bump itself was fixing, now in the one project that stands in for a
# stranger consuming our packages. It failed on three lanes at once (Linux, macOS, and the dedicated
# pack lane), which is what told us it was real and not a flake.
#
# Only packages pinned in BOTH files are compared. The sample also pins packages the repository does
# not list at all (OpenTelemetry.*, which reach it transitively); those are the sample's own business.
#
# Run:  bash tests/scripts/sample-pin-alignment.test.sh
# Pure bash: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
REPO_PINS="${ROOT}/Directory.Packages.props"
SAMPLE_PINS="${ROOT}/docs/samples/StableSdkHostSample/package-consumer/Directory.Build.props"

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

version_of() {
  # <file> <package> -> version, or empty when that file does not pin it
  sed -n "s|.*Include=\"$2\"[[:space:]]*Version=\"\([^\"]*\)\".*|\1|p" "$1" | head -1
}

for f in "${REPO_PINS}" "${SAMPLE_PINS}"; do
  [[ -f "${f}" ]] || { echo "FAIL - ${f} is missing; this file cannot compare anything."; exit 1; }
done

# Every package the sample pins by hand.
mapfile -t SAMPLE_PKGS < <(sed -n 's|.*<PackageReference Include="\([^"]*\)".*|\1|p' "${SAMPLE_PINS}" | sort -u)

if [[ "${#SAMPLE_PKGS[@]}" -eq 0 ]]; then
  echo "FAIL - no PackageReference found in ${SAMPLE_PINS}."
  echo "       An empty list would make every comparison below vacuous, so this is a failure"
  echo "       and not a clean run."
  exit 1
fi
echo "== the sample pins ${#SAMPLE_PKGS[@]} packages by hand =="

COMPARED=0
for pkg in "${SAMPLE_PKGS[@]}"; do
  repo_v="$(version_of "${REPO_PINS}" "${pkg}")"
  samp_v="$(version_of "${SAMPLE_PINS}" "${pkg}")"

  if [[ -z "${repo_v}" ]]; then
    echo "  --   ${pkg}: the repository does not pin this, so the sample owns it"
    continue
  fi

  COMPARED=$((COMPARED + 1))
  if [[ "${repo_v}" == "${samp_v}" ]]; then
    ok "${pkg} agrees (${repo_v})"
  else
    bad "${pkg} agrees" \
        "repo Directory.Packages.props pins ${repo_v}, the sample pins ${samp_v}.
         The sample stands in for a stranger consuming our packages: when our packages require
         >= ${repo_v} and the consumer pins ${samp_v}, restore fails with NU1605 and the
         packaging lanes go red. Set both to the same version."
  fi
done

# POSITIVE CONTROL. If the extraction breaks, or the repository stops pinning everything the sample
# restates, every comparison above is skipped and the loop reports a clean run having compared
# nothing - the precise shape this file exists to prevent elsewhere.
if [[ "${COMPARED}" -eq 0 ]]; then
  bad "at least one pin is actually compared" \
      "no package is pinned in both files, so nothing above was checked. Either the extraction
         pattern went stale or the sample stopped restating repository pins."
else
  ok "${COMPARED} pin(s) were actually compared, so a clean run means something"
fi

echo
echo "passed: ${PASS}   failed: ${FAIL}"
[[ "${FAIL}" -eq 0 ]] || exit 1
