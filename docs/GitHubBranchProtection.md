# GitHub branch protection (release discipline)

Branch protection cannot run **`release.yml`** (that workflow is triggered by **tags**, not PR merges). Use it to keep **`master` / `main`** healthy so the commit you tag is already green.

## What `master` enforces today

The upstream `master` rule (verified 2026-09-09 via `gh api repos/IanFrelinger/Ashlar/branches/master/protection`) requires **four** status checks:

- **`cert-gate`** — hermetic certification gate
- **`build-core`** — fast compile check (~2-3 min)
- **`shell-lint`** — shell script syntax verification (~30s)
- **`lychee (README + docs)`** — docs link validation (~30s)

All four run on every PR with no path filters and always report a status. Branch protection also enforces "require branches to be up to date" (`strict: true`) and `enforce_admins: true`. Every other gate — `testing-strategy`, `domain-coverage`, `kernel-coverage`, `layer-boundary / verify`, `Kernel Gate`, `Application Gate`, … — reports on PRs when its `paths:` filter matches but does **not** block a merge. The authoritative inventory is [`CiGateInventory.md`](CiGateInventory.md).

## Recommended rules for `master` (or `main`) — current setting and optional additions

1. **Require a pull request** before merging (disable direct pushes if your team can tolerate it).
2. **Require status checks to pass** — the four currently required checks are:
   - **`cert-gate`** ✅ **required** — hermetic certification gate
   - **`build-core`** ✅ **required** — fast compile check
   - **`shell-lint`** ✅ **required** — shell script syntax verification
   - **`lychee (README + docs)`** ✅ **required** — docs link validation
   
   **Optional additions** (once each gate always reports on PRs):
   - **`testing-strategy`** — pivot policy (gap freeze, ProdStyle wiring hints); see [Testing strategy pivot v1](architecture/TestingStrategyPivot-v1.md)
   - **`kernel-coverage`** — composite floors as enforced by `scripts/ci/kernel-coverage-gate.sh` (Domain 100%, Infrastructure 80%, Application 67%)
   - **`layer-boundary / verify`** — already unfiltered (`paths: "**"`), the one gate that could be added today
   - Path-filtered gates as applicable: **Kernel Gate**, **Application Gate** (each needs an always-report job first)
   - **Cross-Platform Tests**, **Composition Mesh Gate** and **Mesh virtual lab gate** are `workflow_dispatch`-only and cannot be required as-is
3. **Require branches to be up to date** before merge ✅ **already on**.
4. **Require conversation resolution** (optional, for review hygiene).

Full path → workflow map: [Testing strategy tracking v1](architecture/TestingStrategyTracking-v1.md).

## Release-specific checks

- Treat **`runtime-release-gate`** as a **manual or scheduled** quality bar before a big release (or wire it into your process): `dotnet run --project application/src/Ashlar.CLI -- release gate` after `gh auth login`.
- Run **`make rc-gate-full`** before tagging; see [RC readiness v1](production-readiness/RCReadiness-v1.md).
- The **tag** `v*.*.*` is the contract for **`release.yml`**; protect **`master`** so that tag usually points at a merged, reviewed commit.

## Forks

Contributors working in **forks** often cannot push **GHCR** packages to the upstream namespace with the default `GITHUB_TOKEN`. Releases that publish images or NuGet should run in the **upstream** repository (or document PAT-based publishing for maintainers).
