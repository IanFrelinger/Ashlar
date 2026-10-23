# CI secrets and repository variables

Every `secrets.*` and `vars.*` reference under `.github/workflows/`, what reads it, and what a fork
(or a fresh clone of this repository) gets **without** it. Compiled from
`grep -ohE '(secrets|vars)\.[A-Za-z_0-9]+' .github/workflows/*.yml` on 2026-08-16; keep it in sync
when a workflow starts or stops reading one.

The short version for a fork: **nothing is required.** Every gate that runs on `pull_request`,
`push` or `schedule` works with the automatic `GITHUB_TOKEN` alone. Secrets and variables only
change what the release / publish path does, and that path is additionally guarded by an owner
check (below), so a fork's tag push or master push produces a mostly-skipped run rather than an
accidental publish under the fork's namespace.

## Owner guard on publish / release workflows

These jobs carry `if: github.repository_owner == 'IanFrelinger'` (added 2026-08-16):

| Workflow | Guarded job | Effect on a fork |
| --- | --- | --- |
| `.github/workflows/container-image-publish.yml` | `publish` (calls `reusable-container-publish.yml`) | a path-filtered push to `master`/`main` no longer tries to publish `nexo-cli` / `nexo-api` to the fork's GHCR |
| `.github/workflows/release.yml` | `validate` (every other job needs it) | a `v*.*.*` tag push produces skipped `images` / `nuget` / `github-release`; `summarize` and `notify` still run and print / no-op |
| `.github/workflows/release-nuget.yml` | `nuget` (calls `reusable-release-nuget.yml`) | manual dispatch is a no-op |
| `.github/workflows/release-staging-on-label.yml` | `dispatch-staging-release` | labelling a PR `release:staging` no longer dispatches `release.yml` |

A fork that **wants** to publish under its own account edits those four `if:` lines (or replaces
the literal with `github.repository_owner == github.actor`); GHCR image names are already derived
from `github.repository_owner`, so no other change is needed for images. NuGet additionally needs
the variables/secrets in the next section.

`reusable-container-publish.yml`, `reusable-release-nuget.yml` and `reusable-verify-nuget-consumer.yml`
are `workflow_call`-only and are reached only through the guarded callers above.

## Secrets

`GITHUB_TOKEN` is provided automatically on every run and is not listed per row; it is used by
`docs-link-check.yml` (lychee rate-limit headroom), `reusable-container-publish.yml` and
`release.yml` (GHCR login, `gh release create`), `release-staging-on-label.yml` (`gh workflow run`)
and `devlog-ghost-release.yml` (`gh release view`). On a fork it carries the fork's own
permissions, which is what the `permissions:` blocks in those files ask for.

| Secret | Read by | Only when | Without it |
| --- | --- | --- | --- |
| `NUGET_USER` | `reusable-release-nuget.yml` — `NuGet/login@v1` (Trusted Publishing OIDC) | `vars.NUGET_PUBLISH_MODE == 'oidc'` | the login step fails and the run is red. Unset `NUGET_PUBLISH_MODE` instead (artifact-only) if you cannot supply it. |
| `NUGET_API_KEY` | `reusable-release-nuget.yml` — `dotnet nuget push` (API key) | `vars.NUGET_PUBLISH_MODE == 'apikey'` | `dotnet nuget push` fails with an empty key. Same remedy as above. |
| `NUGET_STAGING_API_KEY` | `reusable-release-nuget.yml` — staging feed push | `vars.NUGET_STAGING_FEED_URL != ''` | **hard error** (`::error::NUGET_STAGING_FEED_URL is set but secret NUGET_STAGING_API_KEY is missing`). Either set both or neither; see `docs/StagingFeed.md`. |
| `RELEASE_NOTIFICATION_WEBHOOK_URL` | `release.yml` — `notify` job | always (job runs `if: always()`) | step prints `No RELEASE_NOTIFICATION_WEBHOOK_URL secret; skip notify.` and exits 0. Degrades cleanly. |
| `GHOST_URL`, `GHOST_ADMIN_API_KEY` | `devlog-ghost-release.yml` — `tools/devlog-ghost-publish/publish.mjs` | `workflow_dispatch`, or a `release: published` event with `vars.DEVLOG_GHOST_ENABLED == 'true'` | the script exits non-zero (`Missing GHOST_URL or GHOST_ADMIN_API_KEY`); the run is red but nothing else depends on it. Release-event runs are skipped entirely unless the variable is set, so a fork never sees this by accident. |

Secrets that **used** to be referenced and are gone with the workflow that read them
(2026-08-16 pruning, see `docs/CiGateInventory.md`, "Pruning"): `MAPBOX_ACCESS_TOKEN`
(`mapbox-tile-helpers-ci.yml`; run the Mapbox tests locally with `ASHLAR_TEST_MAPBOX_TILES=1` and the
token in the environment) and `ASHLAR_MESH_DIRECTOR_BASE_URL`, `ASHLAR_MESH_API_KEY`,
`MESH_LAB_PEER_REGISTRATION_KEY`, `MESH_LAB_REMOTE_WORKER_URL`, `ASHLAR_MESH_TLS_INSECURE`
(`mesh-lab-remote-gate.yml`; run `scripts/mesh-lab-verify-remote.sh` from a tailnet host instead).

## Repository variables (`vars.*`)

Repository variables are plain text (Settings → Secrets and variables → Actions → Variables).
None is set on the upstream repository as of 2026-08-16 (`gh variable list` is empty), which is
why every release-path run so far has been artifact-only.

| Variable | Read by | Meaning | Without it |
| --- | --- | --- | --- |
| `NUGET_PUBLISH_MODE` | `reusable-release-nuget.yml`, `release.yml`, `release-nuget.yml`, `release-staging-on-label.yml` | `oidc` or `apikey` enables the nuget.org push steps and the post-push verification jobs; anything else is **artifact-only** | packages are packed, hashed, uploaded as the `nuget-packages-<version>` artifact and never pushed; `verify-nuget-consumer` is skipped; `release-staging-on-label.yml` is allowed to dispatch (it refuses when the mode would push to nuget.org). |
| `NUGET_STAGING_FEED_URL` | `reusable-release-nuget.yml`, `release-staging-on-label.yml` | optional private/staging feed to push every `.nupkg` to (`--skip-duplicate`) | staging push step skipped; `release-staging-on-label.yml` fails fast with `NUGET_STAGING_FEED_URL is not set`. |
| `NUGET_RELEASE_SBOM` | `reusable-release-nuget.yml` | `true` installs Syft and uploads SPDX SBOMs per `.nupkg` | SBOM steps skipped. |
| `NUGET_RELEASE_GRYPE` | `reusable-release-nuget.yml` | `true` (with SBOM) runs a Grype scan (`continue-on-error`) | scan skipped. |
| `NUGET_POST_PUSH_VERIFY` | `reusable-release-nuget.yml` | `false` disables the five post-push nuget.org checks (visibility, registration, SHA-256, restore, hosting-only restore) | checks run — but only after a real push, i.e. only when `NUGET_PUBLISH_MODE` is set. |
| `NUGET_POST_PUSH_VERIFY_PACKAGE_IDS`, `NUGET_POST_PUSH_ATTEMPTS`, `NUGET_POST_PUSH_SLEEP_SEC` | `reusable-release-nuget.yml` | tuning for the post-push checks (package id list, retry count, sleep between retries). Attempts default to 80×15s; values below 80 are raised unless `ASHLAR_NUGET_VERIFY_ALLOW_SHORT=1`. | script defaults (`scripts/verify-nuget-org-*.sh`). |
| `RELEASE_CROSS_VERIFY` | `release.yml` — `verify-published` | `false` skips re-pulling the just-published GHCR images by digest tag and smoking them | re-pull smoke runs when `images` and `nuget` both succeeded. |
| `RELEASE_CREATE_GITHUB_RELEASE` | `release.yml` — `github-release` | `false` skips creating the **draft** GitHub Release on a tag push | draft release is created (tag pushes only, never on dispatch). |
| `DEVLOG_GHOST_ENABLED` | `devlog-ghost-release.yml` | `true` lets a `release: published` event post a Ghost draft | release-event runs are skipped; manual dispatch still works (and still needs the Ghost secrets). |

Variables that **used** to be referenced and are gone with their workflow:
`ASHLAR_WINDOWS_DOCKER_PERSISTENCE` (`test-persistence-multi-os.yml`, self-hosted Windows Docker
lane) and `ASHLAR_MESH_REMOTE_RUNNER` (`mesh-lab-remote-gate.yml`, tailnet runner label).

## What a fork sees, in practice

- **PR / push / schedule gates** (`cert-gate`, `kernel-gate`, `kernel-coverage-gate`,
  `layer-boundary`, `dependency-boundary`, `docs-link-check`, `testing-strategy-gate`,
  `security-gate`, `distribution-matrix-gate`, `compose-gate`, `devcontainer-gate`,
  `container-image-gate`, `full-platform-readiness-gate`, `onboarding-*`, `mcp-a2a-gate`,
  `shell-lint`, ...) read no secrets and no variables. They behave identically on a fork.
- **`container-image-publish.yml`** on a fork: the single job is skipped by the owner guard.
- **`release.yml`** on a fork: `validate` is skipped, everything downstream is skipped, the run
  shows green-with-skips. Remove the guard to publish images under the fork's GHCR namespace;
  set `NUGET_PUBLISH_MODE` plus the matching secret to also push NuGet.
- **`release-nuget.yml`** dispatched on a fork: skipped by the guard. Upstream, with no
  `NUGET_PUBLISH_MODE`, it is the artifact-only dry run used to exercise the release harness
  (first run ever on 2026-08-17 UTC: `0.1.0-ci.1`, 22 packages packed and hashed, consumer-sample
  restore from the local feed green, every push step skipped - see the `ci/workflow-pruning` PR).
- **`devlog-ghost-release.yml`**: inert on a fork unless someone sets `DEVLOG_GHOST_ENABLED` or
  dispatches it by hand.

Related: `docs/PUBLISHING.md` (how the upstream owner enables `NUGET_PUBLISH_MODE`),
`docs/StagingFeed.md` (staging feed pair), `docs/CiGateInventory.md` (which workflows exist and
what triggers them).
