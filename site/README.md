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
- **Official mark:** Node-"o" (bullseye + gold scribble ring) from `ashlar-logo-chaos.svg`
- **Gold usage:** Certification-related only (cert gate badge), never decorative
- **Voice:** Precise, technical, premium craft — open-core commercialization, not hypey AI agent startup

## Design Philosophy

**Do:**
- Bento-grid asymmetric feature sections
- Large, clean typography with high contrast
- Intentional whitespace
- Official brand assets only (SVGs from `assets/brand/`)
- Real UI/code/terminal snippets as proof (`ashlar-terminal-preview.svg`)
- Transparent pricing comparison table
- Fast static HTML/CSS
- Modern developer-tool aesthetic (2026 standard)

**Don't:**
- AI-generated collage imagery (seals, tape, doodles, sparkles, 3D masonry)
- Heavy decorative imagery
- Invented geometric logos
- Autonomy hype visuals

## Content Sections

1. **Hero** — Clean typography, "The .NET runtime you build AI products on" with CTAs
2. **Bento Grid Features** — Asymmetric grid tiles showcasing:
   - Auditable workflows (with code example)
   - Certification gate (gold accent, sparse)
   - Your infrastructure
   - Build on Ashlar (NuGet/API integration)
   - Terminal preview (using official SVG)
3. **Pricing Table** — Transparent comparison: Community (free), Pro ($1,500/yr), Team ($12,000/yr), Enterprise (from $40,000/yr, partner-led); Design Partner and Cloud waitlist in the footnote
4. **CTA** — GitHub and email contact
5. **Footer** — Resources, community, legal links

## Deployment

### GitHub Pages (Recommended)

1. Enable GitHub Pages in repository settings:
   - Settings → Pages
   - Source: Deploy from a branch
   - Branch: `master` (or your preferred branch)
   - Folder: `/site`

2. Site will be available at: `https://ianfrelinger.github.io/Ashlar/`

### Alternative: GitHub Pages with Docs

If using the docs path pattern:
- Move `site/` contents to `docs/marketing/` or `docs/site/`
- Configure Pages to serve from `/docs`

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
- `assets/brand/ashlar-logo-chaos.svg` — Official logo mark
- `assets/brand/ashlar-terminal-preview.svg` — Terminal session preview
- `assets/brand/ashlar-og-flat-1200x630.png` — OpenGraph social card

No AI-generated marketing PNGs are used on the landing page.

## Messaging

All copy aligns with:
- Open-source runtime (Apache 2.0)
- Embeddable dependency via NuGet
- Audit + certification guarantees
- Local-first operation with optional cloud
- Open-core commercialization model
- No false product claims (Cloud marked "coming soon / design partners")

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
- ✅ Commercial pricing framework (marked indicative)
