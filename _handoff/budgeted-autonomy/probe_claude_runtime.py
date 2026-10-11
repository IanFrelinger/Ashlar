"""Bounded verification of the installed Claude Code runtime for the budgeted-autonomy Claude adapter.

Every probe is one tiny `claude -p` invocation (max one turn, no tools unless the probe is about a
tool) in a cleaned environment (no CLAUDE_CODE_* / CLAUDECODE / ANTHROPIC_BASE_URL inherited from the
session that runs this script), so what is observed is what the operator's host would see.

Usage: python probe_claude_runtime.py <out.json> <probe-cwd> [model]
"""
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time
import uuid

OUT = pathlib.Path(sys.argv[1])
CWD = pathlib.Path(sys.argv[2]).resolve()
MODEL = sys.argv[3] if len(sys.argv) > 3 else "claude-sonnet-5-5"
CWD.mkdir(parents=True, exist_ok=True)

REDACT = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")


def cli_path():
    w = shutil.which("claude")
    if w and w.lower().endswith(".cmd"):
        exe = pathlib.Path(w).parent / "node_modules" / "@anthropic-ai" / "claude-code" / "bin" / "claude.exe"
        if exe.exists():
            return str(exe)
    return w


CLI = cli_path()


def clean_env():
    return {k: v for k, v in os.environ.items()
            if not (k.startswith("CLAUDE_CODE_") or k in ("CLAUDECODE", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN"))}


def redact(s):
    return REDACT.sub("<email>", s) if isinstance(s, str) else s


def run(args, timeout=240):
    t0 = time.time()
    p = subprocess.run([CLI] + args, cwd=str(CWD), env=clean_env(), capture_output=True, timeout=timeout,
                       encoding="utf-8", errors="replace")
    return {"argv": args, "exit": p.returncode, "stdout": redact(p.stdout), "stderr": redact(p.stderr),
            "seconds": round(time.time() - t0, 2)}


def parse_json_result(stdout):
    stdout = stdout.strip()
    if not stdout:
        return None
    try:
        return json.loads(stdout)
    except json.JSONDecodeError:
        last = None
        for line in stdout.splitlines():
            line = line.strip()
            if line.startswith("{"):
                try:
                    obj = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if obj.get("type") == "result":
                    last = obj
        return last


def parse_stream(stdout):
    events = []
    for line in stdout.splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            events.append(json.loads(line))
        except json.JSONDecodeError:
            events.append({"unparsed": line[:200]})
    return events


def projects_dir():
    st = json.loads(subprocess.run([CLI, "auth", "status", "--json"], env=clean_env(), capture_output=True,
                                   encoding="utf-8", errors="replace").stdout)
    return pathlib.Path(st["projectsDirectory"]), st


def slug_for(path):
    return re.sub(r"[^A-Za-z0-9]", "-", str(path))


def transcript_for(projects, sid):
    expected = projects / slug_for(CWD) / f"{sid}.jsonl"
    found = sorted(projects.glob(f"*/{sid}.jsonl"))
    info = {"expected_path": str(expected), "expected_exists": expected.exists(),
            "found": [str(f) for f in found]}
    if found:
        f = found[0]
        text = f.read_text(encoding="utf-8", errors="replace")
        info["bytes"] = f.stat().st_size
        info["lines"] = len([l for l in text.splitlines() if l.strip()])
        kinds = []
        for l in text.splitlines():
            try:
                kinds.append(json.loads(l).get("type"))
            except Exception:
                kinds.append("?")
        info["line_types"] = kinds
    return info


report = {"cli": CLI, "model_requested": MODEL, "cwd": str(CWD), "started_at": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
report["version"] = run(["--version"])["stdout"].strip()
projects, auth = projects_dir()
report["auth_status"] = {k: redact(v) for k, v in auth.items() if k not in ("email", "orgName")}
report["auth_status"]["email"] = "<redacted>"
report["auth_status"]["orgName"] = "<redacted>"

common = ["--model", MODEL, "--effort", "low", "--permission-prompts", "none"]

# P1: headless launch, stream-json, capture init (tools) and result (usage figures), our own session id.
sid1 = str(uuid.uuid4())
r1 = run(["-p", "Reply with exactly the word OK and nothing else.", "--output-format", "stream-json", "--verbose",
          "--tools", "", "--max-turns", "1", "--session-id", sid1, "--permission-mode", "manual"] + common)
ev1 = parse_stream(r1["stdout"])
init1 = next((e for e in ev1 if e.get("type") == "system" and e.get("subtype") == "init"), None)
res1 = next((e for e in ev1 if e.get("type") == "result"), None)
report["p1_launch_stream"] = {"run": {k: v for k, v in r1.items() if k != "stdout"}, "event_types": [(e.get("type"), e.get("subtype")) for e in ev1],
                              "init": init1, "result": res1, "session_id_matches": bool(res1 and res1.get("session_id") == sid1),
                              "transcript": transcript_for(projects, sid1)}

# P2: exact resume of the same session; are num_turns / total_cost_usd per invocation or cumulative?
r2 = run(["-p", "Reply with exactly the word AGAIN and nothing else.", "--output-format", "json", "--resume", sid1,
          "--tools", "", "--max-turns", "1"] + common)
res2 = parse_json_result(r2["stdout"])
report["p2_resume"] = {"run": {k: v for k, v in r2.items() if k != "stdout"}, "result": res2,
                       "session_id_matches": bool(res2 and res2.get("session_id") == sid1),
                       "transcript_after": transcript_for(projects, sid1)}

# P3: structured output with --json-schema, and --no-session-persistence.
sid3 = str(uuid.uuid4())
schema = json.dumps({"type": "object", "properties": {"answer": {"type": "string"}}, "required": ["answer"]})
r3 = run(["-p", "What is 2+2? Put the number, as a string, in the answer field.", "--output-format", "json",
          "--json-schema", schema, "--tools", "", "--max-turns", "1", "--session-id", sid3, "--no-session-persistence"] + common)
res3 = parse_json_result(r3["stdout"])
report["p3_structured_output"] = {"run": {k: v for k, v in r3.items() if k != "stdout"}, "result": res3,
                                  "transcript": transcript_for(projects, sid3)}

# P4: the turn ceiling: a task that needs a tool call plus an answer, under --max-turns 1.
(CWD / "probe.txt").write_text("the secret word is PELICAN\n", encoding="utf-8")
sid4 = str(uuid.uuid4())
r4 = run(["-p", "Use the Read tool to read probe.txt in the current directory, then reply with the secret word it contains.",
          "--output-format", "json", "--tools", "Read", "--max-turns", "1", "--session-id", sid4, "--permission-mode", "acceptEdits"] + common)
res4 = parse_json_result(r4["stdout"])
report["p4_max_turns"] = {"run": {k: v for k, v in r4.items() if k != "stdout"}, "result": res4}

# P5: a permission that nobody can grant (--permission-prompts none, manual mode, Bash).
sid5 = str(uuid.uuid4())
r5 = run(["-p", "Use the Bash tool to run `git --version` and reply with its output verbatim. Do not answer from memory.",
          "--output-format", "json", "--tools", "Bash", "--max-turns", "3", "--session-id", sid5, "--permission-mode", "manual"] + common)
res5 = parse_json_result(r5["stdout"])
report["p5_permission_denied"] = {"run": {k: v for k, v in r5.items() if k != "stdout"}, "result": res5}

# P6: a model id that does not exist: the launch-failure shape (no tokens should be spent).
sid6 = str(uuid.uuid4())
r6 = run(["-p", "Reply OK.", "--output-format", "json", "--model", "claude-no-such-model-0", "--effort", "low",
          "--permission-prompts", "none", "--tools", "", "--max-turns", "1", "--session-id", sid6, "--no-session-persistence"])
res6 = parse_json_result(r6["stdout"])
report["p6_bad_model"] = {"run": {k: v for k, v in r6.items() if k != "stdout"}, "result": res6}

# P7: cancellation: start a longer answer, terminate the process once the init event has arrived.
sid7 = str(uuid.uuid4())
out7 = CWD / "p7-stdout.jsonl"
err7 = CWD / "p7-stderr.log"
with out7.open("w", encoding="utf-8") as fo, err7.open("w", encoding="utf-8") as fe:
    t0 = time.time()
    p7 = subprocess.Popen([CLI, "-p", "Write a 600-word story about a lighthouse keeper.", "--output-format", "stream-json",
                           "--verbose", "--tools", "", "--max-turns", "1", "--session-id", sid7, "--permission-mode", "manual"] + common,
                          cwd=str(CWD), env=clean_env(), stdout=fo, stderr=fe)
    saw_init = False
    deadline = time.time() + 20
    while time.time() < deadline and p7.poll() is None:
        time.sleep(0.25)
        try:
            txt = out7.read_text(encoding="utf-8", errors="replace")
        except OSError:
            txt = ""
        if '"subtype":"init"' in txt or '"subtype": "init"' in txt:
            saw_init = True
            time.sleep(1.0)  # let the model request start
            break
    alive_before = p7.poll() is None
    p7.terminate()
    try:
        rc7 = p7.wait(timeout=15)
    except subprocess.TimeoutExpired:
        p7.kill()
        rc7 = p7.wait(timeout=15)
    killed_after = round(time.time() - t0, 2)
ev7 = parse_stream(out7.read_text(encoding="utf-8", errors="replace"))
report["p7_cancel"] = {"saw_init": saw_init, "alive_before_terminate": alive_before, "exit": rc7, "seconds": killed_after,
                       "event_types": [(e.get("type"), e.get("subtype")) for e in ev7],
                       "result_event_present": any(e.get("type") == "result" for e in ev7),
                       "stderr": redact(err7.read_text(encoding="utf-8", errors="replace"))[-500:],
                       "transcript": transcript_for(projects, sid7)}

# P8: is a cancelled session resumable (the recoverable handoff)?
r8 = run(["-p", "Reply with exactly the word RESUMED and nothing else.", "--output-format", "json", "--resume", sid7,
          "--tools", "", "--max-turns", "1"] + common)
res8 = parse_json_result(r8["stdout"])
report["p8_resume_after_cancel"] = {"run": {k: v for k, v in r8.items() if k != "stdout"}, "result": res8,
                                    "session_id_matches": bool(res8 and res8.get("session_id") == sid7),
                                    "transcript_after": transcript_for(projects, sid7)}

report["finished_at"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
OUT.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
print(json.dumps({k: (v if not isinstance(v, dict) else {kk: vv for kk, vv in v.items() if kk in ("run", "session_id_matches", "saw_init", "exit", "result_event_present")})
                  for k, v in report.items()}, indent=1, sort_keys=True))
