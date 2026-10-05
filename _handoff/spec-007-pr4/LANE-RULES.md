# PR 4 implementation lanes: shared rules

`$SP` = `/tmp/claude-0/-home-user-Ashlar/119c0f0e-4179-5553-a59e-a4fbe7cfca88/scratchpad`

**Authoritative design:** `$SP/pr4/DESIGN-4-final.md`.
- Read §1, your PR's row in §4, the §2 subsections your PR implements, and the §3.B defaults it names.
- The owner answers are at the end of that file.
- Implement exactly your row: no more, no less.
- If the design is wrong or impossible against the code, say so in `deviations`. Do not silently diverge.

## Clone and branch

- `git clone -q /home/user/Ashlar $SP/pr4-<n> && cd $SP/pr4-<n> && git fetch -q origin master && git checkout -q -b claude/spec-007-pr4-<n>-<slug> origin/master`.
  - The base is master as of today: `9abb491` or later.
- If the clone exists, continue from its state. Read `git log` and `git status`; do not re-clone.
- Commit after every meaningful step; WIP commits are fine.
- Never edit `/home/user/Ashlar`.

## Build and test (CLAUDE.md)

- **Never run dotnet on the host.** Build and test only with `bash $SP/pr4-<n>/scripts/test-in-container.sh --repo $SP/pr4-<n> ...`.
  - Use `--framework`, `--filter` and `--project`. `--` takes a custom command.
  - Only committed state is tested.
- **The machine has 4 CPUs shared by several agents. Run ONE container at a time; never background parallel builds.**
- If docker does not answer, run `CLAUDE_CODE_REMOTE=true bash /home/user/Ashlar/.claude/hooks/session-start.sh`. Never stop dockerd.
- If a change touches `Ashlar.Abstractions`, build it for netstandard2.0, net8.0 and net10.0. It has `TreatWarningsAsErrors`, `AnalysisMode=All` and the PublicAPI analyzers; record public API in `PublicAPI.Unshipped.txt`.

## Evidence

- **Twin tests.** Each behavioural change gets a twin. Where the design says "red at base", show it red at the base: commit the test alone first, run it, then the change.
- **Mutation-check every behavioural change** with `scripts/mutation-check.sh`. Report the verbatim summary lines, including the red test names.
- **Cert-gate tests** live in `src/Ashlar.Tests.Infrastructure/Tests/Certification/` (namespace `Ashlar.Tests.Infrastructure.Tests.Certification`). Add the row to `ci/cert-gate-assertions.md` and update the count paragraph.
- **Run the full cert-gate** (`bash scripts/run-cert-gate.sh` in the container) on net8.0. Run every other test project you touch on its target frameworks.
- **Process environment variables in tests** must follow `ProcessGlobalEnvironmentConventionTests`.

## Records

- Update the records your PR's row in §4's records table names: `docs/EgressInventory.md`, `ci/egress-inventory.tsv`, `ci/cert-gate-assertions.md` and `PublicAPI.Unshipped`.
- Add the `CHANGELOG.md` Unreleased entry.
- Add one line to SPEC-007's status bullet for PR 4 saying what your PR does. Mark it "(this PR)"; the integrator fills in the number.
- The egress convention test (`EgressGuardConventionTests`) must stay green. If your change moves a TSV pin, update the pin and say why.
- **Order at the end:** `git add -A`, then `python scripts/knowledge-graph/build-knowledge-graph.py`, then `bash scripts/ci/run-repo-gates.sh`, which must pass on all gates. `shellcheck` is at `$SP/sc-py/bin/shellcheck`; put it on `PATH`.

## Deliverables

- **Commit.** Squash to ONE commit whose message ends with:
  ```
  Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_01GYPVuHoik1sH5U6gWtSVRT
  ```
- **PR body.** Draft it at `$SP/pr4/pr-<n>-body.md` with these sections:
  - Summary;
  - Changes;
  - Behaviour changes, with what stays report-only;
  - Testing, with a mutation table and real counts;
  - Testing strategy;
  - Checklist;
  - Release.

  Mirror the earlier SPEC-007 PR bodies (#709, #711). Write `[coordinated-integration]` plus a rationale if you change non-test code under `application/`.
