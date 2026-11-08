# Dogfooding findings: building a multi-user product on Ashlar

What building `apps/job-search` on Ashlar exposed in the framework, most consequential first. Each finding gives the
evidence (file and line at commit `d38d825`), what it cost here, and a suggested fix. None of the fixes was made in the
framework in this change; the app works around each one, and the workaround is named.

| # | Finding | Severity |
|---|---|---|
| F1 | A code brick that declares no implementation cannot run as a workflow node, and the error does not say why | High |
| F2 | A `ConditionalNode` computes a flag but does not stop anything downstream | High |
| F3 | Brick nodes get runtime values only through ports: `BrickNode.Parameters` and `WorkflowInput.Parameters` do not reach them | Medium |
| F4 | The brick registry silently drops a brick it cannot construct | Medium |
| F5 | Egress through a local sidecar is classed as inside the host | Medium (security) |
| F6 | `apps/` is outside the egress scan roots | Medium |
| F7 | No deterministic, per-tenant scheduling; cron has no time zone | Medium |
| F8 | No durable state, idempotency or resume for workflow runs; no general audit sink | Medium |
| F9 | `BrickOutput.Summary` is dropped from workflow results | Low |

## F1: an undeclared implementation makes a brick unrunnable in a workflow

`WorkflowExecutor` resolves each brick node's implementation chain through `ImplementationChainResolver`, whose default
availability rule `DeclaredOnly` (`src/Ashlar.Core.Domain/Bricks/ImplementationChainResolver.cs:70`) keeps an
implementation only if the brick declares it in `Implementations`. A brick that declares none gets an empty chain, and
the executor throws `InvalidOperationException("DomainBrick execution failed")`
(`src/Ashlar.Core.Application/Workflows/WorkflowExecutor.cs:326`). The message names neither the brick nor the cause.

The documented way to write a brick doesn't declare one. Neither does the minimal brick in `docs/AuthoringBricks.md`,
`samples/hello-brick`, the `ashlar new brick` template (`samples/templates/brick`), or the three
`Ashlar.Bricks.GitHub` bricks. They all work when `ExecuteAsync` is called directly, and fail inside a workflow.
(Observed here for this app's six bricks; for the others it follows from the code.)

- **Cost here:** every run failed with that message; finding the cause took reading the resolver.
- **Workaround:** every app brick and `ApiAnythingBrickBase` declare `Implementations.Deterministic`, with a test
  (`CompositionDiagnostics`, `Every_brick_declares_its_deterministic_implementation_so_a_workflow_can_run_it`).
- **Fix:** treat a code brick with no declared implementations as deterministic, or make the analyzer (ASHLAR0001/2)
  flag it; and name the brick and "no declared implementation matches" in the error.

## F2: `ConditionalNode` does not gate

`ExecuteConditionalNode` (`WorkflowExecutor.cs:644`) returns `{condition, result}`, and the executor runs every node in
topological order regardless. "Skip drafting unless the posting qualifies" cannot be expressed with the node built for it.

- **Workaround:** the screen brick's `draft` output is wired to the tailor brick's `proceed` input, and the tailor and
  validate bricks return "skipped" outputs themselves.
- **Fix:** let a conditional node's false branch skip its downstream subgraph (and record the skip as an event).

## F3: runtime values reach brick nodes only through ports

`ExecuteBrickNodeAsync` builds `BrickInput` from gathered port values only (`WorkflowExecutor.cs:299`, `GatherInputs`
at `:820`). `BrickNode.Parameters` is not read. `WorkflowInput.Parameters` is read only for cluster nodes (`:365`).
So the same workflow cannot be run with different inputs.

- **Workaround:** a fresh `WorkflowDefinition` per posting, with runtime values as `InputNode.Content` text and the
  candidate as JSON (`Runs/PostingWorkflow.cs`).
- **Fix:** merge `BrickNode.Parameters` as defaults, and let `InputNode` bind a named `WorkflowInput.Parameters` entry.

## F4: the registry silently drops bricks

Brick types registered through `AddAshlarSdk(...RegisterBrick<T>())` are constructed with `ActivatorUtilities`, and a
failure is swallowed: `catch (Exception) { // Skip bricks that fail to instantiate }`
(`src/Ashlar.Infrastructure/Adaptation/Sdk/Extensions/AdaptationServiceCollectionExtensions.cs:137` and `:227`).
A brick whose dependency is missing shows up later as "DomainBrick not found", far from the cause. The same file's
certified-catalog path also admits a registered brick only with a signed certification record. Which `IBrickRegistry`
a host ends up with depends on registration order. Here it held all six uncertified bricks, but no document says which
path applies (the README says host-registered bricks are "not checked").

- **Workaround:** `AshlarRuntime` checks after `AddAshlar()` that every required brick resolved, and fails startup if not.
- **Fix:** log (or fail in strict mode) when a registered brick cannot be constructed; document which registry
  `AddAshlar()` exposes and when certification applies to SDK-registered bricks.

## F5: egress through a local sidecar is "inside the host"

`api-anything serve` listens on loopback, so a request to it is classed as inside the host boundary. The server then
reaches the website from its own process, outside the guard. Under an air-gapped profile, a plain loopback client would
let a call through to the public internet.

- **Workaround:** `ApiAnythingClient` evaluates `https://<website host>/` under its own site id (EG-HTTP-09) before
  every call and refuses unsent when the website cannot be determined. A test proves the server receives nothing when
  the guard refuses.
- **Fix:** a documented pattern (or an `EgressRequest` for "effective destination via a local proxy"), so other sidecar
  integrations (MCP servers, local gateways) get it right by default.

## F6: `apps/` is not scanned for egress

`EgressGuardConventionTests` scans `src`, `application`, `applications`, `commercial`, `products`, `tools` and
`consumer-template` (`EgressGuardConventionTests.Scanner.cs:13`). `apps/` is neither a production root nor a listed
unshipped root. This app's Google client uses `EgressHttp`, but nothing pins it in `ci/egress-inventory.tsv`.

- **Fix:** add `apps` to the production roots (then pin `jobsearch.google`), or list it as unshipped with the reason.

## F7: scheduling

Background agents (`src/Ashlar.BackgroundAgents`) are LLM tool-calling agents. Each has one static schedule from JSON
configuration, and cron runs through NCrontab with no time zone (`Scheduling/ScheduleExecutor.cs:95`). A
per-user, local-time, deterministic job has no home there, and an LLM agent must not run a pipeline that may not invent
facts.

- **Workaround:** `Runs/Dispatcher.cs` computes who is due in their own time zone and runs them in parallel.
- **Fix:** a deterministic scheduled-workflow host (workflow id + schedule + time zone + tenant key).

## F8: no durable runs, idempotency or general audit

`WorkflowExecutor` runs in memory: no persisted run state, no idempotency key, no resume after a crash, no lease. The
audit interfaces are purpose-specific (`IBarrierAuditLog` for barriers, `IDataDecisionAuditLog`,
`IAdaptationAuditLog`); none records "workflow X ran for tenant Y and did Z".

- **Workaround:** the app's `runs`, `run_locks`, `activity` and `job_checks` tables, plus Drive app keys for idempotent
  writes. The executor's event stream is copied into the run summary.
- **Fix:** an optional run store for `WorkflowExecutor` (run id, idempotency key, node checkpoints) and a general
  workflow audit sink fed by its events.

## F9: `BrickOutput.Summary` is lost

A brick node's result is `ToMutableDictionary(result.ToDictionary())` (`WorkflowExecutor.cs:333`), the data only. The
human-readable summary every brick is asked to set (`docs/AuthoringBricks.md`) is not in `NodeResult` or
`NodeCompletedEvent`.

- **Fix:** carry `Summary` on `NodeResult`.

## What worked well

- `AddAshlarSdk` + `AddAshlar()` composed with no configuration in a console host and in tests.
- The brick contract analyzer caught nothing because nothing was wrong, but it ran on the app's own bricks at no cost.
- The egress inventory gate caught the new outbound path immediately and printed the exact row to pin.
- `EgressGuard("air-gapped", "enforce")` made the refusal path trivially testable.
- QuestPDF (already used by Ashlar's workflow PDF exporter) produced extractable PDFs once ligatures were turned off.
