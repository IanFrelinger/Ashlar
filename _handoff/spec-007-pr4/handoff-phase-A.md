# spec-007-pr4 handoff: end of phase A, start of phase B

*Written 2026-10-05 around 20:30 UTC, at the end of phase A. Start the next thread with the prompt in §9, or with `/start-phase spec-007-pr4`.*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`):
- end each phase with `/handoff spec-007-pr4 <phase>`;
- start each phase with `/start-phase spec-007-pr4`.

The handoff is published to `claude/spec-007-pr4-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff.

**The procedure itself is PR #714.** If #714 is not merged when you start, the commands, template and scripts are on branch `claude/phase-handoff-procedure`. Read `.claude/commands/start-phase.md` there, or follow §9 directly. Fetch this file with:

```bash
git show origin/claude/spec-007-pr4-workspace:_handoff/spec-007-pr4/handoff.md
```

## 1. Where things stand

**Master: `79e988c31`** (#713). No code merged during phase A after #713.

| PR | What | Merge commit |
|---|---|---|
| #706 | SPEC-007 PR 1: label lattice and `ReferenceMonitor` | `0f9642ec2` |
| #707 | PR 2: bridges from the existing labels | `f1f2cff48` |
| #709 | PR 3a: report-only egress guard, inventory, convention test | `c257aa684` |
| #710 | Dev workflow: SessionStart hook, devtest image, SPEC-007 in the repo, `scripts/mutation-check.sh` | `ea0674e53` |
| #711 | PR 3b: every listed outbound site routed to the guard, report-only | `8ec674d2a` |
| #712 | drift-711: the 3b merge SHA in SPEC-007 | `9abb491d3` |
| #713 | The eight PR 4 owner decisions and the PR 4 plan, in SPEC-007 (phase A) | `79e988c31` |

**Open PRs from phase A.** The phase A thread is still driving both and will merge them on green. Check their state when you start. If either is still open, take it over: merge on five green required checks, then post `done` on #695.

| PR | What | Branch | State at handoff |
|---|---|---|---|
| #714 | The phase/handoff procedure: CLAUDE.md section, `/handoff`, `/start-phase`, template, `scripts/handoff-{publish,fetch}.sh` and a 42-assertion test | `claude/phase-handoff-procedure` @ `221df799d` | CI running; build-core green. `handoff pr-714` posted on #695 |
| #715 | drift-713: names the 3a defects 4.10 fixes and the question Q5 answers, and says open question B's flip is PR 5 | `claude/drift-713-spec007-refs` @ `7699e0f27` | CI queued. Docs only; repo gates 25/25 locally. `handoff`/`done` not yet posted |

Merges are serial with `strict` on, so whichever merges second must be brought up to date first (update-branch, then re-run CI).

**Readiness.**
- The last readiness verdict on master is `verified`, for `8ec674d2a` (#711, confirmed by Grok).
- #712 and #713 were docs only, so the readiness gate did not run on them.
- Master's scheduled Runtime Portability Gate on `79e988c31` (19:32Z) shows `failure`. Two jobs were cancelled with "The job was not acquired by Runner of type hosted even after multiple attempts". That is runner capacity, not code, and the check is not a required one.

**Agent-bus (#695).**
- `drift-713` (Grok, 17:18Z) is answered by #715. Post `Kind: done`, `About: drift-713` with #715's merge SHA, and say why B names PR 5 rather than 4.11: the design's D38 says so, and PR 4 enforces egress only.
- `handoff pr-714` is posted; its `done` is pending.
- Grok is holding Dependabot #689–#693.
- Grok is standing by for the 4.4, 4.1, 4.6 and 4.2 handoffs.

**Phase A exit criteria are met.**
- The four lane branches are pushed: 4.4 `5f56b8ff4`, 4.1 `1e3e645d2`, 4.6 `a501c2b26`, 4.2 `25301b79a`.
- Every lane has a build report and an adversarial check. Both are recorded in `waveA-findings.md` and `waveA-lanes.json` on the storage branch, and summarised in §4.

## 2. Read these first

**In the repo:**
- `CLAUDE.md`: never run `dotnet` on the host; five required checks; serial merges; mutation-check every behaviour change; the agent-bus loop.
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`: the spec, its status, the decisions log (the eight PR 4 rows dated 2026-10-05), and open questions A to E.
- `docs/EgressInventory.md`, `ci/egress-inventory.tsv` (85 rows on master), and `ci/cert-gate-assertions.md` rows 56 and 64: the egress records the lanes edit.
- `_handoff/bus/PROTOCOL.md`: the message format for #695.

**On the storage branch `claude/spec-007-pr4-workspace`, folder `_handoff/spec-007-pr4/`** (never merge this branch):
- **`waveA-findings.md`: every lane's result, deviations, check verdict and check findings, and the lane's open issues.** Phase B's fix list (§4) comes from it.
- `waveA-lanes.json`: the full lane and check reports, with evidence and verbatim mutation lines.
- `DESIGN-4-final.md`: the authoritative PR 4 design. §4 holds the PR rows and their done-when, §3.B the engineering defaults D1–D43, §2.x the mechanisms, and the end the owner answers.
- `LANE-RULES.md`: the rules every PR 4 lane follows.
- `pr-4.1-body.md`, `pr-4.2-body.md`, `pr-4.4-body.md`, `pr-4.6-body.md`: PR body drafts. Update them with phase B's fixes and the new counts.
- `BRIEF.md`, `scout-S1…S5.md`: the scouting behind the design.

## 3. Decisions

**Owner decisions** (all recorded in SPEC-007's decisions log, 2026-10-05):

| Q | Decision |
|---|---|
| Q1 | AirGapped (AG) and SecureWorkstation (SW) enforce when 4.11 merges. SW alone has a break-glass, `ASHLAR_EGRESS_MODE=report`: read once, logged at Warning, stamped on every decision. AG ignores every override. |
| Q2 | The runner declares a subject's floor, only for inputs it built and can vouch for. Every tool result is observed: a RAG hit at its tier, an unreported read at SystemHigh. Self-extend runs at SystemHigh. |
| Q3 | Child processes that run agent-writable code are off-host. Docker is Host only with `--network=none`. On AG and SW they are refused unless sandboxed. |
| Q4 | Before PR 6, only file exports leave AG and SW: `pkg export --out`, `export`, `mesh export`, in report mode as an operator verb. `pkg publish` and `share` are refused. |
| Q5 | Every factory client is enforced, the host's own included, with a per-client opt-out through `Configure<EgressGuardOptions>`. AG ignores the opt-out; naming one of Ashlar's own clients fails boot. |
| Q6 | On AG and SW, inbound surfaces stay on loopback. On SW, MCP over HTTP fails boot; stdio stays. |
| Q7 (open question D) | The refused subject gets the category, site, family, destination class and a random reference. Operators get the full `Detail` and sequence. Remote parties get a fixed text and the reference. |
| Q8 (open question C) | Labels carry the level only. C3 normalisation applies to the first producer of a custom level. |

**Integrator decisions made in phase A.** None needs the owner, because each follows a design default or the fail-closed rule. Record each in its PR body.
1. **4.2: drop the build-time NoDecision record.** The design (§2.6) wanted the hop to publish a record when it refuses, which netstandard2.0 cannot do. The lane published one when the client is built instead. That record misleads operators: it claims a refused egress where none happened, and it is written for SendAsync-only clients. Keep only the runtime `NotSupportedException`.
   - Delete the record publishing in the hop's constructor and the record assertions.
   - Delete `EgressGuard.PublishNoDecision` and `NotEvaluatedBasis` if nothing else uses them.
   - This also removes the 4.2/4.6 constructor collision (4.2 finding 5) and most of 4.2 findings 1, 3 and 4.
2. **4.4: an ancestor disposed out of order still counts (fail closed).** This is 4.4 check finding 1; D12's aim is to close the declassification hole. Change `Frame.Resolve` to join disposed ancestor frames above the innermost live frame, and flip the twin. If you believe today's behaviour is right, that is an owner call: ask, do not choose.
3. **4.6: the composed guard uses the strictest profile noted in the process** (4.6 check finding 1), for AG and SW alike.
4. **4.6: the opt-in stays out of user-facing docs until 4.11.** Keep the hedged section in `docs/EgressInventory.md`, which §4 requires for 4.6. Trim the CHANGELOG and `AddAshlar`'s XML doc to say a mode field exists, without naming `ASHLAR_EGRESS_MODE` (4.6 finding 7c). "Undocumented until 4.11" is design text, not an owner answer.

**Open questions:**
- **One owner call is coming in phase C, not B** (4.4 check finding 5). A read scope observes its result only when it ends, so a tool that reads and then egresses in the same call is decided at the pre-read mark. 4.5 should record this as a known limit for runners whose floor is below SystemHigh. The stricter fix (an open, unreported scope counts as SystemHigh) changes Scenario B's expected reason from LevelTooLow to SystemHighData. Ask the owner when 4.5 is built.
- **Still open and not blocking:** §8 Q1–Q6 and SPEC-007 open questions A (a receiver-side helper and `TryParse`), B (SystemHigh versus TopSecret; its legacy-side flip is PR 5) and E (the SPEC-007 number clash with SPEC-006:551).

## 4. Plan and live status

**Why PR 4 is split.** No production code enters an `EgressSubject` frame. Enforcement alone would make AG and SW host-only, and the leak test would pass without a label causing the refusal. So the switch comes last, as 4.11.

**Phase map:**

| Phase | Scope | Ends when |
|---|---|---|
| A (done) | PR 4 design; the eight owner decisions (#713); wave A built and adversarially checked (4.4, 4.1, 4.6, 4.2); the procedure PR (#714); drift-713 (#715) | Lane branches pushed and their findings recorded (met) |
| **B (next)** | Finish wave A: fix the check findings below, then integrate, verify, mutation-check, PR, hand off and merge, in the order 4.4 → 4.1 → 4.6 → 4.2 | All four merged; `done` posted on #695 for each; Grok's drift asks on them fixed or queued in the handoff; master readiness `verified` for the last merge (or the reason it is not) |
| C | Build and merge 4.3 (redirects), 4.5 (producers, report-only) and 4.10 (AG and SW hygiene) | All three merged |
| D | 4.7 (refusal surface on the routes), then 4.8 (catch-alls and trust-boundary exits) | Both merged |
| E | 4.9 (explicit sites, operator verbs, child processes) | Merged |
| F | 4.11, the switch, with `EgressEnforcementLeakTests` (§5 done-when); SPEC-007 shows PR 4 merged | Merged, and master readiness `verified` |
| G | Security follow-ups (gRPC open relay, Fleet download, the `mcp:` deny-list), the dead MEAI allow-list, then the PR 5 design | Owner's choice |

**Every work item:**

| PR | Content | Branch @ SHA | State |
|---|---|---|---|
| 4.4 | Frame semantics: chain join, propagation on dispose, `Observe`, `BeginRead`/`ReadScope` (+`Complete`), internal `Detach` at AgentBus | `claude/spec-007-pr4-4.4-frames` @ `5f56b8ff4` (1 commit) | Built and checked: approve with changes. cert-gate 2566/2566; mutations 9/9 killed |
| 4.1 | Records that could read Host: `file:` never Host, `mesh-peer:<ip>`, MEAI `ProviderUri` and fail-closed `meai:<key>`, the Ollama cloud rule, no auto-redirect in `OllamaHttpChatClient` | `claude/spec-007-pr4-4.1-records` @ `1e3e645d2` (**11 commits: squash**) | Built and checked: no blocking defect. Mutations 9/9 killed. Needs `[coordinated-integration]` (MeshServeService) |
| 4.6 | Mode plumbing: resolver, `ModeBasis`/`Refused`/`Ref`, opt-ins, read-once latch, strictest-wins, reset seam, startup line; every profile still reports | `claude/spec-007-pr4-4.6-mode` @ `a501c2b26` (1 commit) | Built and checked: pass with fixes. cert-gate 2637/2637; mutations 15/15 killed |
| 4.2 | netstandard2.0 synchronous `Send` refused through a hop; ALC twin | `claude/spec-007-pr4-4.2-syncsend` @ `25301b79a` (1 commit) | Built and checked: sound, with record fixes. Mutations 6/6 killed |
| 4.3, 4.5, 4.10 | Redirects; producers; AG/SW hygiene | – | Phase C |
| 4.7, 4.8 | Refusal surface; catch-alls | – | Phase D |
| 4.9 | Explicit sites (17, not 16: 4.1's check recounted), operator verbs, child processes, CLI exit 77 | – | Phase E |
| 4.11 | The switch and the leak test | – | Phase F |

**Phase B, spelled out.** For each lane, in merge order, fix these. File and line details for each finding are in `waveA-findings.md`.

**4.4 → first.**
- [medium] Finding 1, integrator decision 2: in `Frame.Resolve`, join disposed ancestors above the innermost live frame, stopping at a detachment.
  - Flip `A_frame_disposed_out_of_order_leaves_the_chain` to expect Secret and LevelTooLow.
  - Add the child-task (`Task.Run`) twin.
  - Reword cert-gate row 65 and the class remark at `EgressSubject.cs:24`.
  - Mutation: walk ancestors through `Live()`; it must go red.
- Finding 2: a twin that ends a read scope on a flow without its begin chain (`ExecutionContext.SuppressFlow()`, a new Thread, or `Detach`). Mutation-check the ambient-Observe mutant, plus a late-report mutant (drop the `_ended` branch in `Report`).
- Finding 3: the `EgressGuard.cs:15-16` class doc says "the join of every live frame's mark".
- Finding 4: record the D9 amendment (IVT for `Ashlar.Orchestration`) in SPEC-007's PR 4 notes. When 4.6 is integrated, add a convention fact pinning the callers of the reset seam and the latch setters.
- Fill "**4.4** (this PR)" in SPEC-007 with the PR number at merge.

**4.1 → second.**
- Test the resolved-name half of the OllamaProvider cloud rule: `|| IsOllamaCloudModel(validationResult.Value.Name)`.
- The low items:
  - the EgressDecision redaction docs for `file:` names;
  - the floors comment (149 occurrences, http.new 19);
  - name EG-MDL-10 and the alias caveat in Known limits;
  - a Bedrock `ProviderUri` twin;
  - the CHANGELOG "fixed when built" wording;
  - the redirect twin's port race.
- Squash the 11 commits. Do **not** push the clone-only helper branches `wip/4.1-*`.

**4.6 → third.**
- [medium] Finding 1, integrator decision 3: compose with `AshlarDeploymentProfileEnvironment.ResolvedRaw ?? canonicalProfile` after `NoteResolved`, and build the startup line from the same value.
  - Extend `A_second_AddAshlar_with_no_profile_does_not_lower_an_AirGapped_process` to resolve the second container's guard and assert `Profile == "air-gapped"`.
  - Mutation: revert to `canonicalProfile`.
- Finding 2: read the effective profile once per `Evaluate`.
- Finding 3: a null-profile guard takes the stricter of its constructor override and `ProcessOverride()`.
- Finding 4: add a `"junk"` hosting-option row. Mutation-check three more: drop the Unrecognised raise; `Ref = Sequence.ToString("x16")`; remove `ReferenceEquals(ProcessDefault)`.
- Finding 5: `Version = 1` on event 1.
- Finding 7:
  - fill "4.6 (this PR)";
  - row 64 gains a sentence, plus the two new twin classes;
  - integrator decision 4 (trim the CHANGELOG and XML doc);
  - note in the 4.11 plan that `An_explicit_profile_is_reported_and_does_not_change_the_decision` already has an enforce row.
- Finding 8: make `ModeResolutionProbe` an `AsyncLocal`, or document the constraint.
- Finding 6 goes to the 4.11 known limits, not B.
- For Grok and the CHANGELOG: after `AddAshlar(AirGapped)`, a later `AddAshlar` no longer lowers `ForbidsRemoteProtocolEgress` or `DisplayName`.

**4.2 → fourth.**
- Integrator decision 1: drop the build-time record.
- Fix the SPEC-007 "Gaps carried to PR 4" bullet (about lines 44–50): the netstandard2.0 synchronous-Send gap is closed by 4.2 (refused); redirects and Host records stay open for 4.3 and 4.1.
- Fix the `EgressDecision.Fault` and `CurrentBasis` XML docs, if anything 4.2 still adds touches them.
- The low items:
  - the hop is an `HttpMessageHandler`: give it an internal `Inner` accessor for 4.3's walker, and document in `Wrap`'s remarks that replacing `InnerHandler` voids the guarantee;
  - run the build-core command (`dotnet build Ashlar.LocalDevCore.slnf`) in the container, because of the `SetTargetFramework=netstandard2.0` reference;
  - `docs/SdkCompatibilityPolicy.md:175-177` wording ("every request it is handed"; the refusal applies on any .NET 5+ runtime; "classic Mono");
  - fold the CHANGELOG entry into the existing Added entry.

**Every lane, after master moves:**
- merge master in and recount:
  - the `ci/cert-gate-assertions.md` Certification count paragraph (each lane counted alone on `79e988c`);
  - row 64's TSV sums;
  - the `EgressGuardConventionTests` floors comment;
- regenerate the knowledge graph after `git add`;
- expect conflicts in CHANGELOG Unreleased, the SPEC-007 PR 4 plan bullets, `docs/EgressInventory.md` and `ProcessGlobalEnvironmentConventionTests`.

**Phase B exit:**
- all four are merged, and `done` is posted for each with its merge SHA;
- Grok's drift asks on them are fixed or listed in the phase C handoff;
- the readiness verdict for master after the last merge is `verified`, or the handoff says why not.

## 5. How to finish a lane (routine)

1. **Clone.** `git clone` into the scratchpad. A lane clone whose `origin` is `/home/user/Ashlar` sees that checkout's possibly stale `origin/master`, so fetch from GitHub first. Check out the lane branch and squash it if it has several commits.
2. **Fix.** Apply the §4 findings. Write the twin before the fix wherever a twin is asked for.
3. **Update.** Merge master in, resolve the conflicts, recount the records, `git add`, then `python scripts/knowledge-graph/build-knowledge-graph.py`.
4. **Verify in the container only** (`scripts/test-in-container.sh --repo <clone>`):
   - `bash scripts/run-cert-gate.sh` (net8.0); for net10.0, `sed` the TFM inside a throwaway clone;
   - the touched test projects on their frameworks (Tests.Orchestration is net8.0 only);
   - `scripts/ci/run-repo-gates.sh` with shellcheck on PATH; all 25 must pass.
5. **Mutation-check** each new or changed behaviour with `scripts/mutation-check.sh` on the committed head. Put the verbatim summary line in the PR body.
6. **Open the PR** from `pr-4.x-body.md`, with `[coordinated-integration]` when non-test code under `application/` changes. Subscribe to it.
7. **Post** `Kind: handoff`, `About: pr-<n>` on #695: what changed, what was verified, and which records it touches.
8. **Merge** (squash) on five green required checks, unless Grok posted `block`. Post `Kind: done` with the merge SHA. The next PR is now BEHIND: update it and re-run.

Parallelism: fix the four lanes in parallel workflows (2–3 workflows, 2 agents each), but merge serially.

## 6. Environment facts

- **Docker:** the container restarts often. The SessionStart hook (`.claude/hooks/session-start.sh`) restarts dockerd, writes the docker wrapper at `/root/.ashlar-session/bin/docker` and builds `ashlar-devtest:local`. If docker is down: `CLAUDE_CODE_REMOTE=true bash .claude/hooks/session-start.sh`.
- **`scripts/test-in-container.sh`** needs a clone, not a worktree. It tests committed state unless given `--dirty`. **Never pipe it through `| head`**: SIGPIPE kills the run. Save full output to a log instead.
- **Background tasks die at 2 hours.** A workflow agent's long mutation batch hit this (4.6's m13), so split long mutation batches.
- **`mutation-check.sh` reports `INVALID … reason=red-no-tests`** when the mutant does not compile, for example CS0162 (unreachable code) under TreatWarningsAsErrors. That is not a kill: pick a mutant that compiles.
- **Workflow concurrency** is capped at 2 agents per workflow on 4 CPUs. Run 2–3 workflows side by side. A killed workflow resumes with `resumeFromRunId`.
- **Cert-gate counts.** Master has 2542 tests. Each lane counted alone on `79e988c`: 4.4 → 2566, 4.6 → 2637. Recount after each merge.
- **xUnit 2.9.3** runs `DisableParallelization` collections (such as `EnvironmentVariables`) after the parallel ones. 4.6's process-global seams depend on that.
- **Readiness:** the best verdict on a PR is `partial`, because the Docker lanes run on push only. Master pushes give `verified`. Docs-only changes skip the gate. `scripts/release/readiness-verdict-for-sha.sh` refuses when `gh auth status` reports an invalid token, even though `gh api` works through the proxy. In that case, read the "Readiness verdict" annotation on the `Readiness summary` check run with `gh api`.
- **GitHub:** use the GitHub MCP tools, loaded through ToolSearch. `gh api` also works through the proxy.
- **Shellcheck** is not installed by default. Phase A used the static shellcheck 0.9.0 binary from `pip install --target <scratchpad>/sc-py shellcheck-py==0.9.0.6`, with `<scratchpad>/sc-py/bin` on PATH when running the repo gates.
- **Known pre-existing failures** in Tests.Infrastructure (net8.0, container): `DogfoodBlock8Tests.ParallelTestMatrix…` and two `DogfoodBlock8ComposedTests`. `RuntimeStudioBlackBoxPlaygroundTests.Prod_cli_objectives…` is flaky under load.
- **Pre-existing hazard (4.2 lane):** a process that loads two copies of `Ashlar.Abstractions` has two `Ashlar-Egress` EventSources, and the second goes dark. Decisions still reach the `EgressDecisionLog` sinks.

## 7. Queued work

**Within PR 4, for later phases:**
- **4.5:**
  - `ToolCallingAgent` wraps each tool call in `using var read = EgressSubject.BeginRead(); …; read.Complete();`; a labelled tool calls `read.Report(label)`; RAGTool's "read nothing" is `Report(Public)`;
  - the floor-pin convention for production `Enter` calls;
  - the read-scope owner call (§3).
- **4.7:**
  - define `Refuses` in terms of `EgressDecision.Refused`;
  - the ILogger sink (event 7300) gains Mode, ModeBasis and Ref;
  - event 7301 is reserved for EgressRefused.
- **4.11:**
  - 4.6 finding 6 (the per-decision profile re-read can be lowered by `SetEnvironmentVariable`; note AG/SW through `NoteResolved`);
  - 4.6 finding 3 if not fixed in B;
  - the D6 twin list.
- **Follow-up idea:** Ollama's `/api/tags` `remote_host`/`remote_model` would identify cloud models more precisely than the name suffix. EG-MDL-11 does not apply the cloud rule.

**Outside PR 4 (owner's choice; the design recommends them):**
- **Defect 1:** the gRPC open relay (`AgentTransportServiceImpl.cs:173`). It needs its own security PR now.
- **Defect 2:** the commercial Fleet task-result download (`CommercialFleetEndpoints.cs:495-499`) may allow arbitrary file reads.
- A deny-list on `mcp:` tool ids in `DataExfiltrationPolicy`.
- The dead MEAI cloud allow-list, after 4.11.
- **Side findings:**
  - the SNS signing-cert host check admits `*.amazonaws.com`;
  - `CloudSanitizationProxy` skips sanitising on a wire-supplied `IsAirGapped`;
  - `docs/Configuration.md:168` drift.
- **Task cards queued in the Claude app** (several overlap PR 4):
  - orchestrator transport under non-Full profiles;
  - A2A registry agents;
  - `RemoteBrick` `hostBaseUrl` SSRF;
  - agent `RequireLocalOnly` and the `mcp:` policy (PR 5);
  - `ASHLAR_SHARED_ADAPTATIONS_PATH` ignored.

## 8. Ways of working the owner has endorsed

- **One phase per thread**, ending with `/handoff`. This becomes a CLAUDE.md rule when #714 merges.
- **Lanes and checks:** build PRs in parallel lanes, each followed by an adversarial check. Integrate, verify against a base, mutation-check, then run a final review whose findings must survive refutation.
- **Asking the owner:** use AskUserQuestion, recommended option first, only for real decisions: policy, security posture, compatibility, or anything the spec reserves to the owner. Otherwise take the fail-closed design default and record it.
- **PR bodies** give real counts, say what was not observed failing, and quote each mutation's verbatim `mutation-check.sh` summary.
- **Proceduralize** repeatable work: commands, templates and tested scripts over memory.

## 9. Starting prompt for the next phase

> Execute **phase B** of spec-007-pr4. Run `/start-phase spec-007-pr4`, or read this handoff in full, starting with §0. If #714 is not merged yet, the command is on branch `claude/phase-handoff-procedure`. Phase B finishes PR 4's wave A: for 4.4 → 4.1 → 4.6 → 4.2 in that order, fix the check findings listed in §4 (details in `waveA-findings.md` on `claude/spec-007-pr4-workspace`), squash, merge master in, recount the records, verify, mutation-check, open the PR, hand it off on #695 and merge on green. Phase B ends when all four are merged with `done` posted, Grok's drift asks are fixed or queued, and master's readiness verdict after the last merge is `verified`. First check whether #714 and #715 merged; if not, finish them. Build and test only through `scripts/test-in-container.sh`. If anything the owner has not decided blocks you, ask instead of choosing. When the phase is done, run `/handoff spec-007-pr4 B` and notify me.
