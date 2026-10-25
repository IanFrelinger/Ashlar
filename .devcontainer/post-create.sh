#!/usr/bin/env bash
# Setup-gate restore graph (same projects as scripts/docker-restore.ps1).
# Full Ashlar.sln restore is skipped here in favor of the same minimal graph as scripts/docker-restore.ps1 (fast dev-container bootstrap).
set -euo pipefail
cd "$(dirname "$0")/.."

# Named volume at ~/.nuget/packages can leave ~/.nuget (or parents) owned by root; dotnet then cannot create ~/.nuget/NuGet.
mkdir -p "${HOME}/.nuget"
if command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
  sudo -n chown -R "$(id -un)":"$(id -gn)" "${HOME}/.nuget"
fi

# The dotnet devcontainer image ships python3-minimal only; its stripped stdlib lacks
# modules such as `dataclasses`, which scripts/verify-open-commercial-dependency-boundary.py
# (run by scripts/dependency-boundary-gate.sh) imports. Install the full stdlib once.
if ! python3 -c 'import dataclasses' >/dev/null 2>&1; then
  PY_MM="$(python3 -c 'import sys; print("%d.%d" % sys.version_info[:2])')"
  echo "post-create: installing libpython${PY_MM}-stdlib (python3-minimal lacks the full stdlib)..."
  APT_SUDO=""
  if [ "$(id -u)" != "0" ] && command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
    APT_SUDO="sudo -n"
  fi
  ${APT_SUDO} apt-get update -qq
  ${APT_SUDO} apt-get install -y --no-install-recommends "libpython${PY_MM}-stdlib"
  python3 -c 'import dataclasses'
fi

# The dotnet:10.0 image ships ONLY the .NET 10 runtimes, but this repo multi-targets net8.0.
# Without the 8.0 ASP.NET runtime present, net8.0 assemblies can run only by rolling forward
# onto ASP.NET Core 10, and anything hosting HTTP then dies with
#   System.Text.Json ... PipeWriter 'ResponseBodyPipeWriter' does not implement UnflushedBytes
# because ASP.NET Core 8's writer predates that member. It reads as a product bug: the whole
# GameDirector MCP endpoint suite fails that way (10 tests) and passes once 8.0 is installed.
if ! dotnet --list-runtimes | grep -q '^Microsoft\.AspNetCore\.App 8\.'; then
  echo "post-create: installing the ASP.NET Core 8 runtime (image ships .NET 10 only)..."
  DOTNET_SUDO=""
  if [ "$(id -u)" != "0" ] && command -v sudo >/dev/null 2>&1 && sudo -n true 2>/dev/null; then
    DOTNET_SUDO="sudo -n"
  fi
  curl -sSL https://dot.net/v1/dotnet-install.sh -o /tmp/dotnet-install.sh
  ${DOTNET_SUDO} bash /tmp/dotnet-install.sh --version 8.0.33 --runtime aspnetcore \
    --install-dir "${DOTNET_ROOT:-/usr/share/dotnet}" --no-path
  rm -f /tmp/dotnet-install.sh
  dotnet --list-runtimes | grep '^Microsoft\.AspNetCore\.App 8\.' || {
    echo "post-create: WARNING - ASP.NET Core 8 runtime still not visible; net8.0 targets will roll forward." >&2
  }
fi

echo "post-create: restoring NuGet graph..."
dotnet restore src/Ashlar.Core.Application/Ashlar.Core.Application.csproj --verbosity minimal
dotnet restore src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj --verbosity minimal
dotnet restore application/src/Ashlar.CLI/Ashlar.CLI.csproj --verbosity minimal
dotnet restore src/Ashlar.Tests.Infrastructure/scripts/copy-assemblies.csproj --verbosity minimal
dotnet restore src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --verbosity minimal
echo "post-create: ok"
