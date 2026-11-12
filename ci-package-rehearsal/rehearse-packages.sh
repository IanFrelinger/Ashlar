#!/usr/bin/env bash
# Run INSIDE scripts/test-in-container.sh, after the same-SHA mutation campaign.
# Usage: bash /evidence/rehearse-packages.sh <full-source-sha> <unique-local-version>
# Requires /evidence to be an external writable mount. Never publishes anything.
set -euo pipefail

if [[ $# -ne 2 ]]; then
  echo "usage: $0 <full-source-sha> <unique-local-version>" >&2
  exit 2
fi
expected_sha="$1"
version="$2"
[[ "$expected_sha" =~ ^[0-9a-f]{40}$ ]] || { echo "Full SHA required" >&2; exit 2; }
[[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+-[a-zA-Z0-9.]+$ ]] || {
  echo "Use a unique prerelease version for this local rehearsal" >&2
  exit 2
}
repo="$(git rev-parse --show-toplevel)"
[[ "$(uname -s)" == Linux && "$repo" == /repo &&
   -d /repo/.git && -d /src-mirror/.git && -e /.dockerenv ]] || {
  echo "Run through scripts/test-in-container.sh in its Linux clone" >&2
  exit 2
}
mountpoint -q /evidence || { echo "/evidence must be a writable external mount" >&2; exit 2; }
[[ -w /evidence ]] || { echo "/evidence is not writable" >&2; exit 2; }
[[ "$(git rev-parse HEAD)" == "$expected_sha" ]] || { echo "Source SHA mismatch" >&2; exit 2; }
[[ -z "$(git status --porcelain)" ]] || { echo "Source clone is not clean" >&2; exit 2; }

evidence=/evidence/package-rehearsal
# Do not reuse previous output, logs, packages, or evidence.
mkdir "$evidence"
mkdir "$evidence/packages"
exec 3>&1 4>&2
exec > >(tee "$evidence/rehearsal.log") 2>&1
log_pid="$!"
finish() {
  result="$?"
  trap - EXIT
  printf 'REHEARSAL_BODY_EXIT_STATUS=%s\n' "$result"
  exec 1>&3 2>&4
  if wait "$log_pid"; then
    log_result=0
  else
    log_result="$?"
  fi
  if [[ "$result" -eq 0 && "$log_result" -ne 0 ]]; then
    result="$log_result"
  fi
  printf '%s\n' "$result" > "$evidence/exit-status.txt"
  printf 'REHEARSAL_EXIT_STATUS=%s LOG_EXIT_STATUS=%s\n' "$result" "$log_result"
  exit "$result"
}
trap finish EXIT

printf '%s\n' "$expected_sha" > "$evidence/source-sha.txt"
printf '%s\n' "$version" > "$evidence/package-version.txt"
git status --porcelain > "$evidence/initial-status.txt"
uname -a
git log -1 --format='%H %s'
unset DOTNET_ROLL_FORWARD
unset ASHLAR_SDK_VERIFY_NO_ISOLATED_CACHE
unset ASHLAR_SDK_VERIFY_ISOLATED_ROOT
unset ASHLAR_CERT_DEV_HMAC_KEY

dotnet --info | tee "$evidence/dotnet-info.txt"
dotnet --list-runtimes | tee "$evidence/runtimes.txt"
dotnet --list-sdks | tee "$evidence/sdks.txt"
grep -q '^10\.' "$evidence/sdks.txt"
for framework in Microsoft.NETCore.App Microsoft.AspNetCore.App; do
  for major in 8 10; do
    grep -q "^$framework $major\." "$evidence/runtimes.txt"
  done
done

export PACKAGE_VERSION="$version"
export ASHLAR_REHEARSAL_EVIDENCE="$evidence"
export ASHLAR_REHEARSAL_VERSION="$version"
export ASHLAR_REHEARSAL_FEED="$evidence/packages"
export ASHLAR_REHEARSAL_SOURCE_SHA="$expected_sha"
export ASHLAR_REHEARSAL_PHASE=build
export NUGET_PACKAGES
export DOTNET_CLI_HOME
NUGET_PACKAGES="$(mktemp -d /tmp/ashlar-rehearsal-build-packages-XXXXXX)"
DOTNET_CLI_HOME="$(mktemp -d /tmp/ashlar-rehearsal-build-cli-XXXXXX)"
OUT="$ASHLAR_REHEARSAL_FEED"

# Verify each package's identity, version, manifest digest, content digest, and sidecar.
cat > "$evidence/verify-feed.py" <<'PY'
import hashlib
import json
import os
from pathlib import Path
import sys
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, "/repo/scripts/lib")
from nupkg_content_digest import content_digest

feed = Path(os.environ["ASHLAR_REHEARSAL_FEED"])
evidence = Path(os.environ["ASHLAR_REHEARSAL_EVIDENCE"])
version = os.environ["ASHLAR_REHEARSAL_VERSION"]
expected = {"Ashlar." + suffix for suffix in (
    "Abstractions", "Contracts", "Core.Domain", "Core.Application",
    "Brick.Contracts", "Analyzers", "Policies", "Tools.Assembly", "Tools.Dev",
    "Transport.Grpc", "Runtime", "Certification.Contracts", "Infrastructure",
    "Orchestration", "BackgroundAgents", "AI.Pipeline", "Hosting",
    "Hosting.Bundle", "Client", "Sdk", "Authoring", "Bricks.GitHub",
    "CLI", "Mcp.Server.Host",
)}
rows = json.loads((feed / "nuget-publish-manifest.json").read_text())
if not isinstance(rows, list) or len(rows) != len(expected):
    raise SystemExit("Expected exactly 24 manifest rows")
if {row["id"] for row in rows} != expected:
    raise SystemExit("Package identity set does not match this audited release recipe")
by_file = {row["fileName"]: row for row in rows}
if len(by_file) != len(rows):
    raise SystemExit("Duplicate package filenames in manifest")
if {p.name for p in feed.glob("*.nupkg")} != set(by_file):
    raise SystemExit("Feed files and manifest differ")

def identity(path):
    with zipfile.ZipFile(path) as z:
        names = [n for n in z.namelist() if n.endswith(".nuspec")]
        if len(names) != 1:
            raise SystemExit(f"{path.name}: expected one nuspec")
        root = ET.fromstring(z.read(names[0]))
        metadata = next((n for n in root if n.tag.rsplit("}", 1)[-1] == "metadata"), None)
        if metadata is None:
            raise SystemExit(f"{path.name}: missing nuspec metadata")
        fields = {}
        for key in ("id", "version"):
            matches = [n.text for n in metadata if n.tag.rsplit("}", 1)[-1] == key]
            if len(matches) != 1:
                raise SystemExit(f"{path.name}: invalid nuspec {key}")
            fields[key] = matches[0]
        return fields["id"], fields["version"]

verified = []
for name, row in sorted(by_file.items()):
    path = feed / name
    pid, package_version = identity(path)
    if (pid, package_version) != (row["id"], version) or row["version"] != version:
        raise SystemExit(f"{name}: ID or version mismatch")
    if name != f"{pid}.{version}.nupkg":
        raise SystemExit(f"{name}: noncanonical package filename")
    digest = hashlib.sha256(path.read_bytes()).hexdigest()
    if digest != row["sha256"] or content_digest(path) != row["contentSha256"]:
        raise SystemExit(f"{name}: manifest digest mismatch")
    if (feed / (name + ".sha256.txt")).read_text() != f"{digest}  {name}\n":
        raise SystemExit(f"{name}: SHA-256 sidecar mismatch")
    verified.append(row)
# Symbols are retained and checked for identity/version, but are not runtime packages.
symbols = []
for path in sorted(feed.glob("*.snupkg")):
    pid, package_version = identity(path)
    if pid not in expected or package_version != version:
        raise SystemExit(f"{path.name}: unexpected symbol package identity/version")
    symbols.append({"fileName": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
snapshot = {
    "sourceSha": os.environ["ASHLAR_REHEARSAL_SOURCE_SHA"],
    "packages": verified,
    "symbols": symbols,
    "manifestSha256": hashlib.sha256((feed / "nuget-publish-manifest.json").read_bytes()).hexdigest(),
}
phase = sys.argv[1]
before = evidence / "package-digests-before.json"
if phase == "before":
    before.write_text(json.dumps(snapshot, indent=2) + "\n")
elif phase == "after":
    if snapshot != json.loads(before.read_text()):
        raise SystemExit("Produced package bytes or manifest changed during consumption")
    (evidence / "package-digests-after.json").write_text(json.dumps(snapshot, indent=2) + "\n")
else:
    raise SystemExit("Unknown verification phase")
print(f"PACKAGE_INVENTORY_AND_DIGESTS=OK ({len(rows)} packages, {phase})")
PY

# This runs immediately after each consumer restore, before its script deletes the cache.
cat > "$evidence/verify-consumer-cache.py" <<'PY'
import hashlib
import json
import os
from pathlib import Path
import xml.etree.ElementTree as ET
import zipfile

evidence = Path(os.environ["ASHLAR_REHEARSAL_EVIDENCE"])
feed = Path(os.environ["ASHLAR_REHEARSAL_FEED"])
rows = json.loads((feed / "nuget-publish-manifest.json").read_text())
expected = {row["id"].lower(): row for row in rows}
version = os.environ["ASHLAR_REHEARSAL_VERSION"]
phase = os.environ["ASHLAR_REHEARSAL_PHASE"]
cache = Path(os.environ["NUGET_PACKAGES"])
required = {"sdk": "ashlar.hosting.bundle", "trust": "ashlar.certification.contracts"}[phase]
packages = []
for package in sorted(cache.glob("ashlar.*/*/*.nupkg")):
    with zipfile.ZipFile(package) as z:
        nuspecs = [n for n in z.namelist() if n.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise SystemExit(f"Bad cache nuspec count: {package}")
        root = ET.fromstring(z.read(nuspecs[0]))
        metadata = next(n for n in root if n.tag.rsplit("}", 1)[-1] == "metadata")
        def field(key):
            values = [n.text for n in metadata if n.tag.rsplit("}", 1)[-1] == key]
            if len(values) != 1:
                raise SystemExit(f"Bad cache {key}: {package}")
            return values[0]
        pid, actual_version = field("id"), field("version")
    row = expected.get(pid.lower())
    if row is None or actual_version != version:
        raise SystemExit(f"Unexpected Ashlar package in isolated cache: {pid} {actual_version}")
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    if digest != row["sha256"]:
        raise SystemExit(f"Consumer restored different bytes for {pid}")
    packages.append({"id": pid, "version": actual_version, "sha256": digest})
if required not in {p["id"].lower() for p in packages}:
    raise SystemExit(f"Required consumer package was not observed in cache: {required}")
receipt = {
    "phase": phase,
    "sourceSha": os.environ["ASHLAR_REHEARSAL_SOURCE_SHA"],
    "packageVersion": version,
    "isolatedCache": str(cache),
    "packages": packages,
}
with (evidence / "consumer-cache-receipts.jsonl").open("a") as stream:
    stream.write(json.dumps(receipt, sort_keys=True) + "\n")
print(f"CONSUMER_PACKAGE_BYTES=OK ({phase}, {len(packages)} Ashlar packages)")
PY

python3 scripts/verify-pack-ashlar-hosting-graph-alignment.py
bash scripts/pack-ashlar-hosting-graph.sh "$version" "$OUT"
for project in Client Sdk Authoring Bricks.GitHub; do
  dotnet pack "src/Ashlar.$project/Ashlar.$project.csproj" \
    -c Release -o "$OUT" -p:PackageVersion="$version" \
    -p:IncludeSymbols=true -p:SymbolPackageFormat=snupkg -v minimal
done
dotnet pack application/src/Ashlar.CLI/Ashlar.CLI.csproj \
  -c Release -o "$OUT" -p:PackageVersion="$version" \
  -p:IncludeTestProjectReferences=false -v minimal
dotnet pack src/Ashlar.Mcp.Server.Host/Ashlar.Mcp.Server.Host.csproj \
  -c Release -o "$OUT" -p:PackageVersion="$version" \
  -p:AshlarMcpHostLocalModels=false -v minimal

bash scripts/render-nuget-release-manifest.sh "$OUT"
python3 "$evidence/verify-feed.py" before
(cd "$OUT" && sha256sum -c -- *.nupkg.sha256.txt)

# Add evidence after successful restores without altering dotnet's arguments or exit status.
dotnet() {
  if command dotnet "$@"; then
    if [[ $# -gt 0 && "$1" == restore ]]; then
      python3 "$ASHLAR_REHEARSAL_EVIDENCE/verify-consumer-cache.py"
    fi
  else
    return "$?"
  fi
}
export -f dotnet

export ASHLAR_REHEARSAL_PHASE=sdk
ASHLAR_SDK_PACKAGE_VERSION="$version" ASHLAR_SDK_PACKAGE_FEED="$OUT" \
  bash scripts/verify-stable-sdk-host-sample-packages.sh \
  | tee "$evidence/sdk-consumer.log"
grep -Fxq 'verify-stable-sdk-host-sample-packages: OK' "$evidence/sdk-consumer.log"

export ASHLAR_REHEARSAL_PHASE=trust
ASHLAR_CONFORMANCE_SOURCE=local ASHLAR_CONFORMANCE_VERSION="$version" \
ASHLAR_CONFORMANCE_FEED="$OUT" ASHLAR_CONFORMANCE_TFMS='net8.0 net10.0' \
  bash scripts/verify-packed-certification-trust-behavior.sh \
  | tee "$evidence/packed-trust.log"

# The repository verifier is authoritative; separately preserve and check the full assertion set.
python3 - "$evidence" <<'PY'
import json
from pathlib import Path
import re
import sys

evidence = Path(sys.argv[1])
text = (evidence / "packed-trust.log").read_text()
expected = {
    "bound-asset-matches-consumer-tfm",
    "default-requires-ed25519", "strict-requires-ed25519",
    "default-schema-floor-is-trust-loop", "strict-schema-floor-is-trust-loop",
    "strict-requires-gate-emitted-artifact", "strict-requires-certifier-identity",
    "default-reports-itself-strict", "legacy-reports-itself-not-strict",
    "hmac-fallback-reports-dev-key", "positive-control-legacy-trusts-valid-hmac-record",
    "default-refuses-hmac-only-record", "strict-refuses-hmac-only-record",
    "omitted-options-refuses-hmac-only-record", "default-refuses-schema-downgrade",
    "unknown-schema-version-refused", "content-hash-mismatch-refused",
    "stripped-hmac-signature-refused",
}
segments = re.split(r"^==> Consumer TFM (net8\.0|net10\.0)\s*$", text, flags=re.M)
if len(segments) != 5 or segments[1::2] != ["net8.0", "net10.0"]:
    raise SystemExit("Expected exactly one trust transcript per requested framework")
receipts = []
for tfm, transcript in zip(segments[1::2], segments[2::2]):
    names = re.findall(r"^PASS (.+)$", transcript, flags=re.M)
    if len(names) != 18 or set(names) != expected:
        raise SystemExit(f"{tfm}: missing, extra, or duplicate named assertions")
    for marker in ("ASSERTIONS_RUN=18", "ASSERTIONS_FAILED=0", "RESULT=OK"):
        if transcript.splitlines().count(marker) != 1:
            raise SystemExit(f"{tfm}: invalid transcript marker {marker}")
    (evidence / f"trust-{tfm}.txt").write_text(transcript)
    receipts.append({"framework": tfm, "assertions": sorted(names), "passed": 18, "failed": 0})
version = (evidence / "package-version.txt").read_text().strip()
if text.splitlines().count(f"PACKED_CERTIFICATION_TRUST=OK (version {version}, source local)") != 1:
    raise SystemExit("Missing final packed trust verdict")
cache_receipts = [json.loads(line) for line in (evidence / "consumer-cache-receipts.jsonl").read_text().splitlines()]
if [r["phase"] for r in cache_receipts] != ["sdk", "trust"]:
    raise SystemExit("Missing or unexpected isolated consumer cache receipts")
(evidence / "trust-assertion-receipts.json").write_text(json.dumps(receipts, indent=2) + "\n")
print("PACKED_TRUST_ASSERTION_RECONCILIATION=OK (36/36)")
PY

python3 "$evidence/verify-feed.py" after
(cd "$OUT" && sha256sum -c -- *.nupkg.sha256.txt)
[[ "$(git rev-parse HEAD)" == "$expected_sha" ]]
git status --porcelain | tee "$evidence/final-status.txt"
[[ ! -s "$evidence/final-status.txt" ]]
printf '%s\n' 'CLEAN_PACKAGE_REHEARSAL=OK'
