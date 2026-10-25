# Verified artifact runtime portability

This probe builds one `net8.0` brick, signs its bytes once, and executes that same
deployment on actual .NET 8, 9, and 10 runtimes. CI repeats consumption on Linux,
Windows, and macOS. Consumers never reference or build the fixture project.

The fixture is built by MSBuild against .NET 8 reference assemblies and
`Ashlar.Brick.Contracts`. A separate .NET 10 producer verifies its target and
assembly references, then creates a **synthetic test certification record** using
the existing signing and export APIs. This tests compatibility of the verified
consumer, not admission by the full certification gate. The fresh Ed25519 private
key is never written to disk. The public key and a deliberately public test HMAC
key are test trust configuration, not a production key-distribution example.

Each consumer checks:

- Its actual runtime major/minor and loaded sample/contracts target frameworks.
  A .NET 9 host must use the sample and contracts' `net8.0` assets.
- The producer's SHA-256 and the loaded module's MVID.
- Execution and the returned value, including after replacing the on-disk DLL.
- Refusal of changed DLL/source bytes, missing or invalid Ed25519 signatures,
  and an untrusted signer, before loading a new fixture assembly.
- Preservation of the original downloaded artifact; mutations use scratch copies.

The host explicitly uses `RollForward=LatestPatch`. An installed newer major
cannot satisfy a missing runtime. A process-level version assertion also rejects
environment overrides that select the wrong runtime. The runner deliberately
passes an incorrect expected runtime as a negative control and requires refusal
without a success receipt.

## Run locally

Use PowerShell 7 (`pwsh`) on any supported operating system, the SDK selected by
`global.json`, and each runtime you want to test. From the repository root, choose
an empty deployment directory and scratch directories outside the checkout:

```powershell
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ashlar-portability-' + [Guid]::NewGuid().ToString('N'))
$artifact = Join-Path $scratch 'deployment'
./scripts/portability/verified-artifact-probe.ps1 -Mode Produce -ArtifactDirectory $artifact -WorkDirectory (Join-Path $scratch 'producer')
foreach ($major in 8, 9, 10) {
    ./scripts/portability/verified-artifact-probe.ps1 -Mode Consume -RuntimeMajor $major -ArtifactDirectory $artifact -WorkDirectory (Join-Path $scratch "net$major")
}
```

`-RuntimeDotnet /path/to/dotnet` optionally selects a separate runtime installation
for consumption; builds still use `dotnet` from PATH. `Produce` refuses to overwrite
a nonempty deployment directory. Consumers preserve the deployment and place logs,
runtime configuration, and `receipt.json` in their work directories.

## CI evidence and boundaries

The [workflow](../../.github/workflows/verified-artifact-portability.yml) uploads
one deployment from its producer. All nine consumer jobs download that artifact.
An aggregation job requires receipts for every OS/runtime pair and compares each
hash and MVID with the producer manifest. The workflow runs on relevant PR changes,
on the main branch, weekly, and by manual dispatch. It is advisory; it does not
change branch protection.

Receipts identify the runtime patch, OS, architecture, loaded target frameworks,
artifact hash, module ID, and successful checks. They establish the combinations
actually exercised by that run, not every architecture or every runtime patch.
.NET 10 is the primary forward-looking target; 8 and 9 are compatibility lanes,
not a promise to extend Microsoft's support lifecycle.

The host uses checkout project references. This is not a NuGet package compatibility
test, a dependency-bundling test, or a process sandbox. NativeAOT dynamic loading,
.NET Framework, Mono, Unity, browser WebAssembly, and mobile runtimes are outside
this matrix. Existing contracts-only portability probes cover different surfaces.
