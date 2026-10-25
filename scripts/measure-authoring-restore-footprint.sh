#!/usr/bin/env bash
# Measures what a brick project that references Ashlar.Authoring downloads on its first restore:
# a net8.0 class library with one PackageReference, restored into an empty NuGet cache.
#
#   bash scripts/measure-authoring-restore-footprint.sh published 0.3.0   # from nuget.org
#   bash scripts/measure-authoring-restore-footprint.sh local 9.9.9-local # this checkout, packed
#
# Prints the package count, the cache size in bytes and MiB, and the ten largest packages.
# Not a gate: it measures, it asserts nothing.
set -euo pipefail

MODE="${1:?usage: measure-authoring-restore-footprint.sh <published|local> <version>}"
VERSION="${2:?usage: measure-authoring-restore-footprint.sh <published|local> <version>}"
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK}"' EXIT

SOURCES=("--source" "https://api.nuget.org/v3/index.json")
if [[ "${MODE}" == "local" ]]; then
  FEED="${WORK}/feed"
  bash "${ROOT}/scripts/pack-ashlar-hosting-graph.sh" "${VERSION}" "${FEED}" >/dev/null
  dotnet pack "${ROOT}/src/Ashlar.Authoring/Ashlar.Authoring.csproj" -c Release -o "${FEED}" \
    -p:PackageVersion="${VERSION}" -v quiet >/dev/null
  SOURCES=("--source" "${FEED}" "${SOURCES[@]}")
elif [[ "${MODE}" != "published" ]]; then
  echo "measure-authoring-restore-footprint: mode must be published or local, not '${MODE}'" >&2
  exit 2
fi

mkdir -p "${WORK}/brick"
cat > "${WORK}/brick/Brick.csproj" <<CSPROJ
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Ashlar.Authoring" Version="${VERSION}" />
  </ItemGroup>
</Project>
CSPROJ
printf '<configuration><packageSources><clear /></packageSources></configuration>\n' > "${WORK}/brick/nuget.config"

CACHE="${WORK}/cache"
NUGET_PACKAGES="${CACHE}" dotnet restore "${WORK}/brick/Brick.csproj" "${SOURCES[@]}" \
  --configfile "${WORK}/brick/nuget.config" -v quiet >/dev/null

COUNT=$(find "${CACHE}" -mindepth 1 -maxdepth 1 -type d | wc -l)
BYTES=$(du -sb "${CACHE}" | cut -f1)
echo "Ashlar.Authoring ${VERSION} (${MODE}): ${COUNT} packages, ${BYTES} bytes ($((BYTES / 1048576)) MiB) in the NuGet cache"
echo "largest:"
du -sm "${CACHE}"/* | sort -rn | head -10 | sed "s#${CACHE}/##"
