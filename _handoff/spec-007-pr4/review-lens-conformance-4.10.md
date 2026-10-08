# conformance-4.10: PR #720 (squash `02fa27f1d`) against DESIGN §4 row 4.10 / §2.7, owner Q6, D5, D41, critic G9–G13/G15/G16/G19

Read-only review on the shared clone `scratchpad/review-master` at master `095ba46e2` (2026-10-08). Every line
number below is at `095ba46e2` unless a PR or commit is named. Empirical probe: one container run in my own clone
`scratchpad/review-conformance-4.10` (commit `a579639f`, a review-only test file; log
`scratchpad/review/logs-conformance-4.10/probe-loopback.log`). No dotnet on the host; nothing edited in the shared clone.

PR facts (REST, `gh api repos/IanFrelinger/Ashlar/pulls/720`): head `fa454fabe`, merged `02fa27f1d` at 2026-10-06T23:02:19Z
by `cursor[bot]`. Required checks on `fa454fabe`: cert-gate ✔ 22:25Z, build-core ✔ 22:18Z, shell-lint ✔ 22:15Z,
lychee ✔, Readiness summary ✘ 22:30Z (`verdict=failed lanes_ran=2 lanes_skipped=1`) then ✔ 22:59Z. Master `02fa27f1d`:
Readiness verdict annotation `verdict=verified lanes_ran=4 lanes_skipped=0`. Agent-bus #695: last comment
2026-10-06T17:28Z (drift-716); **no `handoff`/`done` for #720**.

## 1. What master does (read of every production file #720 touched)

**Profile plumbing.** `src/Ashlar.Abstractions/AshlarResolvedDeploymentProfileOptions.cs` — public sealed class, settable
`Profile` (default `"full"`), `IsAirGapped`, `IsSecureWorkstation`, `RequiresLoopback => IsAirGapped || IsSecureWorkstation`
(`:23-34`). Registered in `src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs:132-138`:
`NoteResolved(canonicalProfile)` then `var notedProfile = AshlarDeploymentProfileEnvironment.Effective(canonicalProfile) ?? canonicalProfile;`
`services.AddOptions<AshlarResolvedDeploymentProfileOptions>().Configure(resolved => resolved.Profile = notedProfile);`.
`Effective(raw)` = `ResolvedRaw` when non-empty (`AshlarDeploymentProfileEnvironment.cs:47-48`), so the value is the
**strictest profile noted in the process at the time `AddAshlar` ran** (D5 ✔ for the same container; two `AddAshlar`
calls each add a `Configure`, the last one runs last, and both captured values are monotone). A Full container composed
*before* AG was noted keeps Full (G15, inherited; recorded nowhere in #720's records). Consumers take
`IOptions<AshlarResolvedDeploymentProfileOptions>` as an optional last ctor parameter (router, factory, mesh serve) or
through `IValidateOptions` (the four validators, the listener validator).

**Routing** `NcrCapabilityRouter.cs` (#720 diff): `PeerNetworkOnly` on AG → `InvalidOperationException("AirGapped: peer-network-only execution is unavailable…")`;
`PreferPeerNetwork` early return skipped on AG; every remote reason → `ExecutionTarget.Local(_localExecutor, "AirGapped: remote execution unavailable; running locally (<reason>)")`. D35 ✔.

**Factory** `src/Ashlar.Infrastructure/Execution/AdaptiveProviderFactory.cs:57-62`: LLM on AG = `{resolved}` if
`ollama|local`, else **`Array.Empty<string>()`** (a cloud resolve tries nothing). `:97-99`: single-image vision list
`{resolved, ollama, openai, azure}` minus `openai`/`azure` by literal name. `:134`: multi-frame still calls
`_inner.ExecuteVisionMultiFrameAsync(resolved, …)` on AG, cloud resolve included (design: catch-all row 25; the lane refused it).

**Validators** `ValidateAirGapped{RunPod,BrickHost,MeshLabWorker}Options.cs`, `src/Ashlar.AI.Pipeline/ValidateAirGappedMeaiBedrockOptions.cs`:
`IValidateOptions<T>` reading `IOptions<AshlarResolvedDeploymentProfileOptions>`, registered with `TryAddEnumerable` next
to each options' `AddOptions<T>().Bind(…).ValidateOnStart()` (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:36-40`,
`AshlarFederatedBrickMeshServiceCollectionExtensions.cs:31-35`, `MeshLabServiceCollectionExtensions.cs:28-32`,
`MeaiPipelineServiceCollectionExtensions.cs:96-100`). Meai moved from `Options.Create(options)` to the options pipeline with
`CopyMeaiPipelineOptions` (all 7 members copied, `MeaiPipelineOptions.cs:43-70` ✔). `AwsBedrockChatClientFactory(IOptions<MeaiPipelineOptions>)`
(`:27`) so Bedrock's first use resolves `.Value` and fires the validator even in a host-less composition (the design's
`ValidateOnStart` bar is met; the lane's composition-time refusal was stricter). A host that registers its own options
later cannot remove an `IValidateOptions` already added; a host that never runs `AddAshlar` gets Full. ✔

**ollama.com** `ModelArtifactCatalogServiceCollectionExtensions.cs:36-44`: after `Bind`, `Configure<IOptions<Profile>>` sets
`Enabled=false` on AG only when `configuration[…:Enabled] is null`. Explicit key or later `Configure` wins — "off by default", G10 wording ✔.

**MCP** `AshlarMcpHttpTransportMarker.cs` registered by `WithAshlarHttpTransport()` (`AshlarMcpServerServiceCollectionExtensions.cs:92-97`);
`ValidateAshlarMcpServerOptions.cs:20-23,47-52` refuses SW only when a marker is present. The only `.WithHttpTransport()`
call in the repo is the one inside `WithAshlarHttpTransport` (grep). A host calling the third-party `WithHttpTransport()`
directly (what Program.cs did before #720, and what DESIGN §2.7 `:617-618` literally describes) registers no marker and
boots MCP over HTTP on SW. No tripwire test forbids that call.

**API** `application/src/Ashlar.API/Program.cs:238-243`: `AddAshlarInboundListenerValidation(CollectEndpoints(builder.Configuration, builder.WebHost.GetSetting(ServerUrlsKey)))`
— pre-bind only, from `urls`/`ASPNETCORE_URLS` and `Kestrel:Endpoints:*:Url` (`AshlarInboundListenerPolicy.cs:76-94`).
**No post-bind check exists** (the lane's `LoopbackListenerVerifier : IHostedLifecycleService` is absent). Not collected:
`ASPNETCORE_HTTP_PORTS`/`HTTPS_PORTS` (Kestrel binds `http://*:<port>` from them when `urls` is unset; the aspnet base image
sets `ASPNETCORE_HTTP_PORTS=8080`, `tests/uat/README.md:89`), `UseUrls`/`ConfigureKestrel(ListenAnyIP…)`/`app.Urls.Add` in code.
The comment `:239` "Empty urls are the host default (localhost)" is false when `HTTP_PORTS` is set.

**Listener predicate** `AshlarInboundListenerPolicy.IsLoopbackEndpoint` (`:27-50`): bare `+`,`*`,`0.0.0.0`,`::`,`[::]` → false;
no scheme → `http://` prefixed; `Uri.TryCreate` fails → false; `IdnHost` equals `localhost` → true; else
`IPAddress.TryParse && IsLoopback`. So: `*.localhost` false (critic C5 ✔), `127.0.0.0/8` true, `::ffff:127.0.0.1` true, a
hostname resolving to loopback false (over-refusal, fail closed), `unix:`/`pipe:` false (over-refusal), bare `::1`
→ `Uri.TryCreate("http://::1")` → see §7 probe.

**Mesh serve** `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs:157-167`: `bindCandidate = BindAddress ?? "0.0.0.0"`;
`Refusal(_profile, [bindCandidate])` non-null → `LogError` + `throw InvalidOperationException` **before the first await**,
so `ExecuteAsync` returns a faulted task, `BackgroundService.StartAsync` returns it and `Host.StartAsync` faults: **an AG/SW
daemon with `ASHLAR_MESH_SERVE_PORT` set and no `ASHLAR_MESH_SERVE_BIND` fails boot.** With a loopback bind it reaches
`:313-314` → `ListenLoopback` (`:261-277`): `localhost` → `ListenLocalhost`, loopback IP → `Listen(ip)`, else
`ListenLocalhost` fallback — that fallback and `BindHost(null) → "localhost"` (`:281-282`) are dead on AG/SW because the
refusal already ran; not contradictory, just two policies in one file (the comment at `:157-158` is the live one). On
Full `:316` is always `ListenAnyIP` — **the knob is ignored** (docs say so: "Other profiles keep ListenAnyIP and do not apply
the bind", `docs/Configuration.md:387`, `docs/Federation.md:50`). Knob read at `BackgroundAgentDaemonCommand.cs:432-433`.
This is the owner's Q6 text literally ("must bind loopback, or boot fails"), not the integrator's INTEGRATION-NOTES-C
choice (bind loopback and keep serving); the integrator's choice is lower authority, the PR body states the behaviour,
and critic O3 said no decisions-log row is needed when the owner's text is followed. Recorded ✔, but **untested** (§3).

## 2. Design row 4.10 / §2.7 bullets, checked

| Bullet | Master | Evidence |
|---|---|---|
| Router Local on AG with the AG reason; explicit PeerNetworkOnly refused | ✔ | §1; twin facts 1–2 |
| Factory never escalates on AG: LLM | ✔ (and a cloud resolve tries nothing — recorded in the PR body and CHANGELOG, see F10) | `AdaptiveProviderFactory.cs:57-62`; twin fact 4 |
| Factory never escalates on AG: single-image vision | ✔ (by literal names `openai`/`azure`; provider names in the repo are `ollama local openai azure mock auto`, so complete today) | `:97-99`; twin fact 3 |
| Multi-frame | design-conformant (resolved only, catch-all row 25) — weaker than the lane | `:134`; no twin |
| Four options-bound `ValidateOnStart` validators | ✔ | §1; twin facts 5–6 (`.Value` path only, no host started) |
| ollama.com `Enabled` defaults false on AG | ✔ "off by default" | twin fact 7 |
| Profile reaches Infrastructure as an options value Hosting registers; Infrastructure never reads `Effective` | ✔ by grep (zero non-comment hits in `src/Ashlar.Infrastructure`, `application/src/Ashlar.API`, `application/src/Ashlar.CLI`; one doc-comment in `Autonomy/ValidateAshlarAutonomyOptions.cs:13`) — **no convention test**, while `Ashlar.Abstractions.csproj:40` now grants IVT to `Ashlar.Infrastructure` (#722) | F3 |
| Q6: MCP over HTTP fails boot on SW via a marker; stdio boots | ✔ for hosts that call `WithAshlarHttpTransport` | F5 |
| Q6: API Kestrel URLs loopback or boot fails | partial: two config sources, pre-bind only | F1 |
| Q6: mesh serve loopback or boot fails | ✔ literal, untested | F2 |
| SPEC-007 Known limits "responses on inbound connections are not mediated until PR 5" | ✔ | SPEC bullet `:136-137`; `docs/EgressInventory.md:183`, `:218`; CHANGELOG |
| D41: AG/SW-composing tests serialized + reset seam | ✔ twin: `[Collection("EnvironmentVariables")]`, `EgressProcessStateScope` field + `Reset()` per compose (`…HygieneCertificationTests.cs:30-36`, `:235`); `ProcessGlobalEnvironmentConventionTests` untouched by #720 (not in its stat) | F8 for the MCP test |
| D5 strictest-wins | ✔ in code; **no twin** for the options value (lane had `A_later_weaker_AddAshlar_keeps_the_AirGapped_routing`, mutation `d5-strictest` killed) | F9 |
| `[coordinated-integration]` | ✔ PR body first line |
| Cert-gate row | ✔ `ci/cert-gate-assertions.md:69`; count paragraph claimed 132/135 matched `git ls-tree 02fa27f1d` (132 `.cs`, 135 entries). At `095ba46e2` the paragraph says 135/138 vs actual 136/139 — later PRs' drift (4.5/#723), not 4.10's |
| Floors comment | "2,142 files, 150 occurrences" (`EgressGuardConventionTests.cs:205-206`), PR body "Scanned production files 2142; occurrences stay 150" from a repo-gates run — plausible (2,135 + 7 new files), stated as measured |
| `PublicAPI.Unshipped` +10 | the class, ctor, `Profile` get/set, `IsAirGapped`, `IsSecureWorkstation`, `RequiresLoopback`, three consts. All are needed only because the value is a public options class in Abstractions; the lane kept an internal Infrastructure singleton and shipped 0 lines | F4 |
| G9/G12 | ✗ no convention fact, no sentence saying the rule is now a convention | F3 |
| G10 | ✔ | SPEC `:133`, CHANGELOG, inventory |
| G11 post-bind window Known limit | ✗ absent (no "post-bind"/"after Build"/"HTTP_PORTS"/"in code" in SPEC, inventory, CHANGELOG, DEPLOYMENT) | F1 |
| G13 (7304 side effects after #722) | twin asserts nothing on logs or hosted-service counts; cert-gate green on #722/#721 heads | ✔ |
| G15 | unrecorded (lane had a Known limit + twin `A_container_composed_Full_before_AirGapped_was_noted_keeps_Full`) | F9 |
| G16 | n/a on master (no composition-time refusal; 4.11's C7 shape throws at first `.Value`, not composition) | — |
| G19 | n/a (no hosted verifier added) | — |

## 3. Tests and evidence (CLAUDE.md: "a check that was never observed failing is not evidence")

Twin `AirGappedSecureWorkstationHygieneCertificationTests` (9 facts, 357 lines). It never starts an `IHost`
(`ValidateOnStart`/"fails boot" inferred from `.Value` throwing), never constructs `MeshServeService`, never boots Kestrel,
never calls multi-frame vision, never composes two `AddAshlar` calls (D5), never tests the Program.cs call site.
`application/src/Ashlar.Tests.CLI` has no reference to `BindAddress`, `RequiresLoopback` or
`AshlarResolvedDeploymentProfileOptions` (grep) — the daemon's fail-boot path has **no test at all**. PR-body mutations:
6 (`ag-router-local`, `ag-vision-cloud`, `ag-runpod-validator`, `ag-ollama-default`, `sw-mcp-http`, `loopback-listener`);
none for BrickHost/MeshLab/Bedrock validators, D5, mesh serve, the API call site, or the LLM cloud-resolve rule. The
lane recorded 11 KILLED (`state-4.10.md` §4) with 17 more planned.

## 4. Findings

**F1 (blocker) — the API's "non-loopback listener fails boot" has a pre-bind-only check over two configuration sources; the design closes every listener.**
`Program.cs:238-243`, `AshlarInboundListenerPolicy.cs:76-94`. `ASPNETCORE_HTTP_PORTS`/`HTTPS_PORTS` (the .NET 8+ aspnet
image default; Kestrel binds `http://*:<port>` from them when `urls` is unset) and any listener added in code bind on
every interface on AG/SW with no refusal; there is no post-bind verification (lane: `LoopbackListenerVerifier`,
`The_port_only_binding_fails_boot_on_AirGapped`, `What_Kestrel_bound_is_checked_after_the_server_starts`). The comment
"Empty urls are the host default (localhost)" (`:239`) and the policy's "Empty means the host default, which is loopback"
(`AshlarInboundListenerPolicy.cs:10`) are false under `HTTP_PORTS`. Owner Q6: "the API's listeners … must bind loopback,
or boot fails". DESIGN §2.7 `:621-623` and G11 required at least a Known-limit sentence for the post-bind window; none
exists. Mitigation: Ashlar's own images set `ASPNETCORE_URLS=http://+:8080` (`.docker/Dockerfile.api:16`), which *is*
collected and refused. Probe: `Http_ports_only_configuration_yields_no_endpoints` (§7).
Fix: collect `http_ports`/`https_ports` into `http(s)://*:<port>` candidates when `urls` is unset; add an
`IHostedLifecycleService` that reads `IServerAddressesFeature` after start and fails on a non-loopback bound address;
record the residual window in SPEC-007 Known limits and `docs/EgressInventory.md`; `docs/DEPLOYMENT.md` note that the
image's `+:8080` fails boot on AG/SW.

**F2 (major) — mesh serve on AG/SW fails boot by design on master, but nothing observes it.** `MeshServeService.cs:157-167`
is reached only from the daemon; no test constructs the service with an AG profile (grep of `Ashlar.Tests.CLI`). The PR
body's blast-radius row "Docker / mesh / fleet / trust: Not exercised end to end; mesh-serve bind is a unit of
`AshlarInboundListenerPolicy` and `MeshServeService`" names no test, and no `MeshServeService` test exists for it. Also
unrecorded: the knob is ignored on Full (docs do say "do not apply the bind", so that is a design choice, not a defect)
and `ListenLoopback`'s `localhost` fallbacks (`:276`, `:281-282`) are dead code that contradicts the comment at `:157-158`.
Fix: a `Ashlar.Tests.CLI` twin (lane: `MeshServeLoopbackProfileTests`, 154 lines, net10.0) asserting the faulted
`StartAsync` on AG with no bind, a serving loopback bind, and Full unchanged; delete or exercise the dead fallbacks.

**F3 (major) — "Infrastructure never reads `Effective`/`ASHLAR_DEPLOYMENT_PROFILE`" is now an untested convention.**
`Ashlar.Abstractions.csproj:40` grants `InternalsVisibleTo Ashlar.Infrastructure` since #722; DESIGN §2.7 `:614-616`,
critic G9/G12 and INTEGRATION-NOTES-C ("4.10 adds the convention fact and its cert-gate row (or says the rule is a
convention)") — master has neither. `docs/EgressInventory.md:50` and the SPEC bullet state "Infrastructure does not read
the environment" as fact. Grep today: clean (one doc comment). Fix: port the lane's `DeploymentProfileReadConventionTests`
(roots Infrastructure/API/CLI, forbidden `AshlarDeploymentProfileEnvironment.` and `ASHLAR_DEPLOYMENT_PROFILE` outside
comment lines, floor 300 files) with its `ci/cert-gate-assertions.md` row (CLAUDE.md requires the row in the same change).

**F4 (major) — the profile value is a public, settable options class, so any host can lower it after `AddAshlar`.**
`AshlarResolvedDeploymentProfileOptions.Profile { get; set; }` (`:23`), registered through the options pipeline
(`AshlarServiceCollectionExtensions.cs:137-138`); a later `services.Configure<AshlarResolvedDeploymentProfileOptions>(o => o.Profile = "full")`
or `PostConfigure` turns off the router, factory, all four validators, the listener check and mesh serve on an AG
process. D6 ("a host guard cannot lower the mode on AG") is the spirit; the lane's `ResolvedDeploymentProfile` was an
internal Infrastructure singleton with the same exposure recorded as a Known limit and a twin
(`Host_code_that_registers_its_own_profile_after_AddAshlar_replaces_the_value`). Master records nothing and ships 10
`PublicAPI.Unshipped` lines for it. Fix: a `PostConfigure` in `AddAshlar` that re-asserts the noted strictest profile (or
make the class `init`-only and seal the registration), a twin, and a Known-limit sentence; or at minimum record the limit.

**F5 (major) — the SW MCP-over-HTTP refusal is opt-in by the host and has no tripwire.** `ValidateAshlarMcpServerOptions.cs:47`
fires only when `WithAshlarHttpTransport` registered the marker; the design's mechanism (§2.7 `:617-618`) was a marker
from `.WithHttpTransport()` itself, which the third-party builder cannot provide; the lane detected
`ModelContextProtocol.AspNetCore.StreamableHttpHandler` through `IServiceProviderIsService` and failed closed. On master
the only `.WithHttpTransport()` call is inside the Ashlar wrapper (grep), so the repo is clean today, but a host or a
future edit calling it directly boots MCP over HTTP on SW silently. The PR body records the mechanism ("refuses HTTP only
when `WithAshlarHttpTransport` has registered…"), so it is a recorded deviation; the gap is the missing convention fact.
Fix: a convention fact that no file outside `AshlarMcpServerServiceCollectionExtensions.cs` calls `.WithHttpTransport(`,
or detect the handler type as the lane did.

**F6 (major) — mutation evidence is thin against CLAUDE.md's "mutation-check every behavioural change".** PR body: six
mutants, one per behaviour family; the BrickHost, MeshLab and Bedrock validators, the LLM cloud-resolve rule, D5, the
mesh daemon and the Program.cs call site were never observed red. The lane's `state-4.10.md` §4 lists 11 KILLED at
`b8ebea42`/`792a8875` for the equivalent code with 17 more scripted. Fix: run the missing mutants on master
(`scripts/mutation-check.sh`), especially `validator-bedrock`, `validator-brickhost`, `validator-meshlab`, `d5-strictest`,
`mesh-anyip`, `api-prebind`.

**F7 (major) — no agent-bus `handoff`/`done` for #720.** CLAUDE.md "The loop" steps 1–2 and "When to post". #695's last
comment is 2026-10-06T17:28Z (drift-716); Grok's drift audit for #720 never ran. Fix: post `Kind: handoff About: pr-720`
naming the records (SPEC-007 bullet, `docs/EgressInventory.md:50/:142/:183/:187-188/:218`, `docs/Configuration.md:387`,
`docs/Federation.md:50`, CHANGELOG, `ci/cert-gate-assertions.md:69`, floors comment, KG) and `Kind: done` with `02fa27f1d`.

**F8 (minor) — D41 for the new MCP test.** `AshlarMcpServerServiceCollectionExtensionsTests.Enabled_http_transport_under_secure_workstation_fails_options_validation`
sets `ASHLAR_DEPLOYMENT_PROFILE` with no collection, riding the pre-existing exemption at
`ProcessGlobalEnvironmentConventionTests.cs:162` (a list "expected to SHRINK"). The convention test was not weakened
(#720 did not touch it). Fix: join a serialized collection in Mcp.Server.Tests or move the fact into the twin.

**F9 (minor) — D5/G15 for the options value have no twin and no record.** The value is captured at `AddAshlar` time
(`AshlarServiceCollectionExtensions.cs:135`); a Full container composed before AG was noted keeps Full; a later weaker
`AddAshlar` on the same collection keeps AG by construction. Neither is tested or written down (lane: two twins + Known
limit). Fix: port `A_later_weaker_AddAshlar_keeps_the_AirGapped_routing` and `A_container_composed_Full_before_AirGapped_was_noted_keeps_Full`; one Known-limit sentence.

**F10 (minor) — record wording.** CHANGELOG `:235-236` "A non-local resolved name adds neither cloud provider" understates
`AdaptiveProviderFactory.cs:60` (it tries nothing, the resolved name included); the PR body says it plainly ("a non-local
resolved name tries nothing"). `LoopbackListenerPolicy`'s doc "True for localhost, 127.0.0.1 and ::1" and the three records
promising `::1` (`CHANGELOG.md:239`, `docs/Configuration.md:387`, `docs/Federation.md:50`, SPEC `:136`) — see §7 probe for whether
bare `::1` is accepted. `docs/DEPLOYMENT.md` has no 4.10 note although every shipped image sets `ASPNETCORE_URLS=http://+:8080`
(lane had one at `docs/DEPLOYMENT.md:55`).

**F11 (minor) — multi-frame vision on AG.** `AdaptiveProviderFactory.cs:134` sends to a cloud `resolved` on AG; design
leaves it to catch-all row 25 (report-only until 4.11), PR body says "Multi-frame stays on the resolved name only". A
follow-up candidate from the lane (`Multi_frame_vision_on_AirGapped_refuses_a_cloud_resolve`), not a design breach.

## 5. Verified OK

- Required checks green on the PR head before merge; master `02fa27f1d` readiness `verified`, 4 lanes.
- D35 routing and factory rules as tabled; the four validators options-bound, `TryAddEnumerable`, `ValidateOnStart`, and
  first-`.Value` refusal in host-less compositions (Bedrock through `AwsBedrockChatClientFactory(IOptions<…>)`).
- `CopyMeaiPipelineOptions` complete (7/7 members).
- ollama.com "off by default" semantics and wording (G10).
- `*.localhost` refused by the listener predicate (critic C5); `0.0.0.0`, `::`, `+`, `*` refused.
- Twin in `EnvironmentVariables` with the reset seam (D41) ; `ProcessGlobalEnvironmentConventionTests` untouched.
- Cert-gate row present; count paragraph true at `02fa27f1d` (132/135 by `git ls-tree`).
- "Responses on inbound connections are not mediated until PR 5" present in SPEC, inventory (`:183`, `:218`), CHANGELOG.
- Mesh serve literal-Q6 behaviour recorded in `docs/Configuration.md:387`, `docs/Federation.md:50`, EG-MESH-03 `:142`, CHANGELOG `:238-240`.
- Infrastructure/API/CLI contain no non-comment read of the profile source (grep).

## 6. What the lane (`origin/claude/spec-007-pr4-4.10-agsw` @ `696d3819`) had that master lacks — follow-up candidates

1. `LoopbackListenerVerifier : IHostedLifecycleService` (post-bind check of `IServerAddressesFeature`) + `Program.cs` wiring (F1).
2. `LoopbackListenerPolicy.ConfiguredAddresses` collecting `http_ports`/`https_ports` as `http(s)://*:<port>` and programmatic urls (F1).
3. `AirGappedProfileApiHostProdStyleTests` +118: `A_non_loopback_listener_fails_boot` (4 rows incl. `Kestrel:Endpoints:Lan:Url`), `The_port_only_binding_fails_boot_on_AirGapped`, `SecureWorkstation_api_host_starts_on_loopback`, `SecureWorkstation_refuses_MCP_over_HTTP_at_boot`, `What_Kestrel_bound_is_checked_after_the_server_starts` (real Kestrel).
4. `MeshServeLoopbackProfileTests` (Ashlar.Tests.CLI, 154 lines): bind address per profile, serves on loopback and nowhere else, Full unchanged (F2).
5. `DeploymentProfileReadConventionTests` + cert-gate row (F3).
6. `AirGappedHygieneTests` 65 tests vs 9: every remote reason × preference table, `AirGapped_routes_locally_even_when_peer_routing_is_forced_past_the_validator`, `A_later_weaker_AddAshlar_keeps_the_AirGapped_routing`, `Multi_frame_vision_on_AirGapped_refuses_a_cloud_resolve`, `AirGapped_without_an_opt_in_boots` (real host start), `A_composition_after_AirGapped_was_noted_registers_AirGapped_and_refuses_the_opt_ins`, `A_container_composed_Full_before_AirGapped_was_noted_keeps_Full`, `Host_code_that_registers_its_own_profile_after_AddAshlar_replaces_the_value`, `SecureWorkstation_outbound_paths_are_unchanged`, two Bedrock composition twins, `The_API_and_mesh_serve_call_the_listener_checks` (source tripwire), loopback spelling table incl. unix sockets and pipes.
7. Composition-time Bedrock refusal inside `AddAshlar` (integrator decision, stricter than design); first-resolution refusal for all three Infrastructure validators (master gets this inherently via `IValidateOptions`).
8. Multi-frame vision cloud-resolve refusal on AG (F11).
9. MCP SW refusal by detecting `StreamableHttpHandler` through `IServiceProviderIsService`, fail-closed when it cannot tell (F5).
10. Internal `ResolvedDeploymentProfile` singleton (0 `PublicAPI.Unshipped` lines) with the host-replacement Known limit (F4).
11. Records: `docs/DEPLOYMENT.md` AG/SW image note; `docs/DocsIndex.md`, `docs/architecture/{ProtocolIntegration-MCP-A2A,README,product-split}.md`; TSV notes rows 14/51; the post-bind window Known limit; the G15 Known limit.
12. 11 KILLED mutants with red/green counts (`state-4.10.md` §4) vs 6 on master.

## 7. Probe (own clone, container run)

See the appended section below once `probe-loopback.log` completes.

### 7.1 Result (`probe-loopback.log:141`: `Failed! - Failed: 4, Passed: 9, Skipped: 0, Total: 13` net10.0, container run `a579639f`)

`AshlarInboundListenerPolicy.IsLoopbackEndpoint` at `095ba46e2`:

| Spelling | Result |
|---|---|
| `::1` | **false** (refused) |
| `http://::1:7420` | **false** (refused) |
| `::ffff:127.0.0.1` | **false** (refused) |
| `[::1]`, `http://[::1]:7420`, `[::ffff:127.0.0.1]` | true |
| `127.0.0.2`, `localhost:7420` | true |
| `api.localhost`, `0.0.0.0`, `http://unix:/tmp/x.sock` | false |

`Refusal(AirGapped, ["::1"])` = "AirGapped: inbound listeners must bind loopback until PR 5 … Non-loopback endpoints: ::1.
127.0.0.1 and localhost succeed." — so `ASHLAR_MESH_SERVE_BIND=::1`, which `docs/Federation.md:50`, `docs/Configuration.md:387`,
`CHANGELOG.md:239`, the SPEC-007 4.10 bullet (`:136`) and `AshlarInboundListenerPolicy.cs:24` all promise, **fails the daemon's boot**
on AG/SW (`MeshServeService.cs:159-166` passes `bind.Trim()` to `Refusal` before `BindHost` ever runs). Cause: `Uri.TryCreate("http://::1")`
fails for an unbracketed IPv6 literal (`AshlarInboundListenerPolicy.cs:36-40`). The twin only ever tested `http://[::1]:5000`
(`…HygieneCertificationTests.cs:222`).

`Http_ports_only_configuration_yields_no_endpoints` **passed**: `CollectEndpoints` with `HTTP_PORTS=8080` and no `urls` returns
`[]`, which the policy then allows on AG/SW (F1 confirmed by test, not only by reading).

**F12 (blocker) — a false claim in four merged records and a doc comment.** "`127.0.0.1`, `localhost` and `::1` succeed" is false for
the bare `::1` every record spells. Fix: strip brackets after prefixing (`http://[` + host + `]`) when the trimmed value parses as an
IPv6 address, or document `[::1]`; add the bare spellings to the twin's table; re-word the four records.
