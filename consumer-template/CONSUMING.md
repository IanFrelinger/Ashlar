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

## Feed and token

1. Edit `nuget.config`: set `ashlar-staging` to your feed URL (see `docs/StagingFeed.md`).
2. For private feeds, uncomment `packageSourceCredentials` in `nuget.config` and supply a read PAT via environment or local (untracked) config — **never commit tokens**.
3. Restore with `dotnet restore --configfile nuget.config`.

## Layout (typical PoP)

- **Brick project** — references `Ashlar.Authoring` (+ `Ashlar.Brick.Contracts` types); scaffold with `ashlar new brick --ashlar-version <version>` from a CLI tool-installed at the same version (`dotnet tool install --global Ashlar.CLI --version <version>` from nuget.org, or `--add-source <feed>` for a staging/pre-release cut).
- **Host** — `Ashlar.Authoring` + `Ashlar.Hosting.Bundle`; register bricks with `AddAshlarBrick<T>()` before `AddAshlar()`; expose `GET /health` and `POST /api/bricks/{id}/execute` (a complete minimal host lives in `consumer-template/host/Program.cs`; its `__TOKEN__` markers are explained in `consumer-template/host/README.md`).
- **Client** — `Ashlar.Sdk`; call `IAshlarClient.InvokeAsync(HttpMethod.Post, "api/bricks/{id}/execute", …)` against the host base URL.

## Verification in Ashlar

These pins are the same set exercised by:

- `scripts/verify-external-product-shape.sh` (local pack feed)
- `scripts/verify-external-product-shape-published.sh` (published staging feed)
- `scripts/consumer-surface-packages.txt` (machine-readable list)

After publishing to staging: `make verify-staging VERSION=0.1.0` with `NUGET_STAGING_READ_TOKEN` set.
