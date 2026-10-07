#!/usr/bin/env bash
# Consumer samples restate package pins that must match the repository's, and nothing checked them.
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
# THREE SAMPLES, NOT ONE. That fix covered StableSdkHostSample and left the two nuget.org restore
# samples on 10.0.11, where the same bump re-armed the same NU1605 - and those two are worse, because
# they run AFTER `dotnet nuget push` in reusable-release-nuget.yml against the version being
# released. A release would have gone red with all 22 packages already public, which nuget.org
# permits unlisting but not deleting. They are checked here because nothing else runs them before a
# tag: their own workflow only path-filters on them, and they are never exercised on master.
#
# Only packages pinned in BOTH a sample and the repository are compared. A sample also pins packages
# the repository does not list at all (OpenTelemetry.*, which reach it transitively); those are the
# sample's own business.
#
# Run:  bash tests/scripts/sample-pin-alignment.test.sh
# Pure bash: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
REPO_PINS="${ROOT}/Directory.Packages.props"

# Every sample that opts out of central package management and restates repository pins by hand.
SAMPLE_PINS=(
  "${ROOT}/docs/samples/StableSdkHostSample/package-consumer/Directory.Build.props"
  "${ROOT}/docs/samples/NugetOrgRestoreVerify/Directory.Build.props"
  "${ROOT}/docs/samples/NugetOrgRestoreHostingOnly/Directory.Build.props"
)

PASS=0
FAIL=0
ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

version_of() {
  # <file> <package> -> version, or empty when that file does not pin it
  sed -n "s|.*Include=\"$2\"[[:space:]]*Version=\"\([^\"]*\)\".*|\1|p" "$1" | head -1
}

[[ -f "${REPO_PINS}" ]] || { echo "FAIL - ${REPO_PINS} is missing; nothing can be compared."; exit 1; }
for f in "${SAMPLE_PINS[@]}"; do
  [[ -f "${f}" ]] || { echo "FAIL - ${f} is missing; this file cannot compare anything."; exit 1; }
done

TOTAL_COMPARED=0

for SAMPLE in "${SAMPLE_PINS[@]}"; do
  rel="${SAMPLE#"${ROOT}/"}"
  mapfile -t SAMPLE_PKGS < <(sed -n 's|.*<PackageReference Include="\([^"]*\)".*|\1|p' "${SAMPLE}" | sort -u)

  if [[ "${#SAMPLE_PKGS[@]}" -eq 0 ]]; then
    echo "FAIL - no PackageReference found in ${rel}."
    echo "       An empty list would make every comparison below vacuous, so this is a failure"
    echo "       and not a clean run."
    exit 1
  fi

  echo "== ${rel} pins ${#SAMPLE_PKGS[@]} package(s) by hand =="

  for pkg in "${SAMPLE_PKGS[@]}"; do
    repo_v="$(version_of "${REPO_PINS}" "${pkg}")"
    samp_v="$(version_of "${SAMPLE}" "${pkg}")"

    if [[ -z "${repo_v}" ]]; then
      echo "  --   ${pkg}: the repository does not pin this, so the sample owns it"
      continue
    fi

    TOTAL_COMPARED=$((TOTAL_COMPARED + 1))
    if [[ "${repo_v}" == "${samp_v}" ]]; then
      ok "${pkg} agrees (${repo_v})"
    else
      bad "${pkg} agrees in ${rel}" \
          "repo Directory.Packages.props pins ${repo_v}, the sample pins ${samp_v}.
         The sample stands in for a stranger consuming our packages: when our packages require
         >= ${repo_v} and the consumer pins ${samp_v}, restore fails with NU1605 and the
         packaging lanes go red. Set both to the same version."
    fi
  done
  echo
done

# POSITIVE CONTROL. If the extraction breaks, or the repository stops pinning everything the samples
# restate, every comparison above is skipped and the loop reports a clean run having compared
# nothing - the precise shape this file exists to prevent elsewhere.
if [[ "${TOTAL_COMPARED}" -eq 0 ]]; then
  bad "at least one pin is actually compared" \
      "no package is pinned in both a sample and the repository, so nothing above was checked.
         Either the extraction pattern went stale or the samples stopped restating repository pins."
else
  ok "${TOTAL_COMPARED} pin(s) were actually compared across ${#SAMPLE_PINS[@]} sample(s)"
fi

echo
echo "passed: ${PASS}   failed: ${FAIL}"
[[ "${FAIL}" -eq 0 ]] || exit 1
