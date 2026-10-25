# Go-to-market

How Ashlar is positioned, priced and distributed. The open-versus-paid boundary itself is
[`LICENSING.md`](../../LICENSING.md) and [`docs/OpenCoreBoundary.md`](../OpenCoreBoundary.md); this page does not
change it. Public copy follows [`docs/marketing/claims-cheat-sheet.md`](../marketing/claims-cheat-sheet.md).

## Decisions log (added 2026-10-25)

On 2026-10-25 the owner delegated the go-to-market decisions and approved every recommended default, D1–D10.
Each row below is one of those defaults, recorded as an owner decision by delegation. Anything not covered here is
an integrator decision and is recorded in the PR that takes it.

Owner-only regardless of these rows: signing contracts, discounts or pricing exceptions, refunds, legal or tax
filings, creating accounts or logging in on registries and marketplaces, spending money, and statements about
security incidents.

| Date | Applies to | Decision |
|---|---|---|
| 2026-10-25 | D1 positioning and wedge (owner-delegated default) | Lead with "the verified execution layer for AI agents in .NET" and the tagline "Your agents decide what to do. Ashlar decides what is allowed to run, and keeps the receipt." Public copy says **bricks**, not "skills" ("skills" is the Agent Skills standard's term). The paid wedge is release evidence for regulated .NET organisations; generic release and PR automation is the free on-ramp. The locked one-liner in `docs/marketing/positioning.md` stays canonical. |
| 2026-10-25 | D2 pricing tiers (owner-delegated default) | Community $0. Design Partner $4,000 per 90 days, at most 3 at a time, credited against a first Team year. Pro $1,500 per org per year, paid by card, no sales call. Team $12,000 per org per year. Enterprise from $40,000 per year, partner-led only. Cloud stays a waitlist. The "Builder ~$8k" tier is retired, and the earlier Team and Enterprise anchors are replaced by these. Pricing is flat per organisation: no seat counting, no usage metering, no telemetry. |
| 2026-10-25 | D3 legal entity and billing (owner-delegated default) | Sell through a company entity (LLC or equivalent, confirmed with an accountant). Pro is sold through a merchant of record (for example Paddle or Lemon Squeezy, chosen on current fees) so sales tax and VAT are handled by it; Team and Enterprise are invoiced. No GitHub or Azure Marketplace billing in the first year. |
| 2026-10-25 | D4 open-core boundary (owner-delegated default) | No change to `LICENSING.md`. The MCP brick host and the GitHub and release-evidence bricks are open source. Paid means the assurance subscription, Fleet, and new Tier 3 vertical packs (for example a compliance-mapped Release Evidence Pack). |
| 2026-10-25 | D5 distribution channels (owner-delegated default) | Now: nuget.org (with the `McpServer` package type and templates), the official MCP Registry, and a free, open-source GitHub Action that runs the `github.blockers` brick on pull requests. Later: skills.sh (month 3 or later, optional), the Docker MCP catalog (month 4 or later), an Azure Marketplace bring-your-own-licence container (month 7 or later, on request). Deferred: a paid GitHub Marketplace app. |
| 2026-10-25 | D6 owner time and agent autonomy (owner-delegated default) | Owner time is 3 hours a week steady state, 6 in the launch month, with a hard ceiling of 5 after launch. Every go-to-market channel starts at autonomy level L0: agents draft, the owner publishes. |
| 2026-10-25 | D7 partner terms (owner-delegated default) | Referral partners receive 20% of the first-year licence; resellers 25–30% margin; implementation partners keep 100% of their services revenue. Counsel reviews the agreement template before its first use. |
| 2026-10-25 | D8 decision queue and pipeline (owner-delegated default) | The go-to-market decision queue, pipeline and customer names live in a private repository (`ashlar-gtm`, created by the owner), never in this public repository. Public GitHub Discussions are for community only. |
| 2026-10-25 | D9 token-savings claims (owner-delegated default) | No token-savings percentages in public copy until a live token benchmark is published in this repository. The benchmark's method and result are published whatever the result. |
| 2026-10-25 | D10 design-partner delivery (owner-delegated default) | Async-first, in a shared thread or document, with one 45-minute kickoff call. At most 3 design partners at a time. An agent drafts each scope; the owner approves it. |
