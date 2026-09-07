# SDK Integration Guide

This guide covers building external integrations against the stable Ashlar SDK surface. For packaging, pinning, and per-channel CI validation, see **`docs/DistributionModels.md`**.

## Stability Tiers

| Tier | Meaning | Breaking-change policy |
|------|---------|----------------------|
| **Stable** | Public API, versioned, covered by compatibility tests | Deprecation notice + 1 minor version migration window |
| **Experimental** | May change without notice; marked with `[Obsolete("Experimental")]` | No guarantee |
| **Internal** | Implementation detail; do not depend on from external code | May change at any time |

See `docs/SdkCompatibilityPolicy.md` for the full versioning policy.

## Stable Public Surface

### Registration

```csharp
services.AddAshlarSdk(sdk => sdk
    .RegisterBrick<MyBrick>()
    .RegisterAgent<MyAgent>()
    .RegisterAgentCard(new AgentCard { ... }));

services.AddAshlar(options =>
{
    options.StrictMode.Enabled = true; // fail-fast during development
});
```

### Key Abstractions

| Interface | Package | Purpose |
|-----------|---------|---------|
| `IModel` | `Ashlar.Abstractions` | LLM execution |
| `IProviderFactory` | `Ashlar.Infrastructure` | Provider selection and execution |
| `IConfigurationService` | `Ashlar.Core.Application` | Configuration management |
| `IPatternStore` | `Ashlar.Core.Application` | Pattern storage for adaptation |
| `IKnowledgeQueryService` | `Ashlar.Core.Application` | Cross-store knowledge queries |
| `ICopilotTaskStore` | `Ashlar.Core.Application` | Copilot task persistence |

### Extension Points

- **Bricks:** Implement `Brick` (domain behavior unit). Register via `sdk.RegisterBrick<T>()`.
- **Agents:** Implement `IAgent` and provide an `AgentCard`. Register via `sdk.RegisterAgent<T>()` + `sdk.RegisterAgentCard(...)`.
- **Background agents:** Configure via agent set JSON. In-tree examples: `apps/runtime-studio/config/agent_set.local.json` and the dogfood campaign set `docs/background-agents/examples/dogfood-campaign.json`. The extracted release-manager vertical lives at [ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager).

## Reference Integration

A complete working sample lives in `docs/samples/StableSdkHostSample/`:

```bash
cd docs/samples/StableSdkHostSample
dotnet build
dotnet run
# Output: "Stable SDK host sample bootstrapped successfully."
```

This sample:
- Registers a custom brick and agent using only stable APIs
- Bootstraps the full Ashlar kernel with `AddAshlar()`
- Does not depend on internal namespaces

## Reference Integration Archetypes

### 1. CLI Tool Extension

Build a standalone CLI tool that uses Ashlar for code analysis:

```csharp
services.AddAshlarSdk(sdk => sdk.RegisterBrick<MyAnalyzerBrick>());
services.AddAshlarProfile(AshlarDeploymentProfile.System); // minimal
```

### 2. Background Service

Embed Ashlar agents in a long-running service:

```csharp
services.AddAshlar(opts =>
{
    opts.RegisterBackgroundAgentHostedService = true;
    opts.DeploymentProfile = AshlarDeploymentProfile.Server;
});
```

### 3. Offline and workstation profiles

Run Ashlar with no cloud connectivity. `AirGapped` is the slim profile (no
trust, agents, or observation). For an IDE / workstation daemon that still
needs local trust and agents, use `SecureWorkstation` instead (or
`products/ashlar-workstation` `AddAshlarWorkstation()`).

```csharp
services.AddAshlarProfile(AshlarDeploymentProfile.AirGapped, opts =>
{
    opts.StrictMode.Enabled = true;
    // TrustEnabled=true is a no-op here: AirGapped does not register trust services.
});
// Set ASHLAR_ALLOW_MOCK=1 or use Ollama locally

services.AddAshlarProfile(AshlarDeploymentProfile.SecureWorkstation, opts =>
{
    opts.TrustEnabled = true; // required: the profile registers trust services but does not enable them
});
// Or: services.AddAshlarWorkstation(); // re-asserts SecureWorkstation + TrustEnabled after configure
```

## CI Validation

The readiness gate builds the **project-reference** sample and then verifies **NuGet-only** consumption:

1. `dotnet build docs/samples/StableSdkHostSample/StableSdkHostSample.csproj` (in-repo references).
2. `scripts/verify-stable-sdk-host-sample-packages.sh` (POSIX) or `scripts/verify-stable-sdk-host-sample-packages.ps1` (Windows): packs the `Ashlar.Hosting` dependency graph to a local feed, restores `docs/samples/StableSdkHostSample/package-consumer/StableSdkHostSample.Package.csproj` with **`--force-evaluate`** and an **empty package cache** by default (avoids masking from `~/.nuget/packages`), builds, and runs.

See `.github/workflows/full-platform-readiness-gate.yml` (steps **Setup — build SDK sample** and **Setup — verify SDK sample consumes local NuGet graph**).

## Publishing `Ashlar.Hosting` for external repos

`Ashlar.Hosting` depends on other `Ashlar.*` projects; publish **the same `PackageVersion`** for the whole graph before pushing to a feed:

```bash
bash scripts/pack-ashlar-hosting-graph.sh 1.2.3 /path/to/output
```

Then push `*.nupkg` from that folder to **nuget.org** or **GitHub Packages**. External hosts reference **`Ashlar.Hosting`** only; NuGet resolves the matching versions of transitive `Ashlar.*` packages from the same feed.
