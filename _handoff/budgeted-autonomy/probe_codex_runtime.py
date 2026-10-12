"""Bounded verification of the installed Codex CLI for the budgeted-autonomy Codex adapter (phase E).

Every probe is one small `codex exec` in a cleaned environment (no CLAUDE_CODE_* / CLAUDECODE inherited
from the session that runs this script). The record is what the CLI printed, redacted of e-mail addresses.

Usage: python probe_codex_runtime.py <codex.exe> <out.json> <probe-cwd> [model]
"""
import json
import os
import pathlib
import re
import shutil
import subprocess
import sys
import time

CLI = sys.argv[1]
OUT = pathlib.Path(sys.argv[2])
CWD = pathlib.Path(sys.argv[3]).resolve()
MODEL = sys.argv[4] if len(sys.argv) > 4 else None
CWD.mkdir(parents=True, exist_ok=True)

REDACT = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
COMMON = ["--skip-git-repo-check", "-C", str(CWD)]


def clean_env():
    return {k: v for k, v in os.environ.items() if not (k.startswith("CLAUDE_CODE_") or k == "CLAUDECODE")}


def redact(s):
    return REDACT.sub("<email>", s) if isinstance(s, str) else s


def run(args, timeout=300, cwd=None):
    t0 = time.time()
    try:
        p = subprocess.run([CLI] + args, cwd=str(cwd or CWD), env=clean_env(), capture_output=True, timeout=timeout,
                           encoding="utf-8", errors="replace")
        return {"argv": args, "exit": p.returncode, "stdout": redact(p.stdout), "stderr": redact(p.stderr),
                "seconds": round(time.time() - t0, 2)}
    except subprocess.TimeoutExpired as e:
        return {"argv": args, "exit": None, "timeout": True, "stdout": redact(e.stdout.decode("utf-8", "replace") if isinstance(e.stdout, bytes) else (e.stdout or "")),
                "stderr": redact(e.stderr.decode("utf-8", "replace") if isinstance(e.stderr, bytes) else (e.stderr or "")),
                "seconds": round(time.time() - t0, 2)}


def events(stdout):
    out = []
    for line in (stdout or "").splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            out.append(json.loads(line))
        except json.JSONDecodeError:
            out.append({"_unparsed": line[:300]})
    return out


def summarise(evs):
    types = []
    thread = None
    usage = None
    model = None
    last_msg = None
    errors = []
    for e in evs:
        t = e.get("type")
        types.append(t)
        if t == "thread.started":
            thread = e.get("thread_id")
        if t == "turn.completed":
            usage = e.get("usage")
        if t == "item.completed":
            item = e.get("item") or {}
            if item.get("type") == "agent_message":
                last_msg = item.get("text")
        if t in ("error", "turn.failed"):
            errors.append(e)
        if e.get("model"):
            model = e.get("model")
    return {"types": types, "thread_id": thread, "usage": usage, "model": model, "last_message": last_msg,
            "errors": errors, "count": len(evs)}


def session_file_for(thread_id):
    home = pathlib.Path(os.environ.get("CODEX_HOME") or (pathlib.Path.home() / ".codex"))
    sess = home / "sessions"
    if not thread_id or not sess.is_dir():
        return None
    for p in sess.rglob("*.jsonl"):
        if thread_id in p.name:
            return str(p)
    return None


report = {"cli": CLI, "cwd": str(CWD), "model_arg": MODEL, "started": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
model_args = ["-m", MODEL] if MODEL else []

report["p0_version"] = run(["--version"], timeout=60)
report["p0_login_status"] = run(["login", "status"], timeout=60)

# p1: the smallest headless run, ephemeral, no tools needed, read-only sandbox; with and without the user config.
r = run(["exec", "--json", "--ephemeral", "-s", "read-only"] + COMMON + model_args +
        ["Reply with exactly the word OK and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["p1_exec_json_ephemeral"] = r
r = run(["exec", "--json", "--ephemeral", "--ignore-user-config", "-s", "read-only"] + COMMON + model_args +
        ["Reply with exactly the word OK and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["p1b_exec_json_ignore_user_config"] = r

# p2: a persisted session, then resume it by id.
r = run(["exec", "--json", "-s", "read-only"] + COMMON + model_args +
        ["Reply with exactly the word FIRST and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["p2_exec_persisted"] = r
tid = r["summary"]["thread_id"]
report["p2_session_file"] = session_file_for(tid)
if tid:
    r2 = run(["exec", "resume", "--json", tid, "Reply with exactly the word AGAIN and nothing else."])
    r2["summary"] = summarise(events(r2["stdout"]))
    report["p2_resume"] = r2
    if r2["exit"] not in (0,):
        r3 = run(["exec", "resume", tid, "Reply with exactly the word AGAIN and nothing else."])
        r3["summary"] = summarise(events(r3["stdout"]))
        report["p2_resume_no_json"] = r3

# p3: a file edit in the workspace-write sandbox (tool use shape).
hello = CWD / "hello.txt"
if hello.exists():
    hello.unlink()
r = run(["exec", "--json", "-s", "workspace-write"] + COMMON + model_args +
        ["Create a file named hello.txt in the current directory containing the single line hello, then reply done."])
r["summary"] = summarise(events(r["stdout"]))
r["hello_txt"] = hello.read_text(encoding="utf-8", errors="replace") if hello.exists() else None
report["p3_workspace_write"] = r

# p4: an unrecognised model id.
r = run(["exec", "--json", "--ephemeral", "-s", "read-only", "-m", "no-such-model-0"] + COMMON +
        ["Reply with exactly the word OK and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["p4_unrecognised_model"] = r

# p5: the policy's candidate id `sol`.
r = run(["exec", "--json", "--ephemeral", "-s", "read-only", "-m", "sol"] + COMMON +
        ["Reply with exactly the word OK and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["p5_model_sol"] = r

# p6: reasoning effort override and the last-message file.
last = CWD / "last.txt"
r = run(["exec", "--json", "--ephemeral", "-s", "read-only", "-c", 'model_reasoning_effort="low"', "-o", str(last)] + COMMON +
        model_args + ["Reply with exactly the word LOW and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
r["last_txt"] = last.read_text(encoding="utf-8", errors="replace") if last.exists() else None
report["p6_effort_and_last_message"] = r

# p7: output schema.
schema = CWD / "schema.json"
schema.write_text(json.dumps({"type": "object", "properties": {"answer": {"type": "string"}}, "required": ["answer"],
                              "additionalProperties": False}), encoding="utf-8")
r = run(["exec", "--json", "--ephemeral", "-s", "read-only", "--output-schema", str(schema)] + COMMON + model_args +
        ["What is 2+2? Answer as JSON."])
r["summary"] = summarise(events(r["stdout"]))
report["p7_output_schema"] = r

# p8: cancel mid-run (terminate the process after the first event), then resume the same thread.
story = CWD / "story.txt"
if story.exists():
    story.unlink()
argv = [CLI, "exec", "--json", "-s", "workspace-write"] + COMMON + model_args + [
    "Write a 600-word story about a lighthouse keeper into story.txt, then reply done."]
t0 = time.time()
p = subprocess.Popen(argv, cwd=str(CWD), env=clean_env(), stdout=subprocess.PIPE, stderr=subprocess.PIPE,
                     encoding="utf-8", errors="replace")
first = None
try:
    first = p.stdout.readline()
    time.sleep(4)
    if os.name == "nt":
        subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
    else:
        p.terminate()
    out, err = p.communicate(timeout=60)
except subprocess.TimeoutExpired:
    p.kill()
    out, err = p.communicate()
ctid = None
try:
    ctid = json.loads(first).get("thread_id") if first and first.strip().startswith("{") else None
except json.JSONDecodeError:
    pass
report["p8_cancel"] = {"argv": argv[1:], "first_line": redact(first), "exit": p.returncode, "stdout_tail": redact((out or "")[-1500:]),
                       "stderr_tail": redact((err or "")[-800:]), "seconds": round(time.time() - t0, 2),
                       "story_exists": story.exists(), "thread_id": ctid, "session_file": session_file_for(ctid)}
if ctid:
    r = run(["exec", "resume", "--json", ctid, "Reply with exactly the word RESUMED and nothing else."])
    r["summary"] = summarise(events(r["stdout"]))
    report["p8_resume_after_cancel"] = r

report["finished"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
OUT.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
print(json.dumps({k: (v.get("summary") if isinstance(v, dict) and "summary" in v else
                      (v if not isinstance(v, dict) else {kk: v[kk] for kk in v if kk in ("exit", "seconds", "timeout", "thread_id", "story_exists", "hello_txt", "last_txt")}))
                  for k, v in report.items()}, indent=1, default=str))
