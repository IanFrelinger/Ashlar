# SPEC-007 disposition index — routed enforcement and PR 4.10b Known limits

Tracking card: [#812](https://github.com/IanFrelinger/Ashlar/issues/812).

This index is the durable disposition map for the inherited **Known limits of routed enforcement** in
[`docs/EgressInventory.md`](../EgressInventory.md) and the **PR 4.10b Known limits** paragraph in
[`docs/specs/SPEC-007-security-labels-and-reference-monitor.md`](SPEC-007-security-labels-and-reference-monitor.md).
It does not repair runtime behaviour, accept release risk, authorize shipping 0.3.0, or change the guard's policy.
Release sign-off and any exception remain separate processes.

## How to read

| Column | Meaning |
|---|---|
| **Id** | Stable row id for citations (`RE-*` routed enforcement; `H10-*` PR 4.10b hygiene). |
| **Limit** | Short name matching the inventory / status wording. |
| **Evidence** | Current source or record that still shows the limit (paths relative to the repository root). |
| **Disposition** | Linked follow-up card, owner decision, or explicit retained/open disposition. |
| **Status** | `open` (still a disclosed limit), `retained` (owner or integrator chose to keep it), or `awaiting` (blocked on later work). |

Specific cards listed under [Precedence](#precedence-specific-cards-take-priority) own their scopes. This index does not override them.

## Routed-enforcement limits (`docs/EgressInventory.md`)

| Id | Limit | Evidence | Disposition | Status |
|---|---|---|---|---|
| RE-01 | Opaque / owned redirect transports and mutable chains | `src/Ashlar.Abstractions/Security/Egress/EgressRedirectHandler.cs` (follower splice and post-send origin check); inventory bullet under "Known limits of routed enforcement" | **Open design.** Needs a supported transport/ownership contract and boundary tests; no scoped repair card. Disclosed here and in the inventory. Release-risk acceptance is not inferred from this row. | open |
| RE-02 | P1 forwarding (custom headers, 307/308 bodies, Host hops, unknown-primary lifetime) | `EgressRedirectHandler.ApplyRedirect` clears `Authorization` only (`EgressRedirectHandler.cs` near the redirect apply); factory P1 vs `EgressHttp` P2 in inventory PR 4.3/4.3b prose | **Open design.** Needs an explicit forwarding policy and diagnostic-storage/lifecycle treatment; do not silently change P1/P2 semantics. No scoped repair card. | open |
| RE-03 | Bedrock endpoint reconstruction | `src/Ashlar.AI.Pipeline/Governance/MeaiEgressDestination.cs` (region reconstruction when `ProviderUri` is absent); twin `EgressGuardChatClientTwinTests.CloudBedrock_TheAwsClientReportsNoProviderUri_SoTheRegionIsReconstructed` | **Open design.** Needs authoritative destination provenance or a reviewed statement of the reconstruction boundary, plus twins for newly supported endpoint forms. Model-family decision remains fail-closed. No scoped repair card. | open |
| RE-04 | Logging startup / subscription gaps (MEAI Debug subscription; EG-TEL-01 before host build) | Inventory bullets on MEAI Debug subscription and EG-TEL-01; `AddAshlarEgressGuard` / hosted activator subscribe the Debug sink in Infrastructure; MEAI guard faults fail closed since PR 4.7 (#742) | **Open design** for subscription ownership / startup guarantees. Does **not** reopen the MEAI guard-fault fail-closed repair. No scoped repair card for the remaining startup gap. | open |
| RE-05 | Record granularity (shared sites / `http.factory` family) | Inventory "One site for several ids" and "Factory clients record `factory:<client name>`"; TSV / convention pins | **Retained aggregation (documented).** Finer attribution needs a separate design; today's records do not identify every sub-site. No scoped repair card. | retained |
| RE-06 | Host / loopback relays and cloud-model aliases | Inventory loopback/relay bullet; Ollama `-cloud` / `:cloud` recognition (PR 4.1); destination-table / per-endpoint labels remain open design (inventory historically cites "open question A"; letter **A** in the spec status now names `ParseOrPublic` — see note below) | **Open design** (destination table / per-endpoint labels). Interim integration contract only as already documented; no blanket claim that loopback proves data stays local. No scoped repair card. | open |
| RE-07 | netstandard2.0 synchronous-send hop and `InnerHandler` walking | `SynchronousSendRefusedOnNetstandard20Asset.cs`; `EgressHttp.cs` Wrap/CreateDelegatingHandler docs; `docs/SdkCompatibilityPolicy.md`; cert-gate `EgressHttpNetstandard20TwinTests` | **Retained platform contract (documented).** Preserve the no-send guarantee; distinguish netstandard2.0 from net8/net10. Supported runtime/client compatibility stays in the SDK policy. No scoped repair card. | retained |
| RE-08 | Multiple `Ashlar.Abstractions` assemblies (EventSource name/GUID collision) | Inventory bullet; PR 4.2 twin harness notes on duplicate `Ashlar-Egress` sources | **Open design.** Needs assembly-load / instrumentation ownership and a multi-`AssemblyLoadContext` control for any proposed fix. No scoped repair card. | open |
| RE-09 | O2 link-local source classified inside host boundary | `EgressRedirectHandler.InsideHostBoundary` (`EgressRedirectHandler.cs:238-239`) uses `IsInsideHost` or `IsLinkLocalHost` on the source too | **Open design.** Explicit source-boundary semantics/controls needed; not the same as a repaired outside-to-inside check. No scoped repair card. | open |
| RE-10 | Host-provided guard mode / break-glass without environment latch | `EgressGuard` constructor (`EgressGuard.cs:104-107`) and profile-bound `ResolveMode` (`:357-358`); `EgressEnforcement.ResolveMode` SecureWorkstation `break-glass` basis (`EgressEnforcement.cs:143`); AirGapped floor (inventory "AirGapped route floor"); design D6 | **Open design** for host-extension policy, mode provenance and diagnostics; respect existing host-guard decisions. No scoped repair card. | open |
| RE-11 | CLI refusals exit 77; `docker exec` limitation; bind-mount rejection; operator initiator pin | Inventory bullets citing PR 4.9b; decisions log 2026-10-14 rows for exit 77 and `docker exec`; `ProcessCommandRunner` / `EgressGuardConventionTests.F12_*` | **[#765](https://github.com/IanFrelinger/Ashlar/issues/765) (closed).** Owner chose retain-`docker-exec` (2026-10-14) and every-command exit 77. Cite #765 directly; this index does not reopen those choices. Untyped remote-refusal propagation remains under #802 / #803. | retained |
| RE-12 | Docker bind mounts do not prove a Host sandbox | Same as RE-11 (`HasNetworkOffRun` volume/mount rejection) | **[#765](https://github.com/IanFrelinger/Ashlar/issues/765) (closed).** Conservatively documented; owner's `docker exec` choice unchanged. | retained |

## PR 4.10b Known limits

| Id | Limit | Evidence | Disposition | Status |
|---|---|---|---|---|
| H10-01 | Code-added listener bind-to-post-start refusal window | `src/Shared/LoopbackListenerVerifier.cs` (`StartedAsync` after sockets open); pre-bind validation in host `Program.cs` files; inventory / SPEC-007 4.10b Known limits | **Open lifecycle disposition.** Track mitigation separately from widening host coverage. gRPC/Fleet wiring itself is [#766](https://github.com/IanFrelinger/Ashlar/issues/766) (closed); this residual window remains. | open |
| H10-02 | Container composed before a stricter process profile was noted keeps its captured profile | Inventory G15 note; process-profile noting in `AddAshlar` / `EgressProcessState` | **Retained lifetime rule (documented)** unless a later change records compatibility implications. No scoped repair card. | retained |
| H10-03 | Resolved options remain mutable (replace service / later `PostConfigure`) | `AshlarServiceCollectionExtensions.cs:137-141` (`Configure` + `PostConfigure` reassert); SPEC-007 Design-as-merged integrator default on profile reassert | **Open host-extension / trust-boundary disposition.** Integrator default reassert does not make options immutable. No scoped repair card; awaits owner if immutability is required. | open |
| H10-04 | Bedrock refused at ValidateOnStart / first options or keyed-client resolution | `ValidateAirGappedMeaiBedrockOptions.cs`; Design-as-merged Bedrock timing bullet | **Retained failure timing (documented integrator default)** until the owner chooses composition-time refusal and tests land. | retained |
| H10-05 | Inbound response bodies not mediated | SPEC-007 status PR 5 / 4.10b Known limits; Q6 loopback decision | **Awaits PR 5** server-side `CanRead`. Loopback binding is not response mediation. [#766](https://github.com/IanFrelinger/Ashlar/issues/766) closed host wiring only. | awaiting |

## Precedence (specific cards take priority)

| Card | Owns | Relation to this index |
|---|---|---|
| [#765](https://github.com/IanFrelinger/Ashlar/issues/765) | CLI exit 77, operator Initiator conventions, risky Docker mounts, owner retain-`docker-exec` | RE-11 / RE-12 — authoritative |
| [#766](https://github.com/IanFrelinger/Ashlar/issues/766) | gRPC / Fleet loopback wiring | Remove/update any "missing wiring" claim; retain H10-01 and H10-05 |
| [#758](https://github.com/IanFrelinger/Ashlar/issues/758) | Host-opt-out Warning EventId | Outside routed-enforcement aggregate; cite #758 |
| [#760](https://github.com/IanFrelinger/Ashlar/issues/760) | Duplicate MEAI refusal audit records | Outside this aggregate; cite #760 |
| [#759](https://github.com/IanFrelinger/Ashlar/issues/759) | `FailurePolicy.Continue` and refused brick | Outside this aggregate; cite #759 |
| [#802](https://github.com/IanFrelinger/Ashlar/issues/802) / [#803](https://github.com/IanFrelinger/Ashlar/issues/803) | Workflow refusal reporting | Outside this aggregate; cite those cards |
| [#805](https://github.com/IanFrelinger/Ashlar/issues/805) | Six unwindowed 7307 Warning sites | Outside this aggregate; cite #805 |
| [#809](https://github.com/IanFrelinger/Ashlar/issues/809) | Five remaining 4.8b reporting / lifecycle limits | Outside this aggregate; cite #809 |
| [#764](https://github.com/IanFrelinger/Ashlar/issues/764) | All-current-manifest mutation evidence campaign | Evidence campaign, not resolution of behaviours catalogued here |

## Reconciliation notes

- After **#765** / **#766**: repaired CLI/Docker/initiator and gRPC/Fleet wiring items must not be presented as still open missing work. Residual bind-window and inbound-body limits stay open/awaiting (H10-01, H10-05).
- After future **PR 5** response mediation: update H10-05 (and any inventory/status prose that still says inbound bodies await PR 5) so repaired limits are not left marked open.
- Rows marked **open** with "no scoped repair card" are explicit dispositions for tracking and release honesty: they stay disclosed; they are not silently closed; they are not release exceptions by themselves.
- **Letter drift:** `docs/EgressInventory.md` still says per-endpoint labels wait on "SPEC-007 open question A". In the spec's "Open questions raised since §8", letter **A** is the `ParseOrPublic` / receiver-side helper question. Destination-table / per-endpoint labels remain open design under RE-06; fixing the inventory's letter citation is editorial follow-up, not a behaviour change.

## Records this index updates

When this file lands or a row's disposition changes, keep these aligned:

- `docs/EgressInventory.md` — pointer under "Known limits of routed enforcement" and the PR 4.10b Known limits sentence
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md` — pointer on the PR 4.10b Known limits paragraph
- `CHANGELOG.md` — Unreleased note that the index exists
- `docs/DocsIndex.md` — link beside SPEC-007
