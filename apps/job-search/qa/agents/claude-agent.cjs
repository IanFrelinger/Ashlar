// A QA agent driven by Claude's computer toolset. It sees only its own screen of a synthetic studio and reports what
// it finds through a report_finding tool. Uses the official TypeScript/JavaScript SDK (@anthropic-ai/sdk).
"use strict";
const { runComputerCalls } = require("./computer.cjs");

const MODEL = process.env.QA_MODEL || "claude-opus-5-5";
const EFFORT = process.env.QA_EFFORT || "high";
const SEVERITIES = ["blocker", "major", "minor", "note"];

const TOOLS = [
  { type: "computer_toolset_20260801" },
  {
    name: "report_finding",
    description: "Record one problem you found: a bug, an error message, broken or confusing behaviour, an accessibility problem, or text that is wrong. Call it once per distinct problem, as soon as you are sure.",
    strict: true,
    input_schema: {
      type: "object", additionalProperties: false,
      required: ["severity", "title", "steps", "expected", "actual"],
      properties: {
        severity: { type: "string", enum: SEVERITIES, description: "blocker: a user cannot complete the charter's goal or data is lost or exposed. major: the goal is reachable only with a workaround, or something is clearly wrong. minor: cosmetic or confusing. note: a suggestion." },
        title: { type: "string", description: "One line." },
        steps: { type: "string", description: "What you did, in order." },
        expected: { type: "string" },
        actual: { type: "string" },
      },
    },
  },
  {
    name: "finish",
    description: "Call once, when you have done what the charter asks or cannot continue.",
    strict: true,
    input_schema: {
      type: "object", additionalProperties: false, required: ["outcome", "summary"],
      properties: {
        outcome: { type: "string", enum: ["passed", "failed", "blocked"], description: "passed: the charter's goal works. failed: it does not. blocked: you could not test it." },
        summary: { type: "string" },
      },
    },
  },
];

function systemPrompt(charter, meta) {
  return [
    "You are a QA tester for Job Search Studio, a web app where job seekers design their own job-search pipeline.",
    "You have your own screen: a 1280x800 browser window showing the app. Use the computer tools to look and act like a person would.",
    `The app is a SYNTHETIC test copy (build ${meta.build}): every person and posting in it is made up. Never type real personal data; use only the persona's made-up details or obviously fake ones.`,
    "Stay inside the app. Links to other sites are blocked on purpose; that is not a bug.",
    `You are signed in as ${charter.persona}.`,
    "Your charter:",
    charter.goal,
    charter.checks?.length ? "Check in particular:\n- " + charter.checks.join("\n- ") : "",
    "Report every problem with report_finding as soon as you are sure of it (one call per problem, with steps to reproduce). Then keep going if you can.",
    "When you are done, or cannot continue, call finish exactly once.",
  ].filter(Boolean).join("\n");
}

/**
 * Runs one charter. `client` is an Anthropic client (or a test double with beta.messages.create); `screen` is the
 * agent's Screen. Returns { outcome, summary, findings, steps, usage, notes }.
 */
async function runClaudeAgent({ client, screen, charter, meta, maxSteps = 40, log = () => {} }) {
  const findings = [];
  const notes = [];
  const usage = { input: 0, output: 0, cacheRead: 0, cacheWrite: 0 };
  let outcome = null;
  const first = await screen.run("screenshot");
  const messages = [{ role: "user", content: [{ type: "text", text: "Here is your screen. Begin." }, ...first] }];
  for (let step = 1; step <= maxSteps; step++) {
    const response = await client.beta.messages.create({
      model: MODEL,
      max_tokens: 16000,
      // A refusal is re-served by Anthropic's recommended fallback; progress notes come back as short thinking
      // summaries for the report; old screenshots are cleared server-side (never pruned here, which would
      // invalidate later thinking blocks). Every turn re-reads the conversation so far, so it is cached: an explicit
      // breakpoint on the system prompt, and automatic caching of the growing tail (cache reads cost 0.05x input).
      betas: ["server-side-fallback-2026-07-01", "thinking-display-updates-2026-08-18", "context-management-2025-06-27"],
      cache_control: { type: "ephemeral" },
      fallbacks: "default",
      thinking: { type: "adaptive", display: "updates" },
      output_config: { effort: EFFORT },
      context_management: { edits: [{ type: "clear_tool_uses_20250919" }] },
      system: [{ type: "text", text: systemPrompt(charter, meta), cache_control: { type: "ephemeral" } }],
      tools: TOOLS,
      messages,
    });
    usage.input += response.usage?.input_tokens ?? 0;
    usage.output += response.usage?.output_tokens ?? 0;
    usage.cacheRead += response.usage?.cache_read_input_tokens ?? 0;
    usage.cacheWrite += response.usage?.cache_creation_input_tokens ?? 0;
    if (response.stop_reason === "refusal") {
      notes.push("The model declined to continue.");
      outcome = outcome || { outcome: "blocked", summary: "model declined" };
      break;
    }
    // Pass the content back unchanged, thinking blocks included.
    messages.push({ role: "assistant", content: response.content });
    for (const block of response.content)
      if (block.type === "thinking" && block.thinking) notes.push(block.thinking);
      else if (block.type === "text" && block.text) notes.push(block.text);

    const calls = response.content.filter((b) => b.type === "tool_use");
    if (calls.length === 0) break;
    const computer = calls.filter((b) => b.toolset_name === "computer");
    const results = await runComputerCalls(screen, computer);
    for (const call of calls.filter((b) => b.toolset_name !== "computer")) {
      if (call.name === "report_finding") {
        const f = call.input || {};
        findings.push({ severity: SEVERITIES.includes(f.severity) ? f.severity : "major", title: f.title, steps: f.steps, expected: f.expected, actual: f.actual, step });
        log(`finding [${f.severity}] ${f.title}`);
        results.push({ type: "tool_result", tool_use_id: call.id, content: "Recorded." });
      } else if (call.name === "finish") {
        outcome = { outcome: call.input?.outcome || "failed", summary: call.input?.summary || "" };
        results.push({ type: "tool_result", tool_use_id: call.id, content: "OK" });
      } else {
        results.push({ type: "tool_result", tool_use_id: call.id, is_error: true, content: `Unknown tool ${call.name}` });
      }
    }
    // Results go back in the order the calls were made.
    const order = new Map(calls.map((c, i) => [c.id, i]));
    results.sort((a, b) => order.get(a.tool_use_id) - order.get(b.tool_use_id));
    messages.push({ role: "user", content: results });
    if (outcome) {
      // Let the final turn's results reach the transcript, then stop.
      return { ...outcome, findings, steps: step, usage, notes };
    }
  }
  return { ...(outcome || { outcome: "blocked", summary: `stopped after ${maxSteps} steps without finishing` }), findings, steps: maxSteps, usage, notes };
}

module.exports = { runClaudeAgent, TOOLS, SEVERITIES, systemPrompt };
