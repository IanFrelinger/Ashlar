#!/usr/bin/env bash
# C6: docs that name a published package version must key off ci/published-version,
# never the repo VERSION file (which may already have been bumped for an unpublished release).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
PUBLISHED="$(tr -d '[:space:]' < "${ROOT}/ci/published-version")"
VERSION="$(tr -d '[:space:]' < "${ROOT}/VERSION")"

if [[ -z "${PUBLISHED}" ]]; then
  echo "C6: ci/published-version is empty" >&2
  exit 1
fi

fail=0

# "from VERSION" (with or without backticks) is the exact lie C6 forbids:
# it treats the unpublished repo pin as the public feed version.
while IFS= read -r -d '' file; do
  if grep -nE 'from[[:space:]]+`?VERSION`?' "${file}" | grep -viE 'never|not |must not|do not|forbid' >/dev/null; then
    echo "C6: ${file} cites VERSION as a published pin; key off ci/published-version (${PUBLISHED})" >&2
    grep -nE 'from[[:space:]]+`?VERSION`?' "${file}" >&2 || true
    fail=1
  fi
done < <(find "${ROOT}/docs" "${ROOT}/consumer-template" -type f \( -name '*.md' -o -name '*.txt' \) -print0)

if [[ "${VERSION}" != "${PUBLISHED}" ]]; then
  # Repo has been bumped ahead of the feed. Docs must not advertise VERSION as shipped.
  while IFS= read -r -d '' file; do
    if grep -nE "published[[:space:]]+(on nuget|to nuget|version).*${VERSION}|nuget.org.*${VERSION}" "${file}" >/dev/null; then
      echo "C6: ${file} advertises unpublished VERSION ${VERSION} as if it were on the feed (published is ${PUBLISHED})" >&2
      fail=1
    fi
  done < <(find "${ROOT}/docs" "${ROOT}/consumer-template" -type f -name '*.md' -print0)
fi

# The consumer template is what a stranger copies. Its pin is a hand-maintained literal, and
# nothing asserted it tracks ci/published-version - only that ci/published-version matches the
# release being tagged (assert_consumer_pin_matches, at tag time). Those are different claims: a
# release commit can bump ci/published-version, satisfy that guard, publish green, and leave the
# template pinning the PREVIOUS version, which is the exact outcome that guard exists to prevent.
TEMPLATE_PROPS="${ROOT}/consumer-template/Directory.Packages.props"
if [[ ! -f "${TEMPLATE_PROPS}" ]]; then
  echo "C6: consumer-template/Directory.Packages.props is missing; the template pin is unchecked" >&2
  fail=1
else
  # grep -o exits 1 when it matches nothing. Without `|| true`, `set -e` kills the script right
  # here and the branch below never gets to say WHY the pin could not be read - a rename would
  # fail the gate with no message, which is the one thing these guards are written not to do.
  TEMPLATE_PIN="$(grep -o '<AshlarConsumerPackageVersion>[^<]*' "${TEMPLATE_PROPS}" | head -1 | cut -d'>' -f2 | tr -d '[:space:]' || true)"
  if [[ -z "${TEMPLATE_PIN}" ]]; then
    echo "C6: could not read <AshlarConsumerPackageVersion> from consumer-template/Directory.Packages.props." >&2
    echo "    The element was renamed or removed, so this check is inspecting nothing." >&2
    fail=1
  elif [[ "${TEMPLATE_PIN}" != "${PUBLISHED}" ]]; then
    echo "C6: consumer-template pins ${TEMPLATE_PIN} but ci/published-version is ${PUBLISHED}." >&2
    echo "    Every Ashlar.* PackageVersion in that file flows from AshlarConsumerPackageVersion, so a" >&2
    echo "    stranger copying the template installs ${TEMPLATE_PIN}, not the published release." >&2
    echo "    Bump both on the release commit." >&2
    fail=1
  fi
fi

if [[ "${fail}" -ne 0 ]]; then
  exit 1
fi

echo "C6: docs published-version lint ok (published=${PUBLISHED}, repo VERSION=${VERSION}, template=${TEMPLATE_PIN})"
