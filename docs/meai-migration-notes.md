# MEAI Migration Notes — Phase 0 Discovery

**Status:** Phase 0 complete (read-only discovery)  
**Date:** 2026-07-14  
**Repo TFM today:** host/library projects are **`net8.0`** (SDK pinned to `9.0.100` in `global.json`). Plan asks for **.NET 9**; Phase 1 should introduce `Ashlar.AI.Pipeline` as `net9.0` (or dual-target) and confirm host upgrade scope separately.  
**MEAI today:** **none** — no `Microsoft.Extensions.AI*` packages, no `IChatClient`.

This document is the map for Phases 1–6. Later phases must update this file when discoveries invalidate assumptions.

---

## 1. Model invocation today

### Primary seam: `IModel` → `IProviderFactory`

```
Agents / ToolCallingAgent / Orchestration
        │
        ▼
   IModel.CompleteAsync(ModelInput)
        │
        OrchestrationRuntimeModelDecorator
          → HotSwappableModel
            → ProviderBackedModel
        │
        ▼
   IProviderFactory.ExecuteLLMAsync / Vision / Video
        │
        ├── openai / openai_compat / azure  → HttpClient chat completions
        ├── ollama                          → OllamaProvider → POST /api/chat
        ├── local                           → LocalModelProvider (LLamaSharp + GGUF)
        ├── video                           → VIDEO_SERVICE_URL HTTP
        └── mock / offline / echo           → MockScaffoldingResponder
```

| Type | Path | Role |
|------|------|------|
| `IModel` | `src/Ashlar.Abstractions/IModel.cs` | Agent-facing completion API |
| `ModelInput` / `ModelOutput` | `src/Ashlar.Abstractions/ModelInput.cs`, `ModelOutput.cs` | Message / completion DTOs |
| `IProviderFactory` | `src/Ashlar.Infrastructure/Execution/IProviderFactory.cs` | Gateway for all provider HTTP / local calls |
| `ProviderFactory` | `src/Ashlar.Infrastructure/Execution/ProviderFactory.cs` | **Central invoker** — OpenAI, Azure, openai_compat, Ollama, local, video, mock |
| `ProviderBackedModel` | `src/Ashlar.Infrastructure/Execution/Models/ProviderBackedModel.cs` | `IModel` → parses `ashlar.model.provider=` / `ashlar.model.name=` → factory |
| `HotSwappableModel` | `src/Ashlar.Infrastructure/Execution/Models/HotSwappableModel.cs` | Runtime swap; respects `ASHLAR_MODEL_PROVIDER` |
| `OrchestrationRuntimeModelDecorator` | `src/Ashlar.Orchestration/Models/OrchestrationRuntimeModelDecorator.cs` | Outer `IModel`; injects orchestration runtime spec |
| `OrchestrationHotSwappableModel` | `src/Ashlar.Orchestration/Models/OrchestrationHotSwappableModel.cs` | Primary/fallback swap (orchestration layer) |
| `AgentScopedModel` | `src/Ashlar.Orchestration/Models/AgentScopedModel.cs` | Per-agent provider/name directives |
| `OllamaProvider` | `src/Ashlar.Infrastructure/Execution/Ollama/OllamaProvider.cs` | Ollama HTTP client (`/api/chat`, health, tags) |
| `LocalModelProvider` | `src/Ashlar.Infrastructure/Execution/LocalModelProvider.cs` | **In-process LLamaSharp GGUF** (`ASHLAR_LOCAL_MODEL_PATH`) — **not ONNX Runtime** |
| `OpenAiCompatibleEndpoint` | `src/Ashlar.Infrastructure/Execution/OpenAiCompatibleEndpoint.cs` | URL normalization for `/v1/chat/completions` |
| `AdaptiveProviderFactory` | `src/Ashlar.Infrastructure/Execution/AdaptiveProviderFactory.cs` | Chooses provider via `ILoadPolicy` |
| `PreferenceLoadPolicy` / `ILoadPolicy` | `src/Ashlar.Infrastructure/Execution/LoadPolicy/` | Local-vs-cloud preference (`ASHLAR_LOAD_PREFERENCE`) |
| `MockScaffoldingResponder` | `src/Ashlar.Infrastructure/Execution/MockScaffoldingResponder.cs` | Deterministic mock/offline responses |
| `OllamaEphemeralLifecycle` | `src/Ashlar.Infrastructure/Execution/Ephemeral/OllamaEphemeralLifecycle.cs` | Ephemeral Docker Ollama per session |

**Important correction vs plan wording:** “ONNX / offline target” in docs/`BackendType.OnnxRuntime` is largely a **placeholder**. Real offline inference is **LLamaSharp + GGUF** via `LocalModelProvider`. Phase 1 should wrap **`local` (LLamaSharp)** as `local:onnx` *only if* product naming requires that key; prefer key **`local:llamasharp`** (or map `local:onnx` → LLamaSharp with a comment) so policy docs stay honest.

### Secondary seam: NCR `IModelServingBackend`

| Type | Path | Role |
|------|------|------|
| `IModelServingBackend` | `src/Ashlar.Core.Application/NodeCapabilityRuntime/Ports/IModelServingBackend.cs` | `RunInferenceAsync`, load/unload/pull |
| `OllamaModelServingBackend` | `src/Ashlar.Infrastructure/NodeCapabilityRuntime/Backends/OllamaModelServingBackend.cs` | Desktop NCR → Ollama HTTP |
| `NullModelServingBackend` | `src/Ashlar.Infrastructure/NodeCapabilityRuntime/Backends/NullModelServingBackend.cs` | No-op; reports `BackendType.OnnxRuntime` |
| `BackendType` | `src/Ashlar.Core.Application/NodeCapabilityRuntime/Models/BackendType.cs` | `Ollama`, `LlamaCppMobile`, `OnnxRuntime` (only Ollama implemented) |
| `NodeCapabilityRuntime` | `src/Ashlar.Infrastructure/NodeCapabilityRuntime/NodeCapabilityRuntime.cs` | Model selection / ensure-ready |
| `NcrAgenticBrickEngine` | `src/Ashlar.Infrastructure/Execution/Agentic/NcrAgenticBrickEngine.cs` | Agentic bricks → NCR lifecycle |

### Capability / job routing (not chat-client routing)

| Type | Path | Role |
|------|------|------|
| `ExecutionTarget` | `src/Ashlar.Core.Application/Execution/Routing/ExecutionTarget.cs` | Local vs remote job target |
| `ICapabilityRouter` / `NcrCapabilityRouter` | Application port + `src/Ashlar.Infrastructure/Execution/Routing/NcrCapabilityRouter.cs` | Local / peer / RunPod |
| `ProviderFactoryLocalExecutor` | `src/Ashlar.Infrastructure/Execution/Routing/ProviderFactoryLocalExecutor.cs` | Local jobs → `ExecuteLLMAsync` |
| `IEndpointRouter` / `CompositeEndpointRouter` | Abstractions + Orchestration | Agent **transport** endpoints (not LLM providers) |

### Direct `IProviderFactory` callers (bypass `IModel`)

| Type | Path |
|------|------|
| `ProviderGeneratorModel` | `src/Ashlar.Infrastructure/Adaptation/Generation/ProviderGeneratorModel.cs` |
| `ProviderCompositionGeneratorModel` | `src/Ashlar.Infrastructure/Certification/Composition/ProviderCompositionGeneratorModel.cs` |
| `ContentGenerator` | `src/Ashlar.Infrastructure/Export/ContentGenerator.cs` |
| `OWASPScannerBrick` | `src/Ashlar.Bricks.Owasp/Security/OWASPScannerBrick.cs` |
| `ProviderFactoryLocalExecutor` | (above) |

### Explicitly absent

| Search | Result |
|--------|--------|
| AWS Bedrock | **Zero** code references |
| `Microsoft.Extensions.AI` / `IChatClient` | **Zero** |
| Anthropic HTTP | Domain enum only; not in `ProviderFactory` |
| Real ONNX Runtime GenAI | Enum/placeholder only |

---

## 2. Sanitization / PII / secret filtering

| Type | Path | Role |
|------|------|------|
| `ICloudSanitizationProxy` / `CloudSanitizationProxy` | `src/Ashlar.BackgroundAgents/Trust/` | Sanitizes outbound prompts before cloud; PII block/redact + taxonomy |
| `SanitizingProviderFactory` | `src/Ashlar.BackgroundAgents/Trust/SanitizingProviderFactory.cs` | **`IProviderFactory` decorator** — runs proxy then delegates |
| `OutgoingContext` / `SanitizationResult` | same folder | Input/result models |
| `SanitizationAuditEntry` (+ DTO) | Trust + `src/Ashlar.Core.Application/Trust/Ports/` | Redaction audit row (counts/categories — no raw secrets by design) |
| `ISanitizationAuditLog` | Trust | `LogRedaction` / `GetRecent` |
| `ISensitiveContentFilter` / `SensitiveContentFilter` | `src/Ashlar.BackgroundAgents/WebSearch/` | Regex email/phone/SSN/API-key/CC; `RedactPii` / `ShouldBlockQuery` |
| `IDataTaxonomy` / `DataTaxonomy` + JSON | `src/Ashlar.BackgroundAgents/DataSensitivity/` | Data-type → sensitivity (e.g. api-keys → Secret) |
| `IDataSensitivityRegistry` / levels | same | Public→TopSecret; drives exfiltration + RAG filters |
| `DataExfiltrationPolicy` | `src/Ashlar.BackgroundAgents/Security/DataExfiltrationPolicy.cs` | Tool-call policy: blocks LLM/search when sensitivity forbids |
| `SupportDiagnosticsExporter` | `application/src/Ashlar.API/Security/` | Redacts sensitive **config** keys (not LLM prompts) |

**Behavior today (CloudSanitizationProxy):** air-gapped → pass-through; else PII detected → **block**; filterable PII → **redact**; taxonomy may further constrain. Not yet policy-pack-driven per destination target (Phase 2 must make this policy-driven: redact / block / pass by target).

**Noise:** `SanitizeXmlName`, `SanitizeIdentifier`, Unity `SanitizeClassName` — unrelated to LLM egress.

---

## 3. Audit sinks

### Barrier audit pipeline

| Type | Path | Role |
|------|------|------|
| `IBarrierAuditSink` / `IBarrierAuditLog` | `src/Ashlar.Abstractions/Barriers/` | Pluggable barrier audit |
| `StructuredBarrierAuditLog` | `src/Ashlar.Runtime/Barriers/` | Fans out to all sinks |
| `FileBarrierAuditSink` | `src/Ashlar.Runtime/Barriers/Sinks/` | `Ashlar:Audit:Sinks` contains `File` |
| `StructuredLogBarrierAuditSink` | same | ILogger sink |
| `NoOpBarrierAuditSink` | same | Default / discard |
| Registration | `src/Ashlar.Runtime/RuntimeServiceCollectionExtensions.cs` → `AddBarrierAuditSinks` | Bound from `Ashlar:Audit:*` |

### Trust / data-decision audit (LLM sanitization lives here)

| Type | Path | Role |
|------|------|------|
| `IDataDecisionAuditLog` | `src/Ashlar.Core.Application/Trust/Ports/` | Unified: sanitization, boundary, classification, etc. |
| `DataDecisionAuditLog` / `LiteDbDataDecisionAuditLog` | `src/Ashlar.BackgroundAgents/Trust/` | In-memory or LiteDB (`ASHLAR_TRUST_AUDIT_DB`) |
| CLI `TrustCommand.AuditAsync` | `application/src/Ashlar.CLI/Commands/TrustCommand.cs` | Export/show audit |

### Related (not model-call audit)

- `IAdaptationAuditLog` / `LiteDbAdaptationAuditLog` — adaptation decisions  
- GameDirector `AuditRecord` / MCP `GetAuditTrailTool` — commercial activity feed  

**Phase 2 implication:** `AuditingChatClient` should write to **`IDataDecisionAuditLog` / sanitization audit** (and optionally emit a barrier correlation id). Do not invent a third audit store; barrier sinks are for barrier lifecycle, not model invocations.

---

## 4. Policy packs — load & evaluate

### Trust policy packs (observation / regulated packs)

| Type | Path | Role |
|------|------|------|
| `ITrustPolicyPackRegistry` / `TrustPolicyPackRegistry` | Ports + `src/Ashlar.Infrastructure/Trust/` | Load `*.json`, activate pack |
| `TrustPolicyPack` (+ info/status/rules models) | `src/Ashlar.Core.Application/Trust/Models/` | Pack schema |
| On-disk packs | `config/trust-packs/{strict-enterprise,internal-only,air-gapped,active-pack}.json` | Pack content + activation |
| `IAccessBoundary` / `AccessBoundary` | Infrastructure Trust | `ApplyPolicyPack`; observation gates |
| `IObservationGate` / `ObservationGate` | same | `ShouldObserve` from active boundary |
| CLI | `TrustCommand` pack list/describe/apply | Operator UX |

**Env:** `ASHLAR_TRUST_POLICY_PACKS_PATH`, `ASHLAR_TRUST_ENABLED`, `ASHLAR_TRUST_AUDIT_DB`.

### Tool-call policy engine (separate from packs)

| Type | Path | Role |
|------|------|------|
| `IPolicy` | `src/Ashlar.Abstractions/IPolicy.cs` | Approve/deny tool calls |
| `PolicyEngine` | `src/Ashlar.Runtime/PolicyEngine.cs` | Evaluate all `IPolicy`, sign deltas |
| `BackgroundAgentPolicyEngineFactory` | `src/Ashlar.BackgroundAgents/Security/` | Builds engine with `DataExfiltrationPolicy` |
| `AllowAllPolicy`, path/sandbox policies | `src/Ashlar.Policies/`, `src/Ashlar.Policies.Dev/` | Built-in tool policies |

**No `PolicyGate` type exists today.** Phase 2 `PolicyGateChatClient` is new; it should consult trust/data-classification → allowed execution targets (extend packs or add a new pack section for target keys — design in Phase 2).

### Trust tiers (mesh / fleet — not RAG trust tags)

| Type | Path | Role |
|------|------|------|
| `PeerTrustTier` | `src/Ashlar.Core.Application/Mesh/Models/` | Unknown/Untrusted/Trusted |
| `MeshTrustPolicyConfiguration` | Mesh | `ASHLAR_MESH_TRUST_POLICY` |
| Fleet trust | commercial Fleet contracts | Placement eligibility |

RAG sensitivity is **`IDataSensitivityRegistry` levels**, not `PeerTrustTier`. Phase 5 “trust-tier tag” maps to sensitivity level names.

---

## 5. RAG / embedding / vector storage

| Type | Path | Role |
|------|------|------|
| `IEmbeddingGenerator` | `src/Ashlar.BackgroundAgents/RAG/IEmbeddingGenerator.cs` | **Ashlar-local** `GenerateAsync → float[]` — **name collision with MEAI** |
| `TokenEmbeddingGenerator` | same | Bag-of-words, dim 64 from `AshlarDefaults`. Deterministic **across processes** since the FNV-1a fix; before it the per-token seed came from a per-process randomized string hash |
| `IVectorStore` / `InMemoryVectorStore` | same | Default DI store |
| `SqliteVectorStore` | same | Implemented + tested; **not** registered in `AddBackgroundAgentsRAG` |
| `IRAGService` / `RAGService` | same | Embed + index/search façade |
| `IKnowledgeBaseIndexer` / `KnowledgeBaseIndexer` | same | File → RAG indexing |
| `RAGTool` | same | Agent tool `rag_search` |
| `RAGConfig` | `src/Ashlar.BackgroundAgents/Configuration/RAGConfig.cs` | Docs mention sqlite/postgres/qdrant; only in-memory wired |
| `DecompositionRetriever` | `src/Ashlar.Orchestration/Architect/` | Keyword “RAG” over examples — **no embeddings** |
| Tests | `src/Ashlar.Tests.BackgroundAgents/RAG/*` | Coverage for stores, embeddings, tool |

**DI:** `AddBackgroundAgentsRAG()` → `TokenEmbeddingGenerator`, `InMemoryVectorStore`, `RAGService`, `KnowledgeBaseIndexer` (kernel Phase 11 when `IncludeBackgroundAgentRag`).

**Phase 5 note:** rename or alias Ashlar’s `IEmbeddingGenerator` when adopting MEAI’s `IEmbeddingGenerator<string, Embedding<float>>` to avoid type collisions (qualify namespaces).

---

## 6. DI registration points (CLI + API)

### Hosts

| Host | Project | Entry |
|------|---------|-------|
| CLI | `application/src/Ashlar.CLI/` | `Program.cs` → `AddAshlarRuntimeRouting` + `AddAshlar()` |
| API | `application/src/Ashlar.API/` | `Program.cs` → same + API-only ingress (SNS/DynamoDB) |

No `Startup.cs`. Feature flags are **Options + env**, not Microsoft.FeatureManagement.

### Shared composition root

| File | Role |
|------|------|
| `src/Ashlar.Hosting/AshlarServiceCollectionExtensions.cs` | `AddAshlar` / `AddAshlarProfile` |
| `src/Ashlar.Hosting/AshlarKernelRegistrar.cs` + `.Phases.cs` | Ordered phases |
| `src/Ashlar.Hosting/ModuleSelection.cs` | Profile gates (`IncludeBackgroundAgentRag`, `IncludeTrustServices`, …) |
| `src/Ashlar.Hosting/AshlarHostingOptions.cs` | `TrustEnabled`, hosted-agent flags, etc. |

### AI-relevant kernel phases

| Phase | What |
|-------|------|
| **11** | `AddBackgroundAgents` + optional `AddBackgroundAgentsRAG` |
| **13** | `HotSwappableModel` + `IModel` = `OrchestrationRuntimeModelDecorator` |
| **14** | Optional `IEphemeralModelLifecycle` → Ollama ephemeral |
| **15** | Trust + `IProviderFactory` 3-way branch (adaptive / sanitizing / plain) |

### Other DI

| Extension | Path |
|-----------|------|
| `AddTrustServices` | `src/Ashlar.BackgroundAgents/ServiceCollectionExtensions.cs` |
| `AddAccessBoundary` | `src/Ashlar.Infrastructure/Trust/Sdk/Extensions/TrustServiceCollectionExtensions.cs` |
| `AddBarrierAuditSinks` | `src/Ashlar.Runtime/RuntimeServiceCollectionExtensions.cs` (`Ashlar:Audit:*`) |
| NCR + Ollama backend | `src/Ashlar.Hosting/AshlarServiceCollectionExtensions.NodeCapabilityRuntime.cs` |

### Planned feature flag

`Ashlar:UseMeaiPipeline` — **does not exist yet**. Follow existing pattern: bind bool from config section + optional env override; default **off** until Phase 6. Suggested env alias: `ASHLAR_USE_MEAI_PIPELINE=1`.

### AWS credentials (for Phase 4 Bedrock reuse)

| Piece | Path | Notes |
|-------|------|-------|
| DynamoDB store | `src/Ashlar.Ingress.DynamoDb/` | `new AmazonDynamoDBClient()` — **default credential/region chain** |
| Options | `src/Ashlar.Contracts/SmsIngressDynamoDbOptions.cs` | Table name only (`Ashlar:SmsIngressDynamoDb`) |
| SNS | `src/Ashlar.Ingress.AwsSns/` | Signature verify only — **no AWS SDK client** |
| Packages | `Directory.Packages.props` | `AWSSDK.DynamoDBv2` 3.7.400, Core/S3/Lambda 3.7.305.12 — **no Bedrock** |

---

## Proposed mapping: existing type → MEAI concept

| Existing type | MEAI concept | Notes |
|---------------|--------------|-------|
| `IModel` / `ProviderBackedModel` | Consumer of `IChatClient` (adapter) | Keep `IModel` until Phase 6; impl can call MEAI when flag on |
| `IProviderFactory` / `ProviderFactory` | Provider `IChatClient`s behind keyed DI | Split per target; do not register raw factory/clients publicly |
| `OllamaProvider` | `IChatClient` via **OllamaSharp** (or thin adapter) | Key: `local:ollama` |
| `LocalModelProvider` (LLamaSharp) | Custom `IChatClient` adapter | Key: `local:onnx` alias or `local:llamasharp` — see §1 |
| `SanitizingProviderFactory` + `ICloudSanitizationProxy` | `SanitizingChatClient : DelegatingChatClient` | Move policy-driven redact/block/pass here |
| `ISensitiveContentFilter` | Used inside `SanitizingChatClient` | Reuse; don’t rewrite filters |
| `IDataDecisionAuditLog` / sanitization audit | `AuditingChatClient : DelegatingChatClient` | Emit counts/categories only |
| `ITrustPolicyPackRegistry` + target allow-list (new) | `PolicyGateChatClient : DelegatingChatClient` | New gate over (caller, target, model) |
| `AdaptiveProviderFactory` / `ILoadPolicy` / `NcrCapabilityRouter` | `RoutingChatClient : IChatClient` | Phase 3; local-first + policy × availability |
| *(none)* Bedrock | `BedrockChatClient : IChatClient` | Phase 4; keys `cloud:bedrock:{fast,balanced,heavy}` |
| Ashlar `IEmbeddingGenerator` / `TokenEmbeddingGenerator` | MEAI `IEmbeddingGenerator<string, Embedding<float>>` | Phase 5; rename Ashlar interface or fully qualify |
| `IVectorStore` / `InMemoryVectorStore` / `SqliteVectorStore` | `VectorStore` / `VectorStoreCollection` (Microsoft.Extensions.VectorData) | Keep old read-only until Phase 6 |
| `RAGService` / `KnowledgeBaseIndexer` | Facades over VectorData + embedding generator | Preserve sensitivity ≤ caller filter |
| Raw `OllamaApiClient` / Bedrock / ONNX session | **Never** resolve from DI | Only decorated `IChatClient` pipeline is public |

### Fixed governance composition (Phase 2)

```
UseAshlarGovernance() →
  PolicyGate → Sanitizing → Auditing → [UseFunctionInvocation()] → provider IChatClient
```

Router (Phase 3) sits **outside** per-target stacks and is itself wrapped in Auditing.

---

## Gaps & risks for later phases

1. **TFM mismatch:** plan = .NET 9; repo libraries/hosts = `net8.0`. `Microsoft.Extensions.*` already at **10.0.8** in CPM — MEAI packages should align carefully.
2. **Dual invocation paths:** `IModel`/`IProviderFactory` and NCR `IModelServingBackend` — decide whether NCR remains parallel or folds into MEAI (recommend: Phase 1 wraps chat path only; NCR later).
3. **Bypass surface:** many direct `IProviderFactory` callers — flag must route them or Phase 6 cleanup will leave holes.
4. **“ONNX” naming** vs LLamaSharp reality — document in policy keys to avoid operator confusion.
5. **Sanitization not per-target today** — Proxy is cloud-oriented; local pass-through must become explicit policy.
6. **Two audit models** — prefer trust data-decision audit for MEAI middleware; barrier sinks for barriers.
7. **Package asks (plan allows these):**
   - Phase 1: `Microsoft.Extensions.AI.Abstractions`, `Microsoft.Extensions.AI`, `OllamaSharp`
   - Phase 4: `AWSSDK.BedrockRuntime` (+ AWS MEAI adapter if available)
   - Phase 5: `Microsoft.Extensions.VectorData.Abstractions` + one concrete store
8. **No Bedrock / no Anthropic** yet; Amazon credentials path is default chain only.
9. **RAG sensitivity ≠ mesh trust tier** — map Phase 5 tags to `IDataSensitivityRegistry`.

---

## Neighbor projects for `src/Ashlar.AI.Pipeline`

| Project | Why |
|---------|-----|
| `Ashlar.Abstractions` | `IModel`, barriers, tools |
| `Ashlar.BackgroundAgents` | Trust sanitization, RAG (reuse; avoid circular refs — prefer ports/interfaces) |
| `Ashlar.Infrastructure` | ProviderFactory, Ollama, LLamaSharp (adapters wrap, don’t rewrite) |
| `Ashlar.Hosting` | Feature-flagged `ChatClientBuilder` registration |
| `Ashlar.Core.Application` | Trust ports, NCR ports |
| `Ashlar.Adapters.Models` | Lightweight model adapters peer |
| Tests: `Ashlar.Tests.BackgroundAgents`, new `Ashlar.Tests.AI.Pipeline` | Unit + composition + architecture tests |

**Suggested dependency direction:** `Ashlar.AI.Pipeline` depends on Abstractions + Application ports + MEAI packages; Infrastructure/BackgroundAgents provide adapters registered from Hosting. Avoid Pipeline → Hosting.

---

## Phase checklist

| Phase | Deliverable | Status |
|-------|-------------|--------|
| 0 | This notes file | **Done** |
| 1 | `Ashlar.AI.Pipeline` + Ollama/LLamaSharp `IChatClient` + flag off | **Done** |
| 2 | PolicyGate / Sanitizing / Auditing middleware + DI architecture tests | **Done** |
| 3 | `RoutingChatClient` + policy × availability matrix tests | **Done** |
| 4 | Bedrock tiered targets + env-gated integration test | **Done** |
| 5 | VectorData RAG + embedding middleware + reindex CLI | **Done** |
| 6 | Flag default on; delete legacy; `docs/governed-pipeline.md` | **Done** |

---

## Phase 1 implementation notes (2026-07-14)

Landing branch: `cursor/meai-phase1-pipeline-5a04`

### Delivered
- New project `src/Ashlar.AI.Pipeline` (TFMs `net8.0;net9.0`) + tests `src/Ashlar.Tests.AI.Pipeline`
- Keyed `IChatClient` targets: `local:ollama` (`OllamaHttpChatClient`), `local:onnx` (`LlamaSharpChatClient`)
- Hosting Phase **13b** registers the pipeline only when `Ashlar:UseMeaiPipeline` / `ASHLAR_USE_MEAI_PIPELINE` / `AshlarHostingOptions.UseMeaiPipeline` is true (**default off**)
- Raw `OllamaHttpChatClient` / `LlamaSharpChatClient` are **not** registered in DI — only keyed `IChatClient` via `AddKeyedChatClient`
- Packages: `Microsoft.Extensions.AI` + `Abstractions` **10.7.0**; CPM bumped related `Microsoft.Extensions.*` / `System.Text.*` **10.0.8 → 10.0.9** for MEAI

### Discovery changes for later phases
1. **OllamaSharp deferred:** package 5.4.25 ships a Roslyn 5 analyzer incompatible with this repo's pinned C# 12 / compiler 4.14. Phase 1 uses a thin `OllamaHttpChatClient` over `/api/chat` instead (plan-allowed). Revisit OllamaSharp when the repo moves to a Roslyn 5-capable toolchain.
2. **`local:onnx` = LLamaSharp GGUF** confirmed in code comments + options; not ONNX Runtime GenAI.
3. Host libraries remain **net8.0**; Pipeline dual-targets so Hosting can consume net8 while still shipping net9.
4. Governance middleware (`UseAshlarGovernance`) is **Phase 2** — Phase 1 registers bare keyed clients through `ChatClientBuilder` with no policy/sanitize/audit stack yet.

---

## Phase 2 implementation notes (2026-07-14)

Landing branch: `cursor/meai-phase2-governance-5a04`

### Delivered
- `UseAshlarGovernance(targetKey)` — fixed order **PolicyGate → Sanitizing → Auditing → provider**
- `PolicyViolationException` with structured `Code` / target / details (no raw secrets)
- Ports: `IChatTargetAccessPolicy`, `IChatMessageSanitizer`, `ITargetSanitizePolicy`, `IChatInvocationAuditor`
- Defaults: local allow / cloud deny; local sanitize=Pass; cloud sanitize=BlockOnSecretRedactOnPii
- `AddAshlarMeaiPipeline` always applies `UseAshlarGovernance` (hosts cannot register ungoverned keyed clients through this API)
- Unit tests: deny short-circuit, PII redact before spy, audit on success/fault/cancel, composition order, architecture (resolved client is `PolicyGateChatClient`)

### Follow-ups for later phases
- Wire adapters to existing `ICloudSanitizationProxy` / `IDataDecisionAuditLog` / trust packs (ports are ready)
- Phase 3 router wraps governed per-target pipelines and audits route decisions

---

## Phase 3 implementation notes (2026-07-14)

Landing branch: `cursor/meai-phase3-router-5a04`

### Delivered
- `RoutingChatClient` + `LocalFirstChatRouter` + `DefaultRouteCandidateTable` + `ITargetAvailability`
- Capability hints via `ChatOptions.AdditionalProperties["ashlar.route.capability"]` (fast/balanced/heavy)
- Local-first escalation; cloud candidates only when policy allow-lists cloud keys
- Router emits audit records (`router:default`) with candidates + reason; default DI wraps router in `AuditingChatClient`
- Table-driven policy × availability matrix tests + scenario tests (cloud forbidden / local down fallback / hard fail)

### Follow-ups
- Phase 4 registers real `cloud:bedrock:*` governed clients; stubs already reserved in the candidate table

---

## Phase 4 implementation notes (2026-07-14)

Landing branch: `cursor/meai-phase4-bedrock-5a04`

### Delivered
- Packages: `AWSSDK.BedrockRuntime` + `AWSSDK.Extensions.Bedrock.MEAI` (AWS MEAI `AsIChatClient`)
- Targets `cloud:bedrock:fast|balanced|heavy` with config model ids (`Ashlar:Meai:Bedrock`)
- Credentials: `new AmazonBedrockRuntimeClient()` — same default chain as DynamoDB ingress; client **not** in DI
- Cloud sanitize defaults already strict (`BlockOnSecretRedactOnPii`); Bedrock enable auto-allow-lists cloud target keys
- Unit tests with fake transport; live test gated by `ASHLAR_TEST_BEDROCK=1`

---

## Phase 5 implementation notes (2026-07-14)

Landing branch: `cursor/meai-phase5-vectordata-5a04`

### Delivered
- Package: `Microsoft.Extensions.VectorData.Abstractions` **10.7.0** + in-process `InProcessVectorStore` / `InProcessChunkCollection` (no preview SK connector — version mismatch with VectorData 10.7)
- `ChunkRecord`, `TrustTierOrder`, `VectorDataRagService` (index / search with caller-tier filter / reindex)
- Governed embeddings: `TokenHashEmbeddingGenerator` → **Auditing** → **Sanitizing** (outer) — same AsyncLocal visibility pattern as chat. The generator buckets tokens by FNV-1a, not `String.GetHashCode()`, so a bucket index means the same thing in every process
- Hosting Phase 13b: VectorData RAG + governance defaults always; chat pipeline still flag-gated
- CLI: `ashlar background-agent rag reindex-meai` (`MeaiRagReindexCommand`) — leaves legacy RAG store read-only
- AWS CPM aligned to v4 so Bedrock MEAI + DynamoDB co-restore: Core `4.0.100.4`, DynamoDBv2 `4.0.101.1`, S3 `4.0.101`, Lambda `4.0.103`

### Follow-ups for Phase 6
- Default `Ashlar:UseMeaiPipeline` on; remove legacy `IProviderFactory` chat path and legacy RAG write path
- Publish `docs/governed-pipeline.md` + architecture tests in CI
- Optional: swap in-process VectorData store for a durable connector when one matches Abstractions 10.7
- **CLI reindex:** deferred to an `application/*` PR (layer-boundary: master cannot change `application/`) — use `VectorDataRagService.ReindexAsync` via Hosting DI until then

---

## Phase 6 implementation notes (2026-07-14)

Landing branch: `cursor/meai-phase6-cutover-5a04`

### Delivered
- Feature flag **defaults ON**; opt out with `Ashlar:UseMeaiPipeline=false` / `ASHLAR_USE_MEAI_PIPELINE=0`
- `MeaiBackedModel` (`IModel` → governed `IChatClient`); Hosting Phase 13 uses it as the HotSwappable agentic leaf when MEAI is on
- `HotSwappableModel` accepts `IModel` (not `ProviderBackedModel` only)
- Default `IRAGService` → `MeaiVectorDataRagAdapter` over `VectorDataRagService`
- Operator doc: `docs/governed-pipeline.md`
- CI: `make meai-pipeline-gate` hooked into `make kernel-gate`
- Pack graph: `Ashlar.AI.Pipeline` added to `pack-ashlar-hosting-graph.{sh,ps1}`
- ProdStyle Hosting smoke: default MEAI + VectorData RAG wiring / opt-out

### Soft-gated (kept)
- `IProviderFactory` / `ProviderFactory` / `SanitizingProviderFactory` for direct non-chat callers (NCR, bricks, content generators)
- Legacy `RAGService` types remain for opt-out/custom hosts

### Remaining debt
- Migrate remaining direct `IProviderFactory.ExecuteLLMAsync` callers onto MEAI or shared policy helpers
- Durable VectorData connector when GA versions align
- Application-layer CLI: `ashlar background-agent rag reindex-meai` (blocked on master by layer-boundary; land on `application/*`)
