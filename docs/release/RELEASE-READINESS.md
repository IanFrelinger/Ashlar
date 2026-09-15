# Release Readiness + Business Plan Interface

**Audience:** CEO / founder decision-making  
**Purpose:** Binary go/no-go for public v0.x release vs design-partner private; map release bars to commercial funnel; list CEO-only actions  
**Status:** Living document — update as P0 issues close and product lanes advance

---

## Executive summary

Ashlar ships as an **embeddable local-first .NET runtime** with cert-gate + trust log. Commercial model: Community free → design partner → Builder/Team/Enterprise tiers; Cloud PAYG later. Dual revenue streams: flagship product on Ashlar runtime + engine licensing (Fortnite+Unreal model). North star: successful embeds, not autonomous self-extension hype before the dogfood ledger exists.

**Current state:**
- **Runtime (Ashlar):** P0 trust PRs merged (limitations 7-8 closed by PR #523; limitation 9 closed 2026-09-13); CI redundancy live (five required checks on `master`: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`); cert-loop honesty shipped in docs/landing
- **Product (Forge):** Scaffold exists; no public Cursor-safe claims without ledger (P3 hold)
- **Recommendation:** Design-partner private until external validation lands (limitation 9 closed 2026-09-13, operative half and lane-agreement detection); autonomy marketing stays HOLD per `docs/dogfood-ledger.md` / `docs/dogfood-scorecard.md`

**This document provides:**
1. [Runtime release bar (Ashlar)](#1-runtime-release-bar-ashlar) — must-close issues before tagging release candidates
2. [Product release bar (Forge)](#2-product-release-bar-forge) — adaptive factory / Forge.Verify phased plan
3. [Business plan mapping](#3-business-plan-mapping) — which bars map to which funnel stages
4. [Go/no-go checklist](#4-gono-go-checklist) — binary decision framework
5. [CEO-only actions](#5-ceo-only-actions-list) — Pages, social preview, branch protection, contact channel

---

## 1. Runtime release bar (Ashlar)

These are **blockers** for any public release candidate tag. Every item references open/closed issues or documentation state.

### 1.1 Trust foundation (P0)

| Item | Issue | Status | Release impact |
|------|-------|--------|----------------|
| **P0 trust signature holes (limitations 7-9)** | [#513](https://github.com/IanFrelinger/Ashlar/pull/513), [#523](https://github.com/IanFrelinger/Ashlar/pull/523) | ✅ **CLOSED** (#513 merged 2026-09-06T02:23:51Z; #523 merged 2026-09-06T05:58:54Z; limitation 9 closed 2026-09-13) | Fail-closed defaults landed (#513); **limitations 7-8 CLOSED by PR #523 (2026-09-06)** — `CertificationVerifyOptions.Default`/`Strict` now set `RequireEd25519Signature = true` + `MinimumSchemaVersion = 2`; **limitation 9 CLOSED 2026-09-13** (the composition signer takes the injected `CertificationRecordSigner` as its key holder and delegates the MAC to it; the gate additionally warns when the two lanes were built independently) — see `docs/certification-evidence.md` limitations 7-9 and `docs/dogfood-ledger.md` |
| **Cert-loop honesty in landing/docs** | [#514](https://github.com/IanFrelinger/Ashlar/pull/514), [#505](https://github.com/IanFrelinger/Ashlar/pull/505), [#506](https://github.com/IanFrelinger/Ashlar/pull/506) | ✅ **COMPLETE** (all merged by 2026-09-06T01:31:06Z) | Landing-page work, not cert-loop work: #505 added the landing page and brand assets, #506 reverted `ashlar-cli` image names to `nexo-cli` across the docs, #514 fixed `site/` defects. Its one certification-related edit deletes an `options.CertificationRequired = true;` line from a landing-page code sample, for a setting the SDK does not have — honesty about the page, not a defect fix in the loop |
| **Cert-loop integration live path** | [#512](https://github.com/IanFrelinger/Ashlar/pull/512) | ⚠️ **DOCS + TESTS ONLY** (merged 2026-09-06T01:40:20Z) | PR #512 changed three files — `docs/SELF-EXTEND-AUDIT.md`, `docs/cert-loop-integration-plan.md` and `LiveExtenderCertLoopIntegrationTests.cs`. No runtime file. The A2/A4 gates it credits already sat in `SelfExtendAdmissionBridge`, and the plan it added still lists the watch-window phase as TODO |

**Residual trust limitations (documented, not blockers for v0.x):**
- Dev HMAC signer (not PKI) — documented in `docs/certification-evidence.md` limitation 1; operator can supply real key via `ASHLAR_CERT_ED25519_KEY`
- Composition seam check is type-level only — limitation 2; graph-mutation teeth partially compensate
- Session containment opt-in — limitation 4; default ships sealed (Passive mode), operator raises dial deliberately
- Model proposing scale boundary — limitation 5; mechanism closed, breadth (multiple objectives/tasks) is host-operations work

**What sales/marketing may claim:**
- ✅ "Certified artifacts require passing analyzer fence, witness (correctness), mutation testing, determinism — gate is CI-proven"
- ✅ "Trust log auditable via `/api/trust/dashboard`; every decision on the record"
- ✅ "Fail-closed admission: proposals face real gate, uncertified code rejected"

**What sales/marketing must NOT claim (until the dogfood ledger shows dated Strict E2E passes):**
- ❌ "Autonomous self-extension safe for unattended production" — ships in hold mode; `docs/dogfood-ledger.md` has cert-path / INFRA ONLY entries but no dated E2E pass yet
- ❌ "No human oversight required" — operator-governed path is supported, autonomous path is experimental

### 1.2 CI redundancy (P0)

| Item | Issue | Status | Release impact |
|------|-------|--------|----------------|
| **CI not SPOF** | [#511](https://github.com/IanFrelinger/Ashlar/pull/511) | ✅ **COMPLETE** (merged 2026-09-06T01:37:01Z; branch protection updated) | `master` branch protection now requires `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary` (verified 2026-09-09 via `gh api repos/IanFrelinger/Ashlar/branches/master/protection`) |

**Action required:** 
- ✅ **Done** — branch protection requires all five checks (see [CEO-only actions](#5-ceo-only-actions-list))
- ✅ **Done** — `docs/CiGateInventory.md`, `CONTRIBUTING.md`, and `README.md` name all five required checks (#572, #573, and the 2026-09-09 five-checks docs PR)

**What sales/marketing may claim:**
- ✅ "Every PR gated by hermetic certification + build integrity + docs verification"
- ✅ "Redundant CI gates prevent broken code merge even if primary gate flaky"

### 1.3 Known limitations honesty (P0)

| Item | Documentation | Status | Release impact |
|------|---------------|--------|----------------|
| **Certification evidence ledger current** | `docs/certification-evidence.md` | ✅ **COMPLETE** | All proven admits/rejects documented with CI runs; known v0 limitations 1-9 listed with closure dates where applicable |
| **Self-extend audit transparent** | `docs/SELF-EXTEND-AUDIT.md` | ✅ **COMPLETE** | Invariants A-D enforced status documented; convergence gap vs certified loop stated (§ "Cert-loop integration") |
| **Landing page claims honest** | `site/index.html` | ✅ **COMPLETE** | Cloud marked "coming soon"; no autonomous claims; SDK examples grounded in real API |
| **README trust claims grounded** | `README.md` | ✅ **COMPLETE** | Links to evidence ledger, audit docs; spike-grade vs supported paths distinguished |

**What sales/marketing may claim:**
- ✅ "Every limitation documented; evidence ledger cites CI runs"
- ✅ "Known gaps disclosed in `certification-evidence.md` and `SELF-EXTEND-AUDIT.md`"

**What sales/marketing must NOT claim:**
- ❌ "Production-ready autonomous self-extension" (until unattended multi-cycle evidence exists beyond spike)
- ❌ "Zero trust gaps" (documented limitations remain for v0.x)

### 1.4 User-facing documentation complete (P0)

| Item | Files | Status | Release impact |
|------|-------|--------|----------------|
| **Tester quickstart accurate** | `docs/TesterQuickstart.md` | ✅ **COMPLETE** | 15-minute local path works; no Docker/API keys required |
| **Integrator guide current** | `docs/IntegratorGuide.md`, `docs/sdk.md` | ✅ **COMPLETE** | NuGet embed, HTTP client, SDK integration documented |
| **Distribution models clear** | `docs/DistributionModels.md` | ✅ **COMPLETE** | NuGet, HTTP, CLI, compose, source, mesh/federation paths |
| **Security defaults honest** | `README.md`, `SECURITY.md` | ✅ **COMPLETE** | HTTP-only / no auth default documented; network exposure gates fail-closed |

**What sales/marketing may claim:**
- ✅ "Embed via NuGet, run as HTTP API, deploy as CLI container, or source integration"
- ✅ "15-minute tester quickstart; no cloud dependencies"

---

## 2. Product release bar (Forge)

**Ashlar.Forge** = separate product repository (adaptive factory / Forge.Verify / Cursor adapter) with one-way dependency on Ashlar runtime. Repo may be empty pending push; document as product lane regardless.

### 2.1 Forge scaffold (required before any Forge claims)

| Item | Status | Release impact |
|------|--------|----------------|
| **Forge repository exists** | ⚠️ **PENDING** | Separate `Ashlar.Forge` repo created; references Ashlar runtime NuGet packages as dependencies |
| **Forge.Verify phased plan named** | ⚠️ **PENDING** | Phased rollout documented: internal dogfood → design partner → limited beta → general availability |
| **Adaptive factory scaffold** | ⚠️ **PENDING** | Core abstractions exist; no production usage until dogfood ledger |

**What sales/marketing may claim NOW:**
- ✅ "Forge product lane planned; adaptive factory + Verify on roadmap"
- ✅ "One-way dependency: Forge builds on Ashlar runtime"

**What sales/marketing must NOT claim (until Forge ledger exists):**
- ❌ "Cursor adapter safe for production use"
- ❌ "Autonomous Forge.Verify without human oversight"
- ❌ Any specific Forge.Verify capabilities, SLAs, or pricing

### 2.2 Cursor-safe claims gate (P3 — OPEN)

| Item | Blocker | Release impact |
|------|---------|----------------|
| **Dogfood ledger shows dated E2E passes** | P3 open: `docs/dogfood-ledger.md` exists but holds only cert-path close / INFRA ONLY entries (no dated Strict E2E pass yet); scorecard unlock criteria in `docs/dogfood-scorecard.md` unmet | Until dated passes: no "production-ready Cursor integration" claims; design-partner private only with explicit "experimental, hold mode" disclosure |

**What sales/marketing may claim (design-partner private only):**
- ✅ "Experimental Cursor adapter available for design partners under hold mode"
- ✅ "Seeking feedback on autonomous code proposal workflow"

**What sales/marketing must NOT claim publicly (until P3 closes):**
- ❌ "Production-ready Cursor integration"
- ❌ "Unattended autonomous code generation"
- ❌ "Safe for general availability"

---

## 3. Business plan mapping

Map release bars to **commercial funnel stages**: Aware → Eval → Embed → Design partner → Paid (Builder/Team/Enterprise tiers).

### 3.1 Aware (inbound interest, docs consumption)

**What they see:**
- Marketing landing page (`site/index.html`)
- README.md hero + quickstart
- GitHub social preview card

**Must be true:**
- ✅ No false product claims (Cloud "coming soon", Forge roadmap-only)
- ✅ Honest security defaults (HTTP-only / no auth for local dev)
- ✅ Evidence ledger linked (so technical readers can verify)

**Funnel success:** User reads landing page → clicks "Get started" → reaches TesterQuickstart or IntegratorGuide

**Current readiness:** ✅ **READY** (PRs #505, #506, #514 merged)

### 3.2 Eval (hands-on testing, local setup)

**What they do:**
- Run TesterQuickstart (15 min, no Docker/API keys)
- Explore IntegratorGuide (NuGet embed, HTTP client)
- Test CLI commands (`ashlar doctor`, `ashlar pipeline validate`)

**Must be true:**
- ✅ Quickstart completes successfully on clean machine
- ✅ Documentation accurate (no invented SDK APIs, no broken image references)
- ✅ CLI/API smoke paths work (mock provider, no external dependencies)
- ✅ Security defaults safe (localhost-only unless explicitly configured)

**Funnel success:** User successfully runs first task → reads trust log → understands cert-gate

**Current readiness:** ✅ **READY** (quickstart tested, docs honest)

### 3.3 Embed (integrate into their product)

**What they do:**
- NuGet package reference (`Ashlar.Client`, `Ashlar.Hosting`)
- `services.AddAshlarClient(baseUrl)` or `services.AddAshlar()`
- Deploy as sidecar HTTP API or embedded runtime
- Query audit trail via `/api/trust/dashboard`

**Must be true:**
- ✅ NuGet packages published (existing: `0.1.2` on nuget.org)
- ✅ SDK examples in docs match real API (`AddAshlarClient`, not invented properties)
- ✅ Distribution models documented (NuGet, HTTP, CLI, compose, source)
- ✅ Trust architecture honest (local-first default, cloud opt-in)

**Funnel success:** User embeds Ashlar runtime → queries trust log → understands certification gate

**Current readiness:** ✅ **READY** (v0.1.2 published, IntegratorGuide accurate)

### 3.4 Design partner (private pilot, direct engagement)

**What they do:**
- Deploy in their staging/pre-prod environment
- Test real workloads against Ashlar runtime
- Explore Forge experimental features (hold mode, explicit disclosure)
- Provide feedback on autonomous proposal workflow

**Must be true:**
- ⚠️ P0 trust holes closed (PRs #513 + #523 merged 2026-09-06 — limitations 7-8 closed 2026-09-06; limitation 9 closed 2026-09-13)
- ⚠️ Cert-loop integration: PR #512 merged 2026-09-06, but shipped docs and tests only — no runtime change
- ✅ CI redundancy in place (PR #511 merged; branch protection requires all five checks)
- ✅ Known limitations documented honestly
- ✅ Design partner agreement includes "experimental" disclosure for Forge features
- ✅ Support channel established (GitHub Discussions or direct contact)

**Funnel success:** Design partner deploys → sees value → willing to pay

**Current readiness:** ⚠️ **NEAR for runtime** (P0 PRs merged, branch protection live; limitation 9 closed 2026-09-13); ⚠️ **HOLD for Forge** (pending dated ledger passes)

### 3.5 Paid (Builder/Team/Enterprise tiers)

**What they buy:**
- **Community (free):** Open-core runtime, NuGet packages, HTTP API, CLI, local-first
- **Builder (~$8k/yr indicative):** Enhanced support, SLA, staging feed access, priority bug fixes
- **Team (~$25k/yr indicative):** Multi-user, shared policies, centralized audit aggregation
- **Enterprise ($75k+/yr indicative):** Dedicated support, custom SLA, on-premises deployment assistance, governance module

**Cloud PAYG:** Later phase (not v0.x)

**Must be true:**
- ✅ All design-partner feedback addressed (or documented as future work)
- ✅ Production readiness gate passed (`docs/ProductionReadinessGate-v1.md`)
- ✅ Pricing confirmed (indicative → final)
- ✅ Order form / contract template ready (`docs/product-fleet/private-order-form-template.md`)
- ✅ Support boundaries documented (`docs/product-fleet/private-support-boundaries.md`)

**Current readiness:** ⚠️ **NOT READY** (design-partner phase required first; pricing indicative only)

---

## 4. Go/no-go checklist

Binary decision framework for **v0.x public release** vs **design-partner private**.

### 4.1 Public v0.x release (Community tier, open to all)

**Go criteria (ALL must be true):**

- [x] **P0 trust holes closed:** PR #513 fail-closed defaults merged (✅ 2026-09-06); limitations 7-8 closed by PR #523 (✅ 2026-09-06); **limitation 9 closed ✅ 2026-09-13** — a host-supplied `CertificationRecordSigner` now keys the composition lane through the shipped DI registration — roadmap M1 requires close before commercial claims (`docs/audits/2026-09-completion-roadmap.md`)
- [x] **CI redundancy live:** PR #511 merged (✅ 2026-09-06) + branch protection requires `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary` (✅ verified 2026-09-09)
- [ ] **Cert-loop integration:** PR #512 merged (✅ 2026-09-06) — but unticked 2026-09-14, because
      the diff does not contain an integration. PR #512 changed three files — `docs/SELF-EXTEND-AUDIT.md`, `docs/cert-loop-integration-plan.md` and `LiveExtenderCertLoopIntegrationTests.cs`. No runtime file. The A2/A4 gates it credits already sat in `SelfExtendAdmissionBridge`, and the plan it added still lists the watch-window phase as TODO
- [ ] **Cert-loop honesty:** PRs #505, #506 and #514 all merged (✅ 2026-09-06), but what they
      changed is the marketing landing page and brand assets (#505, #514) and a revert of
      `ashlar-cli` image names to `nexo-cli` across the docs (#506). The one certification-related
      edit among them removes a fabricated `options.CertificationRequired` from a landing-page code
      sample. That is worth having and is not the same as auditing the cert-loop claims, which is
      what this criterion asks for
- [ ] **Known limitations documented:** `certification-evidence.md` + `SELF-EXTEND-AUDIT.md` current
- [ ] **User-facing docs accurate:** TesterQuickstart + IntegratorGuide tested by external reader
- [x] **Security defaults safe:** README + SECURITY.md warn about HTTP-only / no auth default (verified 2026-09-14)
- [x] **NuGet packages published:** 0.1.2 is live on nuget.org (verified 2026-09-14). The v0.1.2 release run (33867752688) failed *after* publishing: its log records "Your package was pushed." for every package, then `pack-and-publish` failed in the post-push nuget.org visibility poll — "not visible on nuget.org for version 0.1.2 after 12 attempts", the last four still 404 on the flat-container index. The poll budget (12 x 15s) was shorter than nuget.org's indexing lag for the largest packages. `verify-nuget-org-packages-visible.sh` now defaults to 40 attempts and raises any shorter budget unless `ASHLAR_NUGET_VERIFY_ALLOW_SHORT=1`. Knock-on: that failure skipped the run's `Draft GitHub Release` job, which is why the v0.1.2 release carried no assets until they were uploaded on 2026-09-14
- [x] **GHCR images published:** `nexo-cli:0.1.2` is live, multi-arch (amd64 + arm64), and pinned by digest in `deploy/node.yml` (verified 2026-09-14). `nexo-api` is amd64-only: the `GHCR nexo-api` job in `reusable-container-publish.yml` passes `--platform linux/amd64` to both build steps and the `skip_multi_arch` input governs `nexo-cli` alone. No rationale for the asymmetry is recorded anywhere, so treat it as unreviewed rather than decided; `deploy/k8s/ashlar-mesh-worker-deployment.yaml` now warns pullers. Rename to `ashlar-cli` still pending; see README note
- [ ] **Marketing landing honest:** No Cloud GA claims, no autonomous production claims, Forge roadmap-only
- [ ] **GitHub social preview current:** `ashlar-og-flat-1200x630.png` uploaded (CEO action)
- [ ] **Contact channel live:** GitHub Discussions enabled OR `hello@ashlar.dev` with monitoring

**No-go criteria (ANY one blocks public release):**

- [x] ~~**ACTIVE BLOCKER:** Limitation 9~~ — **CLOSED 2026-09-13** by PR #621 (merged 2026-09-13T21:41Z): a host-supplied `CertificationRecordSigner` now keys the composition lane through the shipped DI registration. The lane-agreement detection is a separate change that landed the following day in PR #623 (merged 2026-09-14T22:27Z) — the gate compares its two signers at construction and warns (never refuses) when they were built independently. This criterion no longer blocks. (limitations 7-8 closed by PR #523, 2026-09-06)
- [x] ~~Branch protection not updated (`build-core`, `shell-lint`, `lychee` not required)~~ — ✅ resolved (all five checks required; re-verified 2026-09-14 against the branch protection API)
- [ ] Landing page contains false Cloud GA or autonomous production claims
- [ ] TesterQuickstart fails on clean machine
- [ ] Security defaults allow unauthenticated network exposure without explicit opt-in

**Current recommendation:** ⚠️ **NO-GO for public v0.x** — external validation + remaining CEO actions (Pages, social preview, Discussions) required

### 4.2 Design-partner private release

**Go criteria (LESS restrictive than public):**

- [x] **P0 trust holes closed:** PRs #513 + #523 merged (✅ 2026-09-06; limitations 7-8 closed; limitation 9 closed 2026-09-13)
- [ ] **Cert-loop integration:** PR #512 merged (✅ 2026-09-06) — docs and
      `LiveExtenderCertLoopIntegrationTests.cs` only, no runtime change; see §4.1
- [ ] **CI primary gate working:** `cert-gate` reliable (redundancy nice-to-have, not blocker)
- [ ] **Known limitations documented:** Limitations 1-9 in `certification-evidence.md`
- [ ] **Design-partner agreement signed:** Includes "experimental" disclosure for Forge features
- [ ] **Support channel established:** Direct contact or private Slack/Discord
- [ ] **NuGet packages available:** Staging feed OR nuget.org
- [x] **GHCR images available:** `nexo-cli` and `nexo-api` are public with `latest` and semver tags; the digest pin is in place (verified 2026-09-14)

**No-go criteria:**

- [ ] Cert-gate consistently failing on master
- [ ] No design-partner agreement (no legal protection for experimental features)
- [x] ~~Limitation 9 (composition signer key discard) not disclosed in design-partner agreement~~ — **moot 2026-09-13: limitation 9 is CLOSED**, so there is no residual to disclose. The disclosure that remains is the committed dev key itself (see the HMAC row below), which is a different limitation.

**Current recommendation:** ✅ **GO design-partner private** (runtime pilots) — P0 PRs merged; limitation 9 closed 2026-09-13. Autonomy / design-partner **marketing claims** remain **HOLD** per `docs/dogfood-ledger.md` and `docs/dogfood-scorecard.md` until scorecard thresholds hold ~7 consecutive days and dated Strict+Ed25519 E2E passes appear in the ledger.

---

## 5. CEO-only actions list

These actions require **repository administrator** or **organization owner** permissions and cannot be delegated to contributors.

### 5.1 Branch protection (CI hardening)

**Status:** ✅ **DONE** — [#511](https://github.com/IanFrelinger/Ashlar/pull/511) **merged** (2026-09-06T01:37:01Z); `master` branch protection requires `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary` (verified 2026-09-09 via `gh api repos/IanFrelinger/Ashlar/branches/master/protection`; `enforce_admins` on).

**Docs:** ✅ `docs/CiGateInventory.md`, `CONTRIBUTING.md`, and `README.md` match the live setting (five required checks) as of 2026-09-09.

**Reference (how it was configured; CEO/admin only):**

1. Navigate to **Settings → Branches → Branch protection rule for `master`**
2. Under "Require status checks to pass before merging", add these required checks:
   - `build-core` (fast compile check, ~2-3 min)
   - `shell-lint` (shell script syntax, ~30s)
   - `lychee (README + docs)` (broken docs links, ~30s)
3. Keep `cert-gate` as required (existing)
4. **Do NOT remove `cert-gate`** — new checks are redundancy, not replacement
5. `Readiness summary` was added as the fifth required check on 2026-09-09, once #571 made the readiness gate report on every PR

**Why this matters:** Eliminates CI single point of failure; if `cert-gate` is cancelled or flaky, other gates still block broken code.

**Verify:** Push test PR → confirm all five checks must pass before merge allowed

### 5.2 GitHub Pages (marketing landing)

**Issue:** Landing page at `site/index.html` (PRs #505, #514 merged), not yet deployed.

**Action required:**

1. Navigate to **Settings → Pages**
2. Source: **Deploy from branch**
3. Branch: **`master`** → Folder: **`/site`**
4. Save

**Result:** Marketing landing available at `https://ianfrelinger.github.io/Ashlar/`

**Verify:** Visit deployed URL → see hero, pricing, bento grid, footer → all assets load

### 5.3 GitHub social preview (OG card)

**Issue:** New flat OG card exists (`assets/brand/ashlar-og-flat-1200x630.png`), not yet uploaded.

**Action required:**

1. Navigate to **Settings → General → Social preview**
2. Upload `assets/brand/ashlar-og-flat-1200x630.png`
3. Save

**Alternative:** Keep existing `ashlar-social-card-1280x640.png` if preferred.

**Why this matters:** When GitHub repo linked on social media (Twitter, LinkedIn, Slack), correct card displays.

**Verify:** Share GitHub repo link on Slack → preview shows Ashlar branding + subtitle

### 5.4 Repository variables / secrets (release workflow)

**Documentation:** `docs/GitHubRepoVariables.md`

**Review required variables:**

> **Corrected 2026-09-13. All three rows below were wrong, in the direction that silently breaks a
> release.** Values re-read from `gh api repos/IanFrelinger/Ashlar/actions/variables` and `.../secrets`.
> The previous table said `NUGET_PUBLISH_MODE` was `push` and recommended keeping it. `push` is neither
> `oidc` nor `apikey`, so `reusable-release-nuget.yml` takes the **artifact-only** branch: the
> release workflow goes green, emits a `::notice::`, and **publishes nothing to nuget.org**. It also said
> the `NUGET_API_KEY` secret was "Set"; that secret does not exist. In `oidc` mode the api key is minted
> at run time by the `nuget_login` step (`:185`) from the `NUGET_USER` secret, so `NUGET_USER` plus the
> NuGet trusted-publishing configuration is what must be valid, not a stored key.

| Variable / secret | Actual value (verified 2026-09-13) | Recommended for v0.x |
|----------|---------------|----------------------|
| `NUGET_PUBLISH_MODE` (variable) | `oidc` | **Keep `oidc`.** Only `oidc` or `apikey` publish; anything else is artifact-only (the artifact-only branch's `if:` in `reusable-release-nuget.yml`). |
| `NUGET_RELEASE_SBOM` (variable) | `true` | Keep `true`. |
| `NUGET_RELEASE_GRYPE` (variable) | `true` | Keep `true`. |
| `RELEASE_CREATE_GITHUB_RELEASE` (variable) | **unset** | Leave unset. the release-creation step tests `!= 'false'`, so unset already means "create the release". |
| `NUGET_USER` (secret) | Set | Required by the `oidc` path (the `nuget_login` step in `reusable-release-nuget.yml`). |

**The nine below were missing from this table** until 2026-09-14, so it documented six of the fifteen
variables and secrets the release workflows actually read. All nine are unset today; the column says
what unset means, because for most of them unset is the intended state and the default is the
behaviour you get.

| Variable / secret | Actual value (verified 2026-09-14) | What unset means |
|----------|---------------|----------------------|
| `NUGET_POST_PUSH_VERIFY` (variable) | unset | Post-push verification RUNS. The condition is `!= 'false'`, so only the literal `false` disables it. |
| `NUGET_POST_PUSH_ATTEMPTS` (variable) | unset | Defaults to `40` polls. nuget.org indexing lags publication; a short budget reports "not visible" for a package that published fine. |
| `NUGET_POST_PUSH_SLEEP_SEC` (variable) | unset | Defaults to `15` seconds between polls. With the default attempts that is a ~10 minute budget. |
| `NUGET_POST_PUSH_VERIFY_PACKAGE_IDS` (variable) | unset | Verify every packed id rather than a named subset. |
| `RELEASE_CROSS_VERIFY` (variable) | unset | Cross-verification RUNS. Disabled only by the literal `false`. |
| `NUGET_STAGING_FEED_URL` (variable) | unset | No staging feed; the staging path is inert. Set it with `NUGET_STAGING_API_KEY` to rehearse a publish against a feed that is not nuget.org. |
| `NUGET_STAGING_API_KEY` (secret) | unset | As above — the staging push has no credential and does not run. |
| `RELEASE_NOTIFICATION_WEBHOOK_URL` (secret) | unset | No release notification is sent; the step logs "No RELEASE_NOTIFICATION_WEBHOOK_URL secret; skip notify." and continues. |
| `GITHUB_TOKEN` (secret) | provided by Actions | Never set by hand. Listed so its absence from the configurable rows is not read as an omission. |
| `NUGET_API_KEY` (secret) | **does not exist** | Not needed under `oidc`; only the `apikey` path (`:209`) reads it. |

**Action:** confirm `NUGET_PUBLISH_MODE` is still `oidc` and that NuGet trusted publishing for `NUGET_USER`
is current. Do **not** "restore" a `NUGET_API_KEY` secret to satisfy the old row — it is not on the `oidc`
path, and adding a long-lived key would be a step backwards from trusted publishing.

### 5.4b `VERSION` is `0.2.0`, bumped 2026-09-14 — why the minor moved

`VERSION` read `0.1.2` while `0.1.2` was already published on nuget.org, so the version string no
longer identified its content. It is now `0.2.0`, landed ahead of the tag as the flow below requires.

**The minor moved rather than the patch, because signed bytes changed.** PR #592 gave every `double`
one canonical decimal form across all three target frameworks: the netstandard2.0 asset under Mono
had been writing different digits from net8.0/net10.0 for the same value, and the canonical payload
is the message every certification signature is computed over. Both golden corpora changed with it.
A record minted under `0.1.2` on that asset and the same record minted now are not byte-identical.
PR #621 compounds it: a host that supplied a brick signer previously got composition records under
the committed dev key and now gets them under its own. `0.1.3` would have told a consumer holding
stored signed records that this was a safe patch.

*(The counts this section used to carry — 104 commits past the tag, seven touching* 
*`src/Ashlar.Certification.Contracts/` — were accurate when recorded on 2026-09-13; it is 112 and 7 as of* 
*2026-09-14. No count is stated in the body now, because it changes on every merge. Re-derive with* 
*`git rev-list --count v0.1.2..master`, and do it in a FULL clone: a shallow checkout truncates the* 
*history, `git describe` then finds no tag at all, and the count comes out far too low.)*

**What protects you and what does not.** `release.yml:60-66` asserts the tag name matches root `VERSION`
**at tag time only**, and only when `github.ref_type == 'tag'` — a `workflow_dispatch` from a branch skips
it. `scripts/release-preflight-local.sh` inspects both, and nothing invokes it automatically: it is
reachable from `Makefile`, the release issue template and the PR template, i.e. from human
checklists. It is not the only reader, though — `release-staging-on-label.yml` resolves the
canonical version from root `VERSION` and runs automatically when a PR is labelled, and
`scripts/resolve-canonical-package-version.sh`, `scripts/verify-docs-published-version.sh` and
`tests/uat/tier9.sh` read it too. What none of them do is compare `VERSION` to the latest tag.
**Closed 2026-09-14.** `scripts/check-version-staleness.sh` compares `VERSION` to the newest `v*` tag and to nuget.org, and runs from `release-preflight-local.sh` (the last human checkpoint before a tag) and from `rc-gate`. It is advisory and always exits 0 — how far past a tag is "too far" is a judgement call, and a check that reddens a lane on a judgement call is how lanes get muted here. It needs a FULL clone: a shallow checkout makes `git describe` find no tag, so the rc-gate checkout sets `fetch-depth: 0`.

**Action before any release:** bump `VERSION`, land it, then tag. If you tag first, the guard above stops
the release rather than shipping a mislabelled package — which is the good failure, but it fails late.

### 5.5 Contact channel (customer funnel)

**Current state:** Landing page (`site/index.html`, "Talk commercial" button) links to `https://github.com/IanFrelinger/Ashlar/discussions`; Discussions is **not yet enabled** on the repo (`has_discussions: false`, verified 2026-09-09). README does not currently link Discussions.

**Options:**

1. **Keep GitHub Discussions** (current) — zero-cost, public, searchable
2. **Enable email contact** — Set up `hello@ashlar.dev` OR `support@ashlar.dev` with monitoring
3. **Private channel for design partners** — Slack/Discord invite-only

**Action required:**

- If keeping GitHub Discussions: **Enable Discussions** in repo settings (Settings → General → Features → Discussions)
- If adding email: Register domain, configure inbox monitoring, update landing page `site/index.html` (replace Discussions link)

**Why this matters:** Funnel breaks at "Aware → Eval" transition if users can't ask questions.

### 5.6 Forge repository PAT (product split)

**Issue:** Forge product repo may need separate access token for CI cross-repo operations.

**Action required (when Forge repo created):**

1. Create **Personal Access Token (classic)** with `repo` scope
2. Add as secret `FORGE_REPO_PAT` in Ashlar repo (Settings → Secrets → Actions)
3. Update Ashlar CI workflows to pull Forge integration tests if needed

**Not blocking v0.x:** Forge is roadmap-only for initial release.

---

## 6. Release decision summary

### Recommendation: Design-partner private ready; public v0.x needs external validation + remaining CEO actions

**Rationale:**
- Runtime P0 PRs merged — ✅ (#513 fail-closed defaults, #512 cert-loop, #511 workflows, #514 landing) all landed 2026-09-06
- Limitations 7-8 — ✅ closed by PR #523 (2026-09-06; `Default`/`Strict` require Ed25519 signature + schema floor 2; ledger entry in `docs/dogfood-ledger.md`)
- Limitation 9 — ✅ **CLOSED 2026-09-13** (the composition signer now takes the injected `CertificationRecordSigner` as its key holder and delegates the MAC to it, and the shipped DI registration supplies that signer, so a host-configured key keys both lanes; no key material crosses the boundary. The lane-agreement detection residual closed the same day: the gate compares its two signers at construction and warns, never refuses. No longer a roadmap M1 blocker for commercial claims.)
- CI redundancy workflows on master — ✅ (PR #511 merged)
- Branch protection — ✅ requires `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary` (verified 2026-09-09)
- Known limitations documented honestly ✅
- Forge needs P3 ledger before public claims ⚠️

**Path forward:**

1. **NOW:** Go design-partner private (runtime P0 PRs merged; limitation 9 closed 2026-09-13; Forge hold-mode with disclosure; no autonomy marketing claims)
2. **NEXT:** CEO actions (Pages, social preview, Discussions)
3. **THEN:** External validation (docs tested by non-contributor)
4. **FINALLY:** Public v0.x release (limitation 9 closed 2026-09-13 + validation complete + CEO actions done)

### What to tell prospects TODAY

**If they ask "Is Ashlar production-ready?"**

✅ **Yes for embedded runtime use cases (design-partner private):**
- "Ashlar runtime (cert-gate + trust log) ready for design-partner pilots"
- "P0 trust PRs merged (2026-09-06): fail-closed defaults, Strict+Ed25519 required (#523), cert-loop integration, CI workflows, landing honesty"
- "NuGet packages published, HTTP API works, CLI tested"
- "Fail-closed admission: uncertified code rejected"
- "Limitations 7-9 closed (7-8 by PR #523 2026-09-06; 9 on 2026-09-13). Remaining disclosure is the committed development HMAC key: a host that sets no key signs with a constant published in the repository."
  <!-- Corrected twice on 2026-09-13. This line first read "composition signer ignores an explicitly supplied key", then "compositions have no operator path to a real key". BOTH are superseded: limitation 9 closed the same day, the composition signer takes the injected CertificationRecordSigner as its key holder, and the shipped DI registration supplies it. Do not reissue either earlier wording externally. -->

⚠️ **Not yet for public v0.x:**
- "External validation needed before public announcement"

⚠️ **Not yet for autonomous self-extension:**
- "Autonomous loop ships in hold mode (experimental)"
- "Seeking design partners for Forge product (adaptive factory + Cursor adapter)"
- "Multi-cycle unattended evidence pending (P3 open; dogfood ledger has no dated Strict E2E pass yet — marketing HOLD per `docs/dogfood-scorecard.md`)"

❌ **Not production-ready for:**
- Unattended autonomous code generation (hold mode only)
- Cloud PAYG (not available yet, roadmap)
- Forge.Verify general availability (design-partner private only)

### Success metrics (6-month targets)

| Metric | Target | How measured |
|--------|--------|--------------|
| **Embeds** (north star) | 10 design partners → 3 paid (Builder/Team) | Contract signed, integration deployed |
| **NuGet downloads** | 500 unique packages (Community tier) | nuget.org stats |
| **GitHub stars** | 200+ | Proxy for awareness |
| **Trust log queries** | 50+ unique orgs hitting `/api/trust/dashboard` | API logs (privacy-preserving) |
| **Cert-gate admits** | 100+ in design partner environments | Telemetry opt-in |
| **Forge design partners** | 5 orgs testing Cursor adapter (hold mode) | Direct engagement |

**Revenue target (12-month):** 3 Builder ($24k ARR) + 1 Team ($25k ARR) + 1 Enterprise ($75k ARR) = **$124k ARR**

---

## 7. Open risks and mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| **P3 ledger delays Forge GA** | Revenue from Forge pushed to 2027 | Focus on runtime embeds (proven value); Forge design-partner private generates feedback |
| **Design partners churn before paid** | Revenue target missed | Tight feedback loop, fast bug fixes, clear support boundaries |
| **Competitor (e.g. Copilot) moves faster on trust/audit** | Differentiation weakens | Double down on fail-closed admission + cert-gate teeth (our moat); emphasize local-first |
| ~~**Limitation 9 → composition trust chain weak**~~ — **CLOSED 2026-09-13** | Was: composition records minted under the committed dev key because no production registration supplied one. The shipped registration now injects the brick signer as the composition lane's key holder. | Closed 2026-09-13 by the keyed DI registration; nothing remains to disclose for this row. The committed development HMAC key is disclosed separately, under limitation 1. |
| **Contact channel (Discussions) not enabled** | Funnel breaks at Aware → Eval | CEO action (5.5) before public announcement |

---

## 8. Appendix: Reference documentation

### 8.1 Trust / certification

- `docs/certification-evidence.md` — Falsifiable proof ledger (all ADMIT/REJECT results with CI runs)
- `docs/SELF-EXTEND-AUDIT.md` — Self-extend invariants A-D enforcement audit
- `docs/trust-loop/ashlar-trust-loop-spec.md` — Trust loop spec (analyzer fence, witness, mutation, determinism)
- `docs/governed-pipeline.md` — Governed model pipeline (proposals flow through)
- `docs/dogfood-ledger.md` — Dated dogfood evidence (PR #523 lim-7/8 close entry; no E2E pass yet)
- `docs/dogfood-scorecard.md` — Autonomy marketing unlock thresholds (HOLD status)
- `docs/audits/2026-09-completion-roadmap.md` — Completion roadmap (M1: close limitation 9)

### 8.2 Product / commercial

- `docs/CommercialExtractionPlan.md` — Open-core boundary (what's Apache-2.0 vs commercial)
- `docs/DistributionModels.md` — NuGet, HTTP, CLI, compose, source, mesh/federation
- `docs/CompetitivePositioning.md` — Market positioning vs Copilot / other AI runtimes
- `docs/PayingCustomersASAP.md` — Path to revenue

### 8.3 Operations / deployment

- `docs/DEPLOYMENT.md` — Deploy runbooks (compose, GHCR images)
- `docs/ProductionReadinessGate-v1.md` — Binary pass/fail gate for production deployment
- `docs/RELEASE.md` — NuGet + GHCR release process (happy path)
- `docs/RELEASE_RUNBOOK.md` — Which workflow, fork notes, after-tag checks

### 8.4 Known limitations (open issues)

- `docs/certification-evidence.md` § "Known v0 limitations" (limitations 1-9 — 7-8 closed 2026-09-06, 9 closed 2026-09-13, others residual; no line range, because that section is edited by row insertion)
- `docs/SELF-EXTEND-AUDIT.md` § "Cert-loop integration" (convergence gap: certified loop vs legacy extender)

### 8.5 Marketing / landing

- `site/index.html` — Marketing landing page (ready for Pages deployment)
- `site/README.md` — Landing page docs (design philosophy, deployment instructions)
- `assets/brand/BRAND.md` — Brand assets (logo, OG card, palette)
- `assets/brand/ashlar-og-flat-1200x630.png` — Social preview card (ready for GitHub Settings upload)

---

## Document maintenance

**Owner:** CEO / founder  
**Last updated:** 2026-09-13 (P0 PRs #511/#512/#513/#514 merged 2026-09-06; limitations 7-8 closed by PR #523; branch protection verified live; limitation 9 closed 2026-09-13; design-partner go, autonomy marketing HOLD)  
**Next review:** Before public v0.x announcement  
**Update triggers:** Dogfood ledger shows dated Strict E2E passes, design partner converts to paid, CEO actions completed

**How to update:**
1. Close relevant GitHub issue → mark ✅ in section 1 or 2
2. CEO action completed → mark ✅ in section 5
3. Funnel metric hit → update section 6 success metrics
4. New risk identified → add row to section 7

**Who can update:** Any contributor can PR updates; CEO reviews before merge (affects business decisions).
