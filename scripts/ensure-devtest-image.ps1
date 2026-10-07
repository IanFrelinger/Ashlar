<#
.SYNOPSIS
Build the dev/test image if it is not already present, and return its tag.

.DESCRIPTION
The container scripts need a .NET 10 SDK *and* the real ASP.NET Core 8 runtime. See
.docker/Dockerfile.devtest for why DOTNET_ROLL_FORWARD=LatestMajor is not a substitute:
it rolls net8.0 onto ASP.NET Core 10 even when 8.0 is installed, and every HTTP-hosting
test then fails with an exception that reads like a product bug.

Docker layer-caches the build, so the first call costs pulling the base images (the SDK 10
devcontainer and Microsoft's ASP.NET Core 8 runtime image) and every later call is
effectively free.

.PARAMETER Image
Tag to build. Defaults to ashlar-devtest:local.

.PARAMETER Rebuild
Force a rebuild even when the image already exists.

.EXAMPLE
$image = & "$PSScriptRoot/ensure-devtest-image.ps1"
docker run --rm $image dotnet --list-runtimes
#>
[CmdletBinding()]
param(
    [string] $Image = "ashlar-devtest:local",
    [switch] $Rebuild
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot

$needBuild = $Rebuild.IsPresent
if (-not $needBuild) {
    docker image inspect $Image *> $null
    if ($LASTEXITCODE -ne 0) { $needBuild = $true }
}

if ($needBuild) {
    # Progress goes to the host stream so the tag returned to the pipeline stays clean.
    Write-Host "ensure-devtest-image: building $Image (first build pulls the SDK 10 and ASP.NET Core 8 runtime images)..." -ForegroundColor Cyan
    & docker build -t $Image -f (Join-Path $root ".docker/Dockerfile.devtest") (Join-Path $root ".docker") | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "docker build failed (exit $LASTEXITCODE)" }
}

$Image
