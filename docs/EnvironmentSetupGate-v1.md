# Environment Setup Gate v1

This gate verifies that each supported host platform can bootstrap a working Ashlar development environment and restore required NuGet dependencies.

## Goal

Fail fast on environment drift before deeper functional gates run.

## Platforms

- `ubuntu-latest`
- `macos-latest`
- `windows-latest`
- Ephemeral Linux container:
  - `mcr.microsoft.com/dotnet/sdk:10.0`

The three host labels are the ones `.github/workflows/environment-setup-gate-v1.yml` uses, and they
float: they name whatever image GitHub points them at, not an OS version. On 2026-09-30 they resolved
to Ubuntu 24.04 (`ubuntu-24.04`), macOS 26 on arm64 (`macos-26-arm64`) and Windows Server 2025 with
Visual Studio 2026 (`windows-2025-vs2026`), read from the "Set up job" step of this repository's runs.
From 2026-10-19 GitHub starts moving `ubuntu-latest` to Ubuntu 26.04 (actions/runner-images#14748).
This gate is advisory and not pinned yet. The required readiness lanes that cover the same three
platforms (`full-platform-readiness-gate.yml`, "Readiness summary") are pinned to `ubuntu-24.04`,
`macos-26` and `windows-2025-vs2026`, and `scripts/ci/verify-required-runner-pins.py` refuses a
floating label in any required job.

Ephemeral container validation (Linux):

- `mcr.microsoft.com/dotnet/sdk:10.0` and `mcr.microsoft.com/dotnet/sdk:10.0-noble`

## Gate workflow

Workflow file: `.github/workflows/environment-setup-gate-v1.yml`

Each matrix job performs:

1. Setup .NET SDK (`10.0.x`, `8.0.x`).
2. Run platform setup dependency check:
   - Linux/macOS: `bash scripts/setup/setup.sh check`
   - Windows: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup\setup.ps1 -Mode check`
3. Run platform restore:
   - Linux/macOS: `bash scripts/setup/setup.sh restore`
   - Windows: `powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup\setup.ps1 -Mode restore`
4. Verify post-restore build readiness:
   - `dotnet build application/src/Ashlar.CLI/Ashlar.CLI.csproj --no-restore -v minimal`

Ephemeral container lanes perform:

1. Start a fresh container image for each run.
2. Execute:
   - `bash scripts/setup/setup-linux.sh check`
   - `bash scripts/setup/setup-linux.sh restore`
   - `dotnet build application/src/Ashlar.CLI/Ashlar.CLI.csproj --no-restore -v minimal`

Additionally, ephemeral Linux container jobs run in fresh containers and execute:

1. `bash scripts/setup/setup-linux.sh check`
2. `bash scripts/setup/setup-linux.sh restore`
3. `dotnet build application/src/Ashlar.CLI/Ashlar.CLI.csproj --no-restore -v minimal`

## Local usage

### Linux/macOS

```bash
bash scripts/setup/setup.sh check
bash scripts/setup/setup.sh restore
```

### Windows PowerShell

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup\setup.ps1 -Mode check
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup\setup.ps1 -Mode restore
```

## Pass criteria

- Dependency check completes with all required dependencies present.
- NuGet restore succeeds for the setup baseline project set:
  - `src/Ashlar.Core.Application/Ashlar.Core.Application.csproj`
  - `src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj`
  - `application/src/Ashlar.CLI/Ashlar.CLI.csproj`
  - `src/Ashlar.Tests.Infrastructure/scripts/copy-assemblies.csproj`
  - `src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj`
- CLI project builds with `--no-restore` after restore.
- Ephemeral container jobs pass for all configured Linux images.

## Failure criteria

- Any setup script command exits non-zero.
- Any restore step exits non-zero.
- Post-restore build fails.
