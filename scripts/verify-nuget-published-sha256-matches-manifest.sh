#!/usr/bin/env bash
# Assert that what nuget.org SERVES is the package we packed.
#
# WHAT CHANGED AND WHY. This compared the raw SHA-256 of the downloaded .nupkg against the hash
# taken before `dotnet nuget push`. Those bytes are never equal: nuget.org repository-signs on
# publish, appending a `.signature.p7s` entry. Measured on the real feed - ashlar.sdk 0.1.2 as
# served carries that entry and hashes to eb2111dc..., not to the pushed file's hash. So the check
# could only ever fail, and it runs AFTER the push, when the packages are already public and
# nuget.org allows unlisting but not deletion. It had never actually executed: the v0.1.2 release
# (run 33867752688) died one step earlier on flat-container index lag, which is why a check that
# cannot pass sat here looking green.
#
# It now compares the package CONTENT - every entry except the signature, by name and uncompressed
# bytes (scripts/lib/nupkg_content_digest.py). That is the part publication must not alter, and the
# part a consumer restores. The signature's presence is asserted separately and positively, so
# excluding it from the digest does not lose the fact that it must be there.
#
# Env: ASHLAR_MANIFEST_JSON (path), ASHLAR_NUGET_VERIFY_VERSION (must match manifest entries)
set -euo pipefail
MAN="${ASHLAR_MANIFEST_JSON:?set ASHLAR_MANIFEST_JSON}"
VER="${ASHLAR_NUGET_VERIFY_VERSION:?set ASHLAR_NUGET_VERIFY_VERSION}"
VER="${VER#v}"
WORKDIR="$(mktemp -d)"
trap 'rm -rf "${WORKDIR}"' EXIT
LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/lib" && pwd)"

python3 - "$MAN" "$VER" "$WORKDIR" "$LIB_DIR" <<'PY'
import json, os, sys, urllib.request

manifest_path, ver, workdir, lib_dir = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
sys.path.insert(0, lib_dir)
from nupkg_content_digest import content_digest, has_signature

with open(manifest_path, encoding="utf-8") as f:
    rows = json.load(f)
os.makedirs(workdir, exist_ok=True)

checked = 0
failures = 0
for row in rows:
    pid, v = row["id"], row["version"]
    if v != ver:
        print(f"skip {pid} version mismatch manifest {v} vs {ver}", file=sys.stderr)
        continue
    expected = row.get("contentSha256")
    if not expected:
        print(
            f"::error::manifest row for {pid} {v} has no contentSha256. It was written by an older "
            f"render-nuget-release-manifest.sh, which recorded only the pre-push file hash - a value "
            f"the served package can never match. Re-render the manifest.",
            file=sys.stderr,
        )
        failures += 1
        continue

    id_lc, v_lc = pid.lower(), v.lower()
    url = f"https://api.nuget.org/v3-flatcontainer/{id_lc}/{v_lc}/{id_lc}.{v_lc}.nupkg"
    out = os.path.join(workdir, row["fileName"])
    print(f"download {url}")
    urllib.request.urlretrieve(url, out)

    got = content_digest(out)
    if got.lower() != expected.lower():
        print(
            f"::error::content mismatch for {pid} {v}: the package nuget.org serves does not "
            f"contain the bytes we packed (expected {expected}, got {got}). This compares every "
            f"archive entry except the repository signature, so it is NOT explained by signing.",
            file=sys.stderr,
        )
        failures += 1
        continue

    if not has_signature(out):
        print(
            f"::error::{pid} {v} came back without a .signature.p7s entry. nuget.org repository-signs "
            f"on publish, so a package served unsigned means it did not come through the normal "
            f"publication path.",
            file=sys.stderr,
        )
        failures += 1
        continue

    checked += 1

# POSITIVE CONTROL: a manifest that parsed to nothing, or whose every row was skipped on a version
# mismatch, would reach this point having verified nothing at all.
if checked == 0 and failures == 0:
    print(
        f"::error::verified 0 packages for version {ver}. The manifest has {len(rows)} row(s), so "
        f"either it is empty or every row was skipped on a version mismatch. This check passed "
        f"without inspecting anything.",
        file=sys.stderr,
    )
    sys.exit(1)

if failures:
    print(f"verify-nuget-published-sha256-matches-manifest: {failures} failure(s)", file=sys.stderr)
    sys.exit(1)

print(f"verify-nuget-published-sha256-matches-manifest: OK ({checked} package(s) match, all signed)")
PY
