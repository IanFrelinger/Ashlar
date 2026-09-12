#!/usr/bin/env bash
# Print the verification presets of a PUBLISHED Ashlar.Certification.Contracts, by reflection.
#
# Companion to scripts/verify-packed-certification-trust-behavior.sh. That script is the gate and
# fails a build; this one only reports, and it runs against versions whose API surface differs from
# today's — which is the only way to answer "was version X fail-open?" after the fact.
#
# Usage
#   bash scripts/probe-published-certification-presets.sh 0.1.2
#   ASHLAR_PROBE_FEED=/path/to/nupkgs bash scripts/probe-published-certification-presets.sh 1.0.0
set -euo pipefail

VERSION="${1:-}"
if [[ -z "${VERSION}" ]]; then
  echo "usage: $0 <package-version> (e.g. 0.1.2)" >&2
  exit 2
fi

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SAMPLE="${ROOT}/docs/samples/CertificationPresetProbe/CertificationPresetProbe.csproj"
FEED="${ASHLAR_PROBE_FEED:-}"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/ashlar-preset-probe-XXXXXX")"
trap 'rm -rf "${WORK}"' EXIT

export NUGET_PACKAGES="${WORK}/packages"
export DOTNET_CLI_HOME="${WORK}/cli-home"
mkdir -p "${NUGET_PACKAGES}" "${DOTNET_CLI_HOME}"

if [[ -n "${FEED}" ]]; then
  FEED="$(cd "${FEED}" && pwd)"
  cat > "${WORK}/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="ashlar-local" value="${FEED}" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF
  echo "==> Probing Ashlar.Certification.Contracts ${VERSION} from ${FEED}"
else
  cat > "${WORK}/NuGet.Config" <<'EOF'
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
EOF
  echo "==> Probing Ashlar.Certification.Contracts ${VERSION} from nuget.org"
fi

dotnet restore "${SAMPLE}" \
  --configfile "${WORK}/NuGet.Config" \
  -p:AshlarCertificationContractsVersion="${VERSION}" \
  -v minimal

dotnet run \
  --project "${SAMPLE}" \
  -c Release \
  --no-restore \
  -p:AshlarCertificationContractsVersion="${VERSION}"
