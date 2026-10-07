# Phase 0 — Mesh north star (executed)

This document closes **Phase 0** of the federated mesh plan: a single primary workload, a **capability matrix** by deployment profile, an explicit **trust boundary**, and **measurable SLOs**. It is the contract for Phases 1–7 (control plane, transport, distributed execution, knowledge sync, elasticity, preemption, edge alignment).

**Status:** Adopted for integrator projects that compose Ashlar nodes across hosts (see also [NorthStarGapAnalysis.md](NorthStarGapAnalysis.md) for product-wide north star, and [runtime/ExecutionRouting.md](runtime/ExecutionRouting.md) for NCR generation routing).

---

## 1. Primary north star (chosen)

**North Star A — Federated bricks and traceable cross-node execution**

- **User-visible outcome:** An operator runs **Ashlar.API** (or equivalent host) on **multiple machines**; each node **publishes** its brick catalog (`GET /api/bricks`) and can **execute** bricks on behalf of a trusted peer (`POST /api/bricks/{id}/execute`). Consumers resolve bricks via **`BrickHost:RemoteCatalogBaseUrls`** so **domain metadata** (`DomainKnowledge` in catalog DTOs) and **execution** are available across the fleet without copying arbitrary C# assemblies to every host.
- **Why first:** It aligns with **shared domain knowledge** and **delegated compute** at brick granularity, reuses **HttpRemoteBrickCatalog** / **RemoteBrick**, and does not require GPU checkpoint migration on day one.
- **Explicit non-goals for this north star:** Live migration of in-flight GPU jobs; full CRDT merge of all adaptation stores across nodes; treating phones as full adaptation workers.

**Alternate North Star B — Elastic LLM / generation routing**

- **User-visible outcome:** Under load, **generation** work **fails over** across **local → peer HTTP → RunPod** (or configured remote) using **NCR** and **PeerCapabilitySnapshotPoller** (see [ExecutionRouting.md](runtime/ExecutionRouting.md)).
- **When to prioritize B first:** If the fleet’s main pain is **model inference capacity**, not brick catalog federation. B can run **in parallel** with A once transport and trust are unified (Phase 2 of the gap plan).

**Decision:** Phase 1–3 implementation sequencing in-repo should assume **A** unless a product issue explicitly selects **B**.

---

## 2. Capability matrix (by deployment profile)

Rows are **roles** in a mesh; columns are **capabilities** operators care about. “Via” indicates required wiring (env, compose, or code).

| Capability | Full worker (`Ashlar.API` + `AddAshlar` Full/Server, adaptation on) | Headless worker (CLI + same kernel profile, no portal) | Director / control node (orchestrates others; may be same binary) | Edge / Lite (thin HTTP client such as `Ashlar.Client`, mobile MAUI) |
|------------|-------------------------------------------------------------------|----------------------------------------------------------|-------------------------------------|----------------------------------------|
| Advertise in mesh (`instances.json` / `ASHLAR_MESH_*`) | Yes — `ashlar mesh`, `ICapabilityAdvertisement` | Yes | Yes | Optional; often **client only** |
| Publish brick catalog (`GET /api/bricks`) | Yes | Only if host exposes HTTP (not default CLI) | Yes if API hosted | No — not full **Brick** host |
| Remote brick execute (`POST /api/bricks/.../execute`) | Yes — **must** be policy-gated in production | Same as left if HTTP exposed | Typically **no** (or admin-only) | No |
| Consume peer catalogs (`BrickHost:RemoteCatalogBaseUrls`) | Yes | Yes when DI includes federated mesh | Yes | No — use **API client** to director / worker |
| Generation capability routing (NCR, peers, RunPod) | Yes — `AddRunPodCapabilityRouting`, `Ashlar:RemoteCapabilities` | Profile-dependent | Optional | Lite-local models only unless thin-client |
| Adaptation / pattern persistence | Yes — pattern store path | Yes | Hub or per-node (see Phase 4 gap) | Not in Lite |
| Background agents | Yes on **Ashlar.API** host | `ashlar background-agent daemon` | Optional | Not applicable |

**Interpretation:** **Phones and Lite** are **participants** (submit tasks, show results, local small models), not drop-in replacements for **Full worker** unless you scope a dedicated **edge brick** set and HTTP surface.

---

## 3. Trust boundary (who may do what)

### 3.1 Identity and tiers

- **Peer identity:** Stable **`ASHLAR_MESH_PEER_ID`** per node; **`instances.json`** (or **`ASHLAR_MESH_INSTANCES_PATH`**) lists peers with **`trustTier`** and **`admitted`** (see [IntegratorGuide.md](IntegratorGuide.md), [Configuration.md](Configuration.md)).
- **Mesh discovery policy:** **`ASHLAR_MESH_TRUST_POLICY`** / **`ASHLAR_PEER_TRUST_POLICY`** — treat **`allowlist`** as the default for any deployment that exposes **`POST /api/bricks/.../execute`** beyond localhost.

### 3.2 Remote brick execution (North Star A)

| Caller | Allowed action | Preconditions |
|--------|------------------|---------------|
| Another Full worker in same trust zone | `POST /api/bricks/{id}/execute` for bricks in **allowlist** (integrator-defined) | TLS + **mTLS or API key / Bearer**; caller peer **admitted** and **trusted** |
| Internet / unknown | **Deny by default** | No public exposure without front proxy and auth |
| Director UI / human | Same as API auth for mutating routes | **Ashlar** built-in auth modes for mutating API (see `AshlarSecurityOptions`) |

### 3.3 Data residency

- **Brick inputs/outputs** may contain **PII or secrets**; remote execution implies **cross-node data flow**. Policy: only **redacted** or **approved** payloads cross trust zones; **`IsAirGapped`** on execution context must be honored by bricks that support it.
- **Audit:** Require **correlation IDs** on every cross-node call (Phase 3); until then, log **peer id + brick id + task id** at INFO on both sides.

### 3.4 Exit criteria (Phase 0 trust)

- [ ] Written allowlist of **which brick ids** may run remotely in v1.
- [ ] Production stance: **no** anonymous brick execute; **TLS** termination documented.
- [ ] **`ASHLAR_MESH_TRUST_POLICY=allowlist`** for multi-tenant or internet-adjacent fleets.

---

## 4. SLOs and failure modes (measurable)

Targets are **defaults for a first internal mesh**; tighten per environment.

| Metric | Definition | Initial target (internal / tailnet) | Notes |
|--------|------------|--------------------------------------|--------|
| **Catalog freshness** | Age of cached peer catalog before refresh | ≤ 60 s (HttpRemoteBrickCatalog TTL default) | Tune `BrickHost:CatalogCacheTtlSeconds` |
| **Remote brick call success** | `POST .../execute` returns 200 and `Success=true` | ≥ **99%** over 24h excluding planned drain | Exclude **caller** bugs from numerator |
| **p95 remote brick latency** | Client-side, single hop, payload under 256 KB | **Under 3 s** on LAN; **under 10 s** on tailnet | Large payloads: use **artifact handles** (Phase 3) |
| **Director / API availability** | Any node hosting **scheduling** or **only** ingress | **99%** monthly | Single director = obvious SPOF; document |
| **Partial mesh** | Fraction of peers unreachable | System **degrades**: skip peer, retry next | No silent wrong answers — surface **`peer-routing.all_peers_failed`** style errors |

**Failure modes (explicit):**

1. **Peer down:** Catalog or execute fails → retry next peer or return structured error to caller.
2. **Stale capabilities:** Logged in **CompositeBrickRegistry** / **HttpRemoteBrickCatalog**; operator may **drain** node.
3. **Trust violation:** Reject at middleware / policy; **no** partial execution.

### Exit criteria (Phase 0 SLO)

- [ ] Dashboard or log query exists for **remote brick error rate** (even if manual grep first week).
- [ ] On-call runbook line: “If error rate exceeds 5%, drain suspect peer and rotate credentials.”

---

## 5. Phase 0 completion checklist

- [x] **North star** selected (**A**, with **B** documented).
- [x] **Capability matrix** by profile (table above).
- [x] **Trust boundary** documented (mesh + remote execute + residency).
- [x] **SLOs** and failure modes stated with initial numeric targets.

**Next step (Phase 1):** Implemented — see [MeshPhase1ControlPlane.md](MeshPhase1ControlPlane.md) (`/api/mesh`, in-memory fleet + task placement).

**Next step (Phase 2):** Implemented — see [MeshPhase2TransportAndAuth.md](MeshPhase2TransportAndAuth.md) (`Ashlar:Security:Mesh`, middleware before built-in API auth).

**Next step (Phase 3):** Implemented — see [MeshPhase3DistributedExecution.md](MeshPhase3DistributedExecution.md) (correlation, idempotency, result handles).

**Next step (Phase 4):** Implemented — see [MeshPhase4KnowledgeSync.md](MeshPhase4KnowledgeSync.md) (adaptation + pattern mesh sync).

**Next step (Phase 5):** Implemented — see [MeshPhase5ElasticScheduling.md](MeshPhase5ElasticScheduling.md) (queue-aware placement + stale pending rebalancer).

**Next step (Phase 6):** Implemented — see [MeshPhase6LeasesAndCheckpoints.md](MeshPhase6LeasesAndCheckpoints.md) (leases, extend/migrate, sweep).

Optional tracking issue batch in [IssueBatch_30-60-90_Roadmap.md](IssueBatch_30-60-90_Roadmap.md).

---

## Revision history

| Date | Change |
|------|--------|
| 2026-04-22 | Initial Phase 0 execution document for federated mesh program. |
