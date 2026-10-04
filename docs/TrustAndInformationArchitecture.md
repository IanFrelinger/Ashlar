# Ashlar Trust & Information Architecture — Implementation Spec (Refactored)

This spec refactors the Trust & Information Architecture to **maximize reuse** of existing Ashlar systems and **minimize overlap**. It extends rather than replaces current components.

---

## Principles

- **Default restrictive**: no data reaches cloud unless user opts in
- **Safe failure**: if classification or sanitization is uncertain, block — never silently allow
- **Explainable**: every data decision is auditable
- **Offline-first**: all core functionality works without cloud; `IExecutionContext.IsAirGapped` is respected
- **Inheritance**: inferred facts inherit the most restrictive classification of their sources

---

## 1. Information Classification — Extend Existing

### Reuse

- **`IDataSensitivityRegistry`** — keep as-is for agent-level policy
- **`IDataSensitivityLevel`** — map Trust tiers onto existing levels:
  - `local-only` → **TopSecret** (RequiresLocalOnly = true)
  - `cloud-safe` → **Public** or **Internal**
  - `restricted` → **Confidential** or **Secret** (AllowsExternalLLM = false)
- **`DataSensitivityLevels`** — no changes; use for classification output

**Bridge to security labels (SPEC-007, outside this spec).** `DataSensitivityLabelBridge` maps a level onto an `Ashlar.Abstractions.Security.SecurityLabel` by `SensitivityValue` alone, level only (the flags `AllowsExternalLLM`, `AllowsWebSearch`, `RequiresLocalOnly` and `AllowsNetworkExports` are not carried): `ToDataLabel` for data (no level → `SystemHigh`), `TryToClearance` for an agent's clearance (no level → `Public`). `TrustTierOrder.RecordLabel` / `CallerLabel` do the same for the RAG tier names. Nothing decides through either yet: `IDataSensitivityRegistry.CanAccess` and `TrustTierOrder.IsAllowed` still decide, so everything in this section is unchanged by it. The mapping rules are listed in `ci/cert-gate-assertions.md`.

### New: Data Taxonomy (Only Addition)

Add a **data taxonomy** that assigns a default sensitivity level *name* to each observed data type. This is a mapping layer, not a replacement for `IDataSensitivityRegistry`.

**Interface: `IDataTaxonomy`**
```csharp
/// Maps observed data types to default IDataSensitivityLevel names.
/// Used by the sanitization layer to classify unstructured context.
string? GetDefaultLevelForDataType(string dataType);
```

**Default taxonomy (config file, user-editable):**

| Data Type              | Default Level   | Notes                                  |
|------------------------|-----------------|----------------------------------------|
| editor-events          | Public          | Abstracted; no file paths              |
| file-paths             | TopSecret       | Never cloud                            |
| file-contents          | TopSecret       | Never cloud                            |
| git-metadata           | Public          | Commit messages → TopSecret            |
| inferred-preferences   | (inherits)      | Use source level                       |
| api-keys / tokens      | Secret          | PII/sensitive                          |
| terminal-output        | TopSecret       | May contain secrets                    |
| process-names          | Public          | Low risk                               |
| behavioral-patterns    | TopSecret       | Local-only                             |
| user-declared-context  | (user-specified)| At declaration time                    |

**Implementation:** `DataTaxonomy` reads from `DataTaxonomy.json` (versioned). Falls back to TopSecret for unknown types. Uses `IDataSensitivityRegistry.GetByName` to resolve level names.

**Integration:** Classification engine calls `IDataTaxonomy` first (by data type), then `ISensitiveContentFilter.ShouldBlockQuery` for PII-in-content check. If PII detected → treat as Secret regardless of taxonomy.

---

## 2. Cloud Sanitization — Extend, Don’t Replace

### Reuse

- **`ISensitiveContentFilter`** — use for PII detection and redaction (email, phone, SSN). Already used by web search; extend usage to LLM prompt sanitization.
- **`IProviderFactory`** — keep unchanged. Sanitization lives in a **wrapper/decorator** that implements `IProviderFactory` and delegates to the real factory after sanitizing.

### New: Sanitization Proxy (Thin Layer)

**Interface: `ICloudSanitizationProxy`**
```csharp
/// Sanitizes outgoing LLM/vision context before delegation to IProviderFactory.
/// Blocks if classification uncertain. Logs all redactions.
SanitizationResult SanitizeForCloud(OutgoingContext context, CancellationToken ct = default);
```

**Flow:**
1. `SanitizingProviderFactory` (new) wraps `IProviderFactory`
2. Before each `ExecuteLLMAsync` / `ExecuteVisionAsync` call, build `OutgoingContext` from prompt + variables
3. Call `ICloudSanitizationProxy.SanitizeForCloud(context)`
4. If blocked → throw or return error; do not call inner factory
5. If allowed → replace context with sanitized version, then call inner factory
6. Use `ISensitiveContentFilter.FilterQuery` on prompt text for PII redaction
7. Use `IDataTaxonomy` + field heuristics to strip/replace `TopSecret` and `Secret` fields

**Interface: `ISanitizationAuditLog`**
```csharp
void LogRedaction(DateTimeOffset timestamp, string ruleVersion, string fieldOrType, string disposition, string? reason);
IReadOnlyList<SanitizationAuditEntry> GetRecent(int maxCount, DateTimeOffset? since = null);
```

**Reuse `ISensitiveContentFilter`** as the primary PII rule provider. Optionally add `ISanitizationRuleProvider` if we need versioned rules beyond the filter; otherwise use filter + taxonomy as the rule set.

---

## 3. Exfiltration Prevention — Keep As-Is

### Reuse (No Changes)

- **`DataExfiltrationPolicy`** — continues to block tool calls (external LLM, web search, network export) based on agent config
- **`ExfiltrationPolicy`** — BlockExternalLLMs, BlockWebSearch, RequireLocalOnly, MaxAllowedLevel
- **`BackgroundAgentSpecBuilder`** — injects sensitivity restrictions into system prompt

**Layering:**
1. **Policy layer (existing):** Agent’s `ExfiltrationPolicy` → block tool call entirely if not allowed
2. **Sanitization layer (new):** If tool call is allowed → sanitize content before sending

No overlap: policy controls *whether*; sanitization controls *what*.

---

## 4. Auditable Knowledge Log — New Store, Compatible with Sync

### Reuse

- **`IKnowledgeChunkStore`** — keep for cross-node sync (KnowledgeSync API). Different purpose.
- **`IKnowledgeSyncService`** — unchanged.
- **`AgentMemory`** — keep for in-memory execution/feedback learning. No provenance; different use case.

### New: User-Facing Knowledge Log

**Interface: `IUserKnowledgeLogStore`** (distinct from `IKnowledgeChunkStore`)

Purpose: Transparent, user-editable log of what Ashlar has learned about the user (preferences, patterns, workflow habits). Not for sync; for trust and audit.

- **Storage:** LiteDB (query, versioning, relations)
- **Schema:** Entries with Id, DataType, Content, SourceObservationIds (provenance), Version, CreatedAt, UpdatedAt, DeletedAt
- **Provenance:** Each entry references source observation IDs; full chain traversable
- **Versioning:** Updates create new versions; history retained
- **Retention:** User-controlled; no auto-deletion
- **Export:** JSON and Markdown on demand, including provenance

**No replacement** of `IKnowledgeChunkStore` or `AgentMemory`. This is an additional store for user-facing "what Ashlar knows about me."

---

## 5. User-Controlled Access Boundary — New

### Reuse

None; this is fully new.

**Interface: `IAccessBoundary`**
```csharp
bool IsObservationPaused { get; }
bool IsCategoryAllowed(string category);
bool IsSourceAllowed(string sourceId);
bool IsSourceAllowedForProject(string sourceId, string? projectPath);
void SetPause(bool paused);
void SetCategoryAllowed(string category, bool allowed);
void SetSourceAllowed(string sourceId, bool allowed);
void SetProjectOverride(string projectPath, IReadOnlyDictionary<string, bool>? overrides);
event Action<BoundaryChangeEvent>? BoundaryChanged;
```

**Interface: `IObservationGate`**
```csharp
/// Called by observation pipeline before storing or processing any data.
/// Returns false if data should be dropped (category/source disabled or paused).
bool ShouldObserve(string category, string sourceId, string? projectPath = null);
```

**Granularity:**
- **Category:** file-paths, edit-history, terminal-output, git-metadata, etc.
- **Per-source:** e.g. "VS Code", "git", "shell"
- **Per-project:** Override for a project path; e.g. "observe nothing" for `~/work/secret-project`

**Pause:** `IsObservationPaused` → immediately halt all collection. No rule changes. Pause/resume logged.

**Integration:** Observation pipelines call `IObservationGate.ShouldObserve` before persisting or processing. `IAccessBoundary` is the backing store (in-memory or persisted config).

---

## 6. Air-Gap — Use Existing

### Reuse

- **`IExecutionContext.IsAirGapped`** — already used by BehaviorExecutor, UnderstandingBrick, ProviderFactory, etc.
- **Provider selection** — mock/offline/echo when air-gapped
- **`--airgap` CLI flags** — already present

### Implemented: `ICloudAvailabilityResolver`

`ICloudAvailabilityResolver` (`Ashlar.Core.Application`) and `CloudAvailabilityResolver` (`Ashlar.Infrastructure`) resolve air-gap status at runtime:
- Sources (priority order): env var → config file → network probe
- Used at startup and optionally before cloud calls to refresh
- When cloud unavailable, inject/ensure `IsAirGapped = true` in context

**Sanitization when air-gapped:** `SanitizingProviderFactory` still runs classification and audit logging locally. It never dispatches when `IsAirGapped` is true (inner factory uses mock/offline). So the sanitization layer functions for audit even when cloud is off.

---

## 7. Audit of Data Decisions — New Log

### Reuse

- **`ExecutionEvents`** — keep for execution lifecycle (BehaviorStarted, StepCompleted, etc.)
- **`IBackgroundAgentLogStore`** — keep for agent execution logs

### New: Data Decision Audit

**Interface: `IDataDecisionAuditLog`**
```csharp
void LogSanitization(SanitizationAuditEntry entry);
void LogBoundaryChange(BoundaryChangeEvent evt);
void LogClassification(string dataType, string levelName, string? reason);
IReadOnlyList<DataDecisionAuditEntry> GetRecent(int maxCount, DateTimeOffset? since = null);
```

**Content:**
- Sanitization: rule version, field/type, disposition (stripped/blocked/allowed), timestamp
- Boundary: category/source/project, previous state, new state, timestamp
- Classification: data type, assigned level, optional reason

**Format:** Structured (JSON) for tooling; human-readable (Markdown) for user display. Can be backed by the same store with different export views.

---

## Delivery Phases (Revised)

The sections above define the target architecture. The phase list below reflects implementation status in the current codebase.

### Phase 1 — Classification + Sanitization (Extend Existing) — Implemented
- Implemented `IDataTaxonomy` and `DataTaxonomy` with config file support
- Implemented `SanitizingProviderFactory` wrapping `IProviderFactory`
- Implemented prompt PII filtering via `ISensitiveContentFilter`
- Implemented `ICloudSanitizationProxy` and `ISanitizationAuditLog`
- Implemented DI registration for Trust-enabled wrapper wiring

### Phase 2 — Knowledge Log with Provenance — Implemented
- Implemented `IUserKnowledgeLogStore` (LiteDB impl: `LiteDbUserKnowledgeLogStore`)
- Implemented schema with entries, provenance (`SourceObservationIds`), and versioning
- Implemented JSON/Markdown export (`ExportToJsonAsync`, `ExportToMarkdownAsync`)
- Preserved `IKnowledgeChunkStore` and `IKnowledgeSyncService` unchanged

### Phase 3 — Access Boundary + Observation Gate — Implemented
- Implemented `IAccessBoundary` and `IObservationGate` (`AccessBoundary`, `ObservationGate`)
- Implemented boundary config persistence (JSON/SQLite)
- Integrated checks into observation pipelines (`KnowledgeBaseIndexer`, `ObservationPipelineService`)
- Implemented pause controls and per-project overrides (`TrustCommand` pause/resume/allow/deny/boundary)

### Phase 4 — Audit Dashboard + Compliance — Implemented
- Implemented `IDataDecisionAuditLog` support (or equivalent `ISanitizationAuditLog` extension)
- Unified sanitization, boundary, and classification events
- Implemented compliance export (structured JSON, Markdown, CSV via `TrustCommand.AuditAsync`)
- Implemented persistent boundary indicator and audit view (`TrustCommand.DashboardAsync`, `BoundaryAsync`)

### Phase 5 — Regulated trust policy packs — Implemented
- Added versioned trust policy pack schema (`TrustPolicyPack`) and activation status model.
- Added pack registry + activation persistence (`ITrustPolicyPackRegistry`, `TrustPolicyPackRegistry`).
- Added one-step operator workflows:
  - `ashlar trust pack list`
  - `ashlar trust pack apply --id <pack-id>`
- Added default policy packs under `config/trust-packs/`:
  - `strict-enterprise`
  - `internal-only`
  - `air-gapped`
- Exposed active pack/version in trust status surfaces (`trust boundary/dashboard`, API `GET /api/trust/status`).

---

## Dependency Summary

| New Component              | Depends On (Existing)                 |
|---------------------------|----------------------------------------|
| IDataTaxonomy             | IDataSensitivityRegistry               |
| SanitizingProviderFactory  | IProviderFactory, ISensitiveContentFilter |
| ICloudSanitizationProxy   | IDataTaxonomy, ISensitiveContentFilter  |
| IUserKnowledgeLogStore    | (none)                                 |
| IAccessBoundary           | (none)                                 |
| IObservationGate         | IAccessBoundary                        |
| IDataDecisionAuditLog    | (none)                                 |
| ICloudAvailabilityResolver | (optional; env/config)               |

---

## What Stays Unchanged

- `IDataSensitivityRegistry`, `IDataSensitivityLevel`, `DataSensitivityLevels`
- `IDataSensitivityRegistry`-backed RAG sensitivity filtering
- `DataExfiltrationPolicy`, `ExfiltrationPolicy`
- `ISensitiveContentFilter`, `SensitiveContentFilter`
- `IKnowledgeChunkStore`, `IKnowledgeSyncService`, `KnowledgeChunk`
- `AgentMemory`
- `IExecutionContext`, `IsAirGapped`
- `ProviderFactory` (used internally by wrapper)
- Execution events and agent log stores

---

## Testing

Trust tests run in **Ashlar.Tests.Infrastructure** (AccessBoundary, ObservationGate, UserKnowledgeLogStore) and **Ashlar.Tests.BackgroundAgents** (DataTaxonomy, CloudSanitizationProxy, DataDecisionAuditLog, SanitizingProviderFactory).

### Local

```bash
# Infrastructure Trust tests only
dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --filter "FullyQualifiedName~Trust"

# BackgroundAgents Trust tests only
dotnet test src/Ashlar.Tests.BackgroundAgents/Ashlar.Tests.BackgroundAgents.csproj --filter "FullyQualifiedName~Trust"
```

**Note:** The durable store uses LiteDB (pure managed C#), which runs on all platforms including macOS.

### Multi-environment (Docker)

```bash
# Trust suite across Ubuntu, Alpine, Debian, and Unity
ashlar test multi-env --suite trust --all

# Single environment
ashlar test multi-env --suite trust --env ubuntu-8.0
ashlar test multi-env --suite trust --env unity-8.0
```

### Portable scope

```bash
ashlar test portable --scope trust
```

### CI

- **Cross-Platform Tests** workflow: select scope `trust` or `full` to run Trust tests on Ubuntu, macOS, and Windows.
- **Trust Tests (Multi-Env Docker)** workflow: runs automatically on Trust-related path changes; tests Ubuntu, Alpine, and Debian containers.
