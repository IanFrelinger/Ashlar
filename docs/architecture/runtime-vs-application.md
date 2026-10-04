# Runtime vs application boundary

This monorepo keeps **kernel/runtime libraries** under `src/` and **open application surfaces** under `application/src/` (CLI, HTTP API). The commercial fleet-governance tier (Fleet, MeshDirector) lives under **`commercial/`**. The extractable product scaffolds that lived under `products/` were parked on the `archive/parked-2026-10-03` branch on 2026-10-03 (see [`product-split.md`](product-split.md)). The former plural `applications/` layer and game vertical moved out in the 2026-08-31 native-responsibility slim (archive branch `archive/verticals-2026-08-31`). After extraction, those products consume published packages from their own repositories. The same split matters when you consume Ashlar as **NuGet packages** from another repository.

## Layout in this repository

| Location | Role |
|----------|------|
| `src/` | Execution kernel: abstractions, core, hosting library, infrastructure, orchestration, runtime, agents, transport/protocol adapters (gRPC, MCP, A2A), ingress adapters, and tests for the kernel graph |
| `application/src/` | Open product hosts: `Ashlar.API`, `Ashlar.CLI`, plus `Ashlar.Tests.CLI` |
| `commercial/` | Commercial fleet-governance tier: Fleet, MeshDirector, and their tests |

## Runtime layer (NuGet / embeddable graph)

Use **`Ashlar.Runtime.sln`** at the repository root to build and validate only the kernel graph (no `application/` projects).

Conceptually **runtime** includes:

| Concern | Projects (examples) |
|--------|---------------------|
| Contracts | `Ashlar.Abstractions`, `Ashlar.Brick.Contracts`, `Ashlar.Contracts` |
| Domain rules | `Ashlar.Core.Domain` |
| Application ports and use cases | `Ashlar.Core.Application` |
| Policy primitives | `Ashlar.Policies`, `Ashlar.Policies.Dev` |
| Agent execution and transport | `Ashlar.Runtime`, `Ashlar.Transport.Grpc`, `Ashlar.Transport.Grpc.Server` |
| Default adapters | `Ashlar.Infrastructure` |
| Orchestration | `Ashlar.Orchestration` |
| Background agents (library) | `Ashlar.BackgroundAgents` |
| Tooling for agents | `Ashlar.Tools.Assembly`, `Ashlar.Tools.Dev` |
| SMS ingress (optional) | `Ashlar.Ingress.AwsSns`, `Ashlar.Ingress.DynamoDb` and their test projects |

**Composition helper (still a library, not your app):**

- **`Ashlar.Hosting`** — `AddAshlar` and DI wiring. Many hosts consume it from NuGet; strict splits may reimplement registration in the application repo.

## NuGet metapackages

| Package | Purpose |
|---------|---------|
| `Ashlar.Runtime.Bundle` | Kernel libraries without `Ashlar.Hosting` (compose DI yourself or add `Ashlar.Hosting` separately). |
| `Ashlar.Hosting.Bundle` | Kernel plus `Ashlar.Hosting` for turnkey embedding. |

Version all packages from the same release (same `PackageVersion` when packing). Application-specific deployment (API container, CLI publish) can reference paths under `application/src/` or consume published `Ashlar.API` / `Ashlar.CLI` packages after pack.

## Application layer

Product-specific deployables and descriptors stay under **`application/src/`** (CLI/API) in this repo. If you maintain a **separate** product repository, keep private integrations, bespoke DTOs, and composition overrides there and reference the runtime packages above. The kernel must never take a `ProjectReference` to `products/`. Non-test kernel projects must not reference `application/` (`Ashlar.Tests.Infrastructure` hosts `Ashlar.API` in-process).

## Solution files

| File | Purpose |
|------|---------|
| `Ashlar.Runtime.sln` | Runtime kernel graph for CI and publishing libraries (and `Ashlar.Runtime.Bundle`). |
| `application/Ashlar.Application.sln` | Open `application/src/*` only (`Ashlar.API`, `Ashlar.CLI`, `Ashlar.Tests.CLI`); it no longer pulls the commercial GameDomain projects. |
| `Ashlar.sln` | Full monorepo: kernel, clients, infrastructure tests, `Ashlar.Runtime.Bundle`, ingress projects, `application/` hosts, plus commercial MeshDirector and Fleet/MeshDirector tests. It does **not** include samples, spikes, tools, or the removed Game Director / GameDomain vertical. Application code is built via **`dotnet build application/Ashlar.Application.sln`** when you only need the CLI/API hosts. |

Build application layer:

```bash
dotnet build application/Ashlar.Application.sln
```

`Ashlar.Kernel.sln` (when present) is a mid-sized kernel-focused subset for workflows that do not need the entire `Ashlar.sln` graph.

## See also

- **`docs/DistributionModels.md`** — how external hosts and operators consume Ashlar (NuGet, HTTP, CLI, compose, mesh) and which **CI gates** prove each path.
