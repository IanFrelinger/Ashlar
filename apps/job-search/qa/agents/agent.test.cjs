// Self-test of the QA agent harness, with no studio and no tokens: every computer member on a local page, the batch
// halting rule, and the Claude agent loop against a fake client that answers like the Messages API.
"use strict";
const test = require("node:test");
const assert = require("node:assert/strict");
const { Screen, runComputerCalls, combo, NOT_EXECUTED } = require("./computer.cjs");
const { runClaudeAgent, TOOLS } = require("./claude-agent.cjs");

const PAGE = `<!doctype html><html><body style="margin:0">
  <button id="b" style="position:absolute;left:100px;top:100px;width:120px;height:40px" onclick="this.textContent='clicked'">click me</button>
  <input id="i" style="position:absolute;left:100px;top:200px;width:200px;height:30px">
  <div id="tall" style="height:3000px"></div></body></html>`;

let browser, page, screen;
test.before(async () => {
  const { chromium } = require("playwright");
  browser = await chromium.launch();
  page = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  await page.setContent(PAGE);
  screen = new Screen(page);
});
test.after(async () => { await browser?.close(); });

test("key names from the model map to Playwright's", () => {
  assert.equal(combo("Return"), "Enter");
  assert.equal(combo("ctrl+a"), "Control+a");
  assert.equal(combo("alt+Tab"), "Alt+Tab");
  assert.equal(combo("BackSpace"), "Backspace");
  assert.equal(combo("Page_Down"), "PageDown");
  assert.equal(combo("super+shift+t"), "Meta+Shift+t");
});

test("each computer member acts on the agent's own screen", async () => {
  const shot = await screen.run("screenshot");
  assert.equal(shot[0].type, "image");
  assert.equal(shot[0].source.media_type, "image/png");
  await screen.run("left_click", { coordinate: [160, 120] });
  assert.equal(await page.textContent("#b"), "clicked");
  await screen.run("left_click", { coordinate: [150, 215] });
  await screen.run("type", { text: "hello" });
  assert.equal(await page.inputValue("#i"), "hello");
  await screen.run("key", { text: "ctrl+a" });
  await screen.run("key", { text: "BackSpace" });
  assert.equal(await page.inputValue("#i"), "");
  assert.deepEqual(await screen.run("cursor_position"), [{ type: "text", text: "X=150, Y=215" }]);
  await screen.run("scroll", { coordinate: [640, 400], scroll_direction: "down", scroll_amount: 5 });
  assert.ok(await page.evaluate(() => window.scrollY) > 0);
  const zoom = await screen.run("zoom", { region: [100, 100, 400, 300] });
  assert.equal(zoom[0].type, "image");
  await assert.rejects(screen.run("left_click", { coordinate: [5000, 10] }), /outside/);
  await assert.rejects(screen.run("teleport", {}), /not supported/);
});

test("a failed call halts the rest of the batch, and every result echoes toolset_name", async () => {
  const results = await runComputerCalls(screen, [
    { id: "a", name: "screenshot", input: {} },
    { id: "b", name: "left_click", input: { coordinate: [-1, 5] } },
    { id: "c", name: "type", input: { text: "never typed" } },
  ]);
  assert.deepEqual(results.map((r) => [r.tool_use_id, r.toolset_name, !!r.is_error]), [["a", "computer", false], ["b", "computer", true], ["c", "computer", true]]);
  assert.equal(results[2].content, NOT_EXECUTED);
});

test("the Claude loop runs computer calls, records findings, answers every call, and stops on finish", async () => {
  const requests = [];
  const turns = [
    { stop_reason: "tool_use", usage: { input_tokens: 100, output_tokens: 10 }, content: [
      { type: "thinking", thinking: "Clicking the button to see what happens." },
      { type: "tool_use", id: "t1", name: "left_click", toolset_name: "computer", input: { coordinate: [160, 120] } },
      { type: "tool_use", id: "t2", name: "screenshot", toolset_name: "computer", input: {} },
    ] },
    { stop_reason: "tool_use", usage: { input_tokens: 200, output_tokens: 20 }, content: [
      { type: "tool_use", id: "t3", name: "report_finding", input: { severity: "minor", title: "Button label changes", steps: "click", expected: "x", actual: "y" } },
      { type: "tool_use", id: "t4", name: "finish", input: { outcome: "passed", summary: "done" } },
    ] },
  ];
  const client = { beta: { messages: { create: async (params) => { requests.push(structuredClone(params)); return turns.shift(); } } } };
  const result = await runClaudeAgent({ client, screen, charter: { persona: "Test", goal: "Try it." }, meta: { build: "test" }, maxSteps: 5 });

  assert.equal(result.outcome, "passed");
  assert.equal(result.findings.length, 1);
  assert.equal(result.findings[0].severity, "minor");
  assert.deepEqual(result.usage, { input: 300, output: 30 });
  assert.ok(result.notes.includes("Clicking the button to see what happens."));
  // The request carries the toolset, the fallback and the clearing of old screenshots.
  assert.equal(requests[0].model, "claude-opus-5-5");
  assert.deepEqual(requests[0].tools[0], { type: "computer_toolset_20260801" });
  assert.equal(requests[0].fallbacks, "default");
  assert.ok(requests[0].betas.includes("server-side-fallback-2026-07-01"));
  // The second request answers both computer calls, in order, echoing toolset_name, after the unchanged assistant turn.
  const answer = requests[1].messages.at(-1);
  assert.equal(requests[1].messages.at(-2).role, "assistant");
  assert.equal(requests[1].messages.at(-2).content[0].type, "thinking");
  assert.deepEqual(answer.content.map((r) => [r.tool_use_id, r.toolset_name]), [["t1", "computer"], ["t2", "computer"]]);
  assert.equal(answer.content[1].content[0].type, "image");
  assert.ok(TOOLS.some((t) => t.name === "report_finding" && t.strict === true));
});

test("a run that never finishes stops at its step limit and says so", async () => {
  const client = { beta: { messages: { create: async () => ({ stop_reason: "tool_use", usage: {}, content: [{ type: "tool_use", id: "x" + Math.random(), name: "screenshot", toolset_name: "computer", input: {} }] }) } } };
  const result = await runClaudeAgent({ client, screen, charter: { persona: "T", goal: "g" }, meta: { build: "t" }, maxSteps: 3 });
  assert.equal(result.outcome, "blocked");
  assert.match(result.summary, /3 steps/);
});
