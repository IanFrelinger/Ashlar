#!/usr/bin/env bash
# Poll nuget.org registration5-gz-semver2 until each package lists VERSION.
# Env: ASHLAR_NUGET_VERIFY_VERSION (required), ASHLAR_NUGET_VERIFY_PACKAGE_IDS (comma; default Bundle/Hosting/Sdk/CLI),
#      ASHLAR_NUGET_VERIFY_ATTEMPTS, ASHLAR_NUGET_VERIFY_SLEEP_SEC
set -euo pipefail
VER="${ASHLAR_NUGET_VERIFY_VERSION:?set ASHLAR_NUGET_VERIFY_VERSION}"
VER="${VER#v}"
ATTEMPTS="${ASHLAR_NUGET_VERIFY_ATTEMPTS:-80}"
SLEEP_SEC="${ASHLAR_NUGET_VERIFY_SLEEP_SEC:-15}"
[[ -z "${ATTEMPTS}" ]] && ATTEMPTS=80
[[ -z "${SLEEP_SEC}" ]] && SLEEP_SEC=15
if [[ "${ASHLAR_NUGET_VERIFY_ALLOW_SHORT:-}" != "1" ]] && [[ "${ATTEMPTS}" -lt 80 ]]; then
  echo "::notice::Raising nuget.org registration poll ${ATTEMPTS} -> 80 (0.3.0-rc1 Ashlar.CLI index-lag timeout; was 40 after v0.1.2)"
  ATTEMPTS=80
fi

if [[ -n "${ASHLAR_NUGET_VERIFY_PACKAGE_IDS:-}" && "${ASHLAR_NUGET_VERIFY_PACKAGE_IDS}" != "" ]]; then
  mapfile -t IDS < <(echo "${ASHLAR_NUGET_VERIFY_PACKAGE_IDS}" | tr ',' '\n' | sed 's/^[[:space:]]*//;s/[[:space:]]*$//' | grep -v '^$' || true)
else
  IDS=(Ashlar.Hosting.Bundle Ashlar.Hosting Ashlar.Sdk Ashlar.CLI)
fi

reg_has_version() {
  python3 -c "
import json, sys, urllib.request

pid, want = sys.argv[1], sys.argv[2]
url = f'https://api.nuget.org/v3/registration5-gz-semver2/{pid.lower()}/index.json'
try:
    raw = urllib.request.urlopen(url, timeout=60).read()
except Exception:
    sys.exit(1)
# The registration5-gz-semver2 blobs are stored gzip-compressed and served with
# Content-Encoding: gzip regardless of Accept-Encoding; urllib does not
# decompress. Verified on the first real v0.1.1 publish: without this, every
# poll reads as 'not listed' forever while nuget.org has the version indexed.
try:
    import gzip
    if raw[:2] == bytes([31, 139]):
        raw = gzip.decompress(raw)
    data = json.loads(raw.decode('utf-8'))
except Exception:
    sys.exit(1)
versions = set()

def walk(o):
    if isinstance(o, dict):
        ce = o.get('catalogEntry')
        if isinstance(ce, dict) and 'version' in ce:
            versions.add(ce['version'])
        for v in o.values():
            walk(v)
    elif isinstance(o, list):
        for v in o:
            walk(v)

walk(data)
sys.exit(0 if want in versions else 1)
" "$1" "$2"
}

echo "Registration API: waiting for ${VER} on: ${IDS[*]}"

for i in $(seq 1 "${ATTEMPTS}"); do
  ok=1
  for ID in "${IDS[@]}"; do
    [[ -z "$ID" ]] && continue
    if ! reg_has_version "$ID" "$VER"; then
      ok=0
      echo "  attempt ${i}/${ATTEMPTS}: ${ID} — version ${VER} not in registration yet"
    fi
  done
  if [[ "$ok" -eq 1 ]]; then
    echo "verify-nuget-org-registration-versions: OK (attempt ${i}/${ATTEMPTS})"
    exit 0
  fi
  [[ "$i" -lt "${ATTEMPTS}" ]] && sleep "${SLEEP_SEC}"
done

echo "::error::Registration API did not list ${VER} for all packages after ${ATTEMPTS} attempts."
exit 1
