# Architecture overview

High-level maps of how Ashlar is structured. For day-to-day commands, see the repository root `README.md` and `CONTRIBUTING.md`.

| Document | Contents |
| -------- | -------- |
| [Trust and execution boundaries](TrustAndExecutionBoundaries.md) | Where trust is decided, how requests cross layers, and what runs locally vs. on peers. |
| [Testing model](TestingModel.md) | Relationship between xUnit tests, `UnitTestBase` / `ITestRunner`, and CI. |
| [Kernel phase matrix](KernelPhaseMatrix.md) | `AshlarKernelRegistrar` phases, module flags, and `make kernel-gate` proof. |
| [.NET SDK and target frameworks](DotnetVersions.md) | Why `global.json` pins SDK 10.x, hosts ship `net10.0`, and libraries keep `net8.0;net10.0`. |
| [Runtime vs application layout](runtime-vs-application.md) | `src/` kernel vs `application/src/` hosts vs in-repo `products/` scaffolds; `Ashlar.Runtime.sln`, `Ashlar.Runtime.Bundle`, NuGet metapackages, and packing scripts. |
| [Framework vs product split](product-split.md) | Placement rule for `src/` vs `products/`; `AirGapped` vs `SecureWorkstation`; cloud must not `ProjectReference` kernel. |
| [Protocol integration: MCP + A2A](ProtocolIntegration-MCP-A2A.md) | MCP server bridge over `ITool` (allowlists, policy gate, stdio host) and the planned MCP client / A2A phases. MCP client/A2A refuse AirGapped and SecureWorkstation; local MCP server stays allowed on SecureWorkstation. |
| [Shipping and consumption (all audiences)](../DistributionModels.md) | How Ashlar is packaged (NuGet, containers, HTTP); pinning; **distribution-matrix** CI jobs per channel. |
| [SDK-style layout](SdkStructure.md) | Ports vs options vs builders; `Ashlar.Hosting.Sdk` vs `Ashlar.Sdk.Client`; folder conventions. |
| [SDK migration plan (remaining gaps)](SdkMigrationPlan.md) | **Execution status** at top; **[Plan: close remaining gaps](SdkMigrationPlan.md#plan-close-remaining-gaps-post-migration)** (D1–D6: docs, sweep, consumers, optional `Sdk/Options`, hosting polish, CI clarity). |
| **`Ashlar.Framework.Sdk`** | Optional megaproject in `src/Ashlar.Framework.Sdk/` — `AddAshlarFramework` combines HTTP client + `AddAshlar`. |
| [GitHub Actions trigger policy](../../.github/workflows/README.md) | Manual-first workflow policy (`workflow_dispatch`); tag-driven release automation unchanged. |
