# Ashlar Documentation Index

Documentation index for the Ashlar platform. Start here to find what you need.

## Start Here

1. `docs/TesterQuickstart.md` — **the one lane for a first run**: clone → `dotnet build Ashlar.Kernel.sln` → `ashlar doctor` → run the API on loopback, submit one task, read its audit trail → run the certification gate. No Docker, no API keys, verified paths only.
2. `README.md` — the front door: what Ashlar is (auditable workflows, certified artifacts, your infrastructure), the trust loop / certification section, and the Try / Develop / Deploy lanes (container-first; native paths are escape hatches).
3. `docs/GettingStarted.md` — the longer tour after the quickstart: startup lanes, first pipeline, CLI commands, provider setup, testing.
4. `docs/ProjectTiers.md` — **canonical repo map** by project tier: kernel, hosts, distribution, transport/protocols, extractable `products/` scaffolds, commercial satellites, and tests. Placement rule: [`docs/architecture/product-split.md`](architecture/product-split.md).
5. `docs/IntegratorGuide.md` — embedding Ashlar in your own host: SDK packages, brick/agent registration, trust configuration, compatibility matrix.
6. `consumer-template/CONSUMING.md` — `nuget.config` + `Directory.Packages.props` template. The `Ashlar.*` graph has been on nuget.org since v0.1.1; pin `ci/published-version`. A staging feed remains optional for pre-release testing.
7. `docs/DistributionModels.md` — how to **consume and ship** Ashlar (NuGet, HTTP, CLI, compose, mesh) and the **distribution-matrix** CI workflow.
8. `CONTRIBUTING.md` — branching, the recommended **Dev Container** workflow, and the pre-PR checks; `.devcontainer/devcontainer.json` is the default development environment (Cursor / VS Code).
9. `docs/Architecture.md` — layered architecture and component boundaries; `docs/Conventions.md` — current code conventions as practiced today.
10. `docs/OpenCoreBoundary.md` — authoritative open-vs-commercial boundary and guard policy.

Setup helpers (escape hatches, not the first-run path): `scripts/setup/setup.sh` (forwards to `setup-unix.sh` on macOS/Linux), `scripts/setup/setup-unix.sh`, and `scripts/setup/setup.ps1` — cross-platform **native** dependency bootstrap + restore (CI and escape hatch); `scripts/install/container-bootstrap.sh` / `scripts/install/container-bootstrap.ps1` — one-shot **container-lane** bootstrap (Docker + image pull + smoke run; they do not install a native toolchain); `scripts/install/quickstart.sh` — one command that uses Docker when present, otherwise a local SDK; `docs/SetupMatrixVerification.md` + `scripts/setup/verify-setup-matrix.ps1` / `verify-setup-matrix.sh` — brute-force setup combination checks; `scripts/start-ashlar-api-dev.ps1` / `scripts/start-ashlar-api-dev.sh` — Docker Ollama + host `Ashlar.API` dev stack (see `docs/Configuration.md` → Ollama). Shipping (`docs/RELEASE.md`) lives under **Operator / Production Readiness** below.

## Trust loop / certification (experimental, hold-mode)

The trust loop is how "certified" is a checkable claim: analyzer fence → witness → mutation testing → determinism, then a signed certificate bound to the artifact's content hash. The gate is CI-proven (`cert-gate` is the only required check on `master`); the autonomy loop on top of it is **experimental and ships in hold mode** (`HoldAdmission=true` — it certifies fully and admits nothing), and its evidence is local spike runs.

- `docs/specs/SPEC-006-keys-and-signing.md` — keys and signing (**ACCEPTED** 2026-08-27); every "signed" claim in every other spec resolves here. `docs/specs/` currently holds this one file.
- `docs/InstanceLedger.md` — the instance ledger: durable course records and their signing story.
- `docs/certification-evidence.md` — the **falsifiable proof ledger**: every ADMIT/REJECT with the test or spike and the CI run that proved it; "Known v0 limitations" at the end. Read this before judging any "certified" claim.
- `docs/dogfood-ledger.md` — **dated evidence log** for shippable demos: pass/fail entries with repro steps before any autonomy or design-partner marketing claim. Companion to `DogfoodValidation.md`.
- `docs/certification/since-fences.md` — what the certificate actually binds after the compile-authority fences (gate-emitted artifact, IL fence, certifier identity). Published pin is `ci/published-version`, never `VERSION`.
- `docs/trust-loop/ashlar-trust-loop-spec.md` — the specification: core invariant, gate legs, proposer/witness separation, tier placement.
- `docs/trust-loop/trust-loop-integration.md` and `docs/trust-loop/trust-loop-ext-autonomous-self-extension.md` — how the loop lands in the runtime, and the autonomous self-extension extension.
- `docs/governed-pipeline.md` — the governed MEAI model pipeline every proposal flows through.
- **`docs/RunningASelfExtendingNode.md`** — the **operator guide** to running a node that extends itself unattended (A0–A5): the two dials, `ashlar policy set self_extend` / `policy show`, the build course + post-apply canary/rollback, budgets, and the `background-agent report` / `disarm` safety front doors.
- `docs/SELF-EXTEND-AUDIT.md` — background-agent self-extend safety audit (four invariants, all enforced on the live path as of 2026-08-16).
- **`docs/CertificationGate.md`** — **the builder-facing gate page**: what a *witness* is, the five legs and what each refuses, how a package-only consumer invokes `CertifyAsync`, what a rejection looks like, and the two-package / own-project rule a certifiable brick must satisfy.
- **`docs/OperatorLifecycle.md`** — the **operator persona**: `ashlar init` and the two documents, the operator-owned `ashlar.policy.yaml`, the `ashlar verify` VERIFIED→CERTIFIED reveal, the self-extend dial, `ashlar gates`, and `ashlar pkg` / `keys` trust.
- `docs/AuthoringBricks.md` + `samples/hello-brick/README.md` — author a brick the gate can judge (the sample is package-only and certifies as checked in — `ShippedSampleCertificationTests` pins it; `docs/CertificationGate.md` explains the shape).
- `samples/autonomy-objectives/README.md` — a complete tracked objective + witness + recorded model proposal, and how to feed it to the loop.
- `spikes/README.md` — what each spike under `spikes/` is, which ledger rows cite it, and why none of them is a supported entry point; `spikes/autonomy-first-flight/run-first-flight.ps1` flies one real iteration (Docker + Ollama).
- `scripts/run-cert-gate.sh` + `scripts/cert-gate-config.sh` — reproduce the CI `cert-gate` locally with the same filter.

## Operator / Production Readiness

- **`docs/DistributionModels.md`** — how Ashlar is **distributed** (NuGet, HTTP, CLI, compose, source, mesh), **pinning**, and the **distribution matrix** CI workflow that gates each channel.
- **`docs/production-readiness/README.md`** — **hub** for supporting SMB, enterprise, SaaS, and air-gapped production: release, security, ops, data/compliance, reliability, testing, operator deployment; includes [catalog by deployment type](production-readiness/CatalogByDeploymentType.md) and [runbook template](production-readiness/RunbookTemplate.md).
- `docs/DEPLOYMENT.md` — **golden paths** (portal stack, CLI image, agent server), **pinning** images vs `latest`, NuGet/CI notes.
- `docs/RELEASE.md` — **release hub** (preflight, dispatch, tag, deep links).
- `docs/RELEASE_RUNBOOK.md` — release **decision table** (tag vs NuGet-only vs branch images); **`scripts/release-preflight-local.sh`** / **`make release-preflight`** / **`dotnet run … release preflight`** for one-command local preflight.
- `docs/GitHubRepoVariables.md` — **Actions variables** for NuGet publish mode, post-push verify, SBOM, cross-verify.
- `docs/GitHubBranchProtection.md` — **branch protection** guidance (merge gates vs tag releases).
- `.github/ISSUE_TEMPLATE/release_checklist.yml` — **GitHub issue form** for a release ticket.
- `docs/StagingFeed.md` — optional **NuGet staging** push before nuget.org.
- `docs/NuGetPackageSigning.md` — optional **package signing** notes.
- `docs/PUBLISHING.md` — NuGet pack/push, **post-push verification** (registration API, SHA-256 match, SBOM/Grype vars), operator checklist.
- `.github/workflows/pack-hosting-graph-alignment.yml` — pack script vs `Ashlar.Hosting` MSBuild graph.
- `docs/ProductionReadinessGate-v1.md` — production gate commands and expected assertions (binary PASS/FAIL technical gate).
- `.github/workflows/production-readiness-gate-v1.yml` — automated production readiness gate.
- `.github/workflows/environment-setup-gate-v1.yml` — environment bootstrap + dependency setup gate (Linux/macOS/Windows).
- `.github/workflows/compose-gate.yml` — validates `deploy/compose/docker-compose.test.yml` and `deploy/compose/docker-compose.ephemeral.yml` lanes.
- `.github/workflows/devcontainer-gate.yml` — validates `.devcontainer/post-create.sh` restore + `Ashlar.CLI` build inside the dev image.
- `.github/workflows/setup-smoke-suite.yml` — **parallel** dev container + compose `config` + native Ubuntu `setup.sh`; run manually before target-hardware iteration (`docs/CiFirstHardwareSecond.md`).
- `docs/CiFirstHardwareSecond.md` — **CI first, hardware second**: which workflows to run and what still needs a physical host.
- `.github/workflows/onboarding-quickstart-gate.yml` — runs first-run onboarding commands in native + container lanes.
- `.github/workflows/container-image-gate.yml` — container image buildability and smoke-run gate.
- `.github/workflows/distribution-matrix-gate.yml` — **parallel** gates: NuGet local-pack consumer, CLI image + subcommand help smoke, API image + `curl` `/health` + `/api/status`, `Ashlar.Client` in-process test, pack-graph alignment (plus **weekly** schedule).
- `docs/CiGateInventory.md` — one-row-per-workflow trigger map (57 files, including `products-gate`) and the enforced branch-protection state (`cert-gate` is the only required check).
- `.github/workflows/release.yml` — **one entry**: tag `v*.*.*` → GHCR (`nexo-cli`, `nexo-api`) + NuGet; run summary with pin lines.
- `.github/workflows/container-image-publish.yml` — GHCR on **main** path-filtered pushes + manual (tags use `release.yml` only).
- `.github/workflows/release-nuget.yml` — **NuGet-only** manual dispatch; after push to nuget.org, **Verify NuGet consumer** (same reusable job as **release.yml**).
- `.github/workflows/docs-link-check.yml` — **lychee** link validation on **`README.md`** + **`docs/**/*.md`** (loopback URLs ignored via **`.lycheeignore`**).
- `.github/workflows/onboarding-docs-guard.yml` — prevent startup-doc regressions in quick-start commands.
- `.github/workflows/cross-platform-tests.yml` — cross-platform tests on Ubuntu, macOS, and Windows (manual-only / dormant; `scope=persistence` and `scope=playground` replace the deleted persistence and playground workflows).
- `.github/workflows/runtime-release-gate.yml` — runtime release quality gate.
- `.github/workflows/runtime-release-promotion.yml` — runtime release promotion workflow (manual-only / dormant).
- `.github/workflows/installer-bruteforce-gate.yml` — installer robustness gate.
- `.github/workflows/perf-certification.yml` — performance certification workflow.
- `.github/workflows/workflow-regression-gate.yml` — workflow regression gate (manual-only / dormant).
- `.github/workflows/test-trust-multi-env.yml` — trust tests across multiple Docker environments (manual-only / dormant).
- `.github/workflows/test-air-gapped-no-network.yml` — air-gapped validation with zero network egress (manual-only / dormant; never green — see the file header).
- `docs/CiSecrets.md` — every secret / repository variable a workflow reads and what happens on a fork without it.
- `docs/ReleaseCandidateChecklist-v1.md` — release candidate sign-off checklist.
- `docs/Testing.md` — test guard rails, timeout policy, and workflow guidance.

## Demo / Rollout Walkthroughs

- `scripts/oh-shit-demo.sh` — high-signal end-to-end demo script (bootstrap, chat, orchestration, dogfood).
- `docs/DogfoodCampaign.md` — automated dogfood campaign: release manager + specialist sub-agents (`make dogfood-campaign`, always inside the dev/test container).
- `docs/DogfoodValidation.md` — North Star dogfood blocks 1–9, closed loop, Phase F, and the campaign gate.

## Security / Trust

- `samples/README.md` — index of every tracked sample (`hello-brick`, `certified-brick-reuse`, `autonomy-objectives`, ...): what each shows, run command, prerequisites.
- `docs/FriendMeshPrefab.md` — prefab Docker Compose + env template for a small shared **Ashlar.API** hub (friends / tailnet).
- `docs/MeshPhase8OperatorHardening.md` — **Mesh Phase 8:** discovery admission, trust alias, `ashlar mesh peers` / `mesh health` / `dotnet run --project commercial/src/Ashlar.Commercial.MeshDirector -- director ...`, TLS example.
- `docs/MeshVirtualLab.md` — **Virtual mesh lab:** two Ashlar.API nodes in Docker + verify script (no extra hardware); **`scripts/bootstrap-cloud-mesh-lab.sh`** for Ubuntu/Debian cloud VMs.
- `docs/MeshAgentSetupCapabilityBreakdown.md` — mesh agent setup **tear sheet**: capability tiers, ports, and ops checklist mapped to mesh DI surfaces.
- `docs/TrustAndInformationArchitecture.md` — sanitization, audit, access boundaries.
- `docs/TailscaleAndAshlar.md` — Tailscale + Ashlar exposure profile, ACL guidance, advisory endpoint.
- `docs/config/security-exposure.env.example` — `Ashlar__Security__*` env template for operators.
- `docs/Configuration.md` — environment variables and configuration reference.
- `docs/AgentSandboxArchitecture.md` — project-scoped sandbox model for agent file/tool execution and host-app integration (Unity-friendly).

## API / SDK / Runtime

- `docs/ProjectTiers.md` — canonical repository map by project tier.
- `docs/api/index.md` — API docs index.
- `docs/sdk.md` — SDK integration guidance.
- `docs/AuthoringBricks.md` — authoritative code-brick authoring guide.
- `docs/PUBLISHING.md` — pack and publish `Ashlar.Hosting` (and its `Ashlar.*` graph) to NuGet / GitHub Packages; operator checklist.
- `docs/NuGetConsumerVerify.md` — validate NuGet-only consumption (local pack vs published feed); workflow **`.github/workflows/nuget-consumer-verify.yml`**.
- `docs/samples/StableSdkHostSample/Program.cs` — reference host integration that only uses stable SDK extension points.
- `docs/runtime/ExecutionRouting.md` — NCR-based generation routing (local, peer network, RunPod), preferences, and resilience behavior.
- `docs/AgentExecutionIsolation.md` — per-agent isolation tiers (in-process through container-per-agent), JSON field, and invocation metadata for transports.
- `docs/architecture/product-split.md` — framework vs extractable product trees; `AirGapped` vs `SecureWorkstation`.
- `docs/architecture/ProtocolIntegration-MCP-A2A.md` — MCP + A2A protocol adapters: MCP server bridge over `ITool` (allowlists, policy gate, stdio host), MCP client, A2A server core + client transport, and the `Ashlar.API` wiring (`/api/mcp`, `/api/a2a/{agentId}`; all feature-flagged off by default). MCP client and A2A refuse AirGapped **and** SecureWorkstation; local MCP server stays allowed on SecureWorkstation.
- `docs/runtime/specs/README.md` — runtime spec documents.
- `docs/runtime/benchmarks/README.md` — runtime benchmark goals and notes.
- `apps/runtime-studio/README.md` — **hub** for the Runtime Studio agent-set JSON, CLI vs API-hosted background agents, and how the Director portal fits; anchor [How this fits](../apps/runtime-studio/README.md#how-runtime-studio-fits-with-ashlar-api).
- `docs/SelfHostedAgentServer.md` — `deploy/compose/docker-compose.agent-server.yml`: mounted workspace + env template `docs/config/agent-server.env.example`.
- **`docs/Federation.md`** — hub-less **peer-to-peer sharing** of signed `.ashpkg` extensions (F1–F4): serve (`/mesh/v1/…`), pull from configured peers / a tailnet / LAN multicast discovery (`ashlar mesh lan`), and TLS/mTLS for a private fleet. Distinct from the commercial director/hub mesh. Config lives in `deploy/node.yml`.
- `docs/GrpcHost.md` — `src/Ashlar.Transport.Grpc.Server.Host`: listen address, HTTP/2 (h2c vs TLS), the client-side `Ashlar:GrpcTransport` `/run/secrets/*` defaults, compose secrets shape.
- `docs/ide/AshlarVscode.md` — VS Code / Cursor extension + `/api/ide/*` bridge (chat, patches, runs, workloads, streaming).
- `docs/Phase1SecureCopilotWalkthrough.md` — first-success secure copilot MVP walkthrough using `deploy/compose/docker-compose.agent-server.yml`.

## Planning history (historical as of 2026-08-16)

The plans below describe programs that have since finished; they are kept as the record of *why* the tree looks the way it does. Current state lives in `docs/certification-evidence.md` (the ledger), `docs/ProjectTiers.md` (the repo map) and `CHANGELOG.md`; the two gap analyses carry a banner naming their still-open rows.

- `docs/OpenCoreBoundary.md` — authoritative open-vs-commercial boundary (still current; listed under Start Here).
- `docs/CommercialExtractionPlan.md` — commercial extraction plan (Phases A–F complete); optional CLI/governance follow-ups.
- `docs/FleetGovernanceExtractionInventory.md` — classification inventory for open mesh primitives vs commercial fleet/governance code.
- `docs/MeshPhase0NorthStar.md` — **Phase 0 (executed):** federated mesh north star, capability matrix by profile, trust boundary, SLOs (fed mesh Phases 1–7).
- `docs/ExecutionPlan.md` — phased execution plan with implementation tasks, dependencies, and success metrics.
- `docs/IssueBatch_30-60-90_Roadmap.md` — 30/60/90 gap-closure issue batch (issue templates).
- `docs/NorthStarGapAnalysis.md` — **historical** North Star vs codebase gap analysis; predates the trust loop. Remaining open rows: Document Editor / Spreadsheet products and the application suite.
- `docs/GapAnalysis.md` — **historical** dogfood, observe→improve, trust, and documentation gap analysis; predates the trust loop. Remaining open rows: documentation cross-links (README ↔ IntegratorGuide closed by the front-door pass; ExecutionPlan deliberately not linked from the front door) and dogfood/mesh production workflow docs.

## Additional Material

- `assets/brand/BRAND.md` — the **brand kit**: palette, wordmark/icon SVG masters, NuGet/GitHub/social assets, and where each file gets wired; `docs/ashlar-terminal-style.md` — the CLI's **terminal style guide** (palette roles, glyph vocabulary, line format), implemented by the reference `assets/brand/AshlarConsole.cs`.
- `docs/communications/linkedin-distribution-channels.md` — optional **LinkedIn** copy emphasizing **distribution channels** (NuGet, HTTP, CLI, Compose, mesh) with pointers to **`docs/DistributionModels.md`**.
- `docs/Persistence.md` — persistence behavior and options.
- `docs/ComponentLibrary.md` — component catalog references.
- `deploy/compose/docker-compose.test.yml` — containerized test lane (`test-ubuntu`) with mounted test artifacts.
- `deploy/compose/docker-compose.ollama.yml` — Ollama-only stack (named volume for models; pair with host-run Ashlar).
- `deploy/compose/docker-compose.ephemeral.yml` — disposable local dependencies (Ollama, optional Postgres profile) plus a `ashlar` CLI service built from `.docker/Dockerfile.cli` for `run --rm ashlar ...`.
