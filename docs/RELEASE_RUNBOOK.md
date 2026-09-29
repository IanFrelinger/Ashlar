# Release runbook (operator)

## Fastest path (local, one command)

From repo root, with the **semver you are about to ship** (no `v`):

```bash
bash scripts/release-preflight-local.sh 1.2.3
# or:  make release-preflight VERSION=1.2.3
# or:  dotnet run --project application/src/Ashlar.CLI -- release preflight 1.2.3
```

That runs **pack-graph alignment** + **NuGet consumer sample** (isolated cache). Then push tag **`v1.2.3`** so **`release.yml`** runs (see table below).

Optional: also fire CI **`Runtime Release Gate`** from your machine (needs **`gh auth login`**):

```bash
ASHLAR_RELEASE_PREFLIGHT_TRIGGER_GATE=1 ASHLAR_RELEASE_PREFLIGHT_REF=master bash scripts/release-preflight-local.sh 1.2.3
# or:  dotnet run --project application/src/Ashlar.CLI -- release preflight 1.2.3 --trigger-gate --gate-ref master
# or anytime:  make release-gate   /   dotnet run --project application/src/Ashlar.CLI -- release gate
```

**Dispatch without a tag** (same workflow as tag, from a branch; needs `gh auth login`):

```bash
dotnet run --project application/src/Ashlar.CLI -- release dispatch 1.2.3 --ref master
# or:  make release-dispatch VERSION=1.2.3 REF=master
```

---

Which workflow do I run?

| Goal | Workflow | Notes |
|------|-----------|--------|
| **Ship everything** (GHCR + NuGet) | Push **`vX.Y.Z`** → **`release.yml`** | Preferred. Post-push NuGet checks + optional GHCR re-pull smoke. |
| **NuGet only** | **Actions → Release NuGet packages** → **`release-nuget.yml`** | Register **`release-nuget.yml`** for OIDC if you use it. |
| **Images from `main` only** | **`container-image-publish.yml`** | Rolling `sha-*` / `latest`; no NuGet. |

Trusted Publishing: register **`release.yml`** and **`release-nuget.yml`** as needed — see `docs/PUBLISHING.md`.

**Repo variables & branch protection:** `docs/GitHubRepoVariables.md`, `docs/GitHubBranchProtection.md`.

**Tracking:** open **New issue → Release checklist** (`.github/ISSUE_TEMPLATE/release_checklist.yml`) or use the **Release** section in the PR template when this PR ships a version.

## Before you tag

1. **Green CI** on the commit — run **`runtime-release-gate`** on that ref.
2. **`python3 scripts/verify-pack-ashlar-hosting-graph-alignment.py`** after changing `Ashlar.Hosting` refs or pack scripts.
3. **`bash scripts/verify-stable-sdk-host-sample-packages.sh`** with `ASHLAR_SDK_PACKAGE_VERSION` (isolated cache + `--force-evaluate` by default).
4. **Bump the three version files on the release commit, together.** `release.yml` refuses the
   tag unless they agree, and each is checked by a different guard:

   | file | what it controls | guard |
   |---|---|---|
   | `VERSION` | stamps every assembly and package | `assert_version_matches_canonical` |
   | `ci/published-version` | what `consumer-template` and the docs pin a stranger to | `assert_consumer_pin_matches` |
   | `consumer-template/Directory.Packages.props` (`AshlarConsumerPackageVersion`) | the version a copied template actually installs | C6 lint (`scripts/verify-docs-published-version.sh`) |

   `ci/published-version` names what is **on nuget.org**, so between releases it trails `VERSION`
   rather than leading it. It moves on the release commit, not before.

5. **Rewrite `consumer-template/CONSUMING.md`'s trust rows — do not find-and-replace the version.**
   That document states the verification behaviour *of the version it pins*. On `0.1.2` the
   statement "`Strict` leaves `RequireEd25519Signature` **false**" is correct. From `0.2.0` it is
   **false**: `CertificationVerifyOptions.Default` *and* `.Strict` both set
   `RequireEd25519Signature = true` and a `MinimumSchemaVersion` floor
   (`src/Ashlar.Certification.Contracts/CertificationVerifyOptions.cs`). Changing the pin without
   rewriting those rows publishes a document that tells a consumer the opposite of what the package
   does. See the `### Breaking` entry for `CertificationVerifyOptions` in `CHANGELOG.md`.

6. **Bump the docs that name a published version.** `ci/published-version` becoming equal to
   `VERSION` *silences* the C6 clause that catches stale pins — it only fires when the two disagree
   — so the sweep is manual from this point and nothing will flag a miss:
   `consumer-template/CONSUMING.md`, `docs/AuthoringBricks.md`, `docs/DistributionModels.md`,
   `docs/certification-evidence.md`, `docs/TesterQuickstart.md`, `README.md` (which is packed into
   every package, so a stale pin there ships), `assets/brand/BRAND.md`, and **`SECURITY.md`'s
   supported release line**. Leave statements scoped to a past release ("on `0.1.2` and earlier")
   alone — they stay true.

7. **Promote the public API**: review each stable-tier project's `PublicAPI.Unshipped.txt`, move its lines into `PublicAPI.Shipped.txt`, commit on the release commit (`docs/SdkCompatibilityPolicy.md`, "Release step"). After the tag those lines are the promise.
8. **Rehearse the publish with a prerelease before tagging.** Dispatch `release-nuget.yml` with
   version `X.Y.Z-rc1` from the release commit. Both guards accept it -
   `assert_dispatch_version_allowed` permits a prerelease of the canonical version, and
   `assert_consumer_pin_matches` compares `base_semver`, so `0.2.0-rc1` matches a pin of `0.2.0`.
   It runs pack, both pre-publish gates, the real OIDC push and every post-push check against
   nuget.org. **Cost: one permanent `X.Y.Z-rc1` prerelease**, which no `X.Y.Z` pin resolves to.
   Skip this and the tag is the first time the post-push chain runs against a live feed, on the
   one trigger where a failure cannot be taken back. The staging-feed path does not substitute: the
   post-push scripts target nuget.org, and `NUGET_STAGING_FEED_URL` is unset.

## After `release.yml`

1. **Re-pin `deploy/node.yml` to the new image digest.** It pins `nexo-cli` by `sha256:`, which
   cannot be known until the tag publishes, so it is the one version reference that must move
   *after* the release rather than on the release commit. `README.md`'s operator row points at it.

2. Workflow **Summary** — image `sha-*` tags, NuGet version, cross-verify status.
3. Artifact **`nuget-packages-<version>`** — includes **`nuget-publish-manifest.json`** and per-`.nupkg` **`.sha256.txt`** for audit / manual hash checks.
4. Optional **`nuget-sbom-<version>`** if **`NUGET_RELEASE_SBOM=true`** on the repo.

## If something went wrong

- **Partial NuGet push** — Re-run with **`--skip-duplicate`**; unlist bad versions per policy.
- **Forks** — Default **`GITHUB_TOKEN`** in forks often cannot push to **`ghcr.io/<upstream>/...`**; run releases in **upstream** or use a **PAT** with `packages: write`.
