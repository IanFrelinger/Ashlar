# Authoring Bricks

This is the authoritative entry point for writing **code-authored bricks** for Ashlar. For host setup and SDK registration context, see [`docs/sdk.md`](sdk.md) and [`docs/SdkIntegrationGuide.md`](SdkIntegrationGuide.md).

## What a brick is

A brick is a small unit of domain logic. Code-authored bricks derive from `Ashlar.Core.Domain.Bricks.Brick` and implement one method:

```csharp
Task<BrickOutput> ExecuteAsync(
    BrickInput input,
    ImplementationType implementation,
    IExecutionContext context,
    CancellationToken cancellationToken = default);
```

The constructor or init properties define metadata and the input/output contract:

- `Id`, `Name`, `Version`, `Category`, `Description`
- optional `Icon`, `DomainKnowledge`, `Metadata`
- `Interface` with `BrickInputDefinition` and `BrickOutputDefinition`
- optional `Implementations`, `DefaultImplementation`, `FallbackChain`, `Selector`

`ExecuteAsync` reads typed values from `BrickInput`, returns a `BrickOutput`, and should set a human-readable `Summary`.

## Minimal code brick

```csharp
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

public sealed class HelloBrick : Brick
{
    public HelloBrick()
    {
        Id = "sample.hello";
        Name = "Hello Brick";
        Version = "1.0.0";
        Category = BrickCategory.Transform;
        Description = "Returns a greeting for a supplied name.";
        Interface = new BrickInterface
        {
            Inputs = [new BrickInputDefinition("name", "string", "Name to greet", required: false, defaultValue: "world")],
            Outputs = [new BrickOutputDefinition("message", "string", "Greeting text")]
        };
    }

    public override Task<BrickOutput> ExecuteAsync(
        BrickInput input,
        ImplementationType implementation,
        IExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        var name = input.Get("name", "world") ?? "world";
        var output = new BrickOutput { Summary = $"Greeted {name}." };
        output.Set("message", $"Hello, {name}!");
        return Task.FromResult(output);
    }
}
```

## Registering a brick

Host applications register code bricks through the stable host SDK surface:

```csharp
using Ashlar.Hosting;
using Ashlar.Hosting.Sdk;

services.AddAshlarSdk(sdk => sdk.RegisterBrick<HelloBrick>());
services.AddAshlar();
```

At runtime, registered code bricks flow into the same `IBrickRegistry` surface used by Ashlar itself. The concrete registry composition includes local code bricks and may be wrapped by `CompositeBrickRegistry` when remote brick catalogs are configured.

For CLI/adaptation scenarios, `AddAdaptationBricks(typeof(HelloBrick))` is the lower-level equivalent. Prefer `AddAshlarSdk(...RegisterBrick<T>())` for application-host code.

`Ashlar.Authoring` adds `services.AddAshlarBrick<HelloBrick>()`, which is exactly `AddAshlarSdk(sdk => sdk.RegisterBrick<HelloBrick>())` behind a one-call name: use `AddAshlarBrick<T>()` when your project references `Ashlar.Authoring` (the `ashlar new brick` scaffold and [`consumer-template/CONSUMING.md`](../consumer-template/CONSUMING.md) do), and use `AddAshlarSdk(...RegisterBrick<T>())` directly when you only reference the `Ashlar.Hosting` graph or need to register several things in one `IAshlarSdkBuilder` callback. Both must run before `AddAshlar()`.

## Code bricks vs generated manifests

Ashlar also has an adaptive manifest path: `INewBrickGenerator.GenerateAsync(...)` creates a `BrickManifest` from observed patterns. That path is for runtime/adaptive discovery and promotion.

Use **code-authored bricks** when you want to ship source-controlled domain logic with tests and stable package/version ownership. Use the **manifest generator** when Ashlar is inferring a candidate brick from observed workflow patterns. Both paths describe the same conceptual brick surface; code bricks are the developer-authored, reviewable path.

## Packages on nuget.org

Published feed version is **`0.2.0`** (`ci/published-version`). Repo `VERSION` may already read ahead of that for an unpublished cut — do not treat `VERSION` as the public pin. `Ashlar.CLI`, `Ashlar.Authoring`, `Ashlar.Hosting`, and `Ashlar.Hosting.Bundle` restore from nuget.org at **0.2.0**. A checkout still works with `ProjectReference` (next section) if you do not want the feed.

## Primary path: `samples/hello-brick` (ProjectReference)

[`samples/hello-brick/`](../samples/hello-brick/) is the complete reference implementation and the path that works from a repository checkout with no extra setup. Its brick project references the domain model by **`ProjectReference`**, not by package (`samples/hello-brick/HelloBrick/HelloBrick.csproj`):

```xml
<ItemGroup>
  <ProjectReference Include="../../../src/Ashlar.Core.Domain/Ashlar.Core.Domain.csproj" />
</ItemGroup>
```

Run it from the repository root:

```bash
dotnet test samples/hello-brick/HelloBrick.Tests/HelloBrick.Tests.csproj
```

To start your own brick, copy `samples/hello-brick/` next to it (or anywhere inside the checkout), rename the projects, and keep the `ProjectReference` pointing at `src/Ashlar.Core.Domain/Ashlar.Core.Domain.csproj` (add `src/Ashlar.Authoring/Ashlar.Authoring.csproj` if you want `AddAshlarBrick<T>()` for host registration). One detail: the sample derives from `DomainBrick`, a `global using` alias for `Ashlar.Core.Domain.Bricks.Brick` that `samples/Directory.Build.props` injects **only** into the `HelloBrick` project name, so a renamed copy should derive from `Brick` directly (as the minimal example above does; if your own namespace starts with `Ashlar.` and you reference `Ashlar.Authoring`, the `Ashlar.Brick` namespace from `Ashlar.Brick.Contracts` shadows the short name, so write `Ashlar.Core.Domain.Bricks.Brick` or declare the same alias). The sample layout is:

- `HelloBrick/HelloBrick.csproj` — code-authored brick project.
- `HelloBrick/HelloBrick.cs` — `public sealed class HelloBrick : DomainBrick` (= `Brick`).
- `HelloBrick.Tests/HelloBrick.Tests.csproj` — xUnit test project.
- `HelloBrick.Tests/HelloBrickTests.cs` — smoke test for `ExecuteAsync`.

## Scaffold with the CLI (`ashlar new brick`)

`ashlar new brick <Name>` scaffolds a standalone brick project plus an xUnit test project from the template at [`samples/templates/brick/`](../samples/templates/brick/). The generated brick project references **`Ashlar.Authoring`** as a `PackageReference` (`<PackageReference Include="Ashlar.Authoring" Version="<cli version>" />`); that single package brings the authoring surface (`Brick`, `BrickInput`, `BrickOutput`, `IExecutionContext`, `IBrickExecutor`) and the `AddAshlarBrick<T>()` host registration helper. Use `--ashlar-version` to pin the generated reference to a specific package version:

```bash
ashlar new brick MyThing --ashlar-version 1.2.3
```

Because an unpublished CLI version may generate a `PackageReference` to a version that is not on the feed yet, restoring a freshly scaffolded project can fail with `NU1101` until you pin `--ashlar-version` to **`0.2.0`** (`ci/published-version`) or use a local folder feed (next section). Inside a checkout you can run the CLI without installing it:

```bash
dotnet run --project application/src/Ashlar.CLI -- new brick Hello --output /tmp/hello-brick --ashlar-version 9.9.9-local
```

## Restoring Ashlar.Authoring

Two options; the first is what CI verifies.

### Option 1: local folder feed (CI-verified)

[`scripts/verify-standalone-brick-authoring.sh`](../scripts/verify-standalone-brick-authoring.sh) packs the CLI and everything the generated brick needs into a folder feed, installs the CLI from that feed, scaffolds a brick, and restores it from the feed plus nuget.org. Run it end to end from the repository root:

```bash
bash scripts/verify-standalone-brick-authoring.sh
# ASHLAR_AUTHORING_VERIFY_VERSION (default 9.9.9-local) sets the pack/pin version;
# ASHLAR_AUTHORING_VERIFY_WORK (default: mktemp -d) sets the work dir so you can keep the output.
```

The commands it runs, transcribed (minus its `rg` guard against repo-relative paths leaking into the output), if you want to do it by hand (`ROOT` = repository root, `VERSION` = any semver such as `9.9.9-local`, `FEED` / `TOOL_PATH` / `BRICK_OUT` = empty scratch directories):

```bash
pack() {
  local project="$1"
  dotnet pack "${ROOT}/${project}" \
    -c Release \
    -o "${FEED}" \
    -p:PackageVersion="${VERSION}" \
    -p:IncludeTestProjectReferences=false \
    -v minimal
}

bash "${ROOT}/scripts/pack-ashlar-hosting-graph.sh" "${VERSION}" "${FEED}"
pack src/Ashlar.Adapters.Models/Ashlar.Adapters.Models.csproj
pack src/Ashlar.Bricks.Owasp/Ashlar.Bricks.Owasp.csproj
pack src/Ashlar.BackgroundAgents.HostRunners/Ashlar.BackgroundAgents.HostRunners.csproj
pack src/Ashlar.Policies.Dev/Ashlar.Policies.Dev.csproj
pack src/Ashlar.Authoring/Ashlar.Authoring.csproj
pack application/src/Ashlar.CLI/Ashlar.CLI.csproj

dotnet tool install \
  --tool-path "${TOOL_PATH}" \
  Ashlar.CLI \
  --version "${VERSION}" \
  --add-source "${FEED}" \
  --ignore-failed-sources

# --json is optional (machine-readable result; the script uses it)
"${TOOL_PATH}/ashlar" new brick SampleThing \
  --output "${BRICK_OUT}" \
  --ashlar-version "${VERSION}" \
  --json

dotnet restore "${BRICK_OUT}/SampleThingBrick.Tests/SampleThingBrick.Tests.csproj" \
  --source "${FEED}" \
  --source https://api.nuget.org/v3/index.json

dotnet build "${BRICK_OUT}/SampleThingBrick.Tests/SampleThingBrick.Tests.csproj" \
  --no-restore \
  -v minimal

dotnet test "${BRICK_OUT}/SampleThingBrick.Tests/SampleThingBrick.Tests.csproj" \
  --no-build \
  --blame-hang-timeout 120s \
  --blame-hang-dump-type none
```

The two things that matter for any brick you scaffold yourself: pass the packed version to `--ashlar-version` so the generated `PackageReference` matches what is in the feed, and give `dotnet restore` both `--source "${FEED}"` (for `Ashlar.*`) and `--source https://api.nuget.org/v3/index.json` (for xunit, FluentAssertions and the rest).

### Option 2: switch the scaffold to a ProjectReference

If the brick lives inside (or next to) a Ashlar checkout, replace the package reference in the generated `<Name>Brick.csproj`:

```xml
<!-- before -->
<PackageReference Include="Ashlar.Authoring" Version="9.9.9-local" />
<!-- after: path relative to the generated project -->
<ProjectReference Include="../../src/Ashlar.Authoring/Ashlar.Authoring.csproj" />
```

and restore normally. This is the same shape as `samples/hello-brick` (which references `src/Ashlar.Core.Domain` directly).
