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
| projects | 60 |
| of those, published to nuget.org by a release | 22 |
| of those, declaring `<IsPackable>true</IsPackable>` | 18 |
| `ASHLAR_*` variables named in code | 175 |
| of those, operator-facing (not test-only) | 154 |
| missing from `docs/Configuration.md` | 19 |
| **mentioned in no document at all** | **0** |
| test files declaring xUnit facts | 774 |
| declared facts | 4907 |
| workflows | 61 |

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

_None._

## Packable projects

These ship to people building **on** Ashlar, so their public surface is a compatibility
commitment and ambient process configuration reaching them is a design decision, not a
convenience.

- `Ashlar.AI.Pipeline` (src/Ashlar.AI.Pipeline/Ashlar.AI.Pipeline.csproj)
- `Ashlar.Abstractions` (src/Ashlar.Abstractions/Ashlar.Abstractions.csproj)
- `Ashlar.Analyzers` (src/Ashlar.Analyzers/Ashlar.Analyzers.csproj)
- `Ashlar.Authoring` (src/Ashlar.Authoring/Ashlar.Authoring.csproj)
- `Ashlar.BackgroundAgents` (src/Ashlar.BackgroundAgents/Ashlar.BackgroundAgents.csproj)
- `Ashlar.Brick.Contracts` (src/Ashlar.Brick.Contracts/Ashlar.Brick.Contracts.csproj)
- `Ashlar.CLI` (application/src/Ashlar.CLI/Ashlar.CLI.csproj)
- `Ashlar.Certification.Contracts` (src/Ashlar.Certification.Contracts/Ashlar.Certification.Contracts.csproj)
- `Ashlar.Client` (src/Ashlar.Client/Ashlar.Client.csproj)
- `Ashlar.Contracts` (src/Ashlar.Contracts/Ashlar.Contracts.csproj)
- `Ashlar.Core.Application` (src/Ashlar.Core.Application/Ashlar.Core.Application.csproj)
- `Ashlar.Core.Domain` (src/Ashlar.Core.Domain/Ashlar.Core.Domain.csproj)
- `Ashlar.Hosting` (src/Ashlar.Hosting/Ashlar.Hosting.csproj)
- `Ashlar.Hosting.Bundle` (src/Ashlar.Hosting.Bundle/Ashlar.Hosting.Bundle.csproj)
- `Ashlar.Infrastructure` (src/Ashlar.Infrastructure/Ashlar.Infrastructure.csproj)
- `Ashlar.Orchestration` (src/Ashlar.Orchestration/Ashlar.Orchestration.csproj)
- `Ashlar.Policies` (src/Ashlar.Policies/Ashlar.Policies.csproj)
- `Ashlar.Runtime` (src/Ashlar.Runtime/Ashlar.Runtime.csproj)
- `Ashlar.Sdk` (src/Ashlar.Sdk/Ashlar.Sdk.csproj)
- `Ashlar.Tools.Assembly` (src/Ashlar.Tools.Assembly/Ashlar.Tools.Assembly.csproj)
- `Ashlar.Tools.Dev` (src/Ashlar.Tools.Dev/Ashlar.Tools.Dev.csproj)
- `Ashlar.Transport.Grpc` (src/Ashlar.Transport.Grpc/Ashlar.Transport.Grpc.csproj)
