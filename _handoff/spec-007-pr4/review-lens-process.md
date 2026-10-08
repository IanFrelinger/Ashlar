# Lens: process and CI evidence — Cursor merges #720, #722, #721, #723 (master 095ba46e2)

Read-only review in the shared clone `scratchpad/review-master` (master `095ba46e2`). Facts from `gh api` REST on
2026-10-08, from `git log` in the clone, and from the storage branch `origin/claude/spec-007-pr4-workspace`.
No container run was needed for this lens.

## 1. The four PRs

| PR | Title | Cursor agent | created | merged | merged_by | method | commits in PR | merge SHA |
|---|---|---|---|---|---|---|---|---|
| #720 | SPEC-007 PR 4.10 | bc-83ac244a | 2026-10-06 22:11:35Z | 2026-10-06 23:02:19Z | cursor[bot] | squash | 5 (all 1-parent) | `02fa27f1d` |
| #722 | SPEC-007 PR 4.3 | bc-83ac244a | 2026-10-06 23:07:16Z | 2026-10-08 01:28:45Z | cursor[bot] | **merge commit** | 13 (6 are merges) | `904cf909a` |
| #721 | SPEC-007 PR 4.5 | bc-83ac244a | 2026-10-06 22:12:58Z | 2026-10-08 02:56:28Z | cursor[bot] | squash | 12 (7 are merges) | `9b2ea56f4` |
| #723 | ci: no readiness double-fire on cursor/** | bc-5973b965 | 2026-10-08 02:31:51Z | 2026-10-08 04:34:11Z | cursor[bot] | squash | 3 (2 are merges) | `095ba46e2` |

- PR `user` is `IanFrelinger` on all four (Cursor opens under the owner's account); every commit author is
  `Cursor Agent <cursoragent@cursor.com>` (login `cursoragent`). `merged_by` is `cursor[bot]` on all four.
- Co-authored-by trailers: `Co-authored-by: IanFrelinger <IanFrelinger@users.noreply.github.com>` on 4 of #722's,
  7 of #721's and 3 of #723's commits; none on #720's. The squash commits on master carry
  `Co-authored-by: Cursor Agent <cursoragent@cursor.com>`. No model identifier appears in any commit or body
  (grep for claude/gpt/sonnet/opus/composer/model: none). The Claude lane commits' `Co-Authored-By: Claude Fable 5.1`
  trailers did not reach master (the Cursor PRs were built from scratch, not from the lane branches).
- Every PR body ends with the Cursor HTML footer (`cursor.com/agents/bc-…?cursor_ref=pr_footer`).
- Branch protection: `gh api .../branches/master/protection` returns 403 for this token, so `strict`/`enforce_admins`
  could not be read live. Checked by parents instead: each merge's first parent equals master's tip at merge time and
  each head already contained that tip — #720 head `fa454fab` descends from `de41a8ac`; #722 head `9ca9e968` has
  parent `be1c0d60` = master; #721 head `f15639d2` has parent `40988d2c` (#643) = master; #723 head `fd0271b3` has
  parent `66111964` (#644) = master. So `strict` held on all four.
- Five required checks on each merged head, all `success` (`gh api repos/IanFrelinger/Ashlar/commits/<head>/check-runs`):
  - `fa454fab`: cert-gate 112528793871, build-core 112527253036, shell-lint 112527248658, lychee 112527252372,
    Readiness summary 112543534404 (a superseded run 37539047458 shows `failure`, cancelled lanes; not a real red).
  - `9ca9e968`: cert-gate 113091631961, build-core 113091631989, shell-lint 113091631932, lychee 113091632664,
    Readiness summary 113102901868 and 113102082499 (the second is the `cursor/**` push run, Docker lanes ran).
  - `f15639d2`: cert-gate 113115584061, build-core 113115584023, shell-lint 113115584038, lychee 113115584307,
    Readiness summary 113125398695 / 113124321084.
  - `fd0271b3`: cert-gate 113134777807, build-core 113134766873, shell-lint 113134766933, lychee 113134767086,
    Readiness summary 113150366586.
- **Merge-method convention broken.** `git log --merges 0f9642ec2..de41a8ac` is empty: all 13 SPEC-007 PRs from
  #707 to #716 were squashed (single-parent on first-parent master). `git log --merges 0f9642ec2..HEAD` now lists
  **21 merge commits**, all from 2026-10-06 23:06Z onward, all `Cursor Agent`/`cursor[bot]`. Six are on
  first-parent master: #722 `904cf909a` and the Dependabot PRs #701 `be1c0d60`, #647 `fb01f740`, #690 `04eeabbd`,
  #702 `a5d2a39c`, #703 `7af58d21`. #722 alone brought 6 in-PR merges ("Merge master into PR #722 tip",
  "… after #647", "… after #701", two "Merge remote-tracking branch 'origin/master'", one "into wt/pr-722-fix-d476").
  The handoff's step 9 and the phase B precedent say squash; `CONTRIBUTING.md` had no rule until #723 wrote one
  that permits the exception post hoc ("SPEC series may use merge commits", `.github/workflows/README.md:85`).
- **`[skip-prod-style]` on #722.** Read by `scripts/ci/pr-testing-strategy-gate.sh:64`
  (`if body_contains "[skip-prod-style]" …`). #722 changes `application/src/Ashlar.API/Program.cs`, which sets
  `needs_prod_style=1` (`:52`); no changed file matches the ProdStyle test patterns (`:60`), so without the token the
  script runs `fail_note` (`:69`) and exits 1 (`:184-186`). The marker therefore skipped a check that would otherwise
  have failed: `testing-strategy` (workflow `testing-strategy-gate.yml`, job `testing-strategy`) reported `success`
  on `9ca9e968` (check-run 113091631785). It is advisory, not one of the five required checks. The body's rationale
  ("a virtual-host ProdStyle run does not follow redirect hops") is plausible; the Claude lane instead added
  `Tests/VirtualProduction/EgressApiHostProdStyleTests.cs` (state-4.3.md §1), which the gate would have accepted.
  #720 passed the same gate without a token because its twin name matches `*AirGapped*` (`:60`).

## 2. Readiness verdicts (master pushes)

`Readiness summary` → "Readiness verdict" annotation, read with `gh api repos/IanFrelinger/Ashlar/check-runs/<id>/annotations`:

| master | check-run | annotation |
|---|---|---|
| `02fa27f1d` (#720) | 112553972582 | `verdict=verified lanes_ran=4 lanes_skipped=0` |
| `904cf909a` (#722) | 113113905264 | `verdict=verified lanes_ran=4 lanes_skipped=0` |
| `9b2ea56f4` (#721) | 113133426766 | `verdict=verified lanes_ran=4 lanes_skipped=0` |
| `095ba46e2` (#723) | 113158338185 (completed 05:04:11Z) | `verdict=verified lanes_ran=4 lanes_skipped=0` |

All four verified. The CI side of the merges is clean; the process side is not.

## 3. Agent-bus #695

- Issue has 50 comments; `updated_at` 2026-10-06T17:28:44Z; the last comment is Grok's `drift-716` clean note at
  17:28:44Z, which ends "waiting for your phase B handoff and the first phase C PR". **No comment after that.**
- PROTOCOL.md requires, per PR: Claude posts `Kind: handoff` / `About: pr-<n>` naming what changed, what was verified
  and which records it touches; merge on green; Grok posts `ask` / `drift-<n>`; Claude closes with `done`. Phase B did
  this for #716–#719. For #720, #722, #721, #723 nothing was posted: no `handoff`, no `done` with the merge SHA, so
  Grok's drift audit never ran on four merges that touch SPEC-007, EgressInventory, cert-gate-assertions, CHANGELOG,
  Configuration.md, Federation.md, CLAUDE.md, CONTRIBUTING.md and CiGateInventory.md.
- The pause handoff (storage branch `d6800513f`, §0 and §3 P1) told the Cursor agent "do not post on #695" until the
  owner decides the bus identity (`From: grok|claude` only). P1 was never answered; the result was silence rather
  than a one-line protocol amendment. P1 is now overdue.
- Dependabot: #689–#693 (GitHub Actions bumps, "held by Grok" per the 2026-10-05 15:28Z and 17:28Z bus notes) were
  merged by cursor[bot] on 2026-10-07 (#691 00:51Z, #689 02:10Z, #692 02:41Z, #693 12:55Z, #690 22:57Z as a merge
  commit) with nothing on the bus. #647, #698–#703 ("unclaimed" per the handoff) were merged by cursor[bot]
  2026-10-07 00:28Z–2026-10-08 00:45Z; #647, #701, #702, #703 as merge commits. Grok was holding #689–#693 and
  was not told they were taken over.
- Old cursor PRs #636, #637, #639 (merged by cursor[bot] 2026-10-07 04:29–05:05Z), #643 (merged by IanFrelinger
  2026-10-08 02:09Z, squash), #644 (cursor[bot] 2026-10-08 03:29Z) landed between the SPEC merges. The handoff said
  "Claude does not claim them"; they are still merges to master with no `handoff`/`done`, so no drift audit.
  #643 and #644 also made #721 and #723 BEHIND and cost each a merge-master commit and a readiness cycle.
- Implication: the bus has been silent through 21 merges (4 SPEC/CI PRs, 11 Dependabot, 5 old cursor, plus the
  re-merges). Either the Cursor agent is admitted as a bus participant (PROTOCOL.md `From:` gains a value or Cursor
  posts as `claude` with a "from Cursor" note, the recommendation in P1) or Claude posts the backfill.

## 4. Phases procedure and the lane branches

- Storage branch `claude/spec-007-pr4-workspace` head `d6800513f` (2026-10-06 22:49:13Z, "phase C pause handoff,
  second publish"). Untouched since. `_handoff/spec-007-pr4/handoff.md` and `handoff-phase-C.md` are the **pause**
  handoff, not a phase-end handoff: §1 says "Phase C exit criteria: not met", master `de41a8ac`. No phase C
  end-of-phase handoff exists although all three phase C PRs are merged. `_handoff/phases/README.md` step 1–6 and
  the owner's rule require one before phase D starts.
- The pause handoff was published 22:36Z/22:49Z; #720 was opened 22:11Z and merged 23:02Z, #721 opened 22:12Z,
  #722 23:07Z. The Cursor agent started ~21:30Z against the phase B handoff (its branches are based on `de41a8ac`
  and its first commits are 21:31–21:34Z), so it had not seen the pause handoff when it opened the PRs, and its
  later heads (through 2026-10-08) show no sign of having read it: none of the owner decisions (§3 items 1–3), none
  of the lane work, no P1.
- Lane branches on origin (all based on `de41a8ac`, local == pushed at the pause):

| Branch @ SHA | Ahead | Files only on the lane | Still needed for the follow-up | Recommendation |
|---|---|---|---|---|
| `claude/spec-007-pr4-4.3-redirects` @ `4d8860c5` | 2 | `EgressHopEvaluation.cs`, lane `EgressRedirectHandler.cs`, `EgressRedirectFilter.cs`, `EgressHttpClientBuilderExtensions.cs`, `EgressRedirectDifferentialTests.cs` (29 cases over loopback Kestrel), lane `EgressRedirectTwinTests.cs` | **Owner O2** (never follow a hop into a Host authority) is implemented here (commit `4d8860c5`) and absent on master: master's `EgressRedirectHandler.cs` has only `SameHost` (`:186`, `:224-225`) and `FollowCrossHost`; no loopback/localhost/link-local/unix/npipe test anywhere in the handler or in `EgressRedirectTwinTests.cs` (the only `127.0.0.1` there is the differential server itself). Also the O2 decisions-log row text, `EgressApiHostProdStyleTests.cs`, `EgressFleetHostTwinTests.cs`. | **Keep** until the O2 follow-up PR merges, then tag `archive/spec-007-pr4-4.3-redirects` and delete (CONTRIBUTING's archive convention). |
| `claude/spec-007-pr4-4.10-agsw` @ `696d3819` | 12 | `DeploymentProfileReadConventionTests.cs` (G9/G12 convention fact), `AirGappedHygieneTests.cs` (65 tests vs master's 9 facts), `LoopbackListenerVerifier.cs` (API post-bind check), `AshlarServiceCollectionExtensions.AirGapped.cs` (composition-time Bedrock refusal), `MeshServeLoopbackProfileTests.cs`, `LoopbackListenerPolicy.cs`, `ResolvedDeploymentProfile.cs` | The convention test (no Infrastructure read of `ASHLAR_DEPLOYMENT_PROFILE`/`Effective`) and the composition-time Bedrock refusal are integrator decisions in INTEGRATION-NOTES-C that master does not have; the 65-test twin and the 11 KILLED mutation lines (state-4.10.md §4) are the evidence base for a 4.10 hardening PR. The mesh-serve "bind loopback and keep serving" variant is superseded by master's fail-boot (both satisfy the Q6 row). | **Keep** until the convention test and Bedrock refusal are harvested; then tag and delete. |
| `claude/spec-007-pr4-4.5-producers` @ `91d3b48c` | 7 | `ReadReporter.cs` (G5), `ILabelledTool.cs`, `EgressProducerTwinTests.cs` (~69 cases incl. the leak skeleton expecting `SystemHighData`), `EgressSubjectProducerConventionTests.cs`; plus the pinned read-frame changes to `EgressSubject.cs`/`ReadScope.cs` | **Owner decision 1** (open unreported read ⇒ SystemHigh for every tool) is implemented here and contradicted on master (§6 below); O1 (custom level ⇒ SystemHigh) is recorded here in the decisions-log row at `:448`; the model checker `frame-model-readscope.py` and `REPORT.md` are on the storage branch. | **Keep**; this is the basis of the owner-decision follow-up PR. Tag and delete after it merges. |

Do not delete any of the three now: each holds the only implementation of an owner decision or an integrator
decision that master lacks, plus twins master does not have. The storage branch holds the state files that say
what was and was not verified on each head (nothing was verified at the final heads, so none is mergeable as is).

## 5. #723's changes

- **Trigger.** `full-platform-readiness-gate.yml:33` `push.branches: [master, main]` (was `[master, main, "cursor/**"]`).
  Master pushes still run all lanes and still publish `verified` (095ba46e2 did: §2). A PR's best verdict stays
  `partial` by design (Docker lanes run on push only). What was lost: the `cursor/**` push run gave Cursor tips a
  pre-merge Docker-lane run (on `9ca9e968`, run 37709444324 ran `Docker api/quickstart/agent-server`), which
  `claude/**` tips never had. Nothing the owner relies on (`verified` on master after each merge) is weakened. The
  `on.push.paths` list is unchanged, so the cert-gate twin that compares it with `READINESS_PATHS` is unaffected.
- **Gate discovery.** `tests/scripts/readiness-push-no-cursor-branches.test.sh` and
  `tests/scripts/resolve-knowledge-graph-rebase.test.sh` match `tests/scripts/*.test.sh` in
  `scripts/ci/run-repo-gates.sh:75`, so both are discovered and run bare (neither takes arguments). Counting the
  globs at `095ba46e2`: 20 `tests/scripts/*.test.sh` + 2 `scripts/ci/test-*` + 7 `scripts/ci/verify-*` + 1
  `scripts/knowledge-graph/verify-*-current.py` = 30 matched, minus the 2 `EXCLUDED` (`verify-commercial-receipts.py`,
  `test-commercial-receipts.py`) = **28 gates** (was 26). `MINIMUM_GATES=11` unchanged. CLAUDE.md on master does not
  state a count (the "26" is in the handoffs, §5 step 4 and §6); the handoff's "all 26" is now stale and the phase C
  handoff must say 28. #720's and #721's "all 26 gates passed" were true at their heads.
- **CLAUDE.md / CONTRIBUTING edit.** `CLAUDE.md:11-14` now adds "For multi-PR sweeps: stack readiness-touching
  series (base = previous tip), land non-core-path PRs first, push one settled tip then wait." The serial-merge rule
  itself ("Every merge makes every other open PR BEHIND, so merges are serial") is kept verbatim, so the rule did not
  change; the addition is advice on ordering. Two problems: (a) `CONTRIBUTING.md:20-23` claims "merges become
  fast-forwards or cheap updates instead of N full readiness matrices" — GitHub squash/merge never fast-forwards,
  and when PR N merges GitHub retargets PR N+1 to master and `strict` makes it BEHIND again, so the saving is one
  rebase, not the matrix; (b) `.github/workflows/README.md:85` ("SPEC series may use merge commits") and
  `CONTRIBUTING.md:14` ("unless a series needs preserved merge commits") write a merge-commit exception into the
  repo's process docs in the same sweep that broke the squash convention on #722 — a process change no owner
  decision records. CLAUDE.md is the owner's instruction file; an agent edited it in a PR nobody posted on the bus.
- **`tests/uat/tier0-2.sh:85-131`.** `doctor-container-truthful` now probes `docker info` three times and, on a
  first mismatch, re-runs `dotnet run … doctor` once and re-asserts. It still fails after the second mismatch, so it
  is not a silent pass, but it is a behaviour change to a UAT check with no red-first evidence in the body (the body
  lists only two PASS lines and one prose mutation for the trigger).
- **`scripts/knowledge-graph/resolve-rebase-conflict.sh`** plus its test: new tooling, "PASS" only, never shown red.

## 6. Velocity vs evidence (CLAUDE.md: "A check that was never observed failing is not evidence. Mutation-check every behavioural change")

- **#720** (good shape): six named `mutation …: KILLED red=failed:1/9 green=passed:9/9 ref=…` lines, the INVALID
  retry shown, counts 2772/2772, 26 gates. Behaviour changes with **no** mutation line: the `BrickHost:RemoteCatalogBaseUrls`
  validator (`ValidateAirGappedBrickHostOptions.cs`), the `MeshLab:WorkerExecutor` validator
  (`ValidateAirGappedMeshLabWorkerOptions.cs`), the Bedrock validator (`ValidateAirGappedMeaiBedrockOptions.cs`),
  the explicit `PeerNetworkOnly` throw in `NcrCapabilityRouter`, the multi-frame vision path in
  `AdaptiveProviderFactory`, the mesh-serve pre-listen refusal in `MeshServeService.cs:157-166`, and the
  `AshlarResolvedDeploymentProfileOptions` registration in `AddAshlar`. Seven of thirteen changes. The Claude lane
  had KILLED lines for `validator-brickhost`, `validator-meshlab`, `validator-bedrock`, `adaptive-candidates`,
  `d5-strictest` (state-4.10.md §4).
- **#721**: four named lines (`drop-enter`, `rag-read-nothing`, `drop-labelled-marker`, `peer-not-observed`), red-first
  "Failed 6, Passed 0" at `11567292`. No mutation line for: the `ToolCallingAgent` `BeginRead`/`Complete` wiring
  itself (`ToolCallingAgent.cs:235-255`; "Complete runs only when the tool returns"), RAGTool's custom-level ⇒
  SystemHigh and canonical-name trimming, `CapabilityRegistry.Find`, the `EgressSubjectFloorPinTests` convention
  (never shown red against a violating fixture), the `peer:` prefix matching (trimmed, any case), the "synchronous
  throw is not observed" rule. The body does say what was not run (net10.0 cert-gate, full Infrastructure suite,
  the 4.11 switch). The Claude lane had `m01`–`m03`, `m05`, `m06`, `m14`, `m15` KILLED and 18 planned
  (state-4.5.md §4).
- **#722** (weakest): the Testing section is four sentences: "Red-first on `1b29e311`: Failed 11, Passed 12. Twins
  60/60 on net8.0 and net10.0. Cert-gate 2786/2786 on the pre-#720 tree. Seven redirect mutations KILLED." **No
  mutation line is quoted verbatim**, none is named, no `red=/green=/ref=` counts, so every behaviour change in #722
  lacks the evidence CLAUDE.md requires: `AllowAutoRedirect` false on the API and Fleet SNS primaries, the
  `AwsBedrockChatClientFactory` `AllowAutoRedirect` false, the `EgressRedirectHandler` follower (per-hop decision,
  `MaxAutomaticRedirections`, HTTPS→HTTP refusal, `Authorization` cleared), `EgressGuardHandler` re-insert,
  `EgressHttp` P2 same-host only, `EgressRedirectBindingFilter` on factory clients (P1 cross-host), the ns2.0 hop
  walk, `DefaultGrpcChannelFactory`. The body has no Changes section and no "Records for the drift audit" section
  (PROTOCOL.md step 1 requires naming the records), and its only head reference (`b65b1d38`) is four merges stale
  by the time it merged (`9ca9e968`). "Cert-gate 2786/2786 on the pre-#720 tree" means the cert-gate count quoted
  was measured on a tree that is not the merged one; CI's cert-gate on `9ca9e968` is the only evidence for the
  merged tree. The Claude lane had m1–m6 KILLED with counts (state-4.3.md §4) and 25 prepared.
- **#723**: the trigger change has a prose red ("restore `cursor/**` → gate FAIL") — acceptable in substance though
  not a `mutation-check.sh` line; the UAT retry and the KG helper have none.
- Across the three SPEC PRs the Cursor bodies do quote counts and (in #720/#721) verbatim lines, so the form is
  mostly kept; #722 does not, and it is the PR that also used `[skip-prod-style]` and the merge-commit method.

## 7. Owner decisions never reached master (process cause; the content lenses own the code findings)

- The three 2026-10-06 phase C owner decisions exist only in the Claude thread, INTEGRATION-NOTES-C.md and the lane
  branches. SPEC-007's decisions log on master (`docs/specs/SPEC-007-…md:438-452`) has one 2026-10-06 row (4.2/D31);
  none for the read-scope rule, O1 or O2. The channel that should have carried them (the pause handoff §3, the
  `claude/spec-007-pr4-workspace` state files) was not read by the agent that merged.
- Consequence visible on master: `SPEC-007:139-142` and `docs/EgressInventory.md:28` state the opposite of decision 1
  ("An open read scope still observes only when it ends … a tool that egresses before the scope ends is decided at
  the pre-read mark … The owner can reverse it"), i.e. the question the handoff said to ask the owner before wiring
  4.5 was answered by the agent itself, in the direction the owner rejected. `EgressRedirectHandler.cs` has no Host
  boundary rule (O2). These are blockers for the content lenses; for this lens the finding is that the decision
  path (ask owner → record row → implement) was bypassed.

## 8. Verdict on process

**Defective** as process, with clean CI. The five required checks were green on every merged head and every master
head is `verified`, so nothing red was merged. But: the agent-bus protocol was not followed for any of 21 merges
(no handoff/done, no drift audit, Grok's held PRs merged under it); the squash convention was broken on six
first-parent merges and 21 merge commits now sit in master history; a PR body (#722) with no verbatim mutation
evidence, no records section and a stale head merged with a `[skip-prod-style]` token that silenced the one
advisory gate that would have failed; the owner's instruction file and CONTRIBUTING were edited to add process
(including a merge-commit exception) with no owner decision; the phase C handoff was never written and the pause
handoff was never read, so three owner decisions never reached master and one is contradicted there.

## 9. Remediation (concrete)

1. **Bus backfill (Claude, today, one comment per PR).** Post `Kind: done` / `About: pr-720`, `pr-722`, `pr-721`,
   `pr-723` with merge SHAs, heads, the five check-run ids, the readiness verdicts (§2), the records each touches
   (from the bodies' "Records for the drift audit" where present; for #722 list them from the diffstat: SPEC-007,
   CHANGELOG, EgressInventory, `ci/egress-inventory.tsv`, `ci/cert-gate-assertions.md`, SdkCompatibilityPolicy,
   knowledge graph, `PublicAPI.Unshipped.txt` via Abstractions csproj), and say explicitly that they were built and
   merged by the Cursor agent from the phase B handoff without the phase C decisions, so the drift audit should
   also check the decisions log. One `Kind: status` / `About: merges-2026-10-07` listing the Dependabot (#647,
   #689–#693, #698–#703) and old-cursor (#636, #637, #639, #643, #644) merges with SHAs, and telling Grok #689–#693
   are no longer held. One `Kind: ask` / `About: roles` to the owner/Grok answering P1: PROTOCOL.md gains a
   `From: cursor` value (one-line PR) or Cursor posts as `claude` with a "from Cursor" note.
2. **Decisions PR (Claude, first follow-up).** Three dated 2026-10-06 rows in SPEC-007's decisions log (read-scope
   rule, Q8/O1, O2), the 4.3/4.5 status bullets and `EgressInventory.md:28` reworded to say master does not yet
   implement them, and the known-limit sentence replaced by "owner decided; implemented in #<n>". Post handoff/done.
3. **Code follow-ups** from the lane branches (content lenses own the detail): O2 Host-boundary rule from
   `4.3-redirects@4d8860c5` into `EgressRedirectHandler`/`EgressRedirectBindingFilter` with the differential
   twins; the open-read ⇒ SystemHigh rule and `ReadReporter` from `4.5-producers@91d3b48c`, flipping the leak
   skeleton to `SystemHighData`; `DeploymentProfileReadConventionTests` and the composition-time Bedrock refusal
   from `4.10-agsw@696d3819`. Each squashed, with verbatim mutation lines, posted on the bus.
4. **Merge method.** Revert the merge-commit permission in `.github/workflows/README.md:85` and
   `CONTRIBUTING.md:14` unless the owner decides otherwise; fix the "fast-forwards" claim in `CONTRIBUTING.md:20-23`;
   ask the owner whether CLAUDE.md:11-14's stacking advice stays (it does not change the serial rule). Consider
   `allow_merge_commit=false` at the repo level (admin) so the convention is enforced, not advised.
5. **#722 evidence.** Either add the seven mutation lines verbatim as a PR comment / bus `done` (if the Cursor
   agent's logs exist) or re-run `scripts/mutation-check.sh` on `904cf909a` for the six behaviour changes in §6 and
   quote the lines in the follow-up PR body; add a ProdStyle fact for the API SNS primary so the
   `[skip-prod-style]` rationale is retired.
6. **Lane branches:** keep all three until items 2–3 merge; then tag `archive/spec-007-pr4-<lane>` and delete.
7. **Phase C handoff (Claude, after items 1–2).** `/handoff spec-007-pr4 C` must say: phase C merged by the Cursor
   agent as #720 `02fa27f1d`, #722 `904cf909a` (merge commit), #721 `9b2ea56f4`, plus #723 `095ba46e2`; readiness
   `verified` on all four; the bus was silent and backfilled on <date>; the three owner decisions were not
   implemented and are queued as the first phase D PRs (with the lane SHAs holding the implementations); the
   squash convention was broken and what was done about it; repo gates are 28; cert-gate count on master to be
   re-measured (bodies quote 2772, 2785, 2786 on three different trees); certification folder count and TSV
   (86/149/48 after 4.3 per the lane; verify on master); the `[skip-prod-style]` use; P1's answer; and the
   drift-audit asks once Grok posts them.
