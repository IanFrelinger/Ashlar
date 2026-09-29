#!/usr/bin/env bash
# Write nuget-publish-manifest.json + per-file .sha256.txt for each *.nupkg in DIR.
#
# Each row carries TWO digests, because they answer different questions:
#
#   sha256         the bytes of the file we are about to push. Provenance for the artifact we
#                  produced, and the right thing to compare on a staging feed that does not re-sign.
#
#   contentSha256  every archive entry except the repository signature, by name and uncompressed
#                  content (scripts/lib/nupkg_content_digest.py). This is what survives publication:
#                  nuget.org repository-signs on publish, which appends `.signature.p7s` and changes
#                  the file hash, so `sha256` CANNOT match what the feed serves. Comparing it there
#                  fails every time, after the push has already made the packages public.
set -euo pipefail
DIR="${1:?directory containing *.nupkg}"
VER="${PACKAGE_VERSION:?set PACKAGE_VERSION (semver without v prefix)}"
VER="${VER#v}"
OUT_JSON="${DIR}/nuget-publish-manifest.json"
LIB_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/lib" && pwd)"

python3 - "$DIR" "$VER" "$OUT_JSON" "$LIB_DIR" <<'PY'
import hashlib, json, os, sys

d, ver, out_path, lib_dir = sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4]
sys.path.insert(0, lib_dir)
from nupkg_content_digest import content_digest

rows = []
for name in sorted(os.listdir(d)):
    if not name.endswith(".nupkg"):
        continue
    path = os.path.join(d, name)
    suf = f".{ver}.nupkg"
    if not name.endswith(suf):
        print(f"error: unexpected nupkg name {name} (expected *.{ver}.nupkg)", file=sys.stderr)
        sys.exit(1)
    pid = name[: -len(suf)]
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            h.update(chunk)
    digest = h.hexdigest()
    with open(path + ".sha256.txt", "w", encoding="utf-8") as sf:
        sf.write(f"{digest}  {name}\n")
    rows.append({
        "id": pid,
        "version": ver,
        "fileName": name,
        "sha256": digest,
        "contentSha256": content_digest(path),
    })
with open(out_path, "w", encoding="utf-8") as f:
    json.dump(rows, f, indent=2)
print(f"Wrote {out_path} ({len(rows)} packages)")
PY
