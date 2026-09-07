# Consuming Ashlar from NuGet

Getting started **without a checkout**. Everything here is `PackageReference` against
[nuget.org](https://www.nuget.org/packages?q=Ashlar), version **`0.1.1`** — the first published
release (2026-09-01, Trusted Publishing/OIDC, SPDX SBOMs). Nothing on this page needs a private
feed, a token, or a clone of this repository.

Related pages: [`consumer-template/CONSUMING.md`](../consumer-template/CONSUMING.md) is the
copy-paste `nuget.config` + `Directory.Packages.props` pair; [`AuthoringBricks.md`](AuthoringBricks.md)
is the brick API; [`CertificationGate.md`](CertificationGate.md) is how you get an artifact
certified; [`Configuration.md`](Configuration.md) is the environment-variable reference and is
exact; [`IntegratorGuide.md`](IntegratorGuide.md) covers embedding inside a larger host.

## Which package do I need

| Package | You want it when |
|---------|------------------|
| `Ashlar.Brick.Contracts` | You are writing a brick. Brings `Brick`, `BrickInput`, `BrickOutput`, `IExecutionContext`. **The only package a certifiable brick project may need beyond `Ashlar.Authoring`.** |
| `Ashlar.Authoring` | You want `services.AddAshlarBrick<T>()` — one-call host registration for a brick. |
| `Ashlar.Hosting.Bundle` | You are embedding the kernel in your own process. A **metapackage** that pulls the whole `Ashlar.Hosting` graph at one version. Prefer it over referencing `Ashlar.Hosting` directly. |
| `Ashlar.Sdk` | You are calling a *remote* Ashlar over HTTP. Registers the client with `AddAshlarClientSdk(baseUrl, ...)`. |
| `Ashlar.Client` | `IAshlarClient` itself — transitive via `Ashlar.Sdk`; pin it explicitly only if you reference the type directly. |
| `Ashlar.Infrastructure` + `Ashlar.Certification.Contracts` | You are driving the certification gate yourself ([`CertificationGate.md`](CertificationGate.md)). Also pulled in by `Ashlar.Hosting.Bundle`. |
| `Ashlar.CLI` | A .NET tool, not a library: `dotnet tool install --global Ashlar.CLI --version 0.1.1`. See [`OperatorLifecycle.md`](OperatorLifecycle.md). |

`net8.0` and `net10.0` are both supported target frameworks for the library packages; the CLI tool
targets `net10.0`.

## The smallest working host

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <!-- Required if ANY ancestor directory contains a Directory.Packages.props.
         Without it, restore refuses the inline Version attributes below. -->
    <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Ashlar.Brick.Contracts" Version="0.1.1" />
    <PackageReference Include="Ashlar.Authoring"       Version="0.1.1" />
    <PackageReference Include="Ashlar.Hosting.Bundle"  Version="0.1.1" />
  </ItemGroup>
</Project>
```

```csharp
using Ashlar.Authoring;
using Ashlar.Hosting;
using Microsoft.Extensions.DependencyInjection;

var services = new ServiceCollection();

// Order matters: brick registration must run BEFORE AddAshlar().
services.AddAshlarBrick<MyBrick>();
services.AddAshlar(o => o.RegisterBackgroundAgentHostedService = false);

using var provider = services.BuildServiceProvider();
```

`RegisterBackgroundAgentHostedService = false` keeps a console app from starting the background-agent
scheduler on construction. Leave it at its default if background agents are the point of your host.

### Composition order

`AddAshlarBrick<T>()` (and `AddAshlarSdk(sdk => sdk.RegisterBrick<T>())`, the lower-level form) must
be called **before** `AddAshlar()`. `AddAshlar()` is the composition root: it reads what has been
registered so far and builds the kernel around it.

## A nuget.config, and when you need one

For plain nuget.org you need nothing. You need a `nuget.config` beside your solution when you are
mixing in a **local folder feed** — for example to test a version you packed yourself:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="ashlar-local" value="/absolute/path/to/feed" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
```

`<clear />` is load-bearing. Without it you inherit whatever sources are configured further up the
tree, and a package can silently resolve from nuget.org when you meant to test your local build (or
the reverse). Give the pre-release build a distinct version — `0.1.2-mine` rather than `0.1.1` — so
you can always tell which source served you.

## What you get from DI, and what you do not

After `AddAshlar()`, these resolve (verified from package-only consumers):

- `IBrickRegistry` — the registered bricks, including yours.
- `ICopilotTaskStore` — `LiteDbCopilotTaskStore`.
- `IDataDecisionAuditLog` — `DataDecisionAuditLog`.
- `ICertificationGate` — the certification gate ([`CertificationGate.md`](CertificationGate.md)).

**There is no HTTP surface in the packages.** `Ashlar.API` — the portal and every endpoint the
README advertises (`POST /api/copilot/task`, `GET /api/trust/dashboard`, `/api/activity/feed`) —
lives in `application/src/Ashlar.API` and **is not packed by any release workflow**; it ships as the
GHCR `nexo-api` container image. There is no `MapAshlar()` or `UseAshlar()` extension method. If your
host needs HTTP, map your own endpoints over the DI services above;
`scripts/verify-external-product-shape.sh` in this repository is a working reference host of exactly
that shape (`GET /health`, `POST /api/bricks/{id}/execute`).

## Environment variables you will actually reach for

The full reference is [`Configuration.md`](Configuration.md). The short list for a first host:

| Variable | Why you would set it |
|----------|----------------------|
| `ASHLAR_ALLOW_MOCK=1` | Fail-closed gate on the mock / offline / echo model providers. Required for any model-routed path when no Ollama or cloud provider is configured. **Not** needed for deterministic brick execution. |
| `ASHLAR_STATE_DIR` | Where LiteDB stores and snapshots live. Defaults to `<app root>/.ashlar/state`, so a `.ashlar/` directory appears next to your binary. |
| `ASHLAR_KEY_DIR` | Operator signing keys; defaults to `~/.ashlar/keys`. Only needed for manifest signing. |
| `ASHLAR_DEPLOYMENT_PROFILE` | `full` (default), `server`, `edge`, `air-gapped`, `system` — which modules `AddAshlar()` composes. |

## Writing a brick

Read [`AuthoringBricks.md`](AuthoringBricks.md) for the `Brick` API. Two constraints that only show
up once you try to **certify** the brick, and that are much cheaper to obey from the start:

1. The brick lives in **its own project**, with no `ProjectReference` to anything.
2. That project references **at most two** packages: `Ashlar.Brick.Contracts` and
   `Ashlar.Authoring`.

[`CertificationGate.md`](CertificationGate.md) explains why and what the refusal looks like.

To scaffold one, install the CLI as a tool and run `new brick`:

```bash
dotnet tool install --global Ashlar.CLI --version 0.1.1
ashlar new brick MyThing --output ./MyThing --ashlar-version 0.1.1
```

The scaffold emits a brick project plus an xUnit test project targeting `net8.0`. It does **not**
emit a `nuget.config`; on plain nuget.org that is fine, and with a local feed you must copy yours in
before restoring.

## Known restore snags

- **`NU1605` package downgrade.** The `0.1.1` graph pins `Microsoft.Extensions.*` at `10.0.11`. If
  your project explicitly references any `Microsoft.Extensions.*` below that, align your pin to
  `>= 10.0.11`.
- **`NU1101: Unable to find package Ashlar.Authoring`.** You pinned a version that is not on
  nuget.org — most often a `--ashlar-version` you invented, or a pre-release that only exists in a
  local feed you have not added.
- **Central package management errors** on inline `Version` attributes: set
  `<ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>`, or move the pins into
  `Directory.Packages.props` (see [`consumer-template/`](../consumer-template/CONSUMING.md)).

## Proof this works from packages alone

- [github.com/IanFrelinger/ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager)
  — an out-of-tree consumer whose CI restores from nuget.org and nothing else.
- `scripts/verify-external-product-shape.sh` / `-published.sh` and
  `scripts/consumer-surface-packages.txt` — the consumer surface this repository verifies on every
  release.

---

*The package roles, composition order, DI surface and environment variables on this page were
verified by package-only consumers building against a feed packed from this line. Version numbers
are the `0.1.1` nuget.org release. No build was run while writing this page.*
