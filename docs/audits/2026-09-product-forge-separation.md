# Product/Forge Separation Audit

**Date:** 2026-09-05  
**Status:** Proposed  
**Authors:** Cloud Agent

## Executive Summary

This audit identifies product-shaped code in the Ashlar repository and proposes extraction to a new `Ashlar.Forge` repository. The strategic goal is to maintain a clean separation between **Ashlar Core** (the Unreal Engine equivalent: reusable runtime, admission, certification) and **Ashlar.Forge** (the Fortnite equivalent: adaptive app factory that generates apps/features and validates them using Ashlar admission).

**Key finding:** Approximately 15-20% of the current repository tree represents product/application code that should live in Forge. The separation is already conceptually documented in `docs/architecture/product-split.md` but has not been physically executed.

**Proposed extraction:**
- **Runtime Studio** (apps/runtime-studio) → Forge.Studio
- **VS Code Extension** (extensions/ashlar-vscode) → Forge.IDE
- **Application Layer** (application/) → Forge.Surfaces (API + CLI as product surfaces)
- **Demos & Samples** (docs/demos/, samples/) → Forge.Examples
- **Autonomy Spikes** (spikes/autonomy-first-flight) → Forge.Research
- **Product Scripts** → Forge build/verify infrastructure

**Dependency rule:** `Ashlar.Forge` → `Ashlar.Core` (one-way only, via NuGet packages)

---

## 1. Strategic Context

### 1.1 Product Strategy

The strategic model:
- **Ashlar Core** = Unreal Engine (runtime, admission system, certification gate)
- **Ashlar.Forge** = Fortnite (adaptive app factory, self-extending product)
- **Forge must stay separate** from the core runtime to preserve Ashlar's role as a reusable framework

### 1.2 Why Separation Matters

1. **Clarity of purpose:** Core = framework for anyone building AI workflows; Forge = specific product using that framework
2. **Independent evolution:** Core stability vs. product experimentation velocity
3. **Licensing flexibility:** Core can remain fully open; Forge may have different licensing/commercial models
4. **Dependency hygiene:** Prevents core from accidentally depending on product concerns
5. **Distribution:** Core ships as NuGet packages; Forge ships as installable product with built-in agents
6. **Community boundaries:** Framework contributors vs. product users

---

## 2. Current Repository Structure Analysis

### 2.1 Core Framework (KEEP in Ashlar)

These directories contain the reusable runtime and must stay in the core repository:

#### `src/` — The Kernel Spine
- **58 projects** comprising the core framework
- Key assemblies:
  - `Ashlar.Core.Domain` — bricks, agents, domain contracts
  - `Ashlar.Core.Application` — use cases, ports, MediatR
  - `Ashlar.Orchestration` — architect, agent coordination
  - `Ashlar.BackgroundAgents` — scheduler, RAG, observe loop
  - `Ashlar.Infrastructure` — providers, persistence, routing
  - `Ashlar.Certification.*` — gate, admission, witness system
  - `Ashlar.Brick.Contracts` — portable brick interface
  - `Ashlar.Authoring` — brick authoring SDK
  - `Ashlar.Hosting` — DI composition, `AddAshlar()`
  - `Ashlar.Mcp.*` — MCP client/server adapters
  - `Ashlar.Policies` — execution policies and sandboxing

**Classification:** **KEEP** — This is the Unreal Engine equivalent. Pure framework.

#### `tests/` — Kernel Test Suite
- Cross-cutting integration tests
- Contract validation tests
- Gate smoke tests

**Classification:** **KEEP** — Framework validation suite.

#### `src/Ashlar.Tests.*` — Inline Test Projects
- `Ashlar.Tests.Contracts` — distributed contract tests
- `Ashlar.Tests.Application` — use case tests
- `Ashlar.Ingress.*.Tests` — adapter tests
- `Ashlar.Mcp.*.Tests` — MCP protocol tests

**Classification:** **KEEP** — These test framework contracts and must stay coupled to the framework.

#### `commercial/` — Commercial Fleet Extension
- `Ashlar.Commercial.Fleet.*` — fleet management overlay
- `Ashlar.Commercial.MeshDirector` — mesh coordination

**Classification:** **STAY SEPARATE** — Already in commercial isolation per `LICENSING.md`. Not part of this Forge extraction. Fleet may eventually move to its own `ashlar-fleet` repository but that's a separate decision.

#### `config/`, `ci/`, `deploy/`, `infra/`, `scripts/` (Partial)
- Build infrastructure
- CI gate definitions
- Core deployment templates (compose files for framework demos)
- Repository management scripts

**Classification:** **MOSTLY KEEP** with exceptions (see extraction list below). Core CI gates (`kernel-gate`, `cert-gate`, `coverage-gate`) stay with core. Product-specific scripts move to Forge.

---

### 2.2 Product/Application Layer (EXTRACT to Forge)

These directories represent **applications of the framework** rather than the framework itself:

#### `apps/runtime-studio/` ★★★ HIGH PRIORITY
**What it is:**
- Application-level integration composing Ashlar runtime services
- Planner + worker agent set (extender, optimizer, tester roles)
- Agent set config, bootstrap scripts, operator CLI wrappers
- Local-first model routing via Ollama
- **Phases 1-7 shipped** with daemon, forge tools, operator UX, dashboard

**Why it belongs in Forge:**
- This is a **product workflow**, not a kernel primitive
- Defines product-level agent composition (`agent_set.local.json`)
- Operator scripts (`OPERATOR.md`, `run_agent_set_local.sh`)
- Hardware-tuned workflow optimization
- **This is the "Fortnite" — a specific instantiation of Ashlar capabilities**

**Proposed destination:** `Ashlar.Forge/src/Forge.Studio/`

**Evidence from README:**
> "This is a runtime application of Ashlar, not a kernel primitive. Kernel packages stay reusable; Runtime Studio defines product workflow, operator scripts, and agent-set policy."

**Classification:** **EXTRACT** — This is the centerpiece of the Forge product.

#### `application/` ★★★ HIGH PRIORITY
**What it is:**
- `Ashlar.API` — HTTP host exposing kernel services
- `Ashlar.CLI` — `ashlar` global tool
- `Ashlar.Tests.CLI` — CLI test suite

**Why it belongs in Forge:**
- These are **product surfaces**, not framework internals
- The API is the hosted product endpoint (portal, IDE endpoints, mesh worker)
- The CLI is the product's user-facing tool
- Per `docs/architecture/runtime-vs-application.md`: "Deployable and product-facing projects that consume the runtime under `src/`"

**Existing architecture doc already calls this out:**
> "Application layer: Deployable and product-facing projects that consume the runtime under `src/`."

**Proposed destination:** `Ashlar.Forge/src/Forge.Surfaces/`
- `Forge.Surfaces.API` (was `Ashlar.API`)
- `Forge.Surfaces.CLI` (was `Ashlar.CLI`)

**Classification:** **EXTRACT** — Product surfaces that compose framework capabilities.

#### `extensions/ashlar-vscode/` ★★ HIGH PRIORITY
**What it is:**
- VS Code / Cursor extension for Ashlar agent-server
- Chat sidebar, plan/edit modes, director goals
- Patch application UI, run timeline, workload viewer
- Connects to Ashlar.API

**Why it belongs in Forge:**
- This is a **product IDE integration**, not a framework component
- Tight coupling to `Ashlar.API` product surface
- User-facing UX, not reusable library

**Proposed destination:** `Ashlar.Forge/extensions/forge-vscode/`

**Classification:** **EXTRACT** — Product IDE tooling.

#### `samples/` ★ MODERATE PRIORITY
**What it is:**
- `hello-brick/` — brick authoring reference (keep some minimal version in core)
- `templates/brick/` — brick template for `ashlar new brick` (keep in core)
- `autonomy-objectives/` — tracked autonomy objectives for trust loop **EXTRACT**
- `approval-workflow/` — AWS Step Functions + GitHub check approval **EXTRACT**
- `certified-brick-reuse/` — certification example (keep minimal in core, expand in Forge)
- `aws-sns-ashlar-lambda/` — AWS Lambda integration example **EXTRACT**

**Why some belong in Forge:**
- Autonomy objectives are **product dogfooding** content
- AWS integrations are **product deployment** examples
- Approval workflows are **product automation** examples

**Proposed destination:**
- **KEEP in Core:** `hello-brick/` (minimal brick authoring reference), `templates/brick/`
- **EXTRACT to Forge:** `autonomy-objectives/`, `approval-workflow/`, `aws-sns-ashlar-lambda/`
- **EXPAND in Forge:** Additional product samples showing full Runtime Studio usage

**Classification:** **SPLIT** — Framework samples stay; product samples extract.

#### `docs/demos/` ★ LOW PRIORITY
**What it is:**
- `Ashlar.Demos.Avalonia` — desktop UI demo
- `Ashlar.Demos.BlazorWeb` — web UI demo
- `Ashlar.Demos.ConsoleClient` — console demo

**Why it belongs in Forge:**
- These demonstrate **product HTTP client** usage
- End-user facing demos, not framework docs

**Proposed destination:** `Ashlar.Forge/examples/`

**Classification:** **EXTRACT** — Product usage examples.

#### `spikes/autonomy-first-flight/` ★ MODERATE PRIORITY
**What it is:**
- Full autonomy loop iteration spike
- Live model proposals, sweep campaigns
- Dogfood recordings and campaign evidence
- `FlightLogScannerBrick` — scratch brick for experiments

**Why it belongs in Forge:**
- This is **product R&D** / experimental autonomy workflow
- The spike tests the **product autonomy feature**, not framework contracts
- Evidence is kept for certification ledger but is product-layer proof

**Proposed destination:** `Ashlar.Forge/research/autonomy-first-flight/`

**Classification:** **EXTRACT** — Product research artifacts.

#### `products/` ★ SPECIAL CASE
**What it is:**
- `ashlar-workstation/` — offline IDE daemon (SecureWorkstation profile)
- `ashlar-cluster/` — cluster scheduler (ITaskScheduler implementation)
- `ashlar-cloud/` — hosted control-plane stubs (orgs, billing, quotas)
- `ashlar-native/` — WASM / out-of-process native host

**Current plan from `products/README.md`:**
> "These trees are **applications** that consume the Ashlar framework. They live in this monorepo until they are extracted to their own repositories."

**Strategic question:** Should these go to **Forge first**, then later to individual repos? Or directly to individual repos?

**Recommendation:**
- **PHASE 1 (now):** Move `products/` wholesale to `Ashlar.Forge/products/` as an intermediate staging ground
- **PHASE 2 (later):** Extract each to its own repository when consumer shape is stable
  - `ashlar-workstation` → dedicated repo (includes forge-vscode extension)
  - `ashlar-cluster` → dedicated repo
  - `ashlar-cloud` → dedicated repo
  - `ashlar-native` → dedicated repo

**Rationale:** Forge becomes the **product family repository** while Ashlar remains the pure framework. Later, individual products can split out as they mature.

**Classification:** **EXTRACT to Forge (staging)** — Product scaffolds that will eventually become standalone.

---

### 2.3 Scripts & Tooling (SPLIT)

#### Scripts to KEEP in Core:
- `scripts/setup/setup.sh` (framework bootstrap)
- `scripts/verify-open-commercial-dependency-boundary.py` (gate enforcement)
- `scripts/<pack-local-feed.sh>` (NuGet packaging)
- `scripts/verify-nuget-*.sh` (package verification)
- Gate-related scripts (`cert-gate`, mutation testing)

#### Scripts to EXTRACT to Forge:
- `scripts/Start-FullstackAgentServer.ps1` (product fullstack launcher)
- `apps/runtime-studio/scripts/*` (all Runtime Studio operator scripts)
- `scripts/mesh-lab-verify-*.sh` (product mesh verification scripts)
- Any scripts referencing `Ashlar.API`, `Ashlar.CLI`, or `runtime-studio`

---

### 2.4 Documentation (SPLIT)

#### Docs to KEEP in Core:
- `docs/architecture/` (framework architecture)
- `docs/AuthoringBricks.md` (brick SDK docs)
- `docs/TesterQuickstart.md` (framework testing)
- `docs/certification-evidence.md` (gate evidence ledger)
- `docs/<CONTRIBUTING.md>` (framework contribution guide)
- Contract documentation

#### Docs to EXTRACT to Forge or REPLICATE:
- `docs/demos/` (product demos)
- `docs/product-fleet/` (product operational docs)
- `docs/ide/AshlarVscode.md` (IDE extension docs)
- `apps/runtime-studio/OPERATOR.md` (product operator guide)
- **OR:** Keep architecture docs in core but create product-specific docs in Forge

---

## 3. Proposed Forge Repository Structure

### 3.1 Repository Skeleton

```
Ashlar.Forge/
├── README.md                           # Product overview, not framework
├── LICENSE                             # Potentially different from core
├── VERSION
├── .gitignore
├── global.json                         # May track different .NET version
│
├── src/
│   ├── Forge.Studio/                   # Runtime Studio (from apps/runtime-studio)
│   │   ├── Forge.Studio.csproj
│   │   ├── config/
│   │   │   └── agent_set.local.json   # Default agent set
│   │   ├── scripts/
│   │   │   ├── bootstrap_runtime_studio.sh
│   │   │   └── run_agent_set_local.sh
│   │   └── docs/
│   │       └── OPERATOR.md
│   │
│   ├── Forge.Surfaces/                 # Application surfaces (from application/)
│   │   ├── Forge.Surfaces.API/        # HTTP host (was Ashlar.API)
│   │   │   ├── Forge.Surfaces.API.csproj
│   │   │   ├── Program.cs
│   │   │   ├── Controllers/
│   │   │   └── wwwroot/                # Portal UI
│   │   │
│   │   └── Forge.Surfaces.CLI/        # CLI tool (was Ashlar.CLI)
│   │       ├── Forge.Surfaces.CLI.csproj
│   │       └── Commands/
│   │
│   ├── Forge.Adapters/                 # Cursor adapter for Forge-specific integrations
│   │   └── CursorAdapter/
│   │       ├── Forge.Adapters.CursorAdapter.csproj
│   │       └── CursorIntegration.cs   # Forge-specific Cursor integration
│   │
│   └── Forge.Verify/                   # Cursor verify harness (NEW)
│       ├── Forge.Verify.csproj
│       ├── Phases/
│       │   ├── Phase1_SyntaxCheck.cs
│       │   ├── Phase2_AdmissionGate.cs
│       │   ├── Phase3_Integration.cs
│       │   └── Phase4_Validation.cs
│       └── Harness/
│           └── VerificationRunner.cs
│
├── extensions/
│   └── forge-vscode/                   # IDE extension (from extensions/ashlar-vscode)
│       ├── package.json
│       ├── extension.js
│       └── README.md
│
├── products/                            # Staging area for future extraction
│   ├── ashlar-workstation/            # Moved from Ashlar/products/
│   ├── ashlar-cluster/                # Moved from Ashlar/products/
│   ├── ashlar-cloud/                  # Moved from Ashlar/products/
│   └── ashlar-native/                 # Moved from Ashlar/products/
│
├── examples/                           # Product usage examples
│   ├── demos/                          # From docs/demos/
│   │   ├── Forge.Demos.Avalonia/
│   │   ├── Forge.Demos.BlazorWeb/
│   │   └── Forge.Demos.ConsoleClient/
│   ├── autonomy-objectives/           # From samples/autonomy-objectives/
│   ├── approval-workflow/             # From samples/approval-workflow/
│   └── aws-lambda/                    # From samples/aws-sns-ashlar-lambda/
│
├── research/                           # Product research & spikes
│   └── autonomy-first-flight/         # From spikes/autonomy-first-flight/
│
├── tests/
│   ├── Forge.Tests.Studio/
│   ├── Forge.Tests.Surfaces/
│   └── Forge.Tests.Verify/
│
├── scripts/
│   ├── build-forge.sh
│   ├── run-forge-api.sh
│   ├── verify-forge-integration.sh
│   └── package-vscode-extension.sh
│
├── docs/
│   ├── GettingStarted.md              # Product setup
│   ├── OperatorGuide.md               # From apps/runtime-studio/OPERATOR.md
│   ├── IDEExtension.md                # Extension usage
│   ├── VerifyHarness.md               # Cursor verify documentation
│   └── architecture/
│       ├── ForgeOverview.md
│       └── CursorIntegration.md
│
└── deploy/
    ├── compose/
    │   └── docker-compose.forge.yml
    └── kubernetes/
        └── forge-deployment.yaml
```

### 3.2 Key Components

#### A. Forge.Studio (Runtime Studio)
- **Purpose:** Product workflow orchestration, agent set management
- **Capabilities:**
  - Background agent daemon (`runtime-planner`, `optimizer`, `tester`)
  - Operator CLI wrappers (`runtime-studio` subcommands)
  - Hardware-tuned workflow optimization
  - Forge tools (propose/check/build/test)
  - Observation and metrics dashboard
- **Dependencies:** `Ashlar.Hosting`, `Ashlar.BackgroundAgents.HostRunners`, `Ashlar.CLI`

#### B. Forge.Surfaces (API + CLI)
- **Purpose:** Product entry surfaces that compose framework capabilities
- **API Features:**
  - `/api/copilot/task` — task submission
  - `/api/ide/*` — IDE extension endpoints
  - `/api/runtime-studio/*` — Runtime Studio metrics/status
  - Portal UI (React/Blazor frontend)
- **CLI Features:**
  - `forge task submit`
  - `forge runtime-studio {status|doctor|metrics}`
  - `forge background-agent daemon`
  - `forge workflow optimize`
- **Dependencies:** `Ashlar.Hosting.Bundle`, `Ashlar.Client`, all NuGet packages

#### C. Forge.Verify (Cursor Verify Harness) ★★★ NEW
- **Purpose:** Built-in Cursor agent verification system for Forge self-extension
- **Phases:**
  1. **Phase 1: Syntax Check** — Roslyn analysis, compile verification
  2. **Phase 2: Admission Gate** — Ashlar certification gate (S0-S2)
  3. **Phase 3: Integration** — API endpoint tests, surface-level checks
  4. **Phase 4: Validation** — E2E product validation, UX verification
- **Why it lives in Forge:**
  - This validates **product code changes**, not framework code
  - Uses Ashlar admission as a component, doesn't define it
  - Cursor agents extending Forge run this harness
  - Framework remains unopinionated about how products verify themselves
- **Dependencies:** `Ashlar.Certification.State`, `Forge.Surfaces.API`, `Forge.Studio`

#### D. Forge.Adapters.CursorAdapter
- **Purpose:** Forge-specific Cursor integration (NOT generic Ashlar/Cursor adapter)
- **Capabilities:**
  - Forge task translation (Cursor PR → Forge objective)
  - Forge workspace context injection
  - Forge-specific prompts and templates
  - Integration with `Forge.Verify`

---

## 4. Dependency Architecture

### 4.1 One-Way Dependency Rule

```
┌─────────────────────────────────────┐
│        Ashlar.Forge                 │
│  (Product: app factory + Cursor)    │
│                                      │
│  - Forge.Studio                     │
│  - Forge.Surfaces (API, CLI)        │
│  - Forge.Verify (harness)           │
│  - forge-vscode extension           │
└───────────────┬─────────────────────┘
                │
                │ depends on (NuGet)
                ▼
┌─────────────────────────────────────┐
│        Ashlar.Core                  │
│  (Framework: runtime + admission)   │
│                                      │
│  - Ashlar.Core.* (Domain, App)      │
│  - Ashlar.Orchestration             │
│  - Ashlar.BackgroundAgents          │
│  - Ashlar.Certification.*           │
│  - Ashlar.Hosting                   │
│  - Ashlar.Brick.Contracts           │
└─────────────────────────────────────┘

NO REVERSE DEPENDENCY ALLOWED
(Ashlar.Core must never reference Ashlar.Forge)
```

### 4.2 Package Consumption in Forge

**Forge consumes Ashlar via NuGet packages:**
- `Ashlar.Hosting.Bundle` (main composition)
- `Ashlar.BackgroundAgents.HostRunners` (Runtime Studio daemon)
- `Ashlar.Certification.State` (Forge.Verify admission checks)
- `Ashlar.Client` (HTTP client for Forge.CLI)
- `Ashlar.Authoring` (brick authoring, samples)
- `Ashlar.Brick.Contracts` (portable interface)

**Forge does NOT consume:**
- `Ashlar.Infrastructure.*` (internal framework adapters)
- `Ashlar.Tests.*` (framework test internals)
- `commercial/*` packages (separate licensing boundary)

### 4.3 Embedded Option

For Forge distributions, an **embedded option** is possible:
- Bundle Ashlar assemblies directly in Forge.Surfaces.API executable
- Use `ILRepack` or single-file publish to create a standalone binary
- This is a **deployment choice**, not a code dependency violation

---

## 5. Migration Milestones

### Phase 0: Audit & Planning (Complete)
- ✅ Identify product-shaped trees
- ✅ Classify each component
- ✅ Propose Forge skeleton
- ✅ Document dependency rules
- ✅ Create this audit document
- **Output:** `docs/audits/2026-09-product-forge-separation.md`

### Phase 1: Forge Repository Bootstrap
**Goal:** Create `Ashlar.Forge` repository with skeleton, establish dependency rules

**Tasks:**
1. Create new GitHub repository: `IanFrelinger/Ashlar.Forge`
2. Initialize with:
   - `README.md` (product overview: "adaptive app factory using Ashlar admission")
   - `LICENSE` (decide: same Apache 2.0 or different?)
   - `CONTRIBUTING.md` (Forge-specific guidelines)
   - `.gitignore`, `.editorconfig`, `global.json`
   - `Directory.Build.props` with Ashlar NuGet feed reference
   - CI workflows (`.github/workflows/`)
     - `forge-gate.yml` — build, test, integration checks
     - `forge-verify-integration.yml` — test against latest Ashlar packages
3. Create directory structure (per section 3.1)
4. Add NuGet package references to Ashlar packages (initially from nuget.org or local feed)
5. Add a simple `Forge.Surfaces.CLI` hello-world that calls `AddAshlar()` to verify dependency wiring

**Success criteria:**
- Forge repository exists and builds
- Can restore Ashlar packages from feed
- Simple CLI tool proves Ashlar DI composition works
- CI gate passes

**Estimated scope:** Scaffolding + CI setup

---

### Phase 2: Extract Runtime Studio
**Goal:** Move `apps/runtime-studio/` to `Forge.Studio`, preserve functionality

**Tasks:**
1. Copy `apps/runtime-studio/` → `Ashlar.Forge/src/Forge.Studio/`
2. Rename project: `Ashlar.BackgroundAgents.RuntimeStudio` stays in Ashlar Core (it's a framework component), but the **product application** becomes `Forge.Studio`
3. Update script paths and references
4. Migrate tests: `Ashlar.Tests.RuntimeStudio` → `Forge.Tests.Studio`
5. Update `scripts/` references in Forge
6. Add `README.md` in `Forge.Studio/` with quickstart

**Success criteria:**
- `forge runtime-studio status` works against Forge.Studio
- Agent set daemon runs and claims objectives
- Tests pass in new location
- Scripts work from Forge repository

**Estimated scope:** Copy + rename + test

---

### Phase 3: Extract Application Surfaces (API + CLI)
**Goal:** Move `application/` to `Forge.Surfaces/`

**Tasks:**
1. Copy `application/src/Ashlar.API/` → `Forge.Surfaces/Forge.Surfaces.API/`
2. Copy `application/src/Ashlar.CLI/` → `Forge.Surfaces/Forge.Surfaces.CLI/`
3. Rename namespaces:
   - `Ashlar.API.*` → `Forge.Surfaces.API.*`
   - `Ashlar.CLI.*` → `Forge.Surfaces.CLI.*`
4. Update references to Runtime Studio (now `Forge.Studio`)
5. Migrate tests: `Ashlar.Tests.CLI` → `Forge.Tests.Surfaces/`
6. Update Docker compose files in Forge
7. Test API + CLI as Forge entry surfaces

**Success criteria:**
- `forge` CLI tool works (renamed from `ashlar` product CLI)
- Forge API runs on loopback with portal UI
- Can submit tasks via Forge API
- Extension connects to Forge API

**Estimated scope:** Major rename + integration testing

**Note:** Consider if CLI should be named `forge` or `ashlar-forge` to distinguish from framework. Recommendation: **`forge`** for clarity.

---

### Phase 4: Extract IDE Extension
**Goal:** Move `extensions/ashlar-vscode/` to `Forge/extensions/forge-vscode/`

**Tasks:**
1. Copy `extensions/ashlar-vscode/` → `Ashlar.Forge/extensions/forge-vscode/`
2. Rename extension: `ashlar-vscode` → `forge-vscode`
3. Update `package.json`:
   - Name: `forge-vscode`
   - Publisher: (update as needed)
   - Display name: "Forge VS Code Extension"
4. Update connection defaults to point to Forge API
5. Update README with Forge branding
6. Test extension against Forge API

**Success criteria:**
- Extension installs in VS Code / Cursor
- Connects to Forge API successfully
- Chat, plan, edit, director features work
- Can package `.vsix` from Forge repo

**Estimated scope:** Copy + rebrand + test

---

### Phase 5: Create Forge.Verify Harness
**Goal:** Build the Cursor verification harness for Forge self-extension

**Tasks:**
1. Create `Forge.Verify/` project
2. Implement verification phases:
   - **Phase 1:** Syntax check (Roslyn compilation)
   - **Phase 2:** Admission gate (call `Ashlar.Certification.State` APIs)
   - **Phase 3:** Integration tests (API smoke tests, CLI smoke tests)
   - **Phase 4:** Validation (E2E product tests)
3. Create `VerificationRunner` orchestrator
4. Add CLI command: `forge verify run --phase all`
5. Write tests for each phase
6. Document verification phases in `docs/<VerifyHarness.md>`

**Success criteria:**
- `forge verify run --phase all` executes all phases
- Can verify a sample Forge PR end-to-end
- Phases are independently runnable
- Admission gate integration works (calls Ashlar certification)

**Estimated scope:** New subsystem, significant design + implementation

**Note:** This is the key **new capability** that doesn't exist in current Ashlar. The Cursor agent needs this to validate its own changes to Forge.

---

### Phase 6: Create Cursor Adapter for Forge
**Goal:** Build `Forge.Adapters.CursorAdapter` for Forge-specific Cursor integration

**Tasks:**
1. Create `Forge.Adapters/CursorAdapter/` project
2. Implement:
   - Forge task translation (GitHub PR → Forge objective)
   - Forge workspace context (reads Forge codebase structure)
   - Forge-specific prompts (knows about Forge.Studio, Forge.Surfaces, Forge.Verify)
   - Integration with `Forge.Verify` (triggers verify phases)
3. Add configuration for Cursor agent behavior in Forge context
4. Add tests

**Success criteria:**
- Cursor agent can translate a Forge GitHub issue to a Forge objective
- Adapter injects correct workspace context
- Cursor agent knows to run `forge verify` as part of its workflow

**Estimated scope:** Moderate — adapter design + integration

---

### Phase 7: Migrate Samples & Examples
**Goal:** Move product samples/demos to Forge

**Tasks:**
1. Copy relevant samples to `Ashlar.Forge/examples/`:
   - `samples/autonomy-objectives/` → `examples/autonomy-objectives/`
   - `samples/approval-workflow/` → `examples/approval-workflow/`
   - `samples/aws-sns-ashlar-lambda/` → `examples/aws-lambda/`
2. Copy demos:
   - `docs/demos/` → `Forge/examples/demos/`
3. Update sample READMEs to reference Forge packages
4. Test samples against Forge

**Success criteria:**
- Samples build and run in Forge repository
- READMEs guide users through Forge usage
- CI runs sample tests

**Estimated scope:** Copy + documentation + CI

---

### Phase 8: Migrate Products Tree
**Goal:** Move `products/` wholesale to Forge as staging area

**Tasks:**
1. Copy `products/` → `Ashlar.Forge/products/`
2. Update product READMEs to note they now live in Forge
3. Update dependency references to use Ashlar NuGet packages
4. Add note: "These will eventually extract to individual repos"
5. Test products build in Forge

**Success criteria:**
- All four product scaffolds (workstation, cluster, cloud, native) build in Forge
- Products correctly consume Ashlar via NuGet
- Tests pass

**Estimated scope:** Copy + dependency updates + validation

**Future:** Each product later extracts to its own repo:
- `Ashlar.Forge/products/ashlar-workstation/` → new repo `ashlar-workstation`
- `Ashlar.Forge/products/ashlar-cluster/` → new repo `ashlar-cluster`
- `Ashlar.Forge/products/ashlar-cloud/` → new repo `ashlar-cloud`
- `Ashlar.Forge/products/ashlar-native/` → new repo `ashlar-native`

---

### Phase 9: Migrate Product Scripts & Docs
**Goal:** Move product-specific scripts and docs to Forge

**Tasks:**
1. Identify and move product scripts:
   - `scripts/Start-FullstackAgentServer.ps1` → `Forge/scripts/`
   - `apps/runtime-studio/scripts/*` → already moved with Forge.Studio
   - Mesh verification scripts → `Forge/scripts/verify/`
2. Move product docs:
   - `docs/product-fleet/` → `Forge/docs/operational/`
   - docs/demos/README.md (parked 2026-10-03 on `archive/parked-2026-10-03`) → `Forge/examples/demos/README.md`
   - Create `Forge/docs/GettingStarted.md`
   - Create `Forge/docs/OperatorGuide.md` (from Runtime Studio OPERATOR.md)
3. Update references in Forge README

**Success criteria:**
- Product scripts work from Forge
- Documentation is Forge-branded and accurate
- No dangling references to old Ashlar paths

**Estimated scope:** Documentation + scripting

---

### Phase 10: Migrate Autonomy Spike
**Goal:** Move `spikes/autonomy-first-flight/` to Forge research

**Tasks:**
1. Copy `spikes/autonomy-first-flight/` → `Forge/research/autonomy-first-flight/`
2. Update paths in spike scripts
3. Add README explaining this is product research
4. Test spike runs from Forge

**Success criteria:**
- Spike runs from Forge location
- Recordings and campaign directories work
- Certification ledger references remain valid

**Estimated scope:** Small — copy + path updates

---

### Phase 11: Clean Up Ashlar Core
**Goal:** Remove extracted code from Ashlar repository

**Tasks:**
1. Delete extracted directories from Ashlar:
   - `apps/runtime-studio/`
   - `application/`
   - `extensions/ashlar-vscode/`
   - `products/`
   - `spikes/autonomy-first-flight/`
   - Product samples from `samples/`
   - Demos from `docs/demos/`
   - Product scripts
2. Update `README.md` to clarify Ashlar is now framework-only
3. Update `docs/architecture/product-split.md` to document the completed extraction
4. Add pointer to Forge repository in Ashlar README
5. Update CI to remove product-specific gates

**Success criteria:**
- Ashlar repository only contains framework code
- All CI gates pass (kernel-gate, cert-gate, coverage-gate)
- README clearly states Ashlar is a framework
- Pointer to Forge for product usage

**Estimated scope:** Deletion + documentation + CI cleanup

---

### Phase 12: Forge Dogfooding & Stabilization
**Goal:** Use Forge to build Forge features (self-extend + validate)

**Tasks:**
1. Set up Forge CI with `Forge.Verify` as required gate
2. Configure Cursor agent to work on Forge repository using `Forge.Adapters.CursorAdapter`
3. Submit a test Forge issue to Cursor agent
4. Let Cursor agent:
   - Translate issue to Forge objective
   - Generate code changes
   - Run `forge verify` harness
   - Submit PR with verification evidence
5. Review and iterate on Cursor integration
6. Document the self-extension workflow

**Success criteria:**
- Cursor agent successfully completes a Forge task end-to-end
- `Forge.Verify` gates the change properly
- PR includes verification evidence from all phases
- Forge team can merge with confidence

**Estimated scope:** Major — end-to-end integration + iteration

**Note:** This is the **validation milestone** — proves the product strategy works.

---

## 6. Dependency Rules & Enforcement

### 6.1 Core Rule

**ABSOLUTE:** `Ashlar.Forge` → `Ashlar.Core` (one-way only)

```
✅ ALLOWED:
- Forge.Studio references Ashlar.Hosting (NuGet)
- Forge.Surfaces.API references Ashlar.BackgroundAgents (NuGet)
- Forge.Verify references Ashlar.Certification.State (NuGet)

❌ FORBIDDEN:
- Ashlar.Core.Application references Forge.Studio
- Ashlar.Infrastructure references Forge.Surfaces.API
- Ashlar.BackgroundAgents references Forge.Adapters.CursorAdapter
```

### 6.2 Enforcement Mechanisms

#### In Ashlar Core:
- **Existing gate:** `scripts/verify-open-commercial-dependency-boundary.py`
- **Extend to check:** No references to `Forge.*` namespaces
- **CI gate:** Fails if any `Ashlar.*` project references `Forge.*`

#### In Forge:
- **New gate:** `scripts/verify-forge-ashlar-dependency.py`
- **Check:** All Ashlar references are via NuGet packages (not `ProjectReference`)
- **Exception:** Local development mode with `--allow-project-references` flag
- **CI gate:** Fails if Forge uses `ProjectReference` to Ashlar projects

#### Local Development Mode:
For Forge developers working on both repositories locally:
- `Directory.Build.props` supports `ASHLAR_LOCAL_FEED` env var
- Points to local Ashlar `artifacts/` folder for latest packages
- Or: `ASHLAR_DEV_MODE=1` enables `ProjectReference` (development only, CI disallows)

### 6.3 Namespace Rules

**Ashlar Core namespaces:**
- `Ashlar.*` (all framework namespaces)

**Forge namespaces:**
- `Forge.*` (all product namespaces)
- `Ashlar.Certified.*` (generated bricks) — could be in either; decide by usage

**Commercial namespaces:**
- `Ashlar.Commercial.*` (separate, not part of Forge)

---

## 7. Release & Distribution Strategy

### 7.1 Ashlar Core Releases

**Package:** NuGet packages on nuget.org
**Versioning:** SemVer (e.g., `1.0.0`, `1.1.0`, `2.0.0`)
**Release cadence:** Framework stability first; conservative breaking changes
**Artifacts:**
- `Ashlar.Hosting.Bundle` (main package)
- `Ashlar.Brick.Contracts` (portable interface)
- `Ashlar.Authoring` (SDK for brick authors)
- `Ashlar.Client` (HTTP client)
- Individual component packages (`Ashlar.Orchestration`, etc.)

**Distribution:**
- NuGet.org (primary)
- GitHub Releases (source + NuGet artifacts)
- Container images: `ghcr.io/ianfrelinger/ashlar-kernel` (framework demos)

---

### 7.2 Forge Product Releases

**Package:** Installable product binaries
**Versioning:** Independent from Core (e.g., Forge `1.0.0` may depend on Ashlar `1.3.0`)
**Release cadence:** Faster iteration, product velocity
**Artifacts:**
- `forge` CLI tool (global dotnet tool or standalone binary)
- `Forge.Surfaces.API` Docker images
- `forge-vscode` extension (`.vsix`)
- Installer packages (`.msi`, `.deb`, `.rpm` — future)

**Distribution:**
- GitHub Releases (`Ashlar.Forge` repository)
- NuGet.org for `forge` tool (`dotnet tool install -g forge`)
- VS Code Marketplace for `forge-vscode`
- Docker Hub / GHCR: `ghcr.io/ianfrelinger/forge-api`
- Homebrew / Chocolatey (future)

---

### 7.3 Version Pinning Strategy

**Forge pins Ashlar version:**
- `Directory.Packages.props` in Forge specifies `Ashlar.*` package versions
- Forge tests against specific Ashlar versions
- Forge releases declare compatible Ashlar versions (e.g., "Forge 1.2.0 requires Ashlar ≥1.3.0, <2.0.0")

**Upgrade path:**
- Forge tracks Ashlar stable releases
- Forge CI runs nightly tests against Ashlar latest
- Breaking changes in Ashlar trigger Forge compatibility updates

---

## 8. Testing Strategy

### 8.1 Ashlar Core Tests (Framework)

**Scope:** Framework contracts, certification gate, kernel behavior
**Tests stay in Ashlar:**
- `tests/` (integration tests)
- `src/Ashlar.Tests.*` (contract tests)
- `cert-gate` (admission gate)
- `kernel-gate` (build + unit tests)
- `coverage-gate` (code coverage)

**Success criteria:**
- All gates pass without any Forge code
- Framework remains independently usable

---

### 8.2 Forge Product Tests

**Scope:** Product features, API endpoints, CLI commands, IDE extension, verification harness
**Tests in Forge:**
- `Forge.Tests.Studio/` — Runtime Studio tests
- `Forge.Tests.Surfaces/` — API + CLI tests
- `Forge.Tests.Verify/` — Verification harness tests
- Integration tests against Ashlar packages

**New test categories:**
1. **Forge Integration Tests:** Test Forge surfaces against real Ashlar runtime
2. **Forge E2E Tests:** Full workflow tests (submit task → verify → observe)
3. **Forge.Verify Tests:** Validate each verification phase
4. **Extension Tests:** VS Code extension integration tests

**Success criteria:**
- Forge gates pass independently
- Can test Forge against published Ashlar packages
- E2E tests prove product workflows work

---

### 8.3 Cross-Repo Testing

**Challenge:** Forge depends on Ashlar; how to test integration?

**Strategy:**
1. **Forge CI default:** Test against **published Ashlar packages** from nuget.org
2. **Nightly / PR trigger:** Test against **latest Ashlar main** (preview packages or local feed)
3. **Ashlar CI:** Run **Forge smoke tests** after Ashlar build to catch breaking changes early

**Implementation:**
- Ashlar CI publishes preview packages to a feed (GitHub Packages or Ashlar artifacts)
- Forge nightly CI consumes that feed: `--source https://github.com/IanFrelinger/Ashlar/packages`
- If Forge tests fail against Ashlar main, file issue in Ashlar (breaking change) or Forge (needs update)

---

## 9. Risk Assessment & Mitigations

### Risk 1: Large-scale refactoring disrupts active development
**Likelihood:** High  
**Impact:** High  
**Mitigation:**
- Perform extraction in phases (per milestones above)
- Keep both repositories functional during transition
- Use feature flags to enable/disable migrated components
- Maintain compatibility shims during transition period

---

### Risk 2: Dependency resolution issues (Ashlar packages vs. local development)
**Likelihood:** Medium  
**Impact:** Medium  
**Mitigation:**
- Document local development setup clearly
- Provide `ASHLAR_LOCAL_FEED` environment variable for local package testing
- Add CI check to prevent `ProjectReference` to Ashlar from Forge (except in dev mode)
- Create troubleshooting guide for package resolution issues

---

### Risk 3: Breaking changes in Ashlar break Forge
**Likelihood:** Medium  
**Impact:** High  
**Mitigation:**
- Ashlar CI runs Forge smoke tests before release
- Forge pins to specific Ashlar versions (not `*` wildcards)
- Ashlar maintains compatibility for at least N-1 versions
- Breaking changes in Ashlar require coordinated Forge update

---

### Risk 4: Curse of two repositories (synchronization overhead)
**Likelihood:** Medium  
**Impact:** Medium  
**Mitigation:**
- Clear ownership: Ashlar = framework team, Forge = product team
- Ashlar changes affecting Forge trigger Forge PRs automatically (via CI)
- Regular sync meetings between teams
- Document inter-repo workflow in CONTRIBUTING.md

---

### Risk 5: Confusion about where code belongs
**Likelihood:** Medium  
**Impact:** Low  
**Mitigation:**
- This audit document serves as placement guide
- Update `docs/architecture/product-split.md` with decision tree
- PR template includes checklist: "Is this framework or product code?"
- Code review enforces boundary

---

### Risk 6: Ashlar becomes too abstract without product driving it
**Likelihood:** Low  
**Impact:** Medium  
**Mitigation:**
- Forge provides **living proof** that framework APIs are usable
- Ashlar team reviews Forge usage patterns for API improvements
- Forge issues drive Ashlar feature requests
- Dogfooding loop: Forge pain points → Ashlar improvements

---

### Risk 7: Forge.Verify harness is incomplete / doesn't catch regressions
**Likelihood:** Medium  
**Impact:** High  
**Mitigation:**
- Start with minimal viable phases (syntax + admission)
- Expand phases iteratively based on real Cursor failures
- Track harness coverage: % of Forge PRs that passed harness but had bugs
- Keep human review as final gate during stabilization

---

## 10. Open Questions & Decisions Needed

### Q1: Should Forge have a different license than Ashlar Core?
**Options:**
- **Option A:** Both Apache 2.0 (full open source)
- **Option B:** Ashlar = Apache 2.0, Forge = commercial license (or dual-license)
- **Option C:** Ashlar = Apache 2.0, Forge = AGPL (copyleft for product)

**Recommendation:** Start with **Option A** (both Apache 2.0) for simplicity. Revisit if commercial concerns arise.

---

### Q2: Should `forge` CLI tool be named `forge` or `ashlar-forge`?
**Options:**
- **Option A:** `forge` (clean, product-focused)
- **Option B:** `ashlar-forge` (clarifies relationship to Ashlar)

**Recommendation:** **`forge`** — The product stands alone, users don't need to think about Ashlar internals.

---

### Q3: Where does `CursorGeneratorModel` (test double) belong?
**Current location:** `Ashlar.Infrastructure.Adaptation.Generation.CursorGeneratorModel`  
**Issue:** It's a test double, not a real product integration

**Options:**
- **Option A:** Keep in Ashlar Core (it's framework test infrastructure)
- **Option B:** Move to Forge as a product test helper
- **Option C:** Delete it and use a mock in tests

**Recommendation:** **Keep in Ashlar Core** — It's a test double for framework certification tests, not a product feature.

---

### Q4: Should `products/` go to Forge first or directly to individual repos?
**Options:**
- **Option A:** `products/` → Forge (staging) → individual repos later
- **Option B:** `products/` → individual repos immediately (`ashlar-workstation`, etc.)

**Recommendation:** **Option A** — Forge becomes the product family home. Later, mature products spin out when they have dedicated teams.

---

### Q5: What is the Cursor agent onboarding flow for Forge?
**Question:** When a Cursor agent starts work on Forge, how does it learn about Forge.Verify, Forge.Studio, etc.?

**Proposal:**
- `Forge/README.md` includes "For Cursor Agents" section
- `Forge/.cursorrules` file provides agent guidance
- `Forge.Adapters.CursorAdapter` injects Forge context automatically
- Agent must run `forge verify` before submitting PR

**Decision needed:** Finalize Cursor onboarding doc structure.

---

### Q6: Should Ashlar.API stay in Core as a demo surface?
**Current plan:** Extract to Forge as `Forge.Surfaces.API`

**Alternative:** Keep a **minimal** `Ashlar.API` in Core as framework demo, and Forge has **full product** `Forge.Surfaces.API`

**Recommendation:** **Extract fully to Forge** — API is product surface, not framework primitive. Core can have simple examples in `samples/` but no full HTTP host.

---

### Q7: How do we handle existing Ashlar users during transition?
**Concern:** Users who installed Ashlar CLI and API will suddenly find them moved to Forge

**Mitigation plan:**
1. **Deprecation notice:** Ashlar releases a `1.x` final version with deprecation warnings: "Ashlar.API and Ashlar.CLI are now Forge. Install `forge` CLI."
2. **Migration guide:** `docs/<MigratingToForge.md>` in Ashlar repository
3. **Redirect:** `ashlar` CLI checks if `forge` is installed and suggests migration
4. **Transitional packages:** Publish `Ashlar.CLI` and `Ashlar.API` as thin wrappers that delegate to Forge (temporary, deprecated)

---

## 11. Next Steps: Agent Task for Forge Scaffold

### Immediate Next Task

**Title:** Scaffold Ashlar.Forge repository with initial structure

**Objective:** Create the `Ashlar.Forge` GitHub repository with skeleton structure, minimal CLI tool, and CI gates.

**Scope:**
1. Create GitHub repository: `IanFrelinger/Ashlar.Forge`
2. Initialize repository structure:
   - `README.md` (product overview)
   - `LICENSE` (Apache 2.0 to match core)
   - `CONTRIBUTING.md`
   - `.gitignore`, `.editorconfig`, `global.json`
   - `Directory.Build.props` with Ashlar NuGet package references
   - `Directory.Packages.props` with pinned Ashlar versions
   - Directory structure: `src/`, `tests/`, `scripts/`, `docs/`, `examples/`, `extensions/`, `products/`, `research/`
3. Create minimal `Forge.Surfaces.CLI` project:
   - Single `Program.cs` that calls `AddAshlar()` and runs a hello-world command
   - Dependency: `Ashlar.Hosting.Bundle` from nuget.org
   - Command: `forge hello` → prints "Forge is connected to Ashlar v{version}"
4. Add CI workflow:
   - `.github/workflows/<forge-gate.yml>`
   - Build Forge.Surfaces.CLI
   - Run `forge hello`
   - Verify no `ProjectReference` to Ashlar (only NuGet)
5. Add documentation:
   - `README.md` — overview, architecture diagram, link to this audit
   - `docs/GettingStarted.md` — how to build and run Forge
   - `docs/architecture/<ForgeOverview.md>` — product architecture

**Success Criteria:**
- Repository exists at `IanFrelinger/Ashlar.Forge`
- CI builds and runs successfully
- `forge hello` command works
- README clearly explains Forge is the product layer on top of Ashlar Core

**Estimated Effort:** ~1-2 hours for agent to scaffold

---

### Subsequent Tasks (Queue for Future Agents)

After scaffolding, queue these tasks for subsequent agents:

1. **Extract Runtime Studio** (Phase 2)
   - Copy `apps/runtime-studio/` → `Forge.Studio/`
   - Verify daemon works in new location

2. **Extract Application Surfaces** (Phase 3)
   - Copy `application/` → `Forge.Surfaces/`
   - Rename to Forge namespaces

3. **Extract VS Code Extension** (Phase 4)
   - Copy `extensions/ashlar-vscode/` → `forge-vscode`
   - Rebrand and test

4. **Create Forge.Verify Harness** (Phase 5)
   - Implement verification phases
   - Add `forge verify` command

5. **Create Cursor Adapter** (Phase 6)
   - Implement `Forge.Adapters.CursorAdapter`

6. **Migrate Examples & Samples** (Phase 7)
   - Copy product samples to Forge

7. **Migrate Products Tree** (Phase 8)
   - Copy `products/` to Forge

8. **Clean Up Ashlar Core** (Phase 11)
   - Delete extracted code from Ashlar
   - Update documentation

9. **Dogfood Forge** (Phase 12)
   - Use Cursor agent on Forge to validate self-extend workflow

---

## 12. Conclusion

### Summary

This audit has identified **~15-20% of the Ashlar repository** as product-shaped code that should be extracted to a separate `Ashlar.Forge` repository:

- **Runtime Studio** (apps/runtime-studio)
- **Application Surfaces** (application/)
- **VS Code Extension** (extensions/ashlar-vscode)
- **Product Samples** (samples/, docs/demos/)
- **Autonomy Spikes** (spikes/autonomy-first-flight)
- **Products Scaffolds** (products/)

The extraction follows the strategic model:
- **Ashlar Core** = Unreal (reusable runtime + admission)
- **Ashlar.Forge** = Fortnite (adaptive app factory + built-in validation)

The **one-way dependency rule** is absolute: `Ashlar.Forge → Ashlar.Core` via NuGet packages only.

### Why This Matters

1. **Clarity:** Framework vs. product is explicit
2. **Independence:** Core evolves for stability, Forge for velocity
3. **Reusability:** Ashlar becomes a true framework others can build on
4. **Autonomy:** Forge validates itself using Ashlar admission (dogfooding)
5. **Scalability:** Products can later extract to individual repos as they mature

### Recommended Approval Process

1. **Review this audit** with Ashlar core team
2. **Decide open questions** (licensing, naming, etc.)
3. **Approve milestones** and prioritize phases
4. **Scaffold Forge** (immediate next agent task)
5. **Execute phased extraction** per milestones 1-12

### Final Note

This separation is **not just organizational** — it's **architectural**. By extracting Forge, we prove that Ashlar Core is a true framework. If Forge can build a self-extending product on top of Ashlar, so can anyone else.

**The goal:** Ashlar admission ensures Forge's self-modifications aren't slop. This only works if Forge is a **user** of Ashlar, not part of Ashlar itself.

---

## Appendix A: Detailed File Inventory

### Files to EXTRACT to Forge

```
apps/
  runtime-studio/                     → Forge.Studio/
    config/
    scripts/
    OPERATOR.md
    PLAY_INTERNAL.md
    README.md

application/
  src/
    Ashlar.API/                       → Forge.Surfaces.API/
    Ashlar.CLI/                       → Forge.Surfaces.CLI/
    Ashlar.Tests.CLI/                 → Forge.Tests.Surfaces/
  README.md

extensions/
  ashlar-vscode/                      → forge-vscode/

samples/
  autonomy-objectives/                → Forge/examples/autonomy-objectives/
  approval-workflow/                  → Forge/examples/approval-workflow/
  aws-sns-ashlar-lambda/              → Forge/examples/aws-lambda/

docs/
  demos/                              → Forge/examples/demos/
  product-fleet/                      → Forge/docs/operational/

spikes/
  autonomy-first-flight/              → Forge/research/autonomy-first-flight/

products/
  ashlar-workstation/                 → Forge/products/ashlar-workstation/
  ashlar-cluster/                     → Forge/products/ashlar-cluster/
  ashlar-cloud/                       → Forge/products/ashlar-cloud/
  ashlar-native/                      → Forge/products/ashlar-native/
  tests/                              → Forge/products/tests/

scripts/ (selective)
  Start-FullstackAgentServer.ps1     → Forge/scripts/
  mesh-lab-verify-*.sh                → Forge/scripts/verify/
```

### Files to KEEP in Ashlar Core

```
src/                                  ✅ All framework code
tests/                                ✅ Framework integration tests
samples/hello-brick/                  ✅ Minimal brick authoring reference
samples/templates/brick/              ✅ Brick template for CLI
samples/certified-brick-reuse/        ✅ Certification example (minimal)
docs/architecture/                    ✅ Framework architecture
docs/AuthoringBricks.md               ✅ Brick SDK documentation
docs/TesterQuickstart.md              ✅ Framework testing guide
docs/certification-evidence.md        ✅ Gate evidence ledger
scripts/verify-*                      ✅ Framework verification scripts
scripts/setup/                        ✅ Framework setup scripts
ci/                                   ✅ Framework CI gates
config/                               ✅ Framework config
deploy/compose/ (minimal)             ✅ Framework demo compose files
```

### Files to KEEP SEPARATE (commercial/)

```
commercial/                           🔒 Already separated, not part of Forge
```

---

## Appendix B: Reference Architecture Diagrams

### Before: Monorepo Structure

```
Ashlar (monorepo)
├── src/ (framework)
├── application/ (product)          ← MIXED
├── apps/runtime-studio/ (product)  ← MIXED
├── extensions/ashlar-vscode/       ← MIXED
├── products/ (future products)     ← MIXED
├── samples/ (mixed)                ← MIXED
└── commercial/ (separate)          ← ALREADY SEPARATED
```

**Problem:** Framework and product are intertwined.

---

### After: Clean Separation

```
Ashlar.Core (framework)
├── src/ (kernel spine)
├── tests/ (framework tests)
├── samples/ (minimal framework examples)
└── docs/ (framework docs)
    ↓
    │ (NuGet packages)
    ↓
Ashlar.Forge (product)
├── src/
│   ├── Forge.Studio/           (Runtime Studio)
│   ├── Forge.Surfaces/         (API + CLI)
│   ├── Forge.Verify/           (Cursor verify harness)
│   └── Forge.Adapters/         (Cursor adapter)
├── extensions/forge-vscode/
├── products/                    (staging for future extraction)
├── examples/
└── research/

Ashlar.Commercial (separate)
└── commercial/                  (Fleet, MeshDirector)
```

**Benefit:** Clear boundaries, one-way dependencies, framework reusability.

---

## Appendix C: Glossary

- **Ashlar Core:** The framework (runtime, admission, certification gate). Analogous to Unreal Engine.
- **Ashlar.Forge:** The product (app factory, self-extending, built-in agents). Analogous to Fortnite.
- **Admission:** The certification gate that ensures generated code passes correctness + mutation testing.
- **Runtime Studio:** The product workflow orchestration system (planner + worker agents).
- **Forge.Verify:** The Cursor verification harness that validates Forge code changes through phases.
- **CursorAdapter:** Forge-specific integration with Cursor agents (task translation, context injection).
- **One-way dependency:** Forge depends on Core via NuGet; Core never references Forge.
- **Product scaffold:** A prototype product application living in the monorepo until ready for extraction (e.g., `products/ashlar-workstation`).

---

**End of Audit**
