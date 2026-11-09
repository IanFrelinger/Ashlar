# Job Search Studio: the UI design

**Status:** built 2026-11-09 (`studio-ui/`), after a research pass and three judged directions. This page is the
standing brief: what the UI is, the rules it keeps, and the contract the tests hold it to. Change the UI, and change
this page with it.

## What it is

The studio is a well-set document, not a dashboard. One sheet of warm paper, structure carried by type size, weight,
a 4px rhythm and 1px hairlines. The eye lands on one thing per screen: Sign in · Continue · Your steps · the job
title · Run a search · Confirm all · Delete.

Where this comes from: the research found that the products people call high-craft (Linear, Vercel's Geist, Stripe,
Things, Craft) converge on a few rules, and that the old UI broke most of them: five competing hues, a border plus a
shadow plus nesting on every card, fourteen font sizes, status as filled pills, a phone header that wrapped to three
lines. Linear's own words for the goal: "Structure should be felt not seen" and "Don't compete for attention you
haven't earned" ([Behind the latest design refresh](https://linear.app/blog/behind-the-latest-design-refresh)).
Geist's colour scales keep every semantic colour separate from the gray ladder and guarantee it reads at 4.5:1
([Geist colors](https://vercel.com/geist/colors)). For consent, the ICO's guidance that consent must be a clear,
separate, unticked act, and the EDPB's catalogue of deceptive patterns, shaped the welcome page
([ICO: consent](https://ico.org.uk/for-organisations/uk-gdpr-guidance-and-resources/lawful-basis/a-guide-to-lawful-basis/consent),
[EDPB guidelines 03/2022](https://www.edpb.europa.eu/our-work-tools/our-documents/guidelines/guidelines-032022-deceptive-design-patterns-social-media_en)).

## The rules

| Rule | In practice |
|---|---|
| One accent | `--accent` on the one primary action per region, the focus ring, prose links, the stage-4 numeral and rail. Nothing else is coloured. |
| Semantic colour only as a dot beside a word | `.status .dot` in `--good/--warn/--bad`, or a 3px left rule on an error. **No filled pill anywhere**; a UI test asserts zero `.badge` elements. |
| Two bordered levels at most | A step card holds fields; nothing else is boxed. Built-in stages are rows on a rail. `.tonal` fills replace boxes for disclosure bodies and the evidence prompt. |
| Shadows only float | `.savebar` and `.toast`. Everything else is a hairline or a tonal step. |
| A hand-picked px scale | 12/13/14/15/16/18/22/28/36 (18 is the stage and palette headings), weights 400 and 500, 600 for the wordmark, the primary button and the sign-in hero. Line length ≤60ch for anything longer than a sentence. |
| 4px grid | `--s1..--s8` = 4/8/12/16/24/32/48/64. Radii 6 (controls) and 10 (cards). |
| Dark mode is the same sheet re-lit | Three tonal steps (`#141517 / #1C1D20 / #25272B`), lighter means higher; the accent is lifted and desaturated with dark text on fills; hairlines are white at 10%. Never inverted. |
| Motion is small and optional | 120/180ms ease-out on colour and opacity; one looping animation (the pulsing dot, always beside a word); `prefers-reduced-motion` turns it to a slow pulse and removes the rest. |
| Every fact lives once | The allowance appears once (in the palette); the model facts once (foot of the pipeline, and Privacy); the trust facts once per page (`trustStrip()`). |
| Nothing from anyone else | The CSP is `default-src 'self'`. Icons are inline SVG; the select chevron is a `data:` URI; the favicon is a file; the font is served from this origin. **Inline style attributes are blocked by the CSP**, so spacing is classes (`.mt1`…), never `style=`. |

Tokens live at the top of `studio-ui/app.css`. Every text pair was checked at ≥4.7:1 on the surface it sits on, in
both schemes, and every control border at ≥3.8:1.

## The font

Inter Variable 4.001 (OFL 1.1), subset to Latin and Latin Extended and served from `studio-ui/fonts/`
([README](../studio-ui/fonts/README.md) has the source, the hashes and the command). Two reasons: everyone sees the same
type, and QA screenshots show what users see. The QA container maps `system-ui` to Inter anyway, so without a shipped
font the screenshots never showed a Mac or Windows user's rendering. An "Inter Fallback" face with `size-adjust`
keeps text from jumping while the file loads.

## The shell

- Desktop (>720px): a 52px bar on the paper, brand mark and wordmark left, the five pages centred with a 2px ink
  underline under the current one, name and Sign out right. The synthetic banner sits under it, in one sticky `.top`.
- Phone (≤720px): the same `#nav` becomes a fixed five-cell tab bar (icon over label); the header fits 390px with the
  name truncated; the banner wraps rather than truncating; `main` keeps clear of the bar.
- Two content widths: `.reading` (sign-in, welcome, privacy) and `.working` (designer, tracker, runs, profile).
- Tables keep a sticky head under the bar (`--top-h` follows the bar's real height) and stack into labelled rows at
  390px, with ARIA roles set at build time so they stay tables to assistive tech.
- One visually hidden live region (`#announce`) carries every announcement: toasts, inline "Saved" notes and the
  forge's step changes. The toast itself is not a live region, so it is never read twice. "Skip to content" moves
  focus without changing the hash (a hash change would re-route to the designer).

## The views

| View | What changed, and why |
|---|---|
| Sign-in | Headline, one lede, then the action (Google's button to Google's spec, or the persona buttons on a synthetic server), then the three trust facts, "How this works" folded away, a privacy-notice link. The notice is readable before signing in. |
| Welcome | The trust facts in full, three consent rows with a hairline between, a disabled "Continue to your profile" that reads as "not yet" rather than a faded blue block. |
| Designer | Eight stages as rows on a numbered rail, each built-in stage with its meaning, a live one-line summary of its settings and an "Edit" disclosure. Stage 4 is the only card group: switch, move and remove as icon buttons (Alt+↑/↓ reorders from the keyboard, and focus stays on the moved step), an add row, seams between cards on desktop. The palette (search, detail in place, "Or describe a new step") sits beside the pipeline at ≥960px and under stage 4 on phones. A fixed save bar with Discard and Undo. The forge shows a four-step stepper. The wiring is one vertical chain on the pipeline's axis at every width. Unsaved step changes survive leaving the page. |
| Tracker | Filter chips by status group; job, fit (dot + word + reason), status (grouped options; the evidence prompt appears under the select) and a details row with the user's steps' verdicts, gaps, unknowns, notes and next step. |
| Runs | One "Run a search now", which reads "Running…" and ignores clicks while a search is in progress; dot + word statuses; 24-hour times without seconds; a details row with boards, listing changes, what could not complete and the search log; polling that redraws only when something changed and keeps open rows and focus. |
| Profile | Fields first with "Confirm all" and per-row Confirm/Reject, the user-only fields as real controls with one Save, the résumé section last for a returning user and alone for a new one. |
| Privacy | Notice version, the trust facts, "Row by row", what you agreed to, the optional consent, export, and a danger zone drawn as a left rule, with a visible label on the DELETE field. |

## The test contract

The browser tests (`qa/ui/studio.test.cjs`) and the scripted QA agents (`qa/agents/scripts/*.json`) select on
`data-test` attributes, so every one of them stays on an element of the same type. Beyond the hooks, the tests rely
on: one `h1` per view, with "Tracker", "Runs", "Profile" and "Privacy" in theirs; `#toast` carrying the message text
("saved" after a save, "what happened" when evidence is missing, the server's DELETE refusal); the words "What runs
where" on the privacy page; `[data-test="your-steps"]` holding only `.step` children (the add row and the palette
slot sit outside it); `button[title="Move down"]` inside each step; `show-wiring` as a plain button, never inside a
closed disclosure; `run-now` unique and in the Runs header; `signed-in-as` visible at every width; a real
`table/thead/tbody/tr` for the tracker with the first `select` being a row's status. Two redesign guards: no `.badge`
elements, and every page fits 390px with the tab bar at the bottom.

## Known limits

- The seams between step cards appear on hover only, so they exist for pointer users; the add row and the arrow
  buttons do the same job for everyone else.
- Drafts of unsaved step changes live in `sessionStorage` per account; a browser with storage off loses them on
  navigation, as before.
- `/api/meta` carries no operator name or contact yet, so the privacy page says "Operator: not set" on a synthetic
  server and nothing on a real one; the operator block is listed as an optional server addition.
