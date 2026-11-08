// Deterministic UI suite: the studio's main journeys in a real browser, against a synthetic studio.
// Each test gets its own browser context (its own screen); tests run in order because later ones build on
// earlier ones (Jordan's pipeline), and the account deletion runs last.
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const path = require("node:path");
const { STUDIO_URL, requireSynthetic, launch, lockedContext, signIn, PERSONAS } = require("../lib/studio.cjs");

const OUT = process.env.QA_OUT || path.join(__dirname, "..", "out", "ui");
fs.mkdirSync(OUT, { recursive: true });
let browser;

test.before(async () => {
  await requireSynthetic();
  browser = await launch();
});
test.after(async () => { await browser?.close(); });

/** A fresh screen for one test; screenshots on failure go to qa/out/ui. */
async function screen(t, fn) {
  const context = await lockedContext(browser);
  const page = await context.newPage();
  const errors = [];
  page.on("pageerror", (e) => errors.push(e.message));
  try {
    await fn(page, context);
    assert.deepEqual(errors, [], "the page threw script errors");
    assert.deepEqual(context.blocked, [], "the page tried to reach another origin");
  } catch (e) {
    await page.screenshot({ path: path.join(OUT, t.name.replace(/[^a-z0-9]+/gi, "-") + ".png"), fullPage: true }).catch(() => {});
    throw e;
  } finally {
    await context.close();
  }
}

/** Waits for a toast whose text matches, and returns it (toasts replace each other, so wait for the right one). */
async function toast(page, pattern) {
  await page.waitForFunction((source) => {
    const t = document.querySelector("#toast");
    return t && !t.hidden && new RegExp(source, "i").test(t.textContent);
  }, pattern.source, { timeout: 15000 }).catch(() => {});
  return page.textContent("#toast");
}

test("the sign-in page says it is synthetic, offers only made-up personas, and talks to no third party", (t) => screen(t, async (page, context) => {
  const requests = [];
  page.on("request", (r) => requests.push(r.url()));
  await page.goto(STUDIO_URL + "/");
  await page.waitForSelector('[data-test^="persona-"]');
  assert.equal(await page.isVisible("#synthetic-banner"), true);
  assert.equal(await page.isVisible("#nav"), false, "no navigation before sign-in");
  const origin = new URL(STUDIO_URL).origin;
  assert.deepEqual(requests.filter((u) => !u.startsWith(origin) && !u.startsWith("data:")), []);
}));

test("a new user reads the notice and consents before the studio does anything with their data", (t) => screen(t, async (page) => {
  await signIn(page, PERSONAS.casey);
  await page.waitForSelector('[data-test="consent-continue"]');
  assert.equal(await page.isDisabled('[data-test="consent-continue"]'), true);
  // The notice is readable before agreeing to it.
  await page.click('[data-test="read-notice"]');
  await page.waitForSelector("text=What runs where");
  await page.click('[data-test="back-to-welcome"]');
  for (const box of ["consent-privacy-notice", "consent-terms"]) await page.check(`[data-test="${box}"]`);
  assert.equal(await page.isDisabled('[data-test="consent-continue"]'), true, "all three are required");
  await page.check('[data-test="consent-age"]');
  await page.click('[data-test="consent-continue"]');
  await page.waitForSelector('[data-test="resume-text"]');
  await page.fill('[data-test="resume-text"]', "Casey New\ncasey.new@example.com | Chicago, IL\n\nEXPERIENCE\nData Analyst — Example Retail | Chicago, IL | 2021-01 – Present\n- Built weekly sales dashboards in SQL\n\nSKILLS\nSQL, Excel");
  await page.click('[data-test="resume-save"]');
  await page.waitForSelector('[data-test="profile-fields"]');
  assert.match(await page.textContent('[data-test="profile-fields"]'), /Casey New/);
}));

test("Jordan adds a library step, sets it, saves, and it is still there after a reload", (t) => screen(t, async (page) => {
  await signIn(page, PERSONAS.jordan);
  await page.waitForSelector('[data-test="pipeline"]');
  await page.fill('[data-test="library-search"]', "places remote");
  const item = page.locator('[data-test^="library-item-step.keep-postings"]').first();
  await item.waitFor({ timeout: 120000 }); // the first start certifies the starter steps
  await item.click();
  await page.click('[data-test="add-step"]');
  const key = await page.locator('[data-test="your-steps"] .step').last().getAttribute("data-key");
  await page.fill(`[data-test="step-setting-${key}-0"]`, "Denver, Boulder");
  await page.click('[data-test="save-workflow"]');
  assert.match(await toast(page, /saved/), /saved/i);
  await page.reload();
  await page.waitForSelector(`[data-test="step-${key}"]`);
  assert.equal(await page.inputValue(`[data-test="step-setting-${key}-0"]`), "Denver, Boulder");
}));

test("Jordan describes a new step; the studio certifies it and adds it to the pipeline", (t) => screen(t, async (page) => {
  await signIn(page, PERSONAS.jordan);
  await page.waitForSelector('[data-test="pipeline"]');
  const before = await page.locator('[data-test="your-steps"] .step').count();
  await page.fill('[data-test="forge-request"]', 'Flag any title that contains "intern"');
  await page.click('[data-test="forge-submit"]');
  await page.waitForFunction(() => ["done", "failed", "needs-input"].includes(document.querySelector('[data-test="forge-status"]')?.textContent), null, { timeout: 300000 });
  assert.equal(await page.textContent('[data-test="forge-status"]'), "done", await page.textContent('[data-test="forge-progress"]'));
  assert.equal(await page.locator('[data-test="your-steps"] .step').count(), before + 1);
  assert.match(await page.textContent('[data-test="forge-progress"]'), /no personal data/);
}));

test("Jordan reorders a step, switches one off, and the pipeline keeps both after saving", (t) => screen(t, async (page) => {
  await signIn(page, PERSONAS.jordan);
  await page.waitForSelector('[data-test="your-steps"] .step');
  const keys = await page.locator('[data-test="your-steps"] .step').evaluateAll((els) => els.map((e) => e.dataset.key));
  assert.ok(keys.length >= 2, "two steps from the earlier tests");
  await page.locator(`[data-test="step-${keys[0]}"] button[title="Move down"]`).click();
  await page.uncheck(`[data-test="step-toggle-${keys[1]}"]`);
  await page.click('[data-test="save-workflow"]');
  await toast(page, /saved/);
  await page.reload();
  await page.waitForSelector('[data-test="your-steps"] .step');
  const after = await page.locator('[data-test="your-steps"] .step').evaluateAll((els) => els.map((e) => e.dataset.key));
  assert.deepEqual(after.slice(0, 2), [keys[1], keys[0]]);
  assert.equal(await page.isChecked(`[data-test="step-toggle-${keys[1]}"]`), false);
}));

test("the wiring view shows the Ashlar workflow the pipeline compiles to, with the user's steps in it", (t) => screen(t, async (page) => {
  await signIn(page, PERSONAS.jordan);
  await page.click('[data-test="show-wiring"]');
  await page.waitForSelector('[data-test="wiring"]');
  const text = await page.textContent('[data-test="wiring"]');
  assert.match(text, /jobsearch\.recommendation/);
  assert.match(text, /step\./);
}));

test("a search fills the tracker, and marking a job applied asks what happened", (t) => screen(t, async (page) => {
  await signIn(page, PERSONAS.jordan);
  await page.click('[data-test="nav-runs"]');
  await page.click('[data-test="run-now"]');
  await page.click('[data-test="nav-tracker"]');
  for (let i = 0; i < 40 && !(await page.isVisible('[data-test="tracker"]')); i++) {
    await page.waitForTimeout(1500);
    await page.click('[data-test="nav-runs"]');
    await page.click('[data-test="nav-tracker"]');
  }
  const select = page.locator('[data-test="tracker"] select').first();
  const id = (await select.getAttribute("data-test")).slice("status-".length);
  await select.selectOption("applied");
  await page.click(`[data-test="evidence-save-${id}"]`);
  assert.match(await toast(page, /what happened/), /what happened/i);
  await page.fill(`[data-test="evidence-${id}"]`, "Applied on the employer's site today");
  await page.click(`[data-test="evidence-save-${id}"]`);
  assert.match(await toast(page, /saved/), /saved/i);
  await page.reload();
  await page.waitForSelector('[data-test="tracker"]');
  assert.equal(await page.inputValue(`[data-test="status-${id}"]`), "applied");
}));

test("every form control on the main pages has an accessible name", (t) => screen(t, async (page) => {
  await signIn(page, PERSONAS.jordan);
  for (const nav of ["nav-designer", "nav-tracker", "nav-runs", "nav-profile", "nav-privacy"]) {
    await page.click(`[data-test="${nav}"]`);
    await page.waitForTimeout(400);
    const unnamed = await page.$$eval("input:not([type=hidden]), select, textarea", (els) =>
      els.filter((e) => !(e.labels?.length || e.getAttribute("aria-label") || e.getAttribute("aria-labelledby"))).map((e) => e.outerHTML.slice(0, 120)));
    assert.deepEqual(unnamed, [], `unnamed controls on ${nav}`);
    assert.equal(await page.locator("h1").count(), 1, `${nav} has one h1`);
  }
}));

test("Sam downloads everything, then deletes the account, and is gone", (t) => screen(t, async (page) => {
  await signIn(page, PERSONAS.sam);
  await page.click('[data-test="nav-privacy"]');
  const [download] = await Promise.all([page.waitForEvent("download"), page.click('[data-test="export"]')]);
  const data = JSON.parse(fs.readFileSync(await download.path(), "utf8"));
  assert.equal(data.format, "jobsearch-studio-export/1");
  assert.ok(data.resume_versions.length > 0);
  await page.fill('[data-test="delete-confirm"]', "nope");
  await page.click('[data-test="delete-account"]');
  assert.match(await toast(page, /DELETE/), /DELETE/);
  // Slow the studio's metadata down, so a sign-in page drawn from stale state (still offering Sam) is always seen
  // rather than caught only when the timing happens to line up.
  await page.route("**/api/meta", async (route) => { await new Promise((r) => setTimeout(r, 400)); await route.continue(); });
  await page.fill('[data-test="delete-confirm"]', "DELETE");
  await page.click('[data-test="delete-account"]');
  await page.waitForSelector('[data-test^="persona-"]');
  assert.equal(await page.locator(`[data-test="persona-${PERSONAS.sam}"]`).count(), 0, "Sam's account no longer exists");
}));
