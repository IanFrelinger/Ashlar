# Ashlar Brand Marketing Assets

This directory is for marketing and social media assets.

## Structure

- `experiments/` — Archive of experimental/AI-generated assets (not used on landing page)
- Parent directory (`assets/brand/`) contains official brand assets used in production

## Official Assets (Used)

The landing page and social sharing use only official brand assets from the parent directory:
- `../ashlar-logo-flat.svg` — header logo (wordmark and mark)
- `../ashlar-icon-github-clean.svg` — the mark alone; a copy is the site's favicon
- `../ashlar-og-flat-1200x630.png` — flat, typographic OG card, wired in `site/index.html`
- `../ashlar-social-card-1280x640.png` — older social card (alternative)

`ashlar-logo-chaos.svg` is in `experiments/`, and `../ashlar-terminal-preview.svg` is no longer on the site: it
shows the pre-rename "nexo" banner and v0.2.0.

## Experimental Assets (Not Used)

See `experiments/` subdirectory for AI-generated assets that were created but don't align with the clean, modern developer-tool aesthetic (Linear/Vercel/Notion-adjacent) we're targeting.

## Creating the Flat OG Card

Generate `../ashlar-og-flat-1200x630.png` from `../og-card-generator.html`:

### Method 1: Browser Screenshot
1. Open `../og-card-generator.html` in Chrome/Firefox
2. Open DevTools → Toggle device toolbar
3. Set viewport to exactly 1200x630
4. Take screenshot: Cmd/Ctrl+Shift+P → "Capture screenshot"
5. Save as `../ashlar-og-flat-1200x630.png`

### Method 2: Command Line (if Playwright installed)
```bash
cd /workspace
npx playwright screenshot \
  file://$(pwd)/assets/brand/og-card-generator.html \
  assets/brand/ashlar-og-flat-1200x630.png \
  --viewport-size=1200,630
```

### Method 3: Puppeteer Script
```javascript
const puppeteer = require('puppeteer');
(async () => {
  const browser = await puppeteer.launch();
  const page = await browser.newPage();
  await page.setViewport({ width: 1200, height: 630 });
  await page.goto('file:///workspace/assets/brand/og-card-generator.html');
  await page.screenshot({ path: 'assets/brand/ashlar-og-flat-1200x630.png' });
  await browser.close();
})();
```

## Brand Guidelines

All active marketing assets follow brand guidelines from `../BRAND.md`:
- **Palette:** ink `#2B2420`, cream `#F7F2E5`, oat `#EFE8D8`, sage `#7E8F6E`, gold `#D1A23C`, olive `#4A5540`
- **Official mark:** node-"o" (bullseye + gold scribble ring)
- **Gold:** Certified cue only (dot/ring), never decorative spam
- **Voice:** Precise, technical, premium craft
- **Aesthetic:** Clean, typographic, modern developer-tool (not AI collage/poster)

## GitHub Social Preview

Repository Settings → General → Social preview should use:
- `../ashlar-og-flat-1200x630.png` (preferred, flat typographic design) — once generated
- `../ashlar-social-card-1280x640.png` (alternative, existing clean design)
