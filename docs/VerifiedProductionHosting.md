# Verified production hosting

`Ashlar.Infrastructure` now ships the verified consumer as `VerifiedBrick`. It requires pinned
Ed25519 keys before reading deployment files, verifies the source and emitted assembly against
the signed record, checks the import fence, and loads the same verified byte buffer. It does
not rebuild the source or reopen the DLL after verification. The original
[consumer sample](../samples/verified-brick-consumer/README.md) delegates to this implementation.

For a fixed deployment set, use `Ashlar.Hosting`:

```csharp
using Ashlar.Certification.Contracts;
using Ashlar.Hosting;
using Ashlar.Infrastructure.Certification;

var trust = CertificationTrustPolicy.FromTrustedKeys([trustedPublicKeyBase64]);
builder.Services.AddAshlarVerifiedBricks(
    [new VerifiedBrickDeployment("damage-resolver", sourcePath, recordPath, assemblyPath)],
    trust,
    hmacKey: deploymentHmacKey,
    configure: options => options.DeploymentProfile = AshlarDeploymentProfile.System);
```

Apply this after other Ashlar or SDK registrations. Startup verifies every deployment before
publishing the registry. An unpinned policy is rejected during registration; a tampered deployment
fails startup. An empty set is allowed and exposes no bricks. Duplicate IDs, including case-only
duplicates, are refused. The registry is unavailable before successful startup and after shutdown.
A later replacement of `IBrickRegistry` causes startup to fail instead of silently selecting the
replacement. The host owns and disposes the loaded contexts.

This entrypoint replaces the normal `IBrickRegistry` and removes the concrete raw `BrickRegistry`
registration. SDK-registered and built-in bricks are consequently unavailable through this
registry unless included as verified deployments. The raw-registry federation composition is
not supported by this fixed-deployment entrypoint. Existing `AddAshlar` and `RegisterBrick<T>`
retain their development and trusted-host behavior; they do not automatically verify deployments.

For applications that manage their own lifecycle, await
`VerifiedBrickRegistry.LoadFromFilesAsync(deployments, trust, hmacKey)` and dispose the returned
registry after all executions finish. Single-brick consumers can use
`VerifiedBrick.LoadFromFilesAsync` and `ExecuteAsync` directly.

The certification policy proves the recorded checks and signer identity for these exact bytes.
It does not prove arbitrary model outputs correct, grant network isolation, or sandbox hostile
host code. The host controls configuration, dependency injection, authentication, and signer
trust. Do not accept these settings or deployment paths from model-generated tool arguments.
Disposal requests collectible-context unloading; callers retaining brick objects or executing
them concurrently can keep a context alive. Finish work before shutting down.

`VerifiedConsumerProdStyleTests` covers signed binding, pinning, tampering, import refusal, and
execution after disk replacement through the public loader. `VerifiedHostingProdStyleTests`
runs the actual project loader and certification gate, starts the host with its emitted artifact,
and checks registration isolation, execution, shutdown, and tampered startup refusal. The gate
currently compiles against its host runtime references; this test does not establish that an
artifact emitted on .NET 10 can execute on .NET 8.

The [packaged deployment smoke](samples/VerifiedDeploymentConsumer/README.md) repeats the actual
gate-to-host flow from an outside-checkout consumer using only NuGet packages and fresh caches.
It runs in the native readiness matrix on Windows, Linux, and macOS and publishes its assertion
transcript and deployment evidence. This checks package consumption separately from source tests.
