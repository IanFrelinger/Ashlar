# GitHub repository variables (release / NuGet)

Set these under **Repository → Settings → Secrets and variables → Actions → Variables** (not secrets unless noted). Use the same names on **forks** only if you publish from the fork; upstream releases usually set them on the **main** repo.

| Variable | Values | When |
|----------|--------|------|
| **`NUGET_PUBLISH_MODE`** | `none`, `oidc`, `apikey` | Controls whether **`reusable-release-nuget.yml`** pushes to nuget.org. `none` = artifact only. |
| **`NUGET_POST_PUSH_VERIFY`** | unset / `true` / `false` | Set **`false`** to skip post-push flat-container, registration, SHA256 download check, and nuget.org-only restores. |
| **`NUGET_POST_PUSH_VERIFY_PACKAGE_IDS`** | e.g. `Ashlar.Hosting.Bundle,Ashlar.Hosting,Ashlar.Sdk,Ashlar.CLI` | Comma list for visibility + registration polls (default if empty). |
| **`NUGET_POST_PUSH_ATTEMPTS`** | e.g. `80` | Poll rounds (empty = script default 80; values below 80 are raised unless `ASHLAR_NUGET_VERIFY_ALLOW_SHORT=1`). |
| **`NUGET_POST_PUSH_SLEEP_SEC`** | e.g. `15` | Seconds between polls. |
| **`NUGET_RELEASE_SBOM`** | `true` / unset | When **`true`**, Syft generates SPDX JSON per `.nupkg` and uploads **`nuget-sbom-<version>`**. |
| **`NUGET_RELEASE_GRYPE`** | `true` / unset | With SBOM, runs Grype (non-blocking for the workflow). |
| **`RELEASE_CROSS_VERIFY`** | unset / `true` / `false` | Set **`false`** to skip **`release.yml`** job that re-pulls GHCR **`sha-*`** images for CLI/API smoke. |
| **`NUGET_STAGING_FEED_URL`** | e.g. `https://nuget.pkg.github.com/Org/index.json` | Optional **staging** feed; packages push here **before** nuget.org (`docs/StagingFeed.md`). |
| **`RELEASE_CREATE_GITHUB_RELEASE`** | unset / `true` / `false` | Set **`false`** to skip **draft GitHub Release** creation on **tag** runs of **`release.yml`**. |

**Secrets** (same settings page → Secrets):

| Secret | Purpose |
|--------|---------|
| **`NUGET_USER`** | nuget.org **profile name** (OIDC + `NuGet/login@v1`). |
| **`NUGET_API_KEY`** | Push when `NUGET_PUBLISH_MODE=apikey`. |
| **`NUGET_STAGING_API_KEY`** | Push to **`NUGET_STAGING_FEED_URL`** when that variable is set. |
| **`RELEASE_NOTIFICATION_WEBHOOK_URL`** | Optional HTTPS URL for a **JSON POST** when **`release.yml`** finishes (success if validate+images+nuget all succeeded). Payload: `repository`, `version`, `status`, `run_id`, `run_url`, `workflow`. |

## Org / new repository template

For **new repos** under your GitHub org, add a **repository template** or **org variable** documentation so teams copy the table above. GitHub does not inherit variables from org to child repos automatically for arbitrary names; document the required set in onboarding or a parent “platform ops” repo.
