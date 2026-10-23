#!/usr/bin/env bash
# Poll nuget.org flat container until a package version is visible (handles index lag after push).
# Usage: ASHLAR_NUGET_VERIFY_PACKAGE_ID=Ashlar.Hosting.Bundle ASHLAR_NUGET_VERIFY_VERSION=1.2.3 bash scripts/verify-nuget-org-package-visible.sh
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ID="${ASHLAR_NUGET_VERIFY_PACKAGE_ID:-Ashlar.Hosting.Bundle}"
VER="${ASHLAR_NUGET_VERIFY_VERSION:?set ASHLAR_NUGET_VERIFY_VERSION (semver, no v prefix)}"
VER="${VER#v}"
ATTEMPTS="${ASHLAR_NUGET_VERIFY_ATTEMPTS:-80}"
SLEEP_SEC="${ASHLAR_NUGET_VERIFY_SLEEP_SEC:-15}"
[[ -z "${ATTEMPTS}" ]] && ATTEMPTS=80
[[ -z "${SLEEP_SEC}" ]] && SLEEP_SEC=15
if [[ "${ASHLAR_NUGET_VERIFY_ALLOW_SHORT:-}" != "1" ]] && [[ "${ATTEMPTS}" -lt 80 ]]; then
  echo "::notice::Raising nuget.org visibility poll ${ATTEMPTS} -> 80 (0.3.0-rc1 Ashlar.CLI index-lag timeout; was 40 after v0.1.2)"
  ATTEMPTS=80
fi

id_lc="$(echo "$ID" | tr '[:upper:]' '[:lower:]')"
ver_lc="$(echo "$VER" | tr '[:upper:]' '[:lower:]')"
url="https://api.nuget.org/v3-flatcontainer/${id_lc}/${ver_lc}/${id_lc}.${ver_lc}.nupkg"

echo "Checking nuget.org visibility: ${ID} ${VER}"
echo "URL: ${url}"

for i in $(seq 1 "${ATTEMPTS}"); do
  code="$(curl -sS -o /dev/null -w "%{http_code}" -I --max-time 30 "$url" || true)"
  if [[ "$code" == "200" ]]; then
    echo "verify-nuget-org-package-visible: OK (attempt ${i}/${ATTEMPTS})"
    exit 0
  fi
  echo "  attempt ${i}/${ATTEMPTS}: HTTP ${code} (waiting ${SLEEP_SEC}s)"
  if [[ "$i" -lt "${ATTEMPTS}" ]]; then
    sleep "${SLEEP_SEC}"
  fi
done

echo "::error::Package ${ID} version ${VER} not visible on nuget.org after ${ATTEMPTS} attempts (last HTTP ${code})."
exit 1
