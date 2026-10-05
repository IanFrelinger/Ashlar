# Ashlar handoff: SPEC-007 (security labels and the egress guard)

*Written 2026-10-05, 19:20 UTC, at the end of a long Claude Code cloud session. Start the next session with the prompt in §9.*

## 1. Where things stand

**Master is `79e988c31`.**

| PR | What | Merge commit |
|---|---|---|
| #706 | SPEC-007 PR 1: label lattice and `ReferenceMonitor` | `0f9642ec2` |
| #707 | PR 2: bridges from the existing labels | `f1f2cff48` |
| #709 | PR 3a: report-only egress guard, inventory, convention test | `c257aa684` |
| #710 | Dev workflow: SessionStart hook, MCR devtest image, SPEC-007 in the repo, `scripts/mutation-check.sh` | `ea0674e53` |
| #711 | PR 3b: every listed outbound site is routed to the guard, report-only | `8ec674d2a` |
| #712 | drift-711: the 3b merge SHA in SPEC-007 | `9abb491d3` |
| #713 | **The eight PR 4 owner decisions and the PR 4 plan**, in SPEC-007 | `79e988c31` |

- **Readiness:** master's readiness run for `8ec674d2a` is `verified`, with 4 of 4 lane groups. Grok confirmed it on the bus.
- **Drift audits:** Grok's audits of #710 and #711 are closed.
- **Nothing is open on the agent-bus** (issue #695). The last posts are `done pr-713` and `done drift-711`.

**PR 4 (the guard enforces) is in progress.** The design and every owner decision are done, and wave A is built (§4).

## 2. Read these first

**In the repo:**
- `CLAUDE.md`: the rules. Never run `dotnet` on the host. There are five required checks, merges are serial, and every behaviour change gets a mutation check.
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`: the owner's spec, verbatim, plus a status section, the decisions log (every owner decision, including the eight for PR 4), "Design as merged", and open questions A to E.
- `docs/EgressInventory.md`, `ci/egress-inventory.tsv` (85 rows) and `ci/cert-gate-assertions.md` row 64: the egress records.
- `_handoff/bus/PROTOCOL.md`: the agent-bus with Grok. Claude develops and merges; Grok audits each merge for drift.

**On the storage branch `claude/spec-007-pr4-workspace`, folder `_handoff/spec-007-pr4/`.** Do not merge this branch.
- **`DESIGN-4-final.md`: the authoritative PR 4 design** (about 1,180 lines). §4 is the PR split, §3.B holds the 43 engineering defaults, §5 the leak test, and the end the owner answers.
- `LANE-RULES.md`: the rules every PR 4 implementation lane follows.
- `BRIEF.md` and `scout-S1…S5.md`: the scouting behind the design.
- `pr-4.x-body.md`: the PR-body drafts the lanes wrote.

## 3. The PR 4 owner decisions (2026-10-05; the SPEC-007 decisions log)

| Q | Decision |
|---|---|
| Q1 | AirGapped (AG) and SecureWorkstation (SW) enforce when the switch PR (4.11) merges. SW alone has a break-glass, `ASHLAR_EGRESS_MODE=report`: read once at startup, logged at Warning, stamped on every decision. AG ignores every override. |
| Q2 | The runner declares a subject's floor, and only for inputs it built and can vouch for. `ToolCallingAgent` observes every tool result: a RAG hit counts at its tier, and an unreported read counts as SystemHigh. Self-extend runs at SystemHigh. |
| Q3 | Child processes that run agent-writable code (`dotnet` build, test, run, pack and publish; forge test; the regression runner; the instance spawner) are off-host. Docker is Host only with `--network=none`. On AG and SW they are refused unless sandboxed. |
| Q4 | Before PR 6, only file exports leave AG and SW hosts: `pkg export --out`, `export` and `mesh export`, in report mode as an operator verb. `pkg publish` and `share` are refused. |
| Q5 | Every factory client is enforced, the host's own included. A per-client opt-out goes through `Configure<EgressGuardOptions>`. AG ignores the opt-out, and naming one of Ashlar's own clients fails boot. |
| Q6 | On AG and SW, inbound surfaces stay on loopback. On SW, MCP over HTTP fails boot; stdio stays. |
| Q7 (open question D) | The refused subject gets the reason category, site, family, destination class and a random reference. Operators get the full `Detail` and the sequence number. Remote parties get a fixed text plus the reference. |
| Q8 (open question C) | Labels carry the level only. C3 normalisation is the rule for the first producer of a custom level. |

**Still open, and not blocking:** §8 Q1 to Q6, and open questions A (a receiver-side helper and `TryParse`), B (SystemHigh versus TopSecret visibility; its legacy-side flip belongs to PR 5) and E (the SPEC-007 number clash with SPEC-006:551).

## 4. The PR 4 plan and its live status

Why PR 4 is split: no production code enters an `EgressSubject` frame. Turning enforcement on alone would make AG and SW host-only, and the leak test would pass without a label causing the refusal. The switch therefore comes last.

| PR | Content | Needs | Status at handoff |
|---|---|---|---|
| 4.4 | Frame semantics: monotone nesting, `Observe` (only raises), `BeginRead` and `ReadScope`, a scope that throws observes SystemHigh, `Detach` at AgentBus | – | **Built.** Branch `claude/spec-007-pr4-4.4-frames` @ `5f56b8ff4`, one commit. Its adversarial check was still running. |
| 4.1 | Records that could read Host: `file:` is never Host, `mesh-peer:<ip>`, MEAI `ProviderUri` with fail-closed `meai:<key>`, the Ollama cloud rule, `OllamaHttpChatClient` no longer follows redirects | – | **Built.** Branch `claude/spec-007-pr4-4.1-records` @ `1e3e645d2`, 11 commits, not yet squashed. Check pending. Needs `[coordinated-integration]` (it touches MeshServeService). |
| 4.6 | Mode plumbing: `ResolveMode`, `ModeBasis`/`Refused`/`Ref`, the enforce opt-in, read-once latch, strictest-wins, a reset seam. Every profile still reports. | – | **Nearly done.** Branch `claude/spec-007-pr4-4.6-mode` @ `78f573e89` ("WIP: knowledge graph"), 14 commits. |
| 4.2 | netstandard2.0 synchronous `Send`: an `HttpMessageHandler` hop with a NoDecision record; `CreateDelegatingHandler` throws on .NET 5–7; an ALC twin | – | **Built.** Branch `claude/spec-007-pr4-4.2-syncsend` @ `25301b79a`, one commit. Check pending. |
| 4.3 | Redirects: known primaries set to no auto-redirect, an innermost `EgressRedirectHandler` that re-evaluates every hop, a factory filter, both SNS sites | Q5 ✔ | Not started |
| 4.5 | Producers, report-only: a SystemHigh self-extend frame, `ToolCallingAgent` read scopes, RAGTool labels, a floor-pin convention | Q2, Q8 ✔; after 4.4 | Not started |
| 4.7 | Refusal surface on the routes: `EgressRefusedException`, `Refuses`/`ThrowIfRefused`, handler and chat-client throws, fault handling | Q7 ✔; after 4.6 | Not started |
| 4.8 | Catch-alls (rows 1–29) and the trust-boundary exits (fixed text plus ref) | Q7 ✔; after 4.7 | Not started |
| 4.9 | The 16 explicit sites; operator verbs (Q4); child processes (Q3); CLI exit 77 | Q3, Q4, Q7 ✔ | Not started |
| 4.10 | AG and SW hygiene: defect 5 (AG routes locally, no escalation, boot validators, ollama.com off) and inbound per Q6 | Q6 ✔ | Not started |
| 4.11 | **The switch:** AG and SW enforce, the SW break-glass, the host opt-out, flip the "never refuses" twins by name, `EgressEnforcementLeakTests` (the §5 done-when) | Q1, Q5 ✔; after all others | Not started |

**Merge order:** 4.4 → 4.1 → 4.6 → 4.2, then 4.3, 4.5 and 4.10, then 4.7 → 4.8 → 4.9, and 4.11 last.

**Lane clones were in the old container's scratchpad, and they are gone.** The pushed branches are the only copies. When a branch has several commits, squash it before the PR (the repo convention is one squash-merge per PR).

## 5. How to finish a built lane

1. **Read the lane's check findings, if any.** They were not saved anywhere durable. If they are lost, re-run an adversarial review of the branch against `DESIGN-4-final.md` §4 for that PR.
2. **Clone, merge, rebuild.** `git clone` the repo into the scratchpad, check out the branch, and merge `origin/master` into it. Expect conflicts in `CHANGELOG.md`, the SPEC-007 status and `docs/EgressInventory.md`; resolve them by hand. Regenerate the knowledge graph after `git add`.
3. **Verify, in the container only** (`scripts/test-in-container.sh --repo <clone>`):
   - the full cert-gate, `bash scripts/run-cert-gate.sh` on net8.0;
   - the touched test projects on their target frameworks;
   - `scripts/ci/run-repo-gates.sh`, which must pass all of its gates (25 at the last run).
4. **Mutation-check** every behavioural change with `scripts/mutation-check.sh`, on the committed head.
5. **Open the PR** from `pr-4.x-body.md`, with `[coordinated-integration]` if it changes non-test code under `application/`. End the body with the session attribution lines.
6. **Post on the bus.** Post `Kind: handoff`, `About: pr-<n>` on #695, naming the records it touches. Subscribe to the PR.
7. **Merge** (squash) once the five required checks are green and Grok has not posted a `block`, then post `Kind: done` with the merge SHA.

## 6. Hard-won environment facts

- **Docker:** the cloud container restarts often. The SessionStart hook (`.claude/hooks/session-start.sh`) restarts dockerd, writes the docker wrapper at `/root/.ashlar-session/bin/docker` (proxy and CA) and builds `ashlar-devtest:local`.
  - **If docker is down:** `CLAUDE_CODE_REMOTE=true bash .claude/hooks/session-start.sh`.
- **Builds:** `scripts/test-in-container.sh` needs a **clone**, not a worktree, and tests only committed state unless `--dirty` is passed.
  - **Never pipe it through `| head`.** SIGPIPE kills the container run.
- **Cert-gate:** `scripts/run-cert-gate.sh` pins net8.0. For net10.0, run it with the TFM substituted (`sed`) inside a throwaway clone.
- **Concurrency:** the machine has 4 CPUs, and the Workflow tool caps concurrency at 2 agents per workflow. For parallelism, run two or three workflows side by side, and allow one container build per agent at a time.
- **Readiness verdicts:**
  - On a PR, the best verdict is `partial` (3 of 4), because the Docker API, quickstart and agent-server lane runs only on push.
  - Master push runs give `verified`.
  - Docs-only PRs skip the readiness lanes.
- **Convention tests:**
  - `EgressGuardConventionTests` (194 tests) pins every outbound site in `ci/egress-inventory.tsv`. A failure prints the observed rows in TSV form.
  - Tests that set process environment variables must follow `ProcessGlobalEnvironmentConventionTests`: the `[Collection("EnvironmentVariables")]` spelling, with the variables saved and restored.
- **No `gh` CLI:** use the GitHub MCP tools (load them through ToolSearch).
- **Shellcheck** is not installed by default. A pip shellcheck (0.9.0) was used in the old session.
- **Known pre-existing failures** in Tests.Infrastructure on net8.0 in the container: `DogfoodBlock8Tests.ParallelTestMatrix…` and two `DogfoodBlock8ComposedTests`. `RuntimeStudioBlackBoxPlaygroundTests.Prod_cli_objectives…` fails sometimes under load.

## 7. Other queued work (owner's choice)

**Security defects from the 3a audit** that are not in PR 4. The design recommends:
- **defect 1:** the gRPC server is an open relay (`AgentTransportServiceImpl.cs:173`). It needs its own security PR now.
- **defect 2:** the commercial Fleet task-result download may be an arbitrary file read (`CommercialFleetEndpoints.cs:495-499`).
- a small PR that puts a deny-list on `mcp:` tool ids in `DataExfiltrationPolicy`.

**Task cards queued in the old session** are listed in the Claude app. Several overlap PR 4:
- orchestrator transport under non-Full profiles;
- A2A registry agents;
- the Fleet arbitrary file read;
- `AdaptiveProviderFactory` cloud escalation (AG no-escalation is in 4.10);
- AG network registrations (folded into 4.10);
- gRPC open relay;
- `RemoteBrick` `hostBaseUrl` SSRF and a missing API key;
- agent `RequireLocalOnly` and `mcp:` policy (PR 5);
- the dead MEAI allow-list (after 4.11);
- MCP over HTTP under SW (folded into 4.10);
- `ASHLAR_SHARED_ADAPTATIONS_PATH` ignored.

**Side findings worth a task:**
- the SNS signing-cert host check admits `*.amazonaws.com`;
- `CloudSanitizationProxy` skips sanitising on a wire-supplied `IsAirGapped`;
- `docs/Configuration.md:168` drift.

## 8. Ways of working that the owner has endorsed

- Build PRs in parallel lanes, each followed by an adversarial check. Integrate, verify against a base, mutation-check, then run a final multi-lens review whose findings must survive refutation.
- Ask the owner (AskUserQuestion, recommended option first) only for real decisions: policy, security posture, compatibility, or anything the spec reserves to the owner.
- PR bodies state what was **not** observed failing and give real counts. Every mutation row carries its verbatim `mutation-check.sh` summary.

## 9. Starting prompt for the next session

> Read `handoff.md` (attached, or `_handoff/spec-007-pr4/` on branch `claude/spec-007-pr4-workspace`), then `CLAUDE.md` and `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`. Fetch `claude/spec-007-pr4-workspace` and read `_handoff/spec-007-pr4/DESIGN-4-final.md` §4, §3.B and the owner answers at its end. Then continue SPEC-007 PR 4. Finish the four built lanes (branches `claude/spec-007-pr4-4.4-frames`, `-4.1-records`, `-4.6-mode` and `-4.2-syncsend`): adversarially check each, merge master into it, verify in the container, mutation-check, open its PR, hand it off on #695 and merge on green, in the order 4.4 → 4.1 → 4.6 → 4.2. Then build 4.3, 4.5 and 4.10 in parallel lanes per `LANE-RULES.md`. Build and test only through `scripts/test-in-container.sh`. If anything the owner has not decided blocks you, ask instead of choosing.
