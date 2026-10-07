#!/usr/bin/env bash
# Shared pre-flight guards for staging-only release triggers (sourced, not executed).
set -euo pipefail

_release_staging_root() {
  cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd
}

assert_gh_authenticated() {
  if ! command -v gh >/dev/null 2>&1; then
    echo "ABORT: GitHub CLI (gh) is not installed." >&2
    return 1
  fi
  if ! gh auth status >/dev/null 2>&1; then
    echo "ABORT: gh is not authenticated. Run: gh auth login" >&2
    return 1
  fi
  echo "ASSERT gh auth: OK"
}

normalize_semver() {
  local ver="${1#v}"
  ver="${ver#V}"
  printf '%s' "${ver}"
}

assert_valid_semver() {
  local ver
  ver="$(normalize_semver "$1")"
  if [[ ! "${ver}" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[a-zA-Z0-9.]+)?$ ]]; then
    echo "ABORT: VERSION is not valid semver: ${1} (normalized: ${ver})" >&2
    return 1
  fi
  echo "ASSERT semver format: OK (${ver})"
}

assert_version_matches_canonical() {
  local requested root canonical
  requested="$(normalize_semver "$1")"
  root="$(_release_staging_root)"

  if [[ ! -f "${root}/VERSION" ]]; then
    echo "ASSERT canonical version match: SKIPPED (no VERSION file; create one before a real staging cut)"
    return 0
  fi

  canonical="$(bash "${root}/scripts/resolve-canonical-package-version.sh" | tr -d '[:space:]')"

  if [[ -z "${canonical}" ]]; then
    echo "ABORT: VERSION file exists but is empty." >&2
    return 1
  fi

  if [[ "${requested}" != "${canonical}" ]]; then
    echo "ABORT: VERSION ${requested} does not match VERSION file (${canonical})." >&2
    echo "       Update VERSION at repo root or pass the canonical version to avoid release.yml tag/input mismatch." >&2
    return 1
  fi

  echo "ASSERT canonical version match: OK (${requested} == ${canonical})"
}

gh_repo_variable() {
  local name="$1"
  local err_file
  err_file="$(mktemp)"
  if ! gh variable get "${name}" 2>"${err_file}"; then
    if grep -q '403\|401\|not accessible' "${err_file}" 2>/dev/null; then
      rm -f "${err_file}"
      return 2
    fi
    rm -f "${err_file}"
    return 1
  fi
  rm -f "${err_file}"
}

gh_secret_present() {
  local name="$1" names err_file
  err_file="$(mktemp)"
  if ! names="$(gh secret list --json name -q '.[].name' 2>"${err_file}")"; then
    if grep -q '403\|401\|not accessible' "${err_file}" 2>/dev/null; then
      rm -f "${err_file}"
      return 2
    fi
    rm -f "${err_file}"
    return 1
  fi
  rm -f "${err_file}"
  echo "${names}" | grep -qx "${name}"
}

assert_nuget_org_push_skipped() {
  local mode rc=0
  if [[ -n "${RELEASE_STAGING_TEST_PUBLISH_MODE:-}" ]]; then
    mode="${RELEASE_STAGING_TEST_PUBLISH_MODE}"
  else
    set +e
    mode="$(gh_repo_variable NUGET_PUBLISH_MODE)"
    rc=$?
    set -e

    if [[ "${rc}" -eq 2 ]]; then
      if [[ "${RELEASE_STAGING_DRY_RUN:-0}" == "1" ]]; then
        echo "nuget.org push: SKIPPED — NUGET_PUBLISH_MODE unreadable (dry run; assuming unset)"
        return 0
      fi
      echo "ABORT: cannot read NUGET_PUBLISH_MODE (gh token lacks Actions variables permission)." >&2
      return 1
    fi
  fi

  mode="$(echo "${mode}" | tr '[:upper:]' '[:lower:]' | tr -d '[:space:]')"

  case "${mode}" in
    oidc|apikey)
      echo "ABORT: NUGET_PUBLISH_MODE=${mode} would enable nuget.org push." >&2
      echo "       Staging-only release refused. Unset NUGET_PUBLISH_MODE or set it to 'none' before using release-staging." >&2
      return 1
      ;;
    none)
      echo "nuget.org push: SKIPPED — NUGET_PUBLISH_MODE=none"
      ;;
    '')
      echo "nuget.org push: SKIPPED — NUGET_PUBLISH_MODE unset"
      ;;
    *)
      echo "nuget.org push: SKIPPED — NUGET_PUBLISH_MODE=${mode} (not oidc/apikey)"
      ;;
  esac
}

assert_staging_configured() {
  local feed_url rc=0 secret_rc=0
  set +e
  feed_url="$(gh_repo_variable NUGET_STAGING_FEED_URL)"
  rc=$?
  set -e
  feed_url="$(echo "${feed_url}" | tr -d '[:space:]')"

  if [[ "${rc}" -eq 2 ]]; then
    if [[ "${RELEASE_STAGING_DRY_RUN:-0}" == "1" ]]; then
      echo "ASSERT staging feed: SKIP (gh cannot read repository variables in this environment)"
      echo "ASSERT staging push secret: SKIP (gh cannot read repository secrets in this environment)"
      return 0
    fi
    echo "ABORT: cannot read NUGET_STAGING_FEED_URL (gh token lacks Actions variables permission)." >&2
    return 1
  fi

  if [[ -z "${feed_url}" ]]; then
    echo "ABORT: NUGET_STAGING_FEED_URL repository variable is not set (see docs/StagingFeed.md)." >&2
    return 1
  fi

  set +e
  gh_secret_present NUGET_STAGING_API_KEY
  secret_rc=$?
  set -e

  if [[ "${secret_rc}" -eq 2 ]]; then
    if [[ "${RELEASE_STAGING_DRY_RUN:-0}" == "1" ]]; then
      echo "ASSERT staging feed: OK (${feed_url})"
      echo "ASSERT staging push secret: SKIP (gh cannot read repository secrets in this environment)"
      return 0
    fi
    echo "ABORT: cannot read repository secrets (gh token lacks Actions secrets permission)." >&2
    return 1
  fi

  if [[ "${secret_rc}" -ne 0 ]]; then
    echo "ABORT: NUGET_STAGING_API_KEY secret is not configured (required when NUGET_STAGING_FEED_URL is set)." >&2
    return 1
  fi

  echo "ASSERT staging feed: OK (${feed_url})"
  echo "ASSERT staging push secret: OK (NUGET_STAGING_API_KEY present)"
}

# The base version, with any prerelease or build suffix removed: 0.2.0-rc1 -> 0.2.0.
base_semver() {
  local ver
  ver="$(normalize_semver "$1")"
  ver="${ver%%-*}"
  ver="${ver%%+*}"
  printf '%s' "${ver}"
}

# Refuse a workflow_dispatch release whose version is a DIFFERENT release from the root VERSION.
#
# release.yml guarded tag pushes, and dispatches made from a tag, and nothing else. Dispatch from a
# BRANCH satisfied neither condition and still publishes to nuget.org. Directory.Build.targets
# stamps Version/AssemblyVersion/FileVersion from VERSION, while the nuget job takes its package
# version from the input, and `-p:PackageVersion=` overrides PackageVersion but not Version - so an
# unguarded dispatch ships nupkgs whose package version disagrees with the assemblies inside them.
#
# A PRERELEASE of the canonical version is allowed: cutting 0.2.0-rc1 from a branch while VERSION
# reads 0.2.0 is the staging flow the input exists for. A different release is not.
assert_dispatch_version_allowed() {
  local requested base canonical root
  requested="$(normalize_semver "$1")"
  base="$(base_semver "$1")"
  root="$(_release_staging_root)"

  canonical="$(bash "${root}/scripts/resolve-canonical-package-version.sh" 2>/dev/null | tr -d '[:space:]')"
  if [[ -z "${canonical}" ]]; then
    echo "ABORT: could not resolve the canonical version from VERSION; refusing to publish." >&2
    return 1
  fi

  if [[ "${base}" != "${canonical}" ]]; then
    echo "ABORT: dispatch version ${requested} is not ${canonical} nor a prerelease of it." >&2
    echo "       Assemblies are stamped from VERSION, so this publishes packages whose version" >&2
    echo "       disagrees with their assemblies. Bump VERSION, or dispatch ${canonical}." >&2
    return 1
  fi

  echo "ASSERT dispatch version: OK (${requested} is ${canonical} or a prerelease of it)"
}

# Refuse a release whose consumer pin still names the PREVIOUS release.
#
# ci/published-version is what consumer-template/Directory.Packages.props and CONSUMING.md pin a
# stranger to. Nothing in the release path writes it, so after a release it kept naming the version
# before - and the existing lint only checks the file parses, not that it is current. A stranger
# copying the template would install one release behind, silently, forever.
#
# Enforced rather than automated on purpose: the release commit is where a human states what is
# being shipped, and a workflow that rewrites the repository mid-publish is a worse hazard than a
# guard that refuses.
assert_consumer_pin_matches() {
  local want root pinned
  want="$(base_semver "$1")"
  root="$(_release_staging_root)"

  if [[ ! -f "${root}/ci/published-version" ]]; then
    echo "ABORT: ci/published-version is missing; consumers have no version to pin." >&2
    return 1
  fi

  pinned="$(tr -d '[:space:]' < "${root}/ci/published-version")"
  if [[ "${pinned}" != "${want}" ]]; then
    echo "ABORT: ci/published-version is ${pinned} but this release publishes ${want}." >&2
    echo "       consumer-template pins ${pinned}, so a stranger copying it never receives this" >&2
    echo "       release. Set ci/published-version to ${want} on the release commit." >&2
    return 1
  fi

  echo "ASSERT consumer pin: OK (${want})"
}
