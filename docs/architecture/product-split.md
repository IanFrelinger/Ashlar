# Framework vs product split

Ashlar is the **framework**. The extractable product scaffolds that lived in
this repository (the SecureWorkstation IDE daemon, the cluster engine, the
hosted control plane, and the native host) were parked on the
`archive/parked-2026-10-03` branch on 2026-10-03, because nothing on the
certified-change path used them. Guard, Forge, and Mesh Exchange are products
of the same kind and were never in-tree. This document is the placement rule
for new code.

## Rule

If a type defines how an **arbitrary** Ashlar workload executes, certifies,
routes, or is verified, it belongs in the framework (`src/`). If it is
user-facing product UX, a tenant, billing, an installer, or a deployment of one
of those products, it belongs in a product tree: its own repository, or
`products/` restored from the archive branch once a product has a consumer.

```text
ashlar-cloud  →  ashlar-cluster  →  ashlar (this repo's src/)
ashlar-workstation  →  ashlar
ashlar-native  →  ashlar
```

Ashlar must never reference a product project. Products consume framework
packages only. The parked scaffolds took `ProjectReference`s to
`Ashlar.Hosting` and/or `Ashlar.Contracts` (workstation: both; cluster and
native: Contracts; cloud: none, so the scaffold could not invert the split).
`Ashlar.Client` is the remote HTTP consumer package — not a required product
dependency. The dependency-boundary gate still rejects `src/` → `products/`
and non-test `src/` → `application/` `ProjectReference`s, and still refuses a
`products/ashlar-cloud` reference into `src/` or `commercial/`, so a restored
scaffold is held to the same rule. The existing exception is
`Ashlar.Tests.Infrastructure` hosting `Ashlar.API` in-process.

## Stay in this repository (framework)

- Agent, brick, tool, workflow, and pipeline contracts
- Execution-envelope and result-evidence schemas (`Ashlar.Contracts.Distributed`)
- Local / peer / cluster routing interfaces
- Certification, signatures, provenance
- Artifact manifests and verification
- Sandbox abstractions
- Durable task lifecycle **ports** (`ITaskScheduler`) — not a Kubernetes scheduler
- Native execution **ports** (`INativeExecutionHost`) — not a `dlopen` plugin loader
- Deployment profiles, including `SecureWorkstation`
- MCP / A2A / gRPC adapters
- Local model and RAG abstractions

`AirGapped` is a slim offline profile. It is **not** the workstation profile:
it excludes trust, background agents, RAG, and observation.
`SecureWorkstation` keeps those local capabilities and still excludes runtime
transport (gRPC remote execution). Cloud providers remain opt-in via
trust/provider configuration, not a profile kill-switch.

Do not set `ASHLAR_DEPLOYMENT_PROFILE=air-gapped` (or `airgapped` /
`air_gapped`) expecting an IDE workstation. Use `secure-workstation`,
`workstation`, or `secure_workstation`, or call
`AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation, o => o.TrustEnabled = true)`.
A host that only sets the env var still needs `ASHLAR_TRUST_ENABLED=1` (or
`TrustEnabled = true`) because the profile registers trust services but does
not enable them by itself.

MCP **client** and A2A (client and server) refuse to enable under both
profiles. Local MCP **server** stays allowed on `SecureWorkstation` for an IDE
stdio tool surface (MCP over HTTP is refused there since SPEC-007 PR 4.10); it stays forbidden on `AirGapped`. Profile aliases are
parsed by one linked helper (`AshlarDeploymentProfileEnvironment`) so hosting
and protocol assemblies cannot drift. (`AddAshlarWorkstation()`, which
re-asserted the profile and `TrustEnabled=true` after any caller `configure`
callback, was the workstation scaffold's helper and is parked with it.)
`AddAshlar` records the resolved profile so MCP/A2A validators (including the
MCP server AirGapped refusal) honor that value even when the env var is unset.
Underscore aliases (`secure_workstation`, `air_gapped`) parse the same as
hyphenated ones.

Envelope, evidence, native-manifest, and scheduled-handle factories reject
blank ids, undefined enums, malformed digests, and non-positive budgets.

A product that comes back should stay in this monorepo until its consumer
shape is stable, then extract following the
[release-manager](https://github.com/IanFrelinger/ashlar-release-manager)
pattern. The future GitHub homes named in the parked product READMEs were never
created.

## Parked product trees (`archive/parked-2026-10-03`)

Removed from master on 2026-10-03, with `products/Ashlar.Products.sln` and the
`products-gate` workflow. Restore with
`git checkout archive/parked-2026-10-03 -- products/`.

| Tree | Future repo | Consumes | Ships |
|------|-------------|----------|-------|
| `products/ashlar-workstation` | `ashlar-workstation` | `SecureWorkstation`, IDE contracts | Daemon UX, VS Code extension, installers |
| `products/ashlar-cluster` | `ashlar-cluster` | `ITaskScheduler`, envelopes | Scheduler, GPU workers, k8s |
| `products/ashlar-cloud` | `ashlar-cloud` | Cluster protocol + org/billing stubs | Hosted control-plane stubs (orgs, quotas, billing); OIDC planned |
| `products/ashlar-native` | `ashlar-native` | `INativeExecutionHost` | WASM / out-of-process workers |

Existing in-repo surfaces that would move with those products if they return:

- `extensions/ashlar-vscode/` → workstation
- `application/src/Ashlar.API` IDE endpoints → workstation host or stay as the
  open single-node API
- `commercial/` Fleet / MeshDirector → cluster overlay or a commercial repo
  (not moved here)

## Native code

The kernel hot-loads **managed** assemblies in an `AssemblyLoadContext` and
rejects P/Invoke in `IlImportFence`. Generated native code must not be
`dlopen`ed into the IDE or API process. Use WebAssembly or an out-of-process
worker via `INativeExecutionHost`.

## See also

- [`runtime-vs-application.md`](runtime-vs-application.md)
- [`../ProjectTiers.md`](../ProjectTiers.md)
- [`../OpenCoreBoundary.md`](../OpenCoreBoundary.md)
- [`KernelPhaseMatrix.md`](KernelPhaseMatrix.md)
