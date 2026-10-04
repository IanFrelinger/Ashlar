# Licensing and open-core boundary

Ashlar uses an open-core boundary:

- **Single-node + inspectable = OPEN (Apache-2.0).**
- **Fleet-scale + governance + vertical product packaging = COMMERCIAL.**
- **Trust primitives are always open.** Ashlar monetizes trust at the operational layer, not by hiding policy, audit, sanitization, or SDK primitives behind a paywall.

The repository root license is Apache-2.0. See [`LICENSE`](LICENSE).

## Covenants

The boundary is kept honest by these standing commitments. They are load-bearing for the
product's trust story; changing any of them is a breaking change to the project's social
contract, not a routine edit.

1. **The verify verb is free forever.** Anything Ashlar signs — certification verdicts,
   evidence bundles, `.ashpkg` seals, asset certificates — can be verified with
   Apache-2.0 tooling, offline, free, forever. If verifying ever costs money, every claim
   this project makes is void. (Commercial license files will join this list when license
   signing moves to Ed25519 — tracked in `docs/OpenCoreBoundary.md`; the current HMAC
   scheme's verification key would also be a signing key, so no public-verification claim
   is made for it yet.)
2. **The ratchet turns one way.** A capability may graduate commercial → open (see
   `apps/release-manager` and `apps/runtime-studio`, graduated 2026-08-31); a capability
   shipped open never moves behind the paywall. New commercial value comes from new
   fleet-scale capability, never from reclaiming open ground. Tier 1 stays Apache-2.0 —
   no future relicensing (BSL/SSPL-style) of anything already shipped open.
3. **Walls are architectural.** Commercial capability is a separate deliverable (separate
   projects, separate feed); open binaries never contain hidden capability a license key
   unlocks, and open capability is never gated behind a key. On nuget.org, `Ashlar.*` is
   Apache-2.0 by definition; `Ashlar.Commercial.*` is never published there. (Known seam,
   tracked in `docs/OpenCoreBoundary.md`: the open `Ashlar.API` host compiles in the
   default-off license-enforcement gate used by commercial deployments — it restricts
   nothing unless an operator enables it, its lapsed floor is pinned read-only in code,
   and extracting it into the commercial host is planned.)
4. **The buyer-based test decides placement.** A feature is commercial only when its
   natural buyer is an organization coordinating many nodes or many people (directors,
   aggregation, RBAC/SSO, org-wide policy, compliance packaging). Anything an individual
   operator of one node needs — including full self-extension and hub-less peer
   federation — is open.
5. **No commercial trust exceptions.** No paid component bypasses, weakens, or fast-lanes
   the admission gate. Paid buys scale and management, never a different answer from the
   gate. The safe posture (propose-and-hold, the second pair of eyes) is never paywalled.
6. **No telemetry in open packages.** Open code never phones home — no usage pings, no
   license checks that call out. Verifiable by reading the source, which air-gapped
   adopters are expected to do.
7. **Data freedom.** Evidence-bundle, ledger, and `.ashpkg` formats are documented open
   specs; commercial tools import and export them without proprietary lock-in, so leaving
   the commercial tier never strands your own governance history.

Extraction **Phases A–E** are complete (see [`docs/CommercialExtractionPlan.md`](docs/CommercialExtractionPlan.md)). The open/commercial project graph is enforced in CI by [`scripts/dependency-boundary-gate.sh`](scripts/dependency-boundary-gate.sh). Optional follow-up extractions (for example `src/**/Networking/**`) are classified in [`docs/FleetGovernanceExtractionInventory.md`](docs/FleetGovernanceExtractionInventory.md).

## Tier 1 — OPEN (Apache-2.0)

Tier 1 is the adoption and trust surface: SDKs, contracts, single-node runtime/hosts, trust primitives, tests, samples, and inspectable extension points. These projects carry Apache-2.0 package metadata either directly in their `.csproj` or through the repository-wide `Directory.Build.props` / `Directory.Build.targets` defaults (`PackageLicenseExpression=Apache-2.0` for non-commercial projects).

Rationale: this tier protects two moats at once — an extensible SDK and a single runtime across surfaces. `Ashlar.Policies`, `Ashlar.Policies.Dev`, and `Ashlar.Bricks.Owasp` are open on purpose: trust-by-design fails if the trust primitives are a paywalled black box.

| Allocation | Path |
|------------|------|
| OPEN | `src/Ashlar.Abstractions/Ashlar.Abstractions.csproj` |
| OPEN | `src/Ashlar.Contracts/Ashlar.Contracts.csproj` |
| OPEN | `src/Ashlar.Brick.Contracts/Ashlar.Brick.Contracts.csproj` |
| OPEN | `src/Ashlar.Analyzers/Ashlar.Analyzers.csproj` |
| OPEN | `src/Ashlar.Sdk/Ashlar.Sdk.csproj` |
| OPEN | `src/Ashlar.Framework.Sdk/Ashlar.Framework.Sdk.csproj` |
| OPEN | `src/Ashlar.Client/Ashlar.Client.csproj` |
| OPEN | `src/Ashlar.Core.Application/Ashlar.Core.Application.csproj` |
| OPEN | `src/Ashlar.Core.Domain/Ashlar.Core.Domain.csproj` |
| OPEN | `src/Ashlar.Runtime/Ashlar.Runtime.csproj` |
| OPEN | `src/Ashlar.Runtime.Bundle/Ashlar.Runtime.Bundle.csproj` |
| OPEN | `application/src/Ashlar.CLI/Ashlar.CLI.csproj` |
| OPEN | `application/src/Ashlar.API/Ashlar.API.csproj` |
| OPEN | `src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj` (includes open `MeshLab` worker executor) |
| OPEN | `src/Ashlar.Orchestration/Ashlar.Orchestration.csproj` |
| OPEN | `src/Ashlar.Adapters.Models/Ashlar.Adapters.Models.csproj` |
| OPEN | `src/Ashlar.BackgroundAgents/Ashlar.BackgroundAgents.csproj` |
| OPEN | `src/Ashlar.BackgroundAgents.HostRunners/Ashlar.BackgroundAgents.HostRunners.csproj` |
| OPEN | `src/Ashlar.Hosting/Ashlar.Hosting.csproj` |
| OPEN | `src/Ashlar.Hosting.Bundle/Ashlar.Hosting.Bundle.csproj` |
| OPEN | `src/Ashlar.Policies/Ashlar.Policies.csproj` |
| OPEN | `src/Ashlar.Policies.Dev/Ashlar.Policies.Dev.csproj` |
| OPEN | `src/Ashlar.Bricks.Owasp/Ashlar.Bricks.Owasp.csproj` |
| OPEN | `src/Ashlar.Compat/` (source-only compatibility/polyfill surface; no `.csproj`) |
| OPEN | `src/Ashlar.Tools.Assembly/Ashlar.Tools.Assembly.csproj` |
| OPEN | `src/Ashlar.Tools.Dev/Ashlar.Tools.Dev.csproj` |
| OPEN | `src/Ashlar.Ingress.AwsSns/Ashlar.Ingress.AwsSns.csproj` |
| OPEN | `src/Ashlar.Ingress.AwsSns.Tests/Ashlar.Ingress.AwsSns.Tests.csproj` |
| OPEN | `src/Ashlar.Ingress.DynamoDb/Ashlar.Ingress.DynamoDb.csproj` |
| OPEN | `src/Ashlar.Ingress.DynamoDb.Tests/Ashlar.Ingress.DynamoDb.Tests.csproj` |
| OPEN | `src/Ashlar.Transport.Grpc/Ashlar.Transport.Grpc.csproj` |
| OPEN | `src/Ashlar.Transport.Grpc.Server/Ashlar.Transport.Grpc.Server.csproj` |
| OPEN | `src/Ashlar.Transport.Grpc.Server.Host/Ashlar.Transport.Grpc.Server.Host.csproj` |
| OPEN | `src/Ashlar.Mcp.Server/Ashlar.Mcp.Server.csproj` |
| OPEN | `src/Ashlar.Mcp.Server.Host/Ashlar.Mcp.Server.Host.csproj` |
| OPEN | `src/Ashlar.Mcp.Server.Tests/Ashlar.Mcp.Server.Tests.csproj` |
| OPEN | `src/Ashlar.Mcp.Client/Ashlar.Mcp.Client.csproj` |
| OPEN | `src/Ashlar.Mcp.Client.Tests/Ashlar.Mcp.Client.Tests.csproj` |
| OPEN | `src/Ashlar.Transport.A2A/Ashlar.Transport.A2A.csproj` |
| OPEN | `src/Ashlar.Transport.A2A.Server/Ashlar.Transport.A2A.Server.csproj` |
| OPEN | `src/Ashlar.Transport.A2A.Tests/Ashlar.Transport.A2A.Tests.csproj` |
| OPEN | `src/Ashlar.Transport.A2A.Server.Tests/Ashlar.Transport.A2A.Server.Tests.csproj` |
| OPEN | `src/Ashlar.Tests.Application/Ashlar.Tests.Application.csproj` |
| OPEN | `src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj` |
| OPEN | `src/Ashlar.Tests.Contracts/Ashlar.Tests.Contracts.csproj` |
| OPEN | `src/Ashlar.Tests.Domain/Ashlar.Tests.Domain.csproj` |
| OPEN | `src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj` |
| OPEN | `src/Ashlar.Tests.Kernel/Ashlar.Tests.Kernel.csproj` |
| OPEN | `src/Ashlar.Tests.Orchestration/Ashlar.Tests.Orchestration.csproj` |
| OPEN | `src/Ashlar.Tests.Transport/Ashlar.Tests.Transport.csproj` |
| OPEN | `application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj` |
| OPEN | `docs/samples/NugetOrgRestoreHostingOnly/Ashlar.NugetOrgRestoreHostingOnly.csproj` |
| OPEN | `docs/samples/NugetOrgRestoreVerify/Ashlar.NugetOrgRestoreVerify.csproj` |
| OPEN | `docs/samples/StableSdkHostSample/StableSdkHostSample.csproj` |
| OPEN | `docs/samples/StableSdkHostSample/package-consumer/StableSdkHostSample.Package.csproj` |
| OPEN | `samples/**` |
| OPEN | `tools/**`, `spikes/**` — repo tools and evidence spikes, same rule |
| OPEN | Release Manager — graduated commercial → open 2026-08-31 (covenant 2), **extracted 2026-09-01** to [github.com/IanFrelinger/ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager) as the minimal SDK reference and first out-of-tree nuget.org consumer |
| OPEN | `apps/runtime-studio/` — single-node planner/worker agent-set config + docs; **graduated commercial → open 2026-08-31** (covenant 2). Single-node operator tooling belongs open by the headline rule; the open `setup all` lane already seeds from its config |

Open mesh **primitives** (local discovery, capability advertisement, trust middleware) remain under `src/Ashlar.Core.Application/Mesh/**` and `src/Ashlar.Infrastructure/Mesh/**`. Mesh-lab **workers** poll the commercial fleet director via open `src/Ashlar.Infrastructure/MeshLab/**`.

## Verify-then-place decisions

These projects were explicitly inspected and placed:

| Project | Decision | Reason |
|---------|----------|--------|
| `application/src/Ashlar.API/Ashlar.API.csproj` | OPEN | Single-node HTTP/API host over the open kernel. Fleet `/api/mesh/*` director endpoints live on `Ashlar.Commercial.Fleet.Host`, not open `Ashlar.API`. |
| `src/Ashlar.Transport.Grpc.Server/Ashlar.Transport.Grpc.Server.csproj` | OPEN | Server implementation exposes the open gRPC transport surface; it is not a fleet-scale director/control-plane project. |
| `src/Ashlar.Transport.Grpc.Server.Host/Ashlar.Transport.Grpc.Server.Host.csproj` | OPEN | Standalone gRPC host for the open transport server, not a governance tier. |
| `application/Ashlar.Application.sln` contents | OPEN | The solution contains `Ashlar.API`, `Ashlar.CLI`, and `Ashlar.Tests.CLI` only. It previously also listed `Ashlar.Commercial.GameDomain` and its tests "for local dev"; those were removed so the tester quickstart never compiles commercial code. `Ashlar.LocalDevCore.slnf` likewise pulls no `commercial/` project. The one filter that deliberately spans both tiers (`Ashlar.sln`) says so in `docs/ProjectTiers.md`. |

## Tier 2 — COMMERCIAL (fleet + governance)

Tier 2 is the commercial layer for fleet-scale and governance capabilities:

- mesh control plane / distributed execution,
- knowledge replication on the director,
- elastic scheduling,
- leases and checkpoints,
- data-plane federation,
- operator hardening,
- director persistence,
- centralized policy management (future `Ashlar.Commercial.Governance`),
- aggregated tamper-evident audit,
- RBAC/SSO and organization-scale governance.

**Current modules** (each directory has `COMMERCIAL-LICENSE.md` and `AshlarCommercialProject=true` in its `.csproj`):

| Module | Path |
|--------|------|
| Fleet contracts | `commercial/src/Ashlar.Commercial.Fleet.Contracts/` |
| Fleet infrastructure | `commercial/src/Ashlar.Commercial.Fleet.Infrastructure/` |
| Fleet API extensions | `commercial/src/Ashlar.Commercial.Fleet.Api/` |
| Fleet operator host | `commercial/src/Ashlar.Commercial.Fleet.Host/` |
| Mesh director CLI | `commercial/src/Ashlar.Commercial.MeshDirector/` |
| Fleet tests | `commercial/tests/Ashlar.Commercial.Tests.Fleet/`, `commercial/tests/Ashlar.Commercial.Tests.Fleet.Host/` |
| Mesh director tests | `commercial/tests/Ashlar.Commercial.Tests.MeshDirector/` |

Mesh-lab **peer-a** runs `Ashlar.Commercial.Fleet.Host` (`.docker/Dockerfile.fleet-host`). Open duplicate fleet trees under `src/**/Fleet/**` have been removed.

**Networking (Phase F, done):** knowledge-sync / network-bus / adaptive-cache surfaces live under `commercial/src/Ashlar.Commercial.Fleet.Contracts/Networking/**` and `commercial/src/Ashlar.Commercial.Fleet.Infrastructure/Networking/**` (namespaces are `Ashlar.Commercial.Fleet.Contracts.Networking.*` / `Ashlar.Commercial.Fleet.Infrastructure.Networking`). Register via `AddAshlarCommercialFleetNetworking()` on the commercial fleet host.

## Tier 3 — COMMERCIAL (verticals)

Tier 3 is the commercial product/vertical layer.

| Allocation | Path |
|------------|------|

Tier 3 is empty in this repository as of 2026-08-31: the Game Director / GameDomain /
Forge vertical (nine commercial projects, two `apps/` configuration surfaces, game data,
and their docs) was removed from the monorepo in the native-responsibility slim. The
runtime repository carries the platform and its own fleet-governance tier only; verticals
live in their own repositories consuming the published packages. The removed vertical is
preserved intact on the archive branch `archive/verticals-2026-08-31` for extraction, and
its commercial status travels with it. This removal is not a licensing event: nothing
open moved behind a paywall (covenant 2 is untouched).

`apps/release-manager/` (since extracted to its own repository, 2026-09-01) and `apps/runtime-studio/` were **graduated to Tier 1 OPEN on
2026-08-31** — the one-way ratchet (covenant 2) exercised in its trust-building direction.
Both are single-node agent-set configuration + docs with no fleet capability, so the
headline rule (single-node + inspectable = open) always applied to them; the Tier 3
listing was the contradiction, and this diff is its resolution. As part of the
graduation, the game-director run-mode config and launcher that lived under
`apps/runtime-studio/` moved to `apps/game-director/` (now on the archive branch with the
rest of the vertical), so the opened directories carry no Game Director material.

Each remaining commercial directory carries a `COMMERCIAL-LICENSE.md` with the in-force
text: all rights reserved except the evaluation grant below, contact for commercial terms,
lapsed-license behavior, and a pointer back to the covenants.

Commercial vertical projects may reference each other and the open core; open projects must not reference commercial projects.

### Evaluation use of commercial sources — IN FORCE since 2026-08-31

Source code under `commercial/` is made
visible for evaluation. You may build and run it locally, in CI on your own fork, and in
non-production test environments, solely to evaluate Ashlar. You may not deploy it in
production, offer it as a service, or redistribute it (in source or binary form) without a
separate written agreement. Forking this repository on GitHub, and modifying your fork,
for the evaluation uses permitted above is not redistribution for purposes of this
paragraph. The open core under `src/`, `application/`,
`apps/runtime-studio/`, `samples/`, `tools/`, `docs/`, and `spikes/` is Apache-2.0
and unaffected by this paragraph.

Production use, or any use beyond evaluation, requires a written commercial agreement:
contact **icfrelinger@gmail.com** with "Ashlar commercial" in the subject line.

**Lapsed-license behavior (commitment).** A commercial deployment whose license expires
degrades to read-only: it stops accepting new fleet configuration and new commercial-tier
operations. It never disables running nodes, never withholds or deletes already-recorded
evidence or audit history, and never changes an answer the admission gate would give.
Ashlar never ships a kill switch.

The former recommendation to open the release-manager app as the minimal SDK reference was
**executed on 2026-08-31** — it and `apps/runtime-studio` are now Tier 1 OPEN (see the
graduation note under Tier 3).

## Dependency-direction safety

Rule: no Tier 1 OPEN project may reference a Tier 2/3 COMMERCIAL project.

Enforced in CI by `scripts/dependency-boundary-gate.sh` (workflow: `.github/workflows/dependency-boundary.yml`). The scanner classifies projects by path and `AshlarCommercialProject`, fails on open→commercial `ProjectReference` edges, requires `COMMERCIAL-LICENSE.md` beside commercial `.csproj` files, and verifies open packable projects resolve `PackageLicenseExpression=Apache-2.0`.

Local verification:

```bash
make dependency-boundary-gate
```

## Open questions (post-extraction)

- ~~Should `src/**/Networking/**` move to commercial?~~ **Done (Phase F):** under `Ashlar.Commercial.Fleet.*`.
- ~~Should open `MeshHubCommand` split further?~~ **Done:** open `ashlar mesh peers` / `mesh health` (local probe); fleet `list-nodes` / director `health` on `Ashlar.Commercial.MeshDirector`.
- ~~Should `apps/release-manager` become the future minimal open SDK reference app?~~ **Done (2026-08-31):** graduated to Tier 1 OPEN together with `apps/runtime-studio`.

See [`docs/CommercialExtractionPlan.md`](docs/CommercialExtractionPlan.md) for the completed extraction sequence and validation gates.
