# State of lane 4.3 (SPEC-007 PR 4, redirects) at the 2026-10-06 pause

Read-only reconstruction, written 2026-10-06 ~20:50Z for the agent that resumes this lane. Every claim below was
checked against the lane clone, its logs and the drafts; nothing was built, run or edited. Paths:

- `$SP` = `/tmp/claude-0/-home-user-Ashlar/a81481a5-34f6-5b00-a61f-74b03c502c7b/scratchpad`
- lane clone: `$SP/c-4.3` (branch `claude/spec-007-pr4-4.3-redirects`); logs: `$SP/logs-4.3/`; mutations: `$SP/mut/`
- PR body draft: `$SP/ws/pr-4.3-body.md` (214 lines); scouts: `$SP/ws/attack-4.3.md`, `$SP/ws/records-4.3.md`;
  critic: `$SP/ws/phase-C-critic.md`; design: `$SP/ws/DESIGN-4-final.md`; squash message: `$SP/ws/squash-msg-4.3.txt`
- `file:line` with no prefix is a path in `$SP/c-4.3` at HEAD `4d8860c5`.

**One-paragraph verdict.** The code and records are complete for every bullet of DESIGN §4 row 4.3 and for owner
decision O2, and the branch is pushed (local head = remote head). It is **not PR-ready**: (1) the generated
knowledge graph at HEAD is stale (`shell-lint` would fail: regenerating it in a scratch clone changes
`docs/knowledge-graph.{json,md}`, declared facts 4905 → 4910); (2) the second commit `4d8860c5` (O2 rule, cancellation,
disposal, late-primary twins) has **no test-run evidence at all**: every green run and the full cert-gate are from
`906bdda7` or earlier, and the only run started at `4d8860c5` (mutation m7) was killed during the build; (3) only 6 of
25 prepared mutations have run (all KILLED, all at ref `906bdda7`, before the second commit); (4) the body still
holds the `{{MUTATIONS}}` placeholder, a "Resume state" preamble, and claims ("one commit on top of master",
"full cert-gate on the pushed head", "every behaviour mutation-checked") that are false at `4d8860c5`.

---

## 1. Branch and commits

| Item | Value | Source |
|---|---|---|
| Branch | `claude/spec-007-pr4-4.3-redirects` | `git branch --show-current` in `$SP/c-4.3` |
| Local HEAD | `4d8860c5b1583f73b03811a1d85993450a94da22` | `git rev-parse HEAD` |
| Pushed head | `4d8860c5b1583f73b03811a1d85993450a94da22` (**equal**) | `git rev-parse origin/claude/spec-007-pr4-4.3-redirects`; confirmed live with `git ls-remote origin` |
| Working tree | clean (`git status --porcelain` empty) | |
| Base / merge-base | `de41a8ac8d0d1ae94f075c7aba2dde366c890fec` = `origin/master` = live `master` (`ls-remote`) | master has not moved since the phase started |
| Commits on top of master (oldest first) | `906bdda7` "SPEC-007 PR 4.3: every redirect hop is decided before it is sent (R-a, R-b, R-c; D32, D33; Q5)" 2026-10-06 19:29Z; `4d8860c5` "feat(egress): never follow a redirect into the host boundary (owner O2); cancellation, disposal and late-primary twins" 2026-10-06 20:38Z | `git log origin/master..HEAD` |
| Trailers | both commits end with `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` and `Claude-Session: https://claude.ai/code/session_017TjtzCLb6VaYxD1MtRJ1ja` | `git log -1 --format=%B` each |
| Total diff stat | 33 files, +2745 / -105 | `git diff --stat origin/master..HEAD` |
| Second commit alone | 9 files, +338 / -40: `CHANGELOG.md`, `ci/cert-gate-assertions.md`, `docs/EgressInventory.md`, SPEC-007, `EgressDestinations.cs`, `EgressHopEvaluation.cs`, `EgressRedirectHandler.cs`, `EgressRedirectDifferentialTests.cs`, `EgressRedirectTwinTests.cs`; **no** `docs/knowledge-graph.*` | `git show --stat 4d8860c5` |
| Sibling lane heads (live) | 4.10 `claude/spec-007-pr4-4.10-agsw` @ `696d3819`; 4.5 `claude/spec-007-pr4-4.5-producers` @ `91d3b48c` | `git ls-remote origin` |
| Open PR for this branch | unknown: `gh pr list` is refused here (GraphQL not available); use `gh api repos/{owner}/{repo}/pulls?head=...` | |

History: `906bdda7` is the squash of eight lane commits (`824fff1c` twins red-first, `bba4d240` implementation,
`f0c8fe1b` records, `b9785a55` Bedrock twin + floors + KG, `b32c7522` docs, resume commits `30588c74`, `b862c967`,
KG), per the body's Resume state (`pr-4.3-body.md:9-17`). Those SHAs exist only in the logs' names now.

Files changed vs master (from the diffstat): production `src/Ashlar.Abstractions/Security/Egress/{EgressDestinations,
EgressGuard,EgressGuardHandler,EgressHopEvaluation(new),EgressHttp,EgressRedirectHandler(new),
SynchronousSendRefusedOnNetstandard20Asset}.cs`, `Ashlar.Abstractions.csproj`, `src/Ashlar.Infrastructure/Egress/
{EgressHttpClientBuilderExtensions(new),EgressRedirectFilter(new),EgressServiceCollectionExtensions}.cs`,
`src/Ashlar.AI.Pipeline/Clients/AwsBedrockChatClientFactory.cs`, `Ashlar.AI.Pipeline.csproj`,
`src/Ashlar.Transport.Grpc/DefaultGrpcChannelFactory.cs` (comment), `application/src/Ashlar.API/Program.cs:171`,
`commercial/src/Ashlar.Commercial.Fleet.Host/Program.cs:79`; tests `EgressRedirectTwinTests.cs` (new, 942 lines),
`EgressRedirectDifferentialTests.cs` (new, 503), `EgressHttpNetstandard20TwinTests.cs`, `EgressHttpHandlerTwinTests.cs`,
`EgressGuardChatClientTwinTests.cs`, `EgressGuardConventionTests.cs`, `Tests/VirtualProduction/EgressApiHostProdStyleTests.cs`,
`commercial/tests/.../EgressFleetHostTwinTests.cs`, `src/Ashlar.Tests.Transport/DefaultGrpcChannelFactoryGapCoverageTests.cs`;
records `CHANGELOG.md`, `ci/cert-gate-assertions.md`, `ci/egress-inventory.tsv`, `docs/EgressInventory.md`,
`docs/SdkCompatibilityPolicy.md`, `docs/knowledge-graph.{json,md}`, SPEC-007.

## 2. Implemented vs DESIGN §4 row 4.3 (`DESIGN-4-final.md:853`; §2.6 Gap 2 `:503-565`)

| # | Row bullet | Status | Where |
|---|---|---|---|
| 1 | **R-a** every primary Ashlar builds or binds stops following | **done** | `EgressRedirectHandler.Install` `EgressRedirectHandler.cs:104` walks to the primary and flips `HttpClientHandler`/`SocketsHttpHandler` (originals in a `ConditionalWeakTable`); installed by `EgressHttp.cs:157` (P2) and `EgressRedirectFilter.cs:80` (P1); started/credentialed/unknown primaries left alone (Known limit `docs/EgressInventory.md:24`) |
| 1a | R-a: Bedrock, if the SDK exposes the setting | **done** | `AwsBedrockChatClientFactory.cs:38` builds over `RuntimeConfig(region)`; `:47-49` `AllowAutoRedirect = false` |
| 1b | R-a: both SNS signing clients never follow (configure-existing overload) | **done** | `application/src/Ashlar.API/Program.cs:171` and `commercial/src/Ashlar.Commercial.Fleet.Host/Program.cs:79` chain `.NeverFollowRedirects()`; `EgressHttpClientBuilderExtensions.cs:25` (public, `Ashlar.Infrastructure`) |
| 1c | R-a: Ollama client gets no follower (4.1 did the flip) | **done (record only)** | EG-MDL-01 row `docs/EgressInventory.md:71`; CHANGELOG cross-reference `CHANGELOG.md:247-250` |
| 2 | **R-b** internal `EgressRedirectHandler`, innermost, runtime parity, both `SendAsync`/`Send` | **done** | `EgressRedirectHandler.cs:45` (class), `:365` `NextHop` (300/301/302/303/307/308, relative `Location`, fragment, no `https`→`http`, non-http scheme returned, method rewrite, `Authorization` cleared, intermediate response disposed, same request mutated, primary's limit); `Send` override under `#if NET5_0_OR_GREATER` |
| 3 | **Per-authority re-evaluation** (hops 2..n and rewriting handlers) | **done** | `EgressHopEvaluation.cs:63` `Begin`, `:76` `EnsureDecided`, `:126` `AuthorityOf` (= `SchemeAndServer \| StrongPort`); guard handler calls `Begin` at `EgressGuardHandler.cs:63`/`:74` |
| 4 | **Post-send check** (unknown primary follows itself) | **done** | `EgressHopEvaluation.cs:114` `CheckAfterSend`, called at `EgressGuardHandler.cs:76`/`:84` |
| 5 | **R-c** factory binding: `IHttpMessageHandlerBuilderFilter`, re-insert the guard handler with a Warning | **done, with deviation** | `EgressRedirectFilter.cs:28`; presence check (not `[0]`) at `:68`; Warning 7303 `EgressGuardHandlerRestored` `:31,:38`; Warning 7304 `EgressUnknownPrimaryHandler` on AG/SW `:34,:45`; registered with `TryAddEnumerable` at `EgressServiceCollectionExtensions.cs:70`; deviation 3 in the body (`pr-4.3-body.md:108`) |
| 6 | **Both SNS sites** `[coordinated-integration]` | **done** | as 1b; body section `pr-4.3-body.md:142-144` carries the literal token and rationale |
| 7 | **P2 for `EgressHttp` clients** (D33), P1 for factory clients | **done** | `followsAcrossOrigins: false` at `EgressHttp.cs:157`, `true` at `EgressRedirectFilter.cs:80`; check at `EgressRedirectHandler.cs:383` |
| 8 | **IVT Infrastructure** (D9) | **done** | `src/Ashlar.Abstractions/Ashlar.Abstractions.csproj:35`; plus `Ashlar.AI.Pipeline.csproj:25` → `Ashlar.Tests.Infrastructure` (new, for the Bedrock config twin) |
| 9 | **CHANGELOG / release notes** (behaviour changes) | **done** | `CHANGELOG.md:198-246` (`### Changed`): five bullets; release notes come from the CHANGELOG (no release-notes file, records R49) |
| 10 | Twins named by the row: stub, factory, differential, enforcement, rewrite, `Clear()` | **done** | `EgressRedirectTwinTests.cs` (24 `[Fact]` + 1 `[Theory]` × 4 rows = 28 cases at HEAD; 21 at `906bdda7`), `EgressRedirectDifferentialTests.cs` (6 facts + 1 `MemberData` theory, 23 cases) |
| 11 | **Seven mutations red** | **partial** | 25 prepared, **6 run** (KILLED, ref `906bdda7`); see §4 |
| 12 | **Owner decision O2** (not in the design; integrator instruction (e)) | **done in code and records; not run** | `EgressRedirectHandler.cs:344` `InsideHostBoundary`, `:376-381` inward rule + `RecordUnfollowed` (`EgressHopEvaluation.cs:98`); `EgressDestinations.cs` `IsInsideHost`, `IsLinkLocalHost`; twins §6(e) |
| 13 | Carry-in: ns2.0 hop walk | **done** | walker steps through the hop's `Inner` (`EgressRedirectHandler.Install`); twin `EgressHttpNetstandard20TwinTests.cs:151`; shape twin `:133` reads `Guard > Hop > Follower > primary` |
| 14 | Carry-in: Known limit `docs/EgressInventory.md:23` narrows | **done** | `:23` now "a chain walker **outside Ashlar** stops at the synchronous-send hop" |
| 15 | Carry-in: SPEC-007 4.4 line gains `de41a8ac` | **done** | SPEC-007 `:109` "**PR 4.4** (#716, `de41a8a`)" |
| 16 | Critic G1–G4 (pulled forward) | **done in code/records; G1–G3 twins never run, m24/m25 not run** | G1 twin `Wrap_over_a_delegating_handler_whose_inner_is_set_later…` + Known limit `docs/EgressInventory.md:24` ("no primary yet"); G2 `An_intermediate_redirect_response_is_disposed…`; G3 `A_token_cancelled_after_the_first_hop…` (`EgressRedirectHandler.cs:389-393`); G4 `docs/EgressInventory.md:15` now reads "A hop can change the destination class, not only the recorded host …" (the old sentence is gone: `grep "only the recorded host is wrong"` finds nothing) |

Nothing in the row is "not started".

## 3. Tests and runs (every log in `$SP/logs-4.3/`; head named in each log's first line)

| Log | Head tested | Command (abridged) | Verbatim summary | Current head? |
|---|---|---|---|---|
| `build1.log` | `de41a8ac` + uncommitted | `dotnet build` Abstractions / AI.Pipeline | `error CS0160` ×2 and `error CS8601` at `EgressRedirectHandler.cs(201,16)/(243,20)/(194,35)` (first draft; fixed before `build2`) | superseded |
| `build2.log` | `de41a8ac` + uncommitted | `dotnet build` Abstractions, AI.Pipeline, API, Fleet.Host | `Build succeeded.` ×4 (3 TFMs for Abstractions) | stale: re-run on `4d8860c5` |
| `red-first-824fff1c.log` | `824fff1c` (twins only on master) | `dotnet test` Infrastructure net8.0, filter `EgressRedirectTwinTests\|EgressRedirectDifferentialTests\|EgressHttpNetstandard20TwinTests`; API prod-style net10.0; Fleet twins net10.0 | `Failed!  - Failed:    18, Passed:    27, Skipped:     0, Total:    45` (net8.0); `Failed!  - Failed:     1, Passed:     3, … Total:     4` (API, net10.0); `Failed!  - Failed:     1, Passed:     1, … Total:     2` (Fleet, net10.0); 13 `EgressRedirectTwinTests`, 4 differential, 1 ns2.0 red by name | red-first evidence for the first 45 cases (holds) |
| `green1-f0c8fe1b.log` | `f0c8fe1b` | 11 egress classes net8.0 and net10.0; API; Fleet | `Total tests: 481` / `EXIT-net8.0=0`; `Total tests: 481` / `EXIT-net10.0=0`; `Passed! … Total: 4` (API net10.0); `Passed! … Total: 2` (Fleet net10.0); `ScannedFiles=2139 ExaminedOccurrences=150` (pre-Bedrock change) | stale |
| `kg.log` | host, tree at `b9785a55` | `build-knowledge-graph.py` | `declared_facts: 4896` | stale |
| `repo-gates-b9785a55.log` | `b9785a55` (host) | `scripts/ci/run-repo-gates.sh` | `repo-gates: all 26 gate(s) passed` | stale |
| `certgate-b9785a55.log` | `b9785a55` | `bash scripts/run-cert-gate.sh` | `Total tests: 2802`; `cert-gate reported 2802 tests (expected>=2797 …)`; `skip guard: 0 skipped`; one `[FAIL]`: `EgressGuardChatClientTwinTests.CloudBedrock_TheRuntimeClientNeverFollowsARedirect(region: "")`; no `EXIT-certgate` line (container restart) | superseded |
| `run1-net8-30588c74.log` | `30588c74` | 11 classes net8.0 (+ adversarial twins); `Ashlar.Tests.Transport` net8.0 | `Total tests: 499`, `EXIT-net8=1` (one `[FAIL]`: `A_factory_client_follows_a_remote_first_hop_into_the_host_boundary_which_the_label_model_allows`, wrong premise, since deleted); `ScannedFiles=2139 ExaminedOccurrences=149`, `DocsRows=67`; `Passed!  - … Passed:     7, … Total:     7 … Ashlar.Tests.Transport.dll (net8.0)` `EXIT-transport=0` | stale |
| `run2-b862c967.log` | `b862c967` | Abstractions build 3 TFMs; `EgressRedirectTwinTests` net8.0; 11 classes net10.0; API; Fleet | `Build succeeded.` `EXIT-abstractions-build=0` (no warning line); `Passed! … Passed: 21 … Total: 21` (net8.0); `Total tests: 499` `EXIT-net10=0`; `Passed! … Total: 4` `EXIT-api=0`; `Passed! … Total: 2` `EXIT-fleet=0` | stale |
| `repo-gates-resume-b862c967.log` | `b862c967` tree + regenerated KG (host) | `run-repo-gates.sh` | `repo-gates: all 26 gate(s) passed` | stale (KG now differs) |
| `certgate-906bdda7.log` | **`906bdda7`** (= first commit on the branch) | `bash scripts/run-cert-gate.sh` (net8.0) | `Total tests: 2818`; `cert-gate reported 2818 tests (expected>=2813, derived from --list-tests).`; `cert-gate skip guard: 0 skipped (total=2818 executed=2818)`; `EXIT-certgate=0` | **stale: re-run on `4d8860c5`** (or the squashed final head). Expected total there ≈ 2818 + 7 (twin file 21 → 28 cases) + 1 (differential) = 2826; measure |
| `mut-m1…m6-*.log` | `906bdda7` (clone of ref) | `scripts/mutation-check.sh` | see §4 | stale ref |
| `mut-m7-authorization-not-cleared.log` | `4d8860c5` | `mutation-check.sh` m7 | mutation applied (`1 file changed, 1 deletion(-)` at `EgressRedirectHandler.cs:398`); RED run reached `Ashlar.Bricks.SqlProfile -> …` then `Terminated`. **No summary: killed run** | re-run |

Summary of what is and is not verified on the current head `4d8860c5`:
- **Nothing has run green on `4d8860c5`.** The 9 new/changed test cases of the second commit (O2 theory 4 rows, O2
  factory-still-follows fact, G1, G2, G3 facts, differential inward fact) have never executed anywhere; the second
  commit's production changes (`NextHop` inward rule and cancellation check, `RecordUnfollowed`, `IsInsideHost`,
  `IsLinkLocalHost`) were never compiled in a logged run (m7's build was interrupted before Abstractions tests built).
- Red-first evidence holds for the first 45 cases (18/45 red at `824fff1c`). The adversarial twins added on resume
  and the second commit's twins were never run at the base; their red evidence is meant to be the mutations
  (m19–m26, m1, m5, m21, m22; body `:99`), of which only m1 and m5 have run.
- Full cert-gate: 2818/2818 at `906bdda7` only. Net10.0: 499/499 at `b862c967`. Abstractions 3-TFM build: clean at
  `b862c967`. `build-core`: never run (body `:91` argues no project reference or TFM changed; the IVT edits are
  csproj edits, so the next agent should run it once or state the argument in the body). Repo gates: 26/26 at
  `b862c967` + KG; **would now fail** (stale KG). `Ashlar.Tests.Transport`: 7/7 at `30588c74`. `MeshLanPartyTests`
  (`application/src/Ashlar.Tests.CLI/Tests/Commands/MeshLanPartyTests.cs:252` `PeerPull_doesNotFollowRedirects_noSsrf`):
  never run.

## 4. Mutations (`$SP/mut/mk.py` prepares 25: m1–m15, m17–m26; no m16; batches `batch-a.sh` m1–m8, `batch-b.sh` m9–m17, `batch-c.sh` m18–m22, `batch-d.sh` m23–m26; old/new text in `$SP/mut/t/`)

Verbatim lines in `$SP/logs-4.3/mutations-summary.txt` (all ref `906bdda7`, filter = the five redirect-related classes, Infrastructure net8.0, 85 tests selected):

```
mutation m1-per-authority-decision-dropped: KILLED red=failed:9/85 green=passed:85/85 ref=906bdda74e6899b0878fc81e40af7a10ad88007f
mutation m2-egresshttp-installs-no-follower: KILLED red=failed:11/85 green=passed:85/85 ref=906bdda74e6899b0878fc81e40af7a10ad88007f
mutation m3-factory-filter-installs-no-follower: KILLED red=failed:29/85 green=passed:85/85 ref=906bdda74e6899b0878fc81e40af7a10ad88007f
mutation m4-guard-handler-not-restored: KILLED red=failed:1/85 green=passed:85/85 ref=906bdda74e6899b0878fc81e40af7a10ad88007f
mutation m5-p2-cross-origin-followed: KILLED red=failed:2/85 green=passed:85/85 ref=906bdda74e6899b0878fc81e40af7a10ad88007f
mutation m6-https-to-http-followed: KILLED red=failed:2/85 green=passed:85/85 ref=906bdda74e6899b0878fc81e40af7a10ad88007f
```
(no `BATCH-a DONE` line: batch-a was interrupted at m7.)

| Mutation | What it breaks | Status |
|---|---|---|
| m1–m6 | per-authority compare; `EgressHttp` follower; filter follower; guard re-insert; P2 cross-origin; https→http | KILLED at `906bdda7`. The second commit changed `NextHop` and `EgressHopEvaluation`, so the lines name a tree that differs from HEAD; re-run on the final head (all 25 old strings still occur exactly once at HEAD: checked with `git show HEAD:<file>`) |
| m7 `Authorization` not cleared | `EgressRedirectHandler.cs:398` | started at `4d8860c5`, **killed during build** (`Terminated`); re-run |
| m8 post-send check dropped (async) | `EgressGuardHandler.cs` | not run |
| m9 original setting forgotten | `EgressRedirectHandler.cs` | not run |
| m10 method rewrite dropped (300/301/302) | `EgressRedirectHandler.cs` | not run |
| m11 walker stops at the ns2.0 hop | filter `EgressHttpNetstandard20TwinTests` only | not run |
| m12 credentials ignored | `EgressRedirectHandler.cs` | not run |
| m13 API SNS follows (`--framework net10.0`, filter `EgressApiHostProdStyleTests`) | `application/src/Ashlar.API/Program.cs` | not run |
| m14 Fleet SNS follows (`--framework net10.0 --project commercial/tests/Ashlar.Commercial.Tests.Fleet.Host/…csproj`) | `commercial/…/Program.cs` | not run |
| m15 Bedrock follows (filter `EgressGuardChatClientTwinTests`) | `AwsBedrockChatClientFactory.cs` | not run |
| m17 unknown-primary Warning inverted | `EgressRedirectFilter.cs` | not run |
| m18 redirect limit off by one | `EgressRedirectHandler.cs` | not run |
| m19 no note treated as decided | `EgressHopEvaluation.cs` | not run |
| m20 hop decided by `ProcessDefault` | `EgressHopEvaluation.cs` | not run |
| m21 started primary as if flipped | `EgressRedirectHandler.cs` | not run |
| m22 known primary by exact type | `EgressRedirectHandler.cs` | not run |
| m23 composite primary skipped | `EgressRedirectFilter.cs` | not run |
| m24 cancellation between hops ignored (G3) | `EgressRedirectHandler.cs:389-393` (deletion; `.new` is empty on purpose) | not run |
| m25 intermediate response not disposed (G2) | `EgressRedirectHandler.cs` | not run |
| m26 follower follows into the host boundary (O2) | `EgressRedirectHandler.cs:378` | not run |

INVALID: none so far. Risks to watch: m8 removes the post-send check only on the async path (the `Send` path keeps
it), so if every twin that pins the post-send check also exercises `Send` on net8.0 it could SURVIVE; m17 inverts a
Warning predicate that only `On_AirGapped_an_unknown_factory_primary_is_named_in_a_warning_and_on_Full_it_is_not` pins.
A SURVIVED result is a finding to record, not to hide.

## 5. Records (at HEAD `4d8860c5`)

| Record | Status | Evidence |
|---|---|---|
| SPEC-007 status bullet | **done, placeholder** | `:129-144` `**PR 4.3** (this PR) …` incl. the O2 sentence `:140-141`; `(this PR)` → `(#N)` once the PR is opened |
| SPEC-007 gaps paragraph | done | `:48-49` "which PR 4.3 closes (each hop is decided before it is sent)" |
| SPEC-007 4.4 merge SHA | done | `:109` `(#716, \`de41a8a\`)` |
| SPEC-007 decisions-log intro | done | `:423` "the PR 4.2 and PR 4.3 answers of 2026-10-06" |
| SPEC-007 decisions-log row (O2) | done | `:443` `| 2026-10-06 | PR 4 (4.3) | **A redirect hop from outside the host boundary into it is never followed.** …` |
| `CHANGELOG.md` `### Changed` | done | `:198-246`; O2 bullet `:227-231`; IVT `Ashlar.AI.Pipeline` → `Ashlar.Tests.Infrastructure` named `:224-226`; `NeverFollowRedirects` `:222`; Warnings 7303/7304 `:236-238`; cancellation sentence `:245-246`; `grep -c ASHLAR_EGRESS_MODE` unchanged (body `:71`) |
| `docs/EgressInventory.md` | done | `:3` names 4.3; `:15` the redirect paragraph with O2, G4 wording, cancellation; Known limits `:23` narrowed, `:24` (another type / credentials / started / **no primary yet**, G1), `:25` only `Authorization`, `:26` host filter, `:27` non-HTTP `Location` / P2; the former "P1 follows a remote hop into the host boundary" limit is **gone**; route vocabulary `:58-59`; EG-MDL-01 `:71`, EG-MDL-02 `:72` (`:38-39`, `RuntimeConfig`), EG-MDL-14 `:84`, EG-HTTP-03 `:130` |
| `ci/egress-inventory.tsv` | done | 86 rows, **149** occurrences (recomputed with awk), rows 9 and 34 notes name `NeverFollowRedirects` and keep `:171`/`:79`; row 38 `sdk.client 2` (`:38`, `:49`, `:39`); row 42 `(:68, :89)` |
| `ci/cert-gate-assertions.md` row 64 | done | `:64` contains `EgressRedirectFilter`, "one decision per send to one authority", the host-boundary sentence, "149 occurrences of which 48 are guarded", "2,139, 149 and 67 at PR 4.3", `EgressRedirectTwinTests`, `EgressRedirectDifferentialTests`; no new row |
| `ci/cert-gate-assertions.md` count paragraph | done, matches tree | `:69-75` "133 `.cs` files … (136 entries …) … then 131, before it read 133"; `git ls-files` gives 133 / 136 |
| `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt` | unchanged (correct: every new Abstractions type is internal) | `git diff origin/master..HEAD -- '*PublicAPI*'` empty |
| `EgressGuardConventionTests` floors comment | done | `EgressGuardConventionTests.cs:207-208` "2,139 files, 149 occurrences (http.new 20, sdk.client 8), as measured in the devtest container" (`run1`/`run2`: `ScannedFiles=2139 ExaminedOccurrences=149 DocsRows=67`) |
| `docs/SdkCompatibilityPolicy.md` | done | one line rewritten (follower on both assets; walker wording) |
| `docs/knowledge-graph.{json,md}` | **STALE** | last regenerated in `906bdda7` (`declared_facts: 4905`); the second commit added facts and did not regenerate. Regenerating in a scratch clone of HEAD gives `declared_facts: 4910` and `git status --porcelain` shows ` M docs/knowledge-graph.json`, ` M docs/knowledge-graph.md`. `shell-lint` byte-compares these: **red until regenerated** |
| `docs/DEPLOYMENT.md`, release notes, runbook | not touched; not needed (body `:213`: no runbook change; release notes from CHANGELOG) | diffstat |
| `.github/workflows/full-platform-readiness-gate.yml` | unchanged; touched paths match existing globs (records R47) | |

## 6. Integrator instructions (after the resume), one by one

| Instruction | Status | Evidence |
|---|---|---|
| (a) commit trailer "Claude Fable 5.1" | **implemented** | both commits carry `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` + `Claude-Session:`; `$SP/ws/squash-msg-4.3.txt` ends the same way |
| (b) push after every commit | **implemented** | remote head = local head `4d8860c5` |
| (c) read `attack-4.3.md` and `records-4.3.md`, close every item or say why not | **implemented in the body** | `pr-4.3-body.md:159-190` (26 attack items, §3 1–11, §4 flips by name), `:192-194` (R1–R61, Q-A–Q-F). Not yet re-verified by a second agent against HEAD; §7 below spot-checks the ones with records |
| (d) finish the mutation batch | **not finished**: 6/25 run (at `906bdda7`), m7 killed, m8–m26 never started; `{{MUTATIONS}}` still in the body `:95` | `mutations-summary.txt`; `$SP/mut/batch-*.sh` |
| (d) run `MeshLanPartyTests` as a control | **not run** | body `:91` lists it under "Not run" with a reason; the instruction asked for the run |
| (d) "for 4.7" paragraph naming both enforcement twins | **implemented** | `pr-4.3-body.md:132-134` names `EgressRedirectTwinTests.Under_an_enforcing_guard_a_redirect_to_a_remote_host_is_decided_refused_before_the_hop_is_sent` and `EgressRedirectDifferentialTests.Under_an_enforcing_guard_the_hop_to_a_remote_host_is_decided_refused_before_the_server_sees_it`, today's assertions and 4.7's |
| (d) G4 (`docs/EgressInventory.md:15` sentence) | **implemented** | `:15` "A hop can change the destination class, not only the recorded host: a redirect from a remote host to loopback is Host, and one to a search endpoint is WebSearch, so each hop's record stands on its own …" |
| (d) G1 (`Wrap` over a chain whose `InnerHandler` is null at wrap time) | **implemented, never run** | twin `Wrap_over_a_delegating_handler_whose_inner_is_set_later_treats_it_as_unknown_and_decides_after_the_send` (`EgressRedirectTwinTests.cs`, added in `4d8860c5`); Known limit `docs/EgressInventory.md:24` "no primary yet (a `DelegatingHandler` whose `InnerHandler` the caller sets after the wrap) … decided after the send (`EgressRedirectTwinTests`; not a production shape)" |
| (d) G2 (intermediate responses disposed) | **implemented, never run; m25 not run** | twin `An_intermediate_redirect_response_is_disposed_when_the_hop_is_followed`; `EgressRedirectHandler.cs` disposes in `NextHop` |
| (d) G3 (cancellation between hops) | **implemented, never run; m24 not run** | `EgressRedirectHandler.cs:389-393` (`cancellationToken.IsCancellationRequested` → dispose, throw, before the next hop is decided); twin `A_token_cancelled_after_the_first_hop_stops_the_follower_before_the_next_hop_is_sent_or_decided`; body deviation 10 `:115`; CHANGELOG `:245-246`; `docs/EgressInventory.md:15` |
| (d) name the IVT `Ashlar.Tests.Infrastructure` on `Ashlar.AI.Pipeline.csproj` in the CHANGELOG | **implemented** | `CHANGELOG.md:224-226` |
| (d) say the IVT grant to `Ashlar.Infrastructure` makes 4.10's rule a convention | **implemented (body only, as asked)** | `pr-4.3-body.md:138` (G12) |
| (d) say Warning 7304 fires in AG twins with stub primaries | **implemented (body)** | `pr-4.3-body.md:139` (G13) |
| (e) O2 rule: non-Host → Host hop never followed, P1 and P2 alike, 3xx returned, hop recorded | **implemented, never run** | `EgressRedirectHandler.cs:344` `InsideHostBoundary` (= `EgressDestinations.IsInsideHost` ∪ `IsLinkLocalHost`), `:376-381`; `EgressHopEvaluation.RecordUnfollowed` `:98` (records the attempted hop without moving the note's authority) |
| (e) twins: stub primary presenting `http://remote.example` then 307 to `http://127.0.0.1:<port>/b`, server B sees 0 requests, 2 decisions; link-local; `*.localhost` | **implemented, never run** | `EgressRedirectTwinTests.cs:512` theory `A_redirect_from_outside_the_host_boundary_into_it_is_returned_unfollowed_with_the_hop_recorded` rows `http://127.0.0.1:11434/api/chat` (Host), `http://svc.localhost/x` (Host), `http://169.254.169.254/…` and `http://[fe80::1]/x` (NetworkExport), both routes (`:516` comment); `:553` `A_redirect_inside_the_host_boundary_or_out_of_it_is_still_followed_by_a_factory_client`; `EgressRedirectDifferentialTests.A_remote_hop_into_loopback_Kestrel_is_returned_and_the_server_never_sees_it` (`RemoteFrontedPrimary` fronting the real `SocketsHttpHandler`; asserts `TemporaryRedirect`, `_server.For(token).Should().BeEmpty(…)`, two decisions `remote → 127.0.0.1:<port>`, second `DestinationClass == Host`) |
| (e) mutation "follower follows into Host → red" | **prepared (m26), not run** | `$SP/mut/t/m26-*.{old,new}`: `if (false && !InsideHostBoundary(requestUri) && InsideHostBoundary(location))` |
| (e) records: dated SPEC-007 decisions-log row | **implemented** | SPEC-007 `:443` |
| (e) intro sentence ~`:405-406` gains 4.3 | **implemented** (now at `:423`) | "the PR 4.2 and PR 4.3 answers of 2026-10-06" |
| (e) body | **implemented** | `pr-4.3-body.md:69` (behaviour change 4), `:114` (deviation 9: link-local included), `:118-120` ("Owner decision taken during this lane") |
| (e) CHANGELOG | **implemented** | `:227-231` |
| (e) `docs/EgressInventory.md` Known limits | **implemented** | the P1-into-Host limit removed; `:15` states the rule |

## 7. Attack list, records list and critic items

Attack list (`attack-4.3.md`, items 1–26 at `:89-...`): the body's close-out table (`pr-4.3-body.md:161-188`) names a
twin or a record for every item. Spot-checked against HEAD: 1, 2, 3, 4, 5, 6, 8, 9, 10, 12, 13, 22, 23, 24, 26 →
test method present in `EgressRedirectTwinTests.cs`/`EgressRedirectDifferentialTests.cs` (names listed in §2 row 10
and the `grep` of `public async Task` at HEAD); 14, 15 → `TheSnsSigningClient_NeverFollowsARedirect_OverTheFactorysOwnPrimaryHandler`
in both host test files (red at base, `red-first-824fff1c.log`); 16 → O2 (open: twins never run, m26 not run);
17 → `RuntimeConfig` + `CloudBedrock_*` tests (green in `certgate-906bdda7`); 7, 18, 19, 25 → records only
(`docs/EgressInventory.md:24`, `:71`, `:84`; body `:181`); 20 → ns2.0 twin `EgressHttpNetstandard20TwinTests.cs:151`;
21 → `DefaultGrpcChannelFactoryGapCoverageTests.cs:223-226` chain walk, 7/7 at `30588c74`. **Open:** the closure
of every item whose mutation is unrun (m7–m26) is asserted, not observed. Attack §3 (design wrong) 1–11 and §4
flips are mapped at body `:190`.

Records list (`records-4.3.md` R1–R61, Q-A–Q-F): closed at body `:194`; the records themselves verified in §5 above.
**Open:** R46 (knowledge graph: stale at HEAD); the §17 copy-paste checklist (`records-4.3.md:128-160`) items
"Full cert-gate … total ____", "Seven mutation summary lines", "MeshLanPartyTests", "26/26 repo gates" are not
satisfied on the current head.

Critic (`phase-C-critic.md`): §1 G1–G4 closed in code/records (never run: §6). §3 O2 answered by the owner and
implemented; §3 "already decided" rows for 4.3 (Q-A, Q-B, Q-C, item 13, Q-D/Q-E) recorded in the body `:122-130`.
§5.1 conflicts → §9 below. §5.2 G12, G13, G17 → body `:136-140`. §6 items 7–11: 7 **open** (mutations, MeshLanParty),
8 closed (`:132-134`), 9 superseded by O2 (implemented, not a Known limit any more), 10 closed, 11 closed (`CHANGELOG.md:224-226`).

## 8. Deviations and decisions the body records; placeholders

Deviations 1–11 at `pr-4.3-body.md:106-116`: (1) enforcement twins assert the decision, not a stopped hop (D21, D2;
"for 4.7"); (2) ns2.0 hop kept, follower under it, walker steps through; (3) presence check not `[0]`; (4) missing
note = never decided (duplicate record, fail closed); (5) started primary left as is; (6) composite primary flipped at
its tail; (7) relative `RequestUri` = one authority decided as a fault; (8) Bedrock: SDK config only, the
`HttpClientFactory` seam is a follow-up; (9) inward set = Host classification + link-local (link-local recorded as
`NetworkExport`); (10) cancellation between hops stops before the next hop is decided; (11) TSV 150 → 149,
`sdk.client` 9 → 8 (3 → 2 on the Bedrock file). Owner decision O2 `:118-120`; already-decided table `:122-130`.

Placeholders and stale statements in the body (fix before opening the PR):
- `:95` `{{MUTATIONS}}` — paste the 25 summary lines from the final head.
- `:3-32` "Resume state" preamble — remove (it is a work log, not PR content; its "Half-done / Never started" lists are
  themselves out of date).
- `:148` and SPEC-007 `:129` `(this PR)` → `(#N)` once opened (checklist `:209` already says so).
- `:87` "Full cert-gate on the pushed head (`906bdda7`)": the pushed head is `4d8860c5`; restate on the final head with
  its total.
- `:61` "`EgressRedirectTwinTests.cs` (new, 27 facts and theory rows)": HEAD has 24 facts + 4 theory rows = 28 cases;
  verify from the run output and fix.
- `:202-208` checklist boxes "[x] Every behaviour mutation-checked", "[x] Full cert-gate …", "[x] Repo gates 26/26",
  "[x] … knowledge graph regenerated after `git add`", "[x] One commit on top of master": all false at `4d8860c5`.
- `:99` "Not observed failing": says the second commit's twins' red evidence is m19–m26, m1, m5, m21, m22 — true only
  after those run.
- `$SP/ws/squash-msg-4.3.txt` (the one-commit message for the final squash) already includes O2, cancellation,
  disposal and the late-primary limit; it still says "Known limits … a Wrap over a chain with no primary yet only
  decides" — consistent with HEAD.

## 9. Expected merge conflicts (critic §5.1; merge order 4.3 → 4.10 → 4.5) and counts this lane owns

This lane is first in the merge order, so **it** should merge onto an unmoved master (`de41a8ac`, still true) with
no conflict; the later lanes restate its numbers. If the order changes (4.10 or 4.5 merges first), this lane must
re-derive on the master merge:
- `ci/cert-gate-assertions.md:64` (one line, three-way): this lane's text = TSV 86 / **149** / 48, `sdk.client` 8,
  floors **2,139 / 149 / 67**; after all three the floors are re-measured once (critic expects ≈2,144 files; measure).
- `ci/cert-gate-assertions.md:69-75` count paragraph: this lane 133 / 136; final ≈ 136 / 139 (recount with `git ls-files`).
- `EgressGuardConventionTests.cs:191-208` floors comment: same numbers as row 64.
- `CHANGELOG.md` `### Changed`: 4.3 and 4.10 insert at the same anchor (textual).
- `docs/EgressInventory.md`: `:3`, the per-PR section slot before `:16`, the Known-limits list, EG-MDL-02 row (`:72`
  here; 4.10 edits the same cell).
- `ci/egress-inventory.tsv`: this lane changes row 38's count (150 → 149); 4.10's "150 occurrences unchanged" wording
  becomes false after this merge (G17: 4.10 and 4.5 restate 149).
- SPEC-007: three `**PR 4.x**` bullets at the same anchor after `:127`; the decisions-log intro `:423` (4.5 adds its row
  and edits the same sentence).
- `docs/knowledge-graph.{json,md}`: never hand-merge; regenerate after `git add` on every master merge.
- `application/src/Ashlar.API/Program.cs`: no textual conflict (4.10 adds +19 lines after `:262`); this lane's cites
  (`:171`, `:232` in TSV row 9) are above that and hold.
- Cross-lane semantics to tell 4.10: Warning 7304 fires in `AddAshlar(AirGapped)` twins with stub primaries (G13);
  the IVT grant to `Ashlar.Infrastructure` makes "Infrastructure never reads `Effective` directly" a convention (G12).

## 10. Remaining steps to a PR-ready head (in order; "run" = one `scripts/test-in-container.sh` invocation)

1. **Regenerate the knowledge graph and commit** (host, 0 runs): in `$SP/c-4.3`, `git add -A && python
   scripts/knowledge-graph/build-knowledge-graph.py` (expect `declared_facts: 4910`), `git add docs/knowledge-graph.*`,
   commit with the Fable trailers, push. Then `PATH=$SP/sc-py/bin:$PATH bash scripts/ci/run-repo-gates.sh` → expect
   `repo-gates: all 26 gate(s) passed` (host, 0 runs); save to `$SP/logs-4.3/repo-gates-<sha>.log`.
2. **Decide squash timing.** The mutation lines and the cert-gate must name the SHA the PR ships. Recommended: squash
   now (`git reset --soft origin/master && git commit -F $SP/ws/squash-msg-4.3.txt`, re-check the trailers), push with
   `--force-with-lease`, and run everything below on that SHA. If a fix is needed later, re-squash and re-run the
   affected runs; a "tree byte-identical" argument is weaker than a run on the shipped SHA.
3. **Build and touched classes on the final head** (3 runs): (i) Abstractions build for netstandard2.0/net8.0/net10.0
   plus the 11 egress classes on net8.0 with detailed output for F6/F8 (filter as in `run2-b862c967.log`; expect
   `ScannedFiles=2139 ExaminedOccurrences=149 DocsRows=67`, total 499 + 8 = 507; the 9 never-run cases of `4d8860c5`
   must pass here); (ii) the same 11 classes on net10.0; (iii) `Ashlar.Tests.Transport` net8.0 (expect 7/7).
4. **Host twins on net10.0** (2 runs): `EgressApiHostProdStyleTests` (expect 4/4) and `EgressFleetHostTwinTests` with
   `--project commercial/tests/Ashlar.Commercial.Tests.Fleet.Host/Ashlar.Commercial.Tests.Fleet.Host.csproj` (2/2).
5. **`MeshLanPartyTests` control** (1 run): `--project application/src/Ashlar.Tests.CLI/Ashlar.Tests.CLI.csproj --filter
   FullyQualifiedName~MeshLanPartyTests` (net8.0 and, if cheap, net10.0); record the counts in the body under Testing.
6. **Mutations, all 25 on the final head** (50 runs: red + green each; ~6–10 min per mutation): move
   `$SP/logs-4.3/mutations-summary.txt` aside (it holds the `906bdda7` lines), then run `bash $SP/mut/batch-a.sh`,
   `batch-b.sh`, `batch-c.sh`, `batch-d.sh` sequentially (they `cd $SP/c-4.3` and default `--ref HEAD`, so HEAD must
   be the final SHA and the tree clean). Expect 25 `KILLED` lines; any `SURVIVED`/`INVALID` is a finding to fix or record
   (watch m8 and m17, §4). Paste the lines verbatim over `{{MUTATIONS}}` (`pr-4.3-body.md:95`).
7. **Full cert-gate on the final head** (1 run, ~15 min): `bash scripts/run-cert-gate.sh; echo EXIT-certgate=$?`;
   expect `Total tests: 2826` (measure), `skip guard: 0 skipped`, `EXIT-certgate=0`; put head and total in the body `:87`.
8. **Optional `build-core`** (1 run) or keep the body's argument at `:91` (no project reference/TFM change; two csproj
   IVT lines only).
9. **Fix the body** (0 runs): remove the Resume-state preamble; restate the cert-gate head/total, the twin count
   (`:61`), the checklist boxes (`:202-208`); keep `[coordinated-integration]`; mention `build-core` decision.
10. **Open the PR** against `master` from `claude/spec-007-pr4-4.3-redirects` with the body (the `layer-boundary`
    workflow needs the literal `[coordinated-integration]`); then replace `(this PR)` with `(#N)` in SPEC-007 `:129`
    and body `:148`, regenerate the knowledge graph if that commit changes anything it reads (it does not read
    SPEC-007 text, but run it after `git add` anyway), commit, push; re-run repo gates on the host.
11. **Agent-bus** (`_handoff/bus/README.md`, issue #695): post `Kind: handoff`, `About: pr-<N>` naming the records in
    §5 for Grok's drift audit; merge on green unless Grok posts a `block`; then `Kind: done` with the merge SHA;
    tell 4.10 and 4.5 to restate 149 / `sdk.client` 8 and re-measure the floors (G17), and 4.10 about 7304 (G13) and the
    convention sentence (G12).

Estimated container runs to PR-ready: 7 (steps 3–5, 7) + 50 (step 6) + 1 optional = **57–58**, about 4–6 hours
sequential at the lane's observed pace; mutations dominate.

## Blockers

- Stale `docs/knowledge-graph.{json,md}` at HEAD (shell-lint red) — step 1.
- No run evidence for `4d8860c5` (second commit) — steps 3–4, 7.
- 19 of 25 mutations unrun (m7–m26), 6 run on a superseded ref — step 6.
- Body placeholders and false checklist claims — step 9.
- Environment: `gh pr list` (GraphQL) is refused in this session; use `gh api repos/{owner}/{repo}/pulls`. Container
  restarts have killed runs twice in this phase; push after every commit, one container run per lane at a time.
