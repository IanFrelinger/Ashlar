# Monetization and product design (v1)

> **Superseded for self-host tiers (2026-10-25).** Current tiers and prices are row D2 of the [go-to-market decisions log](commercial/go-to-market.md#decisions-log-added-2026-10-25): Community $0, Pro $1,500, Team $12,000, Enterprise from $40,000 per organisation per year. The ranges below are kept as the calibration history.

This document defines a **dual-motion** model: a **Cursor-style hosted lane** for fast adoption, and an **infrastructure / sovereignty lane** for teams that need control, auditability, and procurement-friendly contracts. Numbers are **starting points** for calibration with design partners—not legal or tax advice.

---

## 1. Strategic frame

**Primary narrative (unchanged):** private, traceable AI workflows on infrastructure the customer trusts—with enforced standards and an audit trail.

**Secondary lane (additive):** **Ashlar Cloud** as an *opt-in* hosted product for individuals and small teams who want speed more than VPC isolation on day one. Enterprise buyers should never feel that “cloud-only” is the only serious product.

**Principle:** charge for **software + operational responsibility + scale**. Treat **LLM tokens** as either **BYOK** (you take little token risk) or **metered credits** (you take token risk but cap exposure).

---

## 2. Product surfaces (what exists in the world)

| Surface | Who | Where compute runs | Your COGS risk |
|--------|-----|---------------------|-----------------|
| **Ashlar Cloud** | Individuals, startups, small teams | Your AWS (multi-tenant) | Medium (compute, egress, logs); **high if you fund tokens** |
| **Ashlar Team (BYOC)** | Same segment, security-minded | Customer cloud / VPC | Low (they pay cloud + LLM) |
| **Ashlar Enterprise** | Mid-market and regulated orgs | Dedicated VPC, their VPC, or air-gapped | Depends on offer; price for **people + SLA** |

You can ship **one codebase** with **feature flags + entitlements** and different **deployment templates** (shared vs dedicated).

---

## 3. Packaging matrix (tiers)

### 3.1 Ashlar Cloud (Cursor-style self-serve)

| Tier | Buyer | Price (USD) | Billing | Seats | Core value |
|------|--------|-------------|---------|-------|------------|
| **Cloud Free** | Evaluators | $0 | None | 1 | Time-boxed org, watermarked exports or short retention, **BYOK required** for external models |
| **Cloud Starter** | Solo dev, indie | **$19 / seat / mo** | Monthly card | 1–3 | Audited copilot/task flow, activity feed, standard retention |
| **Cloud Team** | Small product eng team | **$39 / seat / mo** (min **$79 / mo** for 2+ seats) | Monthly or annual (−15%) | up to 25 | Shared workspaces, higher limits, changelog assistant |

**Included usage (protects you):**

- **Jobs per seat per month:** Starter **300**, Team **1,200** (job = one submitted copilot/agent task through your API or portal; define precisely in product copy).
- **Audit / artifact storage:** Starter **5 GB** org total; Team **25 GB**; overage **$0.15/GB-mo** (illustrative).
- **Concurrency:** Starter **1** active job; Team **3**.

**Overage (simple, predictable):**

- **$0.08–$0.15 per job** above included bucket (pick one number after you measure AWS marginal cost per job).
- Optional: **$12 / seat / mo** “Priority” add-on: doubles included jobs + email support SLA (next business day).

**LLM policy for Cloud (recommended v1):**

- **Default BYOK** for OpenAI/Azure/Ollama gateway: customer enters keys in tenant vault; you never bill tokens.
- Optional **“Hosted inference add-on”** later: you resell with **25–40% margin** *or* **pass-through + 10% platform fee**—only after metering is solid.

---

### 3.2 Ashlar Team (self-hosted / customer infra)

For teams that want the sovereignty story without enterprise procurement.

| Tier | Price (USD) | Billing | Notes |
|------|-------------|---------|--------|
| **Team Self-Host** | **$49 / seat / mo** (min **$149 / mo**) | Annual preferred (−20%) | License key or private registry; **customer** pays AWS/LLM |
| **Team Plus** | **$99 / seat / mo** (min **$299 / mo**) | Annual | SSO (SAML/OIDC), audit export (SIEM-friendly), longer retention guidance |

**Entitlements:** same job/storage buckets as Cloud Team by default, but enforcement is **honor + license** unless you add **telemetry-based metering** (phone home with counts only, configurable for air-gap).

---

### 3.3 Ashlar Enterprise

| Element | Guidance |
|--------|------------|
| **Price** | **$35k–$120k ARR** entry for “single workflow + single region + business-hours support”; **$120k–$400k+** for multi-workflow, SLA, dedicated VPC, or regulated vertical packaging |
| **Contract** | Annual, **quarterly true-up** on seats or environments |
| **Minimum** | **$25k/year** minimum spend if they want custom security questionnaire + named CSM |
| **Professional services** | **$150–$250/hr** blended, or fixed **$15k–$60k** “production readiness” packages (deploy, policy tuning, integration) |

**Enterprise includes:** SSO, audit export, retention policies, environment-based billing option, security pack (questionnaire answers, architecture PDF), roadmap input—not unlimited custom dev unless SOW.

---

## 4. Entitlements (implementation-oriented)

These are the **knobs** your billing and feature gates should share:

| Key | Type | Example values by tier |
|-----|------|-------------------------|
| `deployment_mode` | enum | `cloud_shared`, `customer_vpc`, `air_gapped` |
| `max_seats` | int | Free 1, Starter 3, Team 25, Enterprise contract |
| `included_jobs_per_seat_month` | int | 0 (free trial cap only), 300, 1200, custom |
| `max_concurrent_jobs` | int | 1, 3, 10, custom |
| `retention_days_audit` | int | 7, 90, 365, custom |
| `storage_gb_included` | int | 1, 5, 25, custom |
| `sso_enabled` | bool | false until Team Plus / Enterprise |
| `audit_export_enabled` | bool | false until Team Plus / Enterprise |
| `byok_required` | bool | true on Free; false optional above |
| `sla_tier` | enum | `none`, `next_business_day`, `4h`, `custom` |
| `support_channel` | enum | `docs`, `email`, `shared_slack`, `named_csm` |

---

## 5. Billing mechanics (how charges hit the card)

**Self-serve (Stripe Billing):**

- Products: **per-seat prices** with **minimum quantity** where needed.
- **Metered components:** use Stripe usage records (or a weekly batch) for **overage jobs** and **storage GB-mo** above included.
- **Annual prepay:** invoice line for seats + **prepaid job credits** (optional).

**Enterprise:**

- Order form: **platform fee + included seats + included environments + overage schedule**.
- Invoicing: ACH/wire; Stripe Invoicing or manual is fine early.

**Never bundle “unlimited LLM”** on your dime in v1.

---

## 6. Product elements (UX and scope—not just pricing)

**Onboarding**

- **Cloud:** wizard = identity → workspace → **BYOK or mock provider** → first audited task in **&lt; 10 minutes**.
- **Self-host:** wizard in docs + CLI + “health check” container; paid Team gets **license key** + priority doc links.

**Trust surfaces (sellable artifacts)**

- One-page **architecture** (where keys live, what leaves the tenant, retention).
- **Audit trail export** format and sample (Pro+).
- **Subprocessor list** only if you run hosted workloads with third parties.

**In-product upgrade path**

- Cloud Free → Starter: unlock seats, retention, remove watermark.
- Cloud Team → Enterprise: “Talk to us” when they hit SSO, VPC, SLA, or compliance checklist.

**Support boundaries (write on the pricing page)**

- Starter: docs + community + **best-effort** email.
- Team: **2 business day** email.
- Enterprise: named channel + severity-based response targets in contract.

---

## 7. GTM alignment (who you chase first)

**First 6 months (sequencing):**

1. **3–5 design partners** on **Enterprise-lite** ($15k–$40k pilots) *or* **Team Self-Host** annual if they refuse cloud—prove value and references.
2. Turn repeatable deploy steps into **Cloud Team** self-serve once isolation and metering exist.
3. Only then push **Cloud Free** broadly if CAC and abuse are under control.

**ICP sentence (keep on homepage):**  
“For software teams that need **auditable AI workflows** on **infrastructure they control**—with an optional **hosted** path to start fast.”

---

## 8. Metrics to prove the model

| Metric | Why |
|--------|-----|
| Time-to-first audited job | Activation |
| Jobs / seat / month (p50 vs p95) | Overage revenue + COGS |
| % tenants on BYOK | Margin health |
| Support hours / $ ARR | Pricing vs scope |
| Cloud → Enterprise upgrade rate | Dual motion working |

---

## 9. What to defer (avoid scope creep)

- Marketplace resale of third-party SaaS.
- Complex revenue-share with cloud providers until you have volume.
- “Unlimited seats” enterprise deals without a **true-up** clause.
- Building your own foundation model hosting before cloud path is stable.

---

## 10. Summary table (at-a-glance)

| Offering | From (USD) | Motion |
|----------|------------|--------|
| Cloud Free | $0 | PLG / eval, BYOK |
| Cloud Starter | $19/seat/mo | Cursor-style |
| Cloud Team | $39/seat/mo (min $79) | Small teams, hosted |
| Team Self-Host | $49/seat/mo (min $149) | Sovereignty, they pay infra |
| Team Plus | $99/seat/mo (min $299) | SSO + audit export |
| Enterprise | $35k+ ARR | VPC, SLA, PS |

---

## Revision

Treat this as **v1**. After **10 paying conversations**, adjust: seat caps, included jobs, and whether Cloud Team minimum should move to **$99/mo** if support load is high.
