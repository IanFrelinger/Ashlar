# budgeted-autonomy handoff: end of phase F (close-out); the workstream is closed (no phase G)

*Written 2026-10-13 ~19:55 UTC at the end of phase F. The storage branch is `claude/budgeted-autonomy-workspace`, folder `_handoff/budgeted-autonomy/`. List it with `bash scripts/handoff-fetch.sh --workstream budgeted-autonomy --list`. If the owner starts another phase, begin with the prompt in §9 or with `/start-phase budgeted-autonomy`.*

## Addendum (2026-10-13, after this handoff was first published): the close-out

*Added in the same thread, after the owner answered §3, as phase E's thread added #783 after its handoff. Where
this addendum and the sections below disagree, this addendum is current.*

**The owner's answers.** On the pre-rewrite storage commits, the owner said "I don't care about my email". For the
rest, the owner said "I'll go with your judgement". Claude's recommendations became six owner rows dated 2026-10-13
in the decisions log of `tools/autonomy-controller/README.md`, marked *Accepted recommendation*:
- **The pre-rewrite storage commits are not purged.** GitHub serves **four** of them by SHA, not the two §3 names:
  `cbc1a0d72929`, `c9eb03d92`, `3ad39c834` and `ec6ac7cfd`. Each holds blob `a81c1a1c`.
  - A recursive scan of that blob from the local object store (40 members, nested archives included) found no
    token, key, JWT, bearer header or password.
  - The scanner was first seen catching a planted token two archives deep.
- The six PR 5 archives stay local. The `From: codex` outbox post stands. The owner's Codex priority tier stays.
- **The workstream closes after one cleanup PR.** No phase G is planned, and the undelivered brief items get no
  cards.
- **Card #790 must be built and merged before the next live controller run that launches Claude workers.**

**Merged:**

| PR | What | Merge SHA |
|---|---|---|
| #793 | The six decision rows (records only) | `2d60ba8fc` |
| #794 | Cleanup: saved workflows pinned LF; the checker refuses control characters; README :3, :37 and "Not in PR 3"; the package docstring; `ci/cert-gate-assertions.md` row 82; `PILOT-PR4.md` removed | `4287a18a1` |

**Verification.**
- #794: `scripts/mutation-check.sh` killed 4 of 4 at `248c0886c`:
  - the attribute dropped;
  - the attribute set to `crlf`;
  - the control-character check skipped;
  - CR allowed.

  The test was also observed red on CRLF copies and green on LF copies.
- An adversarial review (three lenses) checked both PRs, and its findings were fixed before merge. They included:
  - the four-commit count;
  - two wrong row references;
  - the registry row not updated;
  - the checker not enforcing the rule its header claims;
  - Git for Windows' `grep` being unable to see a CR, which made the first byte check pass on CRLF copies.

**What this changes below.**
- **§1, bus:**
  - posts 36 (`claim` budgeted-autonomy-close, 6153979028), 37 (`handoff` pr-793, 6154029835), 38 (`done` pr-793,
    6154444451), 39 (`handoff` pr-794, 6154462566) and 40 (`done` pr-794 and budgeted-autonomy-close; see
    `40-done-pr-794-close.md`);
  - Grok's drift notes: on #792 (6153522959), and an interim note on #793 (6154358236) with no `block`.
- **§3 open questions:**
  - 1, 2 and 4 to 8 are answered (above). The live merge-and-repair exercise is not scheduled.
  - **3 (the cloud-cost review) and 9 (promoting integrator decisions) stay open.**
- **§4 phase G:** none. The candidates are kept for the record only. Their status:
  - the cleanup PR is #794;
  - #790 is gated on the next live run;
  - the merge-and-repair exercise is not scheduled;
  - the `validate.py` fixes ride with whichever workstream comes next, as project tooling.
- **§6, saved workflows on Windows:**
  - After #794, a Windows checkout gets LF workflow files once it re-checks them out:
    `rm <file> && git checkout -- <file>`. The session checkout `ashlar-record-correction` was fixed this way after
    #794 merged. Its `project-audit.js` is now byte-identical to the LF copy that ran through `scriptPath`, and its
    only control character is LF.
  - **Launching it by name in this same session was still refused** with the same "control characters" error. The
    likely cause is that the session read its saved workflows when it started, before the fix. **Unverified:** a
    by-name launch in a new thread.
  - If the next `/handoff` is still refused by name, run the LF copy through `scriptPath`, as phase F did, and record
    it.
- **§7:** removed or decided:
  - `PILOT-PR4.md` is removed;
  - the README :3, :37 and :942 items are done;
  - the `.gitattributes` item is done;
  - the run sheet `live-run-pr5.md` on this branch now opens with a "Historical: do not reuse" banner naming its two
    wrong expectations.

  The reviewers' coverage nits stay listed, unscheduled.
- **The pre-rewrite local ref** `claude/budgeted-autonomy-workspace` (`3ad39c834`) in `ashlar-record-correction` was
  deleted, as §4 says. `3ad39c834` itself is one of the four commits GitHub still serves.

**Next.** Nothing in this workstream. The audit's project-wide priorities are in §1:
- the repository security settings (owner-only);
- the dogfood record decision before 2026-10-29;
- the release path, which needs the owner's version number and the API stability call.

Any of those starts in its own thread.

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`):
- end each phase with `/handoff budgeted-autonomy <phase>`;
- start each phase with `/start-phase budgeted-autonomy`.

The handoff is published to `claude/budgeted-autonomy-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch first.

**The roadmap is closed.** The five roadmap PRs of #755 merged in phases A to E. Phase F closed out:
- the PR 5 live run, its pre-flight and its findings;
- card #779 and card #776;
- the owner's open questions, now dated rows in the README's decisions log.

No phase G is planned. Section 4 lists what is queued, so the owner can choose. **Nothing starts without the owner.**

**The lane pattern (phases B to F)** worked again this phase. Phase F ran three sets of parallel lanes in isolated worktrees: the pre-flight fixes, the live-run fixes (lanes P, C and O) and the #776 builder. Each set went through the same steps:
1. Write a lane contract, for example `live-fix-lanes.md`: files per lane, the observed shapes quoted, "declare only what was observed".
2. Run one agent per lane with `isolation: "worktree"` on a pinned base SHA.
3. Integrate yourself.
4. Run an adversarial reviewer in its own clone.
5. Verify the merged branch yourself: the host suite, the container suite and a `scripts/mutation-check.sh` batch.
6. Remove the worktrees and their branches.

## 1. Where things stand

**Master:** `7c50e379fcc42810bd7e590ecc04f21ddbc17b64` (#792).

**Merged in this workstream:**

| PR | What | Merge SHA |
|---|---|---|
| #756 (A) | PR 1: operator policy and durable controller state | `1d8bc7448` |
| #769 (B) | PR 2: Claude adapter and bounded readiness | `f236bd4ad` |
| #771 (C) | PR 3: PR lifecycle and fresh context | `d2c11b347` |
| #773, #774 (D) | PR 4: operator controls and evidence; OAuth refresh contention is `transient` | `18b5da30f`, `3d14ce0b7` |
| #777, #778 (pilot) | PR 4 pilot, opened by the controller, merged by the owner | `f0fd9d113`, `ea71db40c` |
| #780, #781 (E) | Pilot findings; PR 5: Codex and Cursor adapters, bounded provider transfer | `4003ccb0a`, `b130228ee` |
| #783 (between E and F) | Phase-end validation and audit (card #782) | `dd38c007c` |
| **#784 (F)** | Decisions log: the phase D rows and the 2026-10-12 answers | `dccba4e03` |
| **#785 (F)** | The PR 5 pre-flight's 15 findings, fixed before the live run | `af7ff471b` |
| **#789 (F)** | Decisions log: the 2026-10-13 rows after the live run | `f2ddbee01` |
| **#791 (F)** | What the live run exposed: archive privacy, the Cursor result event, the binding allowance window | `200fd1e5e` |
| **#792 (F)** | Card #776: `task add --brief`, ledger schema 3 | `7c50e379f` |

**The live run's test PRs.** #786 (Codex), #787 (Cursor) and #788 (transfer, Claude) were opened by the controller. Codex, as operator, closed them unmerged, which is the sheet's merge-or-close option. Their `auto/pr5-*` branches are deleted (owner decision).

**Required checks on master's head.** On the push, `cert-gate`, `build-core` and `lychee (README + docs)` are all success. `shell-lint` and `Readiness summary` run on PRs only; both were green on #792's head `bf3473a73` when it merged.

**Readiness verdict for master's head.** `scripts/release/readiness-verdict-for-sha.sh 7c50e379f…` reports `REFUSED (no-readiness-run)`. That is expected for tooling-only diffs: no readiness path changed in phase F. The last VERIFIED commit is `b130228ee` (scheduled run, 2026-10-12), 6 commits back.

**Agent-bus (#695).** All of this phase's posts are listed below, and each file is on the storage branch.

| # | Kind | Slug | Comment |
|---|---|---|---|
| 22 | status | card-779 (phase F) | 6135010735 |
| 23 | claim | card-776 | 6135104060 |
| 24 | done | pr-784 | 6136870115 |
| 25 | handoff | pr-785 | 6138379353 |
| 26 | done | pr-785 | 6138617678 |
| 27 | claim, `released` | card-779, card-776 (to Codex for the live run) | 6148472741 |
| 28 | claim | card-779, card-776 (re-claimed after the run) | 6149108052 |
| 29 | status | correction to 28 | 6149566946 |
| 30 | status | the storage branch rewrite | 6151763815 |
| 31 | handoff | pr-791 | 6151795583 |
| 32 | done | pr-789, pr-791 | 6152207668 |
| 33 | done | card-779 | 6152213652 |
| 34 | handoff | pr-792 | 6152659138 |
| 35 | done | pr-792, card-776 | 6152855805 |

Other agents posted this phase:
- **Codex:**
  - its claim (6148811616);
  - handoffs on pr-786, pr-787 and pr-788;
  - the controller's outbox `status` (6149018991).
- **Grok:** clean drift notes covering every PR of this workstream: #756 to #781, plus #783, #784, #785, #786 to #788, #789, #791 and #792 (6125584428, 6131070487, 6146640744, 6150353136, 6152500004, 6153522959).

**Still open on the bus:**
- **For this workstream:** nothing. There is no `ask`, `block` or drift ask, and no claim of mine is open: posts 33 and 35 closed card-779 and card-776.
- **Project-wide, not this workstream's:** SPEC-007's `drift-746` and `drift-748` asks are still open, and so are the PRs #750 to #753.

**Cards.**
- #779 is closed (2026-10-13 18:18Z, `done` 6152213652).
- #776 is closed by #792. Its assignee is IanFrelinger, the account every agent posts from.
- #790 (Claude worker isolation) is open, unassigned and unclaimed. The owner decided it is for later.

**Phase F's exit criteria** (handoff E §4):

| Criterion | State | Evidence |
|---|---|---|
| The live run is recorded in the ledger and the handoff | **met** | Ledger `ctl-pr5-live`. Records on the storage branch: `live-run-pr5-record.md`, `live-run-pr5-verification.md` and `live-run-pr5-console.log`. README "Not in PR 5". Cursor ran; it was not skipped. |
| Its findings are fixed and merged | **met** | #785 (pre-flight, before the run), #791 (the run's findings) and #792 (#776). Every behaviour was mutation-checked; see §4. |
| #779 closed with a `done` naming the evidence | **met** | Bus 6152213652. Closed 2026-10-13. |
| Every posted drift ask answered | **met** | Grok posted no drift `ask` for this workstream. Every drift note is clean. |
| Open questions 2 to 5 asked | **met** | All four were answered on 2026-10-12 and are rows in the decisions log (#784). Eight more were answered on 2026-10-13 (#789). |

**The live run (2026-10-13, about 15:15 to 15:26 UTC).** The owner delegated it to the Codex agent, which ran it from the owner's terminal through `pr5.cmd`, sheet v2.
- **Ledger and policy:** ledger `ctl-pr5-live` (schema 2), on master `af7ff471b`. Policy `2026-10-13.pr5-live-composer-2.5`, ceilings 12/36/3, with the guard check kept.
- **The runs:**
  - Codex `gpt-6-astra` completed in 6 turns: #786.
  - Cursor `composer-2.5` completed and was charged its full 12 requests: #787.
  - The transfer cancelled Codex run-82eb9983dca6 (charged 12). Claude then completed in 7 turns: #788. A second transfer was refused with exit 6 (`provider-transfers-exhausted`).
  - #776 on Claude ran twice: run-5c155cbe6f82, then its `--resume` run-a062813c1dae in the same workspace (`workspace-reused`, exercised live). Each was capped and charged 13. The task parked `parked-budget` at 26 of 36, with nothing pushed.
- **Outbox:** the `gh` poster posted 6149018991 on the first attempt.
- **Evidence:** six archives and the end backup `99-end.db` (sha256 `8422d47c…`).
- **Exercised only against fakes:** merge and repair.
- **Not exercised live:** Codex resume, Cursor resume and cancel, outbox `find`, a transfer of a finished writer, and a transfer while a PR is open.

The run's verification (workflow `wf_1bf8c9b6-1b7`) found eight items, F1 to F8 (`live-fix-lanes.md`):
- Three privacy defects: F1, F2 and F3.
- Four shape defects: F4, F6, F7 and F8.
- F5, the capped Claude charge of ceiling + 1. The owner decided to keep it as reported, documented only.

PR #791 fixed the seven defects.

**Operator deviations, recorded as facts, not decisions** (README "Not in PR 5"):
- Codex changed the Cursor model to `composer-2.5` as a new policy version, instead of stopping.
- The outbox post was signed `From: codex`.
- Codex wrote the archives during the sitting, which the sheet had left to the session.

### Project status at the end of phase F

Master 7c50e379f is green on every required check that reports on a push. It has no readiness verdict of its own, because phase F's six commits touched only tooling and docs; b130228ee is the last VERIFIED commit. The budgeted-autonomy roadmap is closed: the PR 5 live run happened on 2026-10-13, #791 fixed its findings, and #779 and #776 are closed. Nothing else moved: the next release has not started, SPEC-007 is at 4.5 of 8 PRs, the dogfood unlock is at 1.5 of 5, and no defect issue was closed.

Validation on 7c50e379fcc4 (2026-10-13T18:54:57Z): 2 pass, 5 warn, 0 fail, 0 error.

- Required checks: 1 of 5 at the time. cert-gate and build-core passed minutes later, so 3 of 5. shell-lint and Readiness summary never report on a master push.
- Red workflows: 7 of 61. Six are documented DORMANT. compat-gate has been red since 2026-09-12 and has no issue.
- Readiness verdict: REFUSED (no-readiness-run), as expected. The next scheduled run is 2026-10-19.
- Security settings: private vulnerability reporting, Dependabot alerts and code scanning are also off.
- Local-only work: a re-run finds 66 branches (53 bx/WS-*), holding at least four fixes master lacks.

| Area | Distance | Estimate (basis) | Since last phase | Blocks the rest |
|---|---|---|---|---|
| Product roadmap | far | 5 of 41 roadmap criteria done, 6 partly, 30 open (code check) | No code change; 4 partials re-scored open | Owner: which end state governs |
| Next release | partway | 0 of 9 runbook pre-tag steps, 2 partly evidenced | 95 commits past v0.2.0 (was 89) | Owner: version, API stability |
| Security specs | partway | 7 of 12 spec items done, 2 partial, 3 open | None; phase H never started | Owner: Q-A, Q-B, Q-C |
| Autonomy unlock | far | 1.5 of 5 criteria on landed rows; 0 of 7 3a rows | 13 unlanded 3a runs (was 10) | Owner: record files, before 2026-10-29 |
| Bug backlog | partway | 11 of 42 fixed but open, 5 partly, 26 open | +#790; untracked traversal | Owner: security triage |
| CI health | partway | 40 of 47 workflows green; 0 of 14 PRs mergeable | PRs 15 to 14 | Owner: settings, bx/* branches |
| budgeted-autonomy | near | 4.5 of 5 phase F exit criteria | Live run done; #779, #776 closed | This handoff |

**Moved since the last phase**

Resolved: #783, #784, #785, #789, #791 and #792 merged. The live run is in ledger ctl-pr5-live. #779 closed (done 6152213652) and #776 closed by #792. Grok's drift audits are clean through #792 (6153522959).

New:
- #790 is open and deferred.
- `ashlar bg proposals apply` can write outside the repo root (ProposalsBackgroundAgentCommand.cs:193). It has no issue, and its only fix is on the local branch bx/WS-Ia/I2.
- GitHub still serves the pre-rewrite commits cbc1a0d72929 and ec6ac7cfd, which hold the unredacted tarball.
- Six unredacted PR 5 archives exist only locally.
- #750's red Readiness is a real Windows test failure.
- The publish jobs have no protected environment and no tag ruleset.

**Critical path to the next release after v0.2.0**

1. You: choose the version (0.3.0 is implied, but not recorded).
2. You: decide stable or [Experimental] for the 160 unshipped Ashlar.Abstractions lines, as a dated SPEC-007 row.
3. You: confirm or strike SPEC-007 rows :519-521, and answer Q-B, Q-C and review section 10 (a) and (b).
4. Fix BL4 and BL5 refusal masking (PR 4.8b), mutation-checked.
5. Land a records PR that answers drift-746 and drift-748 and corrects PR 5's "refused" claims.
6. Fix compat-gate (kernel-gate-tier-b.sh:67, :80-84) under an issue.
7. Land the proposals-apply fix, #751, a fixed #750, #753 and #752.
8. You: fix, or sign exceptions for, the 11 unfixed security defects and the 3 data-loss defects.
9. Make the release commit: version files, CHANGELOG fold, PublicAPI promotion, CONSUMING.md, pin sweep. Fix changelog-snippet-for-release.sh.
10. Run the rc1 rehearsal, and get runtime-release-gate green and readiness VERIFIED on that SHA.
11. You: sign the Product row and run a real downgrade rollback drill.
12. Tag, run release.yml, and re-pin deploy/node.yml.

**Hard dates**

- 2026-10-19: ubuntu-latest starts moving to 26.04, release.yml included. The weekly readiness run is the same day.
- 2026-10-26: the artifacts of VERIFIED run 38266204232 expire.
- 2026-10-29: the first 3a record artifacts expire, then one per run until 2026-11-12.
- 2026-11-01: monthly rc-gate run.
- 2026-11-10: .NET 8 end of support (an external date). 90 projects build net8.0.
- Passed, 2026-09-01: the SPEC-006 S-4 deadline. DefaultDevKey is still the fallback.

**Not verified**

- No dotnet build, test or mutation ran in this audit. Counts come from CI logs, PR bodies and the bus.
- The critic did not re-check the 41-box roadmap classification or 6 of the 11 fixed-but-open bugs.
- Whether SPEC-007 rows :519-521 are owner decisions.
- What else the unredacted tarball holds; it was not downloaded.
- The .NET 8 and .NET 9 end dates, and 8 of the 12 bx/WS-* tips.

Attachments: `validation-F.md` and `validation-F.json` (the validation), `project-status-F.json` and `project-status.json` (this status, structured), and `project-audit-F.json` (every reader's full report and the critic's verdicts). The audit ran on 2026-10-13 from 19:04 to 19:40 UTC, with nine agents and no errors. It used an LF copy of `.claude/workflows/project-audit.js` (§6), and the `previous` status it compared against was condensed (§6). Two facts changed after it ran:
- Grok's drift note on #792 (6153522959) posted.
- The pre-rewrite local ref `claude/budgeted-autonomy-workspace` (`3ad39c834`) in the `ashlar-record-correction` clone was deleted (§4).


## 2. Read these first

**In the repo (master).** Read `CLAUDE.md`, then `tools/autonomy-controller/README.md`, these sections first:
- "Decisions log (added 2026-10-12)": 16 owner rows, 2026-10-11 to 2026-10-13;
- "Not in PR 5": the live-run record, the operator deviations and the known limits;
- "Codex adapter", "Cursor adapter" and "Claude adapter", each with "What was observed" and "What is unverified";
- "Operator controls and evidence (PR 4)": the redaction layers;
- "Provider transfer (PR 5)";
- "Not in PR 4": `--brief` and schema 3.

Then the code and tests:
- `ashlar_controller/{controller,evidence,models,store,lifecycle,cli,metrics}.py` and `adapters/{codex,cursor,claude,_exit_recorder}.py`;
- `tests/test_preflight_fixes.py`, `tests/test_cursor_across_processes.py`, `tests/test_schema_v3.py`, `tests/test_evidence.py`;
- `tests/scripts/autonomy-controller.test.sh`: `MINIMUM_TESTS` 536. The suite has 555 tests.

**Cards:** #755 (the roadmap), #779 and #776 (closed), and #790 (open, for later).

**On the storage branch, `_handoff/budgeted-autonomy/`:**
- **Handoffs:** `handoff.md` (this file) and `handoff-phase-{A,B,C,D,E,F}.md`.
- **The live run:**
  - `live-run-pr5-record.md`: the verified record, run by run;
  - `live-run-pr5-verification.md` and `live-run-pr5-console.log`;
  - `live-run-pr5.md`, sheet v2, with `pr5.cmd` and `outbox-status.md`;
  - `card-776-partial.patch`.
- **The pre-flight:** `preflight-pr5.md` (corrected for the missed nested archives), `preflight-pr5-findings.txt`, `preflight-pr5-critic.txt` and `preflight-fix-lanes.md`.
- **Lane contracts:** `live-fix-lanes.md`.
- **PR bodies:** `pr-784-body.md`, `pr-785-body.md`, `pr-789-body.md`, `pr-791-body.md` and `pr-792-body.md`.
- **Mutations:** `mutations-pr785-summary.txt`, `mutations-pr791-summary.txt` and `mutations-pr792-summary.txt`.
- **Card #790's body:** `card-790-body.md`.
- **Probes:** `probe-codex-prompt-input-report.json` and `probe-codex-mcp-list-report.txt` (Codex isolation), plus the phase E probes.
- **Bus posts:** `22-` to `35-*.md`.
- **Project status:** `validation-F.md`, `project-status.json`, `project-status-F.json` and `project-audit-F.json` (§1).
- **Evidence:** `live-run-pr4-pilot-evidence.tar.gz` (redacted; see §3) and `live-run-pr3-evidence.tar.gz`. **The six PR 5 archives are not published.** Four of them hold the owner's account email: they were written before #791's redaction. They stay in the ledger's `evidence\` folder on the host.

## 3. Decisions

**Owner decisions:** the 16 rows of the README's "Decisions log", landed by #784 and #789. They are:
- **2026-10-11:** the pilot is guarded; worker launches stay on the owner's terminal.
- **2026-10-12:**
  - transfers are operator-initiated only;
  - only the log's rows are owner decisions;
  - Cursor is logged in and carries a live task;
  - the live run happens in one sitting, after the fix;
  - the outbox's `gh` poster is exercised once;
  - #776 is the fourth live task.
- **2026-10-13:**
  - the live run was delegated to Codex (this run only);
  - the email in published evidence is removed from history;
  - #776 is finished by hand by Claude;
  - capped Claude runs stay charged as reported;
  - #779 closes after the follow-up PR;
  - `policy.example.json` names `composer-2.5`;
  - the `auto/pr5-*` branches are deleted;
  - Claude worker isolation is a card for later (#790).

**Integrator decisions this phase**, recorded in the PR bodies and the README:
- **#785:**
  - `run cancel` without `--adapter` refuses a live run.
  - Cancel waits for an exiting run: 10 polls, 0.5 s apart.
  - A completed but unproposed writer is settled, not marked done (`transfer-needs-pr-lifecycle`).
  - A propose failure blocks dispatch (`propose-failed-blocks-dispatch`).
  - The newest check run by id decides.
  - Attribution comes from the run, not the provider.
  - The worker's own commits are folded into the controller's commit (`git reset --soft start_sha`).
  - Codex runs with `features.plugins=false`, `notify=[]` and each configured MCP server disabled.
  - A Codex `service_tier` is refused as `operator-config-refused`, which sends the task to `blocked-permission`.
  - CLI output is UTF-8.
- **#791:**
  - Archives redact in three layers: the session_context attachment, run-file account keys, and a final email mask that keeps `example.invalid` and the noreply addresses.
  - The Cursor `status_text` is masked.
  - The last Cursor `result` event is kept, but not charged.
  - The allowance takes the higher of the windows present.
  - Permitted attempts are `min(attempts left, floor(remaining / ceiling))`, capped by the policy's `max_attempts_per_task`.
- **#792:**
  - An unusable brief exits 2 (a usage error) with nothing written.
  - A padded brief is kept as given.
  - `CHANGELOG.md` and the test-floor script change, despite the card's letter (repo convention).

**The storage-branch rewrite (owner decision 2026-10-13).**
- `live-run-pr4-pilot-evidence.tar.gz` was replaced by a redacted copy.
- The branch history was rewritten with `git filter-branch` and force-pushed with a lease (bus 6151763815).
- No reachable blob holds the address.
- **GitHub still serves the pre-rewrite commits by SHA,** the audit found: `cbc1a0d72929` and `ec6ac7cfd`. `cbc1a0d72929`'s tree holds the unredacted tarball (blob `a81c1a1c`), and both SHAs are cited publicly (bus 6151763815, `live-run-pr5-record.md`). README:841's "removed from history" holds only for reachable commits.
- Only the owner can ask GitHub Support to purge them. The audit also notes that the owner's email is already public as the author email on 712 master commits. A purge decision therefore rests on what else the tarball holds, which nobody has inventoried.

**Open questions for the owner.** None blocks anything, because nothing is planned:
1. **Is there a phase G?** The candidates are in §4. Card #790 is the only one the owner has already named.
2. **May a future guarded run exercise merge and a repair round live?** Both are still exercised only against fakes. The 2026-10-11 row keeps the controller from merging.
3. **Is the cloud-cost review still the next workstream?** The phase A and B handoffs queued it, and it has been absent since phase C.
4. **The owner's Codex `service_tier = "priority"` is restored** in `~/.codex/config.toml`. Since #785, the controller refuses a Codex launch while it is set (`operator-config-refused`). A future Codex run needs it removed for the sitting, as in the live run.
5. **Purge the pre-rewrite storage commits, or accept the exposure** (above).
6. **The six local PR 5 archives:** keep them, delete them, or redact and publish them. There is no decision row.
7. **Outbox post 6149018991 was signed `From: codex`.** The 2026-10-12 row says `From: claude`. Its dedup key is spent, so it cannot be re-sent. Is a ruling needed, or does the delegation row cover it?
8. **Does the workstream close here,** or do the brief items no PR delivered get cards? They are tick-driven bus claims, card intake, container validation in a tick, the `phases` table and premium escalation.
9. **Should any phase A to F integrator decision become an owner row?**

The audit's project-wide owner decisions are in §1 and `project-status-F.json` (`owner_decisions`). None of them is this workstream's to settle.

## 4. Plan and live status

**Phase map:**

| Phase | PR | Scope | Ends when | Status |
|---|---|---|---|---|
| A | 1 | Budget policy and durable state | merged; criteria mutation-proven | done (#756) |
| B | 2 | Claude adapter and bounded readiness | one verified task; retry, no-fallback, cancel and funding rules proven | done (#769) |
| C | 3 | PR lifecycle and fresh context | head-pinned checks, duplicate events, closure is not success, fresh base and handoff | done (#771) |
| D | 4 | Operator controls, evidence, pilot | status, controls, backup and restore, archives, metrics; pilot of three tasks | done (#773, #774; pilot #777, #778) |
| E | 5 | Codex and Cursor adapters | transfer preserves task, PR, attempts and pool; previous writer stopped and reconciled; one real task per enabled adapter | done (#780, #781); live run in F |
| F | none | Close-out | live run recorded, findings fixed, #779 closed, drift answered, questions asked | **done** (#784, #785, #789, #791, #792) |
| **G** | — | **Not planned: the owner's choice** (below) | set by the owner | — |

**Work items.** No branch is in flight.
- **#785:** `fix/budgeted-autonomy-preflight-findings` at `c1aff5246`, merged as `af7ff471b`.
- **#784:** `docs/budgeted-autonomy-owner-decisions` at `2c35c56f0`, merged as `dccba4e03`.
- **#789:** `docs/budgeted-autonomy-live-run-decisions` at `2c5f704f3`, merged as `f2ddbee01`.
- **#791:** `fix/budgeted-autonomy-live-run-findings` at `da496656b`, merged as `200fd1e5e`.
- **#792:** `feat/budgeted-autonomy-776-brief` at `bf3473a73`, merged as `7c50e379f`.

The remote branches are deleted. The lane worktrees and their `worktree-wf_*` branches are removed; every function they added is on master, which was checked before removing them. The local branch `rewrite-storage` in the `ashlar-phase-f` clone is the rewrite's base, `0aa936c5b`, which is on the storage branch. It is not to be pushed.

The audit found a pre-rewrite local ref in the `ashlar-record-correction` clone: `claude/budgeted-autonomy-workspace` at `3ad39c834`. It had no upstream. Its only difference from its rewritten counterpart `4aecb9f03` was the unredacted PR 4 tarball. **It was deleted after the audit ran**, so it can never be pushed. `handoff-publish.sh` parents on the remote branch, so publishing would not have reintroduced it anyway.

**Verification on the merged PRs:**
- **#785:**
  - host and container suites: 513 tests at the head `c1aff5246`;
  - `scripts/mutation-check.sh`: f1 to f15, **15 of 15 KILLED** at the earlier branch commit `738198fe2` (510 tests then), for example `mutation f3-codex-service-tier-not-refused: KILLED red=failed:4/510 green=passed:509/510`.
- **#791:**
  - host and container suites: 539 tests;
  - the lanes' own hand mutations, each observed red;
  - `scripts/mutation-check.sh`: **12 of 12 KILLED** at `da496656b`, for example `mutation live-P-M3: KILLED red=failed:4/539 green=passed:538/539`.
- **#792:**
  - host and container suites: 555 tests;
  - the builder's 9 or more hand mutations and the review's 2, all observed red;
  - `scripts/mutation-check.sh`: **4 of 4 KILLED** at `bf3473a73`, for example `mutation c776-line-break-allowed: KILLED red=failed:11/555 green=passed:554/555`.
- Each PR body quotes its lines verbatim. An adversarial reviewer checked each PR in its own clone. The batch summaries are on the storage branch.

**Phase G, if the owner opens one.** These are candidates, not a plan; the owner picks.
1. **Card #790: isolate Claude workers** from the operator's user-level hooks, skills, agents, slash commands and auto-memory. The card holds its "Done when". Probe the installed CLI first and declare only what the probe shows.
2. **Small hygiene PR** (needs no owner decision):
   - `.gitattributes`: `.claude/workflows/*.js text eol=lf`. See §6: the saved workflow cannot be invoked by name from a CRLF checkout.
   - The reviewers' coverage nits (§7).
   - The stale "Not in PR 3" line at README:942 (`tick --adapter accepts fake and claude only`), unless the owner prefers the historical sections frozen.
   - README:3 and :37, which overstate tick-driven bus claims and card intake (the audit).
   - Delete `tools/autonomy-controller/PILOT-PR4.md`, the throwaway file from #777, which nothing references.
   - Mark the run sheet `live-run-pr5.md` on the storage branch as historical. It never got the pre-flight's S1 change: it still promises 3 attempts and has no lease-release step.
3. **A guarded live exercise of merge and repair** (open question 2). It needs an owner row first.
4. **validate.py improvements** from the phase E audit (§7). These are project tooling, not this workstream's.

Exit criteria for any of them: the item merged on the five required checks, mutation-checked, with the bus `claim`, `handoff` and `done`, and then `/handoff budgeted-autonomy G`.

## 5. How to do the next phase's routine work

1. Work in a clone. The session checkout `ashlar-record-correction` is a clone, but its master is at `af7ff471b`, behind. The live run's ledger points at it: the Cursor `exit_recorder` path in the run manifests. `ashlar-phase-f` is the clone this phase used, at master `7c50e379f`.
   - Run `git checkout master && git pull --ff-only && git checkout -b <type>/budgeted-autonomy-<topic>`.
   - Leave the untracked `AGENTS.md`, `.agents/`, `pilot-*.json` and `pilot-report.md` alone.
2. Verify:
   - **Host suite:** `bash tests/scripts/autonomy-controller.test.sh`. 555 tests, `OK (skipped=1)` on Windows, about 2 minutes. Floor 536.
   - **Container suite:** `bash scripts/test-in-container.sh -- 'bash tests/scripts/autonomy-controller.test.sh'`. It tests the COMMITTED tree, so commit first.
   - **Docs:** `bash scripts/check-docs-phantom-paths.sh`.
3. Commit, then mutation-check against the commit:
   - Command: `ASHLAR_MUTATION_RUNNER="bash tools/autonomy-controller/mutation-runner.sh" scripts/mutation-check.sh --id … --file … --old … --new …`.
   - Put the python3 shim on PATH **in POSIX form**.
   - The batch driver this phase was `scratchpad/mutcheck/run.sh`, with `prep.py`, which checks that each `old` text occurs exactly once before the batch starts. It reads a JSON list of `{id, file, old, new}`. `batch-776.json` is the latest example.
   - About 2.5 minutes per mutation.
   - **Never `git checkout --` a file with uncommitted work in it.** This phase it discarded a lane's changes once; they were restored from a backup.
4. `git add <paths>` by name, then `python scripts/knowledge-graph/build-knowledge-graph.py`. It was unchanged for every phase F PR.
5. `git push -u origin <branch>`, then `gh pr create --body-file`.
6. On the bus, draft each post as a file and post it with `gh issue comment 695 --body-file`.
7. Merge with `gh pr merge <n> --squash --match-head-commit <sha>` once the five required checks are green, unless Grok has posted a `block`. Then post the `done` with the merge SHA.
8. Live controller runs belong to the owner. A run happens from the owner's terminal, or is delegated by an owner row (2026-10-13, for that run only). The session may run `status`, `report`, `metrics`, `tick --dry-run`, `init`, `task add`, `provider disable|enable` and `backup`.

## 6. Environment facts

- **Required checks.** There are five: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)` and `Readiness summary`. Strict and enforce_admins are on, and merges are serial.
- **Repo gates on this host.** On untouched master, `mutation-check.test.sh` and `session-start-hook.test.sh` fail on this Windows host, as before.
- **The saved workflow `project-audit` cannot be invoked by name from this checkout (new).** `core.autocrlf=true` checks `.claude/workflows/project-audit.js` out with CRLF. The Workflow tool's approval check then refuses it: "script contains control characters that would be hidden in the approval dialog". The workaround this phase:
  1. write an LF copy with `MSYS_NO_PATHCONV=1 git show origin/master:.claude/workflows/project-audit.js > <scratchpad>/project-audit.js`;
  2. run it with `scriptPath`.

  The permanent fix is a `.gitattributes` line (§4 phase G item 2). Also, the `previous` status passed to that run was condensed, to keep the call's arguments manageable: its `stale_docs` list was merged into fewer rows, and its `markdown` was replaced by a one-paragraph summary. Every other field was passed as published.
- **`git show <ref>:<path>` under Git Bash** needs `MSYS_NO_PATHCONV=1`; otherwise the path is converted and the revision is "ambiguous".
- **The python3 shim.** `python3` on this host is the Store stub. This phase's shim is `scratchpad/mutcheck/pybin/python3` in this session's scratchpad (`875065c3-…`). It `exec`s `C:/Users/icfre/AppData/Local/Programs/Python/Python313/python.exe "$@"`. Recreate it if temp reaping removed it.
- **Host tools:** Python 3.13, `gh` as `IanFrelinger`, docker with image `ashlar-devtest:local`.
- **Codex CLI** (memory `ashlar-provider-clis-on-host`):
  - The working binary is `%LOCALAPPDATA%\OpenAI\Codex\bin\2e5e00daee91c61d\codex.exe`. The `.sandbox-bin` copy is broken.
  - `-c mcp_servers={}` removes nothing. `-c features.plugins=false` and `-c mcp_servers.<name>.enabled=false` work.
  - Verify an override without a model call, using `codex debug prompt-input` and `codex mcp list`.
  - Every live `exec` prints `Reading additional input from stdin...` on stderr, even with stdin at DEVNULL.
  - The owner's `service_tier = "priority"` is restored (§3 question 4).
- **Cursor CLI.**
  - It is logged in now.
  - Its model list has `composer-2.5` and `composer-2.5-fast`; `composer-standard` is not offered.
  - `cursor-agent status` prints the account email, which the adapter masks since #791.
  - The stream ends with a `result` event (`subtype`, `is_error`, `duration_ms`, `session_id`, `request_id`, token `usage`), but has no turn, request, cost or allowance figure.
  - Its exit code survives across tick processes through `adapters/_exit_recorder.py`.
- **Claude CLI.**
  - A run capped at `--max-turns 12` reports `num_turns` 13, every time.
  - Every live `rate_limit_event` carried both windows: five-hour 0.02 and seven-day 0.69. The adapter now records the higher one.
  - Transcripts carry a `session_context` attachment with the account email; archives redact it since #791.
  - Workers load the operator's hooks, skills, agents and auto-memory (#790).
- **The classifier this phase.**
  - Allowed: `gh issue comment 695`, `gh issue create`/`close`/`edit`, `gh pr create`/`merge --match-head-commit`, `git push`, and the storage branch's `git push --force-with-lease` after the rewrite.
  - Not attempted: worker-launching ticks. The live run was delegated to Codex instead.
- **Line endings.** `core.autocrlf=true`: Markdown and `.js` files are CRLF on disk, and Python and JSON are LF. A Python patch must keep the file's line endings.
- **Heredocs in the Bash tool** mangle `\n` and Windows paths. Write scripts with the Write tool and run them by path.
- **Personal data.** Scan every archive **recursively**, nested tarballs included, before publishing it. The phase E pre-flight missed nested archives.

## 7. Queued work

- **Card #790:** Claude worker isolation (owner: later).
- **The reviewers' coverage nits from #791 and #792.** None is a known defect.
  - A test that a Cursor stderr blocker text is masked.
  - More evidence-fixture variants: a non-dict `attachment`, and an address split across `rendered` items.
  - NaN and boolean utilization subtests for the allowance.
  - The risk of a lone surrogate in a masked text member when it is encoded.
  - The 1 -> 2 and 2 -> 3 migrations read the schema version outside their transaction. Two processes opening the same old ledger read-write at once could each journal a `schema-migrated` row; the column is added once. This predates #792.
- **`.gitattributes` for `.claude/workflows/*.js`** (§6).
- **A task chip, "Fix CR in mutation-check.sh count parsing on Windows",** was offered to the owner this phase. It is a project script, not this workstream's.
- **Known limits** (README "Not in PR 5"):
  - `outbox flush` and `provider disable|enable` leave no journal row.
  - The Claude allowance's per-session delta compares account-wide values, so a new session's first delta overstates its use. It is recorded, never charged.
- **Not exercised live:** merge, a repair round, Codex resume, Cursor resume and cancel, outbox `find`, a transfer of a finished writer, and a transfer while a PR is open.
- **The 48-turn resumed run** (`run-3225ebdb59dd`, PR 4 pilot) is still unexplained. There was no second sighting: the PR 5 resume run-a062813c1dae stopped at 13.
- **Still from earlier phases:**
  - The `phases` table is unwritten.
  - `premium_escalations_used` is counted and not driven.
  - Nothing prunes `<state dir>/evidence/`.
  - `restore` does not stop a controller that holds the lease.
  - The brief items no card scheduled: tick claims on the bus, card intake, container validation in a tick, the controller merging, the verifier role, premium escalation.
- **The six PR 5 archives** stay local (§2). Do not publish them without a recursive scan and redaction.
- **The audit's findings about this workstream** (`status.workstream_findings`):

  - The roadmap is closed: all five #755 PRs merged and cards #755, #768, #770, #772, #779, #776, #782 and #762 closed. #762 was done by this workstream (storage file 12-done-card-762.md); count it once. #790 is open and deferred by owner row README:847.
  - Phase F met 4.5 of 5 exit criteria. Only the publication of this handoff remains for 'live run recorded in ledger and handoff'.
  - Grok posted drift-792 clean at 19:31:15Z (6153522959), after the workstream reader ran. No drift ask is open for this workstream. Project-wide, SPEC-007's drift-746 and drift-748 are still open, so the handoff must not say 'no drift ask is open' without that scope.
  - Privacy residue: GitHub still serves pre-rewrite storage commits cbc1a0d72929 and ec6ac7cfd by SHA, and cbc1a0d72929's tree holds the unredacted PR 4 tarball blob a81c1a1c. Both SHAs are cited publicly (bus 6151763815, live-run-pr5-record.md). README:841's 'removed from history' holds only for reachable commits. The owner's email is already public as the author email on 712 master commits, so a purge decision should rest on the tarball's other contents, which nobody has inventoried.
  - The local ref claude/budgeted-autonomy-workspace at 3ad39c834 in this clone is a pre-rewrite copy holding blob a81c1a1c and must never be pushed. handoff-publish.sh:74-82 parents on the remote branch, so publishing does not reintroduce it.
  - Six PR 5 evidence archives stay local under pr5\state\evidence, four with the owner's email unredacted; `evidence archive` refuses to overwrite. There is no decision row.
  - Outbox post 6149018991 was signed From: codex, against the 2026-10-12 row requiring From: claude. The dedup key is spent, and there is no ruling.
  - Paths never run live, only against fakes: controller merge (gh pr merge --match-head-commit), a repair round, check observation on a Codex or Cursor PR, Codex resume, Cursor resume and cancel, outbox find, and transfers of a finished writer or with a PR open (README.md:874-879, 916, 946-948, 960-962).
  - Brief items no PR delivered: tick posts no claim, handoff or done (no outbox call in controller.py or lifecycle.py); there is no card intake from GitHub; container validation is operator-driven, not in tick; the phases table is never written (store.py:86); premium_escalations_used is counted but not driven. README.md:3 and :37 overstate the first two.
  - Known limits with no card: nothing prunes <state dir>/evidence; restore does not stop a lease holder; outbox flush and provider disable/enable leave no journal row; Codex and Cursor have no turn limit or allowance figure; Cursor's billing unit is unobserved.
  - The 48-turn charge on resumed run run-3225ebdb59dd is still unexplained (README.md:308-321).
  - The run sheet live-run-pr5.md is stale (S1 not applied: it still promises 3 attempts and lacks a lease release step).
  - tools/autonomy-controller/PILOT-PR4.md, the throwaway file from #777, is still on master.
  - #792 changed CHANGELOG.md and tests/scripts beyond #776's stated scope; this is disclosed in the PR body as an integrator decision.
  - AGENTS.md (Codex instructions) and .agents/skills are untracked in this clone, yet Codex operated the live run. pilot-report.md, pilot-status.json and pilot-metrics.json (ctl-pilot-pr4 output) are also untracked. None contains the owner's email.
  - MEMORY.md's open-threads line ('PR 5 live run still not done') is stale.
  - validate.py timing: it read required checks before the push checks finished, and read PR mergeability from the lazy list call (UNKNOWN). Its stranded-branch count moved from 69 to 66 within the hour. Consider re-reading checks after completion, and using per-PR views.
  - Integrator decisions from phases A-F have not been promoted to owner rows (README.md:835).

## 8. Ways of working the owner has endorsed

- One phase per thread, ending with `/handoff`. Run the phase-end validation and audit before every handoff.
- **Claim before you build.** Read the newest handoff and the bus first, and reuse the card if one exists. Release a claim on the bus when the owner hands the work to another agent (post 27), and re-claim it afterwards (post 28).
- **Parallel lanes in isolated worktrees** for throughput, with a written contract and an adversarial reviewer per PR. Verify the merged branch yourself.
- **Owner decisions are dated rows** in the README's decisions log, landed in a records PR **before** the code that depends on them. Every other default is an integrator decision, in the PR body and the handoff.
- **Declare nothing unverified as a capability.** Probe the CLI, save the report, and cite the probe in the code.
- **Mutation-check every behaviour** with `scripts/mutation-check.sh` after committing. Quote the summary lines verbatim.
- **A pre-flight before a live run.** A read-only fan-out over the code against the run sheet. This phase it found 15 defects before the run.
- **A verification workflow after a live run.** Readers over the ledger, the console, the archives and GitHub, then a synthesis. Fix what it finds in one small PR.
- **Never publish personal data.** Scan recursively. If something leaks, redact it and rewrite history, with an owner decision.
- **Correct your own false statements on the bus at once** (post 29).
- The owner performs logins, credential steps and worker launches, or delegates them explicitly in a dated row.

## 9. Starting prompt for the next phase

> The budgeted-autonomy roadmap is closed (phase F done: #784, #785, #789, #791, #792; cards #779 and #776 closed). If you want another phase, say which item of handoff F §4 "Phase G" it covers. The candidates are card #790 (Claude worker isolation), the small hygiene PR, a guarded live exercise of merge and repair (that one needs an owner row first), or validate.py improvements. Then: Execute **phase G** of budgeted-autonomy. Run `/start-phase budgeted-autonomy`, or read this handoff in full, starting with §0. Check §1 against the live repo and the bus: master `7c50e379f`, and anything posted after Grok's drift note on #792 (6153522959). Claim the card on the bus before building. Build each item in a small PR with tests, mutation-checked with `scripts/mutation-check.sh`, and merged on the five required checks. Build and test only through `scripts/test-in-container.sh` for anything that needs `dotnet`; never run `dotnet` on the host. If anything I have not decided blocks you, ask instead of choosing, and land a decision as a dated row before the code that depends on it. When the phase is done, run `/handoff budgeted-autonomy G` and notify me.
