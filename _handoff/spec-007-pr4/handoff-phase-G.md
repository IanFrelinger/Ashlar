# spec-007-pr4 handoff: end of phase G, start of phase H

*Written 2026-10-11 ~05:00 UTC at the end of phase G, on the owner's machine (Windows host, Docker Desktop, Git Bash). The owner stopped the phase's four follow-up lanes before any of them committed; this handoff carries their briefs so phase H can run them. Start the next thread with `/start-phase spec-007-pr4`, or with the prompt in §9. Storage branch `claude/spec-007-pr4-workspace`, folder `_handoff/spec-007-pr4/` (`bash scripts/handoff-fetch.sh --workstream spec-007-pr4 --list`).*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`): end a phase with `/handoff spec-007-pr4 <phase>`; start one with `/start-phase spec-007-pr4`. The handoff is published to the storage branch, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff.

**Running locally (what phase G learned):**
- `CLAUDE.md` binds: never run `dotnet` on the host; build and test only in the devtest container through `scripts/test-in-container.sh` (committed state of a clone, never a worktree). The image `ashlar-devtest:local` exists; Docker's VM is capped at 5 GB by `%USERPROFILE%\.wslconfig`, so **one container run at a time** across every agent and the main thread (check `docker ps -q --filter ancestor=ashlar-devtest:local` first). Five agents at once is the host's ceiling.
- The host has `python` (3.13, PyYAML) but `python3` is the Store stub; the gates runner probes for `python` itself. No host shellcheck and none needed: no repo gate invokes it (the `shell-lint` workflow runs it as its own step); `docker run --rm -i koalaman/shellcheck:stable -S error - < file.sh` lints a script locally.
- `bash scripts/ci/run-repo-gates.sh` reports **26 of 28** on this host on untouched master: `tests/scripts/mutation-check.test.sh` (tracked-symlink fixture) and `tests/scripts/session-start-hook.test.sh` (docker-wrapper and `find_real_docker` groups, 73 passed / 50 failed) fail identically on any branch. Host artefacts; CI is the check that counts. #756 added a 29th gate.
- **The auto-mode permission classifier refuses `gh issue comment`** (External System Writes), twice, with one comment or nineteen. It allowed `git push` (branches, tags, deletes), `gh pr create`, `gh pr merge --squash`, `gh pr close --comment` and `gh issue create` (eleven cards). So bus posts are prepared as files and the owner posts them, says yes in chat, or adds an allow rule for `gh issue comment` to `permissions.allow` (the `update-config` skill edits it). `gh` is authenticated as IanFrelinger with `repo` scope; only the classifier stands in the way.

## 1. Where things stand

- **Master:** `1d8bc7448` (#756, budgeted-autonomy PR 1, another Claude workstream). Before it: `9faefe46c` (#749, merged by the owner's side 2026-10-11 04:24Z), `c0d110183` (#754, this phase), `ac264559f` (#748, the head phase G reviewed). Readiness: `ac264559f` `verified`; `c0d110183` has no readiness run (docs-only paths do not trigger it); `9faefe46c` and `1d8bc7448` unread at writing (`scripts/release/readiness-verdict-for-sha.sh <full sha>`).
- **Merged this phase:**

| PR | What | Merge | By |
|---|---|---|---|
| #754 | Agent-bus amendments: identities `grok|claude|cursor|codex`, `Kind: claim`, `agent-task` cards, "before starting any work", "decisions land in the log first", ownership extended to Cursor/Codex for assigned cards | `c0d110183` | Claude (this thread) |

- **Merged earlier in the workstream:** the table in `handoff-phase-F.md` on the storage branch (#720–#748); every head `verified` except `3023ef1ea` (#744), repaired by #745 `d4dfbf379`. Re-read by this thread with `readiness-verdict-for-sha.sh`: fourteen VERIFIED, one REFUSED (failed), as the backfill says.
- **Agent-bus (#695):** 61 comments. Grok resumed 2026-10-11 03:07Z: `drift-746` and `drift-748` asks to Claude (stale report-only sentences; "Merge pending"; DocsIndex; the certification count), a release-notes draft (`release-ag-sw`), `land-749-753`, and four `mutations-spec007` status posts with every hygiene (21/21), producers (29/29) and redirects (30 KILLED, `m6`/`m8`/`m20` INVALID) line on `ac264559f`. Another Claude session posted `claim`/`handoff`/`done` for card #755 / PR #756. **This thread posted nothing** (classifier): the 19-comment fact-checked backfill is attached as `bus-comments.tar.gz` (one file per comment, numbered in posting order; comment 19 `roles` amended to cite #754); the `done` replies for `drift-746`/`drift-748` wait for R3a (§4). Grok's drift audit of #724–#745 has still not run.
- **Decisions log (SPEC-007) integrity:** the three unattested rows (2026-10-08 D16; 2026-10-10 §8 Q3; 2026-10-10 §8 Q4) are **still on master unconfirmed**; the owner has not answered. The review's exact replacement texts for confirm and for strike are in `REVIEW-2026-10-11.md` §3.
- **This phase's exit criteria** (phase G starting handoff §4): backfill posted and Grok asked — **not met** (prepared; classifier); the three rows confirmed or struck and landed — **not met** (owner); review of 4.7/4.8/4.9/4.11/#748 finished, blockers to PRs, majors to cards — **review met** (`REVIEW-2026-10-11.md`, six lenses + synthesis, 7 agents, 85 minutes), **cards met** (#757–#767), **PRs not met** (the owner stopped the four lanes before they committed; briefs attached); agent-bus amendments landed — **met** (#754 `c0d110183`); PR 5 design question put to the owner — **met, unanswered**; housekeeping — **partly met** (lane branches archived and deleted; #747 closed; gRPC twins run for #753; Dependabot left to #753; #749–#753 assessment stopped by the owner).

## 2. Read these first

**In the repo (master `1d8bc7448`):** `CLAUDE.md` (the bus section is new: identities, the claim rule, cards, decisions-log-first); `_handoff/bus/PROTOCOL.md`; `docs/specs/SPEC-007-security-labels-and-reference-monitor.md` (status table rows 4 and 5; decisions log rows ~519-521 and intro ~494-497); `docs/EgressInventory.md`; `ci/cert-gate-assertions.md` (row 64, the count paragraph); `scripts/mutations/spec007-*.json` + `scripts/run-spec007-mutation-batch.py`.

**On the storage branch `claude/spec-007-pr4-workspace`, `_handoff/spec-007-pr4/`:**
- `handoff.md` (this) = `handoff-phase-G.md`; `handoff-phase-F.md` (the phase G starting handoff, 2026-10-11, cloud thread: its §1 table of fifteen merges and §3 decisions are still the reference); `handoff-phase-C.md`, `-B.md`, `-A.md`.
- **`REVIEW-2026-10-11.md`** (new, 341 lines): the six-lens review of master `ac264559f` for the owner — §1 verdict, §2 the 2026-10-08 blockers closed, §3 decisions-log integrity with replacement texts, §4 blockers BL1–BL5, §5 majors/minors per PR, §6 records drift with true values, §7 process, §8 follow-up plan, §9 what must be true, §10 owner questions. Lens notes: `review2-conformance-4.7-4.8.md`, `review2-conformance-4.9.md`, `review2-conformance-4.11.md`, `review2-remediation-and-PR5.md`, `review2-write-down.md`, `review2-records.md`, `review2-synthesis.md`; `review2-open-prs-assessment.md` is the partial note of the stopped assessment lane.
- `review-enforcement-local.js` (new): the Workflow script that produced the review, adapted to this host (two waves of five and one, one container at a time). `review-enforcement.js` is the cloud original.
- `lane-briefs-phase-G.md` (new): the four lane briefs the owner stopped (PR 4.8b, R3a records, manifest hygiene, PR assessment), verbatim, ready to hand to agents again.
- `bus-comments.tar.gz` (new): the 19 backfill comments. `agent-task-cards.md` + `cards-created.log` (new): the eleven cards' texts and issue numbers.
- `REVIEW-2026-10-08.md` + `review-lens-*.md`: the earlier review of #720/#722/#721/#723. `DESIGN-4-final.md` (its `:605` multi-frame sentence and §5 C12 are still wrong, card #763), the attack lists, `INTEGRATION-NOTES-C.md`, the state files, logs and mutation archives: phase A–C material.

## 3. Decisions

**Owner decisions, recorded on master (attested):** 2026-10-05 Q1–Q8 (#713); 2026-10-06 D31 amendment (#716); 2026-10-06 open read scope ⇒ SystemHigh for every tool; Q8 clarified; O2 never follow a redirect into the host boundary (R1 #724; implemented by #740 and #739). 2026-10-11: the agent-bus amendments (asked for in the cloud thread; landed as #754).

**Rows on master the owner has not attested (confirm or strike; replacement texts in `REVIEW-2026-10-11.md` §3):**
- `:519` 2026-10-08 "PR 4 (4.5b, D16) … the owner accepted the review recommendation" — written by #740 (Cursor, harvested from Codex); the Cursor phase C handoff says Q1–Q9 were not answered and D16 was a default; the code (`EgressGuardChatClient.cs:140-145`) is fine either way.
- `:520` 2026-10-10 "PR 5 (§8 Q3): clearances are configured in `ashlar.policy.yaml`…" and `:521` "PR 5 (§8 Q4): a skill's highest level stays in the sealed package manifest…" — written by #748 (Cursor); its body cites no owner answer; both still say "Merge pending"; §8 Q3/Q4 carry no "Answered" marker; the intro `:494-497` was rewritten by #748 to claim "the PR 5 answers of 2026-10-10". `codex/spec007-clearance-reads` carries a *contradictory* 2026-10-10 "owner" row for Q4 ("Use all 3 … both the package manifest and signed certification record").

**Defaults the Cursor/Codex agents took where the owner had not decided** (unchanged; now also listed for "Design as merged" bullets in the R3a brief): Q1 mesh serve on AG/SW with no bind fails boot (#720/#738); Q4 multi-frame vision on AG refuses a cloud resolve (#738); Q5 `PostConfigure` reasserts the strictest profile (#738); Q6/D16 every non-`local:`/`cloud:` target observes SystemHigh (#740); the Bedrock refusal at `ValidateOnStart`/first resolution (#739); PR 5's shape (#748) without a design pass. The "D26 deviation" in #746's body is **refuted** by the review (`ToolCallingAgent.cs:243-297`: REFUSED observation, cycle continues, third refusal stops with `egress_refused`); the body's sentence is about the C12 adaptation only (owner to accept, card #763).

**Still open (owner):**
- **Q-A** the three rows above.
- **Q-B PR 5's shape.** Two implementations exist: #748 on master (composition-time clearance from `ashlar.policy.yaml`, host meet, skill level on the sealed package; `SkillComposition.Decide` has **no production caller**, review BL3) and the unmerged Codex branch `codex/spec007-clearance-reads` @ `91f0c0d78` (50 commits over `7cb132897`, 99 files, +3,328/−258, no PR, never on CI, no mutation lines): receiver-side `SubjectReadGuard.RequireRead` at the A2A, gRPC, peer-brick, MCP, RAG and chat seams, exact-id `agentClearances` with per-id host limits read from the operator environment, a signed record schema v3, default-refuse of every peer response until granted. `git merge-tree` against `ac264559f`: conflicts in SPEC-007, `PolicyLoader.cs`, `Ashlar.Manifest.csproj`, the knowledge graph. (i) continue from #748 and add receiver-side `CanRead`; (ii) take the Codex shape and strike #748's schema and Q4 row (peer transports stop working with no policy file); (iii) design pass first. Do not merge the Codex branch as is.
- **Q-C Q6 scope:** does "inbound surfaces stay on loopback on AG/SW" cover `Ashlar.Transport.Grpc.Server.Host` and `Ashlar.Commercial.Fleet.Host` (design §2.7) or only the API and mesh serve (the row's text)? Both hosts bind any configured URL on AG/SW today (review M8, card #766).
- From 2026-10-08: Q1 mesh-serve default (fail-boot, de facto); Q2 merge method (`allow_merge_commit=false`?); Q4/Q5 confirm the de facto rules; Q8 accept Grok's bus-posted mutation runs as the record once `m6`/`m8`/`m20` are repaired (card #764); Q9 **done** (archived). New from the review: (a) `docker exec` into an Ashlar-started `--network=none` session as Host or recorded limit; (b) exit 77 for every CLI refusal or export verbs only; (c) `WorkflowExecutor` `FailurePolicy.Continue` past a refused brick; (d) the C12 adaptation.

**Integrator decisions of this phase (Claude):** the `roles` backfill comment cites #754 instead of proposing an interim rule; the gRPC 2.84 bumps were verified once as one combined commit (`ashlar-g-deps`, branch `deps-grpc-284` @ `871039008`: `Passed! 10/10` Ashlar.Tests.Transport net8.0, filter `GrpcAgentTransportTests|Local_transport_refusals`, log `g-refs/deps-logs/grpc-twins-284.log` on the host) rather than per PR; #747 closed as superseded with its CONTINUITY hunk assigned to R3a; the lane branches archived on the review's harvest verdict (owner Q9 was in the phase plan).

## 4. Plan and live status

| Phase | Scope | Status |
|---|---|---|
| A, B | design; 4.1/4.6/4.2/4.4 | done |
| C | 4.3, 4.5, 4.10 + remediation (R1, 4.3b, 4.5b, 4.10b) | done (Cursor, 2026-10-09) |
| D, E, F | 4.7, 4.8; 4.9; 4.11 | merged by Codex with no handoffs; reviewed in phase G |
| G | review of D–F; bus backfill; decisions-log integrity; protocol amendments; cards; housekeeping | done except: backfill unposted (classifier), rows unanswered (owner), follow-up PRs not built (owner stopped the lanes) |
| **H (next)** | **owner answers → R3b rows PR; R3a records PR; PR 4.8b; manifest hygiene PR + gate; bus posts; #752/#753 landing; PR 5 per the owner's choice; cards #757–#767** | this file |

**Work items of phase G:**
- #754 agent-bus amendments: merged `c0d110183`; five required checks green; host gates 26/28 (artefacts); KG unchanged. Branch deleted.
- The review: Workflow run `wf_e3f66366-333`, script `review-enforcement-local.js`; 7 agents, 0 errors, 85 min; verdict **defective in records and in two refusal-masking catch-alls, sound in the switch**; blockers BL1–BL5, majors M1–M13 (§8 of the review has the PR plan). Code blockers BL4 (`ProviderFactory.cs:792-798` + `ProviderRefusalScope` + `ProviderBackedModel.cs:28-30` + `HotSwappableModel.cs:64-99`: the Ollama health-probe refusal is dropped outside `AdaptiveProviderFactory` and becomes echo output under `prefer=deterministic`+provider or `ASHLAR_ALLOW_MOCK=1`) and BL5 (`OrchestrationHotSwappableModel.cs:52-67`: echo fallback on any exception under `ASHLAR_ALLOW_MOCK=1`, wraps every orchestration agent's model) were **re-read and confirmed by the main thread**.
- Cards #757–#767 created (label `agent-task`): #757 gate ignores comment-only diffs; #758 EventId 7308; #759 `WorkflowExecutor` `FailurePolicy` (owner); #760 double audit record; #761 pre-switch wording and the theory name (fold into R3a); #762 inventory citations; #763 `DESIGN-4-final.md:605` and C12; #764 mutation evidence pass over all seven manifests; #765 PR 4.9b (exit 77 central, Initiator convention, `-v` mounts, `docker exec`; partly owner); #766 inbound hosts under Q6 (owner); #767 Dependabot #728 hold.
- Lane branches `claude/spec-007-pr4-{4.3-redirects,4.10-agsw,4.5-producers}`: tagged `archive/spec-007-pr4-<lane>` at `4d8860c5`/`696d3819`/`91d3b48c`, deleted.
- #747 closed (CONFLICTING; content on master via #748 except the CONTINUITY hunk).
- Four follow-up lanes launched 04:3xZ and **stopped by the owner** before any commit: clones `ashlar-g-48b` (branch `fix/spec007-4.8b-refusal-masking`), `ashlar-g-r3a` (`docs/spec007-r3a-records-drift`), `ashlar-g-manifests` (`test/spec007-mutation-manifest-hygiene`), `ashlar-g-prs` (master) exist under `work/`, all at `origin/master`, clean; nothing pushed. Briefs in `lane-briefs-phase-G.md`.
- Meanwhile landed by others: #749 (`9faefe46c`), #756 (`1d8bc7448`). Open: #750 and #751 (DIRTY: need a rebase), #752 (Grok ProdStyle twins, needs its container run on net10.0 quoted), #753 (Grok's consolidated Dependabot bump; the gRPC twins evidence above is what its unticked box asked for; #728 held), Dependabot #728–#737 (superseded by #753; close them when #753 merges).

**Phase H, spelled out, in order:**
1. **Post the bus** (owner, or the rule): the 19 backfill comments in order, then `claim` for each lane PR as it opens, `handoff` when ready, `done` after merge; answer `drift-746`/`drift-748` with R3a's `done`; acknowledge `mutations-spec007` (the three INVALID ids go to the manifest PR).
2. **Owner answers** Q-A, Q-B, Q-C (and (a)–(d) if quick). Land Q-A as **R3b** (the three rows + the intro + the §8 markers; one row each; texts in the review §3). Nothing that cites those rows merges before R3b.
3. **R3a** (records drift, no owner dependency; brief attached; Grok asked that SPEC-007 and the egress sources not be edited "until the review lands" — it has). Then `done` on `drift-746` and `drift-748`.
4. **PR 4.8b** (BL4, BL5, M2; brief attached): twins red first, mutations in `spec007-refusal-surfaces.json`, lines quoted.
5. **Manifest hygiene PR** (M1; brief attached): r14, m20, m6 (investigate the vacuous differential twin), m8, leak-m03; the new gate `verify-mutation-manifests-current.py` + test. Then card #764's full serial pass.
6. **Land #752 and #753**: #752 after one container run of `EgressExplicitApiProdStyleTests` on net10.0 with the `Passed!` line quoted; #753 on green required checks (its gRPC evidence is in §3), then close #729–#737 as superseded; #728 per card #767. #750/#751: the owner's side is landing Cursor's PRs (#749 merged while this thread ran); do not double-work them without a `claim`.
7. **PR 5** per Q-B; the Known limit "inbound response bodies are not mediated" stays until the receiver-side seams land.
8. **Cards** #757–#763 as capacity allows; #765/#766 after the owner's answers.
9. **End with `/handoff spec-007-pr4 H`.**

## 5. How to finish a lane (routine, local)

1. Clone: `git clone -q <work>/ashlar-g-review <work>/<lane> && git -C <work>/<lane> remote set-url origin https://github.com/IanFrelinger/Ashlar.git && git -C <work>/<lane> fetch origin master`, `git config core.longpaths true`, set `user.name`/`user.email`, branch from `origin/master`; one agent per clone; never test in a worktree. The canonical checkout `ashlar-record-correction` is the main thread's.
2. Twin first; a behaviour change is committed twin-alone and shown red in the container, then green.
3. Merge master in before verifying; recount (Certification count paragraph with `git ls-files src/Ashlar.Tests.Infrastructure/Tests/Certification`; TSV sums for row 64; the `EgressGuardConventionTests` floors from F6/F8 in the container); `git add` named paths, then `python scripts/knowledge-graph/build-knowledge-graph.py`, commit.
4. Verify in the container, one run at a time: `bash scripts/test-in-container.sh --repo <clone> -- bash scripts/run-cert-gate.sh` (net8.0); touched classes on net8.0 and net10.0; Abstractions on netstandard2.0/net8.0/net10.0 when it changes; `build-core` when a project reference or TFM changes; `bash scripts/ci/run-repo-gates.sh` on the host (the two host artefacts named in §0).
5. Mutation-check each behaviour (`scripts/mutation-check.sh`, or a manifest under `scripts/mutations/` through `scripts/run-spec007-mutation-batch.py`); quote every summary line verbatim in the body.
6. Adversarial check, two lenses (write-down; records/evidence); repair until clean.
7. Open the PR from the template; `[coordinated-integration]` iff non-test code under both `src/` and `application/` changes; "(this PR)" → "(#N)" in SPEC-007; **post `claim` before, `handoff` when ready, `done` after merge** on #695 (as files if the classifier refuses).
8. Merge (squash, `gh pr merge --squash --delete-branch --subject "<title> (#N)" --body-file <commit body>`) on the five required checks green unless Grok posted `block`; read the readiness verdict with `scripts/release/readiness-verdict-for-sha.sh <full sha>`.

## 6. Environment facts

- Five required checks on master: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`; strict + enforce_admins; merges serial; `gh pr merge` is in the user's allow-list.
- Repo gates: 28 at `ac264559f`, 29 after #756 (`run-repo-gates.sh --list`); 26 (now 27) pass on this host (§0).
- Readiness no longer push-triggers on `cursor/**` (#723); docs-only master pushes get no readiness run; PR verdicts are `partial`; read a master head with `scripts/release/readiness-verdict-for-sha.sh <full 40-char sha>`.
- True counts at `ac264559f` (records lens, measured): TSV 86 rows / 149 occurrences / **66 guarded / 41 `-` / 23 Exempt / 10 Factory / 4 Upstream / 4 Governance / 4 ProcessFunnel**; F6/F8 in the container `ScannedFiles=2155 ExaminedOccurrences=149 DocsRows=73`; certification **153 `.cs` / 156 entries** (per head: 142 #741, 144 #742, 147 #743, 149 #744, 152 #746, 153 #748). Row 64, the floors sentence and the count paragraph still quote older numbers (R3a). Cert-gate totals quoted by the PRs: 3,066 (4.7) → 3,101 (4.8) → 3,174 (4.9) → 3,240 (4.11); `EgressEnforcementLeakTests` `Passed! 19/19` on net8.0 at `ac264559f`; `mutation mode-ag-report-ignored: KILLED red=failed:2/107 green=passed:107/107`.
- `[skip-prod-style]` tokens on #722, #743, #746; #752 adds the ProdStyle twins for #743/#746; #722's debt stays (card #757 for the gate).
- Known pre-existing failures: `DogfoodBlock8Tests.ParallelTestMatrix…`, two `DogfoodBlock8ComposedTests`; `RuntimeStudioBlackBoxPlaygroundTests.Prod_cli_objectives…` flaky under load.
- Dependabot rebases its PRs onto master by itself (all ten sat on `ac264559f` with green checks on 2026-10-11); #753 supersedes nine of them.
- Host clones under `C:/Users/icfre/Documents/Codex/2026-09-12/f-2/work/`: `ashlar-record-correction` (canonical, on master), `ashlar-g-review` (read-only at `ac264559f`, the lane and agent branches fetched), `ashlar-g-conformance-4.11` and other `ashlar-g-<lens>` clones (the review's), `ashlar-g-deps` (`deps-grpc-284`), `ashlar-g-48b`, `ashlar-g-r3a`, `ashlar-g-manifests`, `ashlar-g-prs` (the stopped lanes, clean), `g-refs/` (design/review/attack documents laid out for the workflow; `g-refs/review2/` holds the lens notes and logs; `g-refs/deps-logs/`). The session scratchpad is reaped; everything that matters is on the storage branch.
- The GitHub label `agent-task` exists; cards #757–#767 carry it.

## 7. Queued work

- Phase H steps 1–8 above, in that order.
- Bus texts to post, all prepared in this thread's files: the 19 backfill comments; for each lane PR a `claim`/`handoff`/`done`; `done` on `drift-746`/`drift-748` after R3a; a `status` on `mutations-spec007` acknowledging Grok's runs and naming the repair PR.
- From the review, not yet a PR or a card: the SPEC 4.11 break-glass sentence overstatement (D6 lowering by a host guard on SW; in R3a's brief); the InternalsVisibleTo widening by #743 (15 friends) to record or trim (in 4.8b's brief as a body note); open question A de facto answered by `ParseOrPublic` (R3a).
- PR 5 proper: receiver-side `CanRead` at the server seams, inbound response mediation, §8 A/B/E; the Codex branch harvested (63 facts in 11 certification files, 10 cert-gate rows) or archived.
- Follow-ups outside PR 4 (phase B/C scouts; owner's choice): unchanged from `handoff-phase-F.md` §7.

## 8. Ways of working the owner has endorsed

- One phase per thread, ending with `/handoff`; a pause is also a handoff.
- Lanes and checks: parallel lanes, each followed by adversarial checks (two lenses), repair until clean, then integrate, verify, mutation-check, merge serially.
- Ask the owner only for real decisions; otherwise take the fail-closed default and record it as an integrator decision in the PR body, never as an owner row in the decisions log (now in `CLAUDE.md` and `PROTOCOL.md`).
- PR bodies quote real counts, say what was not observed failing, and quote each mutation's verbatim summary line.
- Claim before you build; read the newest handoff and the decisions log before you start; land the owner's decisions as rows before the code; keep the bus alive (#754).
- On this host: one container at a time, five agents at once, clones under `work/`, the main thread's checkout left alone. **Observed this phase:** the owner stopped four lanes launched at once without a message; the next thread should state the lane plan and let the owner object before launching several agents.

## 9. Starting prompt for the next phase

> Execute **phase H** of spec-007-pr4 on this machine. Run `/start-phase spec-007-pr4` (it fetches this handoff from `claude/spec-007-pr4-workspace`), read it in full starting with §0, then `REVIEW-2026-10-11.md` §1, §3, §4 and §8, and check §1 against the live repo (master `1d8bc7448` or later; the open PRs; #695; whether the three decisions-log rows have been answered). Then, in order: get the bus backfill posted (the attached comments, or ask me for the allow rule); put Q-A, Q-B and Q-C to me and land Q-A as the R3b records PR; run the R3a, PR 4.8b and manifest-hygiene lanes from `lane-briefs-phase-G.md`, one claim each on the bus, telling me the lane plan before launching more than one agent; land #752 and #753 on green; then PR 5 per my Q-B answer. Build and test only through `scripts/test-in-container.sh`, one container at a time; never run `dotnet` on the host. If anything the owner has not decided blocks you, ask instead of choosing. When phase H is done, run `/handoff spec-007-pr4 H` and notify me.
