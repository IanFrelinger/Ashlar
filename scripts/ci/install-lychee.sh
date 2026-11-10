#!/usr/bin/env bash
# Pin both release and digest. Retry installation only, never a failing link check.
set -euo pipefail
: "${RUNNER_TEMP:?RUNNER_TEMP is required}"
: "${GITHUB_PATH:?GITHUB_PATH is required}"
if [[ "$(uname -s)" != Linux || "$(uname -m)" != x86_64 ]]; then
  echo "The pinned lychee asset requires Linux x86_64." >&2
  exit 1
fi
VERSION=v0.24.2
ASSET=lychee-x86_64-unknown-linux-gnu.tar.gz
# Digest published on https://github.com/lycheeverse/lychee/releases/tag/lychee-v0.24.2
SHA256=1f4e0ef7f6554a6ed33dd7ac144fb2e1bbed98598e7af973042fc5cd43951c9a
DOWNLOAD_DIR="$(mktemp -d "${RUNNER_TEMP}/lychee-download.XXXXXX")"
trap 'rm -rf "$DOWNLOAD_DIR"' EXIT
URL="https://github.com/lycheeverse/lychee/releases/download/lychee-${VERSION}/${ASSET}"
echo "Downloading ${URL}"
# --show-error exposes the HTTP/TLS failure hidden by the action's curl -sfLO.
# Retry HTTP failures (22) and TLS failures (35), with bounded backoff and timeout.
if ! curl --fail --show-error --silent --location \
  --retry 5 --retry-all-errors --retry-delay 2 --retry-max-time 180 \
  --connect-timeout 15 --max-time 60 \
  --output "${DOWNLOAD_DIR}/${ASSET}" "${URL}"; then
  # GitHub's release page can return persistent 504s while its asset API works.
  # This is the SAME release asset, not a mirror or a different binary version.
  # Public assets need no token; do not forward repository credentials.
  echo "Release URL failed; trying the pinned GitHub release-asset API." >&2
  rm -f "${DOWNLOAD_DIR}/${ASSET}"
  curl --fail --show-error --silent --location \
    --header 'Accept: application/octet-stream' \
    --retry 2 --retry-all-errors --retry-delay 2 --retry-max-time 90 \
    --connect-timeout 15 --max-time 60 \
    --output "${DOWNLOAD_DIR}/${ASSET}" \
    https://api.github.com/repos/lycheeverse/lychee/releases/assets/409959156
fi
(
  cd "${DOWNLOAD_DIR}"
  printf '%s  %s\n' "${SHA256}" "${ASSET}" | sha256sum --check --strict -
  tar -xzf "${ASSET}" --strip-components=1 "lychee-x86_64-unknown-linux-gnu/lychee"
)
install -D "${DOWNLOAD_DIR}/lychee" "${RUNNER_TEMP}/lychee/bin/lychee"
echo "${RUNNER_TEMP}/lychee/bin" >> "${GITHUB_PATH}"
