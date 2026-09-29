#!/usr/bin/env bash
# Poll nuget.org flat container until all listed package versions return HTTP 200 (index lag after push).
#
# ASHLAR_NUGET_VERIFY_VERSION — required semver (no v prefix)
# ASHLAR_NUGET_VERIFY_PACKAGE_IDS — comma-separated ids; overrides everything below
# ASHLAR_MANIFEST_JSON — when set, poll EVERY id in the release manifest (the default in CI)
# ASHLAR_NUGET_VERIFY_PACKAGE_ID — if set and ASHLAR_NUGET_VERIFY_PACKAGE_IDS unset, a single id (backward compat)
# ASHLAR_NUGET_VERIFY_ATTEMPTS / ASHLAR_NUGET_VERIFY_SLEEP_SEC — optional (defaults 40 / 15)
# v0.1.2: 12×15s timed out after every nupkg was pushed; nuget.org listed them later.
# ASHLAR_NUGET_VERIFY_ALLOW_SHORT=1 keeps an explicit short budget (local negative tests).
set -euo pipefail
VER="${ASHLAR_NUGET_VERIFY_VERSION:?set ASHLAR_NUGET_VERIFY_VERSION (semver, no v prefix)}"
VER="${VER#v}"
ATTEMPTS="${ASHLAR_NUGET_VERIFY_ATTEMPTS:-40}"
SLEEP_SEC="${ASHLAR_NUGET_VERIFY_SLEEP_SEC:-15}"
# GitHub Actions may pass empty strings for unset repo variables; treat as defaults.
[[ -z "${ATTEMPTS}" ]] && ATTEMPTS=40
[[ -z "${SLEEP_SEC}" ]] && SLEEP_SEC=15
if [[ "${ASHLAR_NUGET_VERIFY_ALLOW_SHORT:-}" != "1" ]] && [[ "${ATTEMPTS}" -lt 40 ]]; then
  echo "::notice::Raising nuget.org visibility poll ${ATTEMPTS} -> 40 (v0.1.2 index-lag timeout)"
  ATTEMPTS=40
fi

# GitHub Actions may set ASHLAR_NUGET_VERIFY_PACKAGE_IDS to empty when the repo var is unset; treat as default.
if [[ -n "${ASHLAR_NUGET_VERIFY_PACKAGE_IDS:-}" && "${ASHLAR_NUGET_VERIFY_PACKAGE_IDS}" != "" ]]; then
  mapfile -t IDS < <(echo "${ASHLAR_NUGET_VERIFY_PACKAGE_IDS}" | tr ',' '\n' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//' | grep -v '^$' || true)
elif [[ -n "${ASHLAR_NUGET_VERIFY_PACKAGE_ID:-}" ]]; then
  IDS=("${ASHLAR_NUGET_VERIFY_PACKAGE_ID}")
elif [[ -n "${ASHLAR_MANIFEST_JSON:-}" && -f "${ASHLAR_MANIFEST_JSON}" ]]; then
  # Every package this release pushed, not a sample of them. Polling four ids and then letting the
  # next step download all ~22 is how the rest were still 404 on index lag when they were fetched -
  # the failure that ended the v0.1.2 release after its packages were already public.
  mapfile -t IDS < <(python3 -c 'import json,sys
for r in json.load(open(sys.argv[1], encoding="utf-8")): print(r["id"])' "${ASHLAR_MANIFEST_JSON}" | tr -d '\r')
else
  # Last resort, for a local run with no manifest: the four packages a stranger restores first.
  IDS=(Ashlar.Hosting.Bundle Ashlar.Hosting Ashlar.Sdk Ashlar.CLI)
fi

if [[ "${#IDS[@]}" -eq 0 ]]; then
  echo "::error::No package ids to verify (set ASHLAR_NUGET_VERIFY_PACKAGE_IDS or ASHLAR_NUGET_VERIFY_PACKAGE_ID)."
  exit 1
fi

echo "Checking nuget.org visibility for version ${VER}: ${IDS[*]}"
echo "Attempts=${ATTEMPTS} sleep=${SLEEP_SEC}s"

http_head() {
  local url="$1"
  curl -sS -o /dev/null -w "%{http_code}" -I --max-time 30 "$url" || echo "000"
}

for i in $(seq 1 "${ATTEMPTS}"); do
  all_ok=1
  for ID in "${IDS[@]}"; do
    [[ -z "$ID" ]] && continue
    id_lc="$(echo "$ID" | tr '[:upper:]' '[:lower:]')"
    ver_lc="$(echo "$VER" | tr '[:upper:]' '[:lower:]')"
    url="https://api.nuget.org/v3-flatcontainer/${id_lc}/${ver_lc}/${id_lc}.${ver_lc}.nupkg"
    code="$(http_head "$url")"
    if [[ "$code" != "200" ]]; then
      all_ok=0
      echo "  attempt ${i}/${ATTEMPTS}: ${ID} HTTP ${code} (${url})"
    fi
  done
  if [[ "$all_ok" -eq 1 ]]; then
    echo "verify-nuget-org-packages-visible: OK (attempt ${i}/${ATTEMPTS})"
    exit 0
  fi
  if [[ "$i" -lt "${ATTEMPTS}" ]]; then
    sleep "${SLEEP_SEC}"
  fi
done

echo "::error::One or more packages not visible on nuget.org for version ${VER} after ${ATTEMPTS} attempts."
exit 1
