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
| F5 | `apps/` is outside the egress scan roots | Medium |
| F6 | No deterministic, per-tenant scheduling; cron has no time zone | Medium |
| F7 | No durable state, idempotency or resume for workflow runs; no general audit sink | Medium |
| F8 | `BrickOutput.Summary` is dropped from workflow results | Low |
| F9 | Ashlar's own `generative-artifact` brick declares no implementation, so F1 makes it unrunnable as a workflow node | Medium |
| F10 | An application cannot put a brick it verified itself into the registry: `CertifiedBrickRegistry.TryAdmit` is internal | Medium |
| F11 | The gate's compile is not deterministic: re-certifying unchanged source gives the same content hash but new artifact bytes | Low |

F1–F8 were found at commit `d38d825`; F9–F11 at `a4efecf`, while adding the certified rules and model-worded cover
letters.

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
- **Workaround:** every app brick and `WebOperationBrick` (the web-operations base) declare `Implementations.Deterministic`, with a test
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

## F5: `apps/` is not scanned for egress

`EgressGuardConventionTests` scans `src`, `application`, `applications`, `commercial`, `products`, `tools` and
`consumer-template` (`EgressGuardConventionTests.Scanner.cs:13`). `apps/` is neither a production root nor a listed
unshipped root. This app's Google client uses `EgressHttp`, but nothing pins it in `ci/egress-inventory.tsv`.

- **Fix:** add `apps` to the production roots (then pin `jobsearch.google`), or list it as unshipped with the reason.

## F6: scheduling

Background agents (`src/Ashlar.BackgroundAgents`) are LLM tool-calling agents. Each has one static schedule from JSON
configuration, and cron runs through NCrontab with no time zone (`Scheduling/ScheduleExecutor.cs:95`). A
per-user, local-time, deterministic job has no home there, and an LLM agent must not run a pipeline that may not invent
facts.

- **Workaround:** `Runs/Dispatcher.cs` computes who is due in their own time zone and runs them in parallel.
- **Fix:** a deterministic scheduled-workflow host (workflow id + schedule + time zone + tenant key).

## F7: no durable runs, idempotency or general audit

`WorkflowExecutor` runs in memory: no persisted run state, no idempotency key, no resume after a crash, no lease. The
audit interfaces are purpose-specific (`IBarrierAuditLog` for barriers, `IDataDecisionAuditLog`,
`IAdaptationAuditLog`); none records "workflow X ran for tenant Y and did Z".

- **Workaround:** the app's `runs`, `run_locks`, `activity` and `job_checks` tables, plus Drive app keys for idempotent
  writes. The executor's event stream is copied into the run summary.
- **Fix:** an optional run store for `WorkflowExecutor` (run id, idempotency key, node checkpoints) and a general
  workflow audit sink fed by its events.

## F8: `BrickOutput.Summary` is lost

A brick node's result is `ToMutableDictionary(result.ToDictionary())` (`WorkflowExecutor.cs:333`), the data only. The
human-readable summary every brick is asked to set (`docs/AuthoringBricks.md`) is not in `NodeResult` or
`NodeCompletedEvent`.

- **Fix:** carry `Summary` on `NodeResult`.

## F9: the generative brick cannot be a workflow node

`GenerativeArtifactBrick` (`src/Ashlar.Core.Application/Generation/GenerativeArtifactBrick.cs:46`) sets no
`Implementations`, so `ImplementationChainResolver.DeclaredOnly` gives it an empty chain and the workflow executor
throws exactly as in F1. Ashlar's first-party generative pipeline therefore cannot be placed in a workflow.

- **Cost here:** `jobsearch.tailor` holds the generative brick (constructor injection) and calls its `ExecuteAsync`
  directly for the model-worded cover letter, so the generation is not a node and its events are not in the run's
  node log. `GenerativeBrickFinding` in the app's tests pins the fact.
- **Fix:** declare an agentic implementation on `GenerativeArtifactBrick` (and a deterministic one when the resolved
  profile supports it), or have the resolver treat `GenerativeBrick` as self-describing.

## F10: no public way to admit a verified brick

Ashlar's certified registry admits bricks through `CertifiedBrickRegistry.TryAdmit`
(`src/Ashlar.Infrastructure/Certification/CertifiedBrickRegistry.cs:53`), which is `internal`, and `IBrickRegistry` is
read-only. The verified-consumer sample (`samples/verified-brick-consumer`) loads and verifies a deployed brick but can
only call it, not register it.

- **Cost here:** the app repeats the sample's verify-then-load sequence (`JobSearch.Host/CertifiedBricks.cs`) and puts
  the instances in front of the executor with its own decorator (`CertifiedOverlayRegistry`), replacing the
  `IBrickRegistry` registration by hand.
- **Fix:** a public `AddAshlarCertifiedBricks(directory, trustPolicy)` that verifies at start-up, fails closed, and
  registers the instances.

## F11: gate-emitted bytes are not reproducible

`BrickCompileOptions.CompilationOptions` (`src/Ashlar.Infrastructure/Certification/BrickCompileOptions.cs:27`) does
not set `deterministic: true`, so each compile gets a new module version id. Certifying the unchanged
`jobsearch.recommendation` source four times gave the same content hash and four different artifact hashes.

- **Cost here:** the record binds one particular DLL, so the DLL is committed with the record (`.gitignore` excepts
  `apps/job-search/certified/*/gate-emitted-brick.dll`), and the re-certification test compares content hashes only.
  Anyone re-certifying cannot reproduce the committed artifact and must replace it.
- **Fix:** compile with `deterministic: true`, which would let a consumer rebuild the artifact from source and compare.
  The emitted bytes are pinned by golden corpora, so this is its own change.

## F12: the IL import fence refuses nested types of allowed BCL types

The import fence admits a type by its namespace. A nested type has no namespace of its own in metadata, so
`List<T>.Enumerator`, `Dictionary<TKey, TValue>.Enumerator` and `HashSet<T>.Enumerator` are refused even though
`System.Collections.Generic` is allowed. `foreach` over any of them compiles to the nested enumerator, so ordinary,
harmless code is refused.

- **Cost here:** the two certified rules use indexed loops, and the studio's step writer is told not to use `foreach`
  over those collections (`Prompts` in `JobSearch.Host/Studio/Forge.cs`). A model that does it anyway spends a
  certification round and a repair attempt.
- **Fix:** resolve a nested type's namespace through its declaring type before the allowlist check.

## F13: mutation testing reaches only `ExecuteAsync` and private instance methods, four sites per operator

`AstMutationCatalog` (`src/Ashlar.Infrastructure/Certification/AstMutationCatalog.cs:12`) mutates only `ExecuteAsync`
and private instance methods, with at most four sites per operator (`MaxPerKind = 4`), taken in source order. A
private `static` helper is never mutated. The certified claims-check rule's helpers (`Numbers`, `HasTerm`,
`StripBullet`) are static, so its "every mutant killed" says nothing about them. The "5" matched "9.5" hole (in "What
worked well" below) lived in `Numbers`, and the app's own test found it, not the gate. Rewriting those helpers as
instance methods and re-certifying is open work.

- **Cost here:** the studio refuses `static` in model-written code and puts the written methods before `ExecuteAsync`,
  so the four-site budget reaches them first (`StepSource` in `JobSearch.Core/Studio/StepSource.cs`). The starter
  steps are written the same way.
- **Fix:** mutate every method in the brick's own type, static included. Report the sites the budget skipped, so a
  certificate says what its mutation score covers.

## F14: the in-process certifier has no per-execution timeout

The certification gate runs witness cases and mutants in-process with no deadline. A mutant that turns a loop's
condition around never returns, and certification hangs with it. For a human author that is a stuck build; for a
service certifying model-written code, it is one request holding a worker forever.

- **Cost here:** the studio runs every certification in a child process (`certify-step`) with a hard timeout, a heap
  cap and a clean environment, and kills the process tree when time runs out (`Studio/Isolation.cs`; STUDIO.md I8).
- **Fix:** a per-execution budget in the gate (witness and mutant runs alike), with a timed-out mutant counted as
  killed and a timed-out witness case as a failure.

## F15: a certified brick has no portable, sealed package

Ashlar's `.ashpkg` (`ExtensionPackaging`, `src/Ashlar.Manifest/Packaging/ExtensionPackaging.cs`) seals a self-extend
`GateRecord`, meaning a proposal and its signed admission, together with the proposal's files. A brick certification
(`CertificationRecord`, from the certification chain) has no such package. It travels as a directory holding the
record, the source and the gate-emitted DLL, as in `samples/verified-brick-consumer` and this app's `certified/`. So
federation can move a self-extend proposal between nodes, but not a certified brick.

- **Cost here:** sharing steps across customers ([LIBRARY-SHARING.md](LIBRARY-SHARING.md)) needs its own step package:
  source, witness and metadata, sealed by the library publisher key. Each server re-certifies on import. STUDIO.md
  had promised `.ashpkg` for this.
- **Fix:** an `ashpkg` variant, or a sibling format, that seals a `CertificationRecord` with its source and witness.
  It would be opened by the same fail-closed verifier, so certified bricks travel through federation the way
  extensions do.

## What worked well

- `AddAshlarSdk` + `AddAshlar()` composed with no configuration in a console host and in tests.
- The brick contract analyzer caught nothing because nothing was wrong, but it ran on the app's own bricks at no cost.
- The egress inventory gate caught the new outbound path immediately and printed the exact row to pin.
- `EgressGuard("air-gapped", "enforce")` made the refusal path trivially testable.
- QuestPDF (already used by Ashlar's workflow PDF exporter) produced extractable PDFs once ligatures were turned off.
- The certification gate earned its keep. Its mutation testing refused both rule bricks until the witnesses pinned
  every boundary (for example a requirement count of 1 or 2, and `TS/SCI` mentioned alone), and its IL import fence
  refused `foreach` over a `List<T>` (the nested `Enumerator` type has no namespace). Writing the app's own test of the
  model letter then found a real hole in the claims rule, which the witness had not covered: "5 years" passed because
  "9.5" was in the context. The rule now compares whole numbers, a witness case pins it, and it was re-certified.
- The generative pipeline's repair loop needed nothing from the app beyond a post-validator: refusal reasons went back
  to the drafter, and the provenance said whether the final draft was verified.
