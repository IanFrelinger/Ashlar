# GitHub Actions — trigger policy

Workflows in this directory are **manual-first** to reduce duplicate CI load, surprise
minute costs, and branch-noise on `cursor/**` and other integration branches. The full,
per-file trigger map lives in [`docs/CiGateInventory.md`](../../docs/CiGateInventory.md);
the summary of the 56 files is:

- **18 run on `pull_request`** — only `cert-gate`, `layer-boundary`, `uat-gate`, `build-gate`,
  `shell-lint`, `docs-link-check` and `full-platform-readiness-gate` on every PR; the rest are
  path-filtered (kernel/application/security/coverage/testing-strategy/other path-scoped gates)
  plus the label-driven `release-staging-on-label`. `full-platform-readiness-gate` filters
  paths *inside* the workflow (a `changes` job) so its `Readiness summary` check always reports:
  heavy platform lanes run only when a core path changed, otherwise the summary passes in ~1 min.
- **19 run on `push` only** (path-filtered, `master`/`main`/`cursor/**`), all with
  `workflow_dispatch` as well — post-merge signals such as `mcp-a2a-gate`, `grpc-transport-gate`,
  `onboarding-docs-guard`, `container-image-publish`.
- **17 are `workflow_dispatch` only**, including `cross-platform-tests` and `prod-dry-run-pr`
  despite their names: run them from the Actions tab or with
  `gh workflow run "<Workflow name>" --ref <branch>`.
- **Tag-driven releases** stay automatic where required (`release.yml` on `v*.*.*` tags,
  `devlog-ghost-release.yml` on published releases).
- **Schedules** still exist on five workflows: `distribution-matrix-gate` (Mon 10:00 UTC),
  `full-platform-readiness-gate` (Mon 06:00), `onboarding-quickstart-gate` (Mon 07:00),
  `rc-gate` (06:00 on the 1st of the month) and `mesh-lab-tls-gate` (Tue 07:00). Everything else is push- or dispatch-driven.

When you change a workflow file, open a PR and run the relevant workflow(s) manually
before merge if your branch protection expects a green check from that workflow.

## Branch protection (recommended after workflow changes)

`master` currently requires five status checks: **`cert-gate`**, **`build-core`**, **`shell-lint`**,
**`lychee (README + docs)`** and **`Readiness summary`** (all run on every PR and always report;
verified 2026-09-09 with `gh api repos/IanFrelinger/Ashlar/branches/master/protection`). Every
other gate is advisory.

**`Readiness summary`** (`full-platform-readiness-gate.yml`) became the fifth required context on
2026-09-09. It could not be required while the workflow was path-filtered — a required context
that never reports deadlocks the PR — so #571 made it run on every PR with the path filter inside
the workflow (the `changes` job, see above); it always reports, and it was added to branch
protection after one green `master` run of the changed workflow confirmed the behaviour.

### CI Hardening (September 2026)

To eliminate the cert-gate SPOF, the following workflows run on every PR and always report,
and are now required checks (added by a repository administrator):

- **`build-core`** — fast compile check (~2–3 min) that catches build breakage before heavier tests run
- **`shell-lint`** — bash syntax + shellcheck (~30s) that prevents ops breakage
- **`lychee (README + docs)`** — doc link validation (~30s) that keeps documentation healthy
- **`cert-gate`** — hermetic certification tests (existing required check)
- **`Readiness summary`** — full-platform readiness gate; added 2026-09-09 after #571 made it report on every PR (~1 min when no core path changed)

**Rationale:** If `cert-gate` is cancelled, flaky, or times out, the other four required checks
still prevent merge of broken code. The three September checks add <5 min in total; `Readiness summary`
adds ~1 min when no core path changed and the full platform matrix (~20 min) when one did.

**Done (repo admin):** `build-core`, `shell-lint`, `lychee (README + docs)` and `Readiness summary`
are in the `master` branch protection rule alongside `cert-gate` (see above).

Path-filtered workflows cannot be made required
without an always-report job — a required context that never reports blocks the merge.
If you want to require more, either:

1. Give the path-filtered workflow an **always-report job** (or move the path filter inside
   the job) so it reports on every PR, then add its context, or
2. Add a small **always-on** workflow (for example `dotnet build` + one smoke test project)
   and require only that check on PRs, or
3. Keep manual gates: maintainers run the relevant workflow from **Actions** before merge.

## Finding a workflow

Use the GitHub **Actions** tab or the GitHub CLI, for example:

```bash
gh workflow list
gh workflow run "Cross-Platform Tests" --ref <branch> -f scope=smoke
```

Replace `<branch>` with your default branch name (`master`, `main`, or your fork default).

## Forge API and persistence

Forge session state in **Ashlar.API** can persist to LiteDB when `Ashlar:ForgeSession:LiteDbPath`
is set. See `docs/Persistence.md`.
