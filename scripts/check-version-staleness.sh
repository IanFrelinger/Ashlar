#!/usr/bin/env bash
# Warn when the root VERSION file has fallen behind the repository.
#
# THE GAP THIS CLOSES. release.yml asserts that a tag name matches root VERSION, but only at tag
# time and only when github.ref_type == 'tag'. By then the tag exists and the guard's only move is
# to refuse the release. Nothing warned earlier. docs/release/RELEASE-READINESS.md section 5.4b
# recorded that as an open hole; this is the thing that fills it.
#
# ADVISORY BY DESIGN: this always exits 0. A stale VERSION is a judgement call - a repository can
# sit many commits past a tag perfectly legitimately between releases - and a check that reddens a
# required lane on a judgement call is how required lanes get muted in this repository
# (docs/HowGatesGoQuiet.md). It annotates; it does not block.
#
# Usage: bash scripts/check-version-staleness.sh
#   ASHLAR_STALENESS_COMMITS  commits past the tag before it warns (default 25)
#   ASHLAR_SKIP_NUGET_CHECK   set to 1 to skip the nuget.org lookup (offline/air-gapped)

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "${ROOT}"

THRESHOLD="${ASHLAR_STALENESS_COMMITS:-25}"
CANONICAL_PACKAGE="ashlar.certification.contracts"

# GitHub annotations when running in Actions, plain text otherwise.
if [[ -n "${GITHUB_ACTIONS:-}" ]]; then
  warn() { echo "::warning title=VERSION staleness::$*"; }
  note() { echo "::notice title=VERSION::$*"; }
else
  warn() { echo "WARNING: $*"; }
  note() { echo "$*"; }
fi

if [[ ! -f VERSION ]]; then
  warn "No root VERSION file. Nothing to compare."
  exit 0
fi

VERSION="$(tr -d '[:space:]' < VERSION)"
echo "root VERSION: ${VERSION}"

# --- 1. how far has the tree moved past the newest release tag? ------------------------------
# --tags so lightweight tags count; the release tags in this repo are not all annotated.
LATEST_TAG="$(git describe --tags --abbrev=0 --match 'v[0-9]*' 2>/dev/null || true)"

if [[ -z "${LATEST_TAG}" ]]; then
  note "No v* tag is reachable from HEAD - nothing to measure staleness against. (A shallow clone reports this too: CI checkouts default to depth 1 and need fetch-depth: 0 plus tags for this check to mean anything.)"
else
  AHEAD="$(git rev-list --count "${LATEST_TAG}..HEAD" 2>/dev/null || echo 0)"
  echo "latest tag:   ${LATEST_TAG}  (HEAD is ${AHEAD} commit(s) ahead)"

  if [[ "v${VERSION}" == "${LATEST_TAG}" && "${AHEAD}" -gt "${THRESHOLD}" ]]; then
    warn "VERSION is ${VERSION} and the newest tag is ${LATEST_TAG}, but HEAD is ${AHEAD} commits past it. The version string no longer identifies this tree's content. Bump VERSION and land it BEFORE cutting the next tag - release.yml will otherwise refuse the tag after you have created it."
  elif [[ "v${VERSION}" == "${LATEST_TAG}" ]]; then
    note "VERSION matches the newest tag and the tree has moved ${AHEAD} commit(s) - under the ${THRESHOLD}-commit threshold."
  else
    note "VERSION (${VERSION}) is already ahead of the newest tag (${LATEST_TAG}) - bumped and awaiting a tag."
  fi
fi

# --- 2. is this version already published? ----------------------------------------------------
# The sharper signal: building packages labelled with a version that is already live means the
# artefacts on disk and the artefacts on nuget.org disagree while sharing a name.
if [[ "${ASHLAR_SKIP_NUGET_CHECK:-0}" != "1" ]] && command -v curl >/dev/null 2>&1; then
  URL="https://api.nuget.org/v3-flatcontainer/${CANONICAL_PACKAGE}/index.json"
  PUBLISHED="$(curl -sf --max-time 15 "${URL}" 2>/dev/null || true)"
  if [[ -z "${PUBLISHED}" ]]; then
    note "Could not reach nuget.org to check whether ${VERSION} is published (offline, rate-limited, or the package is unlisted). Skipping that half."
  elif grep -qF "\"${VERSION}\"" <<<"${PUBLISHED}"; then
    warn "${CANONICAL_PACKAGE} ${VERSION} is ALREADY PUBLISHED on nuget.org. Packing from this tree would produce different bytes under a version string that is already taken. Bump VERSION."
  else
    note "${CANONICAL_PACKAGE} ${VERSION} is not yet on nuget.org - safe to publish under this version."
  fi
fi

exit 0
