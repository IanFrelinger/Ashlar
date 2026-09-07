#!/usr/bin/env bash
# Pack certified damage-resolver brick + certification sidecar + verifier contracts for Project B reuse.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${ASHLAR_CERTIFIED_REUSE_VERSION:-0.1.0}"
FEED="${ASHLAR_CERTIFIED_REUSE_FEED:-${ROOT}/artifacts/certified-brick-feed}"
ARTIFACT_DIR="${ROOT}/samples/certified-brick-reuse/Ashlar.Certified.DamageResolver"
RECORD_PATH="${ARTIFACT_DIR}/certification-record.json"
CFG="${FEED}/NuGet.Config"

mkdir -p "${FEED}"

pack() {
  dotnet pack "${ROOT}/${1}" -c Release -o "${FEED}" \
    -p:PackageVersion="${VERSION}" \
    -p:IncludeTestProjectReferences=false \
    -p:UseProjectReferences=false \
    -v minimal
}

echo "==> Pack base contracts to ${FEED}"
# Only the two packages Project B consumes from this feed. Ashlar.Authoring used to be packed here
# too; nothing consumed it, and its packed dependency graph (Ashlar.Core.Application at the repo
# VERSION, which is on nuget.org) dragged nuget.org's Ashlar.Certification.Contracts over the local
# one and failed Project B's restore with NU1605 — the verifier must be the same code as the signer.
pack src/Ashlar.Brick.Contracts/Ashlar.Brick.Contracts.csproj
pack src/Ashlar.Certification.Contracts/Ashlar.Certification.Contracts.csproj

cat > "${CFG}" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="certified-feed" value="${FEED}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

# Honoured by the export tool below: the brick restores Ashlar.Brick.Contracts through this config
# (local feed first, then nuget.org). A loader-stage refusal exits 4 with the gate's own message and,
# via set -e, stops this script there.
export ASHLAR_CERT_NUGET_CONFIG="${CFG}"

echo "==> Certify damage-resolver and write content-bound record"
dotnet run --project "${ROOT}/tools/Ashlar.ExportCertifiedBrick/ExportCertifiedBrick.csproj" -- \
  "${RECORD_PATH}" "${ARTIFACT_DIR}"

echo "==> Pack certified brick artifact"
pack samples/certified-brick-reuse/Ashlar.Certified.DamageResolver/Ashlar.Certified.DamageResolver.csproj

echo "==> Feed ready at ${FEED}"
ls -la "${FEED}"
