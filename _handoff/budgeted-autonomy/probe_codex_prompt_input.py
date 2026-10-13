"""Phase F probe: what the Codex CLI would show the model under the controller's overrides.

`codex debug prompt-input` renders the model-visible prompt input list WITHOUT calling a model. The probe
compares the adapter's PR 5 argv overrides with candidate overrides that also remove the operator's plugins
and MCP servers. It prints only marker counts, never account data. No model is called, nothing is written
outside the probe directory.

Usage: python -I probe_prompt_input.py <codex.exe> <cwd> <report.json>
"""
import json
import subprocess
import sys

CX, CWD, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
MARKERS = ["computer-use", "Control Windows", "browser", "chrome", "cua_repl", "node_repl", "code-review",
           "mcp__", "plugin", "Skills", "notify", "service_tier"]
# `debug prompt-input` has no -m; the model is set through -c model=..., the same config key -m sets.
PR5 = ["-c", 'approval_policy="never"', "-c", "model_reasoning_effort=medium", "-c", "mcp_servers={}",
       "-c", 'model="gpt-6-astra"']
VARIANTS = {
    "pr5-argv": PR5,
    "plugins-off": PR5 + ["-c", "features.plugins=false"],
    "plugins-off+node_repl-off": PR5 + ["-c", "features.plugins=false", "-c", "mcp_servers.node_repl.enabled=false"],
    "plugins-apps-hooks-cu-bu-off+node_repl-off": PR5 + [
        "-c", "features.plugins=false", "-c", "features.apps=false", "-c", "features.hooks=false",
        "-c", "features.computer_use=false", "-c", "features.browser_use=false",
        "-c", "mcp_servers.node_repl.enabled=false"],
}
report = {}
for name, extra in VARIANTS.items():
    p = subprocess.run([CX, "debug", "prompt-input", *extra, "Reply with the single word ok."], cwd=CWD,
                       capture_output=True, text=True, encoding="utf-8", errors="replace", timeout=120,
                       stdin=subprocess.DEVNULL)
    text = p.stdout
    entry = {"exit": p.returncode, "stdout_chars": len(text), "stderr_tail": p.stderr[-300:],
             "markers": {m: text.count(m) for m in MARKERS}}
    try:
        items = json.loads(text)
        entry["items"] = len(items) if isinstance(items, list) else None
        entry["roles"] = [i.get("role") or i.get("type") for i in items] if isinstance(items, list) else None
    except Exception as e:  # noqa: BLE001 - recorded, not raised
        entry["parse_error"] = str(e)[:200]
    report[name] = entry
    print(name, entry["exit"], entry["stdout_chars"], {k: v for k, v in entry["markers"].items() if v})
with open(OUT, "w", encoding="utf-8", newline="\n") as f:
    json.dump(report, f, indent=2)
