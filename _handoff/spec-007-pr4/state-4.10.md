# State of SPEC-007 PR 4 lane 4.10 (AirGapped and SecureWorkstation hygiene), PAUSED

Read-only snapshot taken 2026-10-06 (after ~20:41Z, when the last container run was killed). Every path is absolute or
relative to the lane clone. `$SP` = `/tmp/claude-0/-home-user-Ashlar/a81481a5-34f6-5b00-a61f-74b03c502c7b/scratchpad`.
Lane clone: `$SP/c-4.10`. Logs: `$SP/logs-4.10/`. Mutations: `$SP/mut-4.10/`. Body draft: `$SP/ws/pr-4.10-body.md`.
Nothing in this file was produced by running dotnet, docker or a container; counts marked "(log)" come from the named
log, counts marked "(host)" from `git`/`awk`/`grep` on the clone.

## 1. Branch and commits

| Fact | Value |
|---|---|
| Branch | `claude/spec-007-pr4-4.10-agsw` (clone `$SP/c-4.10`) |
| HEAD | `696d381985a89f81d593ed2c7deb38620e4a577b` |
| `origin/claude/spec-007-pr4-4.10-agsw` | `696d381985a89f81d593ed2c7deb38620e4a577b` (**pushed == local**) |
| `git status --porcelain` | empty |
| merge-base with `origin/master` | `de41a8ac8d0d1ae94f075c7aba2dde366c890fec` = `origin/master` after `git fetch` (master has **not moved**; 4.3 and 4.5 are unmerged on `origin/claude/spec-007-pr4-4.3-redirects` @ `4d8860c5` and `origin/claude/spec-007-pr4-4.5-producers` @ `91d3b48c`) |
| Commits | 12 WIP commits, **none squashed, none carries a `Co-Authored-By` trailer** (`git log --format='%(trailers)'` empty on all 12) |
| Diff stat `origin/master..HEAD` | 33 files changed, 1749 insertions(+), 52 deletions(-) |

Commits, oldest first (`git log --format='%h %s' origin/master..HEAD`):

1. `8ba6d176` WIP 4.10: AirGapped hygiene twin (red at base)
2. `f6e6c385` WIP 4.10: AG/SW hygiene implementation
3. `c705d150` WIP 4.10: twin fix
4. `a0bc0cb1` WIP 4.10: verifier twin binds 127.0.0.1:0
5. `6d862586` WIP 4.10: records, docs, knowledge graph
6. `924df514` WIP 4.10: multi-frame vision refuses a cloud resolve on AG
7. `34f0883d` WIP 4.10: TSV note line numbers
8. `b8ebea42` WIP 4.10: floors re-measured (2,139 files, 150 occurrences)
9. `6f46d1f0` WIP 4.10: first-resolution refusal for all three options-bound validators; uncovered hosts named
10. `253e7b64` WIP 4.10: scouts' items — composition-time Bedrock refusal, four-reason routing twin, order/replacement/SW facts, call-site tripwire, records
11. `792a8875` WIP 4.10: twin fixes (Full control before AirGapped is noted; no router resolve under the refused opt-in)
12. `696d3819` WIP 4.10: mesh serve binds loopback on AG/SW (literal Q6); profile-read convention fact; critic items; records

Files in the diff (production): `application/src/Ashlar.API/Program.cs` (+19), `application/src/Ashlar.API/Security/LoopbackListenerVerifier.cs` (new, 48), `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs` (+51/-), `src/Ashlar.Hosting/AshlarServiceCollectionExtensions.AirGapped.cs` (new, 167), `src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs` (+14), `src/Ashlar.Hosting/AshlarKernelRegistrar.Phases.cs` (1 line), `src/Ashlar.Infrastructure/Deployment/LoopbackListenerPolicy.cs` (new, 129), `src/Ashlar.Infrastructure/Deployment/ResolvedDeploymentProfile.cs` (new, 53), `src/Ashlar.Infrastructure/Execution/AdaptiveProviderFactory.cs`, `src/Ashlar.Infrastructure/Execution/Routing/NcrCapabilityRouter.cs`, `src/Ashlar.Infrastructure/ModelArtifacts/OllamaRemoteLibraryCatalogOptions.cs` (doc line), `src/Ashlar.Mcp.Server/ValidateAshlarMcpServerOptions.cs` (+45). Tests: `src/Ashlar.Tests.Infrastructure/Tests/Certification/AirGappedHygieneTests.cs` (new, 680), `.../Certification/DeploymentProfileReadConventionTests.cs` (new, 64), `.../Certification/EgressGuardConventionTests.cs` (floors comment), `.../VirtualProduction/AirGappedProfileApiHostProdStyleTests.cs` (+118), `application/src/Ashlar.Tests.CLI/Tests/Commands/MeshServeLoopbackProfileTests.cs` (new, 154), `src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj` (+ProjectReference `Ashlar.Mcp.Server`), `src/Ashlar.Mcp.Client.Tests/DeploymentProfileEnvironmentCollection.cs` (comment). Records: CHANGELOG, SPEC-007, `docs/EgressInventory.md`, `ci/egress-inventory.tsv`, `ci/cert-gate-assertions.md`, `docs/{Configuration,DEPLOYMENT,DocsIndex,Federation}.md`, `docs/architecture/{ProtocolIntegration-MCP-A2A,README,product-split}.md`, `docs/knowledge-graph.{json,md}`.

## 2. Implemented against DESIGN §4 row 4.10 (`DESIGN-4-final.md:862`; §2.7 `:589-626`)

| # | Row bullet | State | Where |
|---|---|---|---|
| 1 | Defect 5 by D35: `NcrCapabilityRouter` Local on AG with the AG reason, every remote reason | **done** | `NcrCapabilityRouter.cs:70-76` (`if (_airGapped) return ResolveAirGappedTarget`), `:104-122`; reason prefix const `:34`; optional ctor param `:54` |
| 2 | Explicit `PeerNetworkOnly` refused with an explained failure | **done** (as `InvalidOperationException`, body deviation 5) | `NcrCapabilityRouter.cs:106-114` |
| 3 | `AdaptiveProviderFactory` never escalates: LLM path | **done** (stronger rule: drops `openai`/`azure` whatever `resolved` is, body decision 2) | `AdaptiveProviderFactory.cs:44-52` (`IsCloudProvider`, `Candidates`), `:72-74` |
| 4 | Single-image vision never tries `openai`/`azure` | **done** | `AdaptiveProviderFactory.cs:110` |
| 5 | Multi-frame vision: design said "already resolved only"; attack §3.1 showed it is not | **done** (refuses a cloud resolve with `ModelUnavailableException`) | `AdaptiveProviderFactory.cs:142-144` |
| 6 | Options-bound AG validator: `BrickHost:RemoteCatalogBaseUrls` | **done** (`IValidateOptions` + `ValidateOnStart`, also first-resolution) | `AshlarServiceCollectionExtensions.AirGapped.cs:42,46,119-125` |
| 7 | Validator: `Ashlar:RunPod:EnablePeerNetworkRouting` | **done** | `...AirGapped.cs:43,47,128-134` |
| 8 | Validator: `Ashlar:MeshLab:WorkerExecutor:Enabled` | **done** | `...AirGapped.cs:44,48,137-143` |
| 9 | Validator: `Ashlar:Meai:Bedrock:Enabled` | **done**, two halves: start-time validator through a carrier type `AirGappedBedrockCheck` (`:45,49,92-94,146-153`) **plus** a composition-time refusal inside `AddAshlar` (`:73-88`, called at `AshlarServiceCollectionExtensions.cs:159`) — the second is an integrator decision (body decision 8), stricter than the design | |
| 10 | ollama.com catalog off by default on AG | **done** (an `IConfigureOptions` inserted at index 0; explicit `Enabled=true` still wins) | `...AirGapped.cs:51-56` |
| 11 | Hosting-registered profile value, Infrastructure never reads `Effective` | **done** as a plain singleton `ResolvedDeploymentProfile` (not `IOptions<T>`, body deviation 3), built from `AshlarDeploymentProfileEnvironment.ResolvedRaw ?? profile` (D5) | `ResolvedDeploymentProfile.cs` (whole file); `...AirGapped.cs:36-40`; wired at `AshlarServiceCollectionExtensions.cs:151`, `AshlarKernelRegistrar.Phases.cs:592` |
| 12 | Q6: MCP over HTTP fails boot on SW, stdio boots | **done** (marker `ModelContextProtocol.AspNetCore.StreamableHttpHandler` queried through `IServiceProviderIsService`; fails closed when it cannot tell) | `ValidateAshlarMcpServerOptions.cs:29-50`, `:74-81` |
| 13 | Q6: API Kestrel URLs loopback on AG/SW or boot fails | **done**, two halves: pre-bind configuration check after `Build()` (`Program.cs:270-281`) and post-bind `LoopbackListenerVerifier : IHostedLifecycleService` (`Program.cs:264`; `Security/LoopbackListenerVerifier.cs:27-32`); the shared predicate `LoopbackListenerPolicy` (`IsLoopback :59-93`, `ConfiguredAddresses :28-55`, `Violation :104-123`) | |
| 14 | Q6: mesh serve loopback on AG/SW | **done per integrator instruction (d), literally**: `ListenLocalhost` when `BindsLoopbackOnly(_deploymentProfile)`, else `ListenAnyIP`; keeps serving | `MeshServeService.cs:282-291`; pure `BindAddress`/`BindsLoopbackOnly` `:236-244`; optional ctor param `:146` |
| 15 | D41 test hygiene | **done**: `AirGappedHygieneTests` is `[Collection("EnvironmentVariables")]` with `EgressProcessStateScope(reset: true)` per test (`AirGappedHygieneTests.cs:37-43`); `MeshServeLoopbackProfileTests` builds the value directly, no env, no `AddAshlar` beside the enum (`MeshServeLoopbackProfileTests.cs:13-19`) | |
| 16 | `[coordinated-integration]` (API listeners change) | **done** in the body with the rationale and the operator action | `pr-4.10-body.md` "Behaviour changes" paragraph |
| 17 | Done-when: cert-gate twin composing `AddAshlar(AirGapped)` (routing, vision, validators, ollama.com, Full unchanged, SW MCP HTTP/stdio, non-loopback listener fails boot) | **done**: 24 test methods in `AirGappedHygieneTests.cs` (names at `:71-593`), incl. `Full_is_unchanged :327`, `SecureWorkstation_outbound_paths_are_unchanged :437`, call-site tripwire `The_API_and_mesh_serve_call_the_listener_checks :504`, loopback table `:593` | |
| 18 | G9/G12 convention fact (integrator instruction e) | **done**: `DeploymentProfileReadConventionTests.cs` (roots Infrastructure/API/CLI, forbidden `AshlarDeploymentProfileEnvironment.` and `ASHLAR_DEPLOYMENT_PROFILE` outside `//` lines, floor 300 files) + row `ci/cert-gate-assertions.md:69` | **never run in a container** (added at `696d3819`) |

Partial / stale text left behind by the mesh-serve change at `696d3819`:

- `src/Ashlar.Infrastructure/Deployment/LoopbackListenerPolicy.cs:17-18` remarks still say "Mesh serve, which listens on every interface, **refuses to serve** on those profiles with the same message" — false since `696d3819`; must read "binds `localhost` on those profiles" (one doc-comment edit; no behaviour).
- `pr-4.10-body.md` Records bullet for `PublicAPI.Unshipped.txt` still lists **`MeshServeService.ProfileError`** as new public surface; `ProfileError` no longer exists (grep of `src application` finds none). Replace with `MeshServeService.BindAddress` / `BindsLoopbackOnly` (CHANGELOG already names those, `CHANGELOG.md:229-233`).

Nothing in the row is "not started".

## 3. Tests and runs (every log in `$SP/logs-4.10/`; "killed" = no summary line)

| Log | Head tested | Command (abridged) | Verbatim summary | Current head? |
|---|---|---|---|---|
| `certgate-base-de41a8ac.log` | `de41a8ac` (master) | `bash scripts/run-cert-gate.sh` | `Total tests: 2763` / `Passed: 2763` (`:2884-2885`) | base, still valid (master unmoved) |
| `twin-red-f7571612-net8.log` | `f7571612` (superseded twin draft, not on the branch) | AG twin, net8.0 | **build error** `error CS0246 ... 'ModelUnavailableException'` ×3 (`:72-74`); no test ran | superseded |
| `twin-red-net8.log` | `8ba6d176` (twin alone on master) | `dotnet test ... net8.0 --filter FullyQualifiedName~AirGappedHygieneTests` | **`Failed! - Failed: 13, Passed: 3, Skipped: 0, Total: 16`** (`:213`) — the red-first evidence; the 3 passes are the Full/no-opt-in/stdio controls | red-first, valid |
| `twin-green-net8.log` | `f6e6c385` | same filter | `Failed! - Failed: 1, Passed: 43, Total: 44` (`:126`); the red was `A_composition_after_AirGapped_was_noted_registers_AirGapped_and_refuses_the_opt_ins` (DI, fixed by `c705d150`) | stale |
| `net10-touched-1.log` | `c705d150` | Infrastructure net10.0 AG twin + API prod-style | `Failed! - Failed: 1, Passed: 56, Total: 57` (`:132`): `What_Kestrel_bound_is_checked_after_the_server_starts(air-gapped, loopback: True)` — "Dynamic port binding is not supported when binding to localhost" (fixed by `a0bc0cb1`); log ends there (CLI and Mcp.Server runs never reached) | stale |
| `net10-touched-2.log` | `a0bc0cb1` | Infrastructure net10.0 (AG + API prod-style); `Ashlar.Tests.CLI`; `Ashlar.Mcp.Server.Tests` | `Passed! 57/57` (net10.0, `:105`); `Passed! 33/33` Ashlar.Tests.CLI (`:169`); `Passed! 42/42` Mcp.Server.Tests (net8.0, `:179`) | **stale: re-run on 696d3819** (the CLI run tested the old refuse-to-serve mesh twin; the literal-Q6 `MeshServeLoopbackProfileTests` at `696d3819` has **never run**) |
| `repo-gates-1.log` | `6d862586` (host) | `scripts/ci/run-repo-gates.sh` | `repo-gates: discovered 26 gate(s)` ... `repo-gates: all 26 gate(s) passed` (`:843`) | **stale: re-run on 696d3819** (TSV, cert-gate-assertions, KG changed since) |
| `certgate-1.log` | `6d862586` | `run-cert-gate.sh` + F6 detailed | **killed** by the container restart: 2075 `Passed` lines, 0 `Failed`, no `Total tests:`; `AirGappedHygieneTests` and F6 never reached | **no full cert-gate has ever completed on any branch head** |
| `head-34f0883d-net8-ag-f6.log` | `34f0883d` | net8.0 `AirGappedHygieneTests` + `EgressGuardConventionTests.F6`/`F8` (detailed) | `Total tests: 51` / `Passed: 51` (`:167-168`); F6 printed `ScannedFiles=2139 ExaminedOccurrences=150` (`:96`) and per-marker totals (`:97-107`) — the source of the floors comment | stale for the twin count (65 tests at 792a8875); floors still plausible (no production file added since; re-measure anyway) |
| `head-253e7b64-net8-ag-compile.log` | `253e7b64` | net8.0 AG twin + `ProcessGlobalEnvironmentConventionTests` + `EgressGuardConventionTests.F1/F2/F8` | `Failed! - Failed: 3, Passed: 70, Total: 73` (`:152`): the two Bedrock-composition twins and `A_composition_after_AirGapped_was_noted...` (fixed by `792a8875`) | stale |
| `head-792a8875-net8-ag-compile.log` | `792a8875` | same filter | **`Passed! - Failed: 0, Passed: 73, Skipped: 0, Total: 73`** (`:81`) | **last green container run; one commit behind HEAD** |
| (none) | `696d3819` (HEAD) | — | **no container run of any kind on the current head** | — |

Other facts for this section:

- **Abstractions TFM builds**: not applicable — `src/Ashlar.Abstractions/` is untouched (`git diff --stat` shows no file there; no `PublicAPI.Unshipped.txt` change).
- **build-core**: **never run** (no `final-build-core-*.log`; `final-verify.sh` step 3 would run `dotnet restore/build Ashlar.LocalDevCore.slnf`). Required because `Ashlar.Tests.Infrastructure.csproj:128-130` adds `ProjectReference ..\Ashlar.Mcp.Server\Ashlar.Mcp.Server.csproj`. The body's records-checklist paragraph claims "build-core run" — **that claim is false today**.
- **net8.0 neighbour classes** (`CapabilityRoutingBrickTests`, `MultiSystemNcrSimulationTests`, `InfrastructureExecutionGapCoverageTests`, `RemoteExecutionSafetyTests`, `KernelDiCompositionProdStyleTests`, `HostingDeploymentProfileTests`, `KernelPhaseResolutionTests`, `HostingE2ESmokeTests`, `OnboardingE2ETests`, `OllamaRemoteLibraryModelArtifactCatalogSourceTests`; records-4.10 §13-14): **never run as a named set** (`final-verify.sh` step 2); some are inside the cert-gate population and were in the killed `certgate-1` run.
- `$SP/logs-4.10/final-verify.sh <ref>` is the prepared script for the final four container runs plus host repo gates; it has never been executed (no `final-*.log`).

## 4. Mutations (`$SP/mut-4.10/`; `scripts/mutation-check.sh`, AG filter = `FullyQualifiedName~AirGappedHygieneTests`, net8.0 unless noted)

Every summary line verbatim (`SUMMARY.txt`; 11 KILLED, 0 SURVIVED, 0 INVALID recorded):

```
19:05:58 rc=0 mutation router-ag-local: KILLED red=failed:3/48 green=passed:48/48 ref=b8ebea42701779ada708f9077c1c711afabce20b
19:09:45 rc=0 mutation adaptive-candidates: KILLED red=failed:4/48 green=passed:48/48 ref=b8ebea42701779ada708f9077c1c711afabce20b
19:14:26 rc=0 mutation d5-strictest: KILLED red=failed:2/48 green=passed:48/48 ref=b8ebea42701779ada708f9077c1c711afabce20b
19:20:56 rc=0 mutation mcp-sw-http: KILLED red=failed:2/48 green=passed:48/48 ref=b8ebea42701779ada708f9077c1c711afabce20b
19:28:55 rc=0 mutation loopback-ip: KILLED red=failed:6/48 green=passed:48/48 ref=b8ebea42701779ada708f9077c1c711afabce20b
19:35:26 rc=0 mutation validator-runpod: KILLED red=failed:6/48 green=passed:48/48 ref=b8ebea42701779ada708f9077c1c711afabce20b
19:42:32 rc=0 mutation validator-bedrock: KILLED red=failed:2/48 green=passed:48/48 ref=b8ebea42701779ada708f9077c1c711afabce20b
20:01:44 rc=0 mutation validator-brickhost: KILLED red=failed:2/48 green=passed:48/48 ref=b8ebea42701779ada708f9077c1c711afabce20b
20:23:55 rc=0 mutation validator-brickhost-r2: KILLED red=failed:3/65 green=passed:65/65 ref=792a8875993707da0efb99b201b95f74594d1c7f
20:29:58 rc=0 mutation validator-meshlab: KILLED red=failed:3/65 green=passed:65/65 ref=792a8875993707da0efb99b201b95f74594d1c7f
20:35:45 rc=0 mutation ollama-default: KILLED red=failed:1/65 green=passed:65/65 ref=792a8875993707da0efb99b201b95f74594d1c7f
```

Red test names per kill are in each `<id>.log` (`table.py` extracts them); e.g. `router-ag-local`: `AirGapped_refuses_an_explicit_peer_network_only_job...`, `AirGapped_overnight_routing_runs_locally...`, `A_later_weaker_AddAshlar_keeps_the_AirGapped_routing`; `loopback-ip`: 3 table rows + 3 boot rows; `validator-runpod`: 6 incl. the first-resolution and D5 twins.

Refs vs HEAD: production files changed `b8ebea42..HEAD` = `MeshServeService.cs`, `AshlarServiceCollectionExtensions.AirGapped.cs`, `AshlarServiceCollectionExtensions.cs`; changed `792a8875..HEAD` = **`MeshServeService.cs` only** (host `git diff --name-only`). So the batch-A kills on `NcrCapabilityRouter.cs`, `AdaptiveProviderFactory.cs`, `LoopbackListenerPolicy.cs`, `ValidateAshlarMcpServerOptions.cs` test code byte-identical to HEAD; `validator-bedrock` and `validator-brickhost` at `b8ebea42` were superseded by `-r2` runs at `792a8875` for the `AirGapped.cs` refactor (brickhost-r2 done; **`validator-bedrock-r2` not run**).

**Partial (killed by the pause):** `router-peer-refusal` at `792a8875` — red run complete: `Failed! - Failed: 1, Passed: 64, Total: 65` (`router-peer-refusal/red.log`, red test `AirGapped_refuses_an_explicit_peer_network_only_job_with_an_explained_failure`); green run **Terminated** mid-build (`router-peer-refusal.log` tail); **no summary line**. Work dir `$SP/mut-4.10/router-peer-refusal/` exists, so every run script's `run()` would **skip it** ("work dir exists") — delete the dir before re-running.

**Planned, never run** (scripts `run-batch-2.sh` B/C, `run-batch-3.sh` D, `run-batch-4.sh` E; `run-all.sh <ref>` chains B, C, D):

| Batch | id | file / old → new | filter / TFM | Note |
|---|---|---|---|---|
| B | `adaptive-multiframe` | `AdaptiveProviderFactory.cs` `if (_airGapped && IsCloudProvider(resolved))` → `!IsCloudProvider` | AG net8 | old string present at HEAD |
| B | `loopback-http-ports` | `LoopbackListenerPolicy.cs` drop the `http_ports` AddRange line | AG net8 | present |
| B | `loopback-profile` | `LoopbackListenerPolicy.cs` `if (profile is not { RequiresLoopbackInbound: true })` → `if (profile is null)` | AG net8 | present |
| C | `api-prebind` | `Program.cs` `throw new InvalidOperationException(listenerViolation);` → `_ = listenerViolation;` | `AirGappedProfileApiHostProdStyleTests` **net10.0** | present (also appears in the test file, but `--file` is Program.cs) |
| C | `api-verifier` | `LoopbackListenerVerifier.cs` return CompletedTask always | same, net10.0 | present |
| C | `mesh-return`, `mesh-address` | old refuse-to-serve mutants | — | **WITHDRAWN / INVALID**: `_settings.Port, profileError); return;` no longer exists at HEAD (`mesh-return.old`); `mesh-address` superseded by `mesh-bindaddress`. Empty dirs `$SP/mut-4.10/mesh-return`, `mesh-address` exist → `run()` skips them silently (harmless, but delete for a clean table) |
| D | `bedrock-composition` | `AirGapped.cs` `if (!resolved.IsAirGapped)` → `if (resolved.IsAirGapped)` | AG net8 | present |
| D | `validator-bedrock-r2` | `AirGapped.cs` `return bedrock && _profile.IsAirGapped ?` → `!bedrock && ...` | AG net8 | present |
| D | `router-overnight-only` | `NcrCapabilityRouter.cs` `if (_airGapped)` → `if (_airGapped && requirements.IsOvernightOrBackground)` | AG net8 | present; kills `AirGapped_keeps_every_remote_reason_local` non-overnight rows (attack item 1) |
| D | `mcp-marker-name` | `ValidateAshlarMcpServerOptions.cs` marker type name → `NoSuchHandler` | AG net8 | present; shows fail-closed (stdio twin red) |
| D | `api-prebind-certgate` | `Program.cs` same as C's `api-prebind` but AG filter net8 (tripwire) | AG net8 | superseded by E's `api-prebind-certgate-r2` (identical text; run one, not both — `run-batch-3.sh` still lists it) |
| D | `mesh-return-certgate` | old-file `mesh-return.old` | AG net8 | **INVALID at HEAD** (text gone); empty dir exists → skipped; remove from `run-batch-3.sh` or let it skip |
| D | `collection-attribute` | `AirGappedHygieneTests.cs` drop `[Collection("EnvironmentVariables")]` (multi-line `collection.old/new`) | `ProcessGlobalEnvironmentConventionTests` net8 | present (D41 / attack item 27) |
| E | `mesh-anyip` | `MeshServeService.cs` `k.ListenLocalhost(_settings.Port, configure);` → `k.ListenAnyIP(...)` | `MeshServeLoopbackProfileTests` **net10.0**, project `Ashlar.Tests.CLI` | present; needs a non-loopback IPv4 on the container (`NonLoopbackIPv4Addresses()` must be non-empty, `MeshServeLoopbackProfileTests.cs:84-85`) |
| E | `mesh-bindaddress` | `MeshServeService.cs` `BindAddress` ternary → always `*` | same | present |
| E | `mesh-anyip-certgate` | same text as `mesh-anyip`, AG filter net8 (tripwire) | AG net8 | present |
| E | `profile-read-convention` | `ResolvedDeploymentProfile.cs` `Profile = profile.Trim();` → `... _ = "ASHLAR_DEPLOYMENT_PROFILE";` | `DeploymentProfileReadConventionTests` net8 | present |
| E | `api-prebind-certgate-r2` | as D's `api-prebind-certgate` | AG net8 | present |

Count: **16 valid mutants still to run** (B 3, C 2, D 5 excl. the duplicate and the invalid one, E 5) **plus the `router-peer-refusal` re-run = 17**. Observed pace: batch A 8 mutants in 59 min (~7 min each, one 19-min outlier under CPU contention); batch B 3 in 18 min (~6 min each). Budget ~2 h with one container at a time.

The body's attack-list table (`pr-4.10-body.md` "Adversarial inputs") already names `router-overnight-only`, `adaptive-multiframe`, `bedrock-composition`, `mesh-anyip`, `mesh-bindaddress`, `mesh-anyip-certgate`, `collection-attribute`, `api-prebind`, `api-verifier`, `loopback-http-ports`, `mcp-marker-name`, `profile-read-convention`, `api-prebind-certgate` as closing items — **prospectively; none of those has run**. The table must be re-read against `SUMMARY.txt` once they have.

## 5. Records

| Record | State | Where / what is missing |
|---|---|---|
| SPEC-007 status bullet `**PR 4.10** (this PR)` | **done**, "(this PR)" placeholder kept for the integrator | `docs/specs/SPEC-007-...md:128-142`; says "mesh serve binds `localhost` ... keeps serving (Q6 as recorded)", "off" for ollama.com ("defaults to off"), Known limit sentence, convention-fact sentence |
| SPEC-007 decisions-log row | **not added, by design** — Q6 implemented literally so the critic (O3) and INTEGRATION-NOTES-C say no dated row is needed; `:405-406` intro untouched | nothing to do unless the owner reopens O3 |
| SPEC-007 §3 "Deployment profiles" row | **done** | `:264` |
| SPEC-007 4.4 bullet merge SHA (`de41a8a`) | **not done, on purpose** (left to 4.3, merge order) | records-4.10 §1.2 |
| CHANGELOG Unreleased `### Changed` | **done**, 37 lines incl. operator action and the literal-Q6 mesh paragraph | `CHANGELOG.md:198-234` |
| `docs/EgressInventory.md` | **done**: `:3` names 4.10; new "AirGapped and SecureWorkstation hygiene (SPEC-007 PR 4.10)" section before Known limits; Known-limit bullet on inbound responses + uncovered hosts; `:46` sentence rewritten ("Until SPEC-007 PR 4.10..."); rows EG-MDL-02/03/09/13, EG-EXE-01/03/05, EG-MESH-03 (`:290` `ListenAnyIP`; `ListenLocalhost` `:286`; `:296-308`, `:310`, `:332-358`, `:357`), EG-SRV-01 (`Program.cs:414`, validator `:68-73`/`:74-81`); profiles summary; fourth meaning of air-gapped. 67 `| EG-` rows (host count) | re-derived line numbers hold at HEAD (`MeshServeService.cs:286/290` confirmed) |
| `ci/egress-inventory.tsv` | **done**, notes only: row 14 (`MeshServeService.cs` `:357`/`:354`), row 51 (`AshlarServiceCollectionExtensions.cs` `:144`/`:147`); sums 86 rows / **150** / 48 (host `awk`) | **150 becomes 149 after 4.3 merges** (4.3's TSV sums 86/149/48, host `awk` on `origin/claude/spec-007-pr4-4.3-redirects`) → restate row 64 and floors comment (critic G17) |
| `ci/cert-gate-assertions.md` rows | **done**: two new rows after row 67 — `AirGappedHygieneTests` (`:68`) and `DeploymentProfileReadConventionTests` (`:69`); row 64 unchanged text apart from the floors sentence | critic §5.1 assumed one 4.10 row; it is **two**, so after 4.3 (+2 rows) the slots renumber |
| `ci/cert-gate-assertions.md` count paragraph | **done**: "133 `.cs` files ... (136 entries in all)" (`:71-75`); host `git ls-files` at HEAD = 136 / 133 ✔ | 4.3's branch also says 133 (its own +2); merged = 131 + 2 + 2 = **135 / 138** before 4.5 — recount with `git ls-files` after each merge. **Body inconsistency:** the records-checklist paragraph says "5.5 132/135" while the Records section says 133/136 — fix the body to 133/136 |
| `PublicAPI.Unshipped.txt` | **n/a, correct**: Abstractions untouched; no baseline for Infrastructure/Hosting/Mcp.Server/API/CLI | body lists the new public surface; **stale `MeshServeService.ProfileError` entry** (see §2) |
| `EgressGuardConventionTests` floors comment | **done** at `:205-208`: "4.10 adds four files with no outbound path ... 2,139 files, 150 occurrences", measured at `34f0883d` (`head-34f0883d-net8-ag-f6.log:96` `ScannedFiles=2139 ExaminedOccurrences=150`) | no production file added since → plausibly still 2,139/150 at HEAD, **re-measure on the final head** (F6 detailed run is in `final-verify.sh`); becomes 2,139/149 after 4.3 (4.3 removes one `sdk.client` occurrence and adds files: critic expects ~2,144/149/67 on the fully merged tree — measure, do not compute) |
| `docs/knowledge-graph.{json,md}` | **regenerated at HEAD**: `696d3819` touches both and the last code change is in the same commit; the JSON names `DeploymentProfileReadConventionTests`/`MeshServeLoopbackProfileTests` (host grep, 2 hits) | regenerate again after any further `git add` and after every master merge |
| `docs/DEPLOYMENT.md` | **done** (`:55`: API binds loopback on AG/SW; the image's `ASPNETCORE_URLS=http://+:8080` fails boot; set a loopback URL) | says nothing about mesh serve (that is in `docs/Federation.md:50-52`, done) |
| Release notes | the body's **Release** section says it (API loopback, MCP HTTP refused on SW, mesh serve binds `localhost` only); CHANGELOG carries the operator action | no separate release-notes file in the repo for this |
| `docs/Configuration.md`, `docs/DocsIndex.md`, `docs/architecture/{ProtocolIntegration-MCP-A2A,README,product-split}.md` | **done** (records-4.10 §11.1-11.4) | — |
| `src/Ashlar.Mcp.Client.Tests/DeploymentProfileEnvironmentCollection.cs` stale line cite | **done** (comment) | — |

## 6. Integrator instructions sent after the resume, one by one

| # | Instruction | State | Evidence |
|---|---|---|---|
| (a) | Commit trailer `Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>` + `Claude-Session: https://claude.ai/code/session_017TjtzCLb6VaYxD1MtRJ1ja` | **not yet**: all 12 WIP commits carry no trailer (`git log --format='%(trailers)'` empty). The prepared squash message `$SP/ws/commit-4.10-msg.txt` has the right trailer but its **body is stale** ("mesh serve (ListenAnyIP) refuses to serve" — must say binds loopback and keeps serving) | squash step pending |
| (b) | Push after every commit | **done**: `origin/claude/spec-007-pr4-4.10-agsw` == HEAD `696d3819` | §1 |
| (c) | Read `attack-4.10.md` and `records-4.10.md`, close every item | **done in the body** (38-row attack table, records-checklist paragraph, `pr-4.10-body.md` "Adversarial inputs, item by item"); **partially in evidence**: the mutations the table cites as closers are 13 unrun names (§4); the twins it cites exist in `AirGappedHygieneTests.cs` and were green at `792a8875` (73/73) except the `696d3819` additions (convention test, literal-Q6 mesh twin) which never ran | §4, §7 |
| (d) | MESH SERVE literal Q6: `ListenLocalhost` at the `ListenAnyIP` site, keep serving; do not ship "refuse to serve"; update twin, body, `docs/DEPLOYMENT.md`, release notes | **implemented**: `MeshServeService.cs:282-291`; twin rewritten (`MeshServeLoopbackProfileTests.cs:63-116`: serves on 127.0.0.1, refuses every non-loopback IPv4, Full control); body deviation 1 rewritten ("no deviation"), Behaviour/Changes/Release paragraphs; CHANGELOG `:229-232`; `docs/Federation.md:50-52`; `docs/EgressInventory.md` EG-MESH-03 row; SPEC-007 bullet. **Loose ends**: `LoopbackListenerPolicy.cs:18` remark still says "refuses to serve"; body still names `MeshServeService.ProfileError`; `commit-4.10-msg.txt` stale; `docs/DEPLOYMENT.md` mentions the API only (acceptable: Federation.md carries mesh serve). **Never run in a container**: the new mesh twin (net10.0, `Ashlar.Tests.CLI`) and its three mutants | §2, §4 |
| (e) | Critic §1 G9-G11, §5 G12/G13/G15/G16/G17/G19, §6 items 12-15 | G9/G12 **done** (`DeploymentProfileReadConventionTests` + row `:69`, body sentence), never run; G10 **done** ("off by default" / "defaults to off" in body, SPEC-007, CHANGELOG, row, inventory); G11 **done** (post-bind window = body deviation 6 + inventory Known limit); G13 **deferred by design** (re-run `AirGappedHygieneTests` after 4.3 merges; body says so); G15 **done** (`A_container_composed_Full_before_AirGapped_was_noted_keeps_Full :411`, recorded for 4.11); G16 **recorded** (body decision 8, INTEGRATION-NOTES-C); G17 **deferred** (150 stated for this tree; restate 149 after 4.3); G19 **not yet evidenced** (`EgressKernelFactoryTwinTests` is in the full cert-gate, which has not completed); §6.12 **done**; §6.13 **not yet** (mutations partly, build-core never, full cert-gate never, `924df514`/`34f0883d` since covered by the 792a8875 run); §6.14 convention fact **done**, 149 and 7304 **deferred**; §6.15 honest wording **done** (body: "fail boot, and fail the first resolution in a process with no boot"; post-bind window as Known limit) | §3, §5 |

## 7. Attack-list, records-list and critic items

- **attack-4.10.md items 1-29**: every item has a disposition row in the body (`pr-4.10-body.md` table). Closed by code+twin in the tree: 1, 2, 3, 5-8, 12, 13, 14, 15, 16, 18, 19, 20, 21, 22, 23 (literal Q6), 26, 27, 28, 29. Recorded (limit) not closed: 4, 9, 10, 11, 17, 24, 25. Items 30-38: recorded in the inventory; row 37 corrected the body. §2 runtime claims 1, 7, 10, 11, 12: not pinned. §3 design gaps 1-12: each mapped to a decision/deviation. **Open in evidence**: every "closed by mutation X" where X is in §4's unrun list; the twins for 21 (real Kestrel), 23 (mesh) and 27 (`collection-attribute`) have run only for the API half (`net10-touched-2.log`, stale) or never.
- **records-4.10.md §1-16**: all addressed in the body's records-checklist paragraph; **§10.3 build-core** claimed run but **not run**; **§16 copy-paste checks** claimed run "at the final head" — the host-side ones hold at HEAD by my re-check (TSV 86/150/48, 67 rows, 136/133 ls-files, no forbidden profile read in the three roots, KG mentions the new classes); the container-side ones (F6/F8 totals, cert-gate total) do not exist for HEAD. Owner questions 1-3: Q1 answered by the coordinator (literal Q6); Q2 (UDP/gRPC/Fleet.Host scope) and Q3 (container shape) are body owner-questions 2-3, "already decided" per critic §3 (Known limit + bus follow-up; DEPLOYMENT.md note) — informational, not blocking.
- **phase-C-critic.md** for 4.10: §1 G9-G11 closed (§6 above); §3 O3 resolved by (d); §5 G12 closed, G13/G17 deferred to post-4.3, G15/G16 recorded, G19 pending the full cert-gate; §5.1 conflicts: see §9; §6 items 12-15: 12 done, 13 open (runs), 14 deferred, 15 done.

## 8. Deviations, decisions and placeholders in the body

Decisions recorded (body "Deviations and integrator decisions" 1-11): 1 mesh serve literal Q6 (no deviation); 2 adaptive factory drops `openai`/`azure` whatever `resolved` is; 3 plain singleton, not `IOptions<T>` (+ Known limit: host replaces it after `AddAshlar`); 4 value captured at composition time (reverse order keeps Full); 5 `PeerNetworkOnly` refusal is an `InvalidOperationException`; 6 post-bind half + conservative pre-bind over-refusal; 7 not covered (gRPC host, Fleet.Host, UDP discovery, bare factory, video, direct RunPod brick); 8 Bedrock composition-time refusal in `AddAshlar` (owner can object); 9 discovery beacon still announces the port; 10 MCP-on-SW edges (no transport boots; `Enabled=false` + own `MapMcp` bypasses); 11 listener check reaches cert-gate only through the source tripwire. Owner questions 1-6 listed, 1 answered.

Placeholders / stale text to resolve before the PR opens:

| Where (`pr-4.10-body.md`) | Text | Action |
|---|---|---|
| top, `## Resume state` (lines 1-23) | reconstruction notes from the restart, incl. "`$SP/mut-4.10/` is empty", "build-core never started" | delete the section before publishing (keep as a scratch note elsewhere if wanted) |
| Testing, "**Green.**" | `COUNTS_PLACEHOLDER` | fill from `final-certgate-<ref>.log` (`Total tests:` line, must be 2763 + the new tests, 0 skipped), `final-net8-neighbours`, `final-net10-touched` (Infrastructure, Tests.CLI, Mcp.Server.Tests, Mcp.Client.Tests), `final-build-core`, `final-repo-gates` (26/26) |
| Testing | `MUTATION_TABLE_PLACEHOLDER` | paste `SUMMARY.txt` verbatim + `python3 $SP/mut-4.10/table.py` red names; the paragraph above it already narrates refs `b8ebea42` / `792a8875` / `696d3819` — adjust if the final ref differs (it will, after the doc-comment fix) |
| Testing | `NOT_OBSERVED_PLACEHOLDER` | list: boot order `IStartupValidator` before hosted services; the mesh beacon; the record at EG-MESH-01; anything in §4 that ends SURVIVED/INVALID |
| Records, `PublicAPI.Unshipped.txt` bullet | `MeshServeService.ProfileError` | replace with `BindAddress`/`BindsLoopbackOnly` |
| Records checklist paragraph | "5.5 132/135" | 133/136 |
| Records checklist paragraph | "build-core run" | true only after `final-build-core-*.log` exists |
| Testing mutations paragraph | "The squashed head's tree equals `696d3819`'s (checked with `git diff`, below)" | will be false once the doc-comment fix lands; restate against the final ref |
| SPEC-007, Records | `(this PR)` | the integrator's at PR open (keep) |
| `$SP/ws/commit-4.10-msg.txt` | "mesh serve (ListenAnyIP) refuses to serve" | rewrite to "binds `localhost` and keeps serving" before squashing |

No `{{...}}` or `TBD` tokens in the body.

## 9. Expected merge conflicts and counts to restate (critic §5.1, re-checked on the live branches)

Files 4.10 shares with 4.3 (`origin/claude/spec-007-pr4-4.3-redirects`, host `comm`): `CHANGELOG.md`, `application/src/Ashlar.API/Program.cs` (different regions: 4.3 `:171`, 4.10 after `:262` — no textual conflict expected), `ci/cert-gate-assertions.md` (row 64 floors sentence: three-way; the two new 4.10 rows vs 4.3's two new rows after 67 → renumber; count paragraph both say 133 → merged 135/138), `ci/egress-inventory.tsv` (4.3 row 38 `sdk.client 3→2`, total 150→149; 4.10 rows 14 and 51 notes only), `docs/EgressInventory.md` (`:3`, section slot before Known limits, Known-limits list, **EG-MDL-02 cell edited by both**), `docs/knowledge-graph.{json,md}` (regenerate, never hand-merge), `docs/specs/SPEC-007-...md` (status-bullet anchor after `:127`; 4.3 adds `de41a8a` to the 4.4 bullet), `EgressGuardConventionTests.cs` (floors comment, three-way).

Files shared with 4.5 (`origin/claude/spec-007-pr4-4.5-producers`): `CHANGELOG.md`, `ci/cert-gate-assertions.md`, `docs/EgressInventory.md`, `docs/knowledge-graph.*`, SPEC-007, `EgressGuardConventionTests.cs`. No production-file overlap with either lane.

Counts 4.10 must restate after 4.3 merges (merge order 4.3 → 4.10 → 4.5): TSV total **149** (row 64 sentence, floors comment, body Records "150 occurrences"), `sdk.client` 8; floors re-measured in the container on the merged tree (F6 `ScannedFiles=`/`ExaminedOccurrences=`); Certification count paragraph via `git ls-files` (expect 135 `.cs` / 138 entries); row numbers of the two 4.10 rows; `docs/EgressInventory.md:3` PR list; SPEC-007 4.4 bullet SHA present (4.3 adds it); re-run `AirGappedHygieneTests` for Warning 7304 side effects (G13) and `EgressKernelFactoryTwinTests` (G19) in the full cert-gate.

## 10. Remaining steps to a PR-ready head, in order

Container runs are one at a time (`bash $SP/c-4.10/scripts/test-in-container.sh --repo $SP/c-4.10 --ref <sha> ...`; mutation-check counts as one run of ~6 min). Never run dotnet on the host. Push after every commit with `git push --force-with-lease -u origin claude/spec-007-pr4-4.10-agsw`.

1. **Hygiene commit (0 container runs).** In `$SP/c-4.10`: edit `src/Ashlar.Infrastructure/Deployment/LoopbackListenerPolicy.cs:17-18` ("Mesh serve ... binds `localhost` on those profiles and keeps serving" instead of "refuses to serve"); edit `$SP/ws/pr-4.10-body.md` (drop `## Resume state`; `ProfileError` → `BindAddress`/`BindsLoopbackOnly`; "5.5 132/135" → "133/136"); rewrite `$SP/ws/commit-4.10-msg.txt` mesh sentence. `git add -A && python scripts/knowledge-graph/build-knowledge-graph.py && git add -A && git commit -m "WIP 4.10: stale refuse-to-serve remark" && git push`. Note the new SHA = `REF`.
2. **Clean the mutation workspace (0 runs).** `rm -rf $SP/mut-4.10/router-peer-refusal $SP/mut-4.10/mesh-return $SP/mut-4.10/mesh-address $SP/mut-4.10/mesh-return-certgate`. In `run-batch-3.sh` delete the `mesh-return-certgate` and `api-prebind-certgate` lines (invalid / duplicate of E's `-r2`). In `run-batch-2.sh` batch C delete `mesh-return` and `mesh-address` (or leave: no dir → they would run and come back INVALID, polluting SUMMARY.txt).
3. **Run the remaining mutations at `REF` (17 runs, ~2 h).** `bash $SP/mut-4.10/run-batch-2.sh B REF` (router-peer-refusal, adaptive-multiframe, loopback-http-ports, loopback-profile; `validator-brickhost-r2`/`validator-meshlab`/`ollama-default` skip as done), `bash $SP/mut-4.10/run-batch-2.sh C REF` (api-prebind, api-verifier; net10.0), `bash $SP/mut-4.10/run-batch-3.sh REF` (bedrock-composition, validator-bedrock-r2, router-overnight-only, mcp-marker-name, collection-attribute), `bash $SP/mut-4.10/run-batch-4.sh REF` (mesh-anyip, mesh-bindaddress, mesh-anyip-certgate, profile-read-convention, api-prebind-certgate-r2). Each must print `KILLED`; a `SURVIVED` means a twin is missing (fix twin, commit, re-run that mutant); `INVALID ... red-no-tests` means it did not compile (pick another). Background runs die at 2 h: run one batch per invocation. The three mesh mutants need the container to have a non-loopback IPv4 (`MeshServeLoopbackProfileTests.cs:84-85` fails otherwise — if so, that twin needs a seam, not a skip).
4. **Final verification at `REF` (4 runs + host).** `bash $SP/logs-4.10/final-verify.sh REF`: full cert-gate net8.0 + F6/F8 detailed (expect `Total tests:` = 2763 + new tests, `Failed: 0`, `Skipped: 0`; record `ScannedFiles=`/`ExaminedOccurrences=`), net8.0 neighbour classes, net10.0 touched (Infrastructure AG+VirtualProduction, `Ashlar.Tests.CLI` mesh/TLS/LAN/CLI-site, Mcp.Server.Tests, Mcp.Client.Tests), build-core (`dotnet restore && dotnet build Ashlar.LocalDevCore.slnf`), then host `PATH=$SP/sc-py/bin:$PATH bash scripts/ci/run-repo-gates.sh` → `all 26 gate(s) passed`. If F6 differs from 2,139/150, restate `EgressGuardConventionTests.cs:205-208` and row 64, commit, and re-run only F6 (1 run).
5. **Fill the body (0 runs).** Replace `COUNTS_PLACEHOLDER`, `MUTATION_TABLE_PLACEHOLDER` (SUMMARY.txt verbatim + `python3 $SP/mut-4.10/table.py`), `NOT_OBSERVED_PLACEHOLDER`; fix the mutation-refs paragraph for `REF`; make "build-core run" true; re-read the attack table's mutation names against SUMMARY.txt.
6. **Squash and push (0 runs).** `git reset --soft origin/master && git commit -F $SP/ws/commit-4.10-msg.txt` (message ends with the Fable 5.1 trailer and the session line), `git diff REF HEAD --stat` must be empty, `git push --force-with-lease -u origin claude/spec-007-pr4-4.10-agsw`. Report to the integrator: head SHA, counts, every summary line.
7. **After 4.3 merges (integrator, 2 runs).** `git fetch && git merge origin/master` (resolve: row 64 floors sentence, new-row numbering after 67, count paragraph, CHANGELOG `### Changed` order, `docs/EgressInventory.md` `:3`/section slot/Known limits/EG-MDL-02 cell, SPEC-007 bullet order 4.3 then 4.10, `EgressGuardConventionTests.cs` comment; never hand-merge the KG). Restate TSV **149** / `sdk.client` 8 in row 64 and the body; `git ls-files` count paragraph (expect 135/138); `git add -A && python scripts/knowledge-graph/build-knowledge-graph.py`; commit. Then one full cert-gate + F6/F8 run (restate floors from the measurement; re-check `AirGappedHygieneTests` under Warning 7304 (G13) and `EgressKernelFactoryTwinTests` (G19)), one net10.0 touched run, host repo gates; squash again onto master with the trailer; push; open the PR with the body (`(this PR)` → `#N`), post `handoff` on the bus.

Blockers: none hard. Soft: 4.3 must merge first for the count restatement (merge order); the mesh twin's non-loopback-interface requirement in the devtest container is unverified (the old refuse-to-serve twin passed there, the literal-Q6 twin has never run); no container has run on the current head.
