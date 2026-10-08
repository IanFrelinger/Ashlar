# Records / drift audit of master `095ba46e2` (#720, #722, #721, #723)

Lens: the drift audit Grok Bot would have run per CLAUDE.md's agent-bus loop, had the merges been posted.
Read-only on `scratchpad/review-master`; own clone at `scratchpad/review-records` (095ba46e2) for the KG regen
and the one container run. Logs: `scratchpad/review/logs-records/`.

## 0. Process facts (verified)

- agent-bus #695: 50 comments, last `2026-10-06T17:28:44Z` (grok, `drift-716`). No `handoff`/`done` for pr-720,
  pr-721, pr-722 or pr-723 (`logs-records/bus-695.json`). CLAUDE.md "The loop" steps 1–4 did not run for phase C.
- Merges: #720 squash `02fa27f1d` 2026-10-06T23:02:19Z; #722 MERGE COMMIT `904cf909a` 2026-10-08T01:28:45Z (13
  commits incl. "Merge master into PR #722 tip after #647/#701"); #721 squash `9b2ea56f4` 02:56:28Z; #723 squash
  `095ba46e2` 04:34:11Z. None carries a GitHub label; #720 and #722 bodies carry `[coordinated-integration]`
  (#722 also `[skip-prod-style]`).
- The phase C handoff was never written; the Claude lane branches remain on origin.

## 1. SPEC-007 (`docs/specs/SPEC-007-security-labels-and-reference-monitor.md`)

| Item | Finding |
|---|---|
| Header `:11` `## Status (2026-10-05)` | stale date; three PRs merged 2026-10-06/08 (minor) |
| `:48-49` "redirects that the primary handler follows are not evaluated, which **stays open until 4.3**" | stale: 4.3 (#722) is merged; the sentence should say 4.3 closed it (minor) |
| `:52` PR 4 plan intro "no production code enters an `EgressSubject` frame" | historical (2026-10-05) wording, acceptable; `:125` 4.4 bullet "Report-only, and no production code enters a frame yet" is now false after #721 (`SelfExtendRunnerAdapter.RunAsync` enters). Minor, wording |
| `:100` **PR 4.3** (#722) | no merge SHA (`904cf909a`); text says "An `EgressHttp` client follows only a same-host redirect (P2); a factory client follows a cross-host redirect and each hop is evaluated again (P1)". **Owner decision O2 (2026-10-06) is not described and not implemented**: `EgressRedirectHandler.cs:186` `if (!_settings.FollowCrossHost && !SameHost(requestUri, header)) return false;` is the only cross-host rule; `SameHost` (`:224`) compares `Host` only. A factory client (P1) following `remote.example` → `http://127.0.0.1:…` is followed. |
| `:133-142` **PR 4.5** (#721) | no merge SHA (`9b2ea56f4`). "An open read scope still observes only when it ends (the merged 4.4 rule): a tool that egresses before the scope ends is decided at the pre-read mark. That is a known limit for a runner whose floor is below `SystemHigh`. **The owner can reverse it.**" — the owner already decided the opposite on 2026-10-06 (INTEGRATION-NOTES-C §1: "while a read scope is open and unreported, every egress decided on the flows inside it is decided at SystemHigh, for every tool, RAGTool included; Scenario B's reason becomes SystemHighData"). Master code: `ReadScope.cs:78-84` observes only in `Dispose`; `ToolCallingAgent.cs` `using (var read = EgressSubject.BeginRead()) { … tools.InvokeAsync … labelled.ReportRead(read, result); read.Complete(); }` — nothing raises the decision mark while the scope is open. |
| `:143-153` **PR 4.10** (#720) | no merge SHA (`02fa27f1d`). "The ollama.com catalog defaults off on AirGapped when `Enabled` is unset" ✓ matches `ModelArtifactCatalogServiceCollectionExtensions.cs:38-45`. "On both profiles an API listener **or mesh serve** that does not bind loopback fails boot" — the mesh-serve half has no twin: `AirGappedSecureWorkstationHygieneCertificationTests.cs` has no `MeshServe` reference (grep empty; 9 facts, all on `AshlarInboundListenerPolicy`/options validation); #720's body admits "mesh-serve bind is a unit of `AshlarInboundListenerPolicy` and `MeshServeService`" and lists no test. Never observed failing. |
| Decisions log `:430-452` | Intro `:433` lists "the eight PR 4 answers of 2026-10-05, and the PR 4.2 answer of 2026-10-06" only. **Three 2026-10-06 owner decisions have no row**: (1) open-read-scope = SystemHigh (Scenario B → SystemHighData); (2) Q8 clarification O1 (custom `IDataSensitivityLevel` via RAG is SystemHigh in PR 4; C3 deferred); (3) O2 (a hop from a non-Host authority to a Host authority is never followed, P1 and P2). Only the D31 row exists for 2026-10-06. |
| `§2.3` "Enforced by" lines `:233-240` | none present; the design puts them in 4.11 — not expected yet ✓ |
| Open questions `:499-554` | unchanged; A, B, E still open ✓. Nothing records the phase-C open items (startup line in 4.7; Bedrock SDK HTTP via EgressHttp follow-up). |
| 4.4 line `:113` | has `de41a8ac` (added by 4.3 commit `8fc08e8e`) ✓ (closes phase B's queued item) |

## 2. CHANGELOG.md `[Unreleased]`

- `:22-31` **4.5** under Added: repeats the owner-contradicting sentence "An open read still counts only when the
  scope ends, which is a known limit … (the owner can reverse it)". Same blocker as SPEC.
- `:208-222` **4.3** under Changed ✓ (behaviour change named: `AllowAutoRedirect` false on every known primary,
  P2/P1, SNS clients, Bedrock SDK config, "The default MEAI Ollama client is unchanged from PR 4.1"). Does not
  mention O2 (because it is not implemented). "follows only a same-host redirect" — `SameHost` ignores port and
  scheme, so a same-host, different-port (different authority) redirect is followed under P2; design §2.6 said
  "per-authority" (minor wording / design weakening, unrecorded).
- `:223-241` **4.10** under Changed ✓ (AG routing, vision no-escalate, validators, ollama.com default, SW MCP HTTP,
  loopback boot failure incl. `ASHLAR_MESH_SERVE_BIND` "unset … is any-interface and fails boot rather than being
  rewritten" — matches `MeshServeService.cs:157-167`). The integrator decision in INTEGRATION-NOTES-C
  ("bind loopback and keep serving") was not taken; the owner's recorded Q6 text ("mesh serve must bind loopback,
  or boot fails") is what master does, so this is consistent with the owner, not with the Claude integrator note.
- **#723: no entry at all** (grep `cursor/`, `resolve-rebase-conflict`, `doctor-container-truthful`, `double-fire`,
  `merge queue` → nothing). It changed a CI trigger, added a script and two repo gates, and changed a UAT tier-0
  probe. Earlier CI changes (e.g. "`Readiness summary` says whether any platform ran", `:698`) have entries.

## 3. docs/EgressInventory.md

- `:3` intro: "PR 3b re-derived every line number … and SPEC-007 PRs 4.1, 4.6 and 4.10 did the same for the files
  they edited; every other file cited here is unchanged since `f1f2cff` (or, for `EgressGuard.cs`, since PR 3a
  added it)". False: 4.3 edited `EgressGuardHandler.cs`, `EgressHttp.cs`, both `Program.cs`, `DefaultGrpcChannelFactory.cs`,
  `AwsBedrockChatClientFactory.cs`; 4.5 edited `EgressGuardChatClient.cs`, `RAGTool.cs`, `ToolCallingAgent.cs`;
  4.6 edited `EgressGuard.cs`. Neither 4.3 nor 4.5 is named, and citations into those files are stale (below).
- Known limits:
  - redirects: the 3b/4.1 "not evaluated" bullet is gone; replaced by the `:15` 4.3 bullet ✓. "a chain walker stops
    at the hop" narrowed to "a walker of only `DelegatingHandler.InnerHandler` still stops at the synchronous-send
    hop … A walker that also steps through the hop's internal `Inner` continues" ✓ (`:23`).
  - Bedrock "the recorded host can differ from the one the SDK dials … still fails closed" ✓ (`:17`).
  - `:28` **Within-call egress (known limit, PR 4.5)** "A `BeginRead` scope observes only when it ends … The owner
    can reverse it." — contradicts the owner's 2026-10-06 decision (blocker, same as §1).
  - inbound "not mediated until PR 5" ✓ (`:181-183`, `:218`).
- Producers paragraph `:26` ✓ describes master (`SelfExtendRunnerAdapter.RunAsync` at SystemHigh; `ToolCallingAgent`
  `BeginRead`; `IEgressLabelledTool`; RAGTool canonical names; `peer:` observe). "No new egress site is added, so
  `ci/egress-inventory.tsv` is unchanged" ✓ (TSV diff for #721 is empty).
- AG section `:218` ✓ matches code and row 69, except the mesh-serve claim (untested, §1).
- Route vocabulary `:52-63` unchanged ✓; `Exempt:` closed list matches TSV reasons.
- **Citation spot-check (10+)** against 095ba46e2 (`logs-records`, script in this audit):
  | Citation | Actual | Verdict |
  |---|---|---|
  | `:15` `application/src/Ashlar.API/Program.cs:171` (SNS primary) | `:171` = `AddSingleton<IAshlarIngressAccessor…>`; `AddHttpClient("ashlar-sns-signing"` is `:172` | **stale** (the `:130` row says `:172`, correct) |
  | `:15` `commercial/src/Ashlar.Commercial.Fleet.Host/Program.cs:79` | `AddHttpClient("ashlar-sns-signing"` | ✓ |
  | `:15` `AwsBedrockChatClientFactory.cs:40-41` | two `new AmazonBedrockRuntimeClient(` with `AllowAutoRedirect = false` | ✓ |
  | `:15` `MeshAutoPullService.cs:57`, `OllamaHttpChatClient.cs:170` | `AllowAutoRedirect = false` | ✓ |
  | `:50` `EgressGuard.cs:212` (`DescribeProfile`) | blank line; `DescribeProfile` is `:214` | **stale** |
  | `:103` `Program.cs:160` (A2A transport), `:108` `Program.cs:158` (MCP client) | `AddAshlarA2ATransport`, `AddAshlarMcpClient` | ✓ |
  | `:140` "runs inside Ashlar.API (`Program.cs:221`)" | `:221` is a rate-limit partition line; the self-extend/background registration is `:228-235` | **stale** |
  | `:160` EG-TEL-01 `Program.cs:259`, `:266` | `:259` is the EG-TEL-01 comment, guard `:261`, exporters `:266`, `:273` | **stale** |
  | `:183` EG-SRV-01 `Program.cs:157` (`WithAshlarHttpTransport`), `AshlarMcpToolBridge.cs:122`, `Mcp.Server.Host/Program.cs:47` | ✓ | ✓ |
  | `:218` `AshlarDeploymentProfileEnvironment.cs:81` `ForbidsRemoteProtocolEgress` | ✓ | ✓ |
  | `MeshServeService.cs:316` (`ListenAnyIP`), `:379` (EG-MESH-03 guard) | ✓ | ✓ |
  | `DefaultGrpcChannelFactory.cs:67` | `GrpcChannel.ForAddress` | ✓ |
  Four of twelve are stale, all in `Ashlar.API/Program.cs` (edited by 4.10 then 4.3, +9 and +9 lines, neither
  re-derived the other's citations) and `EgressGuard.cs`.

## 4. ci/egress-inventory.tsv (computed with awk, `logs-records`)

- 86 rows; total 150; guarded 48; rows with `-` reason 33; `Exempt:*` 35 (GuardImpl 2, TestDouble 1, ConsumerSdk 2,
  TestSeam 1, Operator 12, LocalDaemon 6, LocalOnly 7, DataStore 2, Inbound 2); `Factory` 10; `Upstream:` 4;
  `Governance` 4. Row 64's "86 … 150 occurrences of which 48 are guarded … 33 … 35 … 10 … 4 … 4" ✓ all match.
- sdk.client marker: 5 rows, 9 occurrences; Bedrock row pinned **3** (`:40, :41` two constructions + `AsIChatClient` `:47`),
  verified against `AwsBedrockChatClientFactory.cs:39-47` (master kept two `new AmazonBedrockRuntimeClient(` in a
  ternary; the Claude lane had consolidated to one, hence its 3→2 / 150→149). Master is self-consistent at 150.
- #722's TSV edit: only note text (`:239`, `:138`, `:61, :82`, `:77`, Bedrock `:40, :41 … :47`). **Note drift**: the API
  `http.register` note says "AddAshlarEgressGuard follows AddAshlar … (:239)" — `:239` is `AddAshlarInboundListenerValidation(`;
  `AddAshlarEgressGuard()` is `:246`. The API `telemetry` note says exporters `(:252, :259)`, guard `(:247)` — actual guard
  `:261`, exporters `:266`, `:273`. (Notes are free text the test does not check; still records.) Fleet `:79`/`:138` ✓.
- No redirect-handler row (design §4 "redirect handler row" for 4.3): the handler constructs no client, so the scan
  observes nothing; row 64 and the floors comment say so ✓ (not a deviation in substance).

## 5. ci/cert-gate-assertions.md

- Row 68 (floor pin, 4.5) ✓ present, names `SelfExtendRunnerAdapter.cs` `RunAsync` `SystemHigh`.
- Row 69 (AG/SW hygiene, 4.10) ✓ present; describes only what the 9 facts test (listener options validation),
  does not claim a mesh-serve twin — consistent with the test, inconsistent with SPEC/CHANGELOG/Federation which say
  mesh serve "fails boot" (no evidence).
- Row 64: 4.3 sentence ✓ (P2/P1, rewrite twin, `Clear()` twin, unknown-primary post-send check, started-handler setter,
  differential against `SocketsHttpHandler`). Floors sentence: "measured 2,145, 150 and 67 with PR 4.5's one file on
  top of PR 4.3's two on top of PR 4.10, after 2,142 … at PR 4.10". Production `.cs` added de41a8ac..095ba46e2 =
  exactly 10 (7 by 4.10, 2 by 4.3, 1 by 4.5; `git diff --name-status --diff-filter=AD`), 2,135 + 10 = 2,145 ✓
  arithmetic. **Measured in the container** (see §6).
- Count paragraph `:72-74`: "135 `.cs` files … as of 2026-10-06 (138 entries in all)". `git ls-files` at 095ba46e2:
  **136 `.cs`, 139 entries** (3 golden JSON). The extra file is `AdmissionGateDemoTests.cs` (#644, `66111964`,
  merged 2026-10-08 between #721 and #723, not a SPEC-007 PR). The date is also wrong: 135/138 was only true after
  #722 and #721 merged on 2026-10-08. True value: 136 / 139 as of 2026-10-08.

## 6. EgressGuardConventionTests floors (`:192-212`) vs measurement

- Comment: 4.10 → 2,142; 4.3 → 2,144; 4.5 → 2,145; 150 occurrences throughout; consistent with row 64.
- Container run (own clone, net8.0, `FullyQualifiedName~EgressGuardConventionTests.F6|…F8`, detailed console):
  see `logs-records/f6f8-net8.log`. `F6_the_scan_reads_the_tree_and_every_anchor_contributes_its_marker` Passed, stdout `ScannedFiles=2145 ExaminedOccurrences=150`; `F8_every_id_is_a_written_row_and_every_scanned_row_is_pinned` Passed, stdout `DocsRows=67 (floor 30)`; `F6_the_guard_files_are_in_the_scanned_population` Passed; Total tests: 3, Passed 3. Row 64 and the floors comment (2,145 / 150 / 67) are **true** on 095ba46e2.

## 7. PublicAPI.Unshipped.txt (diff de41a8ac..095ba46e2)

- `+IEgressLabelledTool`, `+IEgressLabelledTool.ReportRead(ReadScope!, ToolResult!) -> void` (4.5): XML docs
  present (`IEgressLabelledTool.cs:3-21`); public is right (host tools implement it). Shape deviates from
  INTEGRATION-NOTES-C G5 ("report-only surface"): the tool receives the live `ReadScope`, whose public `Complete()`
  it can call — unrecorded deviation (not an owner decision).
- `+AshlarResolvedDeploymentProfileOptions` (+9 lines, 4.10): XML docs on every member ✓; public is right (bound as
  options across Infrastructure/AI.Pipeline/CLI). Appended after the `static` block, out of the file's sort order
  (nit; the analyzer does not care).
- 4.3 added nothing public ✓ (`EgressRedirectHandler` internal; IVT to `Ashlar.Infrastructure` added at csproj `:40`).

## 8. Knowledge graph

- Own clone at 095ba46e2: `git add -A && python3 scripts/knowledge-graph/build-knowledge-graph.py` → exit 0;
  `git status --porcelain` empty (0 lines). Byte-identical ✓.

## 9. #723 (`095ba46e2`)

- Workflow: `on.push.branches: [master, main]` ✓; `on` keys `schedule, pull_request, push, merge_group,
  workflow_dispatch` (so "the `changes` job already handles `merge_group`" is true, `:141`, `:170-185`).
  `on.push.paths` (83) == `READINESS_PATHS` (83) as sequences ✓ (parsed with comments stripped; cert-gate was green).
- `tests/scripts/readiness-push-no-cursor-branches.test.sh`: asserts the first `branches:` line after the first
  `push:` contains `master`, `main` and not `cursor/**`. It does NOT "pin push.branches to master/main only" as its
  header says: `[master, main, "claude/**"]` passes. It is auto-discovered by `run-repo-gates.sh` and runs bare ✓.
  #723 body's mutation (restore `cursor/**` → FAIL) is the only thing it proves.
- CLAUDE.md `:10-14` and CONTRIBUTING.md `:16-32`, `.github/workflows/README.md:75-85`: "**Stack** core-path series
  (each PR's base is the previous tip) so CI runs once … merges become fast-forwards or cheap updates instead of N
  full readiness matrices." **False under this repo's branch protection** (`strict: true`, `enforce_admins`,
  CiGateInventory `:22`): a stacked PR's head never contains the squash (or merge) commit that landing its base PR
  puts on `master`, so GitHub marks it out of date and the `synchronize` after the required update re-runs the
  matrix. #722's own history is the counter-example: `df15a4f6 Merge master into PR #722 tip after #647`,
  `9ca9e968 … after #701`. CLAUDE.md now says both "merges are serial" and "stack … so CI runs once" in one bullet.
  CONTRIBUTING also recommends squash (`:14`), which is the case where stacking saves nothing.
- CONTRIBUTING `:20` "~40 minutes" vs README `:70` "~20 min" for the heavy matrix (inconsistent, minor).
- `docs/CiGateInventory.md:24-28`, `:122` ✓ accurate about the trigger; `.github/workflows/README.md:15-16` ✓.
- CLAUDE.md `:22-23` adds the KG rebase helper ✓ (script exists; its test passes bare).
- Repo gates now 30 discovered scripts (28 + 2 from #723); the phase-B handoff's "all 26" is a handoff note, not a
  record in the repo ✓ no record states the count.

## 10. docs/Configuration.md, docs/Federation.md, docs/SdkCompatibilityPolicy.md

- `Configuration.md:387` and `Federation.md:50` `ASHLAR_MESH_SERVE_BIND` ✓ match `MeshServeService.cs:157-167`,
  `:260-296` (unset → "0.0.0.0" → refused on AG/SW; loopback IP or `localhost` → `Listen`/`ListenLocalhost`; other
  profiles `ListenAnyIP` and ignore the bind). "the daemon fails to boot": the throw is in `ExecuteAsync` after the
  `Port <= 0` early return, so an unset `ASHLAR_MESH_SERVE_PORT` does not fail ✓. Untested (no twin).
- `SdkCompatibilityPolicy.md:175` (#722) ✓ consistent with EgressInventory `:14`, `:23` (walker through `Inner`;
  synchronous `Send` throws before the follower).

## 11. Design §4 "Records each PR touches" (present / absent on master)

| PR | EgressInventory | TSV | cert-gate-assertions | PublicAPI | `application/` `[coordinated-integration]` | READINESS_PATHS | SPEC status | CHANGELOG | KG | agent-bus `handoff`/`done` |
|---|---|---|---|---|---|---|---|---|---|---|
| 4.3 | Known limits ✓ (`:15`, `:23`) | redirect-handler row: none (explained in row 64) | row 64 ✓ | – ✓ | yes, token in #722 body ✓ | – ✓ | ✓ no SHA; O2 absent | ✓ Changed | ✓ | **absent** |
| 4.5 | producers section ✓ | – ✓ | floor-pin row 68 ✓ | marker ✓ | – | – | ✓ no SHA; contradicts owner | ✓ Added; contradicts owner | ✓ | **absent** |
| 4.10 | AG section ✓ | – ✓ | AG twin row 69 ✓ | – (but +9 public lines, recorded) | yes, token in #720 body ✓ | – | ✓ no SHA | ✓ Changed | ✓ | **absent** |
| #723 | n/a | n/a | n/a | n/a | n/a | lists equal ✓ | n/a | **absent** | ✓ | absent |

Also expected by CLAUDE.md/handoff but absent: decisions-log rows for the three 2026-10-06 owner decisions; the
phase C handoff; the `DESIGN-4-final.md` §5 amendment on the storage branch (Scenario B reason) — unpublished.

## 12. PR bodies (claims checked)

- #722: "Cert-gate 2786/2786 on the pre-#720 tree" (stale by merge time; merged after #720 and #701/#647 merges
  into the tip), "Seven redirect mutations KILLED" with **no verbatim `mutation-check` summary lines** and no list of
  records touched (CLAUDE.md loop step 1 content). "Head `b65b1d38` contains `origin/master` `054b08a8`" was superseded
  by two later master merges into the tip. Nothing in the body records the deviations: no O2 rule, `SameHost` ignores
  port/scheme, no TSV row.
- #720: verbatim mutation lines ✓ (6, one INVALID then re-run KILLED); "count 132 `.cs` files, 135 entries" true at its
  merge; "Scanned production files 2142" plausible (2,135 + 7); "mesh-serve bind is a unit of … `MeshServeService`" —
  no test named; "Documentation updated (… Federation, CHANGELOG)" ✓.
- #721: verbatim mutation lines ✓ (4); "The owner can reverse it" (the read-scope limit) is false as a statement of
  the owner's position (decided 2026-10-06 before the PR was opened); "count 133 / 136" true at its merge.
- #723: "Mutation: restore `cursor/**` … KILLED" ✓ plausible; checklist "[ ] CI green on this PR" left unchecked
  though merged (the five checks were green per the orchestrator's statement; not re-verified here).
