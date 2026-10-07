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
import json, os, sys, time, urllib.error, urllib.request

manifest_path, ver, workdir, lib_dir = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
sys.path.insert(0, lib_dir)
from nupkg_content_digest import content_digest, has_signature

with open(manifest_path, encoding="utf-8") as f:
    rows = json.load(f)
os.makedirs(workdir, exist_ok=True)

# nuget.org indexes packages one at a time after a push, so a package pushed seconds ago can
# still 404 from the flat container. The visibility poll that runs before this step waits for a
# handful of ids, not all of them, so the rest can legitimately be missing on the first request.
# That is exactly the lag that failed v0.1.2's release. A bare urlretrieve raised HTTPError on the
# first such 404 and ended the step with a traceback - after every package was already public.
ATTEMPTS = int(os.environ.get("ASHLAR_NUGET_VERIFY_ATTEMPTS") or 40)
SLEEP_SEC = float(os.environ.get("ASHLAR_NUGET_VERIFY_SLEEP_SEC") or 15)
REQUEST_TIMEOUT_SEC = 60
# Overridable so the retry path can be exercised against a local server, and so a staging feed can
# be verified with the same code that verifies nuget.org rather than a copy of it.
FLAT_CONTAINER = (os.environ.get("ASHLAR_NUGET_FLAT_CONTAINER") or "https://api.nuget.org/v3-flatcontainer").rstrip("/")
RETRYABLE_STATUS = {404, 408, 429, 500, 502, 503, 504}


def fetch(url, dest):
    """Download url to dest, riding out index lag. Returns None on success, else a reason."""
    last = "no attempt made"
    for attempt in range(1, ATTEMPTS + 1):
        try:
            with urllib.request.urlopen(url, timeout=REQUEST_TIMEOUT_SEC) as resp:
                data = resp.read()
            with open(dest, "wb") as fh:
                fh.write(data)
            return None
        except urllib.error.HTTPError as exc:
            last = f"HTTP {exc.code}"
            if exc.code not in RETRYABLE_STATUS:
                return f"{last} (not retryable)"
        except (urllib.error.URLError, TimeoutError, OSError) as exc:
            last = f"{type(exc).__name__}: {exc}"
        if attempt < ATTEMPTS:
            print(f"  attempt {attempt}/{ATTEMPTS}: {last}; retrying in {SLEEP_SEC:g}s", file=sys.stderr)
            time.sleep(SLEEP_SEC)
    return f"{last} after {ATTEMPTS} attempts"


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
    url = f"{FLAT_CONTAINER}/{id_lc}/{v_lc}/{id_lc}.{v_lc}.nupkg"
    out = os.path.join(workdir, row["fileName"])
    print(f"download {url}")
    problem = fetch(url, out)
    if problem is not None:
        print(
            f"::error::could not download {pid} {v} from nuget.org: {problem}. The package was "
            f"pushed before this step ran, so it is public; this is a failure to VERIFY it, not a "
            f"failure to publish it. Re-run this job to verify once indexing catches up.",
            file=sys.stderr,
        )
        failures += 1
        continue

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
