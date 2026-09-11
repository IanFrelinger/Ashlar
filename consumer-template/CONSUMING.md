# Consuming Ashlar packages (external product template)

Starter pins for **ashlar-ai-director**-style repos: authored brick + thin host + HTTP client. Copy `nuget.config` and `Directory.Packages.props` from this folder into your solution root.

**Package version:** `0.1.2` — the current nuget.org release (`ci/published-version`). Repo `VERSION` may already read ahead of a release that has not been published; do not treat it as the public pin.

**On nuget.org since v0.1.1 (2026-09-01); current pin is v0.1.2 (2026-09-04).** The full `Ashlar.*` graph (including `Ashlar.CLI`, `Ashlar.Authoring`, `Ashlar.Hosting.Bundle`) restores from plain nuget.org — no staging feed needed. The living proof is [github.com/IanFrelinger/ashlar-release-manager](https://github.com/IanFrelinger/ashlar-release-manager), whose CI restores from nuget.org and nothing else. A staging feed (below) remains an option for pre-release testing; inside a checkout, a `ProjectReference` into `src/` (as in `samples/hello-brick/`) needs no feed at all.

## Package pins (`0.1.2`)

| Package | Role |
|---------|------|
| `Ashlar.Brick.Contracts` | Authoring surface for code bricks (`Brick`, `BrickInput`, wire DTOs) |
| `Ashlar.Authoring` | `AddAshlarBrick<T>()` registration helpers |
| `Ashlar.Hosting.Bundle` | Thin host: embed Ashlar kernel + HTTP API (`AddAshlar`, hosting graph) |
| `Ashlar.Sdk` | Engine/client DI (`AddAshlarClientSdk`) over HTTP |
| `Ashlar.Client` | `IAshlarClient` (transitive via Sdk; pin explicitly if you reference it directly) |

### `PackageReference` form (without central package management)

```xml
<ItemGroup>
  <PackageReference Include="Ashlar.Brick.Contracts" Version="0.1.2" />
  <PackageReference Include="Ashlar.Authoring" Version="0.1.2" />
  <PackageReference Include="Ashlar.Hosting.Bundle" Version="0.1.2" />
  <PackageReference Include="Ashlar.Sdk" Version="0.1.2" />
  <PackageReference Include="Ashlar.Client" Version="0.1.2" />
</ItemGroup>
```

With **central package management**, use `Directory.Packages.props` in this folder instead.

> **Note:** the `0.1.2` graph pins `Microsoft.Extensions.*` at `10.0.11`. If your project explicitly references any `Microsoft.Extensions.*` package below that version, restore fails with `NU1605` (package downgrade) — align your pins to `>= 10.0.11`.

## Target frameworks

NuGet picks each `Ashlar.*` asset from **your project's** target framework, and that choice decides what the certification verifier can do. No package in the graph ships a `net6.0` or `net7.0` asset group; the groups that exist are `netstandard2.0`, `net8.0` and `net10.0` (per-package table below).

| Your TFM | What works | What does not, and why |
|----------|------------|------------------------|
| `net8.0`, `net10.0` | Everything: brick authoring and hosting, SDK/client, HMAC **and** Ed25519 signing/verification, `CertificationVerifyOptions.Default` / `.Strict` / pinned verdicts. | — `net8.0` is the **floor for trust**: Ed25519 lives in `CertificationRecordEd25519`, which is compiled only under `NET8_0_OR_GREATER` because `NSec.Cryptography` 25.4.0 ships `net8.0`-family assets only (no `netstandard2.0`), and `Default` and `Strict` both set `RequireEd25519Signature`. |
| `net9.0` | Same as `net8.0`. There is no `net9.0` group, so NuGet binds `lib/net8.0`; on the 9.0 runtime that asset loads, mints and verifies (HMAC and Ed25519) and produces canonical signing bytes identical to net8.0/net10.0. Exercised by the advisory `Runtime Portability Gate` (`scripts/portability/net9-probe.sh`), the only 9.0.x runtime pin in CI. | .NET 9 (STS) left Microsoft support in May 2026: it works, it is not recommended. The lane executes `Ashlar.Certification.Contracts`; the other packages bind `lib/net8.0` by the same rule but are not executed on 9.0 in CI. |
| `netstandard2.0` (.NET Framework, Mono, Unity) | **Floor for authoring and execution.** `Ashlar.Brick.Contracts` (brick and wire types) and `Ashlar.Certification.Contracts` ship a `netstandard2.0` asset. On it the canonical signing bytes are byte-identical to the other targets (measured under Mono 6.12 by `scripts/ns20-canonical-bytes-probe.sh`) and HMAC signing/verification is available (`CertificationRecordSigning`, which carries its own constant-time compare for pre-.NET 5 targets). | No Ed25519, so `Default` and `Strict` never return trusted on this asset: they refuse with an `ed25519-…` failure code (`ed25519-verification-unavailable` today) rather than skip the check; only options that neither require Ed25519 nor pin keys — `Legacy` (HMAC-only, insecure by design) is the named one — can trust a record. No hosting: `Ashlar.Authoring` (`AddAshlarBrick<T>()`), `Ashlar.Hosting.Bundle` (`AddAshlar()`), `Ashlar.Sdk` and `Ashlar.Client` are `net8.0;net10.0` only. The asset uses `required` and `init` members, so the consumer compiles the same polyfills the package does — see the consumer `scripts/ns20-canonical-bytes-probe.sh` writes. |
| `net6.0`, `net7.0` | Restore of the two published contract packages (`Ashlar.Brick.Contracts`, `Ashlar.Certification.Contracts`) succeeds — and that is the trap. | **Cliff.** With no `net6.0`/`net7.0` group, NuGet silently takes the nearest compatible asset, `lib/netstandard2.0` (measured: a `net7.0` consumer of the packed `Ashlar.Certification.Contracts` resolves `lib/netstandard2.0`; restore succeeds with only `NETSDK1138`, the SDK's end-of-support warning, and the Ed25519 types are simply absent from the bound asset — `CS0103` if referenced directly, `ed25519-verification-unavailable` through the verifier). You get the `netstandard2.0` row on a modern .NET runtime, with nothing but that warning to say so: `Default`/`Strict` verification returns `ed25519-verification-unavailable`. `Ashlar.Authoring`, `Ashlar.Hosting.Bundle`, `Ashlar.Sdk` and `Ashlar.Client` have no compatible asset at all and fail restore with `NU1202`. Target `net8.0` or later. |

### Target frameworks per package

Read from each project's `<TargetFrameworks>`; the `.csproj` is the authority if this table drifts.

| Package | Target frameworks |
|---------|-------------------|
| `Ashlar.Brick.Contracts` | `netstandard2.0;net8.0;net10.0` |
| `Ashlar.Certification.Contracts` | `netstandard2.0;net8.0;net10.0` — Ed25519 only in the `net8.0` and `net10.0` assets |
| `Ashlar.Certification.State` | `netstandard2.0;net8.0;net10.0` — **in-repo only**: consumed by `ProjectReference`; not packed by `scripts/pack-ashlar-hosting-graph.sh`, not on nuget.org |
| `Ashlar.Authoring` | `net8.0;net10.0` |
| `Ashlar.Hosting.Bundle` | `net8.0;net10.0` — metapackage over 14 `Ashlar.*` packages, including `Ashlar.Hosting` (`net8.0;net10.0`) and `Ashlar.Certification.Contracts` |
| `Ashlar.Sdk` | `net8.0;net10.0` |
| `Ashlar.Client` | `net8.0;net10.0` |
| `Ashlar.CLI` (dotnet tool) | `net10.0` |

`Ashlar.Certification.Contracts` is not in the pin list above (nor in `scripts/consumer-surface-packages.txt`): it arrives transitively through `Ashlar.Hosting.Bundle`; a verify-only consumer pins it directly at the same version. `Ashlar.Certification.State` (the state log) cannot be pinned at all — it is not published, so a consumer that needs it works from a checkout.

## Feed and token

1. Edit `nuget.config`: set `ashlar-staging` to your feed URL (see `docs/StagingFeed.md`).
2. For private feeds, uncomment `packageSourceCredentials` in `nuget.config` and supply a read PAT via environment or local (untracked) config — **never commit tokens**.
3. Restore with `dotnet restore --configfile nuget.config`.

## Layout (typical PoP)

- **Brick project** — references `Ashlar.Authoring` (+ `Ashlar.Brick.Contracts` types); scaffold with `ashlar new brick --ashlar-version <version>` from a CLI tool-installed at the same version (`dotnet tool install --global Ashlar.CLI --version <version>` from nuget.org, or `--add-source <feed>` for a staging/pre-release cut).
- **Host** — `Ashlar.Authoring` + `Ashlar.Hosting.Bundle`; register bricks with `AddAshlarBrick<T>()` before `AddAshlar()`; expose `GET /health` and `POST /api/bricks/{id}/execute` (a complete minimal host lives in `consumer-template/host/Program.cs`; its `__TOKEN__` markers are explained in `consumer-template/host/README.md`).
- **Client** — `Ashlar.Sdk`; call `IAshlarClient.InvokeAsync(HttpMethod.Post, "api/bricks/{id}/execute", …)` against the host base URL.

## Publishing for a runtime identifier (`dotnet publish -r <rid>`)

A host built on `Ashlar.Hosting.Bundle` publishes for a RID with nothing extra in the csproj — which matters because a container image is normally built that way:

```xml
<PropertyGroup>
  <RuntimeIdentifier>linux-x64</RuntimeIdentifier>
  <SelfContained>false</SelfContained>
</PropertyGroup>
```

Put those in the project file rather than passing `-p:RuntimeIdentifier=…` on the command line: a `-p:` switch is a global MSBuild property that also flows into every referenced project, which is a different (and larger) thing than publishing your host for a RID.

**This is fixed from the release after `0.1.2`.** On `0.1.2` and earlier, that publish fails with `NETSDK1152` on 20 duplicate files. `Ashlar.Infrastructure` and `Ashlar.AI.Pipeline` depended on `LLamaSharp.Backend.Cpu`, which ships four same-named CPU-variant native sets — `avx`, `avx2`, `avx512`, `noavx`, each carrying `libggml-base`, `libggml-cpu`, `libggml`, `libllama` and `libmtmd` — under one `runtimes/<rid>/native/` tree. A RID publish flattens that tree into the output root, so the four variants land on the same five paths and the SDK refuses. A RID-less publish keeps the directory structure, which is why the same project publishes fine without `-r` and why this was never a build failure inside Ashlar. Those two packages now carry the backend with `PrivateAssets="all"`, so it is not in their packed dependency list and never enters your restore graph. If you are pinned to `0.1.2` and cannot wait, the prune target below fixes the collision in your own csproj.

Do not reach for `-p:ErrorOnDuplicatePublishOutputFiles=false`. It removes the error by letting one variant win, and records nothing about which: the publish lands five flattened `.so` files, of which `libggml-cpu.so` — the only one that actually differs between the four variants — is whichever the SDK happened to copy last. That is an `avx512` build that `SIGILL`s on older hardware, or a `noavx` one at a large throughput cost, with nothing in the output to say so.

### Local model inference is opt-in

The backend is what loads GGUF weights for the `local` provider (`ASHLAR_LOCAL_MODEL_PATH`). It is composed, never required: the managed `LlamaSharp` package is what the Ashlar assemblies compile against, natives are touched only inside the model load, and a host published without them starts, serves `/health` and executes bricks normally. Nothing else in the graph changes.

If you want local inference, add the backend to your own host project — this is the whole opt-in:

```xml
<ItemGroup>
  <PackageReference Include="LLamaSharp.Backend.Cpu" Version="0.25.0" />
</ItemGroup>
```

Opting in re-inherits the four-variant layout, so if you are **also** publishing for a RID you need to pick one variant yourself. This target does it deterministically:

```xml
<PropertyGroup>
  <!-- Only libggml-cpu.so actually differs between the four variants (libggml, libggml-base,
       libllama and libmtmd are byte-identical across all of them), so this property picks which
       CPU kernels ship. avx2 covers x86-64 since ~2013; noavx is the universally safe choice at a
       large throughput cost; avx512 only where you control the fleet. -->
  <AshlarLlamaCpuVariant>avx2</AshlarLlamaCpuVariant>
</PropertyGroup>
<Target Name="PickOneLlamaCpuVariant" AfterTargets="ComputeResolvedFilesToPublishList">
  <ItemGroup>
    <ResolvedFileToPublish Remove="@(ResolvedFileToPublish)"
      Condition="'%(ResolvedFileToPublish.NuGetPackageId)' == 'LLamaSharp.Backend.Cpu' AND !$([System.String]::Copy('%(ResolvedFileToPublish.Identity)').Replace('%5C','/').Contains('/native/$(AshlarLlamaCpuVariant)/'))" />
  </ItemGroup>
</Target>
```

The `Replace` is there so the condition also holds when you publish from Windows, where `Identity` comes back with `\` separators. Write that backslash as `%5C`, MSBuild's escape for it: measured on Linux the escaped and the literal spelling prune identically (five `avx2` files), but a lone `\` immediately before a quote is the one place MSBuild's condition parser can read the separator as an escape of the quote, and the escaped spelling removes the question.

Match on `%(Identity)` — the full path inside the package — and not on `%(RelativePath)`. By this point `RelativePath` has already been flattened to the bare file name, so the `RelativePath` spelling of the condition matches nothing, removes **all five** natives, and still exits 0: a green publish that throws `DllNotFoundException` at the first inference. Count the `.so` (or `.dll`/`.dylib`) files in your publish output rather than trusting the exit code; five is correct, zero means you hit this.

Ashlar measures the default half of this on every run of `scripts/verify-external-product-shape.sh` (the `external-product-shape` job of the distribution matrix gate): it renders this same template with the two publish properties in the csproj, publishes it for `linux-x64`, asserts the output carries **zero** backend natives, then starts the published binary and executes a brick through it.

## Verification in Ashlar

These pins are the same set exercised by:

- `scripts/verify-external-product-shape.sh` (local pack feed)
- `scripts/verify-external-product-shape-published.sh` (published staging feed)
- `scripts/consumer-surface-packages.txt` (machine-readable list)

After publishing to staging: `make verify-staging VERSION=0.1.0` with `NUGET_STAGING_READ_TOKEN` set.
