# Architecture Overview

Ashlar is a private AI platform built on modular, contract-based components (bricks) that compose into execution pipelines and extend autonomously under policy constraints. Trust enforcement is structural: data provenance, barrier identity resolution, and audit logging are integrated into the execution pipeline at the architectural level. The mesh layer federates capabilities across trusted .NET peers, enabling distributed execution with policy-controlled routing.

## Layers

```
┌─────────────────────────────────────────────────────────────────┐
│  CLI / Host / UI                                                 │
│  (Commands, System.CommandLine, AddAshlar)                          │
├─────────────────────────────────────────────────────────────────┤
│  Core.Application (Use Cases)                                    │
│  MediatR handlers, validation, analysis, agents, testing         │
├─────────────────────────────────────────────────────────────────┤
│  Orchestration                                                   │
│  Architect, agents, coordination, negotiation, orchestration     │
├─────────────────────────────────────────────────────────────────┤
│  BackgroundAgents                                               │
│  Scheduler, RAG, Trust, web search, observation pipeline         │
├─────────────────────────────────────────────────────────────────┤
│  Infrastructure                                                  │
│  ProviderFactory (LLM), persistence, adaptation, IO, execution  │
├─────────────────────────────────────────────────────────────────┤
│  Core.Domain / Abstractions                                      │
│  Bricks, behaviors, agents, ports                               │
└─────────────────────────────────────────────────────────────────┘
```

## Key Components

### Core.Application

- **Use cases** (MediatR): `AnalyzeCode`, `RunValidation`, `RunAgent`, `RunTests`
- **Ports**: `IAnalysisService`, `IValidationService`, `IAgentExecutor`, `ITestRunner`
- **Behaviors**: FluentValidation pipeline

### Abstractions

- **`IModel`**: Core model interface for agent execution
- **`IAgent`**: Agent contract (Name, ThinkAsync)
- **`IAgentMemory`**: Agent memory abstraction

### Orchestration

- **Architect**: Decomposes requests into agent goals
- **Agents**: Domain agents (Gameplay, Economy, Security, etc.) that call LLM via `IModel`
- **Coordination**: Dependency resolution, conflict detection, output integration
- **Orchestrator**: End-to-end flow

Each orchestration call has its own lifecycle, dependency outputs, progress history, and resource
reservation ownership. The scope flows through asynchronous transport calls and restores an outer
run when a nested call finishes. Reusing a decomposition agent ID in another run does not share
outputs or release the first run’s reservations. Resource limits and preemption still operate
across all runs, and health monitoring still reports all live containers.

Result progress is captured before agent shutdown. Result escalations belong to the current call;
the operator escalation queue retains history across calls. Output events remain observable on the
shared bus, while the orchestrator records its own dependency outputs directly without persistent
self-subscriptions.

Run cleanup waits up to five seconds for agent shutdown, including after cancellation or another
failure, then removes that run’s registrations and reservations. A cleanup failure does not replace
the original orchestration error. This bounds the caller’s wait; it cannot stop arbitrary agent code
that ignores cancellation. Agents must cooperate with shutdown and must not register new agents
from a shutdown callback.

Resource allocation rejects negative requirements and negative budget maxima. Zero-capacity
utilization is reported as zero. Preemption uses the remaining deficit in each resource
dimension and commits a complete victim plan only when it can satisfy the request; a rejected
request leaves existing reservations intact. Preemption remains reservation bookkeeping,
not a guarantee that agent code has stopped.

### Infrastructure

- **ProviderFactory** (`IProviderFactory`): Routes LLM calls to OpenAI, Azure, Ollama, local (ONNX), video (SmolVLM2), or mock/offline/echo (with Polly retries)
- **Persistence**: In-memory by default; LiteDB for pattern store, adaptation audit, copilot tasks, pipeline runs, execution traces, test failures
- **Adaptation**: Brick decomposition, recompilation, fix generation
- **Execution routing**: NCR-driven local/remote routing with peer-network and RunPod cloud execution paths
- **Mesh**: File-based peer discovery, capability advertisement, local transport, trust policies

#### Execution Routing (Generation)

- **Entry point**: `CapabilityRoutingBrick` (`generation.capability-routing`) is the default generation brick.
- **Router**: `NcrCapabilityRouter` resolves targets from NCR capability snapshots and job requirements.
- **Local path**: `ILocalExecutor` is selected when VRAM, compute class, and queue depth satisfy requirements.
- **Peer path**: `AshlarPeerBrickExecutor` dispatches to eligible peer Ashlar nodes with timeout + failover.
- **Cloud path**: `RunPodBrick` executes full RunPod lifecycle (spin up, dispatch, poll, pull, teardown).
- **Policy controls**: job-level `RemoteExecutionPreference` plus system-level peer routing options.

### Trust and information architecture

- **SanitizingProviderFactory**: Wraps `IProviderFactory`, sanitizes prompts before cloud
- **CloudSanitizationProxy**: PII checks, `ISensitiveContentFilter` (email, phone, SSN, API keys)
- **Audit log**: Redactions and decisions
- **Scope note**: Trust wiring is automatic in `AddAshlar()` hosting registration; standalone CLI command DI graphs must explicitly opt in.

### Hosting

- **AddAshlar()**: Registers all kernel services
- **AddAshlarProfile(...)**: Registers environment-specific module sets (`Full`, `Server`, `Edge`, `AirGapped`, `System`, `SecureWorkstation`) to peel optional dependencies.
- **AddAshlarOpenTelemetry()**: Optional metrics export

## Data Flow

1. **CLI**: `ashlar validate` → `ValidateCommand` → `RunValidationHandler` → `IValidationService`
2. **Agent**: `ashlar agent` → `AgentCommand` → `IAgentExecutor` → `IModel` (via `IProviderFactory`)
3. **LLM**: `ProviderFactory.ExecuteLLMAsync` → HTTP (OpenAI/Azure/Ollama) with retry
4. **Trust**: `SanitizingProviderFactory` → `CloudSanitizationProxy.SanitizeForCloud` → inner factory

## Dependencies

- **Ashlar.Hosting** → Infrastructure, Orchestration, BackgroundAgents
- **Ashlar.CLI** (`application/src/Ashlar.CLI`) → Ashlar.Hosting, bricks, and test projects for CLI composition (see **`docs/architecture/runtime-vs-application.md`**)
- **Ashlar.Infrastructure** → Core.Application, Core.Domain, Abstractions
