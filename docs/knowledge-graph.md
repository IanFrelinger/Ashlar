# Knowledge graph

<!-- GENERATED FILE. Do not edit by hand.
     Source: scripts/knowledge-graph/build-knowledge-graph.py
     Gated by: scripts/knowledge-graph/verify-knowledge-graph-current.py -->

Derived from the tree, regenerated on demand, and gated: if this file and the repository
disagree, a required check fails. It is not a description of the repository that someone
remembered to update — staleness here is a red build.

## Shape

| | count |
|---|---|
| projects | 61 |
| of those, packable (they ship to consumers) | 19 |
| `ASHLAR_*` variables named in code | 161 |
| of those, operator-facing (not test-only) | 142 |
| missing from `docs/Configuration.md` | 65 |
| **mentioned in no document at all** | **47** |
| test files declaring xUnit facts | 725 |
| declared facts | 4353 |
| workflows | 62 |

## Namespaces cert-gate selects

Read from `scripts/cert-gate-config.sh`, so this cannot drift from the filter itself.

- `Ashlar.Tests.Infrastructure.Tests.Adaptation.GenerationSafety`
- `Ashlar.Tests.Infrastructure.Tests.Certification`
- `AstMutationEngineTests`

## Operator-facing variables no document mentions

An operator cannot set a knob they cannot discover, and the inverse already bit once: a
variable was documented under a name nothing read, so following the docs left pinning off.
This list is data rather than a failure — the gate refuses *growth*, not the existing debt.

Kept separate from "missing from `docs/Configuration.md`" on purpose: a variable described in
some other document is a discoverability problem, not an undocumented one, and conflating the
two overstates the gap by the difference between the counts above.

- `ASHLAR_ANALYZER_SEVERITY_FLOOR`
- `ASHLAR_APP_STATE`
- `ASHLAR_AVAILABLE_VRAM_BYTES`
- `ASHLAR_BACKGROUND_TASK_PERMISSION`
- `ASHLAR_BATTERY_OPTIMIZATION_ENABLED`
- `ASHLAR_BATTERY_PERCENT`
- `ASHLAR_BUILD_BUDGET`
- `ASHLAR_CHARGING`
- `ASHLAR_CPU_UTIL_PERCENT`
- `ASHLAR_E2E_LIVE_MODEL`
- `ASHLAR_E2E_LIVE_STACK`
- `ASHLAR_FORGE_APPROVED_TTL_HOURS`
- `ASHLAR_FORGE_PROPOSED_TTL_HOURS`
- `ASHLAR_GENERATION_DEPTH_CEILING`
- `ASHLAR_GPU_UTIL_PERCENT`
- `ASHLAR_LOCAL_CONTEXT_SIZE`
- `ASHLAR_MESH_DIR`
- `ASHLAR_NETWORK_LATENCY_MS`
- `ASHLAR_NETWORK_METERED`
- `ASHLAR_NETWORK_WIFI`
- `ASHLAR_ON_BATTERY`
- `ASHLAR_RELEASE_CORE_HISTORY_WINDOW`
- `ASHLAR_RELEASE_CORE_MIN_PASS_RATE`
- `ASHLAR_RELEASE_CORE_MIN_TOTAL`
- `ASHLAR_RELEASE_HISTORY_WINDOW`
- `ASHLAR_RELEASE_LANE_REPETITIONS`
- `ASHLAR_RELEASE_MIN_PASS_RATE`
- `ASHLAR_RELEASE_MIN_TOTAL`
- `ASHLAR_RELEASE_PROVIDER`
- `ASHLAR_RELEASE_SLO_NCR_FAILURE_RATE`
- `ASHLAR_RELEASE_SLO_NCR_LOAD_MS`
- `ASHLAR_RELEASE_SLO_NCR_OUTCOME_MS`
- `ASHLAR_RELEASE_SLO_NCR_RESOLUTION_MS`
- `ASHLAR_RELEASE_VISUAL_HISTORY_WINDOW`
- `ASHLAR_RELEASE_VISUAL_MIN_PASS_RATE`
- `ASHLAR_RELEASE_VISUAL_MIN_TOTAL`
- `ASHLAR_STORAGE_AVAILABLE_BYTES`
- `ASHLAR_STORAGE_TOTAL_BYTES`
- `ASHLAR_TAILNET_CMD`
- `ASHLAR_TAILNET_REFRESH_SECONDS`
- `ASHLAR_TEST_BUDGET`
- `ASHLAR_TEST_NO_NETWORK`
- `ASHLAR_THERMAL_STATE`
- `ASHLAR_TOTAL_VRAM_BYTES`
- `ASHLAR_USER_ACTIVE`
- `ASHLAR_VISUAL_PROMOTION_STREAK`
- `ASHLAR_VISUAL_REQUIRED_MODE`

## Packable projects

These ship to people building **on** Ashlar, so their public surface is a compatibility
commitment and ambient process configuration reaching them is a design decision, not a
convenience.

- `Ashlar.AI.Pipeline` (src/Ashlar.AI.Pipeline/Ashlar.AI.Pipeline.csproj)
- `Ashlar.API` (application/src/Ashlar.API/Ashlar.API.csproj)
- `Ashlar.Analyzers` (src/Ashlar.Analyzers/Ashlar.Analyzers.csproj)
- `Ashlar.Authoring` (src/Ashlar.Authoring/Ashlar.Authoring.csproj)
- `Ashlar.Brick.Contracts` (src/Ashlar.Brick.Contracts/Ashlar.Brick.Contracts.csproj)
- `Ashlar.CLI` (application/src/Ashlar.CLI/Ashlar.CLI.csproj)
- `Ashlar.Certification.Contracts` (src/Ashlar.Certification.Contracts/Ashlar.Certification.Contracts.csproj)
- `Ashlar.Certification.State` (src/Ashlar.Certification.State/Ashlar.Certification.State.csproj)
- `Ashlar.Client` (src/Ashlar.Client/Ashlar.Client.csproj)
- `Ashlar.Contracts` (src/Ashlar.Contracts/Ashlar.Contracts.csproj)
- `Ashlar.Core.Domain` (src/Ashlar.Core.Domain/Ashlar.Core.Domain.csproj)
- `Ashlar.Hosting` (src/Ashlar.Hosting/Ashlar.Hosting.csproj)
- `Ashlar.Hosting.Bundle` (src/Ashlar.Hosting.Bundle/Ashlar.Hosting.Bundle.csproj)
- `Ashlar.Ingress.AwsSns` (src/Ashlar.Ingress.AwsSns/Ashlar.Ingress.AwsSns.csproj)
- `Ashlar.Ingress.DynamoDb` (src/Ashlar.Ingress.DynamoDb/Ashlar.Ingress.DynamoDb.csproj)
- `Ashlar.Lite` (src/Ashlar.Lite/Ashlar.Lite.csproj)
- `Ashlar.Manifest` (src/Ashlar.Manifest/Ashlar.Manifest.csproj)
- `Ashlar.Runtime.Bundle` (src/Ashlar.Runtime.Bundle/Ashlar.Runtime.Bundle.csproj)
- `Ashlar.Sdk` (src/Ashlar.Sdk/Ashlar.Sdk.csproj)
