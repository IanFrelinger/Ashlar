# Agent execution isolation

Orchestration can declare **how strongly** each spawned agent should be isolated at the transport and execution boundary. Higher tiers are hints for hosts that can run work out-of-process, in pooled containers, or in a **dedicated container per agent instance** (strongest isolation, highest overhead).

This is **orthogonal** to model routing (for example NCR / peer / cloud in `docs/runtime/ExecutionRouting.md`). Isolation describes the **sandbox around the agent invocation**, not which GPU or region serves a model.

For an explicit, implemented boundary around one code task, see [Isolated code tasks](IsolatedCodeTasks.md).
Its opt-in runner reuses Docker sessions; it does not automatically enact the transport tiers below.

## Isolation tiers

Defined in `Ashlar.Abstractions.Execution.AgentExecutionIsolationLevel`:

| Tier | Meaning (intent) |
|------|-------------------|
| `InProcess` | Same CLR as the orchestrator; logical `AgentContainer` only (default). |
| `OutOfProcess` | Separate OS process; distinct address space. |
| `ContainerPooled` | Container-based execution with pooling (shared image, leased slots). |
| `ContainerPerAgent` | Dedicated container (or equivalent) for this agent instance for the invocation lifecycle. |

Concrete mapping (Docker, Kubernetes, Nomad, etc.) is implemented by **transports and execution hosts**, not by the abstractions layer.

## Where the tier is set

### Decomposition JSON (Architect output)

Per agent object, optional field:

- **Name:** `executionIsolation`
- **Type:** string (enum name, case-insensitive) or integer (`0`–`3` matching the enum values above).

If the field is missing, invalid, or not a defined enum value, the parser uses `InProcess` and logs a warning when the value is unusable.

Parser: `Ashlar.Orchestration.Architect.Parsers.DecompositionJsonParser`.

### Spawn specification

`Ashlar.Orchestration.Architect.Models.AgentSpawnSpec.ExecutionIsolation` defaults to `InProcess`.

## Invocation metadata (wire contract)

When the orchestrator builds an `AgentInvocationRequest`, it sets:

- **Key:** `ashlar.execution.isolation` (constant `AgentExecutionIsolation.MetadataKey`)
- **Value:** enum name as formatted by `AgentExecutionIsolation.Format` (for example `ContainerPerAgent`).

Helpers: `Ashlar.Abstractions.Execution.AgentExecutionIsolation` (`Format`, `TryParse` on metadata, `GetEffective`).

Orchestrator: `Ashlar.Orchestration.Coordination.Orchestrator` (metadata dictionary alongside `domain`, `goal`, `ollamaModel`, etc.).

### gRPC transport

`InvokeRequest` includes a **`metadata`** map (field `8` in `Protos/agent_transport.proto`). `GrpcAgentTransport` copies `AgentInvocationRequest.Metadata` into that map; `AgentTransportServiceImpl` reconstructs `AgentInvocationRequest.Metadata` before calling the inner `IAgentTransport`.

## Implementing a transport or host

1. Read `AgentExecutionIsolation.TryParse(request.Metadata, out var level)` (or `GetEffective` with a fallback).
2. For `InProcess`, keep current behavior.
3. For higher tiers, select a backend (child process, job runner, sidecar, per-invocation container, etc.).

The default **`InProcessAgentTransport`** does not spin containers; it exists so the pipeline works without a remote executor. A production host that honors `ContainerPerAgent` would allocate an isolated runtime **per agent invocation** (or per agent instance, depending on product policy) and forward the payload there.

## Tests

- `Ashlar.Tests.Orchestration.Architect.DecompositionJsonParserExecutionIsolationTests` — JSON parsing.
- `Ashlar.Tests.Orchestration.Coordination.OrchestratorTransportTests` — metadata propagation to `AgentInvocationRequest`.
