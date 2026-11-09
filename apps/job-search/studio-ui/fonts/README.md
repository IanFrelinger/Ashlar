# Inter, self-hosted

The studio ships its own copy of Inter so that every person sees the same type, and so that QA screenshots show what
users see. Nothing is fetched from anyone else: the font is served from this directory under the page's own
`font-src 'self'`.

| File | What | Bytes | SHA-256 |
|---|---|---|---|
| `inter-var-4.1-latin.woff2` | Inter Variable 4.001, Latin subset, `opsz` and `wght` axes | 73,228 | `8861a1d93a2cad95a0e3e875f6989de73f3c00e2d147a0f51dd8901c0fecfc94` |
| `inter-var-4.1-latin-ext.woff2` | Latin Extended subset, loaded only for text that needs it (`unicode-range`) | 129,440 | `95932cc43fa1d7d046191a8723f4f52cb560a6fc7526d84e627646d9a6807337` |
| `LICENSE-Inter.txt` | SIL Open Font License 1.1, from the release | | |

**Source.** `https://github.com/rsms/inter/releases/download/v4.1/Inter-4.1.zip`
(SHA-256 `9883fdd4a49d4fb66bd8177ba6625ef9a64aa45899767dde3d36aa425756b11e`), file `web/InterVariable.woff2`
(SHA-256 `693b77d4f32ee9b8bfc995589b5fad5e99adf2832738661f5402f9978429a8e3`). Subset on 2026-11-09 with fontTools 4.68:

```
pyftsubset InterVariable.woff2 --flavor=woff2 --output-file=inter-var-4.1-latin.woff2 \
  --unicodes="U+0000-00FF,U+0131,U+0152-0153,U+02BB-02BC,U+02C6,U+02DA,U+02DC,U+0304,U+0308,U+0329,U+2000-206F,U+20AC,U+2122,U+2191,U+2193,U+2212,U+2215,U+FEFF,U+FFFD" \
  --layout-features+=tnum,case,ss02,zero
pyftsubset InterVariable.woff2 --flavor=woff2 --output-file=inter-var-4.1-latin-ext.woff2 \
  --unicodes="U+0100-02BA,U+02BD-02C5,U+02C7-02CC,U+02CE-02D7,U+02DD-02FF,U+0304,U+0308,U+0329,U+1D00-1DBF,U+1E00-1E9F,U+1EF2-1EFF,U+2020,U+20A0-20AB,U+20AD-20C0,U+2113,U+2C60-2C7F,U+A720-A7FF" \
  --layout-features+=tnum,case,ss02,zero
```

**Licence.** Inter is © 2016 The Inter Project Authors, under the SIL Open Font License 1.1 with no Reserved Font
Name, so a subset may be bundled and redistributed with this software provided the licence travels with it
(`LICENSE-Inter.txt`). The font is not sold on its own. No italic is shipped: the UI uses none.

**Fallback.** `app.css` declares an "Inter Fallback" face over the system sans with `size-adjust` and metric
overrides, so text set before the font arrives sits at the same size and does not jump.
