# Ashlar integrator guide

This guide is for teams embedding Ashlar, extending bricks, or hosting custom background agents alongside the core platform.

For **how all distribution channels fit together** (NuGet, HTTP, CLI, compose, source, mesh), **pinning**, and the **CI matrix** that validates each channel, see **`docs/DistributionModels.md`**.

## Getting started with the Ashlar SDK

The slim **HTTP client** package is the `Ashlar.Sdk` project (`src/Ashlar.Sdk/Ashlar.Sdk.csproj`). Register it with **`AddAshlarClientSdk(baseUrl, ...)`** (`Ashlar.Sdk.Client`). The obsolete **`AddAshlarSdk(string baseUrl, ...)`** name remains as a compat shim. For **host-side** brick/agent registration, use **`Ashlar.Hosting.Sdk.AddAshlarSdk`** (before `AddAshlar`). See [`docs/architecture/SdkStructure.md`](architecture/SdkStructure.md).

Add a project reference from your integrator assembly:

```xml
<ItemGroup>
  <ProjectReference Include="path/to/ashlar/src/Ashlar.Sdk/Ashlar.Sdk.csproj" />
</ItemGroup>
```

Build and test from the repository root. `Ashlar.Kernel.sln` is the kernel spine plus its tests (it also builds `Ashlar.API`, which the infrastructure tests host in-process); the CLI is a separate project and restores on first use. `Ashlar.sln` builds everything including the commercial satellites and is not needed for integration work:

```bash
dotnet build Ashlar.Kernel.sln
dotnet test path/to/YourIntegrator.Tests/YourIntegrator.Tests.csproj
```

`samples/hello-brick/HelloBrick.Tests/HelloBrick.Tests.csproj` is the smallest working example of that second line (a `ProjectReference` into `src/`, run from the repository root).

Use the Ashlar CLI for local validation and gates:

```bash
dotnet run --project application/src/Ashlar.CLI -- --help
dotnet run --project application/src/Ashlar.CLI -- validate
```

For container or native install paths, see `docs/GettingStarted.md` and `docs/OnboardingAutomation.md`.

## Building a custom brick

Start with **[`docs/AuthoringBricks.md`](AuthoringBricks.md)**, the authoritative code-brick authoring path. Bricks exchange structured payloads; shared DTOs and versioning live in **Ashlar.Brick.Contracts** (`src/Ashlar.Brick.Contracts`), while code-authored bricks derive from `Ashlar.Core.Domain.Bricks.Brick`.

Recommended steps:

1. Add `ProjectReference` to `Ashlar.Brick.Contracts`.
2. Define stable JSON-serializable request/response shapes aligned with the DTOs.
3. Register and exercise your brick through the host’s brick pipeline and existing OWASP sample (`Ashlar.Bricks.Owasp`) as a reference implementation.
4. Run `dotnet run --project application/src/Ashlar.CLI -- validate` before publishing.

## Building a custom background agent

Background agents are configured via JSON agent sets (see `apps/runtime-studio/config/agent_set.local.json` and the in-tree dogfood campaign set `docs/background-agents/examples/dogfood-campaign.json`). The extracted product is [ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager) (`config/agent_set.release_manager.json`). Each agent specifies `Role`, `ModelProvider`, `Commands`, `Schedule`, and `ExfiltrationPolicy`.

To run a custom set locally:

```bash
dotnet run --project application/src/Ashlar.CLI -- background-agent daemon --config path/to/your/agent_set.json
```

Match sensitivity and exfiltration settings to your deployment tier. For mesh-related peer lists used at runtime, see `ashlar mesh` commands and `ASHLAR_MESH_INSTANCES_PATH` if you relocate `instances.json`. To call a remote mesh **director** HTTP API from a headless host (CI, bare metal worker), use **commercial mesh director CLI** (`dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director ...`) with **`ASHLAR_MESH_DIRECTOR_BASE_URL`** and optional **`ASHLAR_MESH_MUTATING_TOKEN`** / **`ASHLAR_MESH_API_KEY`** — see **`docs/MeshPhase7EdgeAlignment.md`**. For a **prefab two-person hub** (Compose + API key defaults), see **`docs/FriendMeshPrefab.md`** and **`deploy/compose/docker-compose.friend-mesh.yml`**.

## Compatibility matrix

| Ashlar line | .NET / toolchain | Notes |
|-----------|------------------|--------|
| **Monorepo / source (`master`)** | **SDK 10.x** (`global.json`); **CLI** and hosts target **`net10.0`**; **API** and libraries multi-target **`net8.0;net10.0`**; tests use **`net8.0`** / **`net10.0`** (see **`docs/architecture/DotnetVersions.md`**) | You build from **`application/Ashlar.Application.sln`** or **`Ashlar.sln`**; no single “repo version” until you tag. |
| **Published NuGet + GHCR** | Same **semver** across packages and release images | Cut with **`docs/RELEASE.md`** / **`docs/RELEASE_RUNBOOK.md`**; consumer verify scripts in **`docs/PUBLISHING.md`** and **`docs/NuGetConsumerVerify.md`**. |

After each **tagged release**, add a row for that **semver** (packages + `nexo-cli` / `nexo-api` digest pins) so integrators can copy known-good pins. Keep **`docs/DistributionModels.md`** in sync when channels or golden paths change.

## Troubleshooting

- **CLI or doctor failures after dependency changes**: Run `dotnet run --project application/src/Ashlar.CLI -- doctor --fix --dry-run` to see planned remediation without executing commands, then `doctor --fix --yes` if appropriate.
- **Restore or build errors**: Confirm SDK versions in `global.json` and run `bash scripts/setup/setup.sh restore` from the repo root.
- **Mesh peer not visible**: Verify `instances.json` (default: `~/.ashlar/instances.json` or `ASHLAR_MESH_INSTANCES_PATH`) and use `ashlar mesh admit <peerId>` / `ashlar mesh revoke <peerId>` to toggle the `admitted` flag.
- **Release gate bundle**: Use `dotnet run --project application/src/Ashlar.CLI -- ci release-bundle --profile full` for an extended step list; reports land under `.ashlar/release-bundle/last-run/`.

For onboarding CI failure categories, the quickstart gate workflow runs `scripts/onboarding/failure-taxonomy.sh` on lane logs and publishes consolidated artifacts from the categorization job.
