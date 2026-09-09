# Testing model

**North-star strategy:** [Testing strategy pivot v1](TestingStrategyPivot-v1.md) (layered gates, domain 100%, ProdStyle for wiring, mesh/RC for environment). **Progress:** [Testing strategy tracking v1](TestingStrategyTracking-v1.md).

Ashlar uses **two complementary styles** of automated tests.

## xUnit (VSTest)

Projects such as `Ashlar.Tests.Application` and `Ashlar.Tests.Infrastructure` use **xUnit** directly. They are the primary targets for `dotnet test` in CI matrices (`FullyQualifiedName` filters, TRX loggers, etc.).

## Framework tests (`UnitTestBase` + `ITestRunner`)

Some suites inherit **`UnitTestBase`** (which extends **`TestBase`**) and implement **`ExecuteAsync`**. The infrastructure **`TestRunnerAdapter`** discovers those types by reflection and runs them; the **CLI** uses **`ITestRunner`** for the same pipeline.

**`UnitTestFrameworkBridge`** (in `Ashlar.Infrastructure`) runs each concrete `UnitTestBase` through that runner. Test assemblies **`Ashlar.Tests.Domain`**, **`Ashlar.Tests.Application`**, **`Ashlar.Tests.Infrastructure`**, and **`Ashlar.Tests.CLI`** each expose **`UnitTestBridgeTests`** (xUnit theory) so `dotnet test` on those projects executes the same framework suites as the CLI. Infrastructure discovery skips **`SimpleTestForRunner`** (helper for **`TestRunnerAdapterTests`**), **`DependencyWrappingArchitectureTests`** (requires the **`copy-assemblies`** layout; run it via the full **`Ashlar.Tests.Infrastructure`** test flow or filtered `dotnet test` after that target), and **`DoctorCommandTests`** (expects repo layout / host health; run via **`dotnet test`** with filter **`FullyQualifiedName~DoctorCommandTests`** or the CLI integration lane).

## Production-like tests (`Category=ProdStyle`)

Use **`[Trait("Category", "ProdStyle")]`** on xUnit classes that mirror production wiring: **`AddAshlar`**, **`AddRunPodCapabilityRouting`**, adaptation/composition stacks, Forge HTTP surfaces, capability routing, barriers, etc. **`UnitTestBridgeTests`** in Application / Domain / Infrastructure / CLI is tagged so every **`UnitTestBase`** suite participates. **`Ashlar.Commercial.Tests.GameDomain`** and **`Ashlar.Tests.Transport`** use **`[assembly: AssemblyTrait("Category", "ProdStyle")]`** so the full assembly runs under **`Category=ProdStyle`**.

**NCR virtual routing (`VirtualProductionNcrRoutingHost`):** production **`RunPodHttpClient`** against an in-process **`RunPodLoopbackApiServer`** (REST-compatible shim), **`ProviderFactory`** local execution, **`EnvironmentHardwareProfiler`**, **`FileBasedInstanceDiscovery`** — see **`docs/NcrReleaseSLOs.md`**.

**Virtual API stack (`FrameworkVirtualProdDemosTests`):** **`WebApplicationFactory&lt;Program&gt;`** spins up **`Ashlar.API`** in-process (environment **`Testing`** → **`appsettings.Testing.json`**, background-agent **`IHostedService`** off). Requests hit the **same** minimal API endpoints as production — no fake route handlers. This matches **`docs/demos/`** (`GET /api/status`, **`AshlarClient`**). These sources compile **only for `net10.0`** in **`Ashlar.Tests.Infrastructure`** (the 8.0 ASP.NET Core TestHost + **`WriteAsJsonAsync`** hits the known **`PipeWriter.UnflushedBytes`** incompatibility with System.Text.Json 9+; the 10.0 TestHost adds the override).

Run **before** lighter smoke (`BaseFrameworkSmokeTests`) and full matrices — see **`make test-prod-style`**, **`make test-prime-time`** (**`Ashlar.PrimeTime.slnf`**), **`make test-framework-prod-first`**, and **`ashlar ci verify`** (ProdStyle runs after Infrastructure build, before smoke).

## Docker mesh virtual lab (multi-container HTTP)

For **real bridge networking**, heterogeneous **`Ashlar.API`** images, and per-role auth (API key, Bearer, Basic), use the **virtual mesh lab** — it complements in-process **`WebApplicationFactory`** tests:

| Style | What it proves | How to run |
|-------|----------------|------------|
| **`WebApplicationFactory`** | Single-process API, full DI graph, route delegates | `dotnet test` — **`FrameworkVirtualProdDemosTests`**, **`ApiDevelopmentHostDiTests`** |
| **Docker mesh lab** | Compose DNS, published ports, cross-container HTTP, mesh director placement + lease lifecycle, optional worker tier | **`make mesh-lab-e2e`** or **`make mesh-lab-e2e-workers`**; CI: **`mesh-lab-gate.yml`** |
| **dotnet mesh lab (optional)** | Same bash verify scripts as CI, orchestrated by **`MeshLabDockerFixture`** | **`ASHLAR_RUN_MESH_LAB=1`** + **`make test-mesh-lab`** or **`--filter Category=MeshLab`** |

See **[`docs/MeshVirtualLab.md`](../MeshVirtualLab.md)** for compose layout, verify scripts (**`mesh-lab-verify.sh`**, optional **`mesh-lab-verify-deep.sh`**), and stress ramp. Requires **Docker** and **`python3`** on the host running verify scripts.

## Merge policy (GitHub)

Today `master` branch protection requires **four** checks: **`cert-gate`**, **`build-core`**, **`shell-lint`**, and **`lychee (README + docs)`** (see [CI gate inventory](../CiGateInventory.md)); the coverage gates run on PRs that touch kernel paths but do not block merges. To block merges when kernel line coverage regresses, in GitHub go to **Settings → Branches → Branch protection rule**, enable **Require status checks to pass**, and add **`domain-coverage`** (Core domain coverage) and **`kernel-coverage`** (composite gate as enforced by `scripts/ci/kernel-coverage-gate.sh`: Domain 100%, Infrastructure 80%, Core.Application 67%). Both workflows are path-filtered, so give each an always-report job first or the required context will never appear on PRs outside those paths. See [Coverage gates v1](../production-readiness/CoverageGates-v1.md). If the UI only shows the workflow name, pick the check that corresponds to each workflow’s latest green run.

## Local commands

- **Framework suites via xUnit:**  
  `dotnet test src/Ashlar.Tests.Domain/Ashlar.Tests.Domain.csproj`  
  `dotnet test src/Ashlar.Tests.Application/Ashlar.Tests.Application.csproj`  
  `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj`  
  `dotnet test application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj`
- **Coverage floors:** `make kernel-coverage-gate` or `bash scripts/ci/kernel-coverage-gate.sh`
- **Broader local bar:** `make test` (see `Makefile`; uses blame-hang options)
- **Production-like integration first (Infrastructure only):** `make test-prod-style` then optionally `make test-framework-prod-first`
- **Prime-time gate (all test projects in `Ashlar.PrimeTime.slnf`):** `make test-prime-time` — **`Category=ProdStyle`** across Application, Domain, Infrastructure, CLI, Orchestration, BackgroundAgents, GameDomain, Transport; then **`make test-prime-time-full`** for the full slice.
- **CI-style verification:** `make ci-verify` or `dotnet run --project application/src/Ashlar.CLI -- ci verify`
