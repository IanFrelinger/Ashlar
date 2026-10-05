# SPEC-007 PR 4: design, final (after three critiques)

This revises `DESIGN-4.md` with three critiques: code-truth, security and scope. Every finding was checked against the
read-only clone at `8ec674d2a` (PR 3b merged) before it was accepted. Nothing was built or run. §7 lists each finding
as accepted or rejected, with the reason.

**Citation rule.** Unmarked `file:line` citations were re-read at `8ec674d2a`. **[S1]**…**[S5]** marks a citation that
rests on that scout's reading and was not re-read. **[needs a test]** marks a claim about the .NET runtime or a
third-party library that only a run can settle. Appendix A lists the corrections to earlier citations.

**What changed most, compared with DESIGN-4.md:**
- The split has 11 PRs, not 9. A new **4.6, mode plumbing** lands before any route acts. Without it, no PR before the
  switch could produce an enforcing decision to test, because `EgressDecision`'s constructor is internal, no test
  project has `InternalsVisibleTo`, and `ProcessDefault` has no seam.
- The refusal surface is split in two (4.7 routes, 4.8 catch-alls and remote exits). Six more masking sites are listed.
- There are two new owner questions, both from the security critique:
  - Q3: child processes that run code an agent can write are `host:` today, so they are never refused;
  - Q6: responses on inbound connections are unmediated.
- O6 (the defect 5 mechanism) and O8 (compatibility) become defaults.
- The leak test no longer claims it is free of the environment. Control C2 now tests what it claims. Three scenarios
  and five controls were added.
- AG is hardened against being turned off:
  - the strictest profile seen in the process wins;
  - the mode override is read once;
  - on AG, a host guard cannot lower the mode.

---

## 1. The problem, in one screen

**What "enforcing" means.** SPEC-007 §5 PR 4 says: "Guard enforces. Switched on per deployment profile; `AirGapped`
and `SecureWorkstation` enforce by default." It is done when "a seeded leak test (an agent tries to write labelled data
down) fails closed with an explained refusal" (`docs/specs/SPEC-007-security-labels-and-reference-monitor.md:278`).
In practice:

- **When the guard refuses, the send does not happen.** Today every route throws the decision away:
  - the HTTP handler calls `_ = …Evaluate(egress)` and then sends (`src/Ashlar.Abstractions/Security/Egress/EgressGuardHandler.cs:51-55`, `:74-85`);
  - the MEAI layer calls `_ = _guard.Evaluate(Request)` (`src/Ashlar.AI.Pipeline/Governance/EgressGuardChatClient.cs:58-71`);
  - the 16 explicit sites call `_ = EgressGuard.ProcessDefault.Evaluate(...)`. Examples:
    `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshServeService.cs:313`,
    `application/src/Ashlar.API/Program.cs:247` and
    `src/Ashlar.BackgroundAgents/WebSearch/BingWebSearchProvider.cs:56` (the count is [S1][S5]).
- **A refusal is explained** (§7, `SPEC-007:310`), fails closed (`:311`), and is never retried, masked or reworded on
  its way to whoever sees it.
- **The mode is a constant today:** `ReportMode = "report"` (`src/Ashlar.Abstractions/Security/Egress/EgressGuard.cs:31`,
  stamped at `:106`). `ProfileEnforcesByDefault` is only reported (`:126-139`).

**No subject frames exist.** All five scouts verified this.
- `EgressSubject.Enter` is called only from four test files [S1][S2][S5].
- With no live frame, `Resolve` returns `(SystemHigh, "no-subject")` (`src/Ashlar.Abstractions/Security/Egress/EgressSubject.cs:47-53`).
- `CanWrite` with a SystemHigh source refuses every destination except a SystemHigh one, with `SystemHighData`
  (`src/Ashlar.Abstractions/Security/ReferenceMonitor.cs:53-63`).
- Only Host-class destinations are SystemHigh: loopback, `*.localhost`, `unix`/`npipe` and `host:` names
  (`EgressGuard.cs:8-14`; `src/Ashlar.Abstractions/Security/Egress/EgressDestinations.cs:62`, `:99`, `:139-142`, `:195-198`).

**So naive enforcement means host-only egress.** Flipping the mode alone makes AirGapped (AG) and SecureWorkstation
(SW) refuse everything that leaves the machine, whatever the data:
- cloud and remote models, web search, webhooks, RunPod and peers, OTLP to a remote collector;
- mesh serve, publish and share; **mesh pull** (auto-pull and pull over HTTP: `EG-MESH-04` is family `mesh.pull`,
  which is NetworkExport, at `application/src/Ashlar.CLI/Commands/BackgroundAgent/MeshAutoPullService.cs:72` and
  `EgressDestinations.cs:78-79`);
- `pkg export --out`, `export` and `mesh export`;
- `dotnet build` restores, recorded as `nuget-feeds` even from a local feed [S2 F9];
- enclave-LAN hosts, because a private address is not Host [S1 §1.6].

So, without operator exports, AG and SW can **import only from files**.

The leak test would also pass *vacuously*. Every refusal would be `SystemHighData` with basis `no-subject`, so no
refusal would be caused by a label (S2 D3c, S5 §2).

**Three more things must be fixed first**, or enforcement is unsound:
- **The known gaps** let traffic go out unevaluated, or classify a remote peer as Host (`SPEC-007:44-49`):
  synchronous `Send` on the netstandard2.0 asset, redirects, and three records that can read Host.
- **Nested frames replace, they do not join.** `Resolve` reads only the innermost live frame (`EgressSubject.cs:47-53`).
  Once the guard refuses, any code that enters a fresh frame lowers the label (S2 F11, S5 §2).
- **Many catch-alls would swallow, retry or reword a refusal** (§2.3).

**Two more things the guard does not see** (security critique; Q3 and Q6):
- **Child processes that run code an agent can write.** `dotnet test --no-build` is recorded as `host:dotnet`, which
  is Host, so it is always allowed (`src/Ashlar.Tools.Dev/DotnetRunner.cs:52-58`; `DotnetTestTool.cs:19-20`). The
  self-extend toolbox gives an agent `repo.fs.write`, `dotnet.build` and `dotnet.test`
  (`src/Ashlar.BackgroundAgents.HostRunners/RepoFsToolboxFactory.cs:93-109`). Its write allow-list includes `src/` and
  `tests/` (`src/Ashlar.Policies.Dev/PathAllowlist.cs:38-41`).
- **Responses on inbound connections.** On SW, MCP over HTTP serves `repo.fs.read` output to remote clients with no
  egress decision (`docs/EgressInventory.md:173-181`; `application/src/Ashlar.API/Program.cs:156`, `:167-168`, `:395`).

---

## 2. The design, end to end

### 2.1 Enforcement mode: per profile, and how it is resolved

**Mode table after the switch (4.11).** It follows Q1 (recommendation shown) and Q5.

| Profile | Default | `ASHLAR_EGRESS_MODE=report` | `ASHLAR_EGRESS_MODE=enforce` | Host per-client opt-out (Q5) |
|---|---|---|---|---|
| AirGapped | enforce | **ignored**: Warning at startup, `ModeBasis` records it | enforce | ignored |
| SecureWorkstation | enforce | **break-glass**: report, Warning at startup, stamped on every decision | enforce | honoured, recorded |
| Full, Server, Edge, System | report | report | opt-in enforce | honoured when enforcing |

**Until 4.11 every profile defaults to report.** From 4.6, `enforce` is honoured as an opt-in on every profile, so
tests for 4.7–4.10 can enforce through the real resolver.

**Unreadable values fail closed.**
- An unrecognised `ASHLAR_EGRESS_MODE` means **enforce**, with a Warning (S5 D1).
- An unrecognised `ASHLAR_DEPLOYMENT_PROFILE` read by `ProcessDefault` also means **enforce**. `AddAshlar` already
  refuses an unknown profile at startup (`src/Ashlar.Hosting/AshlarServiceCollectionExtensions.Deployment.cs:26-33`).

**One resolver, two bindings.**

- **The resolver.** A new internal static `EgressEnforcement.ResolveMode(profile, override)` in
  `Ashlar.Abstractions.Security.Egress`.
  - It is a pure function, tested as a full table: 6 profiles × {unset, `report`, `enforce`, junk}.
  - It returns `(Mode, ModeBasis)`. `ModeBasis` is one of `profile:<p>`, `override`, `break-glass`,
    `override-ignored`, `host-opt-out`, `operator-verb` and `fault`.

- **The process binding**, for the 16 explicit sites. These call `ProcessDefault` directly, so they need it.
  - `EgressGuard.ProcessDefault` resolves its profile from `AshlarDeploymentProfileEnvironment.Effective(raw)`, the
    value the protocol validators read (`src/Ashlar.Abstractions/AshlarDeploymentProfileEnvironment.cs:22-23`, `:56-60`).
  - **The strictest profile seen in the process wins.** Today `NoteResolved` is last-wins (`:10-18`), so a second
    `AddAshlar()` with no profile (`AshlarServiceCollectionExtensions.cs:111-119`) silently turns an AG process into
    Full. It becomes: once AG has been noted, nothing lowers it; once SW has been noted, only AG replaces it.
  - Tests restore the value through an internal reset seam (§2.10). `ClearResolved` has no callers today.
  - **The mode override is read once.**
    - `AddAshlar` reads `ASHLAR_EGRESS_MODE` once, together with an optional `AshlarHostingOptions.EgressMode`, and
      notes the result process-wide, as it does the profile. A process that never runs `AddAshlar` reads the
      variable at its first decision.
    - A later `SetEnvironmentVariable` changes nothing.
    - `AshlarHostingOptions.EgressMode` can only **raise** the mode (`enforce`), so the SW break-glass exists only
      through the environment variable.
    - Ashlar never binds `EgressMode` from `IConfiguration`, so appsettings files and command-line arguments cannot
      lower the mode.

- **The composition binding**, for the factory and MEAI routes.
  - `AddAshlar` registers `new EgressGuard(resolvedProfile, latchedOverride)` as `IEgressGuard`.
  - **The registration rule.** Several public extensions call `AddAshlarEgressGuard()` and can run before `AddAshlar`
    in the same collection: RunPod, NodeCapabilityRuntime, ModelArtifactCatalog, MeshLab and `Phases.cs:780`.
    `AddAshlarEgressGuard()` registers `ProcessDefault` with `TryAdd` (`src/Ashlar.Infrastructure/Egress/EgressServiceCollectionExtensions.cs:54-59`).
    So `AddAshlar` **replaces the `IEgressGuard` descriptor only when its instance is `EgressGuard.ProcessDefault`**,
    and keeps any other registration, which is the host's own. A twin calls `AddAshlarEgressGuard()` before
    `AddAshlar(AirGapped)` and asserts that the bound guard is resolved.
  - The factory and MEAI routes resolve `IEgressGuard` from DI (`EgressServiceCollectionExtensions.cs:97-104`;
    `src/Ashlar.AI.Pipeline/Governance/AshlarGovernanceChatClientBuilderExtensions.cs:56-66`).

- **An explicit-profile guard never reads the environment.** `new EgressGuard(profile, override)` takes its override
  only from its constructor. That keeps the leak test's Scenario A free of the environment.

**Who decides the mode at a send.**
- The route acts on `decision.Mode`.
- **On AG the route enforces when either the decision or the process profile says enforce** (security critique). A
  host `IEgressGuard` is wrapped and never trusted to lower the mode on AG.
- On other profiles, a host's own `IEgressGuard` is the host's trusted base, and its `Mode` is honoured. A host can
  always bypass the guard with a raw `new HttpClient()`.
- When there is no usable decision (a host `IEgressGuard` threw or returned `null`), the route falls back to the
  process mode (§2.4).

**Internals the routes need.** `EgressGuardChatClient` lives in `Ashlar.AI.Pipeline`. The fallback mode and the
synthetic `NoDecision` decision need internals: `EgressDecision`'s constructor is internal (`EgressDecision.cs:17`), and
so is `AshlarDeploymentProfileEnvironment` (`:6`). Abstractions grants `InternalsVisibleTo` to five assemblies only
(`src/Ashlar.Abstractions/Ashlar.Abstractions.csproj:32-38`). Add `Ashlar.AI.Pipeline`, and `Ashlar.Infrastructure`
(which R-c already needs, §2.6). AI.Pipeline has no `PublicAPI.*.txt`, so its new constructor parameter is not tracked
there.

**Mode faults fail closed.** Inside `Evaluate`, the mode is resolved first, in its own try, with fallback
`enforce`/`fault`. The classification try stays as it is. Today the placeholder is `enforces = false`, computed inside
the try that can fault (`EgressGuard.cs:66`, `:80`). Keying enforcement off that would fail **open** (S3 §2.4).

**Records.**
- `Mode` becomes `report` or `enforce`.
- `ModeBasis` and `Refused` are appended to the decision and to EventSource event 1, whose contract is append-only
  [S3 `EgressEventSource.cs:14`].
- `Refused` is what a route must do: `Mode == enforce && !Access.Allowed`.

**Startup visibility.**
- `AddAshlar` has no logger (`AshlarServiceCollectionExtensions.cs:103-135`), so it does not log the line itself. A
  hosted activator logs one line at Information: mode, basis, profile, and whether the profile was defaulted because
  nothing set it. This follows the `EgressDecisionLoggerActivator` pattern (`EgressServiceCollectionExtensions.cs:61`).
- Host-less CLI verbs write the same line to stderr when the mode is not plain `report`.
- A break-glass, an ignored override or a junk value logs at Warning.

### 2.2 Subject frames

#### Producer boundary (Q2)

- **The runner declares the frame** (S5 D2(b); S2's observation rules, D3a and D5). The code that builds an agent's
  inputs enters `EgressSubject.Enter("agent:<id>", new HighWaterMark(floor))`, with a floor it can vouch for.
- **Why the runner, not the agent.** The runner builds the snapshot
  (`src/Ashlar.BackgroundAgents.HostRunners/SelfExtendRunnerAdapter.cs:233-240`). `ToolCallingAgent` then serializes
  all of `snapshot.Data` into the system prompt (`src/Ashlar.BackgroundAgents/Agents/ToolCallingAgent.cs:159`,
  `:334-343`), so only the runner knows what went in.
- **`SelfExtendRunnerAdapter` is the only production caller of `RunCycleAsync`** (`:247-249`).
  - It enters the frame with floor **SystemHigh**, because its snapshot carries unlabelled carry-over: scratchpad,
    other agents' observations, repo listings [S5; S2 B2].
  - The frame wraps the cycle and the post-cycle admission and auto-share (`:290-294`). At SystemHigh this only
    attributes them to the agent.
  - **No production outcome changes.** Records go from `no-subject` to `subject:agent:<id>`, at the same SystemHigh.
- **`ToolCallingAgent` observes inside whatever frame is live.** It sees every tool result before the next model call:
  model call `:192`, tool call `:236`, result `:247`.
- **Not instrumented in PR 4:** the registry (B3), `AgentHost` (B4), the orchestrator (B5), workflows (B6), bricks
  (B7), the autonomy loop (B8), API requests (B10), CLI verbs (B11) and system services (B12) [S2 §2.4]. They stay
  `no-subject`, which is SystemHigh.

**The producer rule (security critique).** A frame below SystemHigh may wrap only code whose reads are observed: agent
tool calls under a read scope. Post-processing such as admission or auto-share runs outside such a frame, or inside a
read scope. A SystemHigh frame may wrap anything.

#### Floor

- **Exactly one runner in PR 4 declares a floor below SystemHigh: the leak test**, acting as a runner whose snapshot
  holds no carry-over.
- `new HighWaterMark()` defaults to Public (`src/Ashlar.Abstractions/Security/HighWaterMark.cs:19-23`), so producers
  pass the floor explicitly.
- A convention test pins every production `EgressSubject.Enter` call site, with its floor and its enclosing method.

#### Observe, report, and read scopes

- **`EgressSubject.Observe(SecurityLabel)`** (public static, S2 D5a). It joins the label into **every live frame on
  the chain**. With no frame it does nothing. **It only raises; it never satisfies a read scope** (security critique).
- **`EgressSubject.BeginRead()`** returns a read scope. `ToolCallingAgent` wraps each tool invocation (`:233-242`) in
  one. How the scope ends:
  - **No report** (the default): the scope observes **SystemHigh** on dispose.
  - **Reported:** the scope observes only what was reported. This happens only through `ReadScope.Report(label)` from
    a tool that **declares itself labelled**: it reports a label for everything its result carries. In PR 4 that is
    `RAGTool` alone. The declaration is a marker that `ToolCallingAgent` checks before it accepts a report.
  - **The tool threw** (a refusal included): the scope observes **SystemHigh**. A tool can read and then throw, and its
    message reaches memory (`react.error`, `:271-276`) and MCP clients
    (`src/Ashlar.Mcp.Server/ToolboxMcpToolContributor.cs:104-110`).
    - After a refusal this is harmless: the mark was already above the refused destination, and Host models stay
      reachable.
- **`RAGTool` reports explicitly in every outcome:**
  - with hits, the label of each hit (rules below);
  - with 0 hits, or with its unrankable-query refusal (`src/Ashlar.BackgroundAgents/RAG/RAGTool.cs:96-99`), it
    reports **"read nothing"**, which marks the scope reported without raising. Its payload is then only the
    model's own query and the store's message. Otherwise every empty search would make the agent SystemHigh.

#### Monotone nesting (S2 D4b; S5 D3b)

- **`Resolve` returns the join of every live frame's mark on the chain.** A nested frame can never decide below an
  enclosing one.
- **Disposing a frame observes its mark into the enclosing live frames.**
- Together with `Observe` into every live frame, this also covers an outer flow that egresses while the inner one is
  still running.
- **Flip by name:** `EgressGuardDecisionTests.An_Internal_subject_may_reach_an_external_model_across_await_and_Dispose_restores_the_previous_frame`,
  whose inner block (`src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardDecisionTests.cs:355-362`) pins
  "inner Public decides Public".
- **`Enter` stays public** (default D37). Sealed skills cannot reach it: the IL import fence is an allow-list
  [S5 `IlImportFence.cs:34-38`].

#### What counts as a read

| Read | Label in PR 4 |
|---|---|
| `rag_search` hit | its tier, if it is one of the five canonical names: trimmed, any case, plus `top-secret`. Anything else, custom registry levels included, is **SystemHigh**. |
| Every other tool result: `repo.fs.*`, dotnet build and test, forge, MCP proxies, web search, objective tools | SystemHigh, by the unreported-read rule |
| A tool that threw | SystemHigh |
| Snapshot and system prompt | the runner's declared floor |
| Persisted state coming back: scratchpad, observations, memory | SystemHigh (S2 D8) |
| **Model responses** from model endpoints | **not a read** (S2 D6a) |
| **Responses from `peer:` and other agent-backed chat targets** | **SystemHigh** until PR 5 carries a label on responses. A peer's own data is not derived from our prompt. `peer:` is allowed by `DefaultChatTargetAccessPolicy.cs:25-26`. `EgressGuardChatClient` observes it after the response. |
| Refusal and DENIED text fed back to the model | not a read: our own text |

**Canonical names only for RAG** (S5 over S2). A custom level sets its four flags freely
(`src/Ashlar.BackgroundAgents/DataSensitivity/CustomSensitivityLevel.cs:18-38`). The bridge carries the level only
(`DataSensitivityLabelBridge.cs:33-38`), and maps a value below 0 to **Public** (`:96`).

**Wiring.** `RAGTool` is in BackgroundAgents, which does not reference AI.Pipeline. The mapping is:
1. Trim the name.
2. `registry.GetByName`.
3. Require the result to be one of `DataSensitivityLevels.All`.
4. Apply `ToDataLabel`.
5. Anything else is SystemHigh.

The trim matters. `TrustTierOrder` trims (`src/Ashlar.AI.Pipeline/Rag/TrustTierOrder.cs:74`), but
`DataSensitivityLevels.FromName` does not (`src/Ashlar.BackgroundAgents/DataSensitivity/DataSensitivityLevels.cs:92`;
the registry calls it first, at `DataSensitivityRegistry.cs:55`). A parity test pins RAGTool's mapping against
`TrustTierOrder.RecordLabel` (`TrustTierOrder.cs:110-111`) over case, white space and `top-secret`.

#### Session

- A session is one run of the runner (S2 D8a).
- Nothing carries over between cycles in memory, and what comes back from disk is SystemHigh.
- A mark persisted with agent state needs labelled storage, which is PR 7.

#### Threading facts the design relies on (corrected)

- Frames flow into awaits and `Task.Run`, and never back out to the caller (`EgressSubject.cs:11-12`, `:23`, `:42`).
- Hosted services start outside any frame. Work handed through a `Channel` runs on the consumer's flow.
- **Pub/sub dispatch inherits the publisher's frame.** `AgentBus.PublishAsync` runs every subscriber's handler through
  `Task.Run` in the publisher's ExecutionContext (`src/Ashlar.Orchestration/Communication/AgentBus.cs:63-76`). So
  another component's egress would be decided at the publisher's mark.
  - **Fix:** an internal, raise-only `EgressSubject.Detach()`. It resets the flow to no-subject (SystemHigh) and
    returns a scope.
  - It is used at the AgentBus subscriber dispatch. A convention test lists the dispatch points.
  - It never lowers: no-subject is the top.
- No frame crosses a process or network boundary (see Q3 for child processes).
- **Corollary:** system and infrastructure egress stays `no-subject`. Nobody may "fix" this with a host-wide frame
  (S2 Risk 11).

### 2.3 The refusal surface

#### Exception type (S3 RD1a; S5 D7)

- `public sealed class EgressRefusedException : Exception` in `Ashlar.Abstractions.Security.Egress`.
- Properties: `Decision`, `Reason`, `Site`, `Sequence`, `Ref` (the random remote reference, §2.5) and
  `ErrorCode = "EGRESS_REFUSED"`.
- **Never** derive it from `HttpRequestException`, `IOException`, `SocketException`, `TimeoutException`,
  `OperationCanceledException` or `InvalidOperationException`.
  - The first five are what `TransientClassifiers.Network` retries
    (`src/Ashlar.Core.Application/Resilience/Ports/TransientClassifiers.cs:26-36`).
  - About 36 generic `InvalidOperationException` handlers exist [S3 §2.2].
- CA1032 is suppressed with a written reason, following the repo's `#pragma` practice (`EgressGuard.cs:93`). This needs
  a build on all three TFMs **[needs a test]**.

#### Decision helpers and the convention

- `decision.Refuses` is `Mode == "enforce" && !Access.Allowed`. A fault gives `Access = default`, which is
  `NoDecision`, so a fault is refused [S3 `AccessDecision.cs:18`, `:42`].
- `decision.ThrowIfRefused()` returns the decision. Explicit sites keep the
  `EgressGuard.ProcessDefault.Evaluate(new EgressRequest(...)).ThrowIfRefused();` shape that the G3 scanner matches
  [S3 `EgressGuardConventionTests.Scanner.cs:668`].
- **New blocking convention, stated positively.** Every production `.Evaluate(new EgressRequest(` is either followed
  by `.ThrowIfRefused()`, or assigned to a local whose `.Refuses` is read in the same method. Banning only the `_ =`
  form misses a bare expression statement. It gets its row in `ci/cert-gate-assertions.md`, and is mutation-checked by
  dropping `.ThrowIfRefused()` at one site.

#### Per route

| Route | Under enforce |
|---|---|
| HTTP: `EgressGuardHandler` (factory clients and `EgressHttp`) | `SendAsync` returns a faulted task, `Send` throws. Never a synthetic 4xx: that would lose the reason, be retried, and read as the remote's answer (S3 RD2a). The handler is at index 0 (`EgressServiceCollectionExtensions.cs:63-69`), so resilience handlers inside it never see the send. HttpClient passes the exception through unwrapped **[needs a test]**. |
| MEAI: `EgressGuardChatClient` | Throws synchronously from `GetResponseAsync`, and before enumeration from streaming, which is PolicyGate's shape. Writes its own `denied`/`egress_refused` audit record [S3 `PolicyGateChatClient.cs:62-71`], because the keyed `AuditingChatClient` is innermost (`AshlarGovernanceChatClientBuilderExtensions.cs:45-49`). The constructor gains an optional auditor. |
| Explicit throwing sites | `.ThrowIfRefused()`: EG-MESH-01 publish and share, MESH-02, MESH-07, MESH-08, FILE-01/02, PROC-01/02/03, WEB-01, MDL-11 |
| Explicit **degrade** sites | check `.Refuses` and do not throw (below) |

**The four degrade sites** (S3 RD6a):
1. **EG-MESH-03 mesh serve** (`MeshServeService.cs:313`). Dispose the stream opened at `:308`, then return
   `Results.NotFound()`.
2. **EG-MESH-05 discovery** (`MeshDiscoveryService.cs:249`). Go listen-only, with a Warning. A throw would fault the
   `Task.WhenAll` at `:213` **[needs a test]**: `BackgroundServiceExceptionBehavior`.
3. **EG-TEL-01 OTLP** (`application/src/Ashlar.API/Program.cs:247`). Skip both `AddOtlpExporter` registrations
   (`:252`, `:259`), and write one stderr line plus a startup Warning.
4. **EG-MESH-01 auto-share** (`SelfExtendAdmissionBridge.cs:288`). Return `"; auto-share refused: …"` in the existing
   shape (`:281-285`).

#### No-retry and no-fallback rules (S3 RD4b)

- **No retry to the same destination.** This holds by type: no classifier matches the new exception.
- **Fallback to another destination is allowed.** The guard decides each attempt again.
- **If every alternative failed or was skipped, and any of them was refused, the refusal is the final error.**
  "Skipped" includes an availability probe that was itself refused (row 24 below).
- **Never fallen back from:** the echo model, and the circuit breaker.

#### Catch-alls that must stop masking a refusal (rows 24–29 are new)

| # | Site | Masks it as | Change |
|---|---|---|---|
| 1 | `src/Ashlar.Infrastructure/Execution/Models/HotSwappableModel.cs:67-71` | echo-model success under `prefer=deterministic` | rethrow `EgressRefusedException` first |
| 2 | `HotSwappableModel.cs:84-95` | `ModelUnavailableException`, or echo under `ASHLAR_ALLOW_MOCK=1` | rethrow first |
| 3 | `src/Ashlar.Infrastructure/Execution/AdaptiveProviderFactory.cs:60-69` (LLM) and `:95-102` (vision) | "No model available" / "No vision model available." | keep trying; throw the first refusal as the final error |
| 4 | `ToolCallingAgent.cs:122-127` (`ThinkAsync`) | "no actions" | record `egress.refused`, rethrow |
| 5 | `ToolCallingAgent.cs:271-276` (cycle) | `stopped=error` with raw `ex.Message` | `StoppedReason = "egress_refused"`; memory `egress.refused` with the redacted text |
| 6 | `ToolCallingAgent.cs:233-242` (tool invoke) | ends the cycle as `error` | a `"tool {id}: REFUSED by egress policy (…)"` observation in the DENIED shape (`:228`); count it; continue; **after 3 refusals in one cycle, stop with `egress_refused`** and a Warning |
| 7 | `src/Ashlar.Orchestration/Coordination/Orchestrator.cs:557-568` | circuit-breaker failure | exempt `EGRESS_REFUSED`, as `AGENT_NOT_FOUND` is (`:558-561`) |
| 8 | `Orchestrator.cs:628-631` | relabelled `CIRCUIT_OPEN` | covered by #7; escalate with its own code |
| 9 | `src/Ashlar.Orchestration/Transport/InProcessAgentTransport.cs:93-111` | `TRANSPORT_ERROR` | `ErrorCode = "EGRESS_REFUSED"`, redacted text |
| 10 | `src/Ashlar.Transport.Grpc/GrpcAgentTransport.cs:79-104` | `GRPC_ERROR_INTERNAL` | map a refusal anywhere in the chain to `EGRESS_REFUSED` **[needs a test]** |
| 11 | `src/Ashlar.Transport.A2A/A2AAgentTransport.cs:108-123` | retried, then `a2a.transport.failed` | `catch (EgressRefusedException)` ahead of `:108` → `a2a.egress_refused` **[needs a test]** |
| 12 | the **router's** `AuditingChatClient`, which wraps `RoutingChatClient` outside the keyed governed clients (`src/Ashlar.AI.Pipeline/MeaiPipelineServiceCollectionExtensions.cs:126-128`; dispatch without a catch at `src/Ashlar.AI.Pipeline/Routing/RoutingChatClient.cs:45-48`); catch at `AuditingChatClient.cs:56-60` | audit `"fault"` | classify as `"denied"`, code `egress_refused`. Twin through the unkeyed router client. |
| 13 | `src/Ashlar.Infrastructure/Execution/HttpRemoteBrickCatalog.cs:62-66` (and `:89-92`, `:149-151` [S3]) | empty catalog | log "refused" (windowed Warning, §2.5), return empty |
| 14 | `src/Ashlar.Infrastructure/Execution/CompositeBrickRegistry.cs:58-61` | "brick not found" | as #13 |
| 15 | `src/Ashlar.Infrastructure/ModelArtifacts/ModelArtifactCatalogService.cs:62-65` | a missing source | as #13 |
| 16 | `src/Ashlar.Infrastructure/Testing/ExecutionPlatform/RemoteExecutionPlatform.cs:40-43` | "not reachable" | "refused" |
| 17 | `application/src/Ashlar.API/Endpoints/IdeEndpoints.cs:208-211` | bare `catch { }`, no log | catch the refusal separately and log it |
| 18 | `MeshAutoPullService.cs:290-293` (and `:340-343` [S3]) | `Errors = 1`, reason dropped | a `Refused` count, reason logged |
| 19 | `src/Ashlar.Mcp.Client/McpClientConnectionManager.cs:177-183`; connect `:84-98`, drift `:186-207` [S3] | "Remote call failed" | log connect and drift as "refused" |
| 20 | `src/Ashlar.Infrastructure/Execution/Routing/AshlarPeerBrickExecutor.cs:185-196`, loop `:66-88` [S3] | `peer.dispatch_exception` | `peer.egress_refused`; the final result if every peer was refused |
| 21 | `src/Ashlar.Infrastructure/Execution/Routing/RunPodHttpClient.cs:67-70` (and `:113`, `:145`, `:169`, `:192` [S3]) | `runpod.*_exception` | `runpod.egress_refused` |
| 22 | `src/Ashlar.Runtime/Routing/EndpointHealthMonitor.cs:79-120` [S3] | "degraded" | "refused" |
| 23 | `application/src/Ashlar.CLI/Commands/ImproveCommand.cs:401-418` [S3] | audit still says "Promoted" | do not record "Promoted" after a refused broadcast |
| 24 | `src/Ashlar.Infrastructure/Execution/ProviderFactory.cs:783-797`: `IsProviderAvailable("ollama")` (`:222`) runs an EG-MDL-07 health check (`:814`) and catches everything | "unavailable"; `AdaptiveProviderFactory` skips it (`:54-55`) and ends with "No model available" / "All providers failed" (`:67-69`) | record the refusal on the factory; `AdaptiveProviderFactory` throws it as the final error when nothing else succeeded |
| 25 | `AdaptiveProviderFactory.cs:118-125` (multi-frame vision) | `ModelUnavailableException("Vision model failed.")` | rethrow the refusal unwrapped |
| 26 | `src/Ashlar.Infrastructure/Validation/Adapters/ValidationServiceAdapter.cs:237-250`, outer `:282-294`; refused site `:528` (EG-PROC-02) | a **failing test** (`totalTestsFailed++`), then "Validation error" | a distinct "refused" result: `Passed = false`, a refusal reason, no test counted as failed |
| 27 | `src/Ashlar.Core.Application/Workflows/WorkflowExecutor.cs:296-303` and `:376-385` (brick fallback chains) | the last exception, or a generic "DomainBrick execution failed" | the refusal is the final error when every implementation was refused |
| 28 | `src/Ashlar.Infrastructure/MeshLab/MeshLabWorkerExecutorBackgroundService.cs:50-53` | "unexpected error" on every poll | catch the refusal: "refused", windowed Warning |
| 29 | `src/Ashlar.Infrastructure/Scaling/ElasticWorkloadAutoscaleService.cs:55-58` (kubectl, EG-PROC-03) and `src/Ashlar.BackgroundAgents/Autonomy/AutonomyLoopService.cs:256` | "tick failed" / a generic objective failure | catch the refusal: "refused" with its reason, windowed Warning; the autonomy loop counts it under its own name |

**Safe by type.** These swallow only `HttpRequestException`, so the new exception passes through them:
- `BingWebSearchProvider.cs:64-67`; the guard sits outside the try, at `:56`.
- the MeshLab **client** `:190-193`, SNS `:52-59`, and `OllamaProvider` `:120-126` and `:263-281` [S3].

The MeshLab **background service** around that client is row 28.

**CLI verbs.** Each catches `EgressRefusedException`, writes the redacted text plus `ref` to stderr, and exits **77**
(S3 RD8b). The verbs:
- `pkg export --out` (`PkgCommand.cs:54`, guard `:93`);
- `pkg publish` (`:389`) and `pkg share` (`:444`);
- `export` (`ExportCommand.cs:74-82`, `:156-164` [S3]);
- `mesh export` (`MeshCommand.cs:199-202`; the guard is inside `SneakernetTransport.cs:36`).

There is no verb `pkg pack`.

**Trust-boundary exits send a fixed text and a random `ref` only.** The exits:
- the A2A server (`src/Ashlar.Transport.A2A.Server/AshlarA2AAgentHandler.cs:86-88`);
- the gRPC server (`src/Ashlar.Transport.Grpc.Server/AgentTransportServiceImpl.cs:176-190`);
- the MCP server (`ToolboxMcpToolContributor.cs:104-110`);
- the IDE SSE stream (`IdeEndpoints.cs:465-473`).

They recognise a refusal **anywhere in the exception chain**: `InnerException`, `AggregateException.InnerExceptions`,
`RpcException.Status.DebugException`, or `ErrorCode == "EGRESS_REFUSED"`.

### 2.4 Fault behaviour (§7 fail closed)

- **The guard faults** (`EgressGuard.cs:93-101`).
  - The decision is `NoDecision` with `Fault = <type>`, and the mode was resolved separately (§2.1), so under enforce
    it is refused.
  - Message: "the egress guard could not decide (System.X); the egress fails closed".
- **A host `IEgressGuard` throws, or returns `null`.** Today both are swallowed and the send goes ahead
  (`EgressGuardHandler.cs:80-85`; `EgressGuardChatClient.cs:63-70`).
  - Under the process mode **enforce**: `EgressRefusedException` with a synthetic `NoDecision` decision, built inside
    Abstractions (the AI.Pipeline route reaches it through `InternalsVisibleTo`, §2.1), with `Fault = <type>` and the
    counter incremented.
  - Under **report**: today's behaviour.
- **The guard cannot be resolved from DI.** The route falls back to `ProcessDefault`.
  - When resolving **throws**: counted, and logged at Warning, because it bypasses a host's stricter guard.
  - When it **returns `null`**: the service is simply not registered, which is every AI.Pipeline composition without
    `AddAshlarEgressGuard`. Counted, and logged at Debug only.
- **A sink fault, or a re-entrant publish skip** [S3 `EgressDecisionLog.cs:50-93`]. The decision is still enforced, and
  a skipped publish of a refusal is counted.
- **A hosted service.** A refusal that escapes `ExecuteAsync` can stop the host **[needs a test]**:
  `BackgroundServiceExceptionBehavior`. Every hosted loop that sends catches the refusal explicitly:
  - auto-pull (row 18), the health monitor (22), MCP connect and drift (19), the MeshLab worker service (28), the
    autoscaler and the autonomy loop (29);
  - mesh discovery (degrade site 2).

### 2.5 Redaction and logging

**Three audiences** (Q7; recommendation D-b):

| Audience | Gets |
|---|---|
| Operator: the `Ashlar-Egress` EventSource, the ILogger sink, the MEAI audit | The whole record: destination (scheme, host, port only), both labels, bases, `Access.Detail`, profile, `ModeBasis`, fault, `seq` **and `ref`**. Never a payload, path or query (`EgressDecision.cs:8-12`). |
| In-process code and the local subject: `Exception.Message`, agent memory, the observation the model sees | Per Q7. Under D-b: reason category, site, family, destination class and `ref` (plus `no-subject` when that is the basis). No level names, no compartment or caveat tokens, no `Detail`. The full decision rides on the exception as a property. |
| Remote parties: A2A peer, gRPC caller, MCP client, API client, mesh puller | A fixed text, `"egress refused by policy (ref <ref>)"`, or a bare 404 where a status is the protocol (mesh serve). |

**`ref` and `seq`.**
- `ref` is a random 64-bit value per decision. `seq` is a process-wide counter (`EgressGuard.cs:36`, `:60`), so
  showing it to remote parties would leak the rate of other subjects' egress decisions.
- `seq` stays in operator records; operators join on `ref`.

**Where the redaction lives.** `Message` cannot be kept inside the boundary: at least eight generic `catch (Exception)`
sites carry `ex.Message` to peers, IDEs and the model (S3 §1.6). So the redaction lives in `Message` itself.

**Logging** (S3 RD7a, with a window instead of dropping to Debug):
- Refusals log at **Warning**, as event 7301 `EgressRefused`. Allowed decisions stay at Debug, event 7300
  (`src/Ashlar.Infrastructure/Egress/LoggerEgressDecisionSink.cs:26`, `:62-68`).
- **Rate.** The first refusal per (site, reason) in a window is logged at Warning. At the end of each 5-minute window
  with suppressed refusals, one Warning summary: "N egress refusals at <site>/<reason> suppressed since <t>".
  Nothing drops to Debug without a count.
- A catch-all that swallows a refusal (rows 13–15, 18, 22, 24, 28, 29) logs its own windowed Warning.
- **EventSource.** Append `Refused` and `ModeBasis` to event 1. Add **event 2 `Refused`** at Warning.
- **MEAI.** The `denied`/`egress_refused` audit record (§2.3).
- **No exported refusal metric in PR 4.** Exporting one would itself be EG-TEL-01.
- **Durable `IDataDecisionAuditLog` adapter:** deferred out of PR 4 (default D29). The §5 done-when does not need it,
  and it belongs with PR 6's receipts.

### 2.6 The pre-enforcement gaps (S4)

#### Gap 1: synchronous `Send` on the netstandard2.0 asset (S4 option C; default D31)

- **The hole.** The `Send` override exists only under `#if NET5_0_OR_GREATER` (`EgressGuardHandler.cs:57-64`). On
  .NET 5–7, the netstandard2.0 asset forwards a synchronous `Send` unevaluated.
- **Who is exposed.** Within Ashlar's own composition the gap cannot occur [S4 `scout-S4-gaps.md:170-178`]: every
  project that uses `EgressHttp` or the guard targets `net8.0;net10.0`. `AddAshlarEgressGuard` is in Infrastructure
  (net8.0+), so a .NET 5–7 app cannot even reference it.
  - Only external .NET 5–7 code that calls `EgressHttp` directly, and then sends synchronously, is exposed.
  - .NET 5, 6 and 7 are out of support.
- **The fix.**
  - Under `#if NETSTANDARD2_0`, `EgressHttp.CreateClient` and `Wrap` put an internal hop between the guard and the
    inner handler. It derives from `HttpMessageHandler`, not `DelegatingHandler`, and forwards `SendAsync` through an
    owned `HttpMessageInvoker`.
  - A synchronous `Send` then reaches the base `HttpMessageHandler.Send`, which throws `NotSupportedException` before
    anything is sent **[needs a test]**. The hop is the `EgressRedirectHandler` itself (gap 2).
  - `CreateDelegatingHandler` throws `PlatformNotSupportedException` on such a runtime.
  - **The hop publishes a `NoDecision` record** with `Fault = "SynchronousSendUnsupported"`, so the refusal is in the
    operator log. A bare runtime exception would not be (S4 `:187`).
- **Every mode, not only enforce.** An unevaluated send makes report-mode records incomplete. The exposed population
  is out-of-support external code.
- **Statement.** `docs/SdkCompatibilityPolicy.md`: full guard coverage needs net8.0 or later; the netstandard2.0 asset
  is for .NET Framework, Mono and Unity. There is no `buildTransitive` error.
- **Records.** `HttpMessageInvoker` counts as `http.new`, which needs one `Exempt:GuardImpl` TSV row [S4 F2.18].
- **The twin, inside the cert-gate project.** Cert-gate builds and tests only `Ashlar.Tests.Infrastructure`, and
  counts only that project (`scripts/run-cert-gate.sh:20-35`; `scripts/cert-gate-config.sh:16-29`, single
  `<Counters>` at `:44-60`).
  - The twin loads the **netstandard2.0 build of Abstractions into an isolated `AssemblyLoadContext`** inside that
    project, and drives it by reflection **[needs a test]**. A `ProjectReference` with `ReferenceOutputAssembly=false`
    and `SetTargetFramework=netstandard2.0` copies that build to the output.
  - Fallback, if ALC loading does not work: a `tests/scripts/*.test.sh` driver under shell-lint, plus a
    `cert-gate-assertions.md` row. That avoids editing `run-cert-gate.sh`, `cert-gate-config.sh`, `cert-gate.yml` and
    both `READINESS_PATHS` copies.
  - Today: `Send` returns the stub's response, with 0 decisions. After: `NotSupportedException`, the stub sees 0
    requests, and `SendAsync` still records exactly 1 decision. Mutation: drop the hop.

#### Gap 2: redirects (S4 R-a, R-b, R-c; plus the security critique)

- **R-a. Every primary Ashlar builds or binds stops following redirects.**
  - `EgressHttp.CreateClient` uses `AllowAutoRedirect = false`.
  - `CreateClient(inner)` and `Wrap(inner)` walk to the end of the chain and flip `HttpClientHandler` or
    `SocketsHttpHandler`, remembering the original values. The flip is inside a try: the setter throws on a started
    handler **[needs a test]**.
  - `OllamaHttpChatClient` gets `AllowAutoRedirect = false` and **no follower** [S4 `OllamaHttpChatClient.cs:163-170`].
    This is a **behaviour change**: an Ollama behind a redirecting proxy breaks. It goes in the CHANGELOG.
  - Bedrock, if AWSSDK.Core 4.0.100.4 exposes the setting **[needs a test]**.
  - **Both SNS signing clients never follow:** `application/src/Ashlar.API/Program.cs:171` and
    `commercial/src/Ashlar.Commercial.Fleet.Host/Program.cs:79`. Use the configure-existing overload
    `ConfigurePrimaryHttpMessageHandler((h, _) => …)`, never the replace overload [S4 F2.6].
- **R-b. An internal `EgressRedirectHandler`** in `src/Ashlar.Abstractions/Security/Egress/`.
  - **Placement: innermost**, directly above the primary. An outer follower re-runs every client handler on each hop
    and re-adds credentials (S4).
  - **Every send whose authority differs from the last one evaluated is re-evaluated** (security critique). The guard
    handler stashes the evaluated authority in `request.Options`. Before each send, the redirect handler compares and,
    on a difference, decides with the same guard, family and site.
    - That covers redirect hops 2..n.
    - It also covers a `DelegatingHandler` between the guard and the primary that rewrites `RequestUri`: service
      discovery, hedging, base-address rewriters.
  - **Runtime parity** [S4 E2]. Follow only when the primary originally followed, with its own limit:
    - resolve a relative `Location`; keep the fragment;
    - refuse HTTPS → HTTP, and any scheme other than `http`/`https`;
    - rewrite the method on 301/302 POST and on 303;
    - clear `Authorization`;
    - dispose intermediate responses;
    - mutate the same `HttpRequestMessage`.
  - Overrides both `SendAsync` and `Send` on net; on netstandard2.0 it is the gap 1 hop.
  - **Cross-host policy** (default D33):
    - **P2** for `EgressHttp`-built clients: a cross-host 3xx is returned to the caller. This covers A2A, MCP, gRPC,
      the mesh CLI, MeshDirector, ProviderFactory and the cloud probe. It also stops their API-key headers following
      a cross-host hop [S4 F2.12].
    - **P1** for factory clients: follow and evaluate each hop.
- **R-c. The factory binding** (**needs Q5**; it changes host clients).
  - `AddAshlarEgressGuard` registers an `IHttpMessageHandlerBuilderFilter`. Its post-`next(builder)` step sees the
    final primary [S4 F2.4].
  - For a known primary type, it flips the type and wraps it in `EgressRedirectHandler`.
  - **It checks that the guard handler is still `AdditionalHandlers[0]`.** A client's own
    `ConfigureAdditionalHttpMessageHandlers((h, _) => h.Clear())` runs after the defaults action. If the guard
    handler is missing, the filter re-inserts it and logs a Warning.
  - **Unknown primary types** (every test stub, in-memory handlers) are not flipped:
    - after the send, the guard handler compares `response.RequestMessage.RequestUri` with the evaluated authority.
      On a mismatch it evaluates; on a refusal it disposes and throws, and logs a Warning that the body may already
      have gone;
    - on AG and SW, each unknown primary type is named in a startup Warning.
  - Original values are kept per instance in a `ConditionalWeakTable`. The filter needs `InternalsVisibleTo
    Ashlar.Infrastructure`.
  - If Q5 picks "Ashlar's clients only", the filter applies only to Ashlar's registered client names (the registry
    from §2.8).
- **Twins:**
  - a `RedirectingStub : HttpClientHandler` (307 to `remote.example`): today 1 decision, after 2;
  - a factory twin;
  - a **differential twin against the real `SocketsHttpHandler`** over loopback Kestrel (301/302/303/307/308,
    HTTPS → HTTP, relative and missing `Location`, the limit);
  - an enforcement twin: hop 2 refused, the server never sees `/b`;
  - **a URI-rewriting inner-handler twin** (a rewrite to a remote host is evaluated);
  - **a `Clear()` twin** (the guard handler is re-inserted).
  - Seven mutations.
- **Host-visible behaviour change in report mode.** `AllowAutoRedirect` reads `false` on every known primary, and
  per-hop semantics now come from Ashlar's follower. This goes in the CHANGELOG and release notes **in 4.3**. The
  "exactly once per `SendAsync`" wording in `EgressGuardHandler.cs:8-9` changes.

#### Gap 3: records that can read Host (S4 §2.3)

| Record | Fix | Twin, and what changes |
|---|---|---|
| **EG-MESH-03** mesh serve | `PeerDestination` returns `mesh-peer:<ip>` (`mesh-peer:unknown` with no address). The class is then the family's, NetworkExport, whatever the IP, which closes local proxies, tunnels and forwarded headers. | A loopback client becomes NetworkExport. Restore `tcp://` → red. **Red-to-green flips:** `application/src/Ashlar.Tests.CLI/Tests/Commands/EgressCliSiteTwinTests.cs:73-113` (`PeerDestination` output and the served decision). The classifier rows `EgressExplicitSiteTwinTests.cs:267-271` feed literal `tcp://` names and never go red; they are rewritten to `mesh-peer:` names, or kept as classifier facts. |
| **EG-MDL-01** custom inner client; `local:onnx` unrecorded | `MeaiEgressDestination.Resolve(targetKey, sp, inner)` (`src/Ashlar.AI.Pipeline/Governance/MeaiEgressDestination.cs:57-92`): (1) an absolute `ChatClientMetadata.ProviderUri` is the destination; (2) `local:onnx` records nothing **only** when the inner client is a `LlamaSharpChatClient`; (3) Bedrock keeps its region reconstruction (`:82-89`); (4) anything else is `meai:<key>`, ExternalModel. Fail closed. | Mutations: drop step 1; make step 2 key-only. Four MEAI twins move off `FakeChatClient` [S4 Risk 4]. |
| **EG-MESH-07/08** `file:` paths | One rule in `EgressDestinations`: a name starting `file:` is never URL-parsed or Host, and a `file` URI is never Host. | New classifier rows; drop the rule → red. |
| **Ollama cloud models** | **Unconditional and fail closed.** A model id whose tag ends in `cloud` (`-cloud`, `:cloud`, any case) is ExternalModel at `https://ollama.com`. It applies at `EgressGuardChatClient` (`options.ModelId`) and at `OllamaProvider`. The local daemon relays these to ollama.com [S4 E8]. Nothing needs verifying first: a wrong guess only blocks models with that tag. | A `llama3:cloud` call to `localhost:11434` records ExternalModel. Drop the rule → red. |

**Loopback relays: a Known limit.** Host is SystemHigh by design. Loopback is inside the boundary only for services
that do not relay. A loopback OTLP collector, the docker daemon, `kubectl proxy` and local LLM gateways can forward
off-host.
- PR 4 does not narrow Host to an allow-list of loopback ports, for three reasons:
  - Ashlar cannot tell which local endpoints relay (the Ollama daemon itself does).
  - On AG, narrowing would refuse a local collector with no replacement.
  - Per-endpoint labels are the destination table's job, which waits on open question A.
- The SPEC-007 threat model and `docs/EgressInventory.md` Known limits say this, naming those four.
- `*.localhost` is trusted by spelling, without resolution. Whether glibc or musl without systemd-resolved sends it
  to DNS is **[needs a test]**.

### 2.7 AirGapped and SecureWorkstation hygiene (defect 5 by default D35; inbound by Q6)

**Defect 5. Keep the DI shape; AG routing stays local.**
- **`ResolveExecutionTarget`** (`src/Ashlar.Infrastructure/Execution/Routing/NcrCapabilityRouter.cs:46-72`). Every
  remote reason (`:132-153`) returns `ExecutionTarget.Local(_localExecutor, "AirGapped: remote execution unavailable;
  running locally (<reason>)")`.
- An explicit `PeerNetworkOnly` (`:48-51`, `:89-105`) is refused with an explained failure.
- **Why this matters:** today RunPod is reachable on AG with no configuration at all.
  - It is the default remote target (`:126-127`; `RunPodBrickConfig.cs:53`).
  - Its base URL defaults to `https://api.runpod.io` (`src/Ashlar.Core.Domain/AshlarDefaults.cs:161`).
  - The request goes out without a key (`RunPodHttpClient.cs:203-205`).

**`AdaptiveProviderFactory` never escalates past local on AG**, on both escalating paths:
- LLM: `providersToTry` (`AdaptiveProviderFactory.cs:47-49`) becomes `{ resolved }` when `resolved` is local.
- **Single-image vision:** `providersToTry` (`:85`) is unconditionally `{ resolved, "ollama", "openai", "azure" }`.
  On AG it drops `openai` and `azure`. An AG twin covers each path.
- The multi-frame path (`:118-125`) already uses `resolved` only; it is catch-all row 25.

**AG boot validators.** They bind to the options types, not to literal strings, on the `ValidateOnStart` pattern
(`src/Ashlar.Mcp.Server/ValidateAshlarMcpServerOptions.cs:29-36`). Each refuses under AG:
- a non-empty `BrickHost:RemoteCatalogBaseUrls` (`BrickHostOptions.SectionName = "BrickHost"`);
- `Ashlar:RunPod:EnablePeerNetworkRouting=true` (`RunPodBrickConfig.cs:24`);
- `Ashlar:MeshLab:WorkerExecutor:Enabled=true` (`src/Ashlar.Infrastructure/MeshLab/MeshLabWorkerExecutorOptions.cs:9`);
- `Ashlar:Meai:Bedrock:Enabled=true` (`src/Ashlar.AI.Pipeline/MeaiPipelineOptions.cs:10`, `:64`). This one protects AG
  from the later defect 4 fix [S1 D7].

**Remaining defaults.**
- The ollama.com catalog `Enabled` defaults to false on AG (`OllamaRemoteLibraryCatalogOptions.cs:13`).
- The guard is the backstop for everything else [S1 matrix].
- The profile reaches Infrastructure through an options value that Hosting registers from the resolved profile.
  Infrastructure never reads `Effective` directly.

**Inbound surfaces (Q6; recommendation shown).**
- **MCP over HTTP fails boot on SW.** The check is a marker service that `.WithHttpTransport()` registers; stdio
  stays. The intent is stated at `AshlarDeploymentProfileEnvironment.cs:50-52`. Today the check covers AG only
  (`ValidateAshlarMcpServerOptions.cs:31`), while the API calls `.WithHttpTransport()` unconditionally (`Program.cs:156`).
- **On AG and SW, every Ashlar inbound listener binds loopback, or boot fails** with an explained message. This
  covers the API's Kestrel URLs and mesh serve, which uses `ListenAnyIP` (`MeshServeService.cs:229`).
- SPEC-007's PR 4 status and Known limits say: "responses on inbound connections are not mediated until PR 5's
  CanRead at the server seams".

### 2.8 Host-app factory clients (Q5; recommendation: all, with an opt-out)

- **Every factory client is enforced**, the host's included. That continues the 3a answer (`SPEC-007:335`).
- **The opt-out is an options value, not a second overload.**
  - It is `services.Configure<EgressGuardOptions>(o => o.ReportOnlyClients.Add("<name>"))`, read when the
    composition-bound guard is resolved.
  - A second `AddAshlarEgressGuard` overload would fail F4(B), which requires exactly one declaration
    (`src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressGuardConventionTests.cs:489-500`).
  - It would also be dropped by the early return (`EgressServiceCollectionExtensions.cs:54-55`) after `AddAshlar`
    applied it first.
  - A twin shows an opt-out registered after `AddAshlar` still takes effect.
- **What the opt-out stamps.** The bound guard stamps `Mode = report` and `ModeBasis = host-opt-out` for a
  `factory:<name>` site in the list.
- **Where it applies.** Ignored on AG. Honoured on SW, and on opt-in profiles.
- **Ashlar's own clients can never be opted out.**
  - Ashlar keeps a registry of the client names it registers: the unnamed default, `"AshlarExecution"`
    (`AshlarKernelRegistrar.Phases.cs:778`), the typed RunPod client
    (`RunPodCapabilityRoutingServiceCollectionExtensions.cs:50`), and the rest.
  - An opt-out naming one of them **fails boot**.
  - Every opted-out name is logged at Warning at startup.
- **A host that needs a non-loopback call on AG** declares a subject frame around it (§2.2).
- **What hosts see.** Their outer generic catches see `EgressRefusedException`; their resilience handlers do not.
  Release notes say so.

### 2.9 Child processes (Q3; recommendation: off-host unless sandboxed)

- **The hole.** A spawn that runs code from a writable working tree is recorded as `host:`. So it is always allowed,
  and the child has the host's network. The spawn sites:
  - `DotnetRunner.RestoreDestination` gives `host:dotnet` to any `build|test|publish|pack` with `--no-build` or
    `--no-restore` (`DotnetRunner.cs:52-58`).
  - `dotnet.test` always passes `--no-build` (`DotnetTestTool.cs:19-20`).
  - `ValidationServiceAdapter.RestoreDestination` does the same for EG-PROC-02 (`ValidationServiceAdapter.cs:825-828`).
  - Docker with no `DOCKER_HOST` is `host:docker` (`src/Ashlar.Infrastructure/Scaling/ProcessCommandRunner.cs:18-19`),
    even when a sandbox spec omits `--network=none` (`DockerSandboxedCommandRunner.cs:135`).
  - The `Exempt` rows for the regression runner, the instance spawner and `TimedProcess` execute code too
    (`ci/egress-inventory.tsv:54`, `:64`, `:72`).
- **How an agent can use it.** An agent can write a test project's `.csproj`, add an `<Exec>` target with
  `BeforeTargets="VSTest"`, and call `dotnet.test`. Whether MSBuild runs that target under `--no-build` is
  **[needs a test]**. Previously built test binaries run with network in any case.
- **The recommended rule:**
  - A spawn that runs working-tree code is `process:dotnet`, classified by family (NetworkExport, Internal). This
    covers dotnet build, test, run, pack and publish; forge test; the regression runner; and the instance spawner.
    MSBuild runs project targets even with `--no-build`.
  - `host:docker` is valid only when the argv carries `--network=none`.
  - Every `Exempt:LocalOnly`/`Operator`/`LocalDaemon` process row is re-audited for "executes code an agent or a peer
    can write". The result is recorded in `docs/EgressInventory.md`.
- **Consequence on AG and SW under enforcement.** `dotnet build` is already refused there by its restore (§1). This
  adds test and run, so nothing runs working-tree code with ambient network. Self-extend build and test, validation
  runs (row 26) and the regression runner are refused, unless they run in the network-off docker sandbox.
- **Rejected: carrying the frame into children through an environment variable.** The child's environment is
  controlled by the very code it would constrain: an agent's `Exec` target sets its own child's environment.

### 2.10 Test hygiene for process-global profile state (default D41; the truncated scope finding)

After the switch, the profile noted by `AddAshlar` decides enforcement at all 16 explicit sites. It is
process-global, like an environment variable. Today nothing restores it: `ClearResolved` has no callers
(`AshlarDeploymentProfileEnvironment.cs:20`). Four test classes compose AG or SW (`HostingDeploymentProfileTests`,
`OnboardingE2ETests`, `KernelDiCompositionProdStyleTests`, `KernelPhaseResolutionTests`). All four are already in the
serialized `EnvironmentVariables` collection, but none restores the profile, so a later test inherits enforcement.

- **An internal reset seam** snapshots and restores `ResolvedRaw` and the mode latch. Tests reach it by reflection,
  as they already read Abstractions internals (`EgressGuardDecisionTests.cs:978-986`).
- **Every test class that composes AG or SW, or sets the mode variable,** runs in `EnvironmentVariables` and restores
  through the seam in `Dispose`.
- **`ProcessGlobalEnvironmentConventionTests` learns the new write.** It treats `AddAshlar(` with an AG or SW profile,
  and any call to the seam, as a process-global write, just like `Environment.SetEnvironmentVariable`.
- Whether xUnit's non-parallel collection lets a leaked value reach later tests is **[needs a test]**. The seam makes
  the answer irrelevant.

---

## 3. Decisions

### 3.0 Where the scouts disagreed, and what this design picks

| # | Question | Pick, and why |
|---|---|---|
| X1 | SW at merge | Enforce, with a recorded SW break-glass (Q1). A `MaxDataSensitivity` floor uses a clearance as a data label (§8 Q3, PR 5's job), and it is unsound because reads are not clearance-checked [S2 L12]. |
| X2 | Defect 5 mechanism | S1 (c) plus the ollama.com default-off (default D35). Stripping registrations needs null objects in `NcrCapabilityRouter`'s constructor (`:23-38`) and changes pinned DI shapes. |
| X3 | Label source | The runner-declared floor, with S2's observation rules (Q2). |
| X4 | RAG labels | Canonical names only (§2.2). |
| X5 | Nesting | Chain join, propagate on dispose, `Observe` into every live frame. |
| X6 | Where the mode binds | One resolver, two bindings. The route acts on `decision.Mode`, except on AG, where either the decision or the process enforcing is enough (§2.1). |
| X7 | Redirect follower | Innermost (S4), and it re-evaluates every authority change. |
| X8 | netstandard2.0 sync `Send` | S4's mechanism in every mode (default D31). The exposure is out-of-support external code only. |
| X9 | Destination-label table | Not in PR 4. It needs open question A answered first (`SPEC-007:390-400`). |
| X10 | Operator exports | Q4. |
| X11 | Defect 3 | Mandatory on SW through Q6's recommendation, and no longer a yes/no. |
| X12 | Defect 6 | The AG part (LLM and vision) in 4.10; refusal as the final error in 4.8; the agent-policy check in PR 5. |
| X13 | Explicit sites before the switch | Yes. Every route acts on `decision.Mode`, and production produces `report` until 4.11. |

### 3.A Owner questions (8), ordered by impact

The machine-readable set is in the hand-off. Each question's options are listed recommended first.

**Q1. SW and AG at merge.**
- **Enforce, SW break-glass.** AG and SW enforce. `ASHLAR_EGRESS_MODE=report` is honoured on SW only, read once,
  logged at Warning and stamped on every decision. AG ignores it.
- **Enforce, no hatch.**
- **AG enforces, SW reports.** This departs from §5.

Both enforce options lose the same things for unlabelled flows on SW: cloud and remote models, web search, webhooks,
remote OTLP, mesh serve, pull, publish and share, remote peers, and `dotnet` restores. Agents on a local Ollama keep
working.

The AG guarantee is only as strong as its profile source. An unset `ASHLAR_DEPLOYMENT_PROFILE` means Full
(`AshlarServiceCollectionExtensions.Deployment.cs:20-24`). Every production host takes the profile from that
variable: `application/src/Ashlar.CLI/Program.cs:100`, `BackgroundAgentDaemonCommand.cs:216-223`,
`application/src/Ashlar.API/Program.cs:224-229`.

Docs to update: `AshlarDeploymentProfile.cs:58`, `product-split.md:52-54`, `Configuration.md:28` [S1].

**Q2. Where a subject's label comes from.**
- **Runner-declared floor.** Self-extend declares SystemHigh, `ToolCallingAgent` observes, and `RAGTool` reports
  canonical labels. It needs the owner statement: "A runner may declare a floor below SystemHigh only for inputs it
  built and can vouch for; in PR 4 no production runner does."
- **No producers.**
- **A `MaxDataSensitivity` floor.**

**Q3. Child processes that run agent-writable code** (§2.9).
- **Off-host unless sandboxed.**
- **Agent tools only.**
- **Keep Host, as a Known limit.**

**Q4. What may leave an AG or SW host before PR 6.**
- **File exports only.** `pkg export --out`, `export` and `mesh export` run in report mode, recorded as
  `operator-verb`. `pkg publish` and `pkg share` enforce: they write the mesh store that peers pull, which can be a
  network mount.
- **All five operator verbs.**
- **Pure BLP.**
- **Destination table**, which needs A.

Report mode for an operator verb is a stop-gap for PR 6's signed-off downgrade: labelled data leaves with only a
record.

The mechanism:
- `Initiator` is not a public settable property of `EgressRequest` (`EgressRequest.cs:13`, `:21`, `:38`). It is set
  only through an internal API visible to `Ashlar.CLI` and Infrastructure.
- `mesh export` passes it through `ISneakernetTransport.ExportAsync`, because the guard call is in
  `SneakernetTransport.cs:36`.
- A convention test pins the CLI call sites.

**Q5. Are host-app factory clients enforced?** (3a owner Q1, reserved for PR 4)
- **All, with an opt-out list** (§2.8).
- **All, no opt-out.**
- **Ashlar's clients only.**
- **Only inside a frame.**

The answer also decides whether 4.3's redirect filter applies to host clients.

**Q6. Inbound surfaces on AG and SW** (§2.7). This replaces O6's defect 3 yes/no.
- **Loopback only, no MCP over HTTP.**
- **MCP over HTTP refused only.**
- **A Known limit until PR 5.**

**Q7. How much a refusal reveals** (open question D, `SPEC-007:423-425`).
- **D-b**, with a random `ref` to remote parties.
- **Minimal for the model.**
- **D-c, with level names.**
- **D-a, the full Detail.**

**Q8. Caveat scope** (open question C, `SPEC-007:416-421`, due before PR 4).
- **C1, level only.**
- **C3, with a normalisation now.**
- **C2, the flags become caveats.**
- **C4, defer again.**

### 3.B Engineering defaults (applied unless the owner objects)

| # | Default |
|---|---|
| D1 | One internal resolver. The process binding serves the explicit sites. The composition-bound guard from `AddAshlar` replaces only a `ProcessDefault` registration. Routes act on `decision.Mode`. |
| D2 | Mode plumbing lands before any route acts (4.6). `enforce` is an opt-in on every profile, and every default stays `report` until 4.11. |
| D3 | The mode override is read once per process, never from `IConfiguration`. `AshlarHostingOptions.EgressMode` can only raise the mode. |
| D4 | An explicit-profile guard never reads the environment. |
| D5 | The strictest profile noted in the process wins (AG > SW > others). |
| D6 | On AG, a route enforces when the decision or the process enforces. A host guard cannot lower the mode on AG. |
| D7 | Unrecognised mode or profile values, and mode-resolution faults, enforce. |
| D8 | `ModeBasis`, `Refused` and `Ref` are appended to `EgressDecision` and event 1; event 2 `Refused` is at Warning. |
| D9 | `InternalsVisibleTo` `Ashlar.AI.Pipeline` and `Ashlar.Infrastructure` in Abstractions. |
| D10 | The startup line comes from a hosted activator, or stderr for host-less CLI verbs, and says whether the profile was defaulted. |
| D11 | The `ProcessDefault` fallback logs a Warning only when resolving the guard throws; `null` is counted at Debug. |
| D12 | Monotone nesting (chain join, propagate on dispose) lands before any producer. |
| D13 | `Observe` only raises. A read scope is satisfied only by `Report` from a tool that declares itself labelled (RAGTool alone). |
| D14 | A tool scope that ends by exception observes SystemHigh. |
| D15 | RAGTool reports "read nothing" for 0 hits and for its refusal. Its label is the trimmed canonical name, else SystemHigh, with a parity test. |
| D16 | Model responses are not reads; responses from `peer:` and other agent-backed targets observe SystemHigh. |
| D17 | A session is one runner run; persisted state comes back SystemHigh. |
| D18 | System egress stays `no-subject`. The internal, raise-only `EgressSubject.Detach()` is used at AgentBus subscriber dispatch. |
| D19 | A frame below SystemHigh wraps only observed code. A convention test pins every production `Enter`, its floor and its method. |
| D20 | `EgressRefusedException : Exception`, sealed, carrying the decision; CA1032 suppressed with a reason. |
| D21 | HTTP refuses with a faulted task, or a throw on `Send`; never a synthetic 4xx. |
| D22 | The positive convention: every `.Evaluate(new EgressRequest(` is followed by `.ThrowIfRefused()`, or its `.Refuses` is read. |
| D23 | No same-destination retry. Fallback is allowed. The refusal is the final error when nothing succeeded. Never the echo model or the breaker. |
| D24 | Catch-all rows 1–29 as tabled in §2.3. |
| D25 | The four degrade sites. |
| D26 | A tool refusal gives a REFUSED observation and the cycle continues; the third refusal in a cycle stops it with `egress_refused`. |
| D27 | Trust-boundary exits walk the exception chain and send a fixed text plus `ref`. `seq` stays operator-only. |
| D28 | Refusals log at Warning, rate-limited per 5-minute window with a suppressed-count summary; swallowing catch-alls log their own windowed Warning. |
| D29 | The durable `IDataDecisionAuditLog` adapter is deferred out of PR 4. |
| D30 | A CLI refusal exits 77. |
| D31 | Gap 1 option C in every mode, the SDK-policy statement, a `NoDecision` record from the hop, and the harness inside the cert-gate project through an ALC. |
| D32 | Redirects: innermost per-authority evaluation, the guard handler re-inserted if removed, unknown primaries checked after the send. |
| D33 | Cross-host redirects: P2 for `EgressHttp`-built clients, P1 for factory clients. |
| D34 | The gap 3 record fixes, and the unconditional Ollama `cloud`-tag rule. |
| D35 | Defect 5: keep the DI shape; AG routes local; AG never escalates (LLM and vision); options-bound AG boot validators; ollama.com off on AG. |
| D36 | The host opt-out is `Configure<EgressGuardOptions>`. It fails boot for Ashlar's client names and logs a Warning per name. |
| D37 | `EgressSubject.Enter` stays public. |
| D38 | Open questions A, B and E stay open. B's legacy-side flip belongs to PR 5, which reinterprets `SPEC-007:414` ("the PR that switches enforcement"). |
| D39 | Defect 4 is fixed after the switch; the AG Bedrock boot refusal lands first. |
| D40 | The Known limits say loopback is inside the boundary only for non-relaying services, and AG is as strong as its profile source. |
| D41 | Tests that compose AG or SW are serialized and restore the profile and the latch through a reset seam; the environment convention learns this. |
| D42 | The leak test runs in `EnvironmentVariables`. Scenario A uses an explicit guard; Scenarios B and C compose through `AddAshlar(SecureWorkstation)`. |

---

## 4. The PR split

Merges are serial (CLAUDE.md). Every behavioural change is mutation-checked with `scripts/mutation-check.sh`.
Readiness lanes are diff-conditional, so cert-gate tests are the evidence.

| PR | Content | Done when | Needs |
|---|---|---|---|
| **4.1** Host-reading records | Gap 3: `file:` never Host; `mesh-peer:<ip>`; MEAI `ProviderUri`, the LLamaSharp type check and fail-closed `meai:<key>`; the Ollama `cloud` rule; `OllamaHttpChatClient` stops following redirects (CHANGELOG: behaviour change). | Each twin red at base and green after; the four mutations go red; the CLI twin flips are named; the classifier rows are rewritten and not claimed as red-to-green; the Known-limits bullets are removed. | none |
| **4.2** netstandard2.0 sync `Send` | Gap 1 C: the `HttpMessageHandler` hop with its `NoDecision` record; `CreateDelegatingHandler` throws on such runtimes; the ALC twin in `Ashlar.Tests.Infrastructure`; the SDK-policy statement. | Today `Send` records 0 decisions and reaches the stub; after, `NotSupportedException`, 0 requests, `SendAsync` still 1 decision; the mutation goes red in cert-gate. | none (D31) |
| **4.3** Redirects | R-a, R-b, R-c; per-authority re-evaluation; the guard-handler re-insert; the post-send check; both SNS sites; P2 for `EgressHttp` clients; IVT for Infrastructure; CHANGELOG and release notes. | Stub, factory, differential, enforcement, rewrite and `Clear()` twins; seven mutations red. | **Q5** |
| **4.4** Frame semantics | Monotone nesting; `Observe` only raises; `BeginRead` with `Report` from labelled tools; a thrown scope observes SystemHigh; `Detach` at AgentBus; flip `An_Internal_subject_may_reach_…`; `PublicAPI.Unshipped`. | Twins: Public inside Secret decides Secret; dispose and live propagation; an unreported scope gives SystemHigh; `Observe` alone does not satisfy a scope; a throw gives SystemHigh; an AgentBus subscriber runs with no subject. A mutation per rule. | none |
| **4.5** Producers (report-only) | Self-extend frame at SystemHigh; `ToolCallingAgent` read scopes; RAGTool labels and "read nothing"; the trim parity test; peer responses observe SystemHigh; the floor-pinning convention and its row. | The leak skeleton passes in report mode: after a Secret hit, the next model call records `subject:agent:<id>`, `Current = Secret`, would-refuse `LevelTooLow`; an Internal hit would allow. Production self-extend records `subject:` at SystemHigh. | **Q2, Q8** |
| **4.6** Mode plumbing, defaults unchanged | `ResolveMode`; the `ModeBasis`/`Refused`/`Ref` fields and event 1; `enforce` opt-in through `ASHLAR_EGRESS_MODE`, `AshlarHostingOptions.EgressMode` and the explicit guard constructor; latching; strictest-wins; the reset seam and the convention change (§2.10); the composition-bound guard (replaces `ProcessDefault` only); IVT for AI.Pipeline; the startup line. The opt-in stays undocumented until 4.11: until 4.7–4.9 merge, an enforcing record does not mean the send stopped. | The 6 × 4 mode table is pinned with every profile defaulting to report; a fault resolves to enforce; strictest-wins and the reset seam have twins; the `AddAshlarEgressGuard`-first twin binds the composed guard. | none |
| **4.7** Refusal surface: routes | `EgressRefusedException`, `Refuses`/`ThrowIfRefused`; the handler and chat client throw under enforce; fault handling (§2.4); the windowed Warning sink, event 2, the MEAI audit. | Twins under `ASHLAR_EGRESS_MODE=enforce` (serialized): the inner handler or client is never called; the exception carries the decision; a fault, a throwing host guard and a null host guard are refused. | **Q7** |
| **4.8** Refusal surface: catch-alls and exits | Rows 1–16, 19–22, 24–29; transports; the orchestrator exemption; the A2A, gRPC and MCP exit text with the chain walk; `ToolCallingAgent` mapping and the cap of 3. | Per-site twins under the opt-in: no retry (one decision per `ProviderFactory` send); no echo output; the breaker stays closed; remote exits show only the fixed text; validation reports "refused", not a failed test. A mutation per catch site. | **Q7** |
| **4.9** Explicit sites, operator verbs, child processes | The 16 sites move to the positive convention; the four degrade paths; `application/` rows 17, 18 and 23 and the IDE SSE text; CLI exit 77; the operator initiator per Q4; process classification per Q3; the exempt-row re-audit. `[coordinated-integration]`. | Explicit-site twins under the opt-in refuse or degrade as designed; the operator verbs record `operator-verb` per Q4; per Q3, `dotnet.test` is refused before `Process.Start`; the convention is mutation-checked. | **Q3, Q4, Q7** |
| **4.10** AG and SW hygiene | Defect 5 (D35), including the vision no-escalate; the options-bound validators; inbound per Q6. `[coordinated-integration]` if the API's listeners change. | A cert-gate twin composing `AddAshlar(AirGapped)`: overnight routing chooses Local with the AG reason; vision never tries openai or azure; the opt-in validators fail boot; ollama.com is disabled; Full is unchanged. Per Q6: MCP over HTTP on SW fails boot and stdio boots; a non-loopback listener on AG or SW fails boot. | **Q6** |
| **4.11** The switch | AG and SW default to enforce; the SW break-glass per Q1; AG ignores overrides; AG's either-rule (D6); the host opt-out per Q5; flip the "never refuses" twins by name (`EgressGuardDecisionTests…An_explicit_profile_is_reported_and_does_not_change_the_decision` `:757`, plus the handler and chat-client twins); **`EgressEnforcementLeakTests`** and its row; SPEC-007 §2.3 "Enforced by" lines; row 64; docs; release notes. | **The §5 done-when is green, with every control and mutation red as designed**; the AG override is ignored and recorded. | **Q1, Q5**, and 4.1–4.10 merged |

**Start now, with no decision: 4.4 → 4.1 → 4.6 → 4.2.**
- 4.4 closes the declassification hole first.
- 4.6 unblocks every enforcing twin in 4.7–4.9.

**Then, as answers arrive:** 4.5 (Q2, Q8), 4.7 and 4.8 (Q7), 4.9 (Q3, Q4), 4.3 (Q5), 4.10 (Q6), and 4.11 (Q1).

**Records each PR touches.** Every PR also updates the SPEC-007 status, the CHANGELOG, the knowledge graph
(regenerated after `git add`) and an agent-bus `handoff`.

| PR | `EgressInventory.md` | TSV | `cert-gate-assertions.md` | `PublicAPI.Unshipped` | `application/` (`[coordinated-integration]`) | `READINESS_PATHS` |
|---|---|---|---|---|---|---|
| 4.1 | Known limits, §records | MESH-03, MDL-01, MESH-07/08 rows | row 64 | – | yes (MeshServeService) | – |
| 4.2 | Known limits | +1 `Exempt:GuardImpl` | row 64 (twin) | – | – | – (ALC path) |
| 4.3 | Known limits | redirect handler row | row 64 | – | yes (SNS) | – |
| 4.4 | – | – | new rows (nesting, read scope, Detach) | `Observe`, `BeginRead`, `ReadScope` | – | – |
| 4.5 | producers section | – | floor-pin convention row | labelled-tool marker | – | – |
| 4.6 | mode section | – | env convention row update | `EgressDecision` members, hosting option, guard constructor | – | – |
| 4.7 | refusal section | – | row 64 | `EgressRefusedException`, `Refuses`, `ThrowIfRefused` | – | – |
| 4.8 | refusal section | – | – | – | – | – |
| 4.9 | process rows (Q3) | PROC rows | positive-convention row | operator initiator (internal) | yes | – |
| 4.10 | AG section | – | AG twin row | – | yes if listeners change | – |
| 4.11 | status | – | leak-test row, row 64 | – | yes (docs only) | – |

---

## 5. The seeded leak test (§5 done-when)

**File and collection.** `src/Ashlar.Tests.Infrastructure/Tests/Certification/EgressEnforcementLeakTests.cs`, in
`[Collection("EnvironmentVariables")]`, with `[Trait("Category","Certification")]`.
- The constructor snapshots, and `Dispose` restores: `ASHLAR_DEPLOYMENT_PROFILE`, `ASHLAR_EGRESS_MODE`, `ResolvedRaw`
  and the mode latch, through the reset seam (§2.10).
- **It depends on the environment, on purpose.** Scenario B's refusal comes from the explicit EG-WEB-01 site, which
  always calls `EgressGuard.ProcessDefault` (`BingWebSearchProvider.cs:56`; its constructor `:29-42` takes no guard).
  `ProcessDefault`'s profile comes from the static or the variable (`EgressGuard.cs:55`, `:135-138`;
  `AshlarDeploymentProfileEnvironment.cs:8-23`).
- Scenario A is free of the environment: an explicit-profile guard never reads it (D4). Its destination does not
  depend on `ASHLAR_OLLAMA_BASE_URL` once 4.1's `ProviderUri` fix is in.

**Scenario A: the agent's own model call writes Secret down** (the MEAI route, explicit guard).

1. **DI.**
   - `IEgressGuard` = `new EgressGuard("secure-workstation")`.
   - `AddAshlarMeaiPipeline`, with `ollamaInnerFactory` returning a **scripted stub** `IChatClient` whose metadata
     reports `ProviderUri = http://gpu-<guid>.example:11434`.
   - Resolve the keyed `local:ollama` client. Its outermost layer is `EgressGuardChatClient`. PolicyGate allows every
     `local:` key, so only the egress guard can stop the call. The destination records as ExternalModel / Internal,
     EG-MDL-01.
   - The `peer:` alternative from DESIGN-4.md is dropped: under D16 a `peer:` response observes SystemHigh, which
     would change call 2's reason.
2. **Model:** `new MeaiBackedModel(client, NullLogger)`.
3. **Label source.**
   - A real `RAGTool` over `MeaiVectorDataRagAdapter` over `VectorDataRagService`, with a deterministic test
     embedder.
   - One record at tier **`"Secret"`** holds `CANARY-<guid>`.
   - The snapshot carries `agentId = leak-<guid>` and `maxDataSensitivity = "Secret"`. Production never sets that key
     [S2 L12]. Wiring it is PR 5; the owner accepts this caveat.
4. **Runner.** The test enters `EgressSubject.Enter("agent:leak-<guid>", new HighWaterMark(SecurityLabel.Public))`,
   then runs `new ToolCallingAgent(...).RunCycleAsync(snapshot, toolbox{rag_search}, new PolicyEngine([]), null, memory, ct)`.
5. **The script.** Call 1 returns a `rag_search` call for the indexed text. Call 2 would return `{"tool_calls":[]}`.
   The stub records every message.
6. **The egress attempted.** Call 2, whose conversation holds the Secret chunk, goes to an Internal destination.

**Expected.**
- **The stub:** exactly **1** call, and no canary in any message.
- **The cycle:** `StoppedReason == "egress_refused"`.
- **Memory:** exactly one `egress.refused` record, redacted per Q7. Under D-b it contains `LevelTooLow`, `EG-MDL-01`
  and `ref`, and contains neither the canary nor `Secret` nor `Internal`.
- **The decision for call 2**, from an `EgressDecisionLog` sink filtered by `subject:agent:leak-<guid>`:
  - `Mode == "enforce"`, `ModeBasis == "profile:secure-workstation"`, `Refused == true`;
  - family `model.meai`, class ExternalModel, label Internal;
  - `Current == Secret`, `CurrentBasis == "subject:agent:leak-<guid>"`;
  - `Access.Reason == LevelTooLow`, and `Detail` names both levels (`ReferenceMonitor.cs:66-71`).
- **ILogger:** one Warning, event 7301.

**Scenario B: a tool writes it down** (the explicit site, through the process binding).
- **Composition:** `AddAshlar(o => o.DeploymentProfile = SecureWorkstation)`, which notes SW process-wide.
- The same frame and read. The stub reports `ProviderUri = http://127.0.0.1:11434` (Host), so call 2 is allowed.
- Call 2 asks for `web_search`, served by the production `WebSearchTool` over `BingWebSearchProvider` with a stub
  `HttpClient`. EG-WEB-01 is Confidential, and Secret > Confidential.
- **Expect:**
  - the Bing stub sees **0** requests;
  - the model sees one `REFUSED` observation with the same redaction;
  - call 3 returns empty, so `StoppedReason == "empty"` and the refusal count is 1;
  - the decision is `LevelTooLow`, WebSearch / Confidential, `ModeBasis == "profile:secure-workstation"`.

**Scenario C: the factory route and a redirect** (the composition binding). Same composition as B, inside a frame
whose mark has observed Secret.
- **(i)** `IHttpClientFactory.CreateClient("leak")` with a stub primary, sending to `https://remote-<guid>.example`.
  Expect `EgressRefusedException`, and the stub sees 0 requests.
- **(ii)** A factory client with the default `SocketsHttpHandler` primary calls a loopback Kestrel, which answers 307
  to `http://remote-<guid>.example/b`.
  - Hop 1 (Host) is allowed. Hop 2 is refused.
  - The exception is `EgressRefusedException`, not a DNS `HttpRequestException`, which shows it was refused before
    any connect.

**Controls: they make the cause provable.**

| # | Change | Expected | Proves |
|---|---|---|---|
| C0 | (built into A) call 1, before the read | allowed, `Current = Public` | the same egress was allowed until the label was read |
| C1 | record tier `"Internal"` | call 2 allowed; the stub sees 2 calls; `"empty"` | the refusal is caused by the label's level |
| C2 | snapshot clearance **`"TopSecret"`**; record tier blank, or the custom name `"Restricted"` | the RAG audit shows `results=1`, **then** call 2 is refused with `SystemHighData` | an unknown label that was read fails closed (E9), not an empty search |
| C3 | guard `new EgressGuard("full")` | call 2 sent; `Mode = report`, would-refuse `LevelTooLow` | the switch is per profile |
| C4 | a test tool that enters `Enter("inner", new HighWaterMark())` and, **inside that frame**, sends through a guarded stub | that send is refused, `Current = Secret`, `LevelTooLow` | monotone nesting. A frame set inside an async tool never flows back to the agent, so the egress must happen inside it. |
| C5 | an unlabelled test tool instead of `rag_search` | call 2 refused, `SystemHighData` | the unreported-read rule |
| C6 | the runner enters no frame | call 1 refused, `SystemHighData`, `no-subject` | the floor is the runner's declaration; no-subject fails closed |
| C7 | `AddAshlar(AirGapped)` with `ASHLAR_EGRESS_MODE=report` | still refused; `ModeBasis == "override-ignored"` | on AG no override lowers the mode |
| C8 | a classification fault (a relative URI through a test site) | refused; `Fault` set; the inner handler is never called | a fault fails closed |
| C9 | a host `IEgressGuard` that throws, and one that returns `null`, on the factory route | refused, with a synthetic `NoDecision` | a broken host guard fails closed |
| C10 | (if Q3 picks off-host) `dotnet.test` inside the Secret frame, in an empty directory | refused before `Process.Start` | child processes are mediated |
| C11 | a tool that reads, calls `Observe(Public)` on a side value, and returns unlabelled text | call 2 refused, `SystemHighData` | `Observe` cannot launder a tool result |
| C12 | a tool that reads and then throws | call 2 refused, `SystemHighData` | a thrown tool counts as a read |

**Mutations: each must go red with counts.**

| # | Mutation | Goes red |
|---|---|---|
| M1 | the chat client discards the decision | A (the stub sees 2 calls) |
| M2 | RAGTool does not report | A (`SystemHighData`, not `LevelTooLow`) |
| M3 | `ResolveMode` returns report for SW | A, B, C |
| M4 | `CanWrite` always refuses | C0, C1 |
| M5 | `Resolve` reads only the innermost frame | C4's inner-send assertion |
| M6 | drop the unreported-read rule | C5 |
| M7 | `ToolCallingAgent` maps a refusal to `"error"` | A's stop-reason assertion |
| M8 | `Message` carries `Detail` | the redaction assertions |
| M9 | RAGTool maps an unknown tier to Public | C2, while C5 stays green |
| M10 | a fault decision is allowed | C8 |
| M11 | the composition binds a Full-profile guard | C(i) |
| M12 | the guard handler is removed from `AdditionalHandlers` | C(i) |
| M13 | (Q3) `host:dotnet` restored | C10 |
| M14 | any `Observe` satisfies a read scope | C11 |
| M15 | a thrown tool's scope is discarded | C12 |
| M16 | the Bing site keeps `_ =` | B (the stub sees 1 request) |

**Records in 4.11.**
- A new `ci/cert-gate-assertions.md` row: "An agent that has read labelled data cannot write it down: under an
  enforcing profile the egress guard refuses, explained, before the send".
- "Enforced by" lines under SPEC-007 §2.3 (`:128-135`).
- Row 64 revised.

---

## 6. Out of scope, and the other 3a defects

### PR 5 to 8 and §8: PR 4 must not

- **PR 5:**
  - no clearances, no `CanRead` on the read side, no switch of the RAG filter to labels;
  - no flip of B's assertions (D38);
  - no clearance configuration;
  - no sealed-skill manifest level;
  - no receiver-side parsing (A);
  - no `CanRead` at the inbound server seams (Q6 only bounds who can connect).
- **PR 6.** Nothing that lowers a label:
  - no API to reset a mark (`Detach` only raises);
  - no per-site allow-list (Q4's operator verbs are *unenforced and recorded*, and their records still say
    `LevelTooLow`/`SystemHighData`);
  - no `--release-as`, no downgrade receipts.
- **PR 7.** No labels on provisioned resources.
- **PR 8.** No `INativeExecutionHost`.

### Deferred, and recorded as follow-ups

- **A machine-scoped profile floor** (an admin-writable file that environment and config can only raise). PR 4 hardens
  what exists (D3, D5, D6, D40).
- **Narrowing Host to non-relaying loopback endpoints** (§2.6). It needs the destination table, so it waits on A.
- **Generic text for every exception, not only refusals, at the trust-boundary exits.** That is a general
  error-hygiene change that alters every existing error path.
- **The durable `IDataDecisionAuditLog` adapter** (D29), with PR 6 receipts.
- **Refusing boot on AG/SW for unknown factory primaries.** D32 checks them after the send and names them at startup.

### The other 3a defects

| Defect | Where it goes |
|---|---|
| 1. gRPC open relay (`AgentTransportServiceImpl.cs:173`, `:176`) | A standalone security PR now. Open on Full and Server. |
| 2. Fleet task-result download (`CommercialFleetEndpoints.cs:495-499`, `:564`) | A commercial security PR: confine downloads to the result root. |
| 3. MCP HTTP server allowed on SW (`ValidateAshlarMcpServerOptions.cs:31`) | 4.10, through Q6 |
| 4. Dead MEAI cloud allow-list [S1] | After 4.11, in its own PR; the AG Bedrock refusal first (D39) |
| 5. AG registers network paths | 4.10 (D35) |
| 6. `AdaptiveProviderFactory` escalates (`:47-49`, `:85`) | AG no-escalate in 4.10; refusal as the final error in 4.8; the agent-policy check in PR 5 |
| 7. `RemoteBrick` posts to a host the catalog chooses [S1] | The guard refuses on AG/SW; for Full/Server, a follow-up |
| 8. `RequireLocalOnly`/`BlockExternalLLMs` never checked; `mcp:` ids pass [S1][S2] | PR 5; the `mcp:` deny-list is a small independent PR |

**Side findings:**
- The SNS signing host check admits `*.amazonaws.com` [S4 S2].
- `CloudSanitizationProxy` skips sanitising when a wire-supplied `IsAirGapped` is set [S1].
- `docs/Configuration.md:168` drift [S1].
- There is no AG network evidence today [S1]. 4.10's AG twin and the leak test's C7 are the first.

---

## 7. Critic findings handled

Each was checked against `8ec674d2a`. **A** = accepted, **P** = accepted in part, **R** = rejected.

**Code-truth critique**

| # | Finding | | Outcome |
|---|---|---|---|
| CT1 | Scenario B is not free of the environment (`ProcessDefault`) | A | Leak test in `EnvironmentVariables`; B and C through `AddAshlar(SW)`; reset seam; explicit guards never read the environment (D4, D42) |
| CT2 | C2 never reaches the label mapping (RAG filter) | A | C2 at `TopSecret` clearance, asserts the hit; M9; "read nothing" for 0 hits and the refusal (D15) |
| CT3 | C4's frame cannot flow out of an async tool | A | C4 sends inside its own inner frame; M5 points at that send |
| CT4 | The MEAI fallback and synthetic decision need internals; AI.Pipeline has no PublicAPI | A | IVT for AI.Pipeline (D9); the "Unshipped" note removed |
| CT5 | Six more masking sites; §2.4 pointed at rows that did not exist | A | Rows 24–29; §2.4 corrected |
| CT6 | The AG no-escalate misses single-image vision | A | 4.10 covers `:85` with a twin |
| CT7 | Mesh pull is omitted, so "import-only" is false | A | Added to §1, Q1 and Q4; "file import only" |
| CT8 | `mesh export`'s guard is in Infrastructure; `pkg pack` does not exist | A | Initiator passed through `ExportAsync`; `pkg export --out` throughout |
| CT9 | Cert-gate runs one project | A | ALC twin inside the cert-gate project; a shell-lint driver as fallback (D31) |
| CT10 | "The bound guard wins" fails if `AddAshlarEgressGuard` ran first | A | Replace only a `ProcessDefault` descriptor; twin |
| CT11 | `FromName` does not trim | A | Trim before lookup; parity test |
| CT12 | Row 12's real path is the router's auditor | A | Cited `MeaiPipelineServiceCollectionExtensions.cs:126-128`; router twin |
| CT13 | Two configuration keys lack their prefix | A | Corrected; validators bind the options types |
| CT14 | The explicit classifier rows never go red | A | Only the CLI twins are claimed as flips |
| CT15 | `AddAshlar` has no logger; the Warning on `null` would fire in production | A | Hosted activator and stderr; Warning only on a throw (D10, D11) |
| CT16 | Producer-boundary wording | A | Reworded (§2.2) |

**Security critique**

| # | Finding | | Outcome |
|---|---|---|---|
| SE1 | Spawns of agent-writable code are `host:` | P | Owner question Q3 (it has a user-visible cost), off-host recommended; exempt-row re-audit; C10 and M13. **Rejected:** propagating the frame through an environment variable, because the agent's code controls its child's environment. |
| SE2 | Host relays; the Ollama `cloud` rule was optional | P | The `cloud` rule is unconditional (D34); the threat model and Known limits are stated (D40). **Rejected for PR 4:** narrowing Host to configured loopback ports. Relays cannot be identified (Ollama itself relays), it would refuse a local collector on AG, and per-endpoint labels wait on A. |
| SE3 | Inbound responses on SW | A | Owner question Q6, with loopback-only and the MCP HTTP refusal recommended, no longer optional |
| SE4 | "AG cannot be turned off" | P | Strictest-wins (D5), read-once and raise-only overrides (D3), AG either-rule (D6), O1 text, a "defaulted" note in the startup line. **Rejected:** a Warning on every defaulted Full start (noise for the default profile) and the machine floor file in PR 4 (a follow-up). |
| SE5 | `operator-verb` reachable without a human | P | `Initiator` internal (IVT); a file-exports-only option in Q4. **Rejected:** denying on an inherited-subject variable or a missing tty. The process under test controls both. |
| SE6 | The evaluated URI can differ from the wire | P | Per-authority re-evaluation, re-insert after `Clear()`, post-send check for unknown primaries (D32). **Rejected:** boot failure for unknown primaries on AG/SW. In-memory handlers are common and never follow; replaced by a startup Warning. |
| SE7 | Ambient frames reach pub/sub subscribers | A | `Detach` at AgentBus (D18); the producer rule (D19); threading facts corrected |
| SE8 | A thrown tool is not a read; any `Observe` satisfies a scope | A | D13, D14; C11, C12, M14, M15 |
| SE9 | C2 is vacuous | A | As CT2 |
| SE10 | §5 never exercises `AddAshlar`, the factory route, a fault or a redirect | A | Scenario C; C8, C9, C10; M10–M13 |
| SE11 | Rate limiting makes refusals silent | A | Windowed Warning with a suppressed-count summary (D28) |
| SE12 | Wrapped refusals leak; `#seq` is a covert channel | P | Chain walk and a random `ref` (D27). **Rejected for PR 4:** generic text for every exception (a follow-up). |
| SE13 | Refusals as an oracle | P | Cap of 3 per cycle (D26); a minimal model-facing option added to Q7 |
| SE14 | The convention bans only `_ =` | A | Positive rule (D22) |
| SE15 | Peer responses can launder data | A | D16 |
| SE16 | The opt-out can name Ashlar's clients | A | Boot failure and Warning (D36) |

**Scope critique**

| # | Finding | | Outcome |
|---|---|---|---|
| SC1 | No enforcing decision exists before the switch | A | New 4.6 mode plumbing (D2) |
| SC2 | The leak test depends on test order | A | As CT1, plus the reset seam |
| SC3 | E3 and E18 had no PR; 4.7's operator-verb condition needs mode resolution | A | E3 → 4.6; E18 deferred (D29); operator verbs in 4.9 after 4.6 |
| SC4 | 4.3 assumes O3; second SNS site; no `[coordinated-integration]` | A | 4.3 needs Q5; both SNS sites; token added; CHANGELOG in 4.3 |
| SC5 | Gap 1's exposure was omitted; the harness cost was hidden | A | Exposure stated; ALC harness; the hop records. **Kept as a default (D31), not a question,** because only out-of-support external code is exposed. |
| SC6 | A `ReportOnlyClients` overload breaks F4(B) | A | `Configure<EgressGuardOptions>` (D36) |
| SC7 | (truncated in the critique) the sticky process-wide `ResolvedRaw` after the switch | A | Read as: profile state leaks enforcement into later tests. Handled by §2.10 and D41. |
| SC-v | Split 4.6; a records table; E20, E21 and E23 are owner calls; O4(b) is PR 6 in another form | P | 4.6 split into 4.7 and 4.8; records table in §4; O4's PR 6 framing stated. E20, E21 and E23 stay defaults (D31, D32/D33, D38) under the 8-question cap, with the `SPEC-007:414` reinterpretation flagged for the owner. |

---

## Appendix A. Corrections to earlier citations

**Corrected after the scouts:**
- S5's `AshlarGovernanceChatClientBuilderExtensions.cs:99-100` and `:128-138`: the file has 67 lines. The first `Use()`
  is at `:26-28`, and the fallback is at `:56-66`.
- S5's `MeaiEgressDestination.cs:108-114` and `:135-141`: the file has 113 lines. The branches are at `:74-80` and
  `:101-107`.

**Corrected after the critiques:**
- There is no verb `pkg pack`. The guard at `PkgCommand.cs:93` belongs to `pkg export --out` (`:54`).
- The MeshLab section is `Ashlar:MeshLab:WorkerExecutor`, and the MEAI section is `Ashlar:Meai`.
- Row 12's reachable path is `MeaiPipelineServiceCollectionExtensions.cs:126-128`.
- `EgressExplicitSiteTwinTests.cs:267-271` are classifier rows; only `EgressCliSiteTwinTests.cs:73-113` flip.
- `TrustTierOrder` trims names; `DataSensitivityLevels.FromName` does not.
- AI.Pipeline has no `PublicAPI.*.txt`.
- §2.4 of DESIGN-4 named MeshLab and the autonomy loop as table rows that did not exist; they are now rows 28 and 29.
- The B sentence is at `SPEC-007:414`, not `:406-412`.

## Appendix B. Claims only a build or test settles

- HttpClient passes a handler's non-cancellation exception through unwrapped.
- Grpc.Net.Client's wrapping, and `Status.DebugException`.
- Whether the A2A 1.0.0-preview2 and MCP 2.2.0 transports wrap a handler exception, and whether they retry.
- `BackgroundServiceExceptionBehavior` in the daemon.
- System.CommandLine's default exit code for an unhandled exception.
- CA1032 and serialization rules on the three TFMs.
- That the base `HttpMessageHandler.Send` throws on net5+, and **that an isolated ALC can host the netstandard2.0
  Abstractions build for the twin**.
- The runtime's redirect rules (the differential twin settles them); the factory filter ordering; the setter throwing
  on a started handler.
- `AllowAutoRedirect` in AWSSDK.Core 4.0.100.4.
- **Whether MSBuild runs a `BeforeTargets="VSTest"` target under `dotnet test --no-build`** (Q3).
- **Whether `*.localhost` is resolved through DNS on glibc or musl without systemd-resolved.**
- **Whether xUnit's non-parallel collection lets a leaked `ResolvedRaw` reach later tests** (§2.10).
- The dead allow-list (defect 4).
- AG reaching RunPod through `POST /api/bricks/.../execute` with `overnight=true` [S1].
- That the leak test's RAG query scores 1.0 with a deterministic embedder [S5].

---

## Owner answers (2026-10-05). These override §3.A.

The owner chose the recommended option on all eight questions. They are recorded in SPEC-007's decisions log, PR #713.

| Q | Answer |
|---|---|
| Q1 | Enforce, with a break-glass on SecureWorkstation only: `ASHLAR_EGRESS_MODE=report`, read once, logged at Warning and stamped on every decision. AirGapped ignores every override. |
| Q2 | The runner declares the floor, and ToolCallingAgent observes tool results. RAG reports canonical tiers, an unreported read counts as SystemHigh, and self-extend runs at SystemHigh. |
| Q3 | A child process is off-host unless it runs in the network-off docker sandbox. |
| Q4 | File exports only. `pkg export --out`, `export` and `mesh export` run in report mode, recorded as an operator verb. `publish` and `share` are refused on AirGapped and SecureWorkstation. |
| Q5 | Every factory client is enforced, with an opt-out list in `Configure<EgressGuardOptions>`. AirGapped ignores the list, and naming one of Ashlar's own clients fails boot. |
| Q6 | Loopback only, with no MCP over HTTP: on SecureWorkstation, MCP over HTTP fails boot. On both profiles the API and mesh serve must bind loopback. |
| Q7 | D-b with a random ref. |
| Q8 | C1: labels carry the level only. C3 is the rule for the first producer of custom levels. |

The engineering defaults in §3.B stand: the owner raised no objection.
