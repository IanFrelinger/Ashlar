#!/usr/bin/env bash
# Tests for scripts/lib/nupkg_content_digest.py — the digest the post-push release check compares.
#
# WHY THIS EXISTS. The check this backs compared the raw bytes of the pushed .nupkg against the
# bytes nuget.org serves. nuget.org repository-signs on publish, so those differ by construction and
# the comparison could only fail — after `dotnet nuget push`, when the packages are already public.
# It had never run: the v0.1.2 release died one step earlier on index lag, so a check that cannot
# pass sat in the pipeline looking green.
#
# The replacement has to hold TWO properties at once, and a test that only covers one is worthless:
#   1. appending a repository signature must NOT change the digest  (or it fails every release);
#   2. changing any packaged byte MUST change it                    (or it catches nothing).
# Case 3 below is the one that would have caught the original bug, and it is written as an
# assertion about the OLD approach so the reason stays visible.
#
# Run:  bash tests/scripts/nupkg-content-digest.test.sh
# Pure python + bash: no network, no dotnet, no container.

set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

PASS=0
FAIL=0

# Bump when you add an assertion. See the check at the bottom for why.
EXPECTED_ASSERTIONS=11

ok()  { PASS=$((PASS + 1)); echo "  ok   — $1"; }
bad() { FAIL=$((FAIL + 1)); echo "  FAIL — $1"; echo "         $2"; }

# Probe by RUNNING each candidate, never by `command -v`: on Windows an App Execution Alias stub
# answers `command -v python3` and then exits without executing anything, which would let every
# case below "pass" having run no python at all.
PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'print(1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "FAIL - no working python interpreter (tried python3, python, py)"
  exit 1
fi

RESULTS="$("${PY_BIN}" - "${ROOT}" <<'PY'
import hashlib, io, os, shutil, sys, tempfile, zipfile

sys.path.insert(0, os.path.join(sys.argv[1], "scripts", "lib"))
from nupkg_content_digest import content_digest, frame_entry, has_signature

tmp = tempfile.mkdtemp()
results = []


def emit(name, got, want):
    results.append(f"{name}\t{'PASS' if got == want else 'FAIL'}\t{got!r} vs {want!r}")


PAYLOAD = {
    "[Content_Types].xml": b"<Types/>",
    "Demo.nuspec": b"<package><metadata><id>Demo</id></metadata></package>",
    "lib/net8.0/Demo.dll": b"MZ-not-really-a-dll",
    "README.md": b"# Demo",
}


def build(path, entries, compression=zipfile.ZIP_DEFLATED, order=None):
    names = order if order is not None else list(entries)
    with zipfile.ZipFile(path, "w", compression) as z:
        for n in names:
            z.writestr(n, entries[n])
    return path


def raw_sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for chunk in iter(lambda: f.read(1 << 20), b""):
            h.update(chunk)
    return h.hexdigest()


base = build(os.path.join(tmp, "base.nupkg"), PAYLOAD)
base_digest = content_digest(base)

# 1. deterministic
again = build(os.path.join(tmp, "again.nupkg"), PAYLOAD)
emit("identical packages digest the same", content_digest(again), base_digest)

# 2. THE property that makes the post-push check correct: signing is invisible to it.
signed = os.path.join(tmp, "signed.nupkg")
shutil.copyfile(base, signed)
with zipfile.ZipFile(signed, "a") as z:
    z.writestr(".signature.p7s", b"\x30\x82fake-pkcs7-repository-signature")
emit("appending .signature.p7s leaves the digest unchanged", content_digest(signed), base_digest)

# 3. REGRESSION GUARD for the bug this replaced: the raw file hash DOES move when nuget.org signs,
#    which is exactly why comparing it post-push could never pass.
emit("appending .signature.p7s DOES change the raw file hash", raw_sha256(signed) != raw_sha256(base), True)

# 4. and the signature is still detectable, so excluding it from the digest loses nothing
emit("the signature is still detected on the signed copy", has_signature(signed), True)
emit("and absent on the unsigned copy", has_signature(base), False)

# 5-7. POSITIVE CONTROLS: a digest that tolerated these would catch nothing.
tampered = dict(PAYLOAD)
tampered["lib/net8.0/Demo.dll"] = b"MZ-not-really-a-dll!"
emit("a changed payload byte changes the digest",
     content_digest(build(os.path.join(tmp, "tampered.nupkg"), tampered)) != base_digest, True)

renamed = {("lib/net8.0/Evil.dll" if k == "lib/net8.0/Demo.dll" else k): v for k, v in PAYLOAD.items()}
emit("a renamed entry changes the digest",
     content_digest(build(os.path.join(tmp, "renamed.nupkg"), renamed)) != base_digest, True)

added = dict(PAYLOAD)
added["lib/net8.0/Extra.dll"] = b"smuggled"
emit("an added entry changes the digest",
     content_digest(build(os.path.join(tmp, "added.nupkg"), added)) != base_digest, True)

# 8. zip re-encoding is not a difference a consumer can observe, and any tool that appends an entry
#    may re-encode. Tolerating it is required, not lenient.
reordered = build(os.path.join(tmp, "reordered.nupkg"), PAYLOAD,
                  compression=zipfile.ZIP_STORED, order=sorted(PAYLOAD, reverse=True))
emit("entry order and compression do not change the digest", content_digest(reordered), base_digest)

# 9-10. The length prefix, asserted on chosen bytes. Against real SHA-256 content this collision is
#       not constructible, so the property is checked on frame_entry directly rather than left as a
#       claim no test can reach. R is the shared tail that lets the two entry sets line up.
R = bytes(range(31))
TAIL = bytes(range(32, 64))  # escape-free: ASCII 32..63
set_a = [("a", b"b" + R), ("bc", TAIL)]
set_b = [("ab", R + b"b"), ("c", TAIL)]
naive = lambda pairs: b"".join(n.encode() + d for n, d in pairs)
framed = lambda pairs: b"".join(frame_entry(n, d) for n, d in pairs)
emit("without framing these two entry sets concatenate identically", naive(set_a) == naive(set_b), True)
emit("frame_entry keeps them distinct", framed(set_a) != framed(set_b), True)

shutil.rmtree(tmp, ignore_errors=True)
print("\n".join(results))
PY
)"

if [[ -z "${RESULTS}" ]]; then
  echo "FAIL - the python harness produced no results at all"
  exit 1
fi

echo "== nupkg content digest =="
while IFS=$'\t' read -r name verdict detail; do
  [[ -z "${name}" ]] && continue
  if [[ "${verdict}" == "PASS" ]]; then ok "${name}"; else bad "${name}" "${detail}"; fi
done <<< "${RESULTS}"

echo
echo "passed: ${PASS}   failed: ${FAIL}"

# The harness runs without -e, so a mid-file error would end it quietly and the summary would print
# whatever it reached and exit 0. A partial run is not a pass.
RAN=$((PASS + FAIL))
if [[ "${RAN}" -ne "${EXPECTED_ASSERTIONS}" ]]; then
  echo "FAIL - ran ${RAN} assertions, expected ${EXPECTED_ASSERTIONS}."
  echo "       Either this file stopped early or assertions were added without bumping"
  echo "       EXPECTED_ASSERTIONS at the top."
  exit 1
fi

[[ "${FAIL}" -eq 0 ]] || exit 1
