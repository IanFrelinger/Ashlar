#!/usr/bin/env node
// The QA agent fleet: one agent per charter, each with its own browser (its own screen), run in parallel against a
// synthetic studio. Writes qa/out/agents/<run>/report.{json,html} with screenshots and a video per agent, and exits
// non-zero when a finding at a blocking severity is reported, which is what gates a release.
//
//   node agents/run.cjs [--mode claude|scripted] [--charters a,b] [--parallel 3] [--max-steps 40] [--out DIR]
//   env: STUDIO_URL, ANTHROPIC_API_KEY (claude mode), QA_MODEL, QA_EFFORT, QA_FAIL_ON (default blocker,major)
"use strict";
const fs = require("node:fs");
const path = require("node:path");
const { STUDIO_URL, requireSynthetic, NotSyntheticError, launch, lockedContext, signIn } = require("../lib/studio.cjs");
const { Screen } = require("./computer.cjs");
const { runScriptedAgent } = require("./scripted-agent.cjs");
const report = require("./report.cjs");

function args() {
  const a = process.argv.slice(2);
  const get = (k, d) => { const i = a.indexOf(`--${k}`); return i >= 0 && a[i + 1] ? a[i + 1] : d; };
  return {
    mode: get("mode", process.env.QA_MODE || "claude"),
    charters: get("charters", process.env.QA_CHARTERS || ""),
    parallel: Number(get("parallel", process.env.QA_PARALLEL || 3)),
    maxSteps: Number(get("max-steps", process.env.QA_MAX_STEPS || 40)),
    out: get("out", process.env.QA_OUT || path.join(__dirname, "..", "out", "agents", new Date().toISOString().replace(/[:.]/g, "-"))),
    failOn: (process.env.QA_FAIL_ON || "blocker,major").split(",").map((s) => s.trim()).filter(Boolean),
  };
}

function loadCharters(filter) {
  const dir = path.join(__dirname, "charters");
  const all = fs.readdirSync(dir).filter((f) => f.endsWith(".json")).sort().map((f) => ({ id: f.replace(/\.json$/, ""), ...JSON.parse(fs.readFileSync(path.join(dir, f), "utf8")) }));
  const want = filter ? new Set(filter.split(",").map((s) => s.trim())) : null;
  return want ? all.filter((c) => want.has(c.id)) : all;
}

async function runOne(browser, charter, opt, meta, makeClient) {
  const dir = path.join(opt.out, charter.id);
  fs.mkdirSync(dir, { recursive: true });
  const started = Date.now();
  const context = await lockedContext(browser, { video: dir });
  const page = await context.newPage();
  const screens = [];
  const screen = new Screen(page, { onShot: (png) => { const f = `screen-${String(screens.length + 1).padStart(3, "0")}.png`; fs.writeFileSync(path.join(dir, f), png); screens.push(`${charter.id}/${f}`); } });
  const log = (line) => console.log(`[${charter.id}] ${line}`);
  let result;
  try {
    await signIn(page, charter.email);
    if (charter.start) await page.goto(STUDIO_URL + "/" + charter.start);
    await page.waitForTimeout(500);
    if (opt.mode === "scripted") {
      const scriptPath = path.join(__dirname, "scripts", `${charter.id}.json`);
      if (!fs.existsSync(scriptPath)) throw new Error(`no script for ${charter.id}`);
      result = await runScriptedAgent({ screen, script: JSON.parse(fs.readFileSync(scriptPath, "utf8")), base: STUDIO_URL, log });
    } else {
      const { runClaudeAgent } = require("./claude-agent.cjs");
      result = await runClaudeAgent({ client: makeClient(), screen, charter, meta, maxSteps: charter.maxSteps || opt.maxSteps, log });
    }
  } catch (e) {
    result = { outcome: "blocked", summary: `the agent could not run: ${e.message.split("\n")[0]}`, findings: [], steps: 0, usage: { input: 0, output: 0 }, notes: [] };
  }
  const video = page.video();
  await context.close();
  const videoPath = video ? path.relative(opt.out, await video.path()) : null;
  log(`${result.outcome}: ${result.summary} (${result.findings.length} findings)`);
  if (context.blocked.length) result.notes.push(`Blocked ${context.blocked.length} request(s) to other origins.`);
  return { charter: charter.id, persona: charter.persona, mode: opt.mode, ...result, screens, video: videoPath, ms: Date.now() - started };
}

async function main() {
  const opt = args();
  let meta;
  try {
    meta = await requireSynthetic();
  } catch (e) {
    if (e instanceof NotSyntheticError) { console.error(`refusing: ${e.message}`); process.exit(2); }
    throw e;
  }
  let makeClient = null;
  if (opt.mode === "claude") {
    if (!process.env.ANTHROPIC_API_KEY) { console.error("claude mode needs ANTHROPIC_API_KEY; use --mode scripted to run without tokens"); process.exit(3); }
    const Anthropic = require("@anthropic-ai/sdk").default ?? require("@anthropic-ai/sdk");
    makeClient = () => new Anthropic({ maxRetries: 3 });
  }
  const charters = loadCharters(opt.charters);
  if (charters.length === 0) { console.error("no charters selected"); process.exit(4); }
  const startedAt = new Date().toISOString();
  const browser = await launch();
  const agents = [];
  try {
    // Charters that change shared state (an account deletion) run after the others.
    const ordered = [...charters.filter((c) => !c.destructive), ...charters.filter((c) => c.destructive)];
    for (let i = 0; i < ordered.length; i += opt.parallel) {
      const batch = ordered.slice(i, i + opt.parallel);
      agents.push(...await Promise.all(batch.map((c) => runOne(browser, c, opt, meta, makeClient))));
    }
  } finally {
    await browser.close();
  }
  const r = { target: STUDIO_URL, build: meta.build, mode: opt.mode, startedAt, finishedAt: new Date().toISOString(), failOn: opt.failOn, agents };
  r.blocking = report.blocking(r, opt.failOn);
  report.write(opt.out, r);
  console.log(`report: ${path.join(opt.out, "report.html")}`);
  console.log(`${agents.length} agents, ${agents.reduce((n, a) => n + a.findings.length, 0)} findings, ${r.blocking.length} blocking`);
  process.exit(r.blocking.length ? 1 : 0);
}

main().catch((e) => { console.error(e); process.exit(5); });
