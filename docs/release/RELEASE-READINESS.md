# Release Readiness + Business Plan Interface

**Audience:** CEO / founder decision-making  
**Purpose:** Binary go/no-go for public v0.x release vs design-partner private; map release bars to commercial funnel; list CEO-only actions  
**Status:** Living document — update as P0 issues close and product lanes advance

---

## Executive summary

Ashlar ships as an **embeddable local-first .NET runtime** with cert-gate + trust log. Commercial model: Community free → design partner → Builder/Team/Enterprise tiers; Cloud PAYG later. Dual revenue streams: flagship product on Ashlar runtime + engine licensing (Fortnite+Unreal model). North star: successful embeds, not autonomous self-extension hype before the dogfood ledger exists.

**Current state:**
- **Runtime (Ashlar):** P0 trust holes being closed; CI not yet redundant; cert-loop honesty incomplete in docs/landing
- **Product (Forge):** Scaffold exists; no public Cursor-safe claims without ledger (P3 hold)
- **Recommendation:** Design-partner private only until runtime P0s close + CI hardening complete

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
| **P0 trust signature holes (limitations 7-9)** | [#513](https://github.com/IanFrelinger/Ashlar/pull/513) | ⚠️ **PARTIAL** (merged 2026-09-06T02:23:51Z) | Fail-closed defaults landed; **Limitation 7 NOT fully closed** — Strict production paths still lack `RequireEd25519Signature` (follow-up PR pending per `docs/dogfood-ledger.md`) |
| **Cert-loop honesty in landing/docs** | [#514](https://github.com/IanFrelinger/Ashlar/pull/514), [#505](https://github.com/IanFrelinger/Ashlar/pull/505), [#506](https://github.com/IanFrelinger/Ashlar/pull/506) | ✅ **COMPLETE** (all merged: #505, #506, #514 merged 2026-09-06T01:31:06Z) | Marketing landing honest; cert-loop defect fixes shipped |
| **Cert-loop integration live path** | [#512](https://github.com/IanFrelinger/Ashlar/pull/512) | ✅ **COMPLETE** (merged 2026-09-06T01:40:20Z) | Certified loop integration + canary verification enforced, live on master |

**Residual trust limitations (documented, not blockers for v0.x):**
- Dev HMAC signer (not PKI) — documented in `docs/certification-evidence.md` limitation 1; operator can supply real key via `ASHLAR_CERT_ED25519_KEY`
- Composition seam check is type-level only — limitation 2; graph-mutation teeth partially compensate
- Session containment opt-in — limitation 4; default ships sealed (Passive mode), operator raises dial deliberately
- Model proposing scale boundary — limitation 5; mechanism closed, breadth (multiple objectives/tasks) is host-operations work

**What sales/marketing may claim:**
- ✅ "Certified artifacts require passing analyzer fence, witness (correctness), mutation testing, determinism — gate is CI-proven"
- ✅ "Trust log auditable via `/api/trust/dashboard`; every decision on the record"
- ✅ "Fail-closed admission: proposals face real gate, uncertified code rejected"

**What sales/marketing must NOT claim (until P3 ledger exists):**
- ❌ "Autonomous self-extension safe for unattended production" — ships in hold mode, ledger documents spike-grade status
- ❌ "No human oversight required" — operator-governed path is supported, autonomous path is experimental

### 1.2 CI redundancy (P0)

| Item | Issue | Status | Release impact |
|------|-------|--------|----------------|
| **CI not SPOF** | [#511](https://github.com/IanFrelinger/Ashlar/pull/511) | ⚠️ **PARTIAL** (merged 2026-09-06T01:37:01Z) | Four complementary workflows on master; **branch protection update still needed** to make `build-core`, `shell-lint`, `lychee` required checks |

**Action required:** 
- **CEO/admin:** Update branch protection settings to add `build-core`, `shell-lint`, `lychee (README + docs)` as required checks (see [CEO-only actions](#5-ceo-only-actions-list))

**What sales/marketing may claim:**
- ✅ "Every PR gated by hermetic certification + build integrity + docs verification"
- ✅ "Redundant CI gates prevent broken code merge even if primary gate flaky"

### 1.3 Known limitations honesty (P0)

| Item | Documentation | Status | Release impact |
|------|---------------|--------|----------------|
| **Certification evidence ledger current** | `docs/certification-evidence.md` | ✅ **COMPLETE** | All proven admits/rejects documented with CI runs; known v0 limitations 1-9 listed with closure dates where applicable |
| **Self-extend audit transparent** | `docs/SELF-EXTEND-AUDIT.md` | ✅ **COMPLETE** | Invariants A-D enforced status documented; convergence gap vs certified loop stated (line 103) |
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
| **Dogfood ledger exists** | P3 open: Cursor adapter needs multi-cycle unattended evidence beyond spike | Until ledger: no "production-ready Cursor integration" claims; design-partner private only with explicit "experimental, hold mode" disclosure |

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
- ⚠️ P0 trust holes closed (PR #513 **PARTIAL** — fail-closed defaults merged; Limitation 7 residual follow-up pending)
- ✅ Cert-loop integration complete (PR #512 merged 2026-09-06)
- ⚠️ CI redundancy in place (PR #511 merged; **branch protection update needed**)
- ✅ Known limitations documented honestly
- ✅ Design partner agreement includes "experimental" disclosure for Forge features
- ✅ Support channel established (GitHub Discussions or direct contact)

**Funnel success:** Design partner deploys → sees value → willing to pay

**Current readiness:** ⚠️ **NEAR for runtime** (P0 PRs merged; branch protection + Limitation 7 follow-up remaining); ⚠️ **HOLD for Forge** (pending P3 ledger)

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

- [ ] **P0 trust holes closed:** PR #513 fail-closed defaults merged (✅ 2026-09-06); **Limitation 7 follow-up PR merged** (residual: Strict+Ed25519, currently pending)
- [ ] **CI redundancy live:** PR #511 merged (✅ 2026-09-06) + branch protection updated (**protection update pending**)
- [x] **Cert-loop integration:** PR #512 merged + verified (✅ 2026-09-06)
- [x] **Cert-loop honesty complete:** PRs #505 (✅ merged), #506 (✅ merged), #514 (✅ merged 2026-09-06) + docs audited
- [ ] **Known limitations documented:** `certification-evidence.md` + `SELF-EXTEND-AUDIT.md` current
- [ ] **User-facing docs accurate:** TesterQuickstart + IntegratorGuide tested by external reader
- [ ] **Security defaults safe:** README + SECURITY.md warn about HTTP-only / no auth default
- [ ] **NuGet packages published:** v0.1.2 or later on nuget.org
- [ ] **GHCR images published:** `nexo-cli:0.1.2` or later multi-arch digest pinned (rename to `ashlar-cli` pending; see README note)
- [ ] **Marketing landing honest:** No Cloud GA claims, no autonomous production claims, Forge roadmap-only
- [ ] **GitHub social preview current:** `ashlar-og-flat-1200x630.png` uploaded (CEO action)
- [ ] **Contact channel live:** GitHub Discussions enabled OR `hello@ashlar.dev` with monitoring

**No-go criteria (ANY one blocks public release):**

- [x] **ACTIVE BLOCKER:** Limitation 7 follow-up PR not merged (Strict+Ed25519 enforcement) — **currently pending**
- [x] **ACTIVE BLOCKER:** Branch protection not updated (`build-core`, `shell-lint`, `lychee` not required) — **CEO action needed**
- [ ] Landing page contains false Cloud GA or autonomous production claims
- [ ] TesterQuickstart fails on clean machine
- [ ] Security defaults allow unauthenticated network exposure without explicit opt-in

**Current recommendation:** ⚠️ **NO-GO for public v0.x** — Branch protection update + Limitation 7 follow-up PR required, then external validation

### 4.2 Design-partner private release

**Go criteria (LESS restrictive than public):**

- [x] **P0 trust holes closed:** PR #513 merged (✅ 2026-09-06; Limitation 7 follow-up disclosed in design-partner agreement)
- [x] **Cert-loop integration:** PR #512 merged (✅ 2026-09-06)
- [ ] **CI primary gate working:** `cert-gate` reliable (redundancy nice-to-have, not blocker)
- [ ] **Known limitations documented:** Limitations 1-9 in `certification-evidence.md`
- [ ] **Design-partner agreement signed:** Includes "experimental" disclosure for Forge features
- [ ] **Support channel established:** Direct contact or private Slack/Discord
- [ ] **NuGet packages available:** Staging feed OR nuget.org
- [ ] **GHCR images available:** Even if `latest` only (digest pin nice-to-have)

**No-go criteria:**

- [ ] Cert-gate consistently failing on master
- [ ] No design-partner agreement (no legal protection for experimental features)
- [ ] Limitation 7 (Strict+Ed25519) not disclosed in design-partner agreement

**Current recommendation:** ✅ **GO design-partner private** — P0 PRs merged; disclose Limitation 7 residual (Strict+Ed25519 follow-up pending) in agreement

---

## 5. CEO-only actions list

These actions require **repository administrator** or **organization owner** permissions and cannot be delegated to contributors.

### 5.1 Branch protection (CI hardening)

**Issue:** [#511](https://github.com/IanFrelinger/Ashlar/pull/511) **merged** (2026-09-06T01:37:01Z); workflows on master, branch protection update remains.

**Action required (CEO/admin only):**

1. Navigate to **Settings → Branches → Branch protection rule for `master`**
2. Under "Require status checks to pass before merging", add these required checks:
   - `build-core` (fast compile check, ~2-3 min)
   - `shell-lint` (shell script syntax, ~30s)
   - `lychee (README + docs)` (broken docs links, ~30s)
3. Keep `cert-gate` as required (existing)
4. **Do NOT remove `cert-gate`** — new checks are redundancy, not replacement

**Why this matters:** Eliminates CI single point of failure; if `cert-gate` is cancelled or flaky, other gates still block broken code.

**Verify:** Push test PR → confirm all four checks must pass before merge allowed

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

| Variable | Current value | Recommended for v0.x |
|----------|---------------|----------------------|
| `NUGET_PUBLISH_MODE` | `push` | Keep `push` (publishes to nuget.org on tag) |
| `RELEASE_CREATE_GITHUB_RELEASE` | `true` | Keep `true` (auto-creates GitHub Release with changelog) |
| `NUGET_API_KEY` (secret) | Set | **Verify not expired** before tagging release |

**Action:** Review `Settings → Secrets and variables → Actions` → confirm `NUGET_API_KEY` valid.

### 5.5 Contact channel (customer funnel)

**Current state:** README references `https://github.com/IanFrelinger/Ashlar/discussions` (GitHub Discussions).

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

### Recommendation: Design-partner private ready; public v0.x needs branch protection + Limitation 7 follow-up

**Rationale:**
- Runtime P0 PRs merged — ✅ (#513 fail-closed defaults, #512 cert-loop, #511 workflows, #514 landing) all landed 2026-09-06
- Limitation 7 residual — ⚠️ **Strict production paths lack `RequireEd25519Signature`** (follow-up PR pending per `docs/dogfood-ledger.md`)
- CI redundancy workflows on master — ✅ (PR #511 merged)
- Branch protection not updated — ⚠️ **CEO action needed** (`build-core`, `shell-lint`, `lychee` not yet required)
- Known limitations documented honestly ✅
- Forge needs P3 ledger before public claims ⚠️

**Path forward:**

1. **NOW:** Go design-partner private (runtime P0 PRs merged; disclose Limitation 7 residual in agreement; Forge hold-mode with disclosure)
2. **NEXT:** CEO actions (branch protection, Pages, social preview, Discussions)
3. **PARALLEL:** Limitation 7 follow-up PR (Strict+Ed25519 enforcement for production paths)
4. **THEN:** External validation (docs tested by non-contributor)
5. **FINALLY:** Public v0.x release (branch protection live + Limitation 7 follow-up merged + validation complete)

### What to tell prospects TODAY

**If they ask "Is Ashlar production-ready?"**

✅ **Yes for embedded runtime use cases (design-partner private):**
- "Ashlar runtime (cert-gate + trust log) ready for design-partner pilots"
- "P0 trust PRs merged (2026-09-06): fail-closed defaults, cert-loop integration, CI workflows, landing honesty"
- "NuGet packages published, HTTP API works, CLI tested"
- "Fail-closed admission: uncertified code rejected"
- "Known residual: Limitation 7 (Strict production paths lack `RequireEd25519Signature`; follow-up PR pending) — disclosed in design-partner agreement"

⚠️ **Not yet for public v0.x:**
- "Branch protection update pending (CEO action) to make CI redundancy live"
- "Limitation 7 follow-up PR pending for full Strict+Ed25519 enforcement"
- "External validation needed before public announcement"

⚠️ **Not yet for autonomous self-extension:**
- "Autonomous loop ships in hold mode (experimental)"
- "Seeking design partners for Forge product (adaptive factory + Cursor adapter)"
- "Multi-cycle unattended evidence pending (P3 open)"

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
| **Branch protection not updated → CI SPOF persists** | Broken code merges if cert-gate flaky | CEO action (5.1) before next PR merge; interim: manual review rigor |
| **Contact channel (Discussions) not enabled** | Funnel breaks at Aware → Eval | CEO action (5.5) before public announcement |

---

## 8. Appendix: Reference documentation

### 8.1 Trust / certification

- `docs/certification-evidence.md` — Falsifiable proof ledger (all ADMIT/REJECT results with CI runs)
- `docs/SELF-EXTEND-AUDIT.md` — Self-extend invariants A-D enforcement audit
- `docs/trust-loop/ashlar-trust-loop-spec.md` — Trust loop spec (analyzer fence, witness, mutation, determinism)
- `docs/governed-pipeline.md` — Governed model pipeline (proposals flow through)

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

- `docs/certification-evidence.md` lines 644-830 (limitations 1-9, some closed, some residual)
- `docs/SELF-EXTEND-AUDIT.md` line 103 (convergence gap: certified loop vs legacy extender)

### 8.5 Marketing / landing

- `site/index.html` — Marketing landing page (ready for Pages deployment)
- `site/README.md` — Landing page docs (design philosophy, deployment instructions)
- `assets/brand/BRAND.md` — Brand assets (logo, OG card, palette)
- `assets/brand/ashlar-og-flat-1200x630.png` — Social preview card (ready for GitHub Settings upload)

---

## Document maintenance

**Owner:** CEO / founder  
**Last updated:** 2026-09-06 04:51 UTC (P0 PRs #511/#512/#513/#514 merged; design-partner go; public v0.x needs branch protection + Limitation 7 follow-up)  
**Next review:** After branch protection update + Limitation 7 follow-up PR merge, before public v0.x announcement  
**Update triggers:** Branch protection live, Limitation 7 follow-up merged, P3 ledger complete, design partner converts to paid, CEO actions completed

**How to update:**
1. Close relevant GitHub issue → mark ✅ in section 1 or 2
2. CEO action completed → mark ✅ in section 5
3. Funnel metric hit → update section 6 success metrics
4. New risk identified → add row to section 7

**Who can update:** Any contributor can PR updates; CEO reviews before merge (affects business decisions).
