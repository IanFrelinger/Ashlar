# Verified brick consumer

This reference library loads a deployed brick from its source, certification record,
and gate-emitted DLL. The caller supplies the expected brick ID and a policy containing
trusted Ed25519 public keys. An unpinned policy is rejected before any deployment file
is read.

```csharp
using Ashlar.Certification.Contracts;
using Ashlar.Core.Domain.Execution;
using Ashlar.Samples.VerifiedBrickConsumer;

var policy = CertificationTrustPolicy.FromTrustedKeys(trustedPublicKeys);
using var consumer = await VerifiedConsumer.LoadFromFilesAsync(
    expectedBrickId, sourcePath, recordPath, assemblyPath, policy, hmacKey);
var output = await consumer.ExecuteAsync(
    new BrickInput(), new Ashlar.Infrastructure.Execution.ExecutionContext());
```

`trustedPublicKeys` comes from the operator's trust configuration, not the record being
verified. `hmacKey` is the deployment's existing HMAC verification key; the consumer
does not need the certifier's Ed25519 private key. The current record format requires
both HMAC and Ed25519 verification.

The consumer checks the expected record identity, verifies the source and DLL with
`policy.Strict`, and requires exactly one signed artifact binding naming the brick
type. It applies `IlImportFence` before loading. A collectible `AssemblyLoadContext`
loads the same byte array that was verified and resolves the named type from that
assembly. It checks the constructed brick's ID before returning it for execution.
The source is never recompiled, and the DLL is never read a second time.

`ArtifactSha256` exposes the signed hash of the loaded bytes. `Dispose` releases the
brick and requests unloading; execution after disposal is refused. Finish pending
executions before disposing. Dependencies use the host's default load context, so
the host supplies the compatible Ashlar contracts. An assembly load context is not
a process sandbox.

The project uses checkout references, including `Ashlar.Infrastructure` for its
existing IL import fence. This is a source integration sample, not a separately
published NuGet package or evidence of package-feed compatibility.

Run its integration tests from the repository root:

```sh
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 --filter FullyQualifiedName~VerifiedConsumerProdStyleTests
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 --filter FullyQualifiedName~VerifiedConsumerProdStyleTests
```

The tests deploy real files and check module identity, execution after DLL replacement,
pinning, tampering, signed identity mismatches, artifact bindings, and disposal.

For runtime portability, use the [same-artifact probe](../../tools/VerifiedArtifactPortability/README.md).
It builds and signs one .NET 8 fixture, then consumes those unchanged bytes on actual
.NET 8, 9, and 10 runtimes across Windows, Linux, and macOS. It checks runtime and
module identity explicitly; the integration tests above compile a new fixture per
run and their test host permits major runtime roll-forward.
