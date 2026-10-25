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

## External consumers: complete API with namespaces

**If you are consuming Ashlar as a published package** (the typical external developer path), here is the complete API surface with all required namespaces and a copy-pasteable minimal example:

### Required using statements

```csharp
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;
```

### Complete minimal brick

```csharp
using Ashlar.Core.Domain.Bricks;
using Ashlar.Core.Domain.Execution;

namespace MyCompany.MyBricks;

public sealed class HelloBrick : Brick
{
    public HelloBrick()
    {
        Id = "my-company.hello";
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
        var name = input.Get<string>("name");
        var output = new BrickOutput { Summary = $"Greeted {name}." };
        output.Set("message", $"Hello, {name}!");
        return Task.FromResult(output);
    }
}
```

### Required package

Add this `PackageReference` to your `.csproj`:

```xml
<ItemGroup>
  <PackageReference Include="Ashlar.Authoring" Version="0.3.0" />
</ItemGroup>
```

The `Ashlar.Authoring` package brings all authoring types: `Brick`, `BrickInput`, `BrickOutput`, `BrickInterface`, `BrickInputDefinition`, `BrickOutputDefinition`, `IExecutionContext`, and the `AddAshlarBrick<T>()` registration helper.

### Registering in a host

```csharp
using Ashlar.Authoring;
using Ashlar.Hosting;

// In your host's Startup or Program.cs
services.AddAshlarBrick<HelloBrick>();
services.AddAshlar();
```

That's the complete external consumer path. For repository-internal development with `ProjectReference`, see the sections below.

## Minimal code brick (repository internal)

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
using Ashlar.Hosting.Sdk.Extensions;

services.AddAshlarSdk(sdk => sdk.RegisterBrick<HelloBrick>());
services.AddAshlar();
```

At runtime, registered code bricks flow into the same `IBrickRegistry` surface used by Ashlar itself. The concrete registry composition includes local code bricks and may be wrapped by `CompositeBrickRegistry` when remote brick catalogs are configured.

For CLI/adaptation scenarios, `AddAdaptationBricks(typeof(HelloBrick))` is the lower-level equivalent. Prefer `AddAshlarSdk(...RegisterBrick<T>())` for application-host code.

`Ashlar.Authoring` adds `services.AddAshlarBrick<HelloBrick>()`, which is exactly `AddAshlarSdk(sdk => sdk.RegisterBrick<HelloBrick>())` behind a one-call name: use `AddAshlarBrick<T>()` when your project references `Ashlar.Authoring` (the `ashlar new brick` scaffold and [`consumer-template/CONSUMING.md`](../consumer-template/CONSUMING.md) do), and use `AddAshlarSdk(...RegisterBrick<T>())` directly when you only reference the `Ashlar.Hosting` graph or need to register several things in one `IAshlarSdkBuilder` callback. Both must run before `AddAshlar()`.

## Input type enforcement and wire format

Ashlar **strictly enforces** the `type` field in `BrickInputDefinition` when bricks are invoked over the wire (HTTP API). The type validation happens during deserialization, and mismatches are rejected with a clear error message before your brick's `ExecuteAsync` is called.

### String inputs must receive string values

When a brick defines an input with `type: "string"`, the wire format requires a JSON string value, **not** a JSON object or array:

```csharp
// Brick definition
Interface = new BrickInterface
{
    Inputs = [
        new BrickInputDefinition("data", "string", "JSON data", required: true)
    ]
};
```

**Correct wire format** — pass complex data as JSON-serialized strings:

```json
{
  "input": {
    "data": "{\"items\":[{\"name\":\"test\"}]}"
  }
}
```

**Incorrect wire format** — passing an object when string is expected:

```json
{
  "input": {
    "data": {"items": [{"name": "test"}]}
  }
}
```

This fails with: `"Input key 'data' is not of type String"`

### Handling complex data in bricks

To work with complex data structures:

1. Define inputs as `type: "string"` in `BrickInputDefinition`
2. Pass JSON-serialized strings in the wire format
3. Deserialize inside your brick's `ExecuteAsync`:

```csharp
public override Task<BrickOutput> ExecuteAsync(
    BrickInput input,
    ImplementationType implementation,
    IExecutionContext context,
    CancellationToken cancellationToken = default)
{
    var dataJson = input.Get("data", "{}") ?? "{}";
    var data = JsonSerializer.Deserialize<MyDataType>(dataJson);
    
    // Process data...
    
    return Task.FromResult(output);
}
```

### Type validation for all input types

This strict validation applies to all input types (`"string"`, `"number"`, `"boolean"`, `"object"`, `"array"`). The wire format must match the declared type exactly. This ensures:

- Clear contract between brick authors and consumers
- Early validation failures with specific error messages
- Type safety across the execution pipeline

## Reading inputs safely

`BrickInput` provides multiple methods for reading input values with different error-handling strategies:

### Get<T>(key) — Required inputs

Use `Get<T>(key)` when an input is required:

```csharp
var name = input.Get<string>("name");
```

Throws `KeyNotFoundException` if the key is missing or `InvalidCastException` if the type doesn't match.

### Get<T>(key, defaultValue) — Optional inputs with defaults

Use `Get<T>(key, defaultValue)` for optional inputs:

```csharp
var timeout = input.Get<int>("timeout", 30);
var format = input.Get<string>("format", "json");
```

Returns the default value if the key is missing or the type doesn't match. Never throws.

### TryGet<T>(key, out value) — Idiomatic C# pattern

Use `TryGet<T>(key, out value)` when you need to distinguish between missing keys and type mismatches:

```csharp
if (input.TryGet<string>("optionalField", out var value))
{
    // Key exists and type matches - use value
    ProcessField(value);
}
else
{
    // Key missing or type mismatch - handle appropriately
}
```

Returns `true` and sets `value` if the key exists and the type matches. Returns `false` and sets `value` to `default(T)` otherwise.

**When to use TryGet:**
- You need different behavior for missing vs present keys
- You want to avoid exception handling for control flow
- You prefer the standard C# Try-pattern

### Reading outputs safely

`BrickOutput` provides the same three methods (`Get<T>`, `Get<T> with default`, and `TryGet<T>`) for reading output values when consuming other bricks or inspecting results.

## Code bricks vs generated manifests

Ashlar also has an adaptive manifest path: `INewBrickGenerator.GenerateAsync(...)` creates a `BrickManifest` from observed patterns. That path is for runtime/adaptive discovery and promotion.

Use **code-authored bricks** when you want to ship source-controlled domain logic with tests and stable package/version ownership. Use the **manifest generator** when Ashlar is inferring a candidate brick from observed workflow patterns. Both paths describe the same conceptual brick surface; code bricks are the developer-authored, reviewable path.

## Packages on nuget.org

Published pin is **`0.3.0`** (`ci/published-version`, equal to repo `VERSION` on this commit), the newest version on nuget.org — do not treat a later repo `VERSION` that has moved ahead of `ci/published-version` as the public pin. A checkout still works with `ProjectReference` (next section) if you do not want the feed.

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

Because an unpublished CLI version may generate a `PackageReference` to a version that is not on the feed yet, restoring a freshly scaffolded project can fail with `NU1101` until you pin `--ashlar-version` to the version nuget.org actually lists (**`0.3.0`**, the pin in `ci/published-version`) or use a local folder feed (next section). Inside a checkout you can run the CLI without installing it:

```bash
dotnet run --project application/src/Ashlar.CLI -- new brick Hello --output /tmp/hello-brick --ashlar-version 9.9.9-local
```

## Restoring Ashlar.Authoring

**For a published version (`0.3.0` today), use plain nuget.org:** a `PackageReference` to `Ashlar.Authoring` at that version restores with `dotnet restore` and no feed configuration. A scaffold from an installed `Ashlar.CLI` at the same version (`dotnet tool install --global Ashlar.CLI --version 0.3.0`, then `ashlar new brick MyThing --ashlar-version 0.3.0`) restores the same way. To see the brick's output outside a test, use the console runner in [`consumer-template/CONSUMING.md`](../consumer-template/CONSUMING.md#3-run-it).

The two options below are for a version that is **not** on nuget.org yet (a checkout ahead of the published pin); the first is what CI verifies.

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
