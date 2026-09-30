# Kernel Readiness v1

**Status: KERNEL READY FOR APPLICATIONS** (Tiers A–E automated gates green, 2026-05-19)
[corrected 2026-09-30: this sheet names no SHA, and no commit held these gates on 2026-05-19. CI
evidence exists for Tier A, for the mesh lab, and for parts of B–E. The Kernel Gate workflow never
ran Tiers B–E. See [Corrections](#corrections-2026-09-30).]

**Plan:** [Kernel Hardening Plan v1](KernelHardeningPlan-v1.md) · **Quarterly ops:** [Kernel Chaos Drill v1](KernelChaosDrill-v1.md)

## One command

```bash
make kernel-gate-full   # A + B + C + D + E (~10–15 min first run; Docker for E)
```

## Gate record

| Date | Gate | Result |
|------|------|--------|
| 2026-05-19 | A–E local | **PASS** [corrected 2026-09-30: no single run backs this. Evidence per tier is in the table below.] |
| 2026-05-19 | mesh-lab-e2e | **PASS** [corrected 2026-09-30: SUPPORTED, carried forward 7457e6c5 → 0ecb5bfc. Mesh virtual lab gate run 26129468841 (2026-05-19) started the lab and passed mesh-lab-verify.sh and mesh-lab-verify-deep.sh.] |

## Tier summary

| Tier | Focus | Command | Status |
|------|--------|---------|--------|
| A | DI contracts, profiles | `make kernel-gate` | **PASS** [corrected 2026-09-30: SUPPORTED on 0ecb5bfc by Kernel Gate run 26262716833 (2026-05-22, step Tier A). Not on 2026-05-19: one of its three suites was first committed in #110.] |
| B | CLI + LiteDB resume | `make kernel-gate-tier-b` | **PASS** [corrected 2026-09-30: SUPPORTED, carried forward 7457e6c5 → 0ecb5bfc. The Kernel Gate workflow never ran this tier, but Production Readiness Gate v1 run 26129468793 (2026-05-19) ran the same steps or a superset of them on three OSes.] |
| C | ProdStyle, transport, air-gapped | `make kernel-gate-tier-c` | **PASS** [corrected 2026-09-30: partly SUPPORTED, carried forward 7457e6c5 → 0ecb5bfc. The gRPC ProdStyle leg passed as gRPC transport gate run 26129468823 (7457e6c5, 2026-05-19). The ProdStyle, workflow-executor and air-gapped legs are UNEVIDENCED.] |
| D | NuGet consumer sample | `make kernel-gate-tier-d` | **PASS** [corrected 2026-09-30: partly SUPPORTED. The runtime-graph build is covered by run 26262716833 on 0ecb5bfc. The pack-list check is carried forward 7457e6c5 → 0ecb5bfc from run 26129468816. The consumer sample is UNEVIDENCED.] |
| E | OTel, perf, Compose dry run | `make kernel-gate-tier-e` | **PASS** [corrected 2026-09-30: partly SUPPORTED, carried forward ee8befa7 → 0ecb5bfc (PR #110 head, identical tree f1e22dfd). The portal dry run passed in Prod dry run (Compose) run 26262666791. The OTel test and 3 perf tests are UNEVIDENCED.] |

## Tier E detail

- OpenTelemetry registration test
- 3 orchestration performance tests
- `prod-dry-run.sh --portal`: `/health` + `/api/status` on published API image (uses `linux/amd64` on ARM hosts)

## Next: application layer

After kernel sign-off, run [Application Readiness v1](ApplicationReadiness-v1.md):

```bash
make application-gate-full
```

## Before NuGet publish

```bash
make release-preflight VERSION=x.y.z
```

Post-publish: `nuget-consumer-verify.yml` against released version on nuget.org.

## Application work

You may build on `application/src/` when `make kernel-gate-full` is green. Re-run after kernel (`src/`, `Ashlar.Hosting`) changes.

## Sign-off

- [x] Tiers A–E automated (2026-05-19) [corrected 2026-09-30: the Kernel Gate workflow ran Tier A
      only, from 2026-05-22 on. For B–E, see the tier table and [Corrections](#corrections-2026-09-30).]
- [ ] Quarterly chaos drill checklist completed
- [ ] Post-publish NuGet verify on release tag

## Corrections (2026-09-30)

**Evidence rule from 2026-09-30.** A row may read PASS only if it names a run id or check run on
the SHA in this sheet's header. Any other row reads NOT RUN, or CARRIED FORWARD with both SHAs. A
readiness PASS must cite a `Readiness summary` run whose verdict is `verified`. The full rule and
the four labels are in the [production-readiness README](README.md#evidence-rule-for-readiness-records-added-2026-09-30).

**Which SHA.** This sheet names none. Its rows were committed in `0ecb5bfc` (#110) at
2026-05-22 01:17 UTC. Their tree is `f1e22dfd`, the same tree as PR #110's head `ee8befa7`. The
gate scripts these rows cite first appear in `43d5fca9`, PR #110's first commit, authored
2026-05-22 00:45 UTC. None of the cited make targets exists in the Makefile at `7457e6c5`, master
on 2026-05-19. A 2026-05-19 run was therefore of an uncommitted tree, and no SHA identifies it.
Outside docs, gate scripts, workflows and the Makefile, `7457e6c5` and `0ecb5bfc` differ only in
global.json, two helper scripts and the new test file KernelPhaseResolutionTests.cs. That is the
basis for carrying runs forward from `7457e6c5`.

**Why test counts are missing.** GitHub keeps logs for 90 days. Every May log now returns HTTP 410,
so only the run and step conclusions below survive. Where a count is given, it is a static count of
xUnit test methods (not theory rows) that the tier's filter selects in the net8.0 build at
`0ecb5bfc`. That check was calibrated against security-gate run 31982500018 (2026-08-17) and
matched the zero or non-zero result of all four legs in that run.

| Row | Label | Evidence |
|-----|-------|----------|
| Tier A | SUPPORTED on `0ecb5bfc` | Kernel Gate run 26262716833 (push, `0ecb5bfc`) and run 26262666837 (PR head `ee8befa7`, same tree), 2026-05-22, step "Tier A" success. The filters select 22 + 14 test methods. KernelPhaseResolutionTests was added in #110, so this tier as written could not have run on a committed tree on 2026-05-19. |
| Tier B | SUPPORTED, carried forward `7457e6c5` → `0ecb5bfc` | Kernel Gate's Tier B step runs only on `workflow_dispatch`, and none of its 908 runs is a dispatch. Production Readiness Gate v1 run 26129468793 (push, `7457e6c5`, 2026-05-19, ubuntu, macOS and windows jobs all success) ran the same three builds, the pipeline tests, the same CLI validate/run/fallback and the same LiteDB cross-process resume. Its filter `FullyQualifiedName~Pipelines` is a superset of this tier's. |
| Tier C | Partly SUPPORTED, carried forward `7457e6c5` → `0ecb5bfc`; rest UNEVIDENCED | The gRPC transport gate run 26129468823 (`7457e6c5`, 2026-05-19) ran this tier's `Category=ProdStyle` transport leg. The ProdStyle infrastructure leg (68 methods), workflow executor (12) and air-gapped (5) legs have no CI run that was found. |
| Tier D | Partly SUPPORTED (build on `0ecb5bfc`; pack-list check carried forward `7457e6c5` → `0ecb5bfc`); rest UNEVIDENCED | Run 26262716833 above builds the runtime solution. Pack hosting graph alignment run 26129468816 (`7457e6c5`, 2026-05-19) ran the pack-list check. No CI run of the StableSdkHostSample consumer was found. |
| Tier E | Partly SUPPORTED, carried forward `ee8befa7` → `0ecb5bfc` (identical tree `f1e22dfd`); rest UNEVIDENCED | Prod dry run (Compose) run 26262666791 (PR head `ee8befa7`, 2026-05-22) passed its "portal stack" job, which runs the same prod-dry-run script. The OpenTelemetry test (1 method) and the orchestration performance tests (3 methods, which matches "3" in Tier E detail) have no CI run that was found. |
| mesh-lab-e2e | SUPPORTED, carried forward `7457e6c5` → `0ecb5bfc` | Mesh virtual lab gate run 26129468841 (push, `7457e6c5`, 2026-05-19 22:35 UTC) started the lab with the workers profile, then passed mesh-lab-verify.sh and mesh-lab-verify-deep.sh. docker-compose.mesh-lab.yml, run-mesh-lab-e2e.sh and both verify scripts are byte-identical at `0ecb5bfc`. |

These notes record only what was true for this sheet in May. They say nothing about the kernel
today. To re-sign the sheet, run `make kernel-gate-full` on a named SHA and record a run id there.
