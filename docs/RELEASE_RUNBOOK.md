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
9. **Confirm the readiness gate verified the exact commit you are tagging — read the verdict, not
   the tick.** `Readiness summary` is required, so it has to pass when its platform lanes are
   skipped, and its green tick cannot tell "every platform passed" from "no platform ran". This is
   the last step because steps 4–7 change the release commit; check the SHA you will actually tag:

   ```bash
   git fetch origin
   SHA=$(git rev-parse origin/master)   # or whichever commit you will tag - in full
   bash scripts/release/readiness-verdict-for-sha.sh "$SHA"
   ```

   It reads the `Readiness verdict` annotation that each `Readiness summary` check run carries
   (read-only; needs `gh auth login`), prints every run it found, and exits 0 only on a `VERIFIED`
   read from GitHub. The last line is the outcome:

   | last line | means | do |
   |---|---|---|
   | `VERIFIED`, exit 0 | every lane group ran and passed on this exact commit | tag it. Only this exits 0. A `VERIFIED` line that ends `[offline fixture - not evidence]` exits 3: it read a test fixture, not GitHub, so unset `READINESS_VERDICT_FIXTURE` and run it again |
   | `REFUSED (no-readiness-run)` | nothing ran on this SHA — usually the path filter, below | get evidence, below |
   | `REFUSED (not-verified)` | a run exists but every lane was skipped | get evidence, below |
   | `REFUSED (partial)` | only a `pull_request`/`merge_group` run: production images never built | get evidence, below |
   | `REFUSED (merge-commit-only)` | the only `verified` run is a `pull_request` run, which tested this commit merged into its base, not this commit | get evidence, below |
   | `REFUSED (failed)` or `(conflicting-verdicts)` | a lane failed or was cancelled in some run of this SHA; a pass in another run does not outweigh it | open that run; if a newer push cancelled it or it hit a known flake, re-run **that** run with `gh run rerun <run id>` (its latest attempt supersedes the failure), but only as *Re-running a `master` run*, below, allows; otherwise do not tag. A dispatch on a throwaway branch cannot clear this: the failed run stays attached to the SHA, and a `verified` dispatch beside it is `conflicting-verdicts` |
   | `REFUSED (in-progress)` | a run of this SHA has not finished and could still fail | wait, run the script again |
   | `REFUSED (missing-annotation)` | the run has no verdict annotation: the summary job was cancelled early, or the commit predates the annotation | re-run it, as *Re-running a `master` run*, below, allows; a commit that predates the annotation cannot be certified by the script — read the verdict in the run summary and say so in the release notes |
   | `REFUSED (malformed-annotation)`, `(foreign-check-run)`, `(sha-mismatch)` | the evidence is not trustworthy as it stands | stop and investigate; do not tag around it |
   | `UNDETERMINED (...)`, exit 2 | the script could not read the evidence (gh unauthenticated, API error, a short SHA) | fix that and run it again — this is not a pass |

   `partial` is refused, with no override. Push, schedule and `workflow_dispatch` runs execute every
   lane group, so a partial verdict only ever comes from a pull request or merge-queue run, which
   never builds the production Docker images.

   **Re-running a `master` run.** A failed run is the likeliest refusal on a release SHA, not a
   corner case: a merge that touches a readiness path cancels the readiness run still in progress
   for the commit before it, and that run's `Readiness summary` still runs and records `failed`.
   Observed: `master` push run 36637044333 on `f34a9f67` was cancelled 25 minutes in by the push
   of the `0.2.0` release commit, and its `Readiness summary` concluded `failure`. The fix is a
   re-run of *that* run, and a re-run is not free. It keeps the original run's ref, so a re-run of
   a push, schedule or dispatch run of `master` joins `master`'s concurrency group
   (`full-platform-readiness-refs/heads/master`, `cancel-in-progress: true`): starting it cancels
   any `master` readiness run in flight — which then records `failed` on its own commit — and the
   next merge that touches a readiness path cancels the re-run. So re-run only when this shows
   nothing queued or in progress, and hold readiness-path merges until the re-run has completed:

   ```bash
   gh run list --workflow full-platform-readiness-gate.yml --branch master --limit 5
   gh run rerun <run id>
   ```

   The throwaway-branch dispatch below does **not** clear a failed run. The failure stays attached
   to the SHA, and a `verified` dispatch beside it is `conflicting-verdicts`; only a later attempt
   of the failed run itself supersedes it.

   **The path-filter wrinkle.** On push to `master` the readiness gate is path-filtered: a commit
   whose diff touches no readiness path (a docs-only commit, for example) gets **no run at all**, so
   there is nothing to read and the script refuses. A release commit usually does get one, because
   it bumps `VERSION` and that is a readiness path; a docs fix-up merged after it does not. To get
   evidence for such a SHA, dispatch the gate on a branch pinned to it:

   ```bash
   # $SHA as above
   git push origin "$SHA:refs/heads/readiness-evidence/${SHA:0:12}"
   gh workflow run full-platform-readiness-gate.yml --ref "readiness-evidence/${SHA:0:12}"
   # ...when that run has completed:
   bash scripts/release/readiness-verdict-for-sha.sh "$SHA"
   git push origin --delete "readiness-evidence/${SHA:0:12}"
   ```

   A dispatch runs every lane group unconditionally, but `gh workflow run --ref` accepts a branch or
   tag, not a SHA, and the run evaluates whatever that ref points at when it starts. Dispatching on
   `master` is evidence for the SHA only if `master` has not moved by then, and a `master` dispatch
   shares the push runs' concurrency group, so the next merge that starts a readiness run cancels
   it. A throwaway branch pinned to the SHA has neither problem. Never use a `v*` tag for this:
   that is the release trigger. The script only reads check runs attached to the exact SHA, so a
   dispatch that resolved to some other commit can never be counted as evidence for this one.

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
