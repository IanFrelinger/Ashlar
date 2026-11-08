// The QA report: report.json for machines (and the release gate), report.html for people. Self-contained: the HTML
// references only the screenshots and videos written next to it.
"use strict";
const fs = require("node:fs");
const path = require("node:path");

const RANK = { blocker: 0, major: 1, minor: 2, note: 3 };

function esc(s) {
  return String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

/** Findings at or above the gate's severities. */
function blocking(report, failOn) {
  const gate = new Set(failOn);
  return report.agents.flatMap((a) => a.findings.filter((f) => gate.has(f.severity)).map((f) => ({ ...f, charter: a.charter })));
}

function write(out, report) {
  fs.mkdirSync(out, { recursive: true });
  fs.writeFileSync(path.join(out, "report.json"), JSON.stringify(report, null, 2));
  const rows = report.agents.map((a) => `
    <section class="agent">
      <h2>${esc(a.charter)} <small>${esc(a.persona)} · ${esc(a.mode)}</small> <span class="o ${esc(a.outcome)}">${esc(a.outcome)}</span></h2>
      <p>${esc(a.summary)}</p>
      <p class="meta">${a.steps} steps · tokens: ${a.usage.input} in, ${a.usage.cacheRead ?? 0} read from cache, ${a.usage.cacheWrite ?? 0} written to cache, ${a.usage.output} out · ${(a.ms / 1000).toFixed(0)} s ${a.video ? `· <a href="${esc(a.video)}">video</a>` : ""}</p>
      ${a.findings.length === 0 ? "<p>No findings.</p>" : `<table><tr><th>Severity</th><th>Finding</th><th>Steps</th><th>Expected</th><th>Actual</th></tr>
        ${[...a.findings].sort((x, y) => RANK[x.severity] - RANK[y.severity]).map((f) => `<tr><td class="s ${esc(f.severity)}">${esc(f.severity)}</td><td>${esc(f.title)}</td><td><pre>${esc(f.steps)}</pre></td><td>${esc(f.expected)}</td><td>${esc(f.actual)}</td></tr>`).join("")}</table>`}
      ${a.screens.length ? `<details><summary>Screens (${a.screens.length})</summary><div class="shots">${a.screens.map((s) => `<a href="${esc(s)}"><img src="${esc(s)}" alt="screen" loading="lazy"></a>`).join("")}</div></details>` : ""}
      ${a.notes.length ? `<details><summary>Agent notes</summary><ol>${a.notes.map((n) => `<li>${esc(n)}</li>`).join("")}</ol></details>` : ""}
    </section>`).join("");
  const html = `<!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
<title>Studio QA report</title><style>
body{font:15px/1.5 system-ui,sans-serif;margin:0;padding:1.5rem;background:#f6f7f9;color:#17202a}
@media (prefers-color-scheme:dark){body{background:#0f1419;color:#e6e9ee} table{background:#171d24} th{background:#1e252e}}
h1{margin:0 0 .25rem} .meta{color:#6b7685} .agent{border:1px solid #dfe3e8;border-radius:10px;padding:1rem;margin:1rem 0}
table{border-collapse:collapse;width:100%;background:#fff} th,td{border:1px solid #dfe3e8;padding:.4rem;vertical-align:top;font-size:.9rem;text-align:left}
pre{white-space:pre-wrap;margin:0;font-size:.8rem} .s.blocker,.s.major,.o.failed{color:#b3261e;font-weight:700} .o.passed{color:#1f7a4d;font-weight:700}
.shots{display:grid;grid-template-columns:repeat(auto-fill,minmax(220px,1fr));gap:.5rem} .shots img{width:100%;border:1px solid #dfe3e8}
.gate{padding:.5rem 1rem;border-radius:8px;font-weight:700;display:inline-block}.gate.red{background:#fbe6e4;color:#b3261e}.gate.green{background:#e3f4ea;color:#1f7a4d}
</style></head><body>
<h1>Job Search Studio — QA report</h1>
<p class="meta">Target ${esc(report.target)} · build ${esc(report.build)} · ${esc(report.mode)} · started ${esc(report.startedAt)} · ${report.agents.length} agents, each on its own screen</p>
<p class="gate ${report.blocking.length ? "red" : "green"}">${report.blocking.length ? `${report.blocking.length} finding(s) block the release (${esc(report.failOn.join(", "))})` : "No blocking findings"}</p>
${rows}</body></html>`;
  fs.writeFileSync(path.join(out, "report.html"), html);
}

module.exports = { write, blocking, RANK };
