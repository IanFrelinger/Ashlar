#requires -Version 7.0
param(
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string] $Feed,
    [Parameter(Mandatory = $true)][ValidateNotNullOrEmpty()][string] $Version,
    [string] $EvidenceDirectory = "",
    [switch] $KeepWorkspace
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot "..")).Path
$verificationId = [Guid]::NewGuid().ToString("N")
$startedAtUtc = [DateTimeOffset]::UtcNow
$temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workspace = Join-Path $temporaryRoot ("ashlar-verified-package-" + $verificationId)
if ($workspace.StartsWith($repoRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The consumer workspace must be outside the checkout. Choose a system temporary directory outside the repository."
}
New-Item -ItemType Directory -Path $workspace | Out-Null
$consumer = Join-Path $workspace "consumer"
$fixture = Join-Path $workspace "fixture"
$evidence = Join-Path $workspace "evidence"
New-Item -ItemType Directory -Path $consumer, $fixture, $evidence | Out-Null
$publishedEvidence = $null
if (-not [string]::IsNullOrWhiteSpace($EvidenceDirectory)) {
    $evidenceRoot = [IO.Path]::GetFullPath($EvidenceDirectory)
    $publishedEvidence = Join-Path $evidenceRoot ("run-" + $startedAtUtc.ToString("yyyyMMddTHHmmssfffZ") + "-" + $verificationId)
    New-Item -ItemType Directory -Path $publishedEvidence -Force | Out-Null
    Write-Host "Verification evidence directory: $publishedEvidence"
}
$environmentNames = @("NUGET_PACKAGES", "NUGET_HTTP_CACHE_PATH", "DOTNET_CLI_HOME", "DOTNET_NOLOGO", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE")
$previousEnvironment = @{}
foreach ($name in $environmentNames) { $previousEnvironment[$name] = [Environment]::GetEnvironmentVariable($name) }

function Invoke-CheckedDotNet([string[]] $Arguments, [string] $LogName) {
    & dotnet @Arguments 2>&1 | Tee-Object -FilePath (Join-Path $evidence $LogName) | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE. See $evidence." }
}

function Write-RunOutcome([string] $Status, [string] $Phase) {
    $completedAt = if ($Status -eq "RUNNING") { $null } else { [DateTimeOffset]::UtcNow.ToString("O") }
    # Deliberately omit exception text, environment variables and signing material.
    $outcome = [ordered]@{
        VerificationId = $verificationId
        Status = $Status
        Phase = $Phase
        StartedAtUtc = $startedAtUtc.ToString("O")
        CompletedAtUtc = $completedAt
        PackageVersion = $Version
    } | ConvertTo-Json
    $outcome | Set-Content -LiteralPath (Join-Path $evidence "run-outcome.json") -Encoding utf8NoBOM
    if ($null -ne $publishedEvidence) {
        $outcome | Set-Content -LiteralPath (Join-Path $publishedEvidence "run-outcome.json") -Encoding utf8NoBOM
    }
}

$succeeded = $false
$phase = "setup"
try {
    Write-RunOutcome "RUNNING" $phase
    $packageFeed = (Resolve-Path -LiteralPath $Feed).Path
    if (-not (Test-Path -LiteralPath $packageFeed -PathType Container)) { throw "Feed must be a directory of prepacked packages." }
    foreach ($file in @("VerifiedDeploymentConsumer.csproj", "Program.cs", "Directory.Build.props", "Directory.Packages.props")) {
        Copy-Item -LiteralPath (Join-Path $repoRoot "docs/samples/VerifiedDeploymentConsumer/$file") -Destination $consumer
    }
    $fixtureSource = Join-Path $repoRoot "samples/certified-brick-reuse/Ashlar.Certified.DamageResolver"
    foreach ($file in @("DamageResolverBrick.cs", "damage-resolver.witness.json")) {
        Copy-Item -LiteralPath (Join-Path $fixtureSource $file) -Destination $fixture
    }
    # The gate inspects this author build surface; it compiles the source itself, never invoking this project.
    '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>' |
        Set-Content -LiteralPath (Join-Path $fixture "DamageResolver.csproj") -Encoding utf8NoBOM
    $env:NUGET_PACKAGES = Join-Path $workspace "packages"
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $workspace "http-cache"
    $env:DOTNET_CLI_HOME = Join-Path $workspace "cli-home"
    $env:DOTNET_NOLOGO = "1"
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = "1"
    $escapedFeed = [Security.SecurityElement]::Escape($packageFeed)
    $config = Join-Path $workspace "NuGet.Config"
    @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="ashlar-local" value="$escapedFeed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
  <packageSourceMapping>
    <packageSource key="ashlar-local"><package pattern="Ashlar.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath $config -Encoding utf8NoBOM
    $project = Join-Path $consumer "VerifiedDeploymentConsumer.csproj"
    Write-Host "Package-only consumer: $workspace (feed $packageFeed, version $Version)"
    Push-Location $consumer
    try {
        $phase = "restore"
        Write-RunOutcome "RUNNING" $phase
        Invoke-CheckedDotNet @("restore", $project, "--configfile", $config, "--force-evaluate", "-p:AshlarPackageVersion=$Version", "-v", "minimal") "restore.log"
        $phase = "package-inventory"
        Write-RunOutcome "RUNNING" $phase
        $assets = Get-Content -LiteralPath (Join-Path $consumer "obj/project.assets.json") -Raw | ConvertFrom-Json
        $ashlarPackages = @()
        foreach ($library in $assets.libraries.PSObject.Properties) {
            if ($library.Value.type -eq "project") { throw "Unexpected source project reference: $($library.Name)" }
            if ($library.Name.StartsWith("Ashlar.", [StringComparison]::OrdinalIgnoreCase)) {
                $parts = $library.Name.Split('/')
                if ($parts[1] -ne $Version) { throw "Unexpected Ashlar version: $($library.Name), wanted $Version" }
                $ashlarPackages += $library.Name
            }
        }
        if ($ashlarPackages.Count -eq 0) { throw "No packaged Ashlar dependencies were restored." }
        $ashlarPackages | Sort-Object | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence "packages.json") -Encoding utf8NoBOM
        $phase = "build"
        Write-RunOutcome "RUNNING" $phase
        Invoke-CheckedDotNet @("build", $project, "-c", "Release", "--no-restore", "-p:AshlarPackageVersion=$Version", "-v", "minimal") "build.log"
        $phase = "run"
        Write-RunOutcome "RUNNING" $phase
        Invoke-CheckedDotNet @("run", "--project", $project, "-c", "Release", "--no-build", "--no-restore", "-p:AshlarPackageVersion=$Version", "--", $fixture, $evidence) "run.log"
    }
    finally { Pop-Location }

    # Check the named transcript, so removing an assertion cannot quietly make the smoke green.
    $phase = "assertions"
    Write-RunOutcome "RUNNING" $phase
    $expected = @(
        "runtime-net10", "actual-gate-admitted", "gate-emitted-artifact-bound", "pinned-signature-trusted",
        "host-unavailable-before-start", "verified-only-registry", "signed-module-executed", "damage-witness-output",
        "deployed-dll-replacement-keeps-loaded-module", "host-unavailable-after-stop", "tampered-artifact-refused",
        "tampered-source-refused", "foreign-signer-refused", "unpinned-policy-refused"
    )
    $transcript = @(Get-Content -LiteralPath (Join-Path $evidence "run.log"))
    foreach ($assertion in $expected) {
        if (@($transcript | Where-Object { $_ -eq "ASSERT $assertion PASS" }).Count -ne 1) {
            throw "Missing or repeated consumer assertion: $assertion"
        }
    }
    if ($transcript -notcontains "RESULT=PASS assertions=$($expected.Count)") { throw "Consumer success transcript is incomplete." }
    $succeeded = $true
    $phase = "complete"
    Write-Host "verify-verified-deployment-packages: PASS ($($expected.Count) assertions; .NET 10 real gate and packaged host)"
}
finally {
    foreach ($name in $environmentNames) { [Environment]::SetEnvironmentVariable($name, $previousEnvironment[$name]) }
    try {
        if ($null -ne $publishedEvidence) {
            # This directory belongs only to this run. Earlier successes and failures remain untouched.
            Get-ChildItem -LiteralPath $evidence | Copy-Item -Destination $publishedEvidence -Recurse -Force
        }
    }
    catch {
        $succeeded = $false
        $phase = "evidence-export"
        throw
    }
    finally {
        Write-RunOutcome $(if ($succeeded) { "PASS" } else { "FAIL" }) $phase
    }
    if ($KeepWorkspace -or -not $succeeded) {
        Write-Host "Verification workspace retained at $workspace"
    }
    else {
        # Only the newly-created direct child of the original temp directory may be removed.
        $resolvedWorkspace = (Resolve-Path -LiteralPath $workspace).Path
        $resolvedParent = [IO.Path]::GetFullPath([IO.Path]::GetDirectoryName($resolvedWorkspace))
        if ($resolvedParent.TrimEnd([IO.Path]::DirectorySeparatorChar) -ne $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) -or
            -not [IO.Path]::GetFileName($resolvedWorkspace).StartsWith("ashlar-verified-package-", [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected verification workspace: $resolvedWorkspace"
        }
        Remove-Item -LiteralPath $resolvedWorkspace -Recurse -Force
    }
}
