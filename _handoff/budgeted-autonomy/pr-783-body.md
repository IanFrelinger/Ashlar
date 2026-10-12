Card #782. The owner asked on 2026-10-12 that a project-wide validation and audit run at the end of every phase, before the handoff is written, with its progress status in the handoff document.

## Why

A phase handoff reported only its own workstream. A one-off assessment of the whole project on 2026-10-12 found several things no handoff had mentioned:
- a gate red since 2026-09-12 (compat-gate);
- 12 bugs fixed but still open;
- 53 commits of tech-debt work on local branches only;
- secret scanning off on the public repository.

Now every phase ends with that check, and its result is in the handoff.

## What changed

- **`scripts/phase-end/validate.py`** is the deterministic validation, about two minutes. Each check is `pass`, `warn`, `fail`, `error` or `info`. A check that cannot run is `error`, never a pass. It writes JSON and Markdown, and exits 1 on any `fail` or `error`. It checks:
  - **The required checks on master's head,** from both check runs and commit statuses. One not reported on the push, such as shell-lint, which runs only on PRs, is noted, not failed.
  - **The newest master run of every active workflow whose file is on master.** One bulk read covers most; a per-workflow read covers the rest, so a rarely run gate is not missed. GitHub also lists deleted workflows and Dependabot's built-in one; those are counted apart. On today's master it reports 7 red of 61, including compat-gate.
  - **The readiness verdict for master's head, and the last verified commit.** A commit whose verdict cannot be read is an error, and the search goes on past it.
  - **Open PRs and their staleness, open issues by label, and commits since the last release tag.**
  - **The repository's security settings.** Settings the token cannot see are an error, not a clean result.
  - **Local work that is on no remote and not on master.** Candidates are branches with no upstream, a gone upstream, or commits ahead of it. Remote containment, identical content, a squash's patch-id, and a squash's subject on a master commit touching the same files each rule out work that has landed. That cut the first count from 104 to 65, all genuinely local.
  - **The docs' referenced paths, and the knowledge graph.**
  - **Windows bash.** On Windows it never uses the WSL `bash.exe`; it finds Git Bash beside git, including a `mingw64` git, or uses `ASHLAR_BASH`. The first live run showed a WSL failure read as a REFUSED verdict, and a test now pins that case.
- **`.claude/workflows/project-audit.js`** is the saved audit workflow: nine read-only agents.
  - Seven readers score the project against its own definitions of done, with evidence: the roadmap, the next release, the specs, the autonomy unlock, the bug backlog, CI health, and the workstream ending its phase.
  - A critic refutes the load-bearing claims and finds gaps.
  - A synthesis returns a structured status, plus the Markdown for the handoff. It compares with the previous phase's `project-status.json`, so each area row says what changed.
- **`scripts/phase-end/check_workflows.js`** checks saved workflows the way the Workflow tool runs them:
  - the body runs inside an async function;
  - `meta` is a pure literal, read from the source, because a fresh vm context still evaluates builtins such as `String(3)`;
  - every `phase()` title and phase option matches `meta.phases`;
  - there is no `Date.now`, `Math.random` or `new Date()`.
- **The routine:**
  - `/handoff` gains step 4: validate, then audit, before the handoff is written. Its files are attached to the handoff, and the notification carries one clause of status.
  - `/start-phase` re-runs the validation and reports what changed.
  - The template's §1 gets "Project status at the end of phase <P>".
  - `_handoff/phases/README.md` and the CLAUDE.md "Phases and handoffs" steps say the same.
  - `ci/cert-gate-assertions.md` has a row for the workflow-script convention, which blocks through shell-lint.

## Review and audit fixes

- **The audit's critic** found the validation counted 66 workflows where master has 61. Fixed in `cfe718386`: only workflows whose file is on master are counted.
- **A code review** raised 14 findings. 13 were fixed in `e31ad599c`, each with a mutation below (n1 to n9, w5). The one skipped is the cost of the 180-day patch-id scan, which is seconds on this repository.
- **The mutation batch on `e31ad599c`** showed one survivor, w3. Once the source scan existed, no fixture reached the evaluation failure. `8c59b97a0` gives that path its own message and the gate a fixture for it.

## Verification

- **`tests/scripts/phase-end-validate.test.sh`:** 34 unit tests, floor 34. No test touches git, gh, bash or the network; every command goes through a fake runner.
- **`tests/scripts/claude-workflows.test.sh`:** both saved workflows pass. Five broken fixtures are each refused for their own reason (7 refusals observed), so a checker that passed everything would fail.
- **`bash scripts/ci/run-repo-gates.sh --list`** discovers both new tests.
- **Live validation on master `b130228ee`:** 4 pass, 3 warn, 0 fail, 0 error. The three warns are the 7 red workflows, the security settings and the stranded local work. Master's head is VERIFIED, from today's scheduled readiness run.
- The knowledge graph is unchanged, and the docs' referenced paths all exist.
- **End to end:** the audit workflow ran once on master `b130228ee` (`wf_6cb92545-b28`): nine agents, about 31 minutes. Its status is §1 of the republished phase E handoff on `claude/budgeted-autonomy-workspace`, with the validation and the full audit attached.
- **Mutation checks** ran through `scripts/mutation-check.sh` with `ASHLAR_MUTATION_RUNNER="bash scripts/phase-end/mutation-runner.sh"`. The runner counts the unit tests plus the workflow gate. The validator's mutations ran on `e31ad599c`; `8c59b97a0` changed only the workflow checker and its gate, so the five workflow mutations were rerun there. v12 and v13 targeted code the review rewrote; n1 and n2 replace them. Verbatim, 31 of 31 killed:

```
mutation v1-red-required-check-not-fail: KILLED red=failed:3/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v2-older-run-overrides-newest: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v3-pending-reads-as-pass: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v4-red-workflow-not-reported: KILLED red=failed:2/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v5-unreadable-workflow-skipped: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v6-no-verdict-line-reads-as-refused: KILLED red=failed:2/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v7-bash-probe-skipped: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v8-first-success-taken-as-verified: KILLED red=failed:2/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v9-stale-comparison-reversed: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v10-public-without-scanning-not-warned: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v11-remote-containment-ignored: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v14-failing-local-check-passes: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v15-exit-code-ignores-error: KILLED red=failed:2/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v16-system32-bash-chosen: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v17-main-returns-zero: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v18-markdown-pipes-unescaped: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation v19-squash-suffix-kept: KILLED red=failed:2/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n1-ahead-branch-not-a-candidate: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n2-bare-subject-match-lands: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n3-commit-statuses-ignored: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n4-invisible-security-reads-clean: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n5-disabled-workflow-reported: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n6-bulk-read-ignored: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n7-verdict-error-stops-search: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n8-mktime-minus-timezone: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation n9-mingw64-git-misses-git-bash: KILLED red=failed:1/35 green=passed:35/35 ref=e31ad599c716a4568349a432cbe32ebe85cd2a48 runner=override
mutation w1-unmatched-phase-accepted: KILLED red=failed:1/35 green=passed:35/35 ref=8c59b97a0da0007d09d534861f49714896268c0e runner=override
mutation w2-date-now-accepted: KILLED red=failed:1/35 green=passed:35/35 ref=8c59b97a0da0007d09d534861f49714896268c0e runner=override
mutation w3-malformed-meta-accepted: KILLED red=failed:1/35 green=passed:35/35 ref=8c59b97a0da0007d09d534861f49714896268c0e runner=override
mutation w4-unparsable-body-accepted: KILLED red=failed:1/35 green=passed:35/35 ref=8c59b97a0da0007d09d534861f49714896268c0e runner=override
mutation w5-builtin-call-meta-accepted: KILLED red=failed:1/35 green=passed:35/35 ref=8c59b97a0da0007d09d534861f49714896268c0e runner=override
```

## Integrator decisions (no owner rows)

- The audit runs every phase, at nine agents, which is within the session's agent guideline.
- Each workstream stores its latest status on its own storage branch as `project-status.json`.
- If the audit cannot run, the handoff says so and carries the validation alone. The status is never written from memory.
- The validation treats a workflow whose newest master run is red as `warn`, not `fail`. Most such workflows are dormant by decision (`docs/CiGateInventory.md`). A red required check on master's head is `fail`.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
