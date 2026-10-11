# budgeted-autonomy handoff: end of phase A (PR 1), start of phase B (PR 2)

*Written 2026-10-11 04:43 UTC at the end of phase A. Start the next thread with the prompt in §9, or with `/start-phase budgeted-autonomy`. Storage branch `claude/budgeted-autonomy-workspace`, folder `_handoff/budgeted-autonomy/` (`bash scripts/handoff-fetch.sh --workstream budgeted-autonomy --list`).*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`): end each phase with `/handoff budgeted-autonomy <phase>`; start each phase with `/start-phase budgeted-autonomy`. One phase is one of the five roadmap PRs. The handoff is published to `claude/budgeted-autonomy-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff.

The whole roadmap (the owner's prompt of 2026-10-11, verbatim in the card body of #755 and summarised in §4) is five ordered PRs with one owning session per PR. Do not implement more than one PR per thread.

## 1. Where things stand

- **Master:** `1d8bc744875b711299bb2719f4d259d9d6173561`.
- **Merged this phase:**

| PR | What | Merge SHA |
|---|---|---|
| #756 | Budgeted autonomy PR 1: operator policy and durable controller state (`tools/autonomy-controller/`, `tests/scripts/autonomy-controller.test.sh`, CHANGELOG, DocsIndex) | `1d8bc7448` |

- **Readiness verdict for master's head:** no Full Platform Readiness run was push-triggered by `1d8bc7448` (its diff matches no readiness path, so the `on.push.paths` filter skips it; `scripts/release/readiness-verdict-for-sha.sh 1d8bc744…` returned `UNDETERMINED (gh-unauthenticated)` in this session). Confirmed with the Actions API: the newest push-triggered run on master is #749's (`9faefe46`, in progress at 04:43Z); none exists for `1d8bc7448`. The latest `verified` master verdict on record is `ac264559f` (#748); #749 and #756 are both test/tooling-only diffs. The PR's `Readiness summary` was a diff-skipped pass (no readiness path touched); that is not platform verification and was not claimed as such.
- **Agent-bus (#695):** this phase posted `claim` (card-755), `handoff` (pr-756) and `done` (pr-756) under `From: claude`. Open items that are **not** this workstream's: Grok's `ask` on `drift-746` and `drift-748` and its `status` on `land-749-753` and `mutations-spec007` (all 2026-10-11, all spec-007-pr4 work). Grok's drift audit of #756 may post `ask` / `About: drift-756`; phase B answers it with a follow-up PR and `done` on that slug.
- **Phase A exit criteria:** PR 1 merged through the permitted process on the five required checks; every PR 1 acceptance criterion has a test observed failing (14 mutations KILLED, lines in the PR body); card #755 closed by the `done` with the merge SHA. **Met**, with the known limits in §6.

## 2. Read these first

- **In the repo (master):** `CLAUDE.md`; `tools/autonomy-controller/README.md` (what the controller does, the states and stop reasons, the budget rules, what PR 1 does not do); `tools/autonomy-controller/ashlar_controller/{controller,budget,store,policy}.py`; `tools/autonomy-controller/adapters/base.py` (the adapter protocol PR 2 implements) and `adapters/fake.py` (the scripted shape the tests use); `tests/scripts/autonomy-controller.test.sh`; `_handoff/bus/PROTOCOL.md`; `_handoff/phases/README.md`; `_handoff/readiness/README.md` and `.claude/commands/converge-readiness.md` + `.claude/workflows/readiness-convergence.js` + `.claude/agents/readiness-*.md` (the existing readiness roles PR 2 connects to).
- **The card:** #755 (the full PR 1 to PR 5 roadmap and the operating policy).
- **On the storage branch, `_handoff/budgeted-autonomy/`:** `handoff.md` (this), `handoff-phase-A.md`, `pr-756-body.md` (the PR body with the 14 mutation lines), `mutation-logs-pr1.tar.gz` (the `scripts/mutation-check.sh` logs, one per mutation).

## 3. Decisions

- **Owner decisions this workstream relies on:** none recorded. This workstream has no spec and no decisions log; the owner's prompt of 2026-10-11 is the brief (in #755).
- **Integrator decisions taken in phase A** (recorded in the PR body of #756 and in the README; the owner can reverse any of them):
  - operating defaults in `policy.example.json`: paid inference off, no new paid infrastructure, one implementation writer across providers, one verifier, one readiness fixer, one same-failure retry, two repair rounds, one premium escalation and one provider transfer per PR, 25% interactive reserve, `unobservable_allowance: task-ceiling-only`, invocation ceiling 20 turns, task ceiling 80, four attempts per task;
  - model ids in the example are **candidates** (`claude-sonnet-5-5`, Codex `sol`, Cursor `composer-standard`); PR 2 verifies installed availability before any live launch;
  - package at `tools/autonomy-controller/`, JSON policy (the devtest image's Python has no `tomllib`), one action per tick, the policy trust boundary (refused inside any checkout or when group/world-writable), the pool ceilings frozen at creation so a policy can only tighten.
- **Open questions for the owner (none blocks phase B):**
  1. Devtest image parity: the image's `python3` has no `sqlite3` module (`.docker/Dockerfile.devtest:49` installs `python3` on a `python3-minimal` base without `libsqlite3`'s module). The controller suite therefore runs on the `shell-lint` runner, like every other `tests/scripts/*.test.sh`, and `scripts/test-in-container.sh -- bash tests/scripts/autonomy-controller.test.sh` refuses. Fixing it is a `.docker/Dockerfile.*` change, which is a readiness path (full matrix on the PR). Owner's call whether to pay that.
  2. Whether the conservative defaults above become owner rows somewhere durable (there is no spec for this workstream; a short `docs/specs/` note or a section in `tools/autonomy-controller/README.md` labelled "owner decisions" would do).
  3. The bus credential: `gh` in the cloud session has an invalid `GH_TOKEN`; this phase posted through the GitHub MCP tools. The controller's `GhIssuePoster` needs an authenticated `gh` with issue write on the controller host (operator step, PR 2 or PR 3).

## 4. Plan and live status

**Phase map (one PR per phase):**

| Phase | PR | Scope | Ends when | Status |
|---|---|---|---|---|
| A | 1 | Budget policy and durable controller state | merged on green; acceptance criteria mutation-proven | **done** (#756) |
| **B** | **2** | **Claude adapter and bounded readiness** | one assigned task produces verified output and a recoverable handoff; quota/auth errors cause no fallback launch; cancellation preserves workspace and run record; the funding route is verified | **next** |
| C | 3 | PR lifecycle and fresh context | old green checks cannot satisfy a changed head; duplicate events create no PR or worker; closure without merge is not success; next task uses updated base and compact handoff; previous transcript not injected | |
| D | 4 | Operator controls, evidence retention, pilot | status view, pause/cancel/resume/provider-disable, backups and restore, LLM-free status reports, evidence archives, metrics; three bounded real tasks | |
| E | 5 | Codex and Cursor adapters | provider transfer preserves task/PR/attempts/budget; previous writer stopped and reconciled; one small real task through each enabled adapter | |

**Work items:** `feat/budgeted-autonomy-controller-state` @ `c8e6304a7`, PR #756, merged (`1d8bc7448`). No branch is in flight.

**Phase B (PR 2), spelled out, in order:**
1. `/start-phase budgeted-autonomy`; read §2; post `claim` on the bus for the new card (create the PR 2 card as an `agent-task` issue first, copying the PR 2 section of #755's roadmap as its acceptance criteria; nothing else exists for it).
2. **Verify the installed Claude Code runtime before writing the adapter**, with one tiny bounded task: headless launch (`claude -p` or the Agent SDK, whichever is installed), exact session-id capture and `--resume <id>`, cancellation, structured/JSON output, transcript persistence and where it lives, authentication state, the model/effort/speed flags, and which usage figures the CLI reports (per-invocation cost vs session-cumulative). Record each as `AdapterCapabilities` on the adapter; **declare nothing unverified**. Check whether headless execution exposes the Workflow tool used by `.claude/commands/converge-readiness.md`; if not, drive the existing roles (`.claude/agents/readiness-*.md`) and `scripts/readiness-gate-local.sh` through the adapter and say so.
3. Implement `ashlar_controller/adapters/claude.py` against `adapters/base.py`: launch with explicit model, effort and speed; session-id capture; resume; cancel; `poll` mapping the CLI's exit states to `RunStatus` with **separate** stop reasons for quota, authentication, permission, transient, cancellation and task failure; `observe_usage` returning `UsageObservation` by kind with `fresh|stale|unavailable`; the invocation ceiling as the CLI's turn limit (20 to start). Compute consumption as a **delta** against the previous cumulative figure of the same session (`budget.record_usage` already does this); keep the controller's cumulative pool authoritative; record overshoot.
4. One readiness fixer: direct execution and parsing of the known gate commands (`scripts/readiness-gate-local.sh --json`, `scripts/ci/run-repo-gates.sh`, `scripts/test-in-container.sh`) as ordinary code; persistent failure identities (`failure_identity`) and the one-retry counter already in `failure_retries`; general-purpose fallback only on a recognised missing-role error.
5. Preserve the evidence rules: an intentional mutation-red run is expected evidence; everything through `scripts/test-in-container.sh` from a clone; mutation-check every new behaviour (`ASHLAR_MUTATION_RUNNER=bash tools/autonomy-controller/mutation-runner.sh scripts/mutation-check.sh ...` works for the Python suite; it marks `runner=override`).
6. Acceptance (from the roadmap): one assigned task produces verified output and a recoverable handoff; refutation permits its one retry and verification and another cycle cannot refresh the counter; quota and authentication errors cause no fallback launch; cancellation preserves the workspace and the incomplete-run record; the selected funding route is verified. Use a simulated quota response rather than exhausting an account.
7. PR body per §5; `handoff` on the bus; merge on green; `done` with the merge SHA; `/handoff budgeted-autonomy B`.

## 5. How to do the next phase's routine work

1. Clone from GitHub into the scratchpad (`git clone -q https://github.com/IanFrelinger/Ashlar <work>`), or use the session checkout; branch `feat/budgeted-autonomy-<topic>` from `origin/master`. One agent per clone; never test in a worktree.
2. Build; run `bash tests/scripts/autonomy-controller.test.sh` (host, the CI path) and `bash scripts/ci/run-repo-gates.sh` (all 29 gates); run anything that needs `dotnet` only through `scripts/test-in-container.sh`.
3. Commit, then mutation-check each behaviour against the commit (the mutation runner above); quote each `mutation <id>: ...` line verbatim in the PR body; `git status --porcelain` empty after every restore.
4. `git add -A` then `python scripts/knowledge-graph/build-knowledge-graph.py`; commit the regenerated graph if it changed (it did not change for PR 1's files).
5. Push with `git push -u origin <branch>`; open the PR from `.github/PULL_REQUEST_TEMPLATE.md` (the GitHub MCP `create_pull_request` tool works; `gh` does not in the cloud session); keep the testing-strategy table honest (no .NET change means the repo-gates lane).
6. Bus: `claim` on `card-<n>` before building; `handoff` `About: pr-<n>` when ready, naming the records and docs it touches; merge (squash) on the five required checks green unless Grok posted `block`; `done` with the merge SHA. In the cloud session, post with the GitHub MCP `add_issue_comment` tool and end every comment with the Claude Code footer.
7. Read the readiness verdict for the merge SHA (`scripts/release/readiness-verdict-for-sha.sh`, or the `Readiness verdict` annotation on the `Readiness summary` check run).

## 6. Environment facts

- Five required checks on master: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`; strict + enforce_admins; merges serial. This session could not read branch protection from the API (`gh` token invalid); #756's check list matched the documented five.
- Repo gates: **29** after #756 (`run-repo-gates.sh` discovers `tests/scripts/*.test.sh`, `scripts/ci/test-*`, `scripts/ci/verify-*`, `scripts/knowledge-graph/verify-*-current.py`). The earlier handoffs' "28" is stale from this merge on.
- Host: Python 3.13, `pyyaml` present, **no `shellcheck`** (CI runs it; `bash -n` locally). Devtest image `ashlar-devtest:local`: Python 3.12.3 with `python3-yaml`, **no `sqlite3` module**, no `tomllib` (so the policy is JSON).
- The cloud session's `gh` has an invalid `GH_TOKEN`; every GitHub read and write went through the GitHub MCP tools (`issue_read`, `issue_write`, `add_issue_comment`, `create_pull_request`, `pull_request_read`, `merge_pull_request`). `gh issue view 695` also fails with "GraphQL is not available from Claude Code sessions".
- The controller's unit suite creates its state under `tempfile.mkdtemp()`; `*.db` is gitignored, so no state file can be committed by accident.
- `scripts/mutation-check.sh` with `ASHLAR_MUTATION_RUNNER` works for Python: `tools/autonomy-controller/mutation-runner.sh` prints the dotnet-shaped header and summary line it needs. Multi-line `--old` text must be passed with `--old-file` written by `printf '%s'` (a heredoc adds a trailing newline that then fails to match).
- Known pre-existing failures elsewhere (from the spec-007-pr4 handoff of 2026-10-11): `DogfoodBlock8Tests.ParallelTestMatrix…`, two `DogfoodBlock8ComposedTests`; `RuntimeStudioBlackBoxPlaygroundTests.Prod_cli_objectives…` flaky under load. None touched here.

## 7. Queued work

- Devtest image: `python3` with the `sqlite3` module (`libpython3-stdlib` / full `python3` rather than `python3-minimal`), a readiness-path PR; owner's choice (§3 Q1).
- Follow-up card for the agent-bus posts through the controller: `GhIssuePoster` live verification needs an authenticated `gh` with issue write on the controller host; alternatively a REST poster using a token from the environment. PR 2 or PR 3.
- Cloud-cost review: the next workstream after this one. Runtime and usage evidence collected so far: PR 1 used no model calls at runtime; the repo gates take about 3 minutes on the host; the devtest container run of the Python suite is refused (above). Cloud shutdowns, resizing, migrations and subscription changes stay out of this workstream.
- The spec-007-pr4 items on the bus (drift-746, drift-748, land-749-753, mutations-spec007) belong to that workstream's phase G and are untouched.
- Grok's drift audit of #756 (`drift-756`), when it posts: fix in a follow-up PR, `done` on the slug.

## 8. Ways of working the owner has endorsed

- One phase per thread, one PR per phase, ending with `/handoff`.
- Claim before you build; read the newest handoff and the bus before you start; create the card if none exists and say so.
- Every default taken without the owner is an integrator decision in the PR body and the handoff, never an owner row.
- Mutation-check every behaviour; quote the summary lines verbatim; say what was not observed failing and what only CI proves.
- Keep the controller independent of the application runtime and the heavy CI matrix: no readiness-path file in a controller PR.
- Do not interpret an estimated API dollar figure as a charge or as the subscription allowance; keep the four kinds separate.

## 9. Starting prompt for the next phase

> Execute **phase B (PR 2: Claude adapter and bounded readiness)** of budgeted-autonomy. Run `/start-phase budgeted-autonomy`, or read this handoff in full starting with §0, then `tools/autonomy-controller/README.md` and the adapter protocol in `tools/autonomy-controller/ashlar_controller/adapters/base.py`. Check §1 against the live repo (master, #756 merged, the bus). Create the PR 2 card as an `agent-task` issue from the PR 2 section of #755 and post `claim` on it. First verify the installed Claude Code runtime with one tiny bounded task (headless launch, exact session resume, cancellation, structured output, persistence, authentication, usage figures) and record only what you observed as adapter capabilities; check whether headless execution exposes the Workflow tool the readiness command uses and, if not, drive the existing readiness roles and scripts through the adapter. Then implement the Claude adapter with explicit model, effort and standard speed, separate stop reasons for quota, authentication, permission, transient, cancellation and task failure, per-run usage deltas against the controller's cumulative pool, and one readiness fixer with persistent failure identities. Use simulated quota and failure responses, never exhaust an account, enable no paid route, buy nothing, provision nothing. Build and test only through `scripts/test-in-container.sh` for anything that needs `dotnet`; never run `dotnet` on the host. Mutation-check every behaviour. If anything the owner has not decided blocks you, ask instead of choosing. Land PR 2 on the five required checks, post `handoff` and `done` on #695, then run `/handoff budgeted-autonomy B` and notify me. Do not start PR 3.
