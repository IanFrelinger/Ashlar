# SDK compatibility policy

This document describes how Ashlar classifies NuGet packages and public APIs, what the stability promise is for each tier, which mechanism enforces that promise in the build, and how breaking changes are handled.

The HTTP API has its own, separate policy: [`docs/api/versioning.md`](api/versioning.md).

## The v0.1.0 promise

Ashlar launches as **v0.1.0 = "production-usable core, experimental autonomy"** (decision D5; owner to confirm). Concretely:

- **Stable tier: no breaking changes within `0.1.x`.** A breaking change to a stable-tier public API ships only in the next minor (`0.(x+1).0`), and only after the old shape carried an `[Obsolete]` deprecation in the prior minor. Additive changes (new types, new optional parameters, new overloads) may ship in any `0.1.x`.
- **Experimental tier may change at any time**, including in a patch release. Its use is a compile-time opt-in (see [ASHLAREXP001](#ashlarexp001)).
- **Internal tier carries no promise.**

Until `1.0.0`, "MAJOR" in the [Semantic Versioning 2.0.0](https://semver.org/) sense is the minor digit; from `1.0.0` the ordinary SemVer rules apply (breaking changes only in a MAJOR bump, MINOR is additive, PATCH is fixes).

## Package tiers

### Stable

These packages are intended for external integration and carry the promise above.

| Package | Enforced by |
|---------|-------------|
| `Ashlar.Sdk` | `Microsoft.CodeAnalysis.PublicApiAnalyzers` (`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` in the project) + `PublicApiGenerator` snapshot (`application/src/Ashlar.Tests.CLI/PublicApi/Ashlar.Sdk.approved.txt`) |
| `Ashlar.Client` | `Microsoft.CodeAnalysis.PublicApiAnalyzers` |
| `Ashlar.Brick.Contracts` | `Microsoft.CodeAnalysis.PublicApiAnalyzers` |
| `Ashlar.Authoring` | `Microsoft.CodeAnalysis.PublicApiAnalyzers` + `PublicApiGenerator` snapshot (`Ashlar.Authoring.approved.txt`) |
| `Ashlar.Hosting.Bundle` (metapackage: references the `Ashlar.Hosting` graph at a single version) | `Microsoft.CodeAnalysis.PublicApiAnalyzers` (declared surface is empty by design; the analyzer is there so it stays empty unless reviewed) |

`Ashlar.Abstractions` is not a stable-tier package, but it is the contract assembly the stable packages transitively expose, so it runs the same analyzer with the same files. `Ashlar.Framework.Sdk` is covered by a `PublicApiGenerator` snapshot only (`Ashlar.Framework.Sdk.approved.txt`).

#### How the analyzer enforces the promise

Every project in the table references `Microsoft.CodeAnalysis.PublicApiAnalyzers` and declares its public surface in two text files next to the `.csproj`:

- `PublicAPI.Shipped.txt` - the surface that has been released under the promise. Removing or changing a line here is a breaking change: the analyzer reports RS0017 ("Symbol '...' is part of the declared API, but is either not public or could not be found") for a symbol whose declaration no longer matches its line.
- `PublicAPI.Unshipped.txt` - public surface added since the last release. A new public symbol that is in neither file fails the build with RS0016 ("Symbol '...' is not part of the declared public API"). Malformed or duplicated API files fail with RS0024/RS0025.

The build treats analyzer warnings as errors (`TreatWarningsAsErrors` in `Directory.Build.props`), so **an unreviewed public-API change fails every CI job that builds the project - `cert-gate` and `kernel-gate` for `Ashlar.Abstractions`, `Ashlar.Brick.Contracts` and `Ashlar.Client` (through `Ashlar.Tests.Infrastructure`), and the `Ashlar.sln` build (`cross-platform-tests`) plus the release pack (`scripts/pack-ashlar-hosting-graph.sh` via `reusable-release-nuget.yml`) for `Ashlar.Sdk`, `Ashlar.Authoring` and `Ashlar.Hosting.Bundle` (the bundle is in no solution; the pack is its only CI build) - and it fails the local `dotnet build` that produced it**. To make a public-API change, edit the text file in the same PR: additions go to `Unshipped.txt`; a removal of a `Shipped.txt` symbol is recorded as a `*REMOVED*<symbol>` line in `Unshipped.txt` (the analyzer's convention; the `Shipped.txt` line is dropped at the next promotion) plus an entry under **Breaking** in `CHANGELOG.md`, and is only accepted in a `0.(x+1).0` PR. `dotnet format analyzers --diagnostics RS0016` applies the analyzer's code fix and appends the missing lines for you.

The analyzers' RS0036 (missing nullable annotation in the API file) and RS0037 (nullable context missing) are disabled (`NoWarn`) uniformly, matching the original `Ashlar.Brick.Contracts` configuration; every API file starts with `#nullable enable`.

#### Release step: promote Unshipped -> Shipped on tag

**This has happened.** `v0.1.0`, `v0.1.1` and `v0.1.2` are tagged and published, and the promotion ran: the reviewed surface now lives in `PublicAPI.Shipped.txt` (399 lines in `Ashlar.Abstractions`, 612 in `Ashlar.Brick.Contracts`, 31 in `Ashlar.Client`, 9 in `Ashlar.Sdk`, 5 in `Ashlar.Authoring`), and the promotion left every `PublicAPI.Unshipped.txt` at the bare `#nullable enable` header. Of `Ashlar.Brick.Contracts`' 612, 147 are the generative-profile ports (see "Code-brick authoring surface"), recorded straight into `Shipped.txt` because those types had shipped, untracked, since `v0.1.0`. Since then, `Ashlar.Abstractions`' `Unshipped.txt` has gained six lines (the `Ashlar.Abstractions.Paths.PathContainment` helper), which are promised at the next tag.

So `Shipped.txt` is now a **promise already made**, not an empty file waiting for one. Read the paragraph above accordingly: additions go to `Unshipped.txt` and are promised at the next tag; a change to a line already in `Shipped.txt` is a break against published packages and needs the process in "Breaking change process" below.

`Ashlar.Hosting.Bundle` is the exception, and deliberately: its `Shipped.txt` is header-only because a metapackage declares no surface of its own, and the analyzer is there to keep it that way.

> This section described the pre-`v0.1.0` state for three releases after it stopped being true, which
> is why `scripts/ci/verify-compat-policy-current.py` now fails the build if it says nothing has
> shipped while `git tag` disagrees.

When tagging `v0.1.0` (and every release after it), as part of "Before you tag" in `docs/RELEASE_RUNBOOK.md`:

1. Review `PublicAPI.Unshipped.txt` in each stable-tier project. Anything that should not be promised gets made `internal` (or `[Experimental]`) **before** the tag.
2. Move every line except the `#nullable enable` header from `PublicAPI.Unshipped.txt` to `PublicAPI.Shipped.txt`; leave `Unshipped.txt` with the header only.
3. Commit as `chore(api): promote unshipped public API to shipped for vX.Y.Z` on the release commit.

From that point on, a change to a `Shipped.txt` line is a breaking change under this policy.

#### Code-brick authoring surface

The `ashlar new brick` code-brick path references `Ashlar.Authoring` and exposes the following authoring types as a stable contract. Their namespaces are preserved for source compatibility with existing consumers; their implementation is hosted in the stable brick contract assembly (`Ashlar.Brick.Contracts`), so they are covered by its `PublicAPI.*.txt` files.

- `Ashlar.Core.Domain.Bricks.Brick`
- `Ashlar.Core.Domain.Bricks.BrickCategory`
- `Ashlar.Core.Domain.Bricks.BrickInterface`
- `Ashlar.Core.Domain.Bricks.BrickInputDefinition`
- `Ashlar.Core.Domain.Bricks.BrickOutputDefinition`
- `Ashlar.Core.Domain.Execution.BrickInput`
- `Ashlar.Core.Domain.Execution.BrickOutput`
- `Ashlar.Core.Domain.Bricks.ImplementationType`
- `Ashlar.Core.Domain.Execution.IExecutionContext`

`Ashlar.Brick.Contracts` also ships the generative-profile ports in `Ashlar.Core.Domain.Bricks.Ports`: `AgentProfile`, `AgentProfileCapabilities`, `GenerationTunables`, `BrickConstraintManifest`, `GenerationRequest`, `GeneratedArtifact`, `IArtifactDrafter`, `IDeterministicDrafter`, `ISandboxProvider`, `IDeploymentTarget`, `DeploymentApplyResult`, `IAcceptanceEvaluator`, `IAcceptanceGatedDeploymentTarget`, `DefaultAcceptanceEvaluator`, `AcceptanceDecision`, `AcceptanceContext`, `AcceptanceResult`, `DeploymentSmokeResult`. They have been public, unchanged, since `v0.1.0` and are stable-tier. A file-wide `#pragma warning disable RS0016` in each of their five source files used to keep them out of public-API tracking, so nothing enforced that promise; they are now in `PublicAPI.Shipped.txt`. In this repository, `DiagnosticSuppressionConventionTests` (cert-gate) fails any suppression of RS0016, RS0017, RS0026 or RS0027, and any CS0618 or ASHLAREXP001 disable that does not name the symbol it is for with a reason, other than two reviewed project-wide opt-ins and eight grandfathered pragmas. The check is lexical; its row in `ci/cert-gate-assertions.md` lists what it does not see.

### Experimental

APIs are marked with the .NET [`Experimental`](https://learn.microsoft.com/dotnet/api/system.diagnostics.codeanalysis.experimentalattribute) attribute. They may change or be removed in any release, MINOR or PATCH, without a deprecation window. Consumers should treat them as preview-only and pin package versions if they take a dependency.

The compiler turns every use of an experimental API into an **error** carrying the diagnostic id below, so taking the dependency is always a visible, per-diagnostic opt-in rather than something a transitive reference can smuggle in.

#### ASHLAREXP001

The one experimental diagnostic id today; new ids get their own row (and their own constant) rather than reusing this one.

| Diagnostic | Surface | Meaning | How to opt in |
|------------|---------|---------|---------------|
| `ASHLAREXP001` | The **autonomy (self-extension) loop**: `Ashlar.Core.Application.Autonomy.*` (`TouchSet`, `ObjectiveTierClassifier`, `TrustKernel`, `GenerationLineage`/`RecursionDiscipline`, `IProposalSource` and the proposal/repair records, `ICertificateRevocationList`, `ILineageAuthority`, `LoopPauseControl`, `ClusterBudget`, `RepairFeedbackPolicy`, `ObjectiveSource`); `Ashlar.Infrastructure.Certification.HotSwap.*` (`AutonomousIterationHarness`, `CertifiedBrickHotSwapHost`, the swap/admission/provenance models, session build/execution backends, `AutonomyDigest`, `RepairFeedback`); `Ashlar.Infrastructure.Autonomy.*` (`AddAshlarAutonomy`, `AshlarAutonomyOptions`, digest and reaper services); `Ashlar.BackgroundAgents.Autonomy.*` (`AutonomyLoopService`, `AddAutonomyLoop`, `OllamaProposalSource`, `ObjectiveArtifacts`) plus `TelemetryObjectiveExtractor` and the `Source`/`Touch` members of `ObjectiveDocument`; `AutonomyLedgerScan`; the `TouchSet`/`Lineage` members of `CertificationRequest`; and `CertificationServiceCollectionExtensions.AddCertifiedBrickHotSwapHost`. | The trust-loop extension APIs are usable and tested, but their shapes are still being driven by the dogfood campaigns (`docs/certification-evidence.md`) and may change without a deprecation window. **The certification gate itself (`ICertificationGate`, `CertificationRequest` minus the two members above, the witness/mutation checks) is NOT experimental** - it is the product; only the self-extension surface around it is. | Per call site: `#pragma warning disable ASHLAREXP001` / `restore`. Per project (you accept the whole surface): `<NoWarn>$(NoWarn);ASHLAREXP001</NoWarn>` in the `.csproj`. The Ashlar repo's own test projects do the latter in `Directory.Build.targets`. |

The diagnostic id and the help link it carries are defined once, in `Ashlar.Core.Application.Autonomy.AutonomyExperimental` (`DiagnosticId`, `UrlFormat`), and applied as `[Experimental(AutonomyExperimental.DiagnosticId, UrlFormat = AutonomyExperimental.UrlFormat)]`. That holder type is deliberately not experimental itself (a member-level attribute binds its arguments in the containing type's scope and would otherwise trip the diagnostic it names).

`netstandard2.0` targets: `System.Diagnostics.CodeAnalysis.ExperimentalAttribute` is a `net8.0+` BCL type. `Ashlar.Core.Application` (multi-targeted `netstandard2.0;net8.0;net10.0`) compiles an internal polyfill of the attribute (`src/Ashlar.Compat/Polyfills/ExperimentalAttribute.cs`, linked into every `.NETStandard` inner build by `Directory.Build.targets`); the compiler recognises the attribute by its full name, so a `netstandard2.0` consumer gets the same `ASHLAREXP001` diagnostic as a `net8.0` one. Nothing is documented-only.

### Substrate

**Every package this repository publishes that is not in the Stable table above.** They are not
frozen, and this tier does not pretend otherwise. What they promise is different in kind:

> **A change to a substrate package's public surface is permitted in a minor release. Making one
> silently is not.**

**Why not simply promote them.** `Ashlar.Core.Application` declares 474 public types,
`Ashlar.Infrastructure` 390, `Ashlar.Orchestration` 198 and `Ashlar.BackgroundAgents` 126 (counted as
public type declarations in each project's sources). Freezing that at 0.2.0 would end meaningful refactoring, and it would be a promise made over a
surface nobody yet maps accurately — an IL analysis run against this tree in September proposed 52
`Ashlar.Infrastructure` types as unreferenced and the compiler rejected 9 of them, one of which had
12 call sites the analysis could not see.

**Why not leave them unpromised.** A consumer who builds their own repository on Ashlar lives in this
tier. "May change in any release" is honest and useless to them: it tells them nothing about what an
upgrade costs.

**What this tier is worth to a consumer.** Not that an upgrade is safe, but that it is *knowable* —
a minor release enumerates what moved, so the cost of upgrading can be read before it is paid rather
than discovered during.

#### What enforces it today, and what does not

Stating this exactly, because a policy that claims enforcement it does not have is worse than one
that claims none:

| | Status |
|---|---|
| Surface changes recorded in `CHANGELOG.md` under `### Breaking` | **In force**, by review. `v0.2.0` records 41 `Ashlar.Infrastructure` types becoming internal. |
| `PublicAPI.Shipped.txt` tracked for substrate packages, so a surface change appears as a reviewable diff | **Not yet.** Only the Stable tier and `Ashlar.Abstractions` carry these files. |
| Release notes generated from the API diff between tags, rather than written by hand | **Not yet**, and it depends on the row above. |

Until the second row lands, the promise rests on review rather than on a mechanism — which is
precisely the shape this repository distrusts elsewhere, and the reason it is written down here as a
gap rather than implied as coverage.

#### A naming hazard, recorded rather than fixed

Three substrate packages are named as if they were optional developer tooling and are not:

| Package | Actually required by |
|---|---|
| `Ashlar.Tools.Assembly` | `Ashlar.Hosting` |
| `Ashlar.Tools.Dev` | `Ashlar.Hosting`, `Ashlar.Mcp.Server.Host` |
| `Ashlar.Policies.Dev` | `Ashlar.Runtime.Bundle`, `Ashlar.CLI` |

They cannot be unpublished — `Ashlar.Hosting.Bundle` is a Stable-tier package and reaches two of them
transitively, so removing them from the feed breaks restore for the tier that carries the strongest
promise. Renaming them is a breaking change and belongs to a major. Recorded here so that a consumer
reading `.Dev` on nuget.org does not conclude it is optional.

This is also worth stating plainly for the Stable tier: **its promise covers the API you call, not
the dependency graph beneath it.** `Ashlar.Hosting.Bundle` declares no surface of its own and pulls a
substrate graph; the guarantee is that `AddAshlar()` keeps working, not that nothing under it moves.

### Internal

Assemblies this repository does **not** publish: test projects, in-repo tooling, spikes, and the
commercial tree. They are reachable only by a `ProjectReference` inside a checkout, they carry no
promise of any kind, and they may change without a note.

If an assembly is on nuget.org, it is not in this tier — it is Substrate, and the disclosure promise
above applies to it. That distinction is the point of the split: "internal" previously covered both
things a consumer could never see and things they could `dotnet add package`.

## Breaking change process

1. Prefer additive changes (new types, new optional parameters, new overloads) over modifying existing contracts. Additive changes go into `PublicAPI.Unshipped.txt` in the same PR and may ship in any patch.
2. For stable packages, deprecate first: mark the old shape `[Obsolete]` with a clear message and migration path in minor `0.x`; remove or change behavior only in `0.(x+1).0` (from `1.0.0`: only in the next MAJOR). The removal PR carries the `*REMOVED*` line in `PublicAPI.Unshipped.txt` and a **Breaking** entry in `CHANGELOG.md`.
3. Document notable changes in release notes and, when applicable, in migration notes for integrators.
4. Experimental APIs may change in MINOR or PATCH releases; announce significant shifts in release notes when practical. Promoting an experimental API to stable = removing the `[Experimental]` attribute in a PR that also adds it to `PublicAPI.Unshipped.txt` of a stable-tier package (it becomes promised on the next tag). Demoting a shipped stable API to experimental is a breaking change and follows step 2.

## CI

- The public-API analyzer runs in **every** build of the covered projects with `TreatWarningsAsErrors`; it is not a separate job and cannot be skipped by path filters. `cert-gate` and `kernel-gate` (`.github/workflows/`) build `Ashlar.Abstractions`, `Ashlar.Brick.Contracts` and `Ashlar.Client`; the `Ashlar.sln` build (`cross-platform-tests`) covers `Ashlar.Sdk` and `Ashlar.Authoring`, and the release pack (`reusable-release-nuget.yml`) is the one CI build of `Ashlar.Hosting.Bundle`; `production-readiness-gate-v1` and `runtime-release-gate` build only the CLI graph. An unreviewed public-API change fails them, and fails the local `dotnet build` that produced it.
- The `PublicApiGenerator` snapshot test (`application/src/Ashlar.Tests.CLI/Tests/Commands/BrickAuthoringPublicApiSnapshotTests.cs`) is a second, independent witness for `Ashlar.Sdk`, `Ashlar.Authoring` and `Ashlar.Framework.Sdk` and fails on any surface change until the `.approved.txt` file is updated in the same PR.
