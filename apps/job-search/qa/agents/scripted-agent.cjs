// A scripted QA agent: the same screen, recording and report as a Claude agent, but its steps come from a script, so
// it needs no API key and spends no tokens. Pull requests run every charter this way; the nightly fleet runs Claude.
"use strict";

/**
 * Script steps:
 *   { "click": "<css>" } | { "fill": "<css>", "text": "..." } | { "check": "<css>" } | { "select": "<css>", "value": "..." }
 *   { "goto": "#/route" } | { "reload": true } | { "waitFor": "<css>", "timeout": ms } | { "wait": ms }
 *   { "expectText": "<css>", "matches": "regex", "severity": "major", "title": "..." }
 *   { "expectVisible": "<css>", ... } | { "expectCount": "<css>", "atLeast": n, ... } | { "expectNone": "<css>", ... }
 * A failed expectation becomes a finding with the step's severity (default major); a failed action is a blocker.
 */
async function runScriptedAgent({ screen, script, base, log = () => {} }) {
  const page = screen.page;
  const findings = [];
  let step = 0;
  for (const s of script.steps) {
    step++;
    try {
      if (s.goto) await page.goto(base + "/" + s.goto.replace(/^\//, ""));
      else if (s.reload) await page.reload();
      else if (s.click) await page.click(s.click, { timeout: s.timeout ?? 15000 });
      else if (s.fill) await page.fill(s.fill, s.text ?? "", { timeout: s.timeout ?? 15000 });
      else if (s.check) await page.check(s.check, { timeout: s.timeout ?? 15000 });
      else if (s.select) await page.selectOption(s.select, s.value);
      else if (s.waitFor) await page.waitForSelector(s.waitFor, { timeout: s.timeout ?? 30000 });
      else if (s.wait) await page.waitForTimeout(s.wait);
      else if (s.expectText) {
        await page.waitForFunction(([sel, re]) => new RegExp(re, "i").test(document.querySelector(sel)?.textContent || ""), [s.expectText, s.matches], { timeout: s.timeout ?? 15000 })
          .catch(async () => {
            const actual = (await page.textContent(s.expectText).catch(() => null)) ?? "(element not found)";
            throw Object.assign(new Error("expectation"), { finding: { expected: `text matching /${s.matches}/`, actual: actual.slice(0, 300) } });
          });
      } else if (s.expectVisible) {
        await page.waitForSelector(s.expectVisible, { state: "visible", timeout: s.timeout ?? 15000 })
          .catch(() => { throw Object.assign(new Error("expectation"), { finding: { expected: `${s.expectVisible} visible`, actual: "not visible" } }); });
      } else if (s.expectCount) {
        const n = await page.locator(s.expectCount).count();
        if (n < s.atLeast) throw Object.assign(new Error("expectation"), { finding: { expected: `at least ${s.atLeast} of ${s.expectCount}`, actual: String(n) } });
      } else if (s.expectNone) {
        const n = await page.locator(s.expectNone).count();
        if (n !== 0) throw Object.assign(new Error("expectation"), { finding: { expected: `no ${s.expectNone}`, actual: `${n} found` } });
      } else throw new Error(`unknown step ${JSON.stringify(s)}`);
      await screen.run("screenshot");
    } catch (e) {
      await screen.run("screenshot").catch(() => {});
      const finding = e.finding
        ? { severity: s.severity || "major", title: s.title || `Expectation failed at step ${step}`, steps: describe(script.steps.slice(0, step)), ...e.finding, step }
        : { severity: "blocker", title: `Step ${step} could not be done: ${Object.keys(s)[0]} ${Object.values(s)[0]}`, steps: describe(script.steps.slice(0, step)), expected: "the step succeeds", actual: e.message.split("\n")[0], step };
      findings.push(finding);
      log(`finding [${finding.severity}] ${finding.title}`);
      if (!e.finding) return { outcome: "failed", summary: finding.title, findings, steps: step, usage: { input: 0, output: 0 }, notes: [] };
    }
  }
  return { outcome: findings.length ? "failed" : "passed", summary: `${script.steps.length} scripted steps`, findings, steps: step, usage: { input: 0, output: 0 }, notes: [] };
}

function describe(steps) {
  return steps.map((s, i) => `${i + 1}. ${Object.entries(s).filter(([k]) => !["severity", "title", "timeout"].includes(k)).map(([k, v]) => `${k} ${v}`).join(" ")}`).join("\n");
}

module.exports = { runScriptedAgent };
