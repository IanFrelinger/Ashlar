# Branch layer rules (runtime vs application)

CI workflow **`.github/workflows/layer-boundary.yml`** enforces:

| Base branch | Rule |
|-------------|------|
| `master`, `main`, or `runtime/*` | PR must **not** change files under **`application/`** (singular: the `Ashlar.CLI` / `Ashlar.API` hosts), unless one of four exemptions holds: the PR also changes **`commercial/`** (vertical integration merge); it coordinates **`Ashlar.Authoring`** distribution (`src/Ashlar.Authoring/` or `scripts/verify-standalone-brick-authoring.sh` alongside `application/`); every `application/` change is a pure removal of `<ProjectReference>` lines to `src/` projects that no longer exist on the head commit (forced kernel cleanup); or every changed `application/` path belongs to a project whose nearest `.csproj` contains `Microsoft.NET.Test.Sdk` (test-only change) |
| `application/*` | PR must **not** change files under **`src/`** (kernel) |
| `master` / `main` / `runtime/*` | Head branch must **not** be named `application/*` |
| `application/*` | Head branch must **not** be named `runtime/*` |

Plural `applications/` (removed 2026-08-31) and `apps/` (host configs) are **not** covered by this gate; `dependency-boundary` forbids `src/` → `products/` (and legacy `applications/` paths). Extractable product scaffolds live under `products/`.

**Enforcement status (2026-09-09):** `layer-boundary / verify` is **not** a required status check on `master` — branch protection requires `cert-gate`, `build-core`, `shell-lint`, and `lychee (README + docs)`, but not `layer-boundary` — so host PRs without an exemption merge with a red, non-required `verify`. See [`CONTRIBUTING.md`](../../CONTRIBUTING.md) ("Layer boundary and what master actually enforces") and [`docs/CiGateInventory.md`](../CiGateInventory.md).

## Enable required check on GitHub

1. Repository **Settings → Rules → Rulesets** (or **Branches → Branch protection**).
2. Target branches: e.g. `master`, `main`, `application/**`, `runtime/**` as needed.
3. Enable **Require status checks to pass**.
4. Add the check named **`layer-boundary`** (job **`verify`** appears as **`layer-boundary / verify`** in the checks list — require that job).

Until the workflow runs once on a PR, the check may not appear in the search box; merge any PR that touches `.github/workflows/` first or run workflow manually via **Actions**.

## Branch naming convention

- **`application/feature-xyz`** — work that only touches `application/`, docs, tooling outside `src/`.
- **`runtime/feature-xyz`** or topic branches off `master` — kernel changes under `src/`.

If you do not use long-lived `application/*` integration branches, only the **runtime-base** rules apply until you create one.
