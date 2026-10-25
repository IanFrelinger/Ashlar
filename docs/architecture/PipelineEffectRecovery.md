# Pipeline effect recovery and observed provider usage

Pipeline checkpoints record an invocation's intent before dispatching its worker. A crash after a remote write but before the completion checkpoint leaves the stage `Running`, with its attempt count and operation identity. Checkpointing alone cannot guarantee exactly-once external effects.

Every stage has an explicit `Effects` declaration:

| Declaration | Retry and fallback after a failed invocation | Resume of an incomplete stage |
| --- | --- | --- |
| `Unspecified` (default) | Stop for reconciliation | Refused |
| `None` | Allowed | Allowed |
| `Idempotent` | Allowed using the original key | Allowed with the original persisted key and fingerprint |
| `NonIdempotent` | Stop for reconciliation | Refused |

`None` is a caller's assertion that replay cannot repeat external writes. Deterministic workers are not automatically pure. `Idempotent` requires every connector in the stage's retry/fallback chain to enforce the same operation identity atomically at the external service. Passing a key to a service that ignores it provides no duplicate-effect protection. The connector must also retain its deduplication record for the entire period during which the workflow may resume.

The orchestrator supplies `PipelineStageExecutionRequest.IdempotencyKey`. The value is allocated once, persisted before invocation, and shared by every attempt, fallback worker, and resumed destination. Run IDs and attempt numbers change; the operation key does not. A fingerprint binds the key to the stage declaration, routing, topology, and run inputs. Resume rejects changed work instead of using an old key for a new request. Source stage IDs must match the current graph exactly, with no missing, additional, or duplicate records. Invalid persisted states or effect declarations are refused. Input dictionary ordering does not change the fingerprint.

An uncertain failure of an unannotated or non-idempotent stage sets `ReconciliationRequired` and fails the run even when noncritical failures are otherwise allowed. Exceptions and cancellation retain the last durable state; a `Running` stage may already have affected the outside world. A committed completion whose acknowledgement was lost remains completed. Accounting recovery never rolls it back.

Resume creates a fresh destination and reads the source as a snapshot. It does not acquire a lease on the source. Consequently even an incomplete `Pending` stage with unsafe effects cannot be resumed automatically: another host might still execute the source. Concurrent resumes of an idempotent stage use the same key, and the external connector must deduplicate those concurrent calls. Completed stages are carried forward without invoking their worker again. An unavailable worker may be skipped before dispatch because no invocation has happened yet.

For older checkpoints, incomplete explicitly pure stages can retain their prior resume behavior. Incomplete legacy stages cannot be upgraded to `Idempotent`: they lack the original operation key and fingerprint. Completed legacy outputs without fingerprints are refused even for pure stages, because their match to current inputs cannot be established. Reconcile or migrate those records using independently verified original inputs, or deliberately start a new operation. For unsafe uncertainty, inspect the external service and establish what happened before doing so. Creating a new run without `ResumeRunId` creates new keys and may repeat effects; it is not a reconciliation mechanism.

Existing templates that omit `Effects` still execute their first attempt, but automatic retry, fallback after failure, and incomplete resume are now conservative. Mark only known pure stages `None`; use `Idempotent` only after implementing and testing connector deduplication. The runtime cannot verify these declarations from worker type or source code.

`PipelineRun.ProviderUsageAttempts` records supported provider observations during that run invocation, including retries and failed or cancelled requests. Each record identifies its observation boundary: instrumented legacy/NCR transports record `HttpAttempt`, while the default hosted MEAI path records `LogicalCall`. MEAI observes the per-target SDK call, excluding duplicate router audit events; it does not invent a count of hidden SDK retries. Custom audit registrations remain active, and optional accounting observer failures cannot trigger another provider call.

The computed `ProviderUsage` keeps HTTP `Reported*` and MEAI `LogicalReported*` token subtotals separate. Do not add those subtotals blindly: a custom MEAI target may itself use an instrumented HTTP transport, so both can describe the same tokens. `AttemptCount` counts HTTP attempts, `LogicalCallCount` counts SDK calls, and `ObservationCount` counts all observations. The completeness flag remains false when any logical-call observations are present. Missing counts remain unknown, and an empty collection is not proof of zero usage. Cached input and reasoning counts are subsets, not extra tokens to add to input/output totals.

The native MEAI Ollama client preserves reported counters, including the final streaming chunk with no text. Missing or malformed counts stay unknown; valid zero counts remain zero. `ProviderReportedChatUsage` explicitly identifies provider-reported counters. Plain `UsageDetails` from other or custom adapters has unknown provenance and is not silently promoted to reported usage; LLamaSharp currently supplies no counters. This means an SDK response can expose counts without those counts appearing in the reported subtotal until its adapter establishes their provenance. A consumer that stops reading a stream early records a cancelled logical call, with any usage actually observed up to that point.

Usage is checkpointed with progress and an interruption attempts a final accounting write without altering execution state. A hard process loss or unavailable storage can still lose uncheckpointed usage. On resume, `ResumeSourceRunId` links to the prior run and the destination contains only its newly observed provider attempts. Prior usage is not copied into the destination. To analyze a recovery chain, inspect its source records as well and avoid counting a shared ancestor twice when several destinations resumed it.

The production-style recovery tests inject failures before completion commit and after commit acknowledgement, reopen LiteDB, race two resumed hosts, and exercise real deterministic/agentic executor adapters against an independent deduplicating service ledger. They verify refused unsafe replay and stable operation keys rather than claiming distributed exactly-once delivery.
