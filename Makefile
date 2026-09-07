.PHONY: build build-core build-demos prod-dry-run prod-dry-run-agent-server restore-core test test-prod-style test-framework-prod-first test-prime-time test-prime-time-full test-cross-platform test-portable test-multi-env test-all-platforms test-all-platforms-ephemeral ci-verify meai-pipeline-gate kernel-gate kernel-gate-tier-b kernel-gate-tier-c kernel-gate-tier-d kernel-gate-tier-e kernel-gate-full application-gate application-gate-tier-a application-gate-tier-b application-gate-tier-c application-gate-tier-d application-gate-full composition-mesh-gate composition-mesh-gate-tier-a composition-mesh-gate-tier-b composition-mesh-gate-tier-c composition-mesh-gate-tier-d composition-mesh-gate-full dependency-boundary-gate ship-gate ship-gate-tier-a ship-gate-tier-b ship-gate-tier-c ship-gate-tier-d ship-gate-full ops-gate ops-gate-tier-a ops-gate-tier-b ops-gate-tier-c ops-gate-tier-d ops-gate-tier-e ops-gate-full security-gate security-gate-tier-a security-gate-tier-b security-gate-tier-c security-gate-tier-d security-gate-tier-e security-gate-full rc-gate rc-gate-tier-a rc-gate-tier-b rc-gate-tier-c rc-gate-tier-d rc-gate-tier-e rc-gate-full perf-gate perf-gate-tier-a perf-gate-tier-b perf-gate-tier-c perf-gate-tier-d perf-gate-full compat-gate compat-gate-tier-a compat-gate-tier-b compat-gate-tier-c compat-gate-full dr-gate dr-gate-tier-a dr-gate-tier-b dr-gate-tier-c dr-gate-full waterproofing-gate-full ashlar-ready-gate bootstrap-mesh-lab-env validate-safe review-summary clean-test-artifacts test-readiness-gate release-preflight release-gate release-dispatch release-staging verify-staging release-staging-and-verify verify-external-product-shape mesh-lab-e2e mesh-lab-e2e-workers mesh-lab-e2e-deep mesh-lab-e2e-stress mesh-lab-up mesh-lab-verify mesh-lab-verify-deep mesh-lab-verify-entitlements mesh-lab-verify-governance mesh-lab-verify-director-cli mesh-lab-verify-persistence mesh-lab-verify-network-negative mesh-lab-verify-post-stress mesh-lab-stress mesh-lab-down test-mesh-lab

# External product shape: packed Ashlar.* feed → authored brick + thin host + HTTP client (no repo refs).
verify-external-product-shape:
	bash scripts/verify-external-product-shape.sh

# Local checks before tagging a release (graph alignment + NuGet sample). Usage: make release-preflight VERSION=1.2.3
release-preflight:
	@test -n "$(VERSION)" || (echo "Set VERSION=1.2.3 (semver, no v prefix)"; exit 1)
	bash scripts/release-preflight-local.sh "$(VERSION)"

# Trigger Runtime Release Gate in CI (requires: gh auth login)
release-gate:
	gh workflow run "Runtime Release Gate" --ref $${ASHLAR_RELEASE_PREFLIGHT_REF:-master}

# Trigger full Release workflow (GHCR + NuGet). Requires: gh auth login. Usage: make release-dispatch VERSION=1.2.3 REF=master
release-dispatch:
	@test -n "$(VERSION)" || (echo "Set VERSION=1.2.3"; exit 1)
	gh workflow run Release --ref $${REF:-master} -f version="$(VERSION)" -f skip_multi_arch=false

# Staging-only release dispatch (guarded; refuses when NUGET_PUBLISH_MODE enables nuget.org).
release-staging:
	@test -n "$(VERSION)" || (echo "Set VERSION=x.y.z"; exit 1)
	DRY_RUN=$(DRY_RUN) ASHLAR_RELEASE_STAGING_REF=$${REF:-} bash scripts/release-staging.sh "$(VERSION)"

verify-staging:
	@test -n "$(VERSION)" || (echo "Set VERSION=x.y.z"; exit 1)
	bash scripts/verify-staging.sh "$(VERSION)"

release-staging-and-verify:
	@test -n "$(VERSION)" || (echo "Set VERSION=x.y.z"; exit 1)
	$(MAKE) release-staging VERSION="$(VERSION)" DRY_RUN="$(DRY_RUN)"
	@if [ "$(DRY_RUN)" != "1" ]; then $(MAKE) verify-staging VERSION="$(VERSION)"; fi

# All automated test projects in Ashlar.PrimeTime.slnf (seven Ashlar.Tests.* assemblies).
PRIME_TIME_SLNF := Ashlar.PrimeTime.slnf

# Build the solution (root holds several .sln/.slnf; a bare `dotnet build` fails with MSB1011)
build:
	dotnet build Ashlar.sln

# Restore/build a small slice (CLI + domain tests + infra tests) — avoids full Ashlar.sln workload requirements
restore-core:
	dotnet restore Ashlar.LocalDevCore.slnf

build-core:
	dotnet build Ashlar.LocalDevCore.slnf -v minimal

# Workload-free client samples (console, Blazor, Avalonia) — see docs/demos/README.md
build-demos:
	dotnet build Ashlar.Demos.sln -v minimal

# Production-shaped Compose dry run (portal or agent-server) — see docs/prod-dry-run.md
prod-dry-run:
	bash scripts/prod-dry-run.sh --portal

prod-dry-run-agent-server:
	bash scripts/prod-dry-run.sh --agent-server

# Production-like integration (Category=ProdStyle): Ashlar.Tests.Infrastructure only — real DI hosts / graphs.
# Run this before the full suite when validating framework behaviour locally or in CI-style gates.
test-prod-style:
	dotnet build src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -v minimal
	ASHLAR_ALLOW_MOCK=1 dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 --no-build \
	  --filter "Category=ProdStyle&FullyQualifiedName!~ForgeEndpointsTests&FullyQualifiedName!~FrameworkVirtualProdDemosTests" \
	  --blame-hang-timeout 120s --blame-hang-dump-type none

# Runs test-prod-style then the full LocalDevCore test slice (Domain + Infrastructure + CLI harness).
# ProdStyle runs once in test-prod-style; the second pass excludes Category=ProdStyle.
test-framework-prod-first: test-prod-style
	dotnet test Ashlar.LocalDevCore.slnf --no-build \
	  --filter "Category!=ProdStyle" \
	  --blame-hang-timeout 30s --blame-hang-dump-type none

# Prime-time gate: Category=ProdStyle across Ashlar.PrimeTime.slnf (all test assemblies).
test-prime-time:
	dotnet build $(PRIME_TIME_SLNF) -v minimal
	dotnet test $(PRIME_TIME_SLNF) --no-build \
	  --filter "Category=ProdStyle" \
	  --blame-hang-timeout 300s --blame-hang-dump-type none

# Full PrimeTime matrix after ProdStyle gate (ProdStyle excluded on this pass).
test-prime-time-full: test-prime-time
	dotnet test $(PRIME_TIME_SLNF) --no-build \
	  --filter "Category!=ProdStyle" \
	  --blame-hang-timeout 300s --blame-hang-dump-type none

# Run tests locally (blame-hang-timeout prevents indefinite freeze from hung tests)
# --blame-hang-dump-type none avoids 6GB+ hang dumps that accumulate in TestResults/
# ASHLAR_ALLOW_MOCK=1 matches CI so ProviderFactory / mock-provider tests pass on net10.0.
test:
	ASHLAR_ALLOW_MOCK=1 dotnet test Ashlar.sln --blame-hang-timeout 120s --blame-hang-dump-type none

# Run tests on all target platforms: local + Docker (ubuntu, alpine, debian).
# For native macOS/Windows/Linux use: make test-cross-platform (triggers CI).
test-all-platforms:
	@echo "=== Local (current OS) ==="
	dotnet build Ashlar.sln -v minimal
	dotnet test Ashlar.sln --no-build --verbosity minimal --blame-hang-timeout 30s --blame-hang-dump-type none
	@echo "=== Docker: Ubuntu 8.0 ==="
	docker build -f .docker/Dockerfile.test-caching --build-arg DOTNET_VERSION=8.0 -t ashlar-test-ubuntu:8.0 .
	mkdir -p test-results
	docker run --rm -v "$$(pwd)/test-results:/workspace/test-results" ashlar-test-ubuntu:8.0 \
		bash -c "dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --blame-hang-timeout 60s --filter 'FullyQualifiedName~BaseFrameworkSmokeTests' --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=ubuntu-8.0-base.trx' --results-directory /workspace/test-results"
	@echo "=== Docker: Alpine 8.0 ==="
	docker build -f .docker/Dockerfile.test-caching-alpine --build-arg DOTNET_VERSION=8.0 -t ashlar-test-alpine:8.0 .
	docker run --rm -v "$$(pwd)/test-results:/workspace/test-results" ashlar-test-alpine:8.0 \
		bash -c "dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --blame-hang-timeout 60s --filter 'FullyQualifiedName~BaseFrameworkSmokeTests' --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=alpine-8.0-base.trx' --results-directory /workspace/test-results"
	@echo "=== Docker: Debian 8.0 ==="
	docker build -f .docker/Dockerfile.test-caching-debian --build-arg DOTNET_VERSION=8.0 -t ashlar-test-debian:8.0 .
	docker run --rm -v "$$(pwd)/test-results:/workspace/test-results" ashlar-test-debian:8.0 \
		bash -c "dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --blame-hang-timeout 60s --filter 'FullyQualifiedName~BaseFrameworkSmokeTests' --logger 'console;verbosity=minimal' --logger 'trx;LogFileName=debian-8.0-base.trx' --results-directory /workspace/test-results"
	@echo "=== All target platforms (local + ubuntu + alpine + debian) completed ==="

# Ephemeral: run tests in containers with no volume mounts; results discarded when container is removed
test-all-platforms-ephemeral:
	@echo "=== Ephemeral multi-platform tests (no host artifacts) ==="
	dotnet build Ashlar.sln -v minimal
	dotnet run --project application/src/Ashlar.CLI -- test --platforms ubuntu alpine debian --ephemeral

# Run tests on all platforms (C#-driven; works on Windows, macOS, Linux, mobile)
test-all:
	dotnet run --project application/src/Ashlar.CLI -- test --platforms ubuntu alpine debian android ios windows

# Run tests on specific platform
test-platform:
	dotnet run --project application/src/Ashlar.CLI -- test --platforms $(PLATFORM)

# Trigger cross-platform tests in CI (Mac, Windows, Linux from one place)
# Requires: gh auth login. Workflows are manual-first — see .github/workflows/README.md
# Usage: make test-cross-platform [SCOPE=smoke|persistence|full]
test-cross-platform:
	gh workflow run "Cross-Platform Tests" --ref master -f scope=$${SCOPE:-smoke}

# Trigger full platform readiness gate: setup + discovery + dry-run on all target platforms.
# Runs on Linux, macOS, Windows (native) + Ubuntu, Alpine, Debian (container) + Docker CLI image.
# Requires: gh auth login. Manual-first — see .github/workflows/README.md
test-readiness-gate:
	gh workflow run "Full Platform Readiness Gate" --ref master

# Portable tests: C#-driven (replaces scripts/portable-test.sh). Works on Windows, macOS, Linux, mobile.
# Usage: make test-portable [SCOPE=persistence|smoke|all]. Use --list to see targets: dotnet run --project application/src/Ashlar.CLI -- test portable --list
test-portable:
	dotnet run --project application/src/Ashlar.CLI -- test portable --scope $${SCOPE:-persistence}

# Multi-env framework/caching/persistence tests (C#-driven; replaces test-framework-multi-env.sh etc.)
test-multi-env:
	dotnet run --project application/src/Ashlar.CLI -- test multi-env --suite framework --all

# Air-gapped multi-env: run containers with --network none (no egress). Validates air-gapped deployment.
test-multi-env-no-network:
	dotnet run --project application/src/Ashlar.CLI -- test multi-env --suite framework --all --no-network

# Air-gapped CI validation: framework + adaptation suites with zero network egress (ubuntu-8.0).
test-airgapped:
	dotnet run --project application/src/Ashlar.CLI -- test multi-env --suite framework --env ubuntu-8.0 --no-network
	dotnet run --project application/src/Ashlar.CLI -- test multi-env --suite adaptation --env ubuntu-8.0 --no-network

# Linear adaptation tests across all Docker environments
test-adaptation-all-envs:
	dotnet run --project application/src/Ashlar.CLI -- test multi-env --suite adaptation --all

# CI verification: build + checks (C#-driven; replaces scripts/ci-verify.sh)
ci-verify:
	dotnet run --project application/src/Ashlar.CLI -- ci verify

# Pre-application kernel gate: runtime graph build + hosting resolution matrix + pipeline tests.
# Optional: KERNEL_GATE_MESH=1 (Docker mesh-lab-verify), KERNEL_GATE_PRODSTYLE=1 (full ProdStyle slice).
# Coverlet floors as enforced by scripts/ci/kernel-coverage-gate.sh: Domain 100%, Infrastructure 80%, Core.Application 67% (see docs/production-readiness/CoverageGates-v1.md).
kernel-coverage-gate:
	bash scripts/ci/kernel-coverage-gate.sh

# PR policy: gap freeze, ProdStyle wiring (see docs/architecture/TestingStrategyPivot-v1.md).
testing-strategy-gate:
	bash scripts/ci/pr-testing-strategy-gate.sh origin/master

dependency-boundary-gate:
	bash scripts/dependency-boundary-gate.sh

# MEAI governed pipeline + VectorData RAG architecture tests (net8).
meai-pipeline-gate:
	dotnet test src/Ashlar.Tests.AI.Pipeline/Ashlar.Tests.AI.Pipeline.csproj -f net8.0 -c Release --nologo \
	  --blame-hang-timeout 120s --blame-hang-dump-type none

kernel-gate:
	dotnet build Ashlar.Runtime.sln -v minimal
	dotnet build src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -v minimal
	ASHLAR_ALLOW_MOCK=1 dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 --no-build \
	  --filter "FullyQualifiedName~KernelPhaseResolutionTests|FullyQualifiedName~HostingDeploymentProfileTests|FullyQualifiedName~HostingE2ESmokeTests" \
	  --blame-hang-timeout 120s --blame-hang-dump-type none
	ASHLAR_ALLOW_MOCK=1 dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 --no-build \
	  --filter "FullyQualifiedName~PipelineTemplateValidatorTests|FullyQualifiedName~PipelineLifecycleE2ETests" \
	  --blame-hang-timeout 120s --blame-hang-dump-type none
	$(MAKE) meai-pipeline-gate
	@if [ "$${KERNEL_GATE_PRODSTYLE:-0}" = "1" ]; then $(MAKE) test-prod-style; fi
	@if [ "$${KERNEL_GATE_MESH:-0}" = "1" ]; then $(MAKE) mesh-lab-verify; fi

# Tier B: CLI pipeline ops + cross-process LiteDB resume (see scripts/kernel-gate-tier-b.sh).
kernel-gate-tier-b:
	bash scripts/kernel-gate-tier-b.sh

# Tier C: ProdStyle + workflow + transport + air-gapped profile (+ mesh if .env.mesh-lab).
kernel-gate-tier-c:
	bash scripts/kernel-gate-tier-c.sh

bootstrap-mesh-lab-env:
	bash scripts/bootstrap-mesh-lab-env.sh

# Tier D: NuGet pack alignment + StableSdkHostSample consumer (local feed).
kernel-gate-tier-d:
	bash scripts/kernel-gate-tier-d.sh

# Tier E: observability + perf-scoped tests + prod Compose dry run (Docker).
kernel-gate-tier-e:
	bash scripts/kernel-gate-tier-e.sh

# Tier A–E (skip tiers with KERNEL_GATE_SKIP_TIER_*=1).
kernel-gate-full: kernel-gate kernel-gate-tier-b
	@if [ "$${KERNEL_GATE_SKIP_TIER_C:-0}" != "1" ]; then $(MAKE) kernel-gate-tier-c; fi
	@if [ "$${KERNEL_GATE_SKIP_TIER_D:-0}" != "1" ]; then $(MAKE) kernel-gate-tier-d; fi
	@if [ "$${KERNEL_GATE_SKIP_TIER_E:-0}" != "1" ]; then $(MAKE) kernel-gate-tier-e; fi

# Application gate (after kernel): product solution, CLI, API HTTP, agent-server dry run.
# Prerequisite: make kernel-gate-full (or APPLICATION_GATE_REQUIRE_KERNEL=1 on application-gate-full).
application-gate-tier-a:
	bash scripts/application-gate-tier-a.sh

application-gate-tier-b:
	bash scripts/application-gate-tier-b.sh

application-gate-tier-c:
	bash scripts/application-gate-tier-c.sh

application-gate-tier-d:
	bash scripts/application-gate-tier-d.sh

application-gate: application-gate-tier-a

application-gate-full:
	@if [ "$${APPLICATION_GATE_REQUIRE_KERNEL:-0}" = "1" ]; then $(MAKE) kernel-gate-full; fi
	APPLICATION_GATE_SKIP_KERNEL=1 $(MAKE) application-gate-tier-a
	$(MAKE) application-gate-tier-b
	$(MAKE) application-gate-tier-c
	@if [ "$${APPLICATION_GATE_SKIP_TIER_D:-0}" != "1" ]; then $(MAKE) application-gate-tier-d; fi

# Composition + mesh gate: pipeline templates/orchestration + clustered mesh tasks.
# Prerequisite: make application-gate-full (or kernel-gate-full minimum).
composition-mesh-gate-tier-a:
	bash scripts/composition-mesh-gate-tier-a.sh

composition-mesh-gate-tier-b:
	bash scripts/composition-mesh-gate-tier-b.sh

composition-mesh-gate-tier-c:
	bash scripts/composition-mesh-gate-tier-c.sh

composition-mesh-gate-tier-d:
	bash scripts/composition-mesh-gate-tier-d.sh

composition-mesh-gate: composition-mesh-gate-tier-a composition-mesh-gate-tier-b composition-mesh-gate-tier-c

composition-mesh-gate-full: composition-mesh-gate
	@if [ "$${COMPOSITION_MESH_GATE_STRESS:-0}" = "1" ]; then \
	  MESH_LAB_E2E_WORKERS=1 MESH_LAB_VERIFY_DEEP=1 MESH_LAB_RUN_STRESS=1 bash scripts/run-mesh-lab-e2e.sh; \
	elif [ "$${COMPOSITION_MESH_GATE_SKIP_TIER_D:-0}" != "1" ]; then \
	  $(MAKE) composition-mesh-gate-tier-d; \
	fi

# Ship gate: production readiness CLI + ci verify + release preflight + release bundle.
ship-gate-tier-a:
	bash scripts/ship-gate-tier-a.sh

ship-gate-tier-b:
	bash scripts/ship-gate-tier-b.sh

ship-gate-tier-c:
	bash scripts/ship-gate-tier-c.sh

ship-gate-tier-d:
	bash scripts/ship-gate-tier-d.sh

ship-gate: ship-gate-tier-a

ship-gate-full:
	@if [ "$${SHIP_GATE_SKIP_PRIOR:-0}" != "1" ]; then \
	  COMPOSITION_MESH_GATE_SKIP_TIER_D=1 $(MAKE) composition-mesh-gate; \
	fi
	$(MAKE) ship-gate-tier-a
	@if [ "$${SHIP_GATE_SKIP_TIER_B:-0}" != "1" ]; then $(MAKE) ship-gate-tier-b; fi
	@if [ "$${SHIP_GATE_SKIP_TIER_C:-0}" != "1" ]; then $(MAKE) ship-gate-tier-c; fi
	@if [ "$${SHIP_GATE_SKIP_TIER_D:-0}" != "1" ]; then $(MAKE) ship-gate-tier-d; fi

# Ops gate: dogfood self-improvement + optional mesh chaos + oh-shit demo.
ops-gate-tier-a:
	bash scripts/ops-gate-tier-a.sh

ops-gate-tier-b:
	bash scripts/ops-gate-tier-b.sh

ops-gate-tier-c:
	bash scripts/ops-gate-tier-c.sh

ops-gate-tier-d:
	bash scripts/ops-gate-tier-d.sh

ops-gate-tier-e:
	bash scripts/ops-gate-tier-e.sh

ops-gate: ops-gate-tier-a ops-gate-tier-b ops-gate-tier-c ops-gate-tier-e

# Security & trust gate: trust boundary, API auth, mesh security, supply chain, air-gapped.
security-gate-tier-a:
	bash scripts/security-gate-tier-a.sh

security-gate-tier-b:
	bash scripts/security-gate-tier-b.sh

security-gate-tier-c:
	bash scripts/security-gate-tier-c.sh

security-gate-tier-d:
	bash scripts/security-gate-tier-d.sh

security-gate-tier-e:
	bash scripts/security-gate-tier-e.sh

security-gate: security-gate-tier-a security-gate-tier-b security-gate-tier-c

security-gate-full:
	@if [ "$${SECURITY_GATE_SKIP_PRIOR:-0}" != "1" ]; then SHIP_GATE_SKIP_PRIOR=1 $(MAKE) ship-gate-full; fi
	$(MAKE) security-gate-tier-a
	$(MAKE) security-gate-tier-b
	$(MAKE) security-gate-tier-c
	@if [ "$${SECURITY_GATE_SKIP_TIER_D:-0}" != "1" ]; then $(MAKE) security-gate-tier-d; fi
	@if [ "$${SECURITY_GATE_SKIP_TIER_E:-0}" != "1" ]; then $(MAKE) security-gate-tier-e; fi

# RC gate: release candidate (ship bundle + evidence + GitHub workflows).
rc-gate-tier-a:
	bash scripts/rc-gate-tier-a.sh

rc-gate-tier-b:
	bash scripts/rc-gate-tier-b.sh

rc-gate-tier-c:
	bash scripts/rc-gate-tier-c.sh

rc-gate-tier-d:
	bash scripts/rc-gate-tier-d.sh

rc-gate: rc-gate-tier-b rc-gate-tier-c rc-gate-tier-d

rc-gate-full:
	bash scripts/rc-gate.sh

rc-gate-tier-e:
	bash scripts/rc-gate-tier-e.sh

# Perf gate: regression backstop (after RC).
perf-gate-tier-a:
	bash scripts/perf-gate-tier-a.sh

perf-gate-tier-b:
	bash scripts/perf-gate-tier-b.sh

perf-gate-tier-c:
	bash scripts/perf-gate-tier-c.sh

perf-gate-tier-d:
	bash scripts/perf-gate-tier-d.sh

perf-gate: perf-gate-tier-a perf-gate-tier-b perf-gate-tier-c

perf-gate-full:
	bash scripts/perf-gate.sh

# Compat gate: migration + CLI durability + config/doctor.
compat-gate-tier-a:
	bash scripts/compat-gate-tier-a.sh

compat-gate-tier-b:
	bash scripts/compat-gate-tier-b.sh

compat-gate-tier-c:
	bash scripts/compat-gate-tier-c.sh

compat-gate: compat-gate-tier-a compat-gate-tier-b compat-gate-tier-c

compat-gate-full:
	bash scripts/compat-gate.sh

# DR gate: backup/restore for pipeline + knowledge + mesh.
dr-gate-tier-a:
	bash scripts/dr-gate-tier-a.sh

dr-gate-tier-b:
	bash scripts/dr-gate-tier-b.sh

dr-gate-tier-c:
	bash scripts/dr-gate-tier-c.sh

dr-gate: dr-gate-tier-a dr-gate-tier-b dr-gate-tier-c

dr-gate-full:
	bash scripts/dr-gate.sh

# Post-RC waterproofing: perf → compat → DR → RC policy.
waterproofing-gate-full:
	bash scripts/waterproofing-gate.sh

ops-gate-full:
	@if [ "$${OPS_GATE_SKIP_PRIOR:-0}" != "1" ]; then SHIP_GATE_SKIP_PRIOR=1 $(MAKE) ship-gate-full; fi
	$(MAKE) ops-gate-tier-a
	$(MAKE) ops-gate-tier-b
	$(MAKE) ops-gate-tier-c
	@if [ "$${OPS_GATE_SKIP_TIER_D:-0}" != "1" ]; then $(MAKE) ops-gate-tier-d; fi
	$(MAKE) ops-gate-tier-e

# Meta gate: full readiness stack (skip Docker tiers with ASHLAR_READY_SKIP_DOCKER=1).
ashlar-ready-gate:
	bash scripts/ashlar-ready-gate.sh

# Safe validation: sequential, minimal memory. Run from external terminal to avoid Cursor memory explosion.
# Equivalent to ci-verify but via shell script; use when ci-verify causes high memory usage.
validate-safe:
	@bash scripts/validate-safe.sh

# Dogfood uses the repo's dev/test container. The .NET SDK lives there — do not
# install one on the host. scripts/run-in-devcontainer.sh is a no-op inside that
# container, so nested make recipes do not start a second Docker.
DEVBOX := bash scripts/run-in-devcontainer.sh
DOGFOOD_INFRA := src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj

# Dogfood Block 1: verify observation pipeline watches Ashlar's own dev workflow
dogfood-block1:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock1Tests" --no-build -v minimal'

# Dogfood Block 2: verify static analyzer runs against Block 1 (Observation) code
dogfood-block2:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock2Tests" --no-build -v minimal'

# Dogfood Block 3: adaptation engine decomposes/recompiles Ashlar brick
dogfood-block3:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock3Tests" --no-build -v minimal'

# Dogfood Block 4: promote Ashlar fix via inheritance
dogfood-block4:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock4Tests" --no-build -v minimal'

# Dogfood Block 5: autonomy controls on Ashlar dev workflow
dogfood-block5:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock5Tests" --no-build -v minimal'

# Dogfood Block 6: SelfContextAssembler answers 24h question
dogfood-block6:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock6Tests" --no-build -v minimal'

# Dogfood Block 7: Composition engine composes for Ashlar problem
dogfood-block7:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock7Tests" --no-build -v minimal'

# Dogfood Block 8: Parallel test matrix against Ashlar tests
dogfood-block8:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock8Tests" --no-build -v minimal'

# Phase D: Composition-driven testing (Block 7–8)
dogfood-block8-composed:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock8ComposedTests" --no-build -v minimal'

# Dogfood Block 9: Instance mesh discover/advertise
dogfood-block9:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock9Tests" --no-build -v minimal'

# Phase E: Local IPC mesh - two instances share capability
dogfood-block9-ipc:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodBlock9LocalIpcTests" --no-build -v minimal'

# Dogfood Blocks 1–6 (Phase C validation) — one container for the whole phase
dogfood-phase-c:
	$(DEVBOX) bash -lc '$(MAKE) dogfood-block1 && $(MAKE) dogfood-block2 && $(MAKE) dogfood-block3 && $(MAKE) dogfood-block4 && $(MAKE) dogfood-block5 && $(MAKE) dogfood-block6'

# Dogfood Blocks 7–9 (Phase D+E validation)
dogfood-phase-de:
	$(DEVBOX) bash -lc '$(MAKE) dogfood-block7 && $(MAKE) dogfood-block8 && $(MAKE) dogfood-block9'

# Dogfood Phase F: closed-loop improve on Ashlar
dogfood-closedloop:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodClosedLoopTests" --no-build -v minimal'

# Phase F: Continuous self-improvement loop (changelog, test failure store)
dogfood-phasef:
	$(DEVBOX) bash -lc 'dotnet build $(DOGFOOD_INFRA) -v minimal && dotnet test $(DOGFOOD_INFRA) --filter "FullyQualifiedName~DogfoodPhaseFTests" --no-build -v minimal'

# Automated dogfood campaign: specialists report to the release manager
dogfood-campaign:
	$(DEVBOX) bash scripts/run-dogfood-campaign.sh

dogfood-campaign-full:
	$(DEVBOX) bash scripts/run-dogfood-campaign.sh --full

# All dogfood blocks (1–9) + Phase F + campaign — one container for the lot
dogfood-all:
	$(DEVBOX) bash -lc '$(MAKE) dogfood-phase-c && $(MAKE) dogfood-phase-de && $(MAKE) dogfood-closedloop && $(MAKE) dogfood-phasef && $(MAKE) dogfood-campaign'

# Review summary Markdown from JSON (C#-driven; replaces scripts/review-summary-md.sh)
review-summary:
	dotnet run --project application/src/Ashlar.CLI -- review summary

# Mutation testing: validates tests catch deliberate bugs. Install: dotnet tool install -g dotnet-stryker
mutation-test:
	@bash scripts/run-mutation-tests.sh
	@echo "Open ./StrykerOutput/*/reports/mutation-report.html to review"

# Remove test artifacts: hang dumps (~6GB each), .trx, coverage, per-run TestResults dirs.
# Run after tests to reclaim disk space. Safe to run anytime.
clean-test-artifacts:
	@echo "Cleaning test artifacts..."
	@find src -type d -name "TestResults" -exec rm -rf {} + 2>/dev/null; true
	@rm -rf test-results
	@echo "Done."

# Pack NuGet library packages (Ashlar.Hosting, Ashlar.CLI tool)
pack:
	dotnet pack src/Ashlar.Hosting/Ashlar.Hosting.csproj -c Release -o dist/nuget
	dotnet pack application/src/Ashlar.CLI/Ashlar.CLI.csproj -c Release -o dist/nuget

# ── Mesh virtual lab (Docker bridge network; automated HTTP checks) ────────────
# Full cycle: compose up → scripts/mesh-lab-verify.sh → compose down -v.
# Requires Docker running. Optional: COMPOSE_PROJECT_NAME=… mesh-lab-e2e
mesh-lab-e2e:
	bash scripts/run-mesh-lab-e2e.sh

# Same as mesh-lab-e2e but starts the Compose `workers` profile (Basic + API key paths on worker).
mesh-lab-e2e-workers:
	MESH_LAB_E2E_WORKERS=1 bash scripts/run-mesh-lab-e2e.sh

# Workers profile + standard verify + mesh-lab-verify-deep (checkpoint / migrate / reschedule).
mesh-lab-e2e-deep:
	MESH_LAB_E2E_WORKERS=1 MESH_LAB_VERIFY_DEEP=1 bash scripts/run-mesh-lab-e2e.sh

# Full lab gate + deep + worker stress ramp (scale replicas + parallel /health bursts).
mesh-lab-e2e-stress:
	MESH_LAB_E2E_WORKERS=1 MESH_LAB_VERIFY_DEEP=1 MESH_LAB_RUN_STRESS=1 bash scripts/run-mesh-lab-e2e.sh

# HTTPS director via Caddy + scripts/mesh-lab-tls-certs.sh — see deploy/compose/docker-compose.mesh-lab-tls.override.yml
mesh-lab-e2e-tls:
	bash scripts/run-mesh-lab-e2e-tls.sh

# Long-lived lab using gitignored .env.mesh-lab (copy docs/config/mesh-lab.env.example).
# Optional: MESH_LAB_WORKERS=1 make mesh-lab-up  →  includes --profile workers
mesh-lab-up:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab — cp docs/config/mesh-lab.env.example .env.mesh-lab && edit secrets"; exit 1)
ifeq ($(strip $(MESH_LAB_WORKERS)),1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local docker compose --profile workers -f deploy/compose/docker-compose.mesh-lab.yml --env-file .env.mesh-lab up -d --build
else
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local docker compose -f deploy/compose/docker-compose.mesh-lab.yml --env-file .env.mesh-lab up -d --build
endif

mesh-lab-verify:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-verify.sh .env.mesh-lab

mesh-lab-verify-deep:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-verify-deep.sh .env.mesh-lab

mesh-lab-verify-entitlements:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-verify-entitlements.sh .env.mesh-lab

mesh-lab-verify-governance:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-verify-governance.sh .env.mesh-lab

mesh-lab-verify-director-cli:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-verify-director-cli.sh .env.mesh-lab

mesh-lab-verify-persistence:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-verify-persistence.sh .env.mesh-lab

mesh-lab-verify-network-negative:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-verify-network-negative.sh .env.mesh-lab

mesh-lab-verify-tls:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_tls_local \
		docker compose -f deploy/compose/docker-compose.mesh-lab.yml -f deploy/compose/docker-compose.mesh-lab-tls.override.yml --env-file .env.mesh-lab up -d
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} ./scripts/mesh-lab-verify-tls.sh .env.mesh-lab

mesh-lab-verify-post-stress:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-verify-post-stress.sh .env.mesh-lab

# Requires lab up with workers: MESH_LAB_WORKERS=1 make mesh-lab-up
mesh-lab-stress:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local ./scripts/mesh-lab-stress-ramp.sh .env.mesh-lab

mesh-lab-down:
	@test -f .env.mesh-lab || (echo "Missing .env.mesh-lab"; exit 1)
	DOCKER_DEFAULT_PLATFORM=$${DOCKER_DEFAULT_PLATFORM:-linux/amd64} COMPOSE_PROJECT_NAME=ashlar_mesh_lab_local docker compose --profile workers -f deploy/compose/docker-compose.mesh-lab.yml --env-file .env.mesh-lab down -v

# Optional dotnet gate mirroring mesh-lab-gate (compose + mesh-lab-verify*.sh). Requires Docker + python3.
test-mesh-lab:
	ASHLAR_RUN_MESH_LAB=1 dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj -f net8.0 \
	  --filter "Category=MeshLab" \
	  --blame-hang-timeout 2700s --blame-hang-dump-type none

# Build CLI Docker image (linux/amd64 for portability)
docker-cli:
	docker build --platform linux/amd64 -f .docker/Dockerfile.cli -t ashlar-cli:latest .

# Generate API docs (requires: dotnet tool install -g docfx)
docs-api:
	dotnet build Ashlar.sln -c Release
	cd docs/api && docfx
