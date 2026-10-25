# Verified brick consumer

This reference library delegates to `Ashlar.Infrastructure.Certification.VerifiedBrick`
to load a deployed brick from its source, certification record,
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

The sample uses checkout references. The loader itself ships in `Ashlar.Infrastructure`;
applications can use it directly without referencing this sample. For a verified-only
host registry and startup checks, see [verified production hosting](../../docs/VerifiedProductionHosting.md).

Run its integration tests from the repository root:

```sh
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net10.0 --filter FullyQualifiedName~VerifiedConsumerProdStyleTests
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 --filter FullyQualifiedName~VerifiedConsumerProdStyleTests
```

The tests deploy real files and check module identity, execution after DLL replacement,
pinning, tampering, signed identity mismatches, artifact bindings, and disposal.
