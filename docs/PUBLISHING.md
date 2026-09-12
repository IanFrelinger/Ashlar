# Publishing Ashlar for external consumption

This document describes how to **produce** and **publish** the .NET packages that external repos (for example game tooling) consume. CI already **verifies** NuGet-only consumption locally; publishing to a feed is an operator step.

**Minimal local preflight (one command):** `bash scripts/release-preflight-local.sh X.Y.Z`, `make release-preflight VERSION=X.Y.Z`, or `dotnet run --project application/src/Ashlar.CLI -- release preflight X.Y.Z` — then push **`vX.Y.Z`** for **`release.yml`** (or **`release dispatch`** / **`make release-dispatch`**). Hub: **`docs/RELEASE.md`**. Checklist: **`docs/RELEASE_RUNBOOK.md`**. GitHub **variables**: **`docs/GitHubRepoVariables.md`**.

## What gets published

Embedding the Ashlar kernel from another .NET app uses **`Ashlar.Hosting`**, which depends on other **`Ashlar.*`** packages built from this repo. Publish **all of them with the same semantic version** (for example `1.2.3`).

Use:

```bash
bash scripts/pack-ashlar-hosting-graph.sh 1.2.3 ./artifacts/nuget-release
```

(or `scripts/pack-ashlar-hosting-graph.ps1` on Windows). Output is a folder of `*.nupkg` / `*.snupkg` files, including **`Ashlar.Hosting.Bundle`** — a **metapackage** so consumers can add **one** `PackageReference` instead of chasing transitive versions manually.

**Consumer recommendation:** reference **`Ashlar.Hosting.Bundle`** at version `1.2.3` (same as the graph). **`Ashlar.Hosting`** remains the real assembly package; the bundle only pulls the graph.

**Note:** `Ashlar.Hosting.Bundle` is **not** part of `Ashlar.sln` — it only restores after the graph exists on a feed. CI packs it via `scripts/pack-ashlar-hosting-graph.*`; local `dotnet build` of the repo does not need it.

Stable **client** surface (HTTP) is documented in `docs/sdk.md` (`Ashlar.Sdk` / `Ashlar.Client`). **In CI these are not optional**: `reusable-release-nuget.yml` unconditionally packs the hosting graph **plus** `Ashlar.Client`, `Ashlar.Sdk`, and `Ashlar.Authoring` (and the `Ashlar.CLI` tool) in one versioned set. Locally, pack them the same way if you publish to a feed:

```bash
dotnet pack src/Ashlar.Client/Ashlar.Client.csproj -c Release -o ./artifacts/nuget-release -p:PackageVersion=1.2.3
dotnet pack src/Ashlar.Sdk/Ashlar.Sdk.csproj -c Release -o ./artifacts/nuget-release -p:PackageVersion=1.2.3
```

## Verify before you push

From a clean machine or CI artifact:

```bash
export ASHLAR_SDK_PACKAGE_VERSION=1.2.3
bash scripts/verify-stable-sdk-host-sample-packages.sh
```

This packs the graph to `artifacts/nuget-verify/packages`, restores `docs/samples/StableSdkHostSample/package-consumer/` against **only** that folder + nuget.org, builds, and runs the sample. Restore uses **`--force-evaluate`** and, by default, an **empty `NUGET_PACKAGES` + `DOTNET_CLI_HOME`** under `artifacts/nuget-verify/isolated-*` so a stale user/global cache cannot mask a bad graph (set **`ASHLAR_SDK_VERIFY_NO_ISOLATED_CACHE=1`** to opt out; **`ASHLAR_SDK_VERIFY_ISOLATED_ROOT`** to reuse a fixed directory).

To verify an **unpacked** CI artifact folder without re-packing:

```bash
export ASHLAR_SDK_PACKAGE_VERSION=1.2.3
export ASHLAR_SDK_PACKAGE_FEED=/path/to/unpacked/nuget-packages
bash scripts/verify-stable-sdk-host-sample-packages.sh
```

**Local manifest (optional):** after packing a folder, `PACKAGE_VERSION=1.2.3 bash scripts/render-nuget-release-manifest.sh ./artifacts/nuget-release` writes **`nuget-publish-manifest.json`** plus one **`.sha256.txt`** per `.nupkg`.

### The packed artifact has to *behave*, not just restore

Everything above proves the packages exist, that their bytes match the manifest, and that a sample
restores and builds against them. None of it can tell whether the verifier inside them still refuses
anything — and it is the verifier that consumers are trusting.

```bash
# what a consumer gets from a package packed out of this working tree
bash scripts/verify-packed-certification-trust-behavior.sh

# ...from an unpacked CI artifact folder
ASHLAR_CONFORMANCE_FEED=/path/to/unpacked/nuget-packages ASHLAR_CONFORMANCE_VERSION=1.2.3   bash scripts/verify-packed-certification-trust-behavior.sh

# ...from nuget.org, after publishing
ASHLAR_CONFORMANCE_SOURCE=nuget.org ASHLAR_CONFORMANCE_VERSION=1.2.3   bash scripts/verify-packed-certification-trust-behavior.sh
```

`release.yml` runs the first form against the packed feed **before every push** and the last form
against nuget.org afterwards, so you do not have to remember. Run it by hand when you are cutting a
release from an unusual branch, or when you want to know what an *already published* version does.

**If it fails, do not publish, and do not weaken the script.** It compares the package against the
presets the source tree declares; a red run means those two have come apart, and the package is the
half consumers get. `docs/samples/CertificationTrustConsumer/README.md` explains each assertion.

**To read a version whose API surface predates today's**, the typed consumer will not compile at
all. Use the reflection probe, which reports rather than judges:

```bash
bash scripts/probe-published-certification-presets.sh 1.2.3
```

**After packages are on nuget.org (or a private feed):** use `scripts/verify-stable-sdk-host-sample-published-feed.sh` — see **`docs/NuGetConsumerVerify.md`** and workflow **`.github/workflows/nuget-consumer-verify.yml`**.

## Publish to nuget.org (you do this)

### Option A — GitHub Actions (recommended)

Workflows:

- **`.github/workflows/release.yml`** — **tag `v*.*.*`**: GHCR **and** NuGet in one run. After a successful push to nuget.org, runs **Verify NuGet consumer** (shared with **release-nuget**).
- **`.github/workflows/release-nuget.yml`** — **manual NuGet-only** dispatch (version input). After push to nuget.org, runs the same **Verify NuGet consumer** job when **`NUGET_PUBLISH_MODE`** is **`oidc`** or **`apikey`**.

**Trusted Publishing (OIDC)** on nuget.org matches the workflow that actually runs
`NuGet/login` — for this repo that is the **reusable** `reusable-release-nuget.yml`, NOT the
caller. (Verified empirically on the first v0.1.1 publish: a policy registered for
`release.yml` fails token exchange with `Workflow mismatch … expected 'release.yml', actual
'reusable-release-nuget.yml'`.) Register **one** policy for workflow file
**`reusable-release-nuget.yml`** and it covers every entry point — tag push (**Release**)
and the manual **Release NuGet packages** dispatch alike, since both call the same reusable.

1. **Repository variable** `NUGET_PUBLISH_MODE`:
   - unset, empty, or **`none`** — pack + verify + artifact only; download **`nuget-packages-<version>`** and push manually if desired.
   - **`oidc`** — [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing) for the workflow files above. Secret **`NUGET_USER`** = nuget.org **profile name** (not email).
   - **`apikey`** — secret **`NUGET_API_KEY`**.
2. **Trigger:** push tag **`v1.2.3`** (preferred), or **Actions → Release** / **Release NuGet packages** for partial flows.
3. **After push to nuget.org:** **`release.yml`** runs **Verify NuGet consumer (nuget.org)** when **`NUGET_PUBLISH_MODE`** is **`oidc`** or **`apikey`** (restores the sample from nuget.org only, with retries for index lag). See **`docs/NuGetConsumerVerify.md`**.
4. Write **GitHub Release** notes and verify packages on nuget.org.

### Post-push verification (`reusable-release-nuget.yml`)

When **`NUGET_PUBLISH_MODE`** is **`oidc`** or **`apikey`** (and **`NUGET_POST_PUSH_VERIFY`** is not **`false`**), the reusable workflow:

1. **Flat-container** — `scripts/verify-nuget-org-packages-visible.sh` (HEAD on `.nupkg` URLs).
2. **Registration API** — `scripts/verify-nuget-org-registration-versions.sh` (version listed in `registration5-gz-semver2`).
3. **Byte match** — `scripts/verify-nuget-published-sha256-matches-manifest.sh` downloads each package from nuget.org and checks **SHA-256** against **`nuget-publish-manifest.json`** produced at pack time (artifact includes manifest + `.sha256.txt` files).
4. **Restore (bundle)** — `scripts/verify-nuget-org-restore-with-isolated-cache.sh` — `Ashlar.Hosting.Bundle` on nuget.org only, **isolated** package cache.
5. **Restore (hosting only)** — `scripts/verify-nuget-org-restore-hosting-only-isolated.sh` — direct **`Ashlar.Hosting`** reference (second consumer path).

**Repository variables** (optional):

| Variable | Purpose |
|----------|---------|
| **`NUGET_POST_PUSH_VERIFY`** | Set to **`false`** to skip steps 1–5. |
| **`NUGET_POST_PUSH_VERIFY_PACKAGE_IDS`** | Comma-separated ids for steps 1–2 (default: `Ashlar.Hosting.Bundle,Ashlar.Hosting,Ashlar.Sdk,Ashlar.CLI`). |
| **`NUGET_POST_PUSH_ATTEMPTS`** / **`NUGET_POST_PUSH_SLEEP_SEC`** | Poll tuning (empty uses script defaults: **40** attempts × **15**s). Values below 40 are raised unless `ASHLAR_NUGET_VERIFY_ALLOW_SHORT=1`. |
| **`NUGET_RELEASE_SBOM`** | Set to **`true`** to generate SPDX JSON per `.nupkg` with **Syft** and upload artifact **`nuget-sbom-<version>`**. |
| **`NUGET_RELEASE_GRYPE`** | With **`NUGET_RELEASE_SBOM`**, run **Grype** on each SBOM (reports only; **`continue-on-error`** so vuln data does not fail the release). |
| **`RELEASE_CROSS_VERIFY`** | Set to **`false`** to skip **`release.yml`** job that re-pulls GHCR **`sha-*`** images and runs **`scripts/release-smoke-published-docker.sh`**. |
| **`NUGET_STAGING_FEED_URL`** | Staging v3 feed URL | Push **before** nuget.org; requires secret **`NUGET_STAGING_API_KEY`** — `docs/StagingFeed.md`. |
| **`RELEASE_CREATE_GITHUB_RELEASE`** | Set **`false`** to skip **draft GitHub Release** on tag runs. |

Webhook: set secret **`RELEASE_NOTIFICATION_WEBHOOK_URL`** (not a variable) — `docs/GitHubRepoVariables.md`.

**Not atomic:** nuget.org accepts packages one-by-one; a mid-run failure can leave a **partial** set until you fix and re-push with **`--skip-duplicate`**.

### Option B — Manual from your machine

1. Create a **NuGet.org** account (if needed) and an **API key** with scope **Push** for the `Ashlar.*` package IDs (or org-owned IDs).
2. Locally: `dotnet nuget push "artifacts/nuget-release/*.nupkg" --api-key <KEY> --source https://api.nuget.org/v3/index.json`
3. Optionally push symbols: `*.snupkg` to the same source (NuGet accepts symbols alongside).
4. Tag the git repo **`v1.2.3`** to match the package version you pushed.
5. Write **GitHub Release** notes: list package versions, migration notes, and any breaking changes per `docs/SdkCompatibilityPolicy.md`.

## Publish to GitHub Packages (alternative)

1. Generate a **PAT** or use `GITHUB_TOKEN` in Actions with `packages: write`.
2. `dotnet nuget add source` pointing at `https://nuget.pkg.github.com/<OWNER>/index.json` with credentials.
3. `dotnet nuget push` the same folder to that source.
4. Document for consumers: **feed URL**, **auth** (PAT as password), and **exact package version** to pin.

## Container image (separate track)

The CLI image is published by `.github/workflows/container-image-publish.yml` to **GHCR**. Tag releases also build images via **`release.yml`**. **`reusable-container-publish.yml`** smoke-tests **ashlar-cli** (`--help`) and **ashlar-api** (`/health`) on the **immutable `sha-*`** image after push.

## What you must maintain over time

- When `Ashlar.Hosting` gains a **new project reference** to another in-repo `Ashlar.*` project, add that project to **`scripts/pack-ashlar-hosting-graph.sh`** / **`.ps1`**. CI runs **`python3 scripts/verify-pack-ashlar-hosting-graph-alignment.py`** (workflow **`pack-hosting-graph-alignment.yml`**). Rare extras: **`scripts/pack-ashlar-hosting-graph.allowlist.txt`**.
- Keep **`PackageVersion`** in sync across the graph for a given release (the scripts pass one version to every `dotnet pack`).

## Operator checklist

See **`docs/RELEASE_RUNBOOK.md`** for the release decision table (tag vs NuGet-only vs branch images).
