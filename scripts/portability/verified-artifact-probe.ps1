[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Produce', 'Consume')][string]$Mode,
    [Parameter(Mandatory)][string]$ArtifactDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [ValidateSet(8, 9, 10)][int]$RuntimeMajor = 10,
    [string]$RuntimeDotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$probeRoot = Join-Path $repoRoot 'tools/VerifiedArtifactPortability'
$artifactPath = [IO.Path]::GetFullPath($ArtifactDirectory)
$workPath = [IO.Path]::GetFullPath($WorkDirectory)
New-Item -ItemType Directory -Path $workPath -Force | Out-Null

function Invoke-ProbeCommand {
    param([string]$Executable, [string[]]$Arguments, [string]$Log)
    & $Executable @Arguments 2>&1 | Tee-Object -FilePath $Log
    if ($LASTEXITCODE -ne 0) {
        throw "$Executable failed with exit code $LASTEXITCODE. See $Log"
    }
}

Push-Location $repoRoot
try {
    if ($Mode -eq 'Produce') {
        if (Test-Path -LiteralPath $artifactPath) {
            if (@(Get-ChildItem -LiteralPath $artifactPath -Force).Count -ne 0) {
                throw 'Produce requires a new or empty artifact directory; it never overwrites a deployment.'
            }
        }
        $fixtureProject = Join-Path $probeRoot 'Fixture/Fixture.csproj'
        $fixtureOutput = Join-Path $workPath 'fixture'
        Invoke-ProbeCommand dotnet @('build', $fixtureProject, '-c', 'Release', '-o', $fixtureOutput,
            '-m:1', '-p:UseSharedCompilation=false', '--nologo', '-v', 'minimal') (Join-Path $workPath 'fixture-build.log')
        $producerOutput = Join-Path $workPath 'producer'
        Invoke-ProbeCommand dotnet @('publish', (Join-Path $probeRoot 'Producer/Producer.csproj'),
            '-c', 'Release', '-o', $producerOutput, '--self-contained', 'false', '-p:UseAppHost=false',
            '-m:1', '-p:UseSharedCompilation=false', '--nologo', '-v', 'minimal') (Join-Path $workPath 'producer-build.log')
        Invoke-ProbeCommand dotnet @((Join-Path $producerOutput 'Producer.dll'),
            (Join-Path $fixtureOutput 'Ashlar.PortabilityFixture.dll'), (Join-Path $probeRoot 'Fixture/PortableBrick.cs'),
            $artifactPath) (Join-Path $workPath 'produce.log')
    }
    else {
        $receipt = Join-Path $workPath 'receipt.json'
        if (Test-Path -LiteralPath $receipt) { Remove-Item -LiteralPath $receipt }
        $consumerOutput = Join-Path $workPath "consumer-net$RuntimeMajor"
        Invoke-ProbeCommand dotnet @('publish', (Join-Path $probeRoot 'Consumer/Consumer.csproj'),
            '-f', "net$RuntimeMajor.0", '-c', 'Release', '-o', $consumerOutput,
            '--self-contained', 'false', '-m:1', '-p:UseSharedCompilation=false', '--nologo', '-v', 'minimal') (Join-Path $workPath 'consumer-build.log')
        $config = Get-Content -LiteralPath (Join-Path $consumerOutput 'Consumer.runtimeconfig.json') -Raw | ConvertFrom-Json
        if ($config.runtimeOptions.rollForward -ne 'LatestPatch') {
            throw 'Consumer runtimeconfig must restrict runtime selection to LatestPatch.'
        }
        Invoke-ProbeCommand $RuntimeDotnet @((Join-Path $consumerOutput 'Consumer.dll'),
            $artifactPath, "$RuntimeMajor", $receipt) (Join-Path $workPath 'consume.log')
        if (-not (Test-Path -LiteralPath $receipt)) { throw 'Consumer produced no success receipt.' }

        # A green probe must also prove its runtime assertion can fail, without publishing
        # a receipt for the wrong runtime. Reuse the host; do not build another fixture.
        $wrongMajor = if ($RuntimeMajor -eq 8) { 10 } else { 8 }
        $wrongReceipt = Join-Path $workPath 'wrong-runtime-receipt.json'
        if (Test-Path -LiteralPath $wrongReceipt) { Remove-Item -LiteralPath $wrongReceipt }
        $wrongLog = Join-Path $workPath 'wrong-runtime.log'
        & $RuntimeDotnet (Join-Path $consumerOutput 'Consumer.dll') $artifactPath "$wrongMajor" $wrongReceipt *> $wrongLog
        if ($LASTEXITCODE -ne 1 -or (Test-Path -LiteralPath $wrongReceipt) -or
            -not (Select-String -LiteralPath $wrongLog -Pattern 'runtime-mismatch:' -Quiet)) {
            throw 'The wrong-runtime control did not refuse as expected.'
        }
        Write-Host 'PASS: wrong runtime expectation refused without a receipt.'
    }
}
finally {
    Pop-Location
}

# The expected refusal exits 1; do not leak that status to a calling CI shell.
exit 0
