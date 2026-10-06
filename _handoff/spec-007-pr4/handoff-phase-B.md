# spec-007-pr4 handoff: end of phase B, start of phase C

*Written 2026-10-06 at 17:45 UTC, at the end of phase B. Start the next thread with the prompt in §9, or with `/start-phase spec-007-pr4`.*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`):
- end each phase with `/handoff spec-007-pr4 <phase>`;
- start each phase with `/start-phase spec-007-pr4`.

The handoff is published to `claude/spec-007-pr4-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff. Fetch it with `bash scripts/handoff-fetch.sh --workstream spec-007-pr4`.

## 1. Where things stand

**Master: `de41a8ac8`** (#716).

| PR | What | Merge commit |
|---|---|---|
| #706 | SPEC-007 PR 1: label lattice and `ReferenceMonitor` | `0f9642ec2` |
| #707 | PR 2: bridges from the existing labels | `f1f2cff48` |
| #709 | PR 3a: report-only egress guard, inventory, convention test | `c257aa684` |
| #710 | Dev workflow: SessionStart hook, devtest image, `scripts/mutation-check.sh` | `ea0674e53` |
| #711 | PR 3b: every listed outbound site routed to the guard, report-only | `8ec674d2a` |
| #712 | drift-711 | `9abb491d3` |
| #713 | The eight PR 4 owner decisions and the PR 4 plan (phase A) | `79e988c31` |
| #715 | drift-713 (phase A) | `ce9885e2f` |
| #714 | The phase/handoff procedure (phase A) | `5ff00a4af` |
| **#717** | **PR 4.1: records that could read Host name the remote party; Ollama cloud models at ollama.com; `OllamaHttpChatClient` follows no redirects (phase B)** | `bbc5d7146` |
| **#718** | **PR 4.6: mode plumbing — one resolver, `Mode`/`ModeBasis`/`Refused`/`Ref`, strictest profile wins, read-once latch, reset seam, startup line; everything still reports (phase B)** | `3196ba11c` |
| **#719** | **PR 4.2: a synchronous `Send` on the netstandard2.0 asset is refused before anything is sent; no record (owner's D31 amendment) (phase B)** | `ad3d570b2` |
| **#716** | **PR 4.4: frame semantics — no-skip subject frames, `Observe`, read scopes, callback-shaped `RunDetached` at AgentBus (phase B)** | `de41a8ac8` |

**No PRs are open from this workstream.**

**Readiness (master pushes):** `bbc5d71` verified (4 lanes), `3196ba11` verified (4 lanes), `ad3d570b` verified (4 lanes), `de41a8ac` **verified** (4 lanes run, 0 skipped; `Readiness summary` check run 112399479783). Read with `gh api` on the `Readiness summary` check run's "Readiness verdict" annotation (§6).

**Agent-bus (#695).** `handoff` and `done` are posted for pr-717, pr-718, pr-719 and pr-716. Grok's drift audit of 717, 718 and 719 came back **clean** (14:28Z), with one note: SPEC-007's 4.2 line lacked its merge SHA. #716 added it, so that note is closed. drift-716 came back **clean** (17:28Z). Grok's pr-716 note, a `uat` tier 0 `doctor-container-truthful` failure, was a runner Docker hiccup: the re-run passed, and the note is answered in the pr-716 `done`. Grok is holding Dependabot #689–#693 (GitHub Actions bumps). The NuGet Dependabot PRs #698–#703 and #647 are also open; nobody has claimed them.

**Phase B exit criteria: met.**
- All four are merged: #717 `bbc5d71`, #718 `3196ba11`, #719 `ad3d570b`, #716 `de41a8ac`. `done` is posted for each on #695.
- Grok's drift audits: drift-717, 718 and 719 were clean (14:28Z), and drift-716 was clean (17:28Z). Grok's one note, the missing 4.2 merge SHA, was fixed in #716. **Nothing is open for Claude on #695.**
- Master readiness after the last merge (`de41a8ac`): `verified`. All 35 latest check runs on that master commit are green, per Grok.

## 2. Read these first

**In the repo:**
- `CLAUDE.md`: never run `dotnet` on the host; five required checks; serial merges; mutation-check every behaviour change; the agent-bus loop.
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`: status, the PR 4 plan lines (4.1, 4.2, 4.4 and 4.6 merged, with their numbers; 4.4's merge SHA is still to add, §7), the decisions log (the eight 2026-10-05 rows and the 2026-10-06 D31 row), open questions A–E, and the **4.5 obligation** written into the 4.4 line.
- `src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs` class remarks: the frame rules and the "Known limits (fail closed)" that 4.5 must respect.
- `docs/EgressInventory.md` (Known limits), `ci/egress-inventory.tsv` (86 rows, 150 occurrences after 4.2), `ci/cert-gate-assertions.md` rows 56 and 64–67.
- `_handoff/bus/PROTOCOL.md`.

**On the storage branch `claude/spec-007-pr4-workspace`, folder `_handoff/spec-007-pr4/`** (never merge this branch):
- `DESIGN-4-final.md`: the authoritative PR 4 design (§4 rows for 4.3, 4.5, 4.10; §3.B defaults; §2.x mechanisms; owner answers at the end). **Superseded in two places by phase B:** D9 (IVT list gains `Ashlar.Orchestration`) and D31 (no record for the ns2.0 refusal), and §2.2's `Detach()` is now `RunDetached(Action)`.
- `LANE-RULES.md`: the lane rules (paths in it are from phase A's scratchpad; adjust).
- `PHASE-B-LANE-BRIEF.md`: phase B's lane brief, the template for phase C lanes.
- `INTEGRATION-NOTES.md`: phase B's decision and progress log.
- `pr-4.1/4.2/4.4/4.6-body.md`: the final PR bodies, as merged.
- `waveA-findings.md`, `waveA-lanes.json`, `BRIEF.md`, `scout-S1…S5.md`: phase A material.
- `frame-model-noskip.py`, `frame-model-wd-callback.py`, `frame-model-pins.py`: the Python model checkers behind the 4.4 rule. They enumerate programs over flows with Enter, Dispose, Observe, fork and `RunDetached` brackets, and check for write-downs. `pins.py` computes the exact frame counts the growth twins pin. Reuse them if 4.5 or later touches frame semantics, and make sure any new model is caught failing on a broken variant first.

## 3. Decisions

**Owner decisions** (SPEC-007 decisions log): Q1–Q8 of 2026-10-05, unchanged (AG/SW enforce at 4.11 with an SW-only break-glass; runner-declared floors; child processes off-host; only file exports leave AG/SW before PR 6; every factory client enforced with a per-client opt-out AG ignores; inbound loopback on AG/SW; refusal surface per audience; labels carry the level only). **New, 2026-10-06:** a synchronous `Send` refused on the netstandard2.0 asset leaves **no decision record**; the exception is the only signal (amends D31; cost: such a refusal never appears in Ashlar's egress log under AG/SW enforcement).

**Integrator decisions this phase** (each recorded in its PR body; none needed the owner):
1. **4.4 frame rule (fail closed).** Subject frames are **no-skip**: a flow leaves a frame only by disposing its own head while it is undisposed, and goes back to exactly the frame it was entered under, disposed or not; every frame on a chain counts at its live mark down to a detachment. Detachments are **callback-shaped**: internal `EgressSubject.RunDetached(Action)` (replaces `Detach()`), which restores the caller's head inside the protected region, before any caller exception filter runs; work created and started inside keeps the detachment (a Task, `System.Threading.Timer`, registration or continuation captures the flow where it is created; a `Thread` or `System.Timers.Timer` where it is started). **Why:** three rounds of bounded unwinding (a frame-keyed record, a "subject-previous" rule, a flow-local skip list) each had a verified write-down found by adversarial re-checks; the no-skip rule has a one-line invariant and model checks found none (~14M sequences, with a broken-restore control caught in 3–6 ops). **Accepted (fail closed):** (a) a flow that disposes frames out of order stays in the outer frame; repeated, its chain grows one frame per repetition; (b) later reads raise that frame's shared mark; (c) a frame left undisposed inside a `RunDetached` callback is dropped on return; (d) **4.5 obligation**: every production `Enter` is a `using` on the entering flow, disposed in order, never spanning a `yield return`, never entered inside an async helper whose frame outlives the helper; a runner creates and starts the work it reads through inside its `using` block (what a `Task`, `System.Threading.Timer`, registration or continuation created before the frame reads never reaches the frame's mark).
2. **4.1:** a `file:` name is recorded as written (never URL-parsed), so a URL-shaped one keeps its userinfo; the `EgressDecision` docs state the whole exception.
3. **4.2:** the hop is unconditional under `NETSTANDARD2_0` (not gated on the reflection probe), so the refusal does not depend on it.
4. **4.6:** the composed guard uses the strictest profile noted in the process; the opt-in variable is named only in the hedged `docs/EgressInventory.md` section; the startup line says "the guard refuses nothing yet" (4.2's runtime refusal exists beside it).
5. **Merge order** became 4.1 → 4.6 → 4.2 → 4.4 (4.4 was reworked; the four share no code).

**Open questions:**
- **Owner call due in phase C (4.5):** a read scope observes its result only when it ends, so a tool that reads and then egresses within one call is decided at the pre-read mark. Record it as a known limit for runners whose floor is below SystemHigh, or make an open, unreported scope count as SystemHigh (changes Scenario B's expected reason from LevelTooLow to SystemHighData). **Ask when 4.5 is built.**
- **Due in 4.7, integrator or owner:** the startup line when a host's own `IEgressGuard` is the container's guard (`AddAshlar` keeps it; the activator logs the composed guard's line): suppress, name the host guard, or keep it as the process mode. Depends on 4.7's route rule.
- Still open, not blocking: §8 Q1–Q6; SPEC-007 open questions A (receiver-side helper, `TryParse`), B (SystemHigh vs TopSecret; legacy flip in PR 5), E (SPEC number clash).

## 4. Plan and live status

| Phase | Scope | Ends when |
|---|---|---|
| A (done) | Design, owner decisions, wave A built and checked, the procedure | met |
| B (done) | Wave A fixed, integrated, merged: 4.1 #717, 4.6 #718, 4.2 #719, 4.4 #716 | met |
| **C (next)** | **4.3 (redirects), 4.5 (producers, report-only), 4.10 (AG and SW hygiene)** | All three merged; `done` posted; drift asks fixed or queued; master readiness `verified` |
| D | 4.7 (refusal surface on the routes), then 4.8 (catch-alls and trust-boundary exits) | Both merged |
| E | 4.9 (explicit sites — **17**, incl. EG-MDL-07 — operator verbs, child processes, CLI exit 77) | Merged |
| F | 4.11, the switch, with `EgressEnforcementLeakTests`; SPEC-007 shows PR 4 merged | Merged, master `verified` |
| G | Security follow-ups, the dead MEAI allow-list, the PR 5 design | Owner's choice |

**Branches.** The four lane branches were deleted on GitHub after their squash merges; their content is on master. Local helper branches in phase B's scratchpad clones (`wip/4.4-noskip`, `wip/4.4-round3-*`) held probe and superseded-rule work, were never pushed (lane rule), and are lost with the container. No branch from this workstream is in flight.

**Every work item:**

| PR | Content | Branch @ SHA | State |
|---|---|---|---|
| 4.1 | Host records | `claude/spec-007-pr4-4.1-records` | merged #717 `bbc5d71` |
| 4.6 | Mode plumbing | `claude/spec-007-pr4-4.6-mode` | merged #718 `3196ba11` |
| 4.2 | ns2.0 synchronous `Send` | `claude/spec-007-pr4-4.2-syncsend` | merged #719 `ad3d570b` |
| 4.4 | Frames | `claude/spec-007-pr4-4.4-frames` | merged #716 `de41a8ac8` |
| 4.3 | Redirects (design §4: R-a/R-b/R-c, per-authority re-evaluation, guard-handler re-insert, post-send check, both SNS sites, P2 for `EgressHttp` clients, IVT for Infrastructure). Done-when: stub, factory, differential, enforcement, rewrite and `Clear()` twins; seven mutations red. Relies on Q5. | – | phase C |
| 4.5 | Producers, report-only (self-extend frame at SystemHigh; `ToolCallingAgent` read scopes; RAGTool labels and "read nothing"; trim parity; peer responses observe SystemHigh; the floor-pinning convention). Done-when: the leak skeleton passes in report mode (after a Secret hit the next model call records `subject:agent:<id>`, Current = Secret, would-refuse LevelTooLow; an Internal hit allows). Relies on Q2, Q8, **and the 4.4 obligation (d)**. | – | phase C |
| 4.10 | AG and SW hygiene (defect 5 / D35 incl. vision no-escalate; options-bound validators; inbound per Q6). Done-when: an `AddAshlar(AirGapped)` cert-gate twin (overnight routing Local with the AG reason; vision never tries openai/azure; opt-in validators fail boot; ollama.com disabled; Full unchanged; MCP over HTTP on SW fails boot, stdio boots; a non-loopback listener on AG/SW fails boot). `[coordinated-integration]` if the API's listeners change. Relies on Q6. | – | phase C |

**Phase C, spelled out.**
1. Build 4.3, 4.5 and 4.10 in parallel lanes (one clone per lane; §5), each with an adversarial check.
2. Ask the owner the read-scope question (§3) **before** building 4.5's read-scope wiring.
3. Specific carry-ins:
   - **4.3:** 4.2's hop is an `HttpMessageHandler` with an internal `Inner` accessor; the redirect walker must step through it (or fold it into `EgressRedirectHandler` on ns2.0). The `Wrap` remarks say replacing `InnerHandler` voids the guarantee. The 4.1 known limit "a chain walker stops at the hop" closes or narrows here.
   - **4.5:** obligation (d); `ToolCallingAgent` wraps each tool call as `using var read = EgressSubject.BeginRead(); …; read.Complete();`, a labelled tool calls `read.Report(label)`, RAGTool's "read nothing" is `Report(SecurityLabel.Public)`; the floor-pin convention for production `Enter` calls; work created and started inside a runner frame keeps it for its whole life (limits (a)/(b)).
   - **4.10:** the 4.6 startup-line / module-set notes (a later `AddAshlar` still selects its own module set).
4. Merge serially. Suggested order 4.3 → 4.10 → 4.5 (4.5 waits on the owner's answer). Merge master in and recount after each merge.
5. **Phase C exit:** all three merged; `done` posted for each; Grok's drift asks fixed or queued; master readiness `verified` after the last merge (or the handoff says why not).

## 5. How to finish a lane (routine)

1. **Clone.** `git clone -q https://github.com/IanFrelinger/Ashlar <scratchpad>/<lane>` (from GitHub, not the host checkout). **One agent per clone** (two agents in one clone collided this phase).
2. **Fix / build.** Twin first; for a behaviour change commit the twin alone and show it red.
3. **Update.** Merge master in, resolve, recount (Certification count paragraph with `git ls-files`; TSV sums for row 64; the `EgressGuardConventionTests` floors comment, re-measured in the container), `git add`, then `python scripts/knowledge-graph/build-knowledge-graph.py`.
4. **Verify in the container only** (`scripts/test-in-container.sh --repo <clone>`): `bash scripts/run-cert-gate.sh` (net8.0); touched classes on net8.0 and net10.0; build-core when a project reference or TFM changes; Abstractions on ns2.0/net8.0/net10.0 when it changes; `scripts/ci/run-repo-gates.sh` with shellcheck on PATH — **all 26** must pass.
5. **Mutation-check** with `scripts/mutation-check.sh` on the committed head; quote each summary line verbatim in the PR body.
6. **Adversarial check** (at least two lenses: code/write-down, records/evidence), then repair; repeat until clean.
7. **Open the PR** from the body draft (`[coordinated-integration]` if non-test code under `application/` changes); then commit "(this PR)" → "(#N)" in SPEC-007 and push; subscribe.
8. **Post** `Kind: handoff`, `About: pr-<n>` on #695 (what changed, what was verified, which records it touches).
9. **Merge** (squash) on five green required checks unless Grok posted `block`; post `Kind: done` with the merge SHA. The next PR is now behind: merge master into it.

Parallelism: several lanes at once, merges serial. Phase B ran 2–4 agents at once on 4 CPUs; container runs slow down but work.

## 6. Environment facts

- **Docker:** the SessionStart hook restarts dockerd, writes the docker wrapper and builds `ashlar-devtest:local`. If docker is down: `CLAUDE_CODE_REMOTE=true bash .claude/hooks/session-start.sh`.
- **`scripts/test-in-container.sh`** needs a clone, tests committed state unless `--dirty`; never pipe it through `| head`; save logs.
- **Repo gates are 26** (#714 added `tests/scripts/handoff-scripts.test.sh`).
- **Shellcheck:** `pip install --target <scratchpad>/sc-py shellcheck-py==0.9.0.6`, put `<scratchpad>/sc-py/bin` on PATH.
- **`mutation-check.sh` INVALID … reason=red-no-tests** = the mutant did not compile (e.g. CS0162 under TreatWarningsAsErrors); pick another.
- **Background tasks die at 2 hours:** split long mutation batches.
- **Readiness:** a PR's best verdict is `partial` (Docker lanes run on push only); master pushes give `verified`. `scripts/release/readiness-verdict-for-sha.sh` refuses when `gh auth status` reports an invalid token; read the annotation instead: `gh api repos/IanFrelinger/Ashlar/check-runs/<id>/annotations` on the `Readiness summary` run.
- **A superseded head's `Readiness summary` reports `failure`** when a newer push cancels its lanes. That is not a real failure; check the current head.
- **Workflow hygiene (learned this phase):** stop a superseded workflow (TaskStop) before launching its replacement — a leftover agent pushed to a lane branch. A `SendMessage` to a workflow's agent detaches it from the workflow, so the script never receives its result; re-launch the downstream steps yourself.
- **Converting a PR to draft:** `mcp__github__update_pull_request` with `draft: true` works.
- **Cert-gate counts:** 2763 on the 4.4 head `91cf8d74` (which had master `ad3d570b` merged in), so about 2763 on master `de41a8ac`; recount from the next cert-gate run. Certification folder: 131 `.cs` files, 134 entries. TSV: 86 rows, 150 occurrences. Floors measured: 2,135 / 150 / 67.
- **xUnit 2.9.3** runs `DisableParallelization` collections (such as `EnvironmentVariables`) after the parallel ones; 4.6's process-global seams depend on that.
- **Known pre-existing failures** in Tests.Infrastructure (net8.0): `DogfoodBlock8Tests.ParallelTestMatrix…` and two `DogfoodBlock8ComposedTests`; `RuntimeStudioBlackBoxPlaygroundTests.Prod_cli_objectives…` is flaky under load.
- **Two copies of `Ashlar.Abstractions` in one process** (plugin ALCs): the second `Ashlar-Egress` EventSource goes dark; now a Known limit in `docs/EgressInventory.md` (4.2).

## 7. Queued work

**Carried from phase B (do first in phase C):**
- **drift-716:** clean, with nothing to fix. Still read #695 at the start of phase C for anything newer.
- **SPEC-007's 4.4 line** names `#716` but not its merge SHA `de41a8ac`. Add it in phase C's first PR, as #716 did for 4.2.

**Within PR 4:**
- **4.5:** obligation (d); the read-scope owner call; the floor-pin convention; `ToolCallingAgent`/RAGTool wiring (§4).
- **4.7:** define `Refuses` in terms of `EgressDecision.Refused`; the ILogger sink (event 7300) gains Mode, ModeBasis and Ref; event 7301 is reserved for EgressRefused; the startup-line question (§3).
- **4.9:** 17 explicit sites, including EG-MDL-07's cloud decision in `OllamaProvider`, which must refuse **before** `SendAsync` (the handler's own decision stays Host for a loopback daemon).
- **4.11:** 4.6 finding 6 (where `AddAshlar` never ran, `ProcessDefault` re-reads the profile per decision, so `SetEnvironmentVariable` can lower AG); composed guards keep their composition-time profile and override; a later `AddAshlar` still selects its own module set (4.10); the D6 twin list; `An_explicit_profile_is_reported_and_does_not_change_the_decision` already has an enforce row (4.11 flips its AG/SW rows).
- Follow-up idea: Ollama `/api/tags` `remote_host`/`remote_model` would identify cloud models more precisely than the name suffix; EG-MDL-11 does not apply the cloud rule.

**Outside PR 4 (owner's choice; the design recommends them):**
- Defect 1: the gRPC open relay (`AgentTransportServiceImpl.cs:173`) — needs its own security PR.
- Defect 2: the commercial Fleet task-result download (`CommercialFleetEndpoints.cs:495-499`) may allow arbitrary file reads.
- A deny-list on `mcp:` tool ids in `DataExfiltrationPolicy`; the dead MEAI cloud allow-list after 4.11.
- Side findings: the SNS signing-cert host check admits `*.amazonaws.com`; `CloudSanitizationProxy` skips sanitising on a wire-supplied `IsAirGapped`; `docs/Configuration.md:168` drift.
- **New this phase:** `OllamaProvider.BuildChatPayload` ends its JSON with one extra `}` (`"]}}"` is not interpolated). Ollama tolerates it; a strict parser does not. Queued as a task card ("Fix extra brace in OllamaProvider chat payload").
- Task cards queued earlier in the Claude app: orchestrator transport under non-Full profiles; A2A registry agents; `RemoteBrick` `hostBaseUrl` SSRF; agent `RequireLocalOnly` and the `mcp:` policy (PR 5); `ASHLAR_SHARED_ADAPTATIONS_PATH` ignored.

## 8. Ways of working the owner has endorsed

- One phase per thread, ending with `/handoff`.
- Lanes and checks: build in parallel lanes, each followed by adversarial checks (two lenses), repair until clean, then integrate, verify against a base, mutation-check, and merge serially.
- Ask the owner (AskUserQuestion, recommended option first) only for real decisions: policy, security posture, compatibility, or a default the owner accepted (D31 was asked for this reason). Otherwise take the fail-closed design default and record it in the PR body.
- PR bodies give real counts, say what was not observed failing, and quote each mutation's verbatim summary line.
- **Learned this phase:** for concurrency semantics, prefer the simplest rule with a one-line invariant, and back it with a model checker that is shown to catch a broken variant. When adversarial re-checks keep finding new edge cases in a clever rule, step back to the simpler fail-closed rule rather than patching again.
- Proceduralize repeatable work: commands, templates and tested scripts over memory.

## 9. Starting prompt for the next phase

> Execute **phase C** of spec-007-pr4. Run `/start-phase spec-007-pr4`, or read this handoff in full, starting with §0. Phase C builds and merges PR 4.3 (redirects), 4.5 (producers, report-only) and 4.10 (AG and SW hygiene): build them in parallel lanes with adversarial checks, ask the owner the read-scope question in §3 before wiring 4.5's read scopes, then merge serially (suggested 4.3 → 4.10 → 4.5), posting `handoff` and `done` on #695 for each. Phase C ends when all three are merged, Grok's drift asks are fixed or queued, and master's readiness verdict after the last merge is `verified`. Build and test only through `scripts/test-in-container.sh`. If anything the owner has not decided blocks you, ask instead of choosing. When the phase is done, run `/handoff spec-007-pr4 C` and notify me.
