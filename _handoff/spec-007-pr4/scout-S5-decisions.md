# Scout S5: owner decisions for SPEC-007 PR 4, and the done-when test

Base: read-only clone at `8ec674d2a` (`$SP/pr4-base`, `git log -1` = "SPEC-007 PR 3b … (#711)"). Every `file:line` below is at
that commit. Nothing was built or run; claims that need a build are marked **(needs build)**.

Lens: what the owner has to decide before PR 4 code starts, what the seeded leak test should be, how to split PR 4, and
what PR 4 must leave to PR 5-8.

---

## 1. Facts

### 1.1 What the spec asks of PR 4 and what is open

- PR 4 row: "Guard enforces. Switched on per deployment profile; `AirGapped` and `SecureWorkstation` enforce by default."
  Done when "A seeded leak test (an agent tries to write labelled data down) fails closed with an explained refusal"
  (`docs/specs/SPEC-007-security-labels-and-reference-monitor.md:278`). PR 5: clearances on subjects and sealed-skill
  highest level (`:279`); PR 6 trusted downgrade (`:280`); PR 7 provisioned resources (`:281`); PR 8 container host (`:282`).
- §7: a MUST is enforced only where it names a passing test; refusals explained; fail closed to SystemHigh (`:306-311`).
- §2.3 (★-property, explained refusals) has **no "Enforced by" line** today (`:128-135`); PR 4's leak test is the first
  runtime test that can be named there.
- §8 Q1-Q6 are open (`:313-323`). Open questions A-E are open (`:385-434`): A receiver-side helper/TryParse (`:390-400`),
  B SystemHigh vs TopSecret (`:402-414`), C caveat scope, "now due before PR 4" (`:416-421`), D refusal-detail redaction
  (`:423-425`), E numbering (`:427-434`).
- Decisions log: the factory handler covers every `IHttpClientFactory` client, host's included (`:335`); C deferred to PR 4
  (`:338`). 3a scope: "PR 4 decides whether host-app clients are enforced" (`$SP/pr3a/SCOPE-3a.md`, Owner decisions, Q1).
- Gaps carried to PR 4: sync `Send` on the netstandard2.0 asset, redirects, Host-reading records (SPEC `:44-49`;
  `docs/EgressInventory.md:9`, `:10`, `:16-19`).

### 1.2 The guard as merged

- Mode is the constant `"report"` (`src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs:31`, stamped at `:106`);
  `EgressDecision.Mode` (`EgressDecision.cs:58`). `ProfileEnforcesByDefault` is computed from the configured profile
  (`EgressGuard.cs:128-132`) or from `ForbidsRemoteProtocolEgress(env)` (`:135-138`), which prefers the process-wide
  `ResolvedRaw` (`src/Ashlar.Abstractions/AshlarDeploymentProfileEnvironment.cs:8`, `:22-23`, `:56-60`).
- `AddAshlar` writes that process-wide value on every call (`src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs:111-119`);
  nothing outside its own file calls `ClearResolved` (grep). Ten test files name the AirGapped or SecureWorkstation profile,
  six of them in `Ashlar.Tests.Infrastructure` (five hosting/onboarding ones such as `Tests/Hosting/HostingDeploymentProfileTests.cs`
  and `Tests/VirtualProduction/AirGappedProfileApiHostProdStyleTests.cs`, plus `EgressGuardDecisionTests.cs`).
- No subject means SystemHigh (`EgressSubject.cs:47-53`; SPEC `:352-355`). **Verified: no production code enters a frame.**
  `EgressSubject.Enter` appears only in four test files (`EgressGuardDecisionTests.cs`, `EgressExplicitSiteTwinTests.cs`,
  `Ashlar.Tests.BackgroundAgents/EgressAgentSiteTwinTests.cs`, `Ashlar.Tests.CLI/.../EgressCliSiteTwinTests.cs`), and no
  production code constructs a `HighWaterMark` or calls a bridge (grep for `HighWaterMark`, `ToDataLabel`, `RecordLabel`).
- `EgressSubject.Enter` is public (`src/Ashlar.Abstractions/PublicAPI.Unshipped.txt:113`). A frame links to the active one
  (`EgressSubject.cs:41`) but `Resolve` reads only the innermost live frame's mark (`:49-52`): **a nested frame with a lower
  mark replaces the enclosing label.** The remarks say entering a frame "can only lower the current label from SystemHigh"
  (`:15-16`).
- Sealed skills cannot reach `Ashlar.Abstractions.Security.Egress`: the IL fence is an allowlist of `System.*` and
  `Ashlar.Core.Domain.Bricks|Execution` (`src/Ashlar.Infrastructure/Certification/IlImportFence.cs:34-38`, `:57-75`).
- Routes today all discard the decision:
  - HTTP handler: `Report` then `base.SendAsync` (`EgressGuardHandler.cs:51-55`), guard faults swallowed and counted
    (`:80-85`); sync `Send` only under `NET5_0_OR_GREATER` (`:57-64`, remarks `:13-18`).
  - MEAI: `EgressGuardChatClient.Decide` does `_ = _guard.Evaluate(Request)` and swallows a throwing guard
    (`src/Ashlar.AI.Pipeline/Governance/EgressGuardChatClient.cs:58-71`); it is the first `Use()` (outermost)
    (`AshlarGovernanceChatClientBuilderExtensions.cs:99-100`); the guard comes from DI or falls back to
    `ProcessDefault` (`:128-138`).
  - Explicit: 16 `Evaluate(new EgressRequest(...))` calls in 13 production files: `SelfExtendAdmissionBridge.cs:288`,
    `DotnetRunner.cs:16`, `SneakernetTransport.cs:36`, `FileBasedSharedAdaptationStore.cs:57`, `ProcessCommandRunner.cs:18`,
    `ValidationServiceAdapter.cs:528`/`:765`, `OllamaProposalSource.cs:132`, `BingWebSearchProvider.cs:56`,
    `PkgCommand.cs:93`/`:389`/`:444`, `MeshDiscoveryService.cs:249`, `MeshServeService.cs:313`, `NativeBundle.cs:88`,
    `application/src/Ashlar.API/Program.cs:247`. Five files are under `application/` (`[coordinated-integration]`).
- Factory handler site is `factory:` + client name, the unnamed default client is `factory:`
  (`src/Ashlar.Infrastructure/Egress/EgressServiceCollectionExtensions.cs:40`, `:63-69`). Ashlar itself uses the unnamed
  default client for remote bricks, the peer executor and workflow webhooks (`ci/egress-inventory.tsv:50`), so a host's
  default client and Ashlar's are indistinguishable by site.
- `AccessDecision.Detail` names levels and missing compartment/caveat tokens (`ReferenceMonitor.cs:57-88`); the docs say it
  is for operators only (`AccessDecision.cs:51-53`; `EgressDecision.cs:11-12`).

### 1.3 The existing refusal and agent plumbing a leak test can use

- PolicyGate already refuses by throwing `PolicyViolationException` (`PolicyGateChatClient.cs:54-76`); it allows every
  `local:*` and `peer:*` key unconditionally (`DefaultChatTargetAccessPolicy.cs:25-30`).
- `local:ollama` is registered governed in every profile (`MeaiPipelineServiceCollectionExtensions.cs:105-108`); its egress
  destination is the URL `OllamaEndpointResolver` gives (`MeaiEgressDestination.cs:108-114`), env first
  (`OllamaEndpointResolver.cs:24-32`). `AddAshlarGovernedChatClient` registers any key governed (`:165-174`); an unknown key
  is recorded as `meai:<key>`, ExternalModel (`MeaiEgressDestination.cs:135-141`).
- `MeaiBackedModel` is the `IModel` over a governed `IChatClient` (`src/Ashlar.AI.Pipeline/Models/MeaiBackedModel.cs:23`, `:61`).
- `ToolCallingAgent.RunCycleAsync` (`src/Ashlar.BackgroundAgents/Agents/ToolCallingAgent.cs:141`) calls the model (`:192`),
  approves each call through `PolicyEngine` (`:222`), invokes the tool (`:236`) and appends its payload to the
  conversation (`:247`); any non-cancel, non-`ModelUnavailableException` exception ends the cycle as `stopped=error` with a
  `react.error` memory record of `ex.Message` (`:271-276`). `ThinkAsync` swallows into `think.error` (`:122-127`).
- The only production caller of `RunCycleAsync` is `SelfExtendRunnerAdapter` (`src/Ashlar.BackgroundAgents.HostRunners/
  SelfExtendRunnerAdapter.cs:224-249`). Its snapshot carries unlabelled carry-over: scratchpad tail `RecentNotes`
  (`:363-368`), other agents' `RecentObservations` (`:374-386`) and a repo directory listing (`:388-400`).
- The labelled read path exists but no production agent registers it: `RAGTool` (`src/Ashlar.BackgroundAgents/RAG/RAGTool.cs:18`)
  returns each hit's `SensitivityLevelName` (`:104`) and searches at the snapshot's `maxDataSensitivity` (`:112-127`); no
  production code sets that snapshot key and nothing constructs `RAGTool` or `WebSearchTool` (grep). The default
  `IRAGService` is `MeaiVectorDataRagAdapter` over `VectorDataRagService` (`src/Ashlar.Hosting/Meai/MeaiVectorDataRagAdapter.cs:27-67`;
  `AshlarKernelRegistrar.Phases.cs:420-430`), which keeps the record's tier (`VectorDataRagService.cs:80-89`, `:102-139`).
- `TrustTierOrder.RecordLabel` maps the five canonical names (plus `top-secret`) and fails anything else to SystemHigh
  (`src/Ashlar.AI.Pipeline/Rag/TrustTierOrder.cs:58-68`, `:110-111`).
- Built-in sensitivity flags are monotone in level (`DataSensitivityLevels.cs:20-79`); custom levels set the four flags freely
  (`CustomSensitivityLevel.cs:18-38`); the bridge carries the level only (`DataSensitivityLabelBridge.cs:82-97`).
- `Ashlar.Tests.Infrastructure` references BackgroundAgents, HostRunners, Runtime, Hosting, Infrastructure and the API
  (`Ashlar.Tests.Infrastructure.csproj`), so a cert-gate test can drive the agent, RAG and MEAI pipeline in one process.
  The existing MEAI twin unsets `ASHLAR_OLLAMA_BASE_URL` and runs in the `EnvironmentVariables` collection
  (`EgressGuardChatClientTwinTests.cs:31-37`, `:111`).

### 1.4 Profiles and related defects (verified where they bear on a decision)

- AirGapped includes NCR and Adaptation and excludes BackgroundAgents (`AshlarServiceCollectionExtensions.Deployment.cs:110-121`);
  SecureWorkstation includes BackgroundAgents, Trust and WorkflowIntegrations (`:134-145`).
- Defect 5 (folded into PR 4 by the owner), verified: NCR registers RunPod routing (`AshlarKernelRegistrar.Phases.cs:69-72`),
  Adaptation registers the federated brick mesh (`:216-224`), the ollama.com catalog defaults `Enabled = true`
  (`src/Ashlar.Infrastructure/ModelArtifacts/OllamaRemoteLibraryCatalogOptions.cs:13`).
- Defect 3, verified: the MCP server validator checks only AirGapped (`src/Ashlar.Mcp.Server/ValidateAshlarMcpServerOptions.cs:29-36`).
- Defect 1, verified: the gRPC server forwards the caller's `TargetEndpoint` (`AgentTransportServiceImpl.cs:173`) and sends it
  (`:176`).
- Defect 6, verified: `AdaptiveProviderFactory` tries local then OpenAI/Azure, catching every exception
  (`src/Ashlar.Infrastructure/Execution/AdaptiveProviderFactory.cs:47-60`).
- Cert-gate: filter `FullyQualifiedName~Ashlar.Tests.Infrastructure.Tests.Certification` (`scripts/cert-gate-config.sh:6`);
  rule 2: a new blocking assertion adds its row in the same PR (`ci/cert-gate-assertions.md`, Rules); row 64 says
  "enforcement is PR 4" (`ci/cert-gate-assertions.md:64`).

---

## 2. Analysis

**Turning enforcement on without producers is a profile-wide off-host ban.** With no frame anywhere, every decision is
`no-subject`/SystemHigh, so on AirGapped and SecureWorkstation every destination outside the host boundary is refused with
`SystemHighData`. For AirGapped that is close to the intended meaning. For SecureWorkstation it removes cloud models (even
allow-listed), a remote Ollama, web search, workflow webhooks, mesh publish and share, telemetry to a remote collector, and
the NuGet restore inside `dotnet.build` (EG-PROC-01 records `nuget-feeds` unless `--no-restore`). A leak test under that
regime would pass trivially: the refusal would not be *caused by a label*.

**Most of what agents read is unlabelled, so honest producers mostly confirm SystemHigh.** The self-extend loop reads repo
files and puts unlabelled carry-over into its first prompt. §2.1 and §7 make all of that SystemHigh. The only labelled read
source in the tree is RAG (tier per record), and no production agent wires the RAG tool. So a producer adds precision only
where labels exist, and is never less safe than no-subject as long as unlabelled reads observe SystemHigh.

**A public frame is a declassification capability once the guard refuses.** Because the innermost frame wins, any in-process
code that enters a fresh `HighWaterMark` lowers the current label, including inside an agent's Secret frame. Sealed skills are
fenced off, but host code and every Ashlar tool are not. This must be fixed before enforcement, independent of the owner's
other choices.

**The refusal text leaves the guard.** It lands in agent memory (`react.error`), in API responses, and in host logs. Open
question D therefore has to be answered by PR 4's refusal type, not later.

**Process-global profile state will leak across tests and hosts.** `ProcessDefault` reads `ResolvedRaw`, which the last
`AddAshlar` wrote. Once that value refuses traffic, a test that composed AirGapped earlier in the process can turn unrelated
egress twins into refusals.

---

## 3. Decisions

Each item gives the question, options, the consequences for PR 4's code and for users, and a recommendation.

### D1. How is the mode chosen, and can it be overridden?

- **Question.** AirGapped and SecureWorkstation enforce by default. Can other profiles opt in? Can those two opt out?
- **Options.**
  - (a) Profile only: AG and SW enforce, every other profile reports, no override.
  - (b) Profile default plus opt-in (`AshlarHostingOptions.EgressMode` / `ASHLAR_EGRESS_MODE=enforce`). AG and SW cannot opt
    down.
  - (c) A two-way override. AG and SW can set `report`. It is logged at Warning, and each decision records `Mode=report`
    with `ProfileEnforcesByDefault=true`.
  - (d) (b), plus a SecureWorkstation-only break-glass opt-down that is explicitly named and recorded. Never on AirGapped.
- **Consequences.**
  - (a) Smallest change: the mode equals `ProfileEnforcesByDefault`. Full/Server users cannot rehearse enforcement before
    moving to SW, and SW users have no hatch.
  - (b) One option and one variable. An unreadable value must enforce (fail closed). SW users with a legitimate remote need
    have no hatch until labels exist.
  - (c) Easiest rollout, but one variable silently undoes the air-gap promise.
  - (d) AirGapped stays a hard promise, which matches the validators that refuse protocol surfaces there (`ValidateAshlarMcpServerOptions.cs:31`).
    SecureWorkstation gets an explicit, recorded hatch.
- **Design rule whatever the choice.** Bind the mode when the host is composed: `AddAshlar` registers a guard built with
  the resolved profile, so the mode does not hang only on the process-wide `ResolvedRaw` (see Risks).
- **Recommendation: (d).** Choose (b) if the owner wants no hatch at all.

### D2. Where does an agent's current label come from? (gap 4, subject frames)

- **Options.**
  - (a) No producers, as merged. Every decision is `no-subject` = SystemHigh.
  - (b) **Runner-declared frames with strict observation.** The code that builds an agent's inputs enters an `EgressSubject`
    frame with the floor it can vouch for:
    - `SelfExtendRunnerAdapter` uses SystemHigh, because its snapshot carries unlabelled carry-over (`:363-400`).
    - A runner that builds a clean snapshot may use Public.
    - `ToolCallingAgent` observes every tool result. A labelled hit counts at its label (canonical names only; anything else,
      custom levels included, is SystemHigh). An unlabelled result counts as SystemHigh.
    - With no frame, the decision is no-subject.
  - (c) A declared working label in agent config: a new field, or `MaxDataSensitivity` read in the data role. Unlabelled
    inputs are taken at that label, and labelled reads raise it.
  - (d) A Public floor, observing only labelled reads.
- **Consequences.**
  - (a) No code. SecureWorkstation loses all remote egress. The leak test cannot show cause, because everything off-host is
    refused anyway.
  - (b) Small code: frame entry, a public `EgressSubject.Observe`, a producer in `RAGTool`, and observation in
    `ToolCallingAgent`.
    - It is never less safe than (a).
    - On SW, self-extend works with a local (Host) model and is refused off-host.
    - Labelled RAG flows get precise decisions, so the leak test is meaningful.
  - (c) Makes SW plus a remote model usable. But it is an operator assertion that labels unlabelled data. That is clearance
    and label configuration (§8 Q3), it sits next to downgrade (PR 6), and it needs an explicit exception to §7.
  - (d) Breaks §2.1 and §7. Repo files read as Public is a write-down: fail open.
- **Recommendation: (b) in PR 4.** Defer (c) to PR 5 with §8 Q3. Reject (d).

### D3. Who may enter a frame, and may a nested frame lower the label?

- **Options.**
  - (a) Leave it as merged: public `Enter`, and the innermost frame wins.
  - (b) **Monotone nesting.** A nested frame's current label is `join(own mark, enclosing live current)`. Top-level `Enter`
    stays public, and a public `Observe` raises the label.
  - (c) (b), with `Enter` made internal, for Ashlar runners only.
- **Consequences.**
  - (a) A write-down by nesting. It must not ship with enforcement.
  - (b) Closes the nesting hole. A host can still declare its own top-level flow, recorded as `subject:<id>`, which it needs
    if D4 enforces host clients.
  - (c) The strongest option, but hosts can then never egress off-host on AG/SW. That is an Unshipped API change, which is
    allowed before the tag.
- **Recommendation: (b).** Add a twin: a Public frame nested inside a Secret frame still reads Secret.

### D4. Are host-app factory clients enforced? (3a owner Q1, deferred to PR 4)

- **Options.**
  - (a) Enforce every factory client.
  - (b) Enforce only Ashlar's clients. Ashlar's uses of the unnamed default client move to Ashlar-owned names first (TSV `:50`).
  - (c) Enforce host clients only inside a frame.
  - (d) (a), plus a per-client host opt-out list in `AddAshlarEgressGuard` options. Each decision records
    `mode=report basis=host-opt-out`, and the opt-out is ignored on AirGapped.
- **Consequences.**
  - (a) No code. Every non-loopback host call on AG/SW is refused unless the host enters a frame: a breaking change for SDK
    hosts.
  - (b) Needs a rename, a name registry and a convention rule. Host apps are untouched, but agent output handed to a host
    client is no longer covered.
  - (c) Context-dependent behaviour on the same client, and a later out-of-frame post goes unenforced.
  - (d) Same as (a), with an explicit, auditable hatch.
- **Recommendation: (d).** It is consistent with D1(d).

### D5. Operator-initiated exports and mesh paths before PR 6 exists

- **Background.** With no-subject = SystemHigh, every one of these is refused on AG/SW:
  - `pkg pack --out`, publish and share (`PkgCommand.cs:93`, `:389`, `:444`);
  - `export` (`NativeBundle.cs:88`);
  - `mesh export` (`SneakernetTransport.cs:36`);
  - the `improve` broadcast (`FileBasedSharedAdaptationStore.cs:57`);
  - mesh serve and discovery;
  - auto-share.

  The offline export is the air-gap's designed door (§1 lifecycle).
- **Options.**
  - (a) Enforce all of them. AG/SW become import-only until PR 6.
  - (b) Enforce agent-reachable and automatic sites (auto-share, broadcast, serve, beacon, telemetry, the process funnels,
    and the HTTP and MEAI routes). Keep the operator verbs (pkg pack/publish/share, export, mesh export) in report mode,
    recorded as `basis=operator-verb`. PR 6 makes them sign-off-gated downgrades.
  - (c) An operator `--release-as <label>` flag. This is a downgrade, so it belongs to PR 6.
- **Recommendation: (b).** It keeps the sneakernet workflow working and is honest about it in the record. If the owner reads
  AirGapped as "nothing leaves, ever", then (a).

### D6. Open question C: caveat scope (due before PR 4)

- **Options.**
  - (C1) Level-only for v1. The four flags do not become caveats, and no destination carries a caveat, so caveated data can
    only go to the host.
  - (C2) The flags become caveats (NOEXTLLM, NOWEB, …). The destination table gains caveats, the bridge starts emitting
    them (reversing the PR 2 decision), and the token names have to be reserved (§8 Q2).
  - (C3) Level-only, plus a fail-closed normalisation. A custom level's effective level becomes the lowest built-in level
    whose flags are no more permissive than its own.
  - (C4) Defer again.
- **Consequences.**
  - (C1) No PR 4 code. Custom non-monotone levels are under-enforced if a producer ever reads them. D2(b) avoids that by
    mapping only the five canonical names.
  - (C2) Changes the destination table, its derivation twin and PR 2's bridge.
  - (C3) Small. It may over-refuse a custom level that forbids only web search.
  - (C4) Needs owner consent, because the owner made C due before PR 4.
- **Recommendation: C1.** Write C3 into the spec as the rule for the first producer that labels data from an
  `IDataSensitivityLevel`. ORCON and REL TO stay out of scope, and §8 Q1 stays open.

### D7. Open question D, and how a refusal is surfaced

- **What the subject sees. Options.**
  - (D-a) The full `Detail`.
  - (D-b) A reason category, the site, the destination class, and the decision sequence as a correlation id. `Detail` goes
    only to the `Ashlar-Egress` log.
  - (D-c) The reason plus level names, but never compartment or caveat tokens.
  - (D-d) Redaction that depends on the profile.
- **The exception type. Options.**
  - One public sealed `EgressRefusedException` in `Ashlar.Abstractions.Security.Egress`, carrying the non-sensitive decision
    fields and used on all three routes.
  - Types per route: an `HttpRequestException` subclass for HTTP, and `PolicyViolationException("egress_refused")` for MEAI.
  - A synthetic 403 on HTTP. This reads like a remote answer, so it is not "explained".
- **Consequences of D-b.**
  - Agent memory and API callers learn the category, never compartment names.
  - Operators join on the sequence number.
  - Not deriving from `HttpRequestException` keeps retry wrappers from retrying a deterministic refusal.
- **Recommendation: D-b, one exception type.** `ToolCallingAgent` maps it to `StoppedReason = "egress_refused"` and an
  `egress.refused` memory record.

### D8. Open question A: receiver-side helper and the shape of `TryParse`

- **When PR 4 is affected.** Only if PR 4 parses a label from text in a receiver role: per-destination label overrides, or
  D2(c). There, `ParseOrSystemHigh` would turn a typo into a destination that receives everything.
- **Options.**
  - (A-a) Add `ParseOrPublic`.
  - (A-b) Change `TryParse` to `out SecurityLabel?`.
  - (A-c) PR 4 adds no label-text configuration, and A stays open for PR 5.
- **Recommendation: (A-c).** Destination labels stay the fixed table. If the owner wants destination overrides in PR 4,
  (A-a) is the smaller change and must land first.

### D9. Open question B: SystemHigh vs TopSecret visibility

- **Options.**
  - (B-a) PR 4 leaves the read side on ranks. It flips none of the assertions named at SPEC `:406-412`, and B stays open for
    PR 5.
  - (B-b) PR 4 switches RAG to `CanRead`. TopSecret callers lose unlabelled records.
  - (B-c) Answer now: no subject holds SystemHigh, only the host boundary does.
- **Note.** PR 4's producer uses `RecordLabel`, so an unlabelled record served to a TopSecret agent makes the agent
  SystemHigh for egress. §2.1 requires that, so it is not a choice.
- **Recommendation: (B-a).** Record (B-c) as design-as-merged.

### D10. The synchronous `Send` gap on the netstandard2.0 asset

- **Options.**
  - (a) Add a `net6.0` TFM to Abstractions. It is end-of-life; NETSDK1138 has to be suppressed under `TreatWarningsAsErrors`,
    and the build gets longer.
  - (b) **Fail closed on the uncovered combination.** On the netstandard2.0 asset, running on .NET 5-7 (where a synchronous
    `Send` exists) under an enforcing mode, `EgressHttp` and the factory handler refuse to build, with an explained message.
  - (c) Document only.
- **Consequences.**
  - (b) Changes nothing for net8/net10 hosts. Ashlar's own executables target those, and .NET 6 and 7 are out of support.
    Consumers on .NET 5-7 cannot use the guarded clients under AG/SW.
- **Recommendation: (b).** It is an owner decision because it narrows the runtimes consumers can use.

### D11. Redirects and the Host-reading records (technical; recommendation only)

- **Recommendation.** Close all three before anything refuses (PR 4a):
  - Turn automatic redirects off on the `EgressHttp` handler and the factory primary handler.
  - Follow redirects in a handler above the guard, so every hop is decided.
  - Classify EG-MESH-03 through a proxy or with forwarded headers, EG-MDL-01 with a custom inner client (and `local:onnx`
    with a custom inner), and EG-MESH-07/08 `//127.0.0.1` paths by their family, not as Host.
- **Alternative.** Keep those sites in report mode until they are fixed. That is weaker, and harder to see.

### D12. AirGapped registrations (defect 5, folded in by the owner)

- **Options.**
  - (a) Do not register them on AirGapped: RunPod routing, the peer executor, federated remote bricks and their catalog,
    and the ollama.com catalog (`Enabled=false`).
  - (b) Rely on enforcement.
  - (c) Both.
- **Recommendation: (c).**
  - It can be a small, independent PR before the switch.
  - AirGapped hosts that configure remote bricks or RunPod lose them, which contradicts AirGapped anyway.
  - The guard stays as the backstop.

### D13. The other 3a defects (1, 2, 3, 4, 6, 7, 8)

- **Recommendation.** Keep them out of PR 4 and queue them as follow-ups.
  - Under AG/SW enforcement the guard already refuses the outbound hop of 1, 6 and 7, but they stay open on Full/Server.
  - 2 and 3 are inbound or read-side.
  - 4 and 8 are policy bugs.
- **Exception.** Fold 3 (the MCP HTTP server allowed on SW) into the AirGapped-hygiene PR only if the owner confirms it must
  be off on SecureWorkstation. The stdio server stays allowed (`AshlarDeploymentProfileEnvironment.cs:50-55`).

### §8 Q1-Q6 and E

None is forced if PR 4 stays level-only with no label-text configuration:

- Q1: C1 leaves it open.
- Q2: no compartments are configured.
- Q3: D2(b) avoids it; D2(c) would force it.
- Q4: no.
- Q5: the high-water mark is join-only, and aggregation stays open.
- Q6: no.
- E: PR 4 does not edit `SPEC-006:551`.

---

## 4. The done-when test: `EgressEnforcementLeakTests`

**Where it lives.**

- File: `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressEnforcementLeakTests.cs`.
- Namespace: `Ashlar.Tests.Infrastructure.Tests.Certification`, so the cert-gate filter selects it
  (`scripts/cert-gate-config.sh:6`).
- `[Trait("Category","Certification")]`. It joins `[Collection("EnvironmentVariables")]` only to unset `ASHLAR_OLLAMA_BASE_URL`,
  as the existing twin does. To avoid the collection, use a `peer:` key instead (see Path).

**Path (all production types).**

1. DI:
   - Register `IEgressGuard` as `new EgressGuard("secure-workstation")`. The profile is explicit, so the test does not depend
     on the environment or `ResolvedRaw`.
   - Call `AddAshlarMeaiPipeline` with `o.OllamaBaseUrl = "http://egress-leak-<guid>.example:11434"`, a scripted stub
     `ollamaInnerFactory`, and a fake onnx client.
   - Resolve the keyed `local:ollama` client. Its outermost layer is `EgressGuardChatClient`
     (`AshlarGovernanceChatClientBuilderExtensions.cs:99-100`), and PolicyGate allows `local:*`, so only the egress guard can
     stop the call.
   - Alternative that needs no env: `AddAshlarGovernedChatClient("peer:leak-<guid>", stub)`, recorded as
     `meai:peer:leak-…` (ExternalModel).
2. Model: `new MeaiBackedModel(client, NullLogger)`.
3. Agent: `new ToolCallingAgent("leak-<guid>", model)`, then `RunCycleAsync(snapshot, toolbox, new PolicyEngine([]), null, memory, ct)`.
4. Label source: a real `RAGTool` over a real `MeaiVectorDataRagAdapter(VectorDataRagService)`.
   - Index one record at tier `"Secret"` whose text holds a canary `CANARY-<guid>`.
   - The snapshot carries `agentId` and `maxDataSensitivity = "Secret"`, so the agent is allowed to *read* Secret.
   - The test, acting as the runner, enters the agent's frame with a Public floor. Its snapshot holds no carry-over, which is
     exactly what D2(b) allows a runner to declare.
   - PR 4's producer observes each hit's tier.
5. Scripted inner client:
   - Call 1 returns a `rag_search` tool call whose query is the indexed text, so the hit scores 1.0.
   - Call 2 would return `{"tool_calls":[]}`.
   - The stub records every message it receives.
6. Egress attempted: call 2, the agent's next model call. Its conversation now holds the Secret chunk, and it goes to
   ExternalModel (Internal) at site EG-MDL-01.

**Expected refusal (the leak case).**

- **What reaches the stub.** Exactly one call, and none of its messages contains the canary.
- **The cycle result.** `StoppedReason == "egress_refused"`.
- **Agent memory.** One `egress.refused` record. Its text names the reason category, the site and the decision sequence, and
  contains neither the canary nor the label tokens.
- **The published decision.** A sink subscribed through `EgressDecisionLog.Subscribe` and filtered by
  `subject:agent:leak-<guid>` sees one decision for call 2:
  - `Mode == "enforce"`, `Site == "EG-MDL-01"`, `Family == model.meai`;
  - `DestinationClass == ExternalModel`, `DestinationLabel == Internal`;
  - `Current == Secret`;
  - `Access.Reason == LevelTooLow`, and `Access.Detail` names both levels.

**Controls in the same class (they make the cause provable).**

- (i) The record indexed at `"Internal"`: call 2 is allowed, the stub sees two calls, and `StoppedReason == "empty"`.
  Refusal is caused by the label.
- (ii) The Secret record under `new EgressGuard("full")`: call 2 goes through, and the decision says would-refuse with
  `Mode == "report"`. The switch is per profile.
- (iii) A twin for D3: a Public frame entered inside the Secret frame still decides Secret.

**Mutations, each must go red with counts.**

1. The chat client discards the decision.
2. The `RAGTool` producer does not observe.
3. Mode resolution returns report for `secure-workstation`.
4. `CanWrite` always refuses (control (i) catches it).
5. Nested frames win again (control (iii) catches it).

**Records in the same PR.**

- A new row in `ci/cert-gate-assertions.md`: "An agent that has read labelled data cannot write it down: under an enforcing
  profile the egress guard refuses, explained, before the send."
- "Enforced by" lines under SPEC-007 §2.3 (★-property and explained refusals).
- Row 64's "report-only / enforcement is PR 4" sentences revised.

---

## 5. Proposed split (serial, each mergeable, in this order)

| PR | Content | Done when |
|---|---|---|
| **4a** gaps | Sync-`Send` rule (D10); redirects followed above the guard; the three Host-reading records reclassified (D11). Still report-only. Split into 4a-1 (HTTP layer, Abstractions and Infrastructure) and 4a-2 (sites: AI.Pipeline and CLI, `[coordinated-integration]`) if it is large. | Twins: a 307 to another host records two decisions and the second names the new host; each Host-reading case records its family class; the netstandard2.0 rule refuses under enforce, simulated. All mutation-checked. |
| **4b** AirGapped hygiene | D12, plus defect 3 if the owner folds it in. | A cert-gate twin composing `AddAshlar(AirGapped)` finds no RunPod client, no peer routing, no remote brick catalog, and the ollama.com catalog disabled; Full is unchanged. |
| **4c** subject frames | D2(b) and D3: monotone nesting, public `Observe`, the runner frame in `SelfExtendRunnerAdapter` (SystemHigh floor), observation in `ToolCallingAgent`, the `RAGTool` producer. Still report-only. | The leak test's skeleton passes in report mode: after a Secret hit the next model call is recorded `subject:agent:<id>`, `Current=Secret`, would-refuse `LevelTooLow`; an Internal hit would-allow; the nested-frame twin holds. |
| **4d** the switch (HTTP and MEAI) | D1 mode, D4 host clients, D7 refusal type and redaction; handler and chat client refuse, a fault refuses; `ToolCallingAgent` maps the refusal; existing "never refuses" twins flipped by name. | **`EgressEnforcementLeakTests` green, with its cert-gate row and the §2.3 "Enforced by" lines; mutations 1-5 red.** This meets the §5 done-when. |
| **4e** explicit sites | The 16 calls switch from discarding the decision to acting on it, per D5; a convention rule bans a discarded explicit decision (`_ = …Evaluate(`); row 64 updated; `[coordinated-integration]`. | Explicit-site twins refuse under `secure-workstation` for agent-reachable sites and record report for operator verbs; the convention rule is mutation-checked; SPEC-007 marks PR 4 merged. |

Every PR updates the SPEC-007 status, `docs/EgressInventory.md`, the CHANGELOG and the knowledge graph (regenerated after
`git add`), and posts an agent-bus `handoff`.

---

## 6. What PR 4 must not do (it belongs to PR 5-8)

- **PR 5.**
  - No agent or caller clearances, no `CanRead` on the read side, no switch of the RAG filter or `IsAllowed` to labels, and
    no flipping of the B-named legacy assertions.
  - No clearance configuration (§8 Q3) and no config-declared working label (D2(c)).
  - No sealed-skill highest-level manifest field and no composition refusal.
  - No receiver-side label parsing (A).
  - No `CanRead` at the inbound server seam (EG-SRV-*).
- **PR 6.**
  - Nothing that lowers a label: no API to lower a mark or reset a frame, no per-site allowlist that lets labelled data out,
    no `--release-as`, and no sanitiser-as-downgrade (`SanitizingChatClient` redaction must not lower the label).
  - No downgrade receipts and no human sign-off.
- **PR 7.** No labels on provisioned databases, containers or ephemeral models; no teardown receipts. The EG-STORE rows stay
  `Exempt:`.
- **PR 8.** No `INativeExecutionHost`, and no compartment isolation through containers.
- **Outside every PR here.** Labels in signed records (§8 Q4), REL TO (Q1), a compartment registry (Q2), the aggregation rule
  (Q5), model mode (Q6), and the SPEC number (E).

---

## 7. Risks

- **Process-global profile.**
  - What happens: `ResolvedRaw` is set by every `AddAshlar` and never cleared (`AshlarServiceCollectionExtensions.cs:111`),
    and `ProcessDefault` reads it at each decision (`EgressGuard.cs:135-138`).
  - Effect in tests: five Tests.Infrastructure hosting/onboarding files compose AG or SW, so test order could turn later egress twins into
    refusals.
  - Effect in production: two hosts in one process share the mode.
  - Mitigation: bind the mode at composition (D1).
- **Hosted services.** A refusal thrown out of `BackgroundService.ExecuteAsync` stops the host under .NET's default
  `StopHost` behaviour. Mesh pull, the endpoint health monitor, the MCP client's hosted service and model warm-ups need
  explicit catches. **(needs build/test)**
- **Catch-all fallbacks blur the explanation.**
  - `AdaptiveProviderFactory.cs:47-60` swallows a refusal and tries the next provider.
  - `ToolCallingAgent.cs:122-127` and `:271-276` turn it into `think.error` / `react.error`.
  - Each needs a specific catch, or the explanation is lost.
- **SecureWorkstation breakage at the switch.** Everything listed in §2 Analysis is refused for no-subject flows. In
  particular, `dotnet.build` restore (EG-PROC-01) after an unlabelled read fails unless it runs with `--no-restore`.
  Operators need release notes and a dry run of the report-mode decisions first.
- **A public frame is declassification** until D3(b) lands. 4c must merge before 4d.
- **The leak test and the environment.** `OllamaEndpointResolver` reads `ASHLAR_OLLAMA_BASE_URL` first (`:27`). Unset it in
  the `EnvironmentVariables` collection, or use a `peer:` key.
- **API surface.** New public members (`Observe`, `EgressRefusedException`, the mode) go into `PublicAPI.Unshipped.txt`.
  Whether they ship as stable or experimental is still to be decided before the next tag.
- **Merge mechanics.** `application/` edits in 4a-2 and 4e need `[coordinated-integration]`. Readiness lanes are
  diff-conditional, so the cert-gate leak test is the evidence, not a skipped lane.
