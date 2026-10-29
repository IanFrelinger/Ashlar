# Ashlar Marketing Site

This directory contains the professional commercial marketing landing page for Ashlar.

## Structure

- `index.html` — Main landing page (self-contained HTML+CSS)
- Clean, minimal developer-tool aesthetic (Linear/Vercel/Notion-adjacent)
- Bento-grid feature sections with asymmetric CSS grid tiles
- Uses only official brand assets (no AI-generated collage imagery)

## Brand Compliance

The site follows the official brand guidelines from `assets/brand/BRAND.md`:

- **Palette:** ink `#2B2420`, cream `#F7F2E5`, oat `#EFE8D8`, sage `#7E8F6E`, gold `#D1A23C`, clay `#C96F4A`, olive `#4A5540`
- **Official mark:** Trace (three fitted pieces); the header uses `ashlar-logo-flat.svg` (wordmark and mark), the favicon `ashlar-icon-trace.svg` (the mark alone)
- **Gold usage:** Certification-related only (cert gate badge), never decorative
- **Voice:** Precise, technical, premium craft — open-core commercialization, not hypey AI agent startup

## Design Philosophy

**Do:**
- Bento-grid asymmetric feature sections
- Large, clean typography with high contrast
- Intentional whitespace
- Official brand assets only (SVGs from `assets/brand/`)
- Real code and CLI snippets as evidence, each one checked against the repository (no mock-ups)
- Transparent pricing comparison table
- Fast static HTML/CSS
- Modern developer-tool aesthetic (2026 standard)

**Don't:**
- AI-generated collage imagery (seals, tape, doodles, sparkles, 3D masonry)
- Heavy decorative imagery
- Invented geometric logos
- Autonomy hype visuals

## Content Sections

1. **Hero** — "Turn AI-generated know-how into verified, reusable behavior", the D1 line ("The verified execution layer for AI agents in .NET"), a link to what is new in 0.4, and two CTAs
2. **Bento grid** — six tiles that fill a three-column grid:
   - Auditable workflows (with code example)
   - Checked before it runs: the admission gate's five checks (gold accent, sparse)
   - Your infrastructure
   - Build on Ashlar (NuGet/API integration)
   - In your editor and your CI: new in 0.4, the MCP brick host and the pr-blockers GitHub Action
   - Local-first operation
3. **See a receipt** — the Docker quickstart and the TesterQuickstart path
4. **Design Partner** — $4k for 90 days, at most 3 at a time
5. **Pricing Table** — Transparent comparison: Community (free), Pro ($1,500/yr), Team ($12,000/yr), Enterprise (from $40,000/yr, partner-led); Design Partner and Cloud waitlist in the footnote
6. **CTA** — See a receipt, Design Partner, Discord
7. **Footer** — Resources, community, legal links

## Deployment

`.github/workflows/pages.yml` publishes this directory to GitHub Pages, at
`https://ianfrelinger.github.io/Ashlar/`, on every push to `master` that touches `site/**`. Pages' source is
GitHub Actions, not a branch folder: that setting accepts only `/` or `/docs`, and `/docs` holds the engineering
docs, not the website. The workflow uploads `site/` alone and fails on any absolute `src` or `href`, because the
site is served under `/Ashlar/`; keep every reference relative.

### Local Testing

```bash
# Simple HTTP server
cd site
python3 -m http.server 8000
# Visit http://localhost:8000

# Or with Node.js
npx http-server .
```

## Assets Used

The page references only official brand assets:
- `assets/brand/ashlar-logo-flat.svg` — header logo (wordmark and mark)
- `assets/brand/ashlar-icon-trace.svg` — favicon (the Trace mark alone), copied from the repository's `assets/brand/`
- `ashlar-og-flat-1200x630.png` — OpenGraph social card, served from the repository's `assets/brand/` on raw.githubusercontent.com and rendered from `og-card-generator.html` with the same lockup

The terminal mock-up (`assets/brand/ashlar-terminal-preview.svg` at the repository root) is no longer on the page: it
shows the pre-rename "nexo" banner, v0.2.0 and invented counts.

No AI-generated marketing PNGs are used on the landing page.

## Messaging

All copy aligns with:
- Open-source runtime (Apache 2.0)
- Embeddable dependency via NuGet
- Receipts for what ran, and an admission gate for new code (no "guarantee" or "proven" wording; see `docs/marketing/claims-cheat-sheet.md`)
- Local-first operation with optional cloud
- Open-core commercialization model
- No claims ahead of the product: Cloud is waitlist only, SSO is marked Roadmap, evidence packs are planned

## Success Criteria

- ✅ Professional, modern developer-tool aesthetic
- ✅ Clean bento-grid layout
- ✅ Mobile-friendly responsive design
- ✅ Accessible contrast ratios
- ✅ Self-contained (no build step required)
- ✅ Official logo and brand colors only
- ✅ Accurate messaging (embeddable, audit+cert, local-first, open-core)
- ✅ No AI-generated collage imagery
- ✅ No Nexo branding
- ✅ Commercial pricing from the go-to-market decisions log (D2)
