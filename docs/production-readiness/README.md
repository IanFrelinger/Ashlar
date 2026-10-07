# Production readiness

This folder helps you **support every major production concern** in one place: release discipline, security, operations, data/compliance, reliability, testing, and operator-facing deployment. Use it as a **roadmap and checklist**, then fill in org-specific details (names, URLs, RPO/RTO, tooling).

**Who this is for:** operators, security reviewers, release managers, and engineers hardening self-hosted or air-gapped deployments.

| Area | Guide | One-line goal |
| ---- | ----- | --------------- |
| **Kernel hardening** | [Kernel hardening plan v1](KernelHardeningPlan-v1.md) · [Kernel readiness](KernelReadiness-v1.md) · [Chaos drill](KernelChaosDrill-v1.md) | `make kernel-gate-full` (A–E) before application work |
| **Application hardening** | [Application hardening plan v1](ApplicationHardeningPlan-v1.md) · [Application readiness](ApplicationReadiness-v1.md) | `make application-gate-full` (A–D) after kernel |
| **Composition & mesh** | [Composition & mesh plan v1](CompositionMeshHardeningPlan-v1.md) · [Readiness](CompositionMeshReadiness-v1.md) | `make composition-mesh-gate-full` (pipelines + mesh tasks) |
| **Ship readiness** | [Ship hardening plan v1](ShipHardeningPlan-v1.md) · [Ship readiness](ShipReadiness-v1.md) | `make ship-gate-full` (prod gate + ci verify + release) |
| **Ops & dogfood** | [Ops hardening plan v1](OpsHardeningPlan-v1.md) · [Ops readiness](OpsReadiness-v1.md) | `make ops-gate-full` (self-improvement + demo) |
| **Security & trust** | [Security hardening plan v1](SecurityHardeningPlan-v1.md) · [Security readiness](SecurityReadiness-v1.md) | `make security-gate-full` (auth + supply chain + air-gapped) |
| **Release candidate** | [RC hardening plan v1](RCHardeningPlan-v1.md) · [RC readiness](RCReadiness-v1.md) | `make rc-gate-full` (bundle + GH workflow evidence) |
| **Performance** | [Perf hardening plan v1](PerfHardeningPlan-v1.md) · [Perf readiness](PerfReadiness-v1.md) | `make perf-gate-full` |
| **Compatibility** | [Compat hardening plan v1](CompatHardeningPlan-v1.md) · [Compat readiness](CompatReadiness-v1.md) | `make compat-gate-full` |
| **Disaster recovery** | [DR hardening plan v1](DRHardeningPlan-v1.md) · [DR readiness](DRReadiness-v1.md) | `make dr-gate-full` |
| **Post-RC waterproofing** | [Rollback drill](RollbackDrill-v1.md) · [Release sign-off](ReleaseSignOff-v1.md) | `make waterproofing-gate-full` |
| **Full stack** | — | `make ashlar-ready-gate` (`ASHLAR_READY_SKIP_DOCKER=1` for fast local) |
| Release & promotion | [Release and promotion](ReleaseAndPromotion.md) | Reproducible versions, artifacts, rollback, one promotion path |
| Security & trust | [Security and trust](SecurityAndTrust.md) | Secrets, threat model, supply chain, dependency risk |
| Operations | [Operations and observability](OperationsAndObservability.md) | SLOs, alerting, health, capacity, runbooks |
| Data & compliance | [Data privacy and compliance](DataPrivacyAndCompliance.md) | Classification, retention, encryption, control mapping |
| Reliability | [Reliability and chaos](ReliabilityAndChaos.md) | Failure modes, limits, idempotency, drills |
| Testing & CI | [Testing strategy pivot v1](../architecture/TestingStrategyPivot-v1.md) · [Testing and quality gates](TestingAndQualityGates.md) · [Coverage gates v1](CoverageGates-v1.md) | Layered proof; line floors as enforced (Domain 100%, Infra 80%, App 67%) |
| Operators | [Operator deployment](OperatorDeployment.md) | Production install, upgrades, backups pointer |
| Audience fit | [Catalog by deployment type](CatalogByDeploymentType.md) | SMB vs enterprise vs SaaS: which items matter most |

## Evidence rule for readiness records (added 2026-09-30)

> **Read this before trusting any PASS in this folder.** An audit on 2026-09-30 re-checked every
> PASS in the `*Readiness-v1.md` sheets and in [Release sign-off v1](ReleaseSignOff-v1.md) against
> what was true on each sheet's own date and SHA. Some rows held up. Others rested on a local run
> that left no artifact, on a tier no CI workflow had run by then, or on a `dotnet test` step whose
> filter selected no tests. `dotnet test` exits 0 when nothing matches, so a green step does not
> show that a test ran. Those rows now carry a dated `[corrected 2026-09-30: …]` note in place.
> Nothing was deleted or re-ticked.

From 2026-09-30 on, these rules apply:

- A row may read **PASS** only if it names a GitHub Actions run id or check run **on the SHA in
  the sheet's header**. For a `dotnet test` leg, the row also gives the executed count from that
  run's log.
- Any other row reads **NOT RUN**, or **CARRIED FORWARD** with both SHAs: the one the cited run
  tested, and the one the sheet is for. This includes a run on a different commit with the same
  tree, such as a pull request head and the commit it merged as.
- A readiness PASS must cite a `Readiness summary` run whose verdict is `verified`. A verdict of
  `partial`, `not-verified` or `failed` does not count. #685 added these verdicts; each run shows
  its verdict in the step summary and job outputs.
- A local run counts as evidence only if its output is committed or attached beside the row.
  `.ashlar/` (`.nexo/` before the rename) is gitignored, so a report left there does not count.
- Nothing enforces this rule yet. No test or gate reads these tables. `scripts/rc-gate-tier-e.sh`,
  which `make waterproofing-gate-full` runs, reports "sign-off: checked items found" for any
  ticked line naming Product or Engineering. It still says that about
  [Release sign-off v1](ReleaseSignOff-v1.md), whose Engineering tick is now annotated
  UNSUPPORTED-AT-THE-TIME. Apply the rule by review until a check reads it.

The corrections use four labels:

- **SUPPORTED** names the run. If the run tested a different commit from the sheet's SHA, the
  note says "carried forward", gives both SHAs as tested → sheet, and says why the run still
  covers the row. If the two commits have the same tree, the note names the tree hash. If the
  trees differ, the note says what differs, or that the files the row depends on are
  byte-identical. A run that finished after the sheet was signed says "after sign-off".
- **UNSUPPORTED-AT-THE-TIME** means the cited command could not have shown what the row claims,
  even at the sheet's own SHA.
- **LATER-DECAYED** means the row was true then and is not now. The note gives the date and cause.
- **UNEVIDENCED** means a local run that nobody can inspect now. The tier's own workflow did not
  run it, and no run found on the sheet's SHA or its parent ran the same commands. It is not
  disproven, but under this rule the row reads NOT RUN.

## How to use this

1. Read [Catalog by deployment type](CatalogByDeploymentType.md) and pick your lane.
2. Open each linked guide for your lane and work through the **checklists** (checkboxes are Markdown `- [ ]` so you can track in a fork or internal doc).
3. Link completed runbooks and decisions from your internal wiki or ticket system; keep this repo as the **canonical structure**, not necessarily every secret or customer name.

## Existing repo docs

- Configuration: `docs/Configuration.md`
- Architecture / trust overview: `docs/architecture/README.md`
- Self-hosted agent server: `docs/SelfHostedAgentServer.md`
- Contributing / local CI: `CONTRIBUTING.md`
