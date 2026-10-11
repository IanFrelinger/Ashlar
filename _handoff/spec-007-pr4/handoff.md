# spec-007-pr4 handoff: end of phases D–F (as executed by Codex and Cursor), start of phase G — for a fresh local thread

*Written 2026-10-11 ~02:50 UTC from the cloud thread, at the owner's request, so the work restarts in a thread on the owner's own machine. Start it with `/start-phase spec-007-pr4`, or with the prompt in §9. Storage branch `claude/spec-007-pr4-workspace`, folder `_handoff/spec-007-pr4/` (`bash scripts/handoff-fetch.sh --workstream spec-007-pr4 --list`).*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`): end a phase with `/handoff spec-007-pr4 <phase>`; start one with `/start-phase spec-007-pr4`.

**This handoff is unusual.** Phases D (4.7, 4.8), E (4.9) and F (4.11) were executed between 2026-10-09 and 2026-10-10 by Codex and Cursor agents with no handoff at the end of any of them; PR 5 has started (#748). The last handoff on the storage branch is Cursor's "end of phase C" of 2026-10-09 (`deaae7b06`). This file closes D–F as they happened and opens phase G. It was written while a review of those merges was still running in the cloud thread (§4 "Review in flight"); that review did not finish, so its findings are **not** in here — the local thread's first job is to finish it.

**Running locally (what changes from the cloud thread):**
- `CLAUDE.md` binds: **never run `dotnet` on the host**; build and test only in the devtest container through `scripts/test-in-container.sh` (or `.ps1`), which tests **committed** state of a **clone** (not a worktree). Build the image once with `scripts/ensure-devtest-image.sh`; #741 added PyYAML to it so `scripts/ci/run-repo-gates.sh` (now **28** gates) passes locally too; the host needs `python3`, `pyyaml` and `shellcheck` for the gates.
- The cloud session's scratchpad is gone. Everything that mattered is on the storage branch (§2). Clones go wherever you like; one agent per clone.
- `gh` must be authenticated with **issue-write** (the Cursor agents got HTTP 403 posting on #695; a token that can comment fixes the bus for everyone who uses it).
- The Claude Code Workflow tool and skills (`/start-phase`, `/handoff`, `/agent-bus`) work locally. The two-hour background limit was a cloud artefact.
- Commit attribution: use whatever the local Claude Code session is told to use; do not add Claude trailers to commits made by other agents.

## 1. Where things stand

- **Master:** `ac264559f` (#748, 2026-10-11 00:08Z). Every master head since `de41a8ac` has readiness `verified` (4 lanes) **except `3023ef1ea` (#744, 4.9): `verdict=failed lanes_ran=3`**, a Windows fixture failure repaired by #745 `d4dfbf379` about two hours later; `7cb132897` (the switch) and `ac264559f` are `verified`.
- **Merged since the phase B handoff (`de41a8ac`), in order:**

| PR | What | Merge | By |
|---|---|---|---|
| #720 | PR 4.10 AG/SW hygiene | `02fa27f1d` | Cursor (from the phase B handoff; missed the 2026-10-06 decisions) |
| #722 | PR 4.3 redirects | `904cf909a` (**merge commit**, 13 commits) | Cursor |
| #721 | PR 4.5 producers | `9b2ea56f4` | Cursor (shipped the opposite of the open-read decision) |
| #723 | CI: readiness no longer push-triggers on `cursor/**`; CLAUDE.md/CONTRIBUTING edits | `095ba46e2` | Cursor |
| #724 | R1 records honesty (three 2026-10-06 decisions-log rows; false stacking claim removed) | `de03fa7c6` | Cursor |
| #738 | 4.10b: HTTP_PORTS candidates, post-bind verifier, IPv6 loopback, multi-frame refusal, PostConfigure, conventions | `bceb1ba21` | Cursor (harvested from Codex) |
| #739 | 4.3b: O2 never-follow-into-Host, P2 by origin, filter order, differential twins | `9c56ecc1a` | Cursor (from Codex) |
| #740 | 4.5b: pinned read frame (open read ⇒ SystemHigh), ReadReporter, streaming observe, D16 scope | `bc1777dfc` | Cursor (from Codex) |
| #741 | Records reconciliation; PyYAML in the devtest image | `9f59d764a` | Codex, merged by the owner |
| #742 | **4.7** refusal surface on HTTP and MEAI routes (`EgressRefusedException`, events 7301/7305) | `50efd7019` | Codex, owner |
| #743 | **4.8** refusals preserved through catch-alls, fallback, transports; fixed remote text + ref | `6d64ad508` | Codex, owner |
| #744 | **4.9** explicit sites, operator exports (internal Initiator), child processes, exit 77 | `3023ef1ea` (readiness failed → #745) | Codex, owner |
| #745 | Windows Docker fixture repair (test-only) | `d4dfbf379` | owner |
| #746 | **4.11 the switch**: AG/SW enforce by default; SW break-glass; host opt-out list; `EgressEnforcementLeakTests` | `7cb132897` | Codex, owner |
| #748 | **PR 5 first slice**: clearances in `ashlar.policy.yaml`, skill `HighestLevel` on the sealed package, `SkillComposition.Decide` via `CanRead` | `ac264559f` | Cursor |

  Also merged 10-07/10-08 by Cursor: Dependabot #689–#693, #647, #698–#703; old cursor PRs #636, #637, #639, #643, #644.
- **Open:** Dependabot #728–#737 (incl. Grpc 2.76→2.84 ×3, Protobuf, Decompiler 8→11 major); Cursor drafts #749 (CLI exit codes), #750 (package field escapes), #751 (docker MissingMethod test); unmerged branches `cursor/spec007-411-records-4a72` and `codex/spec007-clearance-reads` (10-10). The three Claude lane branches `claude/spec-007-pr4-4.3-redirects` @ `4d8860c5`, `-4.10-agsw` @ `696d3819`, `-4.5-producers` @ `91d3b48c` still exist (their content was harvested by 4.3b/4.5b/4.10b per the Cursor handoff — confirm, then archive-tag and delete).
- **Agent-bus (#695): silent since 2026-10-06 17:28Z** — none of the fifteen SPEC-007 merges above was posted, Grok's drift audit has not run since #716. A fact-checked backfill is attached (`bus-backfill-final.md`: 15 `done` + 3 `status` + 1 `ask`, 19 comments); post it first (§4).
- **Decisions log (SPEC-007) integrity:** rows dated 2026-10-06 (D31; open read ⇒ SystemHigh; Q8 clarified/O1; O2) are the owner's. Rows dated **2026-10-08 (4.5b/D16 "the owner accepted the review recommendation")** and **2026-10-10 (PR 5 §8 Q3 clearances in policy; Q4 skill level in the sealed manifest)** were written by agents; the Cursor phase C handoff says Q1–Q9 were **not** answered and D16 was a default it took. **The owner confirms or strikes those three rows** before anything builds on them.
- **This phase's exit criteria:** phases D–F have no criteria of their own on record; against the design's: 4.7/4.8/4.9/4.11 merged ✔, master `verified` ✔, drift asks fixed or queued ✘ (never raised: the bus was silent), handoffs written ✘ (this file). **Not met on process; the code review that would settle "met on substance" is unfinished (§4).**

## 2. Read these first

**In the repo (master `ac264559f`):** `CLAUDE.md`; `docs/specs/SPEC-007-security-labels-and-reference-monitor.md` (status table rows 4 and 5; PR 4 bullets 4.3–4.11; §5; decisions log — the unattested rows above); `docs/EgressInventory.md`; `ci/cert-gate-assertions.md` (row 64, the count paragraph); `scripts/mutations/spec007-*.json` + `scripts/run-spec007-mutation-batch.py` (the mutation manifests Codex introduced; evidence lives there now); `_handoff/bus/PROTOCOL.md`; `_handoff/phases/README.md`.

**On the storage branch `claude/spec-007-pr4-workspace`, `_handoff/spec-007-pr4/`:**
- `handoff.md` (this), `handoff-phase-C.md` (Cursor's 2026-10-09 end-of-phase-C handoff: its §3 lists which owner questions were answered by defaults), `handoff-phase-B.md`, `handoff-phase-A.md`.
- `REVIEW-2026-10-08.md` + `review-lens-*.md`: the six-lens review of #720/#722/#721/#723 — its §3 blockers B1–B9, §5 drift list and §7 plan are what 4.10b/4.3b/4.5b/R1 answered; its §9 Q1–Q9 are still the open owner questions.
- `bus-backfill-final.md` (new): the verified `done`/`status`/`ask` comments for every unposted merge, PROTOCOL format, ready to paste on #695 under `From: claude`. Also `bus-backfill-2026-10-09.md` (Cursor's partial draft, superseded).
- `review-enforcement.js` (new): the six-lens review workflow for the 10-09..10-11 merges that was running when this handoff was written; re-run it locally with the Workflow tool (`Workflow({scriptPath})`) after fixing its `SP` constant to a local scratch dir and cloning master into `<SP>/review-master`; or use its six prompts as the checklist by hand.
- `PROTOCOL-draft-2026-10-11.md`, `bus-README-draft-2026-10-11.md` (new): the agent-bus amendments the owner asked for (identities `cursor|codex`, `Kind: claim`, task cards labelled `agent-task`, "decisions land in the log first", "read the newest handoff before starting"); drafted, **not landed** — the owner interrupted to write this handoff. The label `agent-task` exists on GitHub already.
- `INTEGRATION-NOTES-C.md`, `PHASE-C-LANE-BRIEF.md`, `verify-lane.js`, the attack/records lists, `phase-C-critic.md`, `state-4.3/4.10/4.5.md`, `DESIGN-4-final.md` (amended for the open-read decision; its `:605` multi-frame sentence is wrong and still uncorrected), `REPORT.md` + `frame-model-readscope.py` (the 4.5 model check), the logs and mutation archives: phase A–C material.

## 3. Decisions

**Owner decisions, recorded on master (attested):** 2026-10-05 Q1–Q8 (#713); 2026-10-06 D31 amendment (#716); 2026-10-06 open read scope ⇒ SystemHigh for every tool; Q8 clarified (custom RAG tiers are SystemHigh in PR 4; C3 deferred); O2 never follow a redirect into the host boundary (R1 #724; implemented by 4.5b #740 and 4.3b #739).

**Rows on master the owner has not attested (confirm or strike):** 2026-10-08 "PR 4 (4.5b, D16): governed responses from every non-model target are unlabelled reads until PR 5 — the owner accepted the review recommendation"; 2026-10-10 "PR 5 (§8 Q3): clearances are configured in `ashlar.policy.yaml`…"; 2026-10-10 "PR 5 (§8 Q4): a skill's highest level stays in the sealed package manifest…".

**Defaults the Cursor/Codex agents took where the owner had not decided (recorded as what master does; the owner can reverse):** Q1 mesh serve on AG/SW with no `ASHLAR_MESH_SERVE_BIND` → **fails boot** (Cursor's literal reading of Q6); Q4 multi-frame vision on AG → **refuses a cloud resolve** (4.10b); Q5 `AshlarResolvedDeploymentProfileOptions` → **`PostConfigure` reasserts the strictest profile** (4.10b); Q6/D16 → **every non-`local:`/`cloud:` target observes SystemHigh** (4.5b); PR 5's shape (composition-time clearance vs skill level) taken without a PR 5 design pass; the switch's "the existing runner still ends the failed tool cycle with `error`" (4.11 body) vs the design's D26 `egress_refused` — a deviation to confirm.

**Still open (owner):** Q2 merge method (everything since 10-08 is squash; #722 remains a merge commit; CONTRIBUTING's post-hoc exception text was removed by R1 — decide whether to set `allow_merge_commit=false`); Q3 bus identity for Cursor/Codex (the protocol draft answers it by adding `cursor|codex`; approve or amend); Q7 severities (moot after R1); Q8 who re-runs #722's seven mutations (manifest `spec007-redirects.json` exists; were they run?); Q9 archive the lane branches. Plus: the three unattested rows; whether PR 5 proceeds from #748's shape or from a design pass first (the phase G plan said "the PR 5 design — owner's choice").

**Integrator decisions of phase C (Claude), still binding where not superseded:** pinned read frame; basis = nearest live subject frame; open until disposed; G5 report-only surface; Bedrock composition-time refusal (not taken by master — master refuses at `ValidateOnStart`/first resolution; recorded); G9/G12 convention fact (landed in 4.10b).

## 4. Plan and live status

| Phase | Scope | Status |
|---|---|---|
| A, B | design; 4.1/4.6/4.2/4.4 | done |
| C | 4.3, 4.5, 4.10 + remediation (R1, 4.3b, 4.5b, 4.10b) | done (Cursor's handoff 2026-10-09) |
| D | 4.7, 4.8 | **merged by Codex (#742, #743), no handoff, unreviewed** |
| E | 4.9 | **merged by Codex (#744), no handoff, unreviewed; its master readiness failed once (repaired)** |
| F | 4.11 the switch | **merged by Codex (#746), no handoff, unreviewed; SPEC-007 says PR 4 merged** |
| **G (next)** | **finish the review of D–F; bus backfill; decisions-log integrity; protocol amendments; follow-ups from the review; PR 5 design decision; archive lane branches; release notes** | this file |

**Review in flight when this was written (lost with the cloud session):** six lenses over master `ac264559f` — conformance 4.7+4.8 (D20–D28, catch-all rows 1–29, redaction D-b, the 16+65 mutation manifests), conformance 4.9 (Q3/Q4, explicit-site convention, Docker/dotnet classification bypasses, operator Initiator reach, the readiness failure), conformance 4.11 (§5 scenarios/controls/mutations vs `EgressEnforcementLeakTests`, break-glass latch, opt-out semantics, D6, D26 deviation, `[skip-prod-style]`), remediation-and-PR5 (B1–B9 closed?, lane code harvested?, #748 vs the PR 5 scope, the unattested rows), write-down under enforcement (attack lists re-walked; refusal swallowed into success anywhere; opt-out and break-glass bypasses; process classification bypasses; Full/Dev/Test regressions), records/process (TSV/row 64/floors/count paragraph recount, KG byte-identity, CHANGELOG, Configuration/RELEASE_RUNBOOK/CONTINUITY, bus, phase procedure, merge method). Partial artefacts existed only in the cloud scratchpad (`review2/`); the two PR diffs and bodies it had pulled are reproducible from GitHub. **Re-run it** (attached script) — nothing in this handoff claims those merges are sound; only that CI and readiness are green.

**Phase G, spelled out, in order:**
1. **Post the bus backfill** (`bus-backfill-final.md`, 19 comments, `From: claude`), then ask Grok on the bus to audit #724–#748 (drift asks will follow; fix or queue them).
2. **Decisions-log integrity:** the owner confirms or strikes the three unattested rows; land the result as a one-row-each records PR (R1-style) before any PR 5 work.
3. **Finish the review of 4.7/4.8/4.9/4.11/#748** (the attached workflow, or its prompts by hand, two lenses with container probes). Turn blockers into follow-up PRs; majors into cards.
4. **Land the agent-bus amendments** from the two drafts (+ `CLAUDE.md` bus section, `.claude/commands/agent-bus.md`, `scripts/agent-bus-latest.sh` comment; repo gates; KG regen), then seed the first `agent-task` cards from step 3's findings and from §7.
5. **PR 5:** the owner decides design-first or continue from #748; either way the Known limit "inbound response bodies are not mediated" stays until the receiver-side `CanRead` lands.
6. **Housekeeping:** archive-tag and delete the three lane branches once step 3's remediation lens confirms harvesting; close or land `cursor/spec007-411-records-4a72`, `codex/spec007-clearance-reads`, #749–#751; Dependabot #728–#737 serially (run the gRPC refusal twins for the Grpc 2.84 bumps — 4.8 pinned a Grpc.Core.Api 2.76 behaviour, mutation `s55`); release notes for "AirGapped and SecureWorkstation now refuse by default" + the break-glass variable; `CONTINUITY.md` entry for PR 4 merged.
7. **End with `/handoff spec-007-pr4 G`.**

## 5. How to finish a lane (routine, local)

1. Clone from GitHub (`git clone -q https://github.com/IanFrelinger/Ashlar <work>/<lane>`), branch from `origin/master`; one agent per clone; never test in a worktree.
2. Twin first; a behaviour change is committed twin-alone and shown red in the container, then green.
3. Merge master in before verifying; recount (Certification count paragraph with `git ls-files src/Ashlar.Tests.Infrastructure/Tests/Certification`; TSV sums for row 64; the `EgressGuardConventionTests` floors comment from F6/F8 in the container); `git add -A`, then `python scripts/knowledge-graph/build-knowledge-graph.py`, commit.
4. Verify in the container: `bash scripts/test-in-container.sh --repo <clone> -- bash scripts/run-cert-gate.sh` (net8.0); touched classes on net8.0 and net10.0; Abstractions on netstandard2.0/net8.0/net10.0 when it changes; `build-core` when a project reference or TFM changes; `bash scripts/ci/run-repo-gates.sh` on the host — all 28 pass.
5. Mutation-check each behaviour (`scripts/mutation-check.sh`, or a manifest under `scripts/mutations/` run through `scripts/run-spec007-mutation-batch.py` — read both usages); quote every summary line verbatim in the body.
6. Adversarial check, two lenses (write-down; records/evidence); repair until clean.
7. Open the PR from the template (`.github/PULL_REQUEST_TEMPLATE.md`); `[coordinated-integration]` iff non-test code under `application/` changes; "(this PR)" → "(#N)" in SPEC-007; **post `claim` before, `handoff` when ready, `done` after merge** on #695.
8. Merge (squash) on the five required checks green unless Grok posted `block`; post `done` with the merge SHA; read the readiness verdict (`scripts/release/readiness-verdict-for-sha.sh`, or the `Readiness verdict` annotation on the `Readiness summary` check run).

## 6. Environment facts

- Five required checks on master: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`; strict + enforce_admins; merges serial.
- Repo gates: **28**; `run-repo-gates.sh` auto-discovers `tests/scripts/*.test.sh`, `scripts/ci/test-*`, `scripts/ci/verify-*`, `scripts/knowledge-graph/verify-*-current.py`; needs `shellcheck`, `python3`, `pyyaml` on the host.
- Readiness no longer push-triggers on `cursor/**` (#723); PR verdicts are `partial`; master pushes give `verified`; a superseded head's summary can read `failure`.
- Counts as of the last records PR (#741, master `bc1777dfc`): 2,148 production files / 149 occurrences / 86 rows / 48 guarded / 67 docs rows; 142 certification `.cs` / 145 entries; 4.7 then claimed 2,149/149/67 and 144/147; 4.9 changed 40 TSV lines; 4.11 added `EgressEnforcementLeakTests.cs`. **Recount before quoting any of them.** Cert-gate totals quoted by the PRs: 3,066 (4.7) → 3,101 (4.8) → 3,174 (4.9) → 3,240 (4.11).
- `[skip-prod-style]` tokens were used on #722, #743 and #746 (the testing-strategy gate accepts the token with a rationale); the ProdStyle twins the gate wanted do not exist.
- Known pre-existing failures: `DogfoodBlock8Tests.ParallelTestMatrix…`, two `DogfoodBlock8ComposedTests`; `RuntimeStudioBlackBoxPlaygroundTests.Prod_cli_objectives…` flaky under load; the Windows Docker fixture (fixed by #745).
- The cloud thread's webhook relay idea does not work: the session webhook rejects unsigned POSTs (401), so a GitHub Action cannot wake a Claude session that way. A scheduled Routine (minimum interval one hour) was proposed and **not created** (the owner declined when interrupted). Locally, the equivalent is `/loop /agent-bus` or a cron.
- The GitHub label `agent-task` exists (created 2026-10-11 02:4xZ, green, description points at PROTOCOL.md). Nothing else from the "tag-team via GitHub" proposal has landed.

## 7. Queued work

- Everything in §4 steps 1–6.
- From the 2026-10-08 review still open on master as far as is known without the finished review: mesh-serve default (owner Q1), `[skip-prod-style]` debt (ProdStyle twins for #743/#746), the D26 deviation in 4.11, the `DESIGN-4-final.md:605` multi-frame sentence, the unattested decisions-log rows.
- PR 5 proper: receiver-side `CanRead` at the server seams, inbound response mediation (the standing Known limit), SPEC-007 open questions A (receiver-side helper, `TryParse`), B (SystemHigh vs TopSecret legacy flip), E (SPEC number clash); #748's composition-time clearance check is a slice, not the whole.
- Follow-ups outside PR 4 (from phase B/C scouts; owner's choice): Bedrock SDK HTTP through `EgressHttp`; custom credential headers following an allowed hop; UDP discovery, the gRPC host and Fleet.Host inbound surfaces under Q6; EG-MDL-08 video on AG; the MCP SDK send flow; the gRPC open relay (`AgentTransportServiceImpl.cs:173`); the commercial Fleet task-result download; a deny-list on `mcp:` tool ids; the dead MEAI cloud allow-list after 4.11; `CloudSanitizationProxy` skipping on a wire-supplied `IsAirGapped`; `OllamaProvider.BuildChatPayload` extra `}`; the earlier task cards (orchestrator transport under non-Full profiles; A2A registry agents; `RemoteBrick` SSRF; agent `RequireLocalOnly`; `ASHLAR_SHARED_ADAPTATIONS_PATH`).

## 8. Ways of working the owner has endorsed

- One phase per thread, ending with `/handoff`; a pause is also a handoff.
- Lanes and checks: parallel lanes, each followed by adversarial checks (two lenses), repair until clean, then integrate, verify, mutation-check, merge serially.
- Ask the owner only for real decisions (policy, security posture, compatibility, an accepted default); otherwise take the fail-closed default and record it as an integrator decision in the PR body — **never as an owner row in the decisions log**.
- PR bodies quote real counts, say what was not observed failing, and quote each mutation's verbatim summary line.
- For concurrency semantics: the simplest rule with a one-line invariant, backed by a model checker shown to catch a broken variant.
- More parallel capacity when asked: more clones, one agent per clone, bounded container concurrency; scouts (attack lists, records checklists, model checks, a completeness critic) beside the builders.
- **Learned 2026-10-06..11:** claim before you build; read the newest handoff and the decisions log before you start; land the owner's decisions as rows before the code; when several agents work the same repo, the bus is the only record that stops a double build — keep it alive (fix the 403, post every `done`).

## 9. Starting prompt for the local thread

> Execute **phase G** of spec-007-pr4 on this machine. Run `/start-phase spec-007-pr4` (it fetches this handoff from `claude/spec-007-pr4-workspace`), read it in full starting with §0, and check §1 against the live repo (master `ac264559f`; the open PRs; #695 silent since 2026-10-06; the three unattested decisions-log rows). Then, in order: post `bus-backfill-final.md` on #695 as `From: claude` and ask Grok to audit #724–#748; put the three unattested decisions-log rows to me to confirm or strike and land the answer as a records PR; finish the review of 4.7/4.8/4.9/4.11/#748 (run `review-enforcement.js` with the Workflow tool after pointing its `SP` at a local scratch dir and cloning master into `<SP>/review-master`, two lenses may run containers) and turn its blockers into follow-up PRs and its majors into `agent-task` cards; land the agent-bus amendments from the two drafts; then put the PR 5 design question to me. Build and test only through `scripts/test-in-container.sh`; never run `dotnet` on the host. If anything the owner has not decided blocks you, ask instead of choosing. When phase G is done, run `/handoff spec-007-pr4 G` and notify me.
