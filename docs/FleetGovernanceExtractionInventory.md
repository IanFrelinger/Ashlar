# Fleet and mesh governance extraction inventory

<!-- phantom-paths: historical-record -->

> **The file paths below are PRE-EXTRACTION locations.** Fleet (Phases C-E) and networking
> (Phase F) are complete, so most no longer exist at the path given - the code moved to
> `commercial/`. They are left as written because this document's job is to record what moved
> and from where; rewriting them to today's paths would destroy the record. The marker above
> tells `scripts/check-docs-phantom-paths.sh` to stop checking them, which is why 63
> stale-looking paths here are not a failing build.

This inventory guided fleet and networking extraction. **Fleet (Phases C–E) and networking (Phase F) are complete** on `master`; remaining items are optional CLI splits and governance modules. It classifies fleet, mesh, and networking code before moving it so the commercial boundary stays reviewable.

Classification terms:

- **OPEN primitive** — single-node, inspectable, local-first, or trust primitive. May stay Apache-2.0.
- **COMMERCIAL fleet/governance** — multi-node control plane, fleet scheduling, director persistence, leases/checkpoints, knowledge replication, worker execution, or org-scale governance. Should move to commercial modules before commercial licensing is applied.
- **SPLIT / owner decision** — a file mixes open primitive behavior with fleet-scale behavior or needs an owner call on packaging.

## Summary recommendation

1. Keep low-level mesh primitives open: peer descriptors, local instance discovery/advertisement, artifact negotiation, local transport, and trust configuration primitives.
2. Extract all `Fleet` projects/files into commercial contracts/core/infrastructure modules.
3. Extract `Networking` knowledge-sync / network-bus / adaptive-cache surfaces into commercial fleet/governance modules unless the owner explicitly wants a smaller open networking substrate.
4. Split CLI/API surfaces so open CLI/API retain local mesh inspection and trust middleware, while `mesh director`, fleet registration/admission, and director operations move to commercial modules.
5. Move fleet/mesh governance tests with the commercial code they validate.

## Proposed commercial modules

| Module | Purpose |
|--------|---------|
| `commercial/src/Ashlar.Commercial.Fleet.Contracts` | Fleet DTOs and ports that commercial infrastructure and control-plane hosts consume. |
| `commercial/src/Ashlar.Commercial.Fleet.Core` | Task placement, fleet trust policy, elastic scheduling, lease/checkpoint policy, knowledge replication orchestration. |
| `commercial/src/Ashlar.Commercial.Fleet.Infrastructure` | LiteDB registries, worker executor client/background service, director persistence, import/export implementations, sweep/rebalance services. |
| `commercial/src/Ashlar.Commercial.Fleet.Api` | Commercial `/api/mesh` fleet/task/knowledge endpoint extension seeded from open `Ashlar.API`. |
| `commercial/src/Ashlar.Commercial.Fleet.Host` | Commercial operator host wiring `AddAshlarCommercialFleetDirector()` and `MapCommercialFleetEndpoints()`. |
| `commercial/src/Ashlar.Commercial.MeshDirector` | Mesh director CLI/API/control-plane operations and HTTP client surfaces. |
| `commercial/src/Ashlar.Commercial.Governance` | Future RBAC/SSO, centralized policy management, aggregate tamper-evident audit, org-scale entitlements. |
| `commercial/tests/Ashlar.Commercial.Tests.Fleet` | Fleet/director/governance tests moved out of open test projects. |

## OPEN primitive inventory

These files should remain open unless a later extraction finds unavoidable fleet-only semantics.

### Core mesh contracts and models

| Path | Reason |
|------|--------|
| `src/Ashlar.Core.Application/Mesh/Models/Artifact.cs` | Portable artifact descriptor for local/import/export flows. |
| `src/Ashlar.Core.Application/Mesh/Models/CapabilityDescriptor.cs` | Inspectable capability metadata used by local nodes and SDK consumers. |
| `src/Ashlar.Core.Application/Mesh/Models/InstanceCapabilities.cs` | Local instance capability snapshot; useful without fleet control plane. |
| `src/Ashlar.Core.Application/Mesh/Models/MeshOptions.cs` | Local mesh configuration primitive. |
| `src/Ashlar.Core.Application/Mesh/Models/PeerInfo.cs` | Peer descriptor used by discovery and local operator inspection. |
| `src/Ashlar.Core.Application/Mesh/MeshTrustPolicyConfiguration.cs` | Trust primitive; trust rules should remain inspectable/open. |
| `src/Ashlar.Core.Application/Mesh/Ports/IArtifactNegotiator.cs` | Local artifact negotiation primitive. |
| `src/Ashlar.Core.Application/Mesh/Ports/ICapabilityAdvertisement.cs` | Local capability advertisement primitive. |
| `src/Ashlar.Core.Application/Mesh/Ports/ICapabilityFulfiller.cs` | Local capability fulfillment abstraction. |
| `src/Ashlar.Core.Application/Mesh/Ports/ICapabilityRequester.cs` | Request-side abstraction for open mesh primitives. |
| `src/Ashlar.Core.Application/Mesh/Ports/IInstanceCapabilitiesProvider.cs` | Local capability provider. |
| `src/Ashlar.Core.Application/Mesh/Ports/IInstanceDiscovery.cs` | Local/discoverable peer primitive. |
| `src/Ashlar.Core.Application/Mesh/Ports/ILocalTransport.cs` | Local transport primitive. |

### Infrastructure mesh primitives

| Path | Reason |
|------|--------|
| `src/Ashlar.Infrastructure/Mesh/ArtifactNegotiator.cs` | Implements open artifact negotiation. |
| `src/Ashlar.Infrastructure/Mesh/FileBasedCapabilityAdvertisement.cs` | Local file-based advertisement; inspectable and useful for single-node labs. |
| `src/Ashlar.Infrastructure/Mesh/FileBasedInstanceDiscovery.cs` | File-based discovery primitive. |
| `src/Ashlar.Infrastructure/Mesh/FileBasedLocalTransport.cs` | Local transport primitive. |
| `src/Ashlar.Infrastructure/Mesh/LocalAshlarInstanceCapabilitiesProvider.cs` | Local capability snapshot provider. |
| `src/Ashlar.Infrastructure/Mesh/MeshCapabilityFulfiller.cs` | Capability fulfillment primitive. |
| `src/Ashlar.Infrastructure/Mesh/MeshCapabilityRequester.cs` | Capability requester primitive. |
| `src/Ashlar.Infrastructure/Mesh/Sdk/Extensions/MeshServiceCollectionExtensions.cs` | Open DI entrypoint for primitive mesh services. |

### Open CLI/API mesh and trust surfaces

| Path | Reason |
|------|--------|
| `application/src/Ashlar.CLI/Commands/MeshCommand.cs` | Keep local `lan`, `capabilities`, `sync`, import/export, and local trust-tier edits open. Split only if future owner decision makes `sync` a commercial fleet feature. |
| `application/src/Ashlar.API/Security/MeshCorrelationApplicationBuilderExtensions.cs` | Correlation ID middleware is an audit/trust primitive. |
| `application/src/Ashlar.API/Security/MeshCorrelationMiddleware.cs` | Correlation ID middleware is an audit/trust primitive. |
| `application/src/Ashlar.API/Security/MeshSecurityApplicationBuilderExtensions.cs` | Token/body/rate-limit middleware registration should remain inspectable. |
| `application/src/Ashlar.API/Security/MeshSecurityMiddleware.cs` | Mesh/brick-execute protection is a trust primitive. |
| `application/src/Ashlar.API/Security/MeshSecurityOptions.cs` | Open security configuration options. |

### Open tests that should remain open

| Path | Reason |
|------|--------|
| `src/Ashlar.Tests.Infrastructure/Tests/Mesh/ArtifactNegotiatorTests.cs` | Tests open artifact negotiation primitive. |
| `src/Ashlar.Tests.Infrastructure/Tests/Mesh/FileBasedCapabilityAdvertisementTests.cs` | Tests open local advertisement primitive. |
| `src/Ashlar.Tests.Infrastructure/Tests/Mesh/FileBasedInstanceDiscoveryTests.cs` | Tests open local discovery primitive. |
| `application/src/Ashlar.Tests.CLI/Tests/Commands/MeshCommandTests.cs` | Tests open CLI mesh primitive behavior. |

## COMMERCIAL fleet/governance inventory

These files should move to commercial modules.

### Fleet contracts and models

Target module: `commercial/src/Ashlar.Commercial.Fleet.Contracts`.

| Path | Target / reason |
|------|-----------------|
| `src/Ashlar.Core.Application/Fleet/Models/MeshFleetNodeState.cs` | Fleet node lifecycle model. |
| `src/Ashlar.Core.Application/Fleet/Models/MeshFleetTrustTier.cs` | Fleet placement trust tier, not primitive trust policy. |
| `src/Ashlar.Core.Application/Fleet/Models/MeshKnowledgeExportPayload.cs` | Fleet knowledge replication payload. |
| `src/Ashlar.Core.Application/Fleet/Models/MeshTaskCreateSpec.cs` | Fleet task creation DTO. |
| `src/Ashlar.Core.Application/Fleet/Models/MeshTaskState.cs` | Fleet task state DTO. |
| `src/Ashlar.Core.Application/Fleet/Models/MeshTaskStatus.cs` | Fleet task lifecycle enum. |
| `src/Ashlar.Core.Application/Fleet/Ports/IFleetNodeRegistry.cs` | Fleet registry port. |
| `src/Ashlar.Core.Application/Fleet/Ports/IMeshTaskPlacementService.cs` | Commercial placement port. |
| `src/Ashlar.Core.Application/Fleet/Ports/IMeshTaskRegistry.cs` | Fleet task registry port. |

### Fleet infrastructure and director persistence

Target module: `commercial/src/Ashlar.Commercial.Fleet.Infrastructure`.

| Path | Target / reason |
|------|-----------------|
| `src/Ashlar.Infrastructure/Fleet/FleetServiceCollectionExtensions.cs` | Commercial DI entrypoint. |
| `src/Ashlar.Infrastructure/Fleet/InMemoryFleetNodeRegistry.cs` | Fleet registry implementation. |
| `src/Ashlar.Infrastructure/Fleet/InMemoryMeshTaskRegistry.cs` | Fleet task registry implementation. |
| `src/Ashlar.Infrastructure/Fleet/LiteDbFleetNodeRegistry.cs` | Director/fleet persistence. |
| `src/Ashlar.Infrastructure/Fleet/LiteDbMeshDirectorDocuments.cs` | Director persistence documents. |
| `src/Ashlar.Infrastructure/Fleet/LiteDbMeshTaskRegistry.cs` | Fleet task persistence. |
| `src/Ashlar.Infrastructure/Fleet/MeshCheckpointOptions.cs` | Lease/checkpoint policy. |
| `src/Ashlar.Infrastructure/Fleet/MeshElasticSchedulingOptions.cs` | Commercial elastic scheduling. |
| `src/Ashlar.Infrastructure/Fleet/MeshFleetRegistrationKeys.cs` | Fleet registration key management. |
| `src/Ashlar.Infrastructure/Fleet/MeshFleetRegistrationOptions.cs` | Fleet registration configuration. |
| `src/Ashlar.Infrastructure/Fleet/MeshFleetTrustPolicy.cs` | Fleet trust-tier placement policy. |
| `src/Ashlar.Infrastructure/Fleet/MeshKnowledgeExportService.cs` | Fleet knowledge export. |
| `src/Ashlar.Infrastructure/Fleet/MeshKnowledgeImportService.cs` | Fleet knowledge import. |
| `src/Ashlar.Infrastructure/Fleet/MeshLeaseSweepBackgroundService.cs` | Lease management. |
| `src/Ashlar.Infrastructure/Fleet/MeshPeerKnowledgePullBackgroundService.cs` | Peer knowledge replication. |
| `src/Ashlar.Infrastructure/Fleet/MeshPeerKnowledgeSyncOptions.cs` | Peer knowledge replication options. |
| `src/Ashlar.Infrastructure/Fleet/MeshPendingTaskRebalancerBackgroundService.cs` | Commercial task rebalancing. |
| `src/Ashlar.Infrastructure/Fleet/MeshPersistenceOptions.cs` | Fleet persistence options. |
| `src/Ashlar.Infrastructure/Fleet/MeshPlacementTrustOptions.cs` | Fleet placement trust options. |
| `src/Ashlar.Infrastructure/Fleet/MeshTaskExecutionService.cs` | Fleet task execution. |
| `src/Ashlar.Infrastructure/Fleet/MeshTaskPlacementService.cs` | Fleet task placement implementation. |
| `src/Ashlar.Infrastructure/Fleet/MeshLab/MeshLabWorkerExecutorBackgroundService.cs` | Fleet worker loop for lab/operator use. |
| `src/Ashlar.Infrastructure/Fleet/MeshLab/MeshLabWorkerExecutorClient.cs` | Fleet worker executor client. |
| `src/Ashlar.Infrastructure/Fleet/MeshLab/MeshLabWorkerExecutorOptions.cs` | Fleet worker executor configuration. |

### Networking / knowledge sync

Target module: `commercial/src/Ashlar.Commercial.Fleet.Contracts` for ports/models and `commercial/src/Ashlar.Commercial.Fleet.Infrastructure` for HTTP/in-memory implementations unless the owner defines a smaller open networking primitive.

| Path | Target / reason |
|------|-----------------|
| `src/Ashlar.Core.Application/Networking/Models/AdaptiveBrickCacheStats.cs` | Fleet/adaptive cache metric. |
| `src/Ashlar.Core.Application/Networking/Models/BrickUsageRecord.cs` | Cross-node usage tracking. |
| `src/Ashlar.Core.Application/Networking/Models/BrickUsageStats.cs` | Cross-node usage stats. |
| `src/Ashlar.Core.Application/Networking/Models/KnowledgeChunk.cs` | Knowledge replication payload. |
| `src/Ashlar.Core.Application/Networking/Models/KnowledgeSyncStatus.cs` | Knowledge replication status. |
| `src/Ashlar.Core.Application/Networking/Models/NetworkAgentEntry.cs` | Network agent directory entry. |
| `src/Ashlar.Core.Application/Networking/Models/NetworkEvent.cs` | Network bus event. |
| `src/Ashlar.Core.Application/Networking/Models/NetworkEventTypes.cs` | Network bus event types. |
| `src/Ashlar.Core.Application/Networking/Models/PlasticityMetrics.cs` | Adaptive/fleet plasticity metric. |
| `src/Ashlar.Core.Application/Networking/Ports/IAdaptiveBrickCache.cs` | Cross-node adaptive cache. |
| `src/Ashlar.Core.Application/Networking/Ports/IBrickUsageTracker.cs` | Cross-node usage tracking. |
| `src/Ashlar.Core.Application/Networking/Ports/IKnowledgeChunkStore.cs` | Knowledge replication store. |
| `src/Ashlar.Core.Application/Networking/Ports/IKnowledgeSyncService.cs` | Knowledge sync service. |
| `src/Ashlar.Core.Application/Networking/Ports/INetworkAgentDirectory.cs` | Network/fleet directory. |
| `src/Ashlar.Core.Application/Networking/Ports/INetworkBus.cs` | Network bus. |
| `src/Ashlar.Core.Application/Networking/Ports/INetworkNegotiationService.cs` | Cross-node negotiation. |
| `src/Ashlar.Core.Application/Networking/Ports/IPlasticityService.cs` | Adaptive plasticity service. |
| `src/Ashlar.Infrastructure/Networking/HttpKnowledgeSyncService.cs` | HTTP knowledge sync implementation. |
| `src/Ashlar.Infrastructure/Networking/HttpNetworkAgentDirectory.cs` | HTTP network directory implementation. |
| `src/Ashlar.Infrastructure/Networking/HttpNetworkBus.cs` | HTTP network bus implementation. |
| `src/Ashlar.Infrastructure/Networking/InMemoryKnowledgeChunkStore.cs` | Knowledge chunk store implementation. |
| `src/Ashlar.Infrastructure/Networking/KnowledgeSyncServiceOptions.cs` | Commercial knowledge sync options. |
| `src/Ashlar.Infrastructure/Networking/NetworkAgentDirectoryOptions.cs` | Commercial network directory options. |
| `src/Ashlar.Infrastructure/Networking/NetworkBusOptions.cs` | Commercial network bus options. |
| `src/Ashlar.Infrastructure/Networking/NetworkNegotiationService.cs` | Cross-node negotiation implementation. |
| `src/Ashlar.Infrastructure/Networking/PlasticityOptions.cs` | Commercial plasticity options. |
| `src/Ashlar.Infrastructure/Networking/PlasticityService.cs` | Adaptive plasticity implementation. |

### CLI/API control plane surfaces

| Path | Classification |
|------|----------------|
| `commercial/src/Ashlar.Commercial.MeshDirector/MeshDirectorCommand.cs` | **COMMERCIAL** — direct client for fleet director API. The open CLI duplicate has been removed after mesh-lab scripts/operator packaging moved to the commercial module. |
| ~~`application/src/Ashlar.CLI/Commands/MeshHubCommand.cs`~~ | **Resolved:** removed; open **`mesh peers`** / **`mesh health`**; commercial **`director list-nodes`** / **`director health`**. |

### Fleet tests to move commercial

Target module: `commercial/tests/Ashlar.Commercial.Tests.Fleet`.

| Path group | Target / reason |
|------------|-----------------|
| `src/Ashlar.Tests.Infrastructure/Tests/Fleet/*.cs` | Move all fleet registry, placement, persistence, lease/checkpoint, knowledge replication, worker executor, and trust policy tests with fleet code. |
| `commercial/tests/Ashlar.Commercial.Tests.MeshDirector/MeshDirectorCommandUriTests.cs` | **COMMERCIAL** — URI-building tests for the commercial mesh director client. The open CLI duplicate has been removed. |

## SPLIT / owner decision inventory

| Path | Decision needed |
|------|-----------------|
| `application/src/Ashlar.CLI/Commands/MeshCommand.cs` | **Resolved:** `lan`, `capabilities`, `sync`, import/export, `peers`, `health`, `--set-trust-tier`, and local `admit`/`revoke` (instances.json) stay open. Fleet director ops use `Ashlar.Commercial.MeshDirector`. |
| ~~`MeshHubCommand`~~ | **Resolved** — see open `mesh peers`/`mesh health` vs commercial `director list-nodes`/`director health`. |
| `src/Ashlar.Tests.Infrastructure/Tests/Mesh/MeshLabDockerFixture.cs`, `MeshLabDockerEnv.cs`, `MeshLabDockerE2ETests.cs` | Virtual lab may remain open if it validates primitive two-node behavior; move commercial if it validates fleet worker/director placement. |
| `src/Ashlar.Tests.Infrastructure/Tests/Networking/NetworkBusOptionsTests.cs` | **Resolved by Phase F:** moved with commercial networking to `commercial/tests/Ashlar.Commercial.Tests.Fleet/Networking`. |
| `src/Ashlar.Tests.Infrastructure/Tests/Networking/InfrastructureNetworkingGapCoverageTests.cs` | **Resolved by Phase F:** moved with commercial networking to `commercial/tests/Ashlar.Commercial.Tests.Fleet/Networking`. |

## Updated extraction sequence

### PR 4 — fleet inventory split

This document. No code moves.

### PR 5 — commercial fleet contracts/core

Create the commercial contracts module and seed it with copied fleet contract DTOs/ports while open consumers are still being migrated:

- `commercial/src/Ashlar.Commercial.Fleet.Contracts`
- Seed from `src/Ashlar.Core.Application/Fleet/**`
- Keep the original open files temporarily until commercial infrastructure/API consumers move in later PRs.

Then move:

- commercial-classified `src/Ashlar.Core.Application/Networking/**` ports/models, if owner confirms those are not part of a smaller open networking substrate.

### PR 6 — commercial fleet infrastructure

Create the commercial fleet infrastructure module and seed it with copied fleet implementation code while open consumers are still being migrated:

- `commercial/src/Ashlar.Commercial.Fleet.Infrastructure`
- Seed from `src/Ashlar.Infrastructure/Fleet/**`
- Keep the original open files temporarily until commercial API/CLI consumers move in later PRs.

Then move:

- commercial-classified `src/Ashlar.Infrastructure/Networking/**`
- fleet tests from `src/Ashlar.Tests.Infrastructure/Tests/Fleet/**`

Create:

- `commercial/src/Ashlar.Commercial.Fleet.Infrastructure`
- `commercial/tests/Ashlar.Commercial.Tests.Fleet`

### PR 7 — commercial mesh director / CLI surface

Seed commercial module and then move or split:

- `MeshDirectorCommand` (commercial module owns this now)
- ~~commercial portions of `MeshHubCommand`~~ (done: `director list-nodes` / `director health`)
- related URI tests (commercial module owns these now)

Create:

- `commercial/src/Ashlar.Commercial.MeshDirector`

### PR 8 — commercial fleet API baseline

Seed commercial endpoint module:

- `commercial/src/Ashlar.Commercial.Fleet.Api`
- copy `/api/mesh/fleet/**`, `/api/mesh/tasks/**`, and `/api/mesh/knowledge/**` endpoint mappings/handlers/DTOs from open `Ashlar.API`;
- use commercial fleet contracts/infrastructure namespaces.

### PR 9 — commercial fleet host wiring (done)

Added:

- `commercial/src/Ashlar.Commercial.Fleet.Host` — operator host registering `AddAshlarCommercialFleetDirector()` and mapping `MapCommercialFleetEndpoints()`;
- `commercial/src/Ashlar.Commercial.Fleet.Api/CommercialFleetHostExtensions.cs` — shared DI/endpoint wiring helper;
- `commercial/tests/Ashlar.Commercial.Tests.Fleet.Host` and `scripts/commercial-fleet-host-smoke.sh` — host build/smoke validation;
### PR 10 — open fleet endpoint cleanup (done)

Completed:

- removed open `/api/mesh/fleet/**`, `/api/mesh/tasks/**`, and `/api/mesh/knowledge/**` endpoint handlers from `Ashlar.API`;
- migrated mesh-lab **peer-a** to `.docker/Dockerfile.fleet-host` (`Ashlar.Commercial.Fleet.Host`);
- mesh-lab verify scripts unchanged (same HTTP paths; director is now the commercial host).

### PR 11 — open fleet infrastructure cleanup (done)

Completed:

- removed `src/Ashlar.Core.Application/Fleet/**` and `src/Ashlar.Infrastructure/Fleet/**`;
- extracted mesh-lab worker executor to `src/Ashlar.Infrastructure/MeshLab/**` (open HTTP client only);
- moved fleet unit tests to `commercial/tests/Ashlar.Commercial.Tests.Fleet`;
- kept mesh-lab worker executor tests under `src/Ashlar.Tests.Infrastructure/Tests/MeshLab/**`.

### PR 12 — dependency-boundary scanner (done)

Added:

- `scripts/verify-open-commercial-dependency-boundary.py` — classifies projects, scans `ProjectReference` edges, checks `COMMERCIAL-LICENSE.md`, and evaluates open packable `PackageLicenseExpression`;
- `scripts/dependency-boundary-gate.sh` — strict wrapper (`DEPENDENCY_BOUNDARY_STRICT=1`);
- `scripts/dependency-boundary.open-to-commercial.allowlist.txt` — documented exception list (empty by default);
- `.github/workflows/dependency-boundary.yml` — CI gate on `.csproj` / licensing path changes;
- `make dependency-boundary-gate`.

## Validation for this inventory PR

- Markdown link check.
- Docs lint.
- No source or workflow changes.
