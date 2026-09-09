# CI gate inventory

This file describes what CI **actually does** on this repository: which workflow files exist, what triggers each one, and which checks branch protection **really** requires. Workflow YAML controls when checks run; GitHub branch protection (a repository setting, not YAML) controls which check names must be green before merge. Where the two disagree, this file follows the settings and says so.

Snapshot: **57 workflow files** under `.github/workflows/` (`git ls-files ".github/workflows/*.yml"`), verified 2026-09-04. Includes `products-gate.yml` (added with the product-split scaffolds).

## Required checks (branch protection) — what is enforced today

`master` branch protection requires **four** status-check contexts:

| Context | Workflow | Runs on |
| --- | --- | --- |
| `cert-gate` | `.github/workflows/cert-gate.yml` (job `cert-gate`) | every `pull_request`, every push to `master`, `workflow_dispatch` — no path filter |
| `build-core` | `.github/workflows/build-gate.yml` (job `build-core`) | every `pull_request` — no path filter |
| `shell-lint` | `.github/workflows/shell-lint.yml` (job `shell-lint`) | every `pull_request` — no path filter |
| `lychee (README + docs)` | `.github/workflows/docs-link-check.yml` (job `lychee (README + docs)`) | every `pull_request` — no path filter |

Verified 2026-09-09 with `gh api repos/IanFrelinger/Ashlar/branches/master/protection` (`required_status_checks.contexts == ["cert-gate", "build-core", "shell-lint", "lychee (README + docs)"]`, `strict: true`, `enforce_admins: true`). Everything else in this document is **advisory**: a red `layer-boundary / verify`, `Kernel Gate / kernel-gate`, or `Full Platform Readiness Gate / Readiness summary` does not block a merge. Earlier revisions of this file listed 15 required contexts; that was never the repository setting.

**Intended next required context: `Readiness summary`** (`full-platform-readiness-gate.yml`). Since 2026-09-09 that workflow runs on **every** PR with no path filter: a first `changes` job diffs the PR against its merge-base and only runs the heavy platform lanes when a core path changed; otherwise the lanes are skipped and `Readiness summary` passes in about a minute. It therefore always reports and can be required without deadlock (a required context that never reports leaves a PR at "Expected — Waiting for status" forever, and `enforce_admins: true` means nobody can bypass it). Add it to branch protection only after one green `master` run confirms the change.

### Checks that are safe to require (always report on PRs)

These workflows have **no path filter** on `pull_request:` and will **always** post a check status, so they are safe to add as required contexts without blocking PRs that don't touch their relevant files:

| Check name (exact context for branch protection) | Workflow file | Job name | Notes |
| --- | --- | --- | --- |
| `cert-gate` | `cert-gate.yml` | `cert-gate` | **Currently required** — hermetic certification gate |
| `build-core` | `build-gate.yml` | `build-core` | **Currently required** — compiles `Ashlar.LocalDevCore.slnf` (Ashlar.CLI, Tests.Domain, Tests.Infrastructure); no tests |
| `shell-lint` | `shell-lint.yml` | `shell-lint` | **Currently required** — parse errors + shellcheck -S error; ~5s |
| `lychee (README + docs)` | `docs-link-check.yml` | `lychee (README + docs)` | **Currently required** — external link validation; ~30s |
| `verify` | `layer-boundary.yml` | `verify` | Kernel-first layer boundary (paths: "**"); deliberate exemptions exist |
| `uat` and `uat cross-platform` | `uat-gate.yml` | `uat`, `uat cross-platform` | Always runs (no paths); UAT smoke tests |
| `Readiness summary` | `full-platform-readiness-gate.yml` | `readiness-summary` | Always reports (in-job path filter via the `changes` job; heavy lanes skipped when no core path changed, ~1 min); **intended next required context** once one green `master` run confirms it |

**Action for CEO/admins:** `cert-gate`, `build-core`, `shell-lint` and `lychee (README + docs)` are already required (verified via `gh api repos/IanFrelinger/Ashlar/branches/master/protection`, 2026-09-09). The next candidate is `Readiness summary`: after one green `master` run of the readiness gate with the in-workflow `changes` filter, add it to the `contexts` array in the snippet below. Note that the PATCH **replaces the whole array**, so every context that must stay required has to be listed.

### Why the other gates are not required (and cannot simply be added)

Almost every other PR-triggered gate uses `paths:` filters. GitHub only reports a status for a path-filtered workflow when the filter matches; a PR that does not touch those paths gets **no** status at all, and a required context that never reports blocks the merge forever. Promoting a path-filtered gate to "required" therefore needs one of:

1. an **always-report job** — a lightweight job in the same workflow with no path filter that emits success when the filtered job was skipped (GitHub's documented "handling skipped but required checks" pattern), or
2. dropping the path filter and paying the run cost on every PR (this is what `cert-gate` does), or
3. moving the path filter inside the job (`dorny/paths-filter` or a `git diff` step) so the workflow always runs and always reports.

Until one of those lands per gate, only unfiltered checks are safe to require. `layer-boundary` uses `paths: ["**"]` — effectively unfiltered — and is the one other gate that could be required today without an always-report job; see [`CONTRIBUTING.md`](../CONTRIBUTING.md) ("Layer boundary and what master actually enforces") for why it is not yet.

### Branch protection update snippet

Human runs this; agents cannot change repository settings. The `contexts` array below lists the **four currently required checks**. `Readiness summary` is the next candidate — add `"Readiness summary"` to the array only after one green `master` run confirms the gate reports on every PR (heredoc bodies must stay valid JSON, so there is no commented-out placeholder). The PATCH replaces the whole array: omitting an existing context silently un-requires it.

```bash
OWNER="IanFrelinger"
REPO="Ashlar"
BRANCH="master"

cat > /tmp/ashlar-required-checks.json <<'JSON'
{
  "required_status_checks": {
    "strict": true,
    "contexts": [
      "cert-gate",
      "build-core",
      "shell-lint",
      "lychee (README + docs)"
    ]
  }
}
JSON

gh api --method PATCH -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28" "/repos/$OWNER/$REPO/branches/$BRANCH/protection" --input /tmp/ashlar-required-checks.json
```

## Trigger map

Counts by trigger class (57 files):

| Class | Count | Meaning |
| --- | --- | --- |
| Runs on `pull_request` | 16 | 6 unfiltered (`cert-gate`, `shell-lint`, `docs-link-check`, `layer-boundary`, `uat-gate`, `full-platform-readiness-gate` — path filter inside the workflow), 9 path-filtered (including `products-gate`), 1 label-driven (`release-staging-on-label`) |
| Push- and/or schedule-driven (path-filtered on `master`/`main`/`cursor/**`), plus `workflow_dispatch` | 19 | Post-merge / scheduled signal; never blocks a PR |
| `workflow_dispatch` only | 17 | Manual lanes (mesh labs, multi-env Docker suites, ship/ops/perf, release plumbing) |
| Tag / release event | 2 | `release.yml` (`v*.*.*` tags), `devlog-ghost-release.yml` (`release: published`) |
| Reusable (`workflow_call`) | 3 | `reusable-*` |

Five workflows carry a `schedule`: `distribution-matrix-gate` (Mon 10:00 UTC), `full-platform-readiness-gate` (Mon 06:00), `onboarding-quickstart-gate` (Mon 07:00), `rc-gate` (06:00 on the 1st of each month), `mesh-lab-tls-gate` (Tue 07:00). (`mesh-lab-stress-gate` lost its schedule 2026-08-16 and is dispatch-only.)

### PR-triggered workflows

| Workflow file | Name / job(s) | PR trigger | Also |
| --- | --- | --- | --- |
| `cert-gate.yml` | Cert gate / `cert-gate` | **every PR** (no paths) | push `master`, dispatch — **required** |
| `shell-lint.yml` | Shell lint / `shell-lint` | **every PR** (no paths) — **safe to require** | dispatch — always reports; cheap parse/lint check |
| `docs-link-check.yml` | Docs Link Check / `lychee (README + docs)` | **every PR** (no paths) — **safe to require** | push, dispatch — always reports; ~30s lychee run |
| `layer-boundary.yml` | layer-boundary / `verify` | every PR (`paths: "**"`, types opened/synchronize/reopened/edited) | — |
| `full-platform-readiness-gate.yml` | Full Platform Readiness Gate / `changes`, platform lanes, `Readiness summary` | **every PR** (no paths; types opened/synchronize/reopened/ready_for_review) — `changes` job runs the heavy lanes only when a core path is in the diff (Dockerfiles, setup/install scripts, spine sources, CLI + CLI tests, `commercial/**`, Orchestration/Kernel test projects, StableSdkHostSample); otherwise `Readiness summary` passes in ~1 min. Production images (`docker-all-images`) never build on PRs. | push `master`/`main`/`cursor/**` (same path list), **weekly schedule**, dispatch |
| `uat-gate.yml` | UAT / `uat`, `uat cross-platform` | **every PR** (no paths — deliberate, see file header) | push `master`, dispatch |
| `application-gate.yml` | Application Gate / `application-gate` | paths: `application/**`, VirtualProduction tests, `scripts/application-gate*.sh`, `scripts/prod-dry-run.sh`, `Makefile`, … | dispatch |
| `dependency-boundary.yml` | dependency-boundary / `verify` | paths: `**/*.csproj`, `commercial/**`, `application/**`, `src/**`, `LICENSING.md`, boundary scripts | push, dispatch |
| `distribution-matrix-gate.yml` | Distribution Matrix Gate / 7 jobs | paths: same broad list as push (Dockerfiles, pack/verify scripts, Ashlar.API/CLI, Client/Sdk/Hosting.Bundle/Authoring/Brick.Contracts, samples, VirtualProduction tests) | push (broad paths), weekly schedule, dispatch |
| `kernel-coverage-gate.yml` | Kernel coverage gate / `kernel-coverage` | paths: kernel src + tests, `scripts/ci/kernel-coverage-gate.sh`, `scripts/ci/pr-testing-strategy-gate.sh` | push |
| `kernel-gate.yml` | Kernel Gate / `kernel-gate` | paths: `src/Ashlar.Hosting/**`, Infrastructure, Orchestration, Runtime, Core.Application, kernel tests, `docs/production-readiness/**`, `Makefile` | push (narrower paths), dispatch |
| `security-gate.yml` | Security Gate / `security-gate` | paths: Trust/Security sources and tests, `scripts/security-gate*.sh`, `Makefile` | dispatch |
| `testing-strategy-gate.yml` | Testing strategy gate / `testing-strategy` | paths: `src/**`, `application/**`, `scripts/**`, `.github/**`, `Makefile`, `docs/architecture/TestingStrategy*.md` | — |
| `release-staging-on-label.yml` | Release staging on label / `dispatch-staging-release` | `types: [labeled]` only | — |
| `products-gate.yml` | products-gate / `product scaffolds` | paths: `products/**`, distributed contracts, deployment-profile sources, `ci/test-ownership.tsv` | push `master`/`main`/`cursor/**`, dispatch — **advisory**; runs `products/Ashlar.Products.sln` plus `DistributedContractTests`. Does **not** run the dependency-boundary script (that is `dependency-boundary.yml`). |
| `portability-gate.yml` | Portability Gate | paths: `application/src/Ashlar.CLI/**`, `src/Ashlar.Manifest/**`, `scripts/e2e-loop.sh` | dispatch |

### Push-only (path-filtered) workflows

All of these also accept `workflow_dispatch`. Branch filters are `master`, `main`, `cursor/**` unless noted.

| Workflow file | Name | Notes |
| --- | --- | --- |
| `compat-gate.yml` | compat-gate | `master` only; `scripts/compat-gate*.sh`, `scripts/kernel-gate-tier-b.sh` |
| `compose-gate.yml` | Compose Gate | compose test stacks, `.docker/Dockerfile.test-caching*`, CLI, README |
| `container-image-gate.yml` | Container Image Gate | `.docker/Dockerfile.cli`, CLI + spine sources |
| `container-image-publish.yml` | Container Image Publish | `master`/`main`; `.docker/**`, hosts, spine sources — publishes GHCR images |
| `devcontainer-gate.yml` | Dev Container Gate | `.devcontainer/**`, `Ashlar.LocalDevCore.slnf`, CLI |
| `dr-gate.yml` | dr-gate | `master` only; `scripts/dr-gate*.sh` |
| `environment-setup-gate-v1.yml` | Environment Setup Gate v1 | `master`/`main`; `scripts/setup/**`, CLI |
| `friend-mesh-prefab-gate.yml` | Friend mesh prefab gate | friend-mesh compose, `.docker/Dockerfile.api`, `Ashlar.API` |
| `grpc-transport-gate.yml` | gRPC transport gate | `src/Ashlar.Transport.Grpc/**`, `src/Ashlar.Tests.Transport/**` |
| `mcp-a2a-gate.yml` | MCP + A2A protocol gate | also `application/**` branches; `src/Ashlar.Mcp.*`, `src/Ashlar.Transport.A2A*`, `Ashlar.API` |
| `onboarding-docs-guard.yml` | Onboarding Docs Guard | README, `docs/**/*.md`, `scripts/*.sh`, `scripts/*.ps1`, `Makefile`, `**/*.csproj` (ProjectTiers guard) |
| `onboarding-quickstart-gate.yml` | onboarding-quickstart-gate | README, GettingStarted, setup/install scripts, CLI; **weekly schedule** |
| `optimize-agent-cluster-gate.yml` | Optimize Agent Cluster Gate | `apps/runtime-studio/**`, `scripts/sandbox/**`, CLI |
| `pack-hosting-graph-alignment.yml` | Pack hosting graph alignment | `master`/`main`; `src/**/*.csproj`, pack scripts, NugetOrgRestoreVerify sample |
| `perf-gate.yml` | perf-gate | `master` only; `scripts/perf-gate*.sh`, Orchestration/BackgroundAgents tests |
| `production-readiness-gate-v1.yml` | Production Readiness Gate v1 | pipelines sources/tests, CLI, readiness docs |
| `rc-gate.yml` | RC Gate | `master`/`main`; RC docs + scripts; **monthly schedule** |
| `runtime-release-gate.yml` | Runtime Release Gate | `master`/`main`; CLI runtime/release commands, `docs/runtime/benchmarks/**` |

### Manual-only workflows (`workflow_dispatch`)

`composition-mesh-gate`, `cross-platform-tests`, `installer-bruteforce-gate`, `mesh-lab-gate`, `nuget-consumer-verify`, `ops-gate`, `perf-certification`, `prod-dry-run-pr`, `release-nuget`, `runtime-release-promotion`, `setup-smoke-suite`, `ship-gate`, `test-air-gapped-no-network`, `test-trust-multi-env`, `waterproofing-gate`, `workflow-regression-gate` (16).

Despite their names, **`cross-platform-tests`** and **`prod-dry-run-pr`** do not run on PRs; run them with `gh workflow run "<name>" --ref <branch>`.

### Scheduled-only, tag/release, reusable

| Workflow file | Trigger |
| --- | --- |
| `mesh-lab-stress-gate.yml` | dispatch only (schedule removed 2026-08-16) |
| `mesh-lab-tls-gate.yml` | `schedule` Tue 07:00 UTC + dispatch |
| `release.yml` | push tags `v*.*.*` + dispatch |
| `devlog-ghost-release.yml` | `release: published` + dispatch |
| `reusable-container-publish.yml`, `reusable-release-nuget.yml`, `reusable-verify-nuget-consumer.yml` | `workflow_call` |

## Coverage floors as enforced

`scripts/ci/kernel-coverage-gate.sh` (run by `kernel-coverage-gate.yml`) enforces **Domain 100% / Infrastructure 80% / Core.Application 67%** line coverage (`INFRA_COVERAGE_THRESHOLD` default 80, measured ~80.3%, target 83 — see [`production-readiness/KernelCoverageGate-Findings.md`](production-readiness/KernelCoverageGate-Findings.md)). Neither check is required by branch protection.

## Policy

- Four checks are merge-blocking: `cert-gate`, `build-core`, `shell-lint`, and `lychee (README + docs)`. Treat every other gate as a review signal and read red checks before merging; that is a process rule, not a setting.
- To promote a gate to required, first make it always report on PRs (always-report job or in-job path filtering), then add its context to branch protection and to the table at the top of this file in the same change.
- Release workflows (`release*`, `runtime-release*`, `rc-gate`, `reusable-*`) are not PR branch-protection checks.
- Branch protection is not represented by YAML; when the setting changes, update this file (and [`GitHubBranchProtection.md`](GitHubBranchProtection.md)) in the same PR.

## Pruning (2026-08-16)

Every workflow file was classified from `gh run list --workflow <file> --limit 15 --json conclusion,createdAt,event` plus its `on:` block (PR `ci/workflow-pruning`; the full 62-row table is in that PR's description). Classes: **active-green**, **active-flaky**, **dead** (no run in 60 days and no `push`/`pull_request`/`schedule` trigger that can fire), **duplicate**, **always-red**. At the time of pruning (2026-08-16), only `cert-gate` was required by branch protection; as of 2026-09-09, four checks are required: `cert-gate`, `build-core`, `shell-lint`, and `lychee (README + docs)` (verified with `gh api repos/IanFrelinger/Ashlar/branches/master/protection`).

**Deleted (7)** — recoverable from git history at `71963059`:

| File | Why |
| --- | --- |
| `core-domain-coverage.yml` | duplicate: identical `dotnet test src/Ashlar.Tests.Domain … /p:Threshold=100` step to the first leg of `scripts/ci/kernel-coverage-gate.sh`, same PR/push paths |
| `runtime-studio-playground.yml` | duplicate of `cross-platform-tests.yml` `scope=playground` (same 3-OS matrix, same filters); 4/4 red, last run 2026-05-11, dispatch-only |
| `test-persistence-multi-os.yml` | duplicate of `cross-platform-tests.yml` `scope=persistence`; 15/15 red — tests pass, the `publish-unit-test-result-action` step 403s on `check-runs` |
| `test-caching-multi-env.yml` | always-red (14 red + 1 cancelled of 15), muted 2026-08-11, dispatch-only; the `Dockerfile.test-caching*` images it built are still validated by `compose-gate.yml` |
| `mapbox-tile-helpers-ci.yml` | always-red (15/15), dispatch-only; the job-level `if: secrets.MAPBOX_ACCESS_TOKEN != ''` is not a valid context there. Tests remain runnable locally with `ASHLAR_TEST_MAPBOX_TILES=1` |
| `runtime-studio-forge-smoke.yml` | dead: dispatch-only, last run 2026-06-14, referenced nowhere |
| `mesh-lab-remote-gate.yml` | dead: never dispatched, needs five repository secrets and a tailnet runner; `scripts/mesh-lab-verify-remote.sh` is the supported path |

**Marked `# DORMANT:` (7)** — top-of-file comment with the reason and date; `workflow_dispatch` stays live:

| File | Why |
| --- | --- |
| `cross-platform-tests.yml` | push trigger commented out 2026-08-11 (15/15 red on a product assertion, issue #252); only Windows/macOS matrix in the repo |
| `mesh-lab-gate.yml` | push trigger commented out 2026-08-11 (15/15 red in the compose environment); mesh-lab entry point |
| `mesh-lab-stress-gate.yml` | **weekly schedule removed** after eight consecutive red runs (2026-06-22 .. 2026-08-10) |
| `runtime-release-promotion.yml` | 11 of last 14 red, last run 2026-05-11; kept because `scripts/rc-gate-tier-d.sh` lists it as an optional RC signal |
| `test-air-gapped-no-network.yml` | never green (11/11 red since 2026-03-08; last failure is MSB1011 from the `ashlar test multi-env` step); cited by hardening plans, so kept as an unproven claim |
| `test-trust-multi-env.yml` | dead by the 60-day rule (last dispatch 2026-05-23, mostly green); cited by `KernelHardeningPlan-v1.md` C1 |
| `workflow-regression-gate.yml` | dead by the 60-day rule (last dispatch 2026-06-14, green); only end-to-end run of `ashlar workflow baseline|report|gate` |

**Kept as-is although rarely run** (all have a live path/manual trigger and a Makefile/script/runbook that names them): `compat-gate`, `dr-gate` (path-triggered on their scripts, one green run each), `composition-mesh-gate`, `waterproofing-gate`, `perf-certification`, `installer-bruteforce-gate` (dispatched by `scripts/rc-gate-tier-d.sh`), `nuget-consumer-verify` (post-publish check, `docs/NuGetConsumerVerify.md`), `setup-smoke-suite` (`docs/CiFirstHardwareSecond.md`), `devlog-ghost-release`, `mesh-lab-tls-gate` (weekly, latest run green).

**Not folded:** the CLI image is built by `container-image-gate` (local + multi-arch cache-only), `distribution-matrix-gate` (build + `--help` smoke), `full-platform-readiness-gate` and `reusable-container-publish` (push to GHCR). Each build differs in platform, output and smoke, and three of the four are on the protected list, so a shared reusable job is deferred to its own PR.

**Fork safety (L10):** `container-image-publish.yml`, `release.yml`, `release-nuget.yml` and `release-staging-on-label.yml` now carry `if: github.repository_owner == 'IanFrelinger'`; every secret and repository variable a workflow reads, and what a fork gets without it, is in [`CiSecrets.md`](CiSecrets.md).
