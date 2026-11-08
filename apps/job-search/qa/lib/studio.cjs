// Shared helpers for the deterministic UI suite and the QA agents.
// Everything here refuses to touch a studio that does not say it is synthetic: QA screens are sent to an external
// model, so they must never show a real person's data (apps/job-search/docs/STUDIO.md §5).
"use strict";

const STUDIO_URL = (process.env.STUDIO_URL || "http://127.0.0.1:5080").replace(/\/$/, "");

class NotSyntheticError extends Error {}

/** Reads /api/meta and refuses unless the studio reports synthetic: true. */
async function requireSynthetic(base = STUDIO_URL, fetchImpl = fetch) {
  let meta;
  try {
    const res = await fetchImpl(`${base}/api/meta`);
    if (!res.ok) throw new Error(`HTTP ${res.status}`);
    meta = await res.json();
  } catch (e) {
    throw new NotSyntheticError(`cannot read ${base}/api/meta (${e.message}); refusing to run QA against an unknown target`);
  }
  if (meta.synthetic !== true)
    throw new NotSyntheticError(`${base} is not a synthetic studio (synthetic=${meta.synthetic}); QA never runs against real data`);
  return meta;
}

/** A browser for one tester: its own Chromium process, so its own screen. */
async function launch({ headed = !!process.env.QA_HEADED } = {}) {
  const { chromium } = require("playwright");
  return chromium.launch({ headless: !headed });
}

/** A context locked to the studio's origin: any request elsewhere is aborted, so a tester can't wander off. */
async function lockedContext(browser, { base = STUDIO_URL, video, width = 1280, height = 800 } = {}) {
  const origin = new URL(base).origin;
  const context = await browser.newContext({
    viewport: { width, height }, deviceScaleFactor: 1, acceptDownloads: true,
    recordVideo: video ? { dir: video, size: { width, height } } : undefined,
  });
  const blocked = [];
  await context.route("**/*", (route) => {
    const url = route.request().url();
    if (url.startsWith(origin) || url.startsWith("data:") || url.startsWith("blob:")) return route.continue();
    blocked.push(url);
    return route.abort("blockedbyclient");
  });
  context.on("page", (p) => { if (context.pages().length > 1) p.close().catch(() => {}); });
  context.blocked = blocked;
  return context;
}

/** Signs a page in as a synthetic persona through the sign-in screen. */
async function signIn(page, email, base = STUDIO_URL) {
  await page.goto(base + "/");
  await page.click(`[data-test="persona-${email}"]`);
  await page.waitForSelector('[data-test="signed-in-as"]');
}

const PERSONAS = {
  jordan: "jordan.rivera@example.com",
  sam: "sam.okafor@example.com",
  casey: "new.user@example.com",
};

module.exports = { STUDIO_URL, NotSyntheticError, requireSynthetic, launch, lockedContext, signIn, PERSONAS };
