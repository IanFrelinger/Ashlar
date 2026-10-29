# Ashlar Brand Kit

Palette: ink `#2B2420` · cream `#F7F2E5` · oat `#EFE8D8` · sage `#7E8F6E` · gold `#D1A23C` · clay `#C96F4A` · olive `#4A5540`

Type: Baloo 2 ExtraBold (wordmark). Embedded as subsets inside the SVGs. **Known gap:** the subset in `ashlar-logo-flat.svg` holds only the glyphs of the old name, "nexo", so browsers draw "Ashlar" in their fallback bold sans until the subset is rebuilt.

## Product Name & Positioning

**Name:** Ashlar (not "nexo")  
**One-liner:** Local-first .NET runtime for auditable AI workflows you embed  
**Everyday frame:** "Receipts for AI actions" + "Bouncer for new bricks"  
**Precision line:** Trust log + cert-gate

## Where each file goes

| File | Destination |
|------|-------------|
| `ashlar-logo-flat.svg` | The wordmark with the Trace mark. README hero — **wired**, centred `<img>` at the top of `README.md`; a copy in `site/assets/brand/` is the site header |
| `ashlar-icon-trace.svg` | The Trace mark alone on cream (512×512); a copy in `site/assets/brand/` is the site's favicon |
| `ashlar-icon-nuget-128.png` | NuGet package icon — **wired** repo-wide in `Directory.Build.props` (see below). Still the previous node-"o" artwork |
| `ashlar-icon-github-512.png` | GitHub org/repo avatar — Settings → upload. Still the previous node-"o" artwork |
| `ashlar-icon-github-clean.svg` | The previous node-"o" avatar (see below); the site's favicon is now `ashlar-icon-trace.svg` |
| `ashlar-og-flat-1200x630.png` | **Primary OG card** — flat, typographic social card (1200×630), rendered from `og-card-generator.html` with the Trace lockup; **wired** in `site/index.html` `<meta property="og:image">`; also suitable for GitHub Settings → Social preview |
| `ashlar-social-card-1280x640.png` | Alternative social preview (1280×640) — deprecated, prefer OG flat |
| `ashlar-terminal-preview.svg` | Mock CLI session. **Not used on the site:** it still shows the pre-rename "nexo" banner, v0.2.0 and invented counts, so redraw it before any reuse |
| `marketing/experiments/` | *Archive* — old "nexo" collage branding with tape/sparkles (not used on primary surfaces) |
| `*.svg` sources | keep in `assets/brand/` as the editable masters |

**Design:** The landing page (`site/index.html`) and OG card use a clean, modern developer-tool aesthetic (Linear/Vercel/Notion-adjacent): flat, typographic, official brand assets only. Old collage imagery (tape, doodles, sparkles, "nexo" wordmark) is archived in `marketing/experiments/` and not used on primary marketing surfaces.

## GHCR Image Names

**Note:** The published GHCR container images (`nexo-cli`, `nexo-api`) retain their original names until the next republish. This is intentional to avoid breaking existing deployments. When referencing images:
- Release pin: `ghcr.io/ianfrelinger/nexo-cli:0.4.2` (published with `v0.4.2`; `deploy/node.yml` pins its digest)
- Future: `ghcr.io/ianfrelinger/ashlar-cli:X.Y.Z` (post-republish)

See `docs/DistributionModels.md` for image usage and pinning guidance.

## GitHub Avatar Upload

**Recommended:** Use `ashlar-icon-trace.svg` for the next avatar upload (GitHub Settings → Picture): the Trace mark alone, without collage elements. `ashlar-icon-github-clean.svg` is the previous node-"o" avatar.

Wired repo-wide in `Directory.Build.props`; every packable project picks it up, no per-project
change needed:

```xml
<PackageIcon Condition="Exists('$(MSBuildThisFileDirectory)assets/brand/ashlar-icon-nuget-128.png')">icon.png</PackageIcon>
...
<ItemGroup Condition="'$(IsPackable)' == 'true' AND Exists('$(MSBuildThisFileDirectory)assets/brand/ashlar-icon-nuget-128.png')">
  <None Include="$(MSBuildThisFileDirectory)assets/brand/ashlar-icon-nuget-128.png" Pack="true" PackagePath="icon.png" Visible="false" />
</ItemGroup>
```

The asset is packed straight from `assets/brand/` rather than copied to a root `icon.png`, so
there is one copy of the bytes. Both parts are required: `PackageIcon` alone (without the packed
`None`) fails packing with NU5046, exactly as `PackageReadmeFile` alone fails with NU5039.

## Signature rules

- The Trace mark (three fitted pieces: dark green `#1B2B22`, sage `#76896C`, ochre `#B78543`) is the mark. Use the paths as drawn in `ashlar-logo-flat.svg`; don't outline, recolor or rearrange the pieces. The node-"o" (bullseye + gold scribble ring) is the previous mark.
- Gold means certified — in the terminal and everywhere else. Never use it decoratively.
- Collage elements (tape, sparkles, doodles, `certified!` notes) belong in `marketing/experiments/` only. Do not use them on hero/social primary surfaces or icon-only marks.
