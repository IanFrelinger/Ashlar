# Learning Harness: Autonomous Experience → Verified Adaptation

**Status:** Design sketch (long-term roadmap — not yet implemented)  
**Date:** 2026-09-19  
**Milestone:** M7 (post-M6)

---

## Principle

**Agents can learn freely. They cannot trust their own learning freely.**

This distinction is the foundation of the Learning Harness architecture. Learning — extracting patterns from experience, forming hypotheses, proposing adaptations — is cheap, safe, and happens continuously. Trust — promoting those adaptations into production scope — is expensive, gated, and happens only after evidence meets the promotion bar.

---

## What "Learn" Means

In Ashlar, **learning** is NOT:
- Neural network weight updates
- Gradient descent or backpropagation
- Training loops that modify model parameters

In Ashlar, **learning** IS:
- Experience → Hypothesis: "When tasks shaped like X fail with Y, pattern Z is present"
- Hypothesis → Experiment: "Try adaptation A on tasks matching profile B"
- Experiment → Evidence: "A succeeded in N/M trials; confidence score C"
- Evidence → Promotion: Pass Ashlar certification gates → activate under policy

Learning is structural, explicit, and auditable. Every learned rule carries its derivation chain: which experiences it came from, what experiments validated it, and which gates approved its promotion.

---

## Four-Loop Harness Diagram

```
┌─────────────────┐
│  Experience     │  Every task run: objective, context, plan, tools used,
│  Record         │  output, success/failure, validator results, cost, duration
└────────┬────────┘
         │
         ▼
┌─────────────────┐
│  Reflect        │  Analyze experience windows: detect patterns, form hypotheses,
│  (Pattern       │  propose adaptations (strategy change, new tool, routing rule)
│   Detection)    │
└────────┬────────┘
         │
         ▼
┌─────────────────┐
│  Propose        │  Package adaptation as a learning candidate:
│  Adaptation     │  scope, confidence, evidence chain, risk tier
└────────┬────────┘
         │
         ▼
┌─────────────────┐
│  Ashlar Gates   │  Certification gate (witness, mutation, determinism)
│  (Evaluate)     │  + Tier-based admission (Tier 0 auto, Tier 1 hold, Tier 2 human)
│                 │  + Replay validation (strategy candidates run against test corpus)
└────────┬────────┘
         │
         ├─ PASS ──▶ ┌────────────────┐
         │           │  Promote        │  Install under scope restrictions:
         │           │  (Activate)     │  - canary rollout
         │           │                 │  - watch window
         │           │                 │  - auto-rollback on breach
         │           └────────────────┘
         │
         └─ FAIL ──▶ ┌────────────────┐
                     │  Store Failure  │  Log rejection reason, fence catalog update,
                     │  (Quarantine)   │  quarantine hash (never re-propose bit-identical)
                     └────────────────┘
```

---

## Learning Ladder

The **learning ladder** orders adaptations by blast radius and required promotion bar. Higher rungs touch more authority perimeter and require stronger evidence + human gates.

| Rung | Adaptation Type | Risk | Promotion Bar | Authority Change |
|------|----------------|------|---------------|------------------|
| **1** | Episodic memory | Lowest | Automatic (no gate) | None — recall only |
| **2** | Semantic memory | Very Low | Automatic (hash check) | None — knowledge extraction |
| **3** | Strategy (heuristic) | Low | Replay gate + Tier 0 cert | Task planning only |
| **4** | Routing rule | Low | Replay gate + Tier 0 cert | Execution path selection |
| **5** | Prompt policy | Medium | Replay gate + Tier 1 (human admit) | Agent instruction template |
| **6** | Workflow template | Medium | Replay gate + Tier 1 + multi-task corpus | Orchestration structure |
| **7** | New tool (external) | Medium-High | Full cert + Tier 1 + capability policy check | Expands capability surface |
| **8** | New brick/code | High | Full cert + Tier 1 + canary + adversarial tests | Code generation + admission |
| **9** | Trust-kernel change | Highest | Tier 2 (human objective required) | Changes gates themselves |

**Autonomy ceiling:** Rungs 1–4 can be autonomous (Tier 0). Rungs 5–8 require human admission (Tier 1). Rung 9 requires human-authored objective (Tier 2). Authority is never autonomously learnable.

---

## Separation of Concerns

The Learning Harness distinguishes four orthogonal dimensions:

| Dimension | Definition | Promotion Cost | Blast Radius |
|-----------|-----------|----------------|--------------|
| **Knowledge** | Facts, patterns, task-context associations (episodic/semantic memory) | Cheap (hash-verified storage) | Narrow (recall only) |
| **Strategies** | Task-planning heuristics, routing rules, retry policies | Low (replay validation) | Medium (task execution) |
| **Capabilities** | New tools, bricks, external API integrations | High (full certification + canary) | High (expands surface) |
| **Authority** | Gate rules, tier boundaries, admission policies | Highest (human objective required) | Critical (trust kernel) |

**Cost and frequency:** Knowledge and strategies are cheap to update and change often. Capabilities are expensive and change rarely. Authority updates are human-gated and versioned with the trust kernel.

---

## Core Port Sketch

```csharp
public interface ILearningHarness
{
    /// <summary>
    /// Record a completed task execution as an experience entry.
    /// Captures: objective, context, plan, tools, output, success, cost, duration.
    /// Failures are first-class and carry validator results.
    /// </summary>
    Task RecordExperienceAsync(AgentExperience experience, CancellationToken ct = default);

    /// <summary>
    /// Retrieve learned context (rules, patterns, prior experiences) relevant to a task.
    /// Used by agents during planning: "What have I learned about tasks like this?"
    /// </summary>
    Task<IReadOnlyList<LearnedContext>> RecallAsync(TaskContext task, CancellationToken ct = default);

    /// <summary>
    /// Analyze a window of experiences and propose learning candidates.
    /// Reflection window may be: last N tasks, tasks in time range, tasks matching filter.
    /// Returns: pattern hypotheses, strategy adaptations, capability proposals.
    /// </summary>
    Task<IReadOnlyList<LearningCandidate>> ReflectAsync(
        ReflectionWindow window, 
        CancellationToken ct = default);

    /// <summary>
    /// Evaluate a learning candidate: run replay tests, check tier placement,
    /// estimate blast radius, compute confidence from evidence chain.
    /// </summary>
    Task<EvaluationResult> EvaluateAsync(
        LearningCandidate candidate, 
        CancellationToken ct = default);

    /// <summary>
    /// Promote a candidate that passed evaluation: install under policy restrictions,
    /// begin canary rollout, activate watch window, set up auto-rollback.
    /// Returns: promotion record with canary plan and rollback trigger.
    /// </summary>
    Task<PromotionResult> PromoteAsync(
        LearningCandidate candidate, 
        CancellationToken ct = default);
}
```

---

## AgentExperience Schema

Every task execution is recorded as an `AgentExperience` with these fields:

```csharp
public sealed record AgentExperience
{
    public required Guid ExperienceId { get; init; }
    public required Guid TaskId { get; init; }
    public required string AgentId { get; init; }
    public required string AgentVersion { get; init; }

    public required string Objective { get; init; }
    public required string ContextHash { get; init; }  // SHA-256 of context snapshot

    public required IReadOnlyList<string> ToolsAvailable { get; init; }
    public required IReadOnlyList<string> ToolsUsed { get; init; }

    public required TaskPlan Plan { get; init; }  // Serialized plan structure
    public required TaskOutput Output { get; init; }

    public required bool Success { get; init; }
    public required IReadOnlyList<ValidatorResult> ValidatorResults { get; init; }
    public string? UserFeedback { get; init; }

    public required TimeSpan Duration { get; init; }
    public required long TokenCost { get; init; }
    public required long ToolCost { get; init; }  // API calls, etc.

    public required string PolicyVersion { get; init; }
    public required string EnvironmentVersion { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
}
```

**Failures are first-class:** A failed task is a complete experience record. Failure patterns are the primary source of useful learning signals (what contexts predict failure, what adaptations reduce failure rate).

---

## LearnedRule Schema

Every promoted rule must carry its evidence chain:

```csharp
public sealed record LearnedRule
{
    public required Guid RuleId { get; init; }
    public required string RuleType { get; init; }  // "Strategy" | "Routing" | "Tool" | ...
    public required string Scope { get; init; }     // Task filter: which contexts this applies to

    public required IReadOnlyList<Guid> DerivedFromExperiences { get; init; }
    public required DateTimeOffset LearnedAt { get; init; }

    public required double SuccessRateBefore { get; init; }
    public required double SuccessRateAfter { get; init; }
    public required int TrialCount { get; init; }
    public required double Confidence { get; init; }  // 0.0–1.0

    public DateTimeOffset? ExpiresAt { get; init; }  // Optional: time-boxed rules
    public required string PromotionGateVersion { get; init; }

    public required string EvidenceChain { get; init; }  // Serialized derivation: experiences → hypothesis → experiments → gate verdicts
}
```

**Lineage is mandatory:** No rule is promoted without a documented trail from raw experiences through evaluation to admission. This enables:
- Tracing a rule back to the experiences that justified it
- Revoking a rule when its source experiences are invalidated
- Re-evaluating rules when gate versions change

---

## Replay as Promotion Gate

**Strategy and routing candidates** (ladder rungs 3–4) must pass replay validation before promotion:

1. **Build test corpus:** Select N prior successful tasks matching the candidate's scope
2. **Replay with candidate:** Re-run those tasks using the proposed adaptation
3. **Compare outcomes:** Success rate, cost, duration vs. baseline
4. **Verdict:** Promote if: `success_rate >= baseline` AND `cost_increase <= threshold`

Replay is fast (no external API calls if mocked) and cheap (no human in loop). It prevents regressions: a strategy that would have broken prior successful tasks is rejected before reaching production.

---

## Unifying Existing Pieces

Ashlar already has components that belong in the Learning Harness. The M7 milestone unifies them under a single subsystem:

| Existing Component | Learning Harness Role |
|-------------------|----------------------|
| Task execution records | `IExperienceStore` (subtype of existing stores) |
| Failure pattern detector | `IReflector` (pattern detection step) |
| Self-improver / self-context | `ReflectAsync` / `RecallAsync` implementations |
| Test failure → adaptation proposals | `LearningCandidate` generation from test results |
| Certification gate | `EvaluateAsync` gate leg |
| Tier-based admission | `PromoteAsync` admission logic |
| Canary rollout + watch window | `PromotionResult` orchestration |
| Quarantine / rollback | `PromoteAsync` failure path |
| Federation (`.ashpkg` signing) | Distributed learning: signed rule packages |

**New subsystem boundary:** `Ashlar.Learning` namespace with ports in core, adapters at edges, DIP (Dependency Inversion Principle) throughout. No Forge UI code in the Learning Harness kernel — product adapters consume the ports.

---

## Self-Extension: Highest Rung

**Self-extension** (generating a new brick or tool) is the highest learnable rung (ladder rung 8):

1. Agent reflects: "Tasks matching profile X consistently fail; no tool exists for capability Y"
2. Agent proposes: New brick implementing interface Z
3. Harness evaluates:
   - Full certification gate (witness, mutation, determinism, analyzer)
   - Tier 1 admission (human reviews proposal)
   - Capability policy check (does this expand surface safely?)
4. On admit: Canary rollout
   - Install brick for 10% of matching tasks
   - Watch window: 50 invocations or 7 days
   - Auto-rollback on: error rate > 5%, latency breach, capability violation
5. On canary success: Promote to general availability

Self-extension is **not special-cased** — it follows the same four-loop harness as any other adaptation, just with the highest promotion bar.

---

## Fleet Learning (Distributed)

The Learning Harness supports **federated learning** without requiring universal trust:

1. **Node A learns a strategy** (rungs 3–4) and promotes it locally
2. **Node A packages the rule** as a signed `.ashpkg` with evidence chain:
   - `LearnedRule` record (includes `DerivedFromExperiences` references)
   - Anonymized experience summaries (no PII, no proprietary context)
   - Promotion gate verdict (certificate + tier + timestamp)
3. **Node A publishes to fleet catalog** (federation, per `docs/Federation.md`)
4. **Node B discovers the package** via peer sync or LAN multicast
5. **Node B re-verifies under local policy:**
   - Check signature (trust Node A's signing key?)
   - Check evidence chain (sufficient trials? acceptable confidence?)
   - Check compatibility (same policy version? same environment constraints?)
6. **If Node B accepts:** Install rule under canary (even if Node A flew it successfully, Node B's environment may differ)

**Distributed learning without universal trust:** Each node decides independently whether to accept a peer's learned rule. Signing proves provenance; local gates enforce safety.

---

## Commercial Positioning (HOLD until Dogfood Unlock)

**Marketing one-liner (when unlocked):**

> "A runtime where AI agents improve from experience without gaining uncontrolled authority."

**Unlock criteria (from `docs/dogfood-scorecard.md`):**

- Last-7-days green rate ≥ 90% (Strict mode, Ed25519 signing)
- Consecutive-days-hold counter ≥ 7
- Mean-time-to-admit ≤ threshold (TBD)
- Dated Strict production evidence in `docs/dogfood-ledger.md`

**Until unlock:** Learning Harness is roadmap/design only. Do not claim "autonomous learning in production" until the thresholds are met and continuous dogfood proof is operational.

---

## Honesty Gate

**Autonomy claims HOLD** until:

1. **Dogfood scorecard unlocked** (see `docs/dogfood-scorecard.md`)
2. **Dated Strict production runs** in public ledger (see `docs/dogfood-ledger.md`)
3. **M1–M6 complete** (see `docs/audits/2026-09-completion-roadmap.md`)
4. **Adversarial validation pack green** (16/16 tests, per M2)

The Learning Harness design exists to prove the path is known. The honesty gate exists to prove the path is flown.

---

## Architectural Constraints

**Ports in core, adapters at edges:**

- `ILearningHarness`, `IExperienceStore`, `IReflector` are ports (interfaces in `Ashlar.Core.Contracts`)
- SQLite/Postgres adapters, Ollama/OpenAI reflection backends, Forge UI are edge adapters
- DIP: Core never references edge infrastructure

**No Forge UI in kernel:**

- Learning Harness lives in `src/Ashlar.Learning.*` (framework tier)
- Forge consumes `ILearningHarness` via DI (product tier)
- Dashboard, visualizations, approval workflows: product concerns, not kernel concerns

**Testability:**

- Experience replay: replay gate must be fast (mock tool calls, no network)
- Reflection: must run in CI (no human, no external API keys)
- Promotion: canary rollout testable via time-acceleration (not wall-clock dependent)

---

## Related Documents

- `docs/audits/2026-09-completion-roadmap.md` — M1–M6 path to autonomy claims; M7 (this doc) is post-M6
- `docs/certification-evidence.md` — Current certification proof ledger (what's live today)
- `docs/dogfood-scorecard.md` — Autonomy marketing unlock thresholds
- `docs/dogfood-ledger.md` — Dated production evidence (autonomy HOLD until green)
- `docs/SELF-EXTEND-AUDIT.md` — Self-extension safety invariants (enforced today)
- `docs/Federation.md` — Signed package distribution (used for fleet learning)
- `docs/trust-loop/ashlar-trust-loop-spec.md` — Trust-loop normative spec (gates, tiers, admission)
- `docs/RunningASelfExtendingNode.md` — Operator guide (dials, canary, rollback)

---

## Implementation Notes (M7 Roadmap Sketch)

**Not a schedule** — just the known subsystem pieces:

1. **Experience store** (adapt existing task ledger)
2. **Reflector port + first adapter** (pattern detection from experience windows)
3. **Replay gate** (test corpus selection + re-run orchestration)
4. **Learning candidate schema** (standardize proposal format)
5. **Promotion orchestrator** (tier check → cert gate → canary → watch → rollback)
6. **Ladder enforcer** (mapping candidate type → required tier + gates)
7. **Evidence chain serializer** (JSON schema for `LearnedRule` provenance)
8. **Fleet sync adapter** (federated rule discovery + local re-verification)

**Exit criteria for M7:**

- [ ] Design doc accepted (this document)
- [ ] `ILearningHarness` port in `Ashlar.Core.Contracts`
- [ ] `IExperienceStore` implemented (SQLite adapter minimum)
- [ ] `IReflector` port + one concrete implementation (pattern detector)
- [ ] Replay gate proven in CI (strategy candidate → test corpus → verdict)
- [ ] Learning ladder enforced (candidate type → tier mapping)
- [ ] Linked from `docs/DocsIndex.md` (under Trust loop or Additional Material)

**Dependencies:**

- M1 (disarm/cert honesty) — canary/rollback must be proven before learning relies on it
- Dogfood continuous proof operational — autonomy HOLD until scorecard unlocked
- M6 complete — no learning-based autonomy claims until M1–M6 closed

---

**END OF DESIGN SKETCH**
