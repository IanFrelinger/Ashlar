# release-0-3-0 handoff: end of phase A, start of phase B

*Written 2026-10-14 ~07:25 UTC at the end of phase A, on the owner's machine (Windows host, Docker Desktop, Git Bash). This is the workstream's first handoff. Start the next thread with `/start-phase release-0-3-0`, or with the prompt in §9. Storage branch `claude/release-0-3-0-workspace`, folder `_handoff/release-0-3-0/` (`bash scripts/handoff-fetch.sh --workstream release-0-3-0 --list`).*

This workstream takes Ashlar from v0.2.0 to the **v0.3.0** tag. The owner chose 0.3.0 on 2026-10-13. It is the first tag that carries the AirGapped and SecureWorkstation enforce switch (#746). The workstream continues two earlier ones:
- **spec-007-pr4:** its phase H items moved here. Its last handoff is `handoff-phase-G.md` on `claude/spec-007-pr4-workspace`.
- **budgeted-autonomy:** closed on 2026-10-13. Its handoff is `0c90e2bef` on `claude/budgeted-autonomy-workspace`.

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`):
- end a phase with `/handoff release-0-3-0 <phase>`;
- start one with `/start-phase release-0-3-0`.

The handoff is published to the storage branch, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff.

**Running locally on this host:**
- **`dotnet` runs only in the devtest container,** through `scripts/test-in-container.sh`, against a clone, never a worktree. Never run it on the host.
- **Capacity:** Docker's VM is capped at 5 GB, so only one container runs at a time, machine-wide, and at most five agents.
  - The lanes take a mkdir lock at `work/g-refs/release-A/docker.lock`, with an owner file and a stale rule. `LANE-RULES.md` is attached.
  - Before a run, check `docker ps -q --filter ancestor=ashlar-devtest:local`.
- **Python:** `python` is the real interpreter; `python3` is the Store stub. A POSIX `python3` shim exists, but only in the session scratchpad, so it is not durable.
- **Line endings:** `core.autocrlf=true`. Markdown and most text are CRLF on disk, while `.sh`, `.py` and `.claude/workflows/*.js` are LF (`.gitattributes`, #794).
  - The KG rebuild leaves line-ending-only diffs; restore them with `git checkout --`.
  - Git Bash `grep` cannot see a CR before a line end. Use `tr -cd '\r' | wc -c`.
  - Use `MSYS_NO_PATHCONV=1` for `git show <ref>:<path>`.
- **Repo gates on the host:** `bash scripts/ci/run-repo-gates.sh` reports **30 of 32** on any branch. The two failures are host artefacts: `tests/scripts/mutation-check.test.sh` (its symlink fixture) and `tests/scripts/session-start-hook.test.sh` (73 passed, 50 failed). CI is the check that counts.
- **Bus posts:** `gh issue comment` was allowed every time it was used this phase. Earlier phases saw the auto-mode classifier refuse it. If it refuses, write the post as a file and hand it to the owner.

## 1. Where things stand

- **Master:** `aa07e6d91` (#804). The project audit ran on `03b5a569e` (#801); the only change after it is #804's one-line annotation.
- **Merged this phase** (five required checks green on every head; no Grok `block`; each PR adversarially reviewed until ship):

| PR | What | Merge |
|---|---|---|
| #795 | The owner's release decisions of 2026-10-13: 0.3.0, and the new public API ships `[Experimental]`. Landed just before phase A as its premise. | `60689412e` |
| #796 | **R3b.** The owner's answers to the 2026-10-11 review: Q-A strikes the D16, §8 Q3 and §8 Q4 rows; Q-C extends the Q6 loopback rule to the gRPC server host and Fleet.Host; `[Experimental]` lines stay in Unshipped. | `f96ee0720` |
| #799 | **R3a.** Records drift after 4.7–4.11 and the PR 5 slice. It answers Grok's `drift-746` and `drift-748`. | `bb195b665` |
| #798 | **compat-gate,** green on master for the first time since 2026-09-12 (push run 38503821959, all 7 legs, including a new hybrid-root agentic leg). Fixes #797. | `b297c98e0` |
| #800 | **SPEC-007 PR 4.8b,** the refusal-masking repair: BL4, BL5 and M2. A refused probe is held on the caller's `ProviderRefusalScope`, five catches rethrow a refusal, D23 holds per step in `BehaviorExecutor`, and the Warning is windowed (7307). | `1f45a9e5b` |
| #801 | **ASHLAREXP002.** The 23 types behind the 160 lines of `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt` carry `[Experimental("ASHLAREXP002")]`, with 16 per-project opt-ins plus the test opt-in. `[coordinated-integration]`. | `03b5a569e` |
| #804 | Records: the 2026-10-05 Q5 decisions-log row gains "Implemented in: #746 (`7cb132897`)". | `aa07e6d91` |

- **Readiness:**
  - VERIFIED: `bb195b665` and `1f45a9e5b`.
  - No readiness run (docs-only paths do not trigger one): `60689412e`, `f96ee0720` and `b297c98e0`.
  - `03b5a569e` (#801) read VERIFIED at the audit's synthesis. `aa07e6d91` (#804) is docs-only and gets no readiness run, so **the release commit will need its own VERIFIED run** (runbook step 10).
  - Read any SHA with `bash scripts/release/readiness-verdict-for-sha.sh <full sha>`.
- **Agent-bus (#695):**
  - Claude posted a `claim`, a `handoff` and a `done` for every PR above. The last are 6161517879 (`done` #800), 6162040031 (`done` #801) and 6162051539 (`claim` records-q5), 6162186318 (`done` #804).
  - Grok's last post is 6156480316 (2026-10-13 22:22Z, ack of drift-793/794/795). **Grok has not yet posted a drift audit of #796–#804.** Expect `drift-<n>` asks.
  - Nothing is addressed to Claude and open. No `block`.
- **This phase's exit criteria:** the owner's request was "the two questions above, the SPEC-007 drift fixes Grok asked for, the BL4/BL5 refusal-masking fix, compat-gate, and applying ASHLAREXP002".
  - Q-A and Q-C answered and landed: **met** (#796).
  - Grok's drift-746 and drift-748: **met** (#799).
  - BL4 and BL5: **met** (#800).
  - compat-gate green: **met** (#798, and green on master).
  - ASHLAREXP002 applied: **met** (#801).

### Project status at the end of phase A

The governance core is built and green on master. Phase A of release-0-3-0 is merged (#796, #799, #798, #800, #801, #804), and 03b5a569e now reads VERIFIED. The v0.3.0 tag is partway off: the owner-required gRPC and Fleet.Host loopback wiring (#766) is not built, the step-9 ProdStyle evidence needs #752, and no release commit, rc1 or release notes exist yet. The wider roadmap and the autonomy unlock are far off: 6 of 41 roadmap exit criteria and 1 of 5 unlock criteria are met, and the unlanded dogfood evidence starts expiring on 2026-10-28.

Validation on 03b5a569e: 2 pass, 6 warn, 0 fail, 0 error.
- Required checks, 2 of 5 green: cert-gate passed later (run 38520088531). Readiness summary was queued, not absent.
- Red master workflows, 6: all dormant. mesh-lab gate and stress died on a script typo fixed in 0c894e20f and were never re-run.
- Readiness verdict, in progress: 03b5a569e now reads VERIFIED. aa07e6d91 (#804, docs-only) gets no readiness run.
- Open PRs, 15: now 12, and none can be merged. #750-#753 are unclaimed. The 8 Dependabot PRs are superseded by #753, and their rebases starve runners.
- Security settings: secret scanning, push protection, Dependabot updates and private vulnerability reporting are off. SECURITY.md:11 sends reporters to that last, disabled channel. There is no CodeQL.
- Branches on no remote, 65: 63 are truly unlanded, including a fix for a `bg proposals apply` path escape that has no issue.

| Area | Distance | Estimate (basis) | Since last phase | Blocks the rest |
|---|---|---|---|---|
| Product roadmap | far | 6 of 41 exit criteria met, 10 partial (checked against code) | First audit | Owner calls on Forge, public ledger, SKUs |
| Next release 0.3.0 | partway | 5 of 23 prerequisites done, 2 partial; 0 of 10 runbook steps on a release commit | First audit | #766, #752 |
| Security specs | partway | SPEC-007: 4 of 8 PRs, PR 5 library-only; SPEC-006 S-4 overdue | First audit | Owner answers gate SPEC-007 PRs 5-6 |
| Autonomy unlock | far | 1 of 5 criteria; 3a has 0 of 7 landed rows (landed rows only) | First audit | Owner: record-commit call, 3b run |
| Bug backlog | partway | 11 of 34 fixed but still open, 6 partial, 17 not fixed (checked against code) | First audit | 9 open security defects, no exception rows |
| CI health | partway | About 33 of 47 workflows green and current, 8 stale, 6 red (newest master run) | First audit | Unowned PRs, stranded branches |
| release-0-3-0 | partway | Phase A 5.5 of 6 PRs; 4 of 10 steps to the tag, 1 partial | First audit | #766 unclaimed |

#### Moved since the last phase

First audit. Phase A landed six PRs and turned compat-gate green (#797 closed). #800's D28 windowing is only partial: four 7307 sites still log unwindowed, and nothing on master tracks them.

#### Critical path to the v0.3.0 tag

1. Claim and build #766. The verifier is internal to application/, so expect two PRs or one [coordinated-integration] PR.
2. Claim, rebase and land #752, so the quoted ProdStyle line exercises default enforcement.
3. Retire Dependabot #730-#737 so the release run is not starved of runners.
4. You: answer #759, the Substrate tier from #801 and the exit-77 scope (#765), or accept each as disclosed in the notes.
5. Release commit: version bumps, CONSUMING.md, the docs sweep (plus SdkCompatibilityPolicy.md:11 and SECURITY.md:5), the Unshipped review, the CHANGELOG fold, and a Known limit for the four unwindowed sites.
6. Dispatch release-nuget.yml with 0.3.0-rc1 from that commit.
7. Open the release issue, write the notes, quote the ProdStyle Passed! line, and get VERIFIED on the exact SHA.
8. You: rule on a downgrade rollback drill, sign the ReleaseSignOff row, and sign exception rows or require fixes.
9. Tag, let release.yml run, and re-pin deploy/node.yml.

#### Hard dates

- 2026-10-19: ubuntu-latest starts moving to 26.04, and the weekly readiness run fires.
- 2026-10-28: the artifacts of VERIFIED run 38516140003 and the first unlanded dogfood rows expire.
- 2026-10-29: the first 3a dogfood records expire.
- 2026-11-01: the monthly rc-gate run.
- 2026-11-10: .NET 8 end of support (external). 90 projects target net8.0.
- Already passed: S-4's dev-HMAC removal (due 2026-09-01) and the roadmap's 0.2.0 launch target (2026-10-01).

#### Not verified

- The roadmap's per-criterion scores, and 7 of the 11 fixed-but-open bugs.
- The mesh-lab-tls cause, and the air-gapped and promotion failures (their logs are gone).
- Whether the proposals-apply path escape is reachable from a remote package.

Attachments: `validation-A.md` (and `validation-A.json`), `project-status-A.json` and `project-audit-A.json` (the audit ran as the saved workflow `project-audit`, run `wf_6a4d66cf-aa1`, 9 agents, 0 errors, on master `03b5a569e` with `previous: null`, as this is the workstream's first audit). #804 merged after the audit started; it changes one decisions-log annotation.

## 2. Read these first

- **In the repo:**
  - `CLAUDE.md`
  - `docs/RELEASE_RUNBOOK.md`, steps 4–10 ("Before you tag"). Step 7 excludes `[Experimental]` lines from promotion. Step 9 requires the release notes to state the AG/SW default and to quote the ProdStyle line. Step 10 requires the readiness verdict on the exact SHA.
  - `docs/SdkCompatibilityPolicy.md`: the tiers, "Release step", and the new `#ashlarexp002` section.
  - `CHANGELOG.md` `[Unreleased]`: the ASHLAREXP002 section first, then 4.8b, then compat-gate.
  - SPEC-007 (`docs/specs/SPEC-007-security-labels-and-reference-monitor.md`):
    - the status table;
    - the decisions log, whose 2026-10-13 rows are the owner's;
    - "Design as merged";
    - §8 and the open questions since §8.
  - `docs/EgressInventory.md`, with the dated Known limits of 2026-10-13.
  - `ci/cert-gate-assertions.md`, the count paragraph: 155 `.cs` files and 158 entries at `03b5a569e`.
- **On the storage branch** `claude/release-0-3-0-workspace`, `_handoff/release-0-3-0/`:
  - `handoff.md` (this file, the same as `handoff-phase-A.md`);
  - `validation-A.md`, `validation-A.json`, `project-status.json`, `project-status-A.json` and `project-audit-A.json`;
  - `LANE-RULES.md`, the binding rules the phase A lanes ran under;
  - `phase-A-bus-and-lanes.tar.gz`: this phase's bus posts (files 41–52), the R3b, records-q5 and release-decision PR bodies, the texts of cards #802 and #803, the #797 correction, and each lane's final `bus-handoff.md`.
- **On `claude/spec-007-pr4-workspace`:**
  - `REVIEW-2026-10-11.md`: §4 the blockers (BL1–BL5 are now closed), §5 the majors, §8 the plan;
  - `lane-briefs-phase-G.md`, which holds the manifest-hygiene brief, still unbuilt.

## 3. Decisions

**Owner decisions this workstream relies on**, all dated rows in the SPEC-007 decisions log on master:

| Date | Decision | Row's scope |
|---|---|---|
| 2026-10-13 | The next release is 0.3.0. | Next release |
| 2026-10-13 | The unshipped `Ashlar.Abstractions.Security` and `.Security.Egress` surface ships `[Experimental]` under `ASHLAREXP002`. | PR 1, PR 3a to PR 5 (§4.2) |
| 2026-10-13 | `AshlarResolvedDeploymentProfileOptions` is experimental too. | PR 4.10 |
| 2026-10-13 | The D16, §8 Q3 and §8 Q4 rows were not the owner's, and are struck. | Records (review Q-A) |
| 2026-10-13 | The loopback-or-fail-boot rule covers `Ashlar.Transport.Grpc.Server.Host` and `Ashlar.Commercial.Fleet.Host`, **wired before the 0.3.0 tag**. | PR 4 (Q6 scope; review Q-C, card #766) |
| 2026-10-13 | `[Experimental]` symbols stay in `PublicAPI.Unshipped.txt` across the tag. | Release (public API) |
| 2026-10-05 | Q1–Q8 for PR 4 (Q1 and Q5 annotated "Implemented in: #746"). | PR 4 |
| 2026-10-06 | D31, read scope, Q8 clarified, O2. | PR 4 |

**The owner also chose, in chat this phase:**
- "None were mine" for Q-A;
- "Yes, wire both" for Q-C;
- "Keep them in Unshipped" for promotion;
- "four lanes in parallel".

All but the lane plan are rows above.

**Integrator decisions of this phase** (Claude's, in the PR bodies; none is an owner row):
- **#801:**
  - SPEC-007 public members in packages other than Abstractions stay Substrate tier, and are disclosed in the CHANGELOG. That covers the 21 that name a surface type, plus a "for example" list of those that do not.
  - The id holder `SecurityExperimental` is internal.
  - The opt-ins are per project, plus one `IsTestProject` NoWarn.
- **#800:**
  - When a fallback failed after a refused attempt, D23's "the refusal is the step's last error" is met by appending the refusal again, so `ExecuteAsync`'s `Errors` can list it twice (a dated Known limit).
  - The outer catch of `ExecuteWithEventsAsync` is a Known limit, not a fix: the outcome recorder, the semantic cache and cancellation. Cancellation wins by design.
  - `ProviderFactory` becomes `IDisposable`.
  - The InternalsVisibleTo friends are recorded per friend.
- **#798:** compat-gate expects the fail-closed unconfigured run, and adds the agentic leg. Its §3.C.4 diagnostics are run but not asserted, and the gates run on ubuntu only.
- **The lane system:** clones, the docker lock, and lanes that never merge or post. The main thread reviews, merges and posts.

**Still open (owner).** None blocks phase B except where marked.
- **(b) Exit 77 scope, card #765 / PR 4.9b:** does exit 77 apply to every CLI refusal, or to export verbs only? **This decides phase B item 4.** R3a scoped the records to what the code does today.
- **Q-B, PR 5's shape:** continue from #748, take the shape of the Codex branch `codex/spec007-clearance-reads` (`91f0c0d78`, never on CI), or do a design pass first. It does not block 0.3.0: the PR 5 slice ships with its disclosed limits ("the composition check is a library API with no production caller"; "inbound response bodies are not mediated").
- **Questions from the 2026-10-11 review, none blocking:**
  - (a) is a `docker exec` into an Ashlar-started `--network=none` session Host, or a recorded limit?
  - (c) does `FailurePolicy.Continue` continue past a refused brick (card #759; #803 is related)?
  - (d) the C12 adaptation (card #763).
- **From 2026-10-08, none blocking:**
  - Q2, the merge method;
  - Q8, whether Grok's bus-posted mutation runs count as the record once `m6`, `m8` and `m20` are repaired (card #764).
- **Release sign-off.** The audit puts these in the release's critical path; ask in phase B so phase C is not held up:
  - whether a downgrade rollback drill is required before the tag (the last drill was 2026-05-22, restore-in-place only);
  - signing the 0.3.0 Product row in `ReleaseSignOff-v1.md`;
  - `docs/exceptions.yaml` rows with a sign-off for each security defect that ships open in 0.3.0, or a requirement to fix them first. The audit counts 9 open security defects.
- **Confirm the #801 integrator default:** SPEC-007 public members in Substrate packages stay Substrate tier, and are disclosed. Otherwise accept it as disclosed in the notes.
- **Who owns #750 and #751 (Cursor-built) and #752 and #753 (Grok-built), or whether to close them.** Phase B proposes to take over #752 and #753 with a `claim`.
- **.NET 8 end of support on 2026-11-10:** 90 projects target net8.0, and the ProdStyle evidence runs on net8.0.
- **Owner-only actions:**
  - the repository security settings (validation warn). Secret scanning, push protection, Dependabot security updates and private vulnerability reporting are off, although `SECURITY.md:11` sends reporters to private reporting. There is no CodeQL;
  - the dogfood-record decision, due before **2026-10-29** (budgeted-autonomy);
  - budgeted-autonomy open questions 3 and 9 (its handoff `0c90e2bef`).

## 4. Plan and live status

| Phase | Scope | Ends when |
|---|---|---|
| A | Q-A and Q-C landed (R3b); R3a and Grok's drift-746/748; PR 4.8b (BL4, BL5, M2); compat-gate green; ASHLAREXP002 applied | **done**: #796, #799, #798, #800, #801 and #804 merged |
| **B (next)** | **The 0.3.0 blockers and debts** (spelled out below) | each blocker is merged, or recorded as a dated Known limit with the owner's choice; master readiness is `verified`; Grok's drift asks for #796–#804 are answered |
| C | **The release**: the release commit (the three version files, CONSUMING trust rows, docs naming the version, PublicAPI promotion without `[Experimental]` lines, the CHANGELOG folded into `[0.3.0]`); `release-nuget.yml` `0.3.0-rc1`; readiness `verified` on the exact SHA; the ProdStyle line quoted; tag `v0.3.0` and `release.yml`; re-pin `deploy/node.yml`; post-release checks | `v0.3.0` is published to nuget.org, the post-push checks pass, and the release issue records the evidence |

**Work items of phase A:**
- Every lane branch is pushed and merged. The branches still exist on origin:
  - `docs/spec007-r3a-records-drift` @ `9bd0c721a`
  - `fix/compat-gate-fail-closed-pipeline` @ `1b94d48fd`
  - `fix/spec007-4.8b-refusal-masking` @ `81f385c93`
  - `feat/ashlarexp002-experimental-security-api` @ `eb5300f40`
  - `records/spec007-q5-implemented-in` @ `60bb646e8`

  Delete them once Grok's drift audit has read them.
- **Review:** every PR was adversarially reviewed, and its findings were fixed before the merge.
  - #800 needed four review rounds. In the third, a fix had broken D23, and it was redone so the refusal is again the step's last error.
  - #801 needed three rounds.
  - The other PRs' rounds are in their bodies and their bus `done` posts.
- **Cards filed this phase:**
  - #797, the compat-gate assertion, now closed;
  - #802, where workflow `stress` and `optimize` lose a refused provider probe and `stress` exits 0;
  - #803, where workflow agent nodes report success after a refused behaviour step.

**Phase B, spelled out, in order** (non-core-path PRs first, one settled tip at a time):
1. **Read the bus and answer Grok's drift asks** for #796–#804 in a follow-up records PR, with a `done` per slug.
2. **Card #766: wire the loopback-or-fail-boot rule** into `Ashlar.Transport.Grpc.Server.Host` and `Ashlar.Commercial.Fleet.Host` on AG/SW. The owner's 2026-10-13 row says before the tag.
   - The card body still says "Blocked on the owner"; the owner has since decided, so update it and `claim` it.
   - `LoopbackListenerVerifier` is `internal sealed` in `application/src/Ashlar.API/Security/LoopbackListenerVerifier.cs:15`, and the gRPC host references only `src/`. Either move the verifier into `src/` next to `AshlarInboundListenerPolicy` first, then wire it, or do one `[coordinated-integration]` PR.
   - Reuse `AshlarInboundListenerPolicy` / `AddAshlarInboundListenerValidation` (`src/Ashlar.Infrastructure/AshlarInboundListenerPolicy.cs`), the 4.10 mechanism.
   - Fleet.Host is not in `Ashlar.sln`, so check which build covers it.
   - Twins go red first, against real Kestrel binds as the 4.10 twins did. Mutation-check each host.
   - A non-test change under both `src/` and `commercial/` may need `[coordinated-integration]`; check the rule in the PR template.
3. **Land #752 and #753** (Grok's):
   - **#752 is a de facto tag prerequisite** (audit). On master, the only ProdStyle egress-refusal test sets `ASHLAR_EGRESS_MODE=enforce` itself (`EgressExplicitApiProdStyleTests.cs:33`), so the runbook step-9 ProdStyle quote would not evidence *default* enforcement. #752 is BEHIND, its shell-lint is red (stale KG), and its net10.0 box is unticked. Take it over with a `claim`, rebase it, run its tests in the container on net10.0 and quote the `Passed!` line.
   - **Retire the Dependabot PRs #729–#737 early.** Their rebases queued 68 runs, and master's Readiness waited more than 33 minutes for a runner.
   - The rest of this step: #753, the consolidated Dependabot bump; #753, the consolidated Dependabot bump. Then close the Dependabot PRs #729–#737 as superseded. #728 follows card #767.
   - Both PRs have been stale since 2026-10-11 and need rebasing; post a `claim` first.
   - #750 and #751 (Cursor's, DIRTY) belong to the owner's side; do not double-work them without a `claim`.
4. **PR 4.9b, card #765.**
   - Ask the owner (b) first.
   - Then put exit 77 into the central CLI refusal path, for every refusal or for export verbs only, as the answer says.
   - If the owner defers it, record a dated Known limit, so the 0.3.0 notes do not overclaim.
5. **Card #803: an agent node fails on a refusal** (`WorkflowExecutor.ExecuteAgentNodeAsync`, `StepErrorEvent` carrying the refusal). Fix it if capacity allows; it is recorded as a Known limit today. The `StepErrorEvent` change is additive public API in Substrate tier, so record it in the PR. Card #802 likewise: `stress` exits 0 after a refused probe.
6. **The manifest-hygiene PR (review M1).** The brief is in `lane-briefs-phase-G.md`:
   - repair r14, m20, m6, m8 and leak-m03;
   - add `verify-mutation-manifests-current.py` with its test.

   Then run card #764's serial pass over the seven manifests on one master SHA, or schedule that pass for phase C.
7. **Card #805 (D28):** six 7307 sites still log unwindowed. Window them, or add a dated Known limit, before the notes.
8. **Pre-release records check:**
   - add `docs/SdkCompatibilityPolicy.md:11` ("within 0.1.x") and `SECURITY.md:5` ("0.2.x") to the runbook step-6 docs sweep;
   - Grok's 2026-10-11 release-notes draft (`release-ag-sw`) is stale on exit 77, and the notes must list the 21 Substrate members (`SdkCompatibilityPolicy.md:102`);
   - close card #761, which #799 delivered (verify first);
   - `CHANGELOG.md` `[Unreleased]` says the AG/SW default and the SW break-glass (runbook step 9);
   - every Known limit cites its card.
9. **End with `/handoff release-0-3-0 B`.**

## 5. How to take a lane to a merged PR (routine)

1. **State the lane plan to the owner before launching more than one agent.** This phase they approved four in parallel.
2. **Set the lane up.**
   - Each lane gets its own clone under `work/` and a branch from `origin/master`, with `user.name t`, `user.email t@t` and `core.longpaths true`.
   - The rules in `LANE-RULES.md` bind: the docker lock; red-first twins; mutation lines verbatim; `git add` of named paths only, then the KG rebuild. Lanes never merge or post.
3. **Build and evidence, in the lane.**
   - Twins are committed alone and shown red in the container, then the fix goes green.
   - Mutation-check each behaviour: `scripts/mutation-check.sh`, or a manifest under `scripts/mutations/` through `scripts/run-spec007-mutation-batch.py`.
   - Run the full cert-gate at the head, the touched classes on net8.0 and net10.0, and the host repo gates.
4. **Review.** One adversarial reviewer agent per PR, then a delta re-review of each fix round, until the verdict is ship.
   - The main thread also checks the lane's fixes against the design rules. This phase that caught a lane's own D23 regression.
   - Lanes can claim more than they did. Verify the counts and histories yourself.
5. **Records conventions.**
   - The Certification count paragraph quotes only values that appeared on master.
   - The PR that adds a file recounts it, and whichever PR merges second recounts again.
   - Line citations run from the `if` line to its closing brace.
6. **Open and announce.** Open the PR, post `claim` before and `handoff` when ready, both on #695.
7. **Merge.** Wait for the five required checks, then `gh pr merge <n> --squash --match-head-commit <full head sha>`. Post `done` with the merge SHA.
   - A lychee red from a GitHub HTTP 500 on an old PR link is a flake; re-run the job.
   - Every merge makes the other open PRs BEHIND, so the next one is rebased and re-run.

## 6. Environment facts

- **Required checks and merging:**
  - The five required checks on master are `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)` and `Readiness summary`, with strict and enforce_admins on.
  - `gh` is authenticated as IanFrelinger.
  - The app's PR monitor is bound to this session's PRs (#796–#804).
- **Readiness:**
  - Master pushes that touch core paths get a readiness run, and docs-only pushes get none.
  - PR runs read `partial`, which `readiness-verdict-for-sha.sh` reports as REFUSED (partial). That is normal for a PR.
- **Counts at `03b5a569e`:**
  - Certification is 155 `.cs` files and 158 entries.
  - cert-gate totals were 3281 at #800's head and 3353 at #801's head (net8.0, 0 skipped).
  - The KG has 60 projects and 5183 declared facts.
  - `spec007-refusal-surfaces.json` has 81 cases, s66–s81 from 4.8b.
  - The ASHLAREXP002 convention tests report:
    - `security-experimental-names=23`
    - `declarations=23 outside-abstractions=0`
    - `surface-lines=160 shipped=0 unshipped=160`
    - `project-wide-disables=19 dead=0`
    - `obsolete-names=4`
- **The saved workflow** `.claude/workflows/project-audit.js` launched by name this phase. That confirms the #794 fix (it is LF, `check_workflows.js` passes).
- **The main thread's checkout** `ashlar-record-correction` was fast-forwarded to `03b5a569e`, so the validation reads master's tree. It keeps five untracked files of the owner's (`.agents/`, `AGENTS.md`, `pilot-*`); leave them.
- **Host clones of this phase**, under `C:/Users/icfre/Documents/Codex/2026-09-12/f-2/work/`, all clean and pushed:

  | Clone | Notes |
  |---|---|
  | `ashlar-g-r3a` | — |
  | `ashlar-r-compat` | — |
  | `ashlar-g-48b` | — |
  | `ashlar-r-exp002` | — |
  | `ashlar-g-records` | Its remote `gh` is GitHub; `origin` is the local `ashlar-g-review`. |
  | `ashlar-phase-f` | — |

- **The lane evidence (28 MB)** is in `work/g-refs/release-A/{r3a,compat,48b,exp002}-logs/`: red and green logs, mutation archives, probe sources and the sweep scripts. It is on the host only and was not published.
- **Known pre-existing failures:**
  - `DogfoodBlock8Tests.ParallelTestMatrix…` and two `DogfoodBlock8ComposedTests`;
  - `RuntimeStudioBlackBoxPlaygroundTests.Prod_cli_objectives…`, which is flaky under load.
- **Dependabot** rebases its PRs onto master by itself. It queued eight update runs on `03b5a569e`.
- **Runner dates:** `ubuntu-latest` starts moving to 26.04 on 2026-10-19, the same day the weekly readiness run fires. The readiness lanes are pinned to ubuntu-24.04; `production-readiness-gate-v1` and `environment-setup-gate-v1` are not.

## 7. Queued work

- Phase B steps 1–7 above, then phase C.
- **Audit findings about this workstream:**

  - release-0-3-0 has no storage branch, claude/release-0-3-0-workspace: handoff-fetch.sh says no such branch. The lane rules exist only on the host, in work/g-refs/release-A/LANE-RULES.md. This handoff is the first durable record of the phase plan.
  - Phase A merged all six named PRs (#796, #799, #798, #800, #801, #804), each with the five required checks green and no Grok block. 03b5a569e reads VERIFIED. aa07e6d91 (#804) is docs-only and gets no readiness push run, so the release commit needs its own verified run.
  - #800 overstates 'D28 windowing'. Four sites still log a bare 7307 Warning: IdeEndpoints.cs:214, MeshAutoPullService.cs:366 (a hosted loop), ImproveCommand.cs:549 and SelfExtendAdmissionBridge.cs:292. Only #800's PR body lists them. Open a card, or record a Known limit before the notes.
  - Phase B (#766) has no claim, and its card body still says 'Blocked on the owner'. LoopbackListenerVerifier is `internal sealed` in application/src/Ashlar.API/Security/LoopbackListenerVerifier.cs:15, and the gRPC host references only src/. Plan for a move into src/ (Ashlar.Infrastructure, next to AshlarInboundListenerPolicy) and then the wiring, or one [coordinated-integration] PR.
  - #752 is a de facto tag prerequisite: without it, the step-9 ProdStyle quote does not evidence default enforcement. It is Grok-built, BEHIND, with a red shell-lint (stale KG) and an unticked net10.0 box, and Claude's release claim excludes it. Take it over with a claim, or re-cut it.
  - Grok's 2026-10-11 draft of the release notes (release-ag-sw) is stale on exit 77. The 0.3.0 notes must also list the 21 Substrate members from #801 (SdkCompatibilityPolicy.md:102).
  - Add SdkCompatibilityPolicy.md:11 ('within 0.1.x') and SECURITY.md:5 ('0.2.x') to the step-6 docs sweep. Neither is on the reader's list.
  - Close card #761, which #799 delivered. Ask Grok for the drift audits of #796-#804; none has been posted since 2026-10-13T22:22Z.
  - #804 had a claim and a done post but no handoff post, although the workstream claim says each PR gets its own handoff and done.
  - Dependabot rebase churn queued 68 runs, and master's Readiness summary waited more than 33 minutes for a runner. Retire #730-#737, which #753 supersedes, before the release window.
  - The ProdStyle net8.0 evidence runs on the runner image's preinstalled .NET 8.0.31. The ubuntu-latest move on 2026-10-19 and .NET 8 end of support on 2026-11-10 both bear on it. The readiness lanes are pinned to ubuntu-24.04, but production-readiness-gate-v1 and environment-setup-gate-v1 are not.
  - The spec-007-pr4 phase H work has no owner, because no phase H handoff exists: the mutation-manifest hygiene (r14 at spec007-enforcement-routes.json:165 is stale), #753, cards #757-#767 and PR 5. Decide whether release-0-3-0 takes any of it.
  - **Handled at writing:** the D28 finding is now card **#805**, which lists six unwindowed 7307 sites. MeshDiscoveryService:266 and Program.cs:282 were added on re-reading master; the audit had named four.

- **Cards still open:**

  | Card | What |
  |---|---|
  | #757 | The testing-strategy gate should ignore comment-only diffs. |
  | #758 | EventId 7308. |
  | #759 | `FailurePolicy` (owner (c)). |
  | #760 | The double audit record. |
  | #761 | Pre-switch wording. Check whether R3a folded it in, and close it if so. |
  | #763 | `DESIGN-4-final.md:605` and C12. |
  | #764 | The mutation evidence pass. |
  | #765 | 4.9b. |
  | #766 | The inbound hosts (phase B). |
  | #767 | Dependabot #728. |
  | #790 | Isolate Claude workers. This is budgeted-autonomy's; it gates the next live Claude-worker run. |
  | #802 | Workflow stress and optimize lose a refused probe. |
  | #803 | Workflow agent nodes report success after a refused step. |
  | #805 | D28: six 7307 Warning sites log unwindowed (phase B). |

- **Left from the reviews of this phase:**
  - two "report-only guard handler" comments in files on ProdStyle trigger paths (`AshlarKernelRegistrar.Phases.cs:781`, `RunPodCapabilityRoutingServiceCollectionExtensions.cs:25,62`), to fix when a code PR touches them;
  - the application/ D28 windowing sites and `SelfExtendAdmissionBridge` (#800 scope);
  - a possible card for lychee resilience to GitHub 500s, which flaked five times this phase.
- **From validation:**
  - 65 stranded local branches (`bx/WS-*`, 2026-10-01);
  - six workflows whose newest master run is red, all stale since May–September: Air-Gapped Multi-Env, Cross-Platform, the three mesh lab gates, and Runtime Release Promotion.

  The owner should decide whether any matters for 0.3.0. The release runbook names `runtime-release-gate`, not these.
- **PR 5 proper:** receiver-side `CanRead` at the server seams, inbound response mediation, and §8 A/B/E, all per Q-B.

## 8. Ways of working the owner has endorsed

- **Phases.** One phase per thread, ending with `/handoff`; a pause is also a handoff.
- **Lanes.** Parallel lanes, each with adversarial review and delta re-reviews until the verdict is ship, then serial merges.
  - State the lane plan before launching several agents. The owner approved four this phase.
- **Owner questions.** Ask only for real decisions (AskUserQuestion, recommended option first). Land the answers as decisions-log rows before the code.
  - A default the owner did not choose is an integrator decision in the PR body, never an owner row.
  - The owner said to use judgement on "common sense stuff" (2026-10-13).
- **PR bodies.** Quote real counts and verbatim mutation lines, and say what was not observed failing.
- **The bus.** Claim before you build; `handoff` when ready; `done` with the merge SHA.

## 9. Starting prompt for the next phase

> Execute **phase B** of release-0-3-0 on this machine. Run `/start-phase release-0-3-0`, which fetches this handoff from `claude/release-0-3-0-workspace`. Read it in full starting with §0, and check §1 against the live repo:
> - master `aa07e6d91` or later, and its readiness verdict;
> - the bus (#695): Grok's drift asks for #796–#804;
> - the open PRs #750–#753.
>
> Then, in order:
> 1. Answer Grok's drift asks.
> 2. Wire the AG/SW loopback-or-fail-boot rule into the gRPC server host and Fleet.Host (card #766; the owner's 2026-10-13 row says before the tag).
> 3. Claim, rebase and land #752 (the ProdStyle twins; a tag prerequisite) and #753, and close the superseded Dependabot PRs.
> 4. Ask me question (b) on exit 77 and build PR 4.9b per the answer, or record the deviation (card #765).
> 5. Window the six 7307 sites of card #805, or record them as a Known limit. Fix #803 (and #802) if capacity allows.
> 6. Build the manifest-hygiene PR.
> 7. Check the CHANGELOG against runbook step 9.
> 8. Put the release sign-off questions in §3 to me: the rollback drill, the ReleaseSignOff row, and exception rows for the security defects that ship open.
>
> Tell me the lane plan before launching more than one agent. Build and test only through `scripts/test-in-container.sh`, one container at a time; never run `dotnet` on the host. If anything I have not decided blocks you, ask instead of choosing. Phase C is the release itself, and is not part of phase B. When phase B is done, run `/handoff release-0-3-0 B` and notify me.
