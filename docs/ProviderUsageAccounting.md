# Provider usage accounting

`ProviderUsageScope` captures inference usage without changing the string-returning `IProviderFactory` port. HTTP attempt coverage includes the legacy factory's OpenAI, OpenAI-compatible, Azure OpenAI and Ollama text/vision calls, plus NCR's Ollama chat backend. Manifest and health probes do not count as inference attempts. Direct local ONNX/LLamaSharp, video and external worker paths do not have HTTP attempt instrumentation.

`AddAshlar` also connects the default MEAI chat pipeline's per-target usage observer to the current scope. These records have `Boundary = LogicalCall`: they describe SDK invocations, whose internal HTTP retries are not visible. Router and policy-only events are excluded. The MEAI Ollama client preserves reported counters, including valid zero values, and distinguishes missing/malformed counts. Other SDK usage with unspecified provenance remains explicitly unknown. Existing custom auditors still receive their events, and observer failures cannot trigger a replay.

```csharp
using var usage = new ProviderUsageScope(runId);
try
{
    var text = await providers.ExecuteLLMAsync(provider, system, prompt, config, ct);
}
finally
{
    var attempts = usage.Snapshot();
    var subtotal = ProviderUsageSummary.FromAttempts(attempts);
    // Persist both records and completeness information, even after failure/cancellation.
}
```

Each record contains its observation boundary, invocation identifier, attempt number, provider/model identifier, correlation identifier, timing, outcome, HTTP status when available, and usage. It contains no prompt, generated text, credential or endpoint URL. Use identifiers without secrets for model/deployment names and correlation IDs.

Instrumented HTTP retries share a request ID and have separate attempt numbers. Those failed responses retain any reported usage before the HTTP error is raised. Transport failures and cancellation retain unknown usage; either may have consumed provider resources. MEAI records logical calls with `Attempt = 1`; SDK retries and counters in HTTP error bodies are not necessarily visible there. No additional request is made to collect counts, and response parsing does not introduce retries after a successful HTTP response. Existing retry policies still determine whether a provider request is replayed.

Input, output, cached input and reasoning token counts are nullable. Missing or malformed fields stay null, while valid zero values remain zero. `HasInvalidCounts` flags invalid supplied fields and preserves other valid fields. Cached input is a subset of input, and reasoning is a subset of output: do not add them to the input/output total. `Source` identifies reported, estimated or unknown counts. MEAI observations can retain counters with unknown provenance; reported subtotals exclude unknown and estimated counts.

NCR `InferenceResult.Usage` holds provider-reported nullable counts. Its existing `TokenCount` now prefers the Ollama `eval_count` when representable as an `int`; otherwise it retains the previous character-based approximation. `TokenCountSource` explicitly distinguishes that estimate from a reported count. The exact nullable `long` count remains available in `Usage`.

`ProviderUsageSummary` keeps boundaries separate. `ObservationCount` counts all records, `AttemptCount` counts actual HTTP attempts, and `LogicalCallCount` counts SDK calls. `Reported*TokenCount` subtotals include only HTTP observations; `LogicalReported*TokenCount` subtotals include only logical calls. A custom SDK target may itself call an instrumented HTTP provider, so these groups can describe the same work. They must not be added without explicit linkage. There is no automatic combined billing total.

A null subtotal means no reported value was available, or summation overflowed; inspect `HasOverflow`. `HasCompleteReportedInputAndOutput` is true only when all observations are HTTP attempts with valid reported input and output counts. It is false for an empty snapshot or any logical SDK call, because SDK retries may be hidden. Even a complete observed HTTP subtotal does not establish coverage of every provider path in a workflow, and does not establish invoice cost or a hard spending limit. Counts loaded from persistence are revalidated before summation.

Scopes flow across awaited child tasks. Parallel child scopes are isolated, while each terminal record also propagates once to active parent scopes. A propagated record retains the nearest child's correlation ID, allowing a workflow collector to retain stage attribution. `RecordCurrent` lets other adapters submit terminal observations with explicit provenance and boundary. Repeated records with the same invocation ID, attempt and boundary are collected once. Await child work before disposing the scope; disposal freezes its snapshot. Collection itself does not use the request cancellation token.

The pipeline orchestrator checkpoints its current invocation's observed attempts, including interrupted attempts when its final accounting write succeeds. Resume lineage stays separate from new usage: the destination does not copy its source's observations. Replayed provider calls can still incur additional charges. An abrupt process crash can lose attempts since the last checkpoint; this in-memory collector is not a durable provider billing ledger.

`ProviderUsageAccountingProdStyleTests` exercises real loopback HTTP exchanges for supported factory providers, usage fields, unknown/malformed counts, retry attribution, error responses, vision parity and duplicate-send prevention. NCR handler fixtures cover provenance, cancellation, transport failure and concurrent nested scopes without live provider credentials.

`MeaiUsageAccountingProdStyleTests` exercises the default host model, custom auditors, persistent
workflow records, streaming completion and early disposal, unknown provenance, and observer failures.
