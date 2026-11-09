#!/usr/bin/env node
// Screenshots of every studio view against a SYNTHETIC studio, for design review and release notes.
//
//   STUDIO_URL=http://127.0.0.1:8080 node screenshots.cjs [--out DIR] [--scale 2]
//
// Writes <out>/<view>-<theme>-<width>.png for the sign-in, welcome, designer (with a step added and the wiring shown),
// tracker (after one search), runs, profile and privacy views, in light and dark colour schemes, at desktop and
// phone widths. Refuses a studio that is not synthetic, like every other QA entry point.
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { STUDIO_URL, requireSynthetic, launch, lockedContext, signIn, PERSONAS } = require("./lib/studio.cjs");

const args = process.argv.slice(2);
const get = (k, d) => { const i = args.indexOf(`--${k}`); return i >= 0 && args[i + 1] ? args[i + 1] : d; };
const out = get("out", path.join(__dirname, "out", "screens"));
const scale = Number(get("scale", 2));
const SIZES = [{ name: "desktop", width: 1440, height: 900 }, { name: "phone", width: 390, height: 844 }];
const THEMES = ["light", "dark"];

async function main() {
  await requireSynthetic();
  fs.mkdirSync(out, { recursive: true });
  const browser = await launch();
  const written = [];
  try {
    // Jordan needs jobs in the tracker: run one search first (idempotent per day).
    {
      const context = await lockedContext(browser);
      const page = await context.newPage();
      await signIn(page, PERSONAS.jordan);
      await page.request.post(`${STUDIO_URL}/api/runs`, { headers: { "X-Studio-Request": "1" } });
      for (let i = 0; i < 60; i++) {
        const runs = await (await page.request.get(`${STUDIO_URL}/api/runs`)).json();
        if (runs.length && runs[0].status !== "running") break;
        await page.waitForTimeout(1000);
      }
      await context.close();
    }
    for (const theme of THEMES) {
      for (const size of SIZES) {
        const context = await browser.newContext({ viewport: { width: size.width, height: size.height }, deviceScaleFactor: scale, colorScheme: theme });
        const page = await context.newPage();
        const shot = async (name, opts = {}) => {
          const file = path.join(out, `${name}-${theme}-${size.name}.png`);
          await page.waitForTimeout(300);
          await page.screenshot({ path: file, fullPage: opts.fullPage ?? true });
          written.push(file);
        };
        await page.goto(STUDIO_URL + "/");
        await page.waitForSelector('[data-test^="persona-"]');
        await shot("sign-in");

        await signIn(page, PERSONAS.casey);
        await page.waitForSelector('[data-test="consent-continue"]');
        await shot("welcome");
        await page.goto(STUDIO_URL + "/#/privacy");
        await page.waitForSelector('[data-test="back-to-welcome"]');
        await shot("notice-before-consent");
        await page.request.post(`${STUDIO_URL}/auth/signout`, { headers: { "X-Studio-Request": "1" } });

        await signIn(page, PERSONAS.jordan);
        await page.waitForSelector('[data-test="pipeline"]');
        await page.waitForSelector('[data-test^="library-item-"]', { timeout: 60000 });
        await shot("designer");
        await page.click('[data-test^="library-item-step.keep-postings"]');
        await page.waitForSelector('[data-test="add-step"]');
        await page.click('[data-test="add-step"]');
        await page.waitForSelector('[data-test="save-workflow"]');
        await shot("designer-step-added");
        await page.click('[data-test="show-wiring"]');
        await page.waitForSelector('[data-test="wiring"]');
        await shot("designer-wiring");

        await page.goto(STUDIO_URL + "/#/tracker");
        await page.waitForSelector('[data-test="tracker"], .empty');
        await shot("tracker");
        await page.goto(STUDIO_URL + "/#/runs");
        await page.waitForSelector('[data-test="run-now"]');
        await shot("runs");
        await page.goto(STUDIO_URL + "/#/profile");
        await page.waitForSelector('[data-test="resume-text"]');
        await shot("profile");
        await page.goto(STUDIO_URL + "/#/privacy");
        await page.waitForSelector('[data-test="export"]');
        await shot("privacy");
        await context.close();
      }
    }
  } finally {
    await browser.close();
  }
  console.log(`${written.length} screenshots in ${out}`);
}

main().catch((e) => { console.error(e); process.exit(1); });
