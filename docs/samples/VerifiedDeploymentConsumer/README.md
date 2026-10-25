# Packaged verified deployment consumer

This smoke checks the complete gate-to-host path from an outside NuGet consumer. It runs the real `BrickCertificationRun` on the repository's damage-resolver source and witness cases using a fresh Ed25519 key, then starts a host through `AddAshlarVerifiedBricks` and executes the exact gate-emitted module. It checks that replacing the DLL after load preserves execution of the verified bytes, while changed artifacts, changed source, foreign signers and unpinned policies refuse new startup.

After producing the hosting package graph, run from PowerShell 7 with a .NET 10 SDK/runtime:

```powershell
./scripts/verify-verified-deployment-packages.ps1 -Feed ./artifacts/nuget-local -Version 1.2.3 -KeepWorkspace
```

`Feed` and `Version` are required. The runner does not pack the graph. It copies the consumer and fixture to a new system temporary directory outside the checkout, uses fresh NuGet and CLI caches, and restricts `Ashlar.*` packages to the selected local feed. Public third-party dependencies restore from NuGet.org. The consumer has package references only; restored project references or different Ashlar package versions fail verification.

`-KeepWorkspace` retains the isolated project, packages, deployment and evidence. Failed runs always retain them. `-EvidenceDirectory <path>` creates a unique `run-<UTC timestamp>-<id>` child directory and prints its exact location. Each run copies only its own evidence there, including on failure; previous runs are retained unchanged. The native readiness jobs upload these directories recursively.

Each evidence directory contains `run-outcome.json` with a verification ID, package version, start/completion timestamps, phase and `RUNNING`, `PASS` or `FAIL` status. A forcibly interrupted process can leave `RUNNING`; it must not be treated as a pass. Successful consumers also produce `result.json`. Restore/build/run logs, the restored package inventory and the actual signed deployment are included when their phases finish. Generated private signing keys and the HMAC key are never written to disk, and the outcome manifest contains no exception text or environment values. The runner checks every named assertion in the transcript as well as the process exit code.

This sample establishes .NET 10 gate and packaged-host behavior for a freshly emitted artifact. It does **not** establish that those same DLL bytes execute on .NET 8, Mono, NativeAOT or every .NET runtime. Cross-runtime contract verification and a synthetic portable fixture are separate evidence; they do not imply portability of the gate's current reference set.
