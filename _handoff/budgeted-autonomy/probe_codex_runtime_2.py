"""Codex probe 2 (after probe 1): code mode disabled so the classic tools run; resume with the git-check flag;
cancel then resume; stdin closed; the app-server rate-limit read; the feature list.

Usage: python probe_codex_runtime_2.py <codex.exe> <out.json> <probe-cwd>
"""
import json
import os
import pathlib
import re
import subprocess
import sys
import time

CLI = sys.argv[1]
OUT = pathlib.Path(sys.argv[2])
CWD = pathlib.Path(sys.argv[3]).resolve()
CWD.mkdir(parents=True, exist_ok=True)
REDACT = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")
COMMON = ["--skip-git-repo-check", "-C", str(CWD)]
NO_CODE_MODE = ["-c", "features.code_mode=false"]


def clean_env():
    return {k: v for k, v in os.environ.items() if not (k.startswith("CLAUDE_CODE_") or k == "CLAUDECODE")}


def redact(s):
    return REDACT.sub("<email>", s) if isinstance(s, str) else s


def run(args, timeout=300, stdin_text=None):
    t0 = time.time()
    try:
        p = subprocess.run([CLI] + args, cwd=str(CWD), env=clean_env(), capture_output=True, timeout=timeout,
                           encoding="utf-8", errors="replace", input=stdin_text,
                           stdin=(None if stdin_text is not None else subprocess.DEVNULL))
        return {"argv": args, "exit": p.returncode, "stdout": redact(p.stdout), "stderr": redact(p.stderr),
                "seconds": round(time.time() - t0, 2)}
    except subprocess.TimeoutExpired as e:
        so = e.stdout.decode("utf-8", "replace") if isinstance(e.stdout, bytes) else (e.stdout or "")
        se = e.stderr.decode("utf-8", "replace") if isinstance(e.stderr, bytes) else (e.stderr or "")
        return {"argv": args, "exit": None, "timeout": True, "stdout": redact(so), "stderr": redact(se),
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
    s = {"types": [], "item_types": [], "thread_id": None, "usage": None, "last_message": None, "errors": [], "count": len(evs)}
    for e in evs:
        t = e.get("type")
        s["types"].append(t)
        if t == "thread.started":
            s["thread_id"] = e.get("thread_id")
        if t == "turn.completed":
            s["usage"] = e.get("usage")
        if t in ("item.completed", "item.started", "item.updated"):
            item = e.get("item") or {}
            s["item_types"].append(f"{t}:{item.get('type')}")
            if t == "item.completed" and item.get("type") == "agent_message":
                s["last_message"] = item.get("text")
        if t in ("error", "turn.failed"):
            s["errors"].append(e)
    return s


def session_file_for(thread_id):
    home = pathlib.Path(os.environ.get("CODEX_HOME") or (pathlib.Path.home() / ".codex"))
    sess = home / "sessions"
    if not thread_id or not sess.is_dir():
        return None
    for p in sess.rglob("*.jsonl"):
        if thread_id in p.name:
            return str(p)
    return None


report = {"cli": CLI, "cwd": str(CWD), "started": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}

report["q0_features"] = run(["features"], timeout=60)

# q1: a file edit with code mode off, stdin closed.
hello = CWD / "hello.txt"
if hello.exists():
    hello.unlink()
r = run(["exec", "--json", "-s", "workspace-write"] + NO_CODE_MODE + COMMON +
        ["Create a file named hello.txt in the current directory containing the single line hello, then reply done."])
r["summary"] = summarise(events(r["stdout"]))
r["hello_txt"] = hello.read_text(encoding="utf-8", errors="replace") if hello.exists() else None
report["q1_edit_code_mode_off"] = r
tid = r["summary"]["thread_id"]
report["q1_session_file"] = session_file_for(tid)

# q2: resume that thread with the git-check flag.
if tid:
    r = run(["exec", "resume", "--json", "--skip-git-repo-check", tid, "Append a second line 'again' to hello.txt, then reply done."])
    r["summary"] = summarise(events(r["stdout"]))
    r["hello_txt"] = hello.read_text(encoding="utf-8", errors="replace") if hello.exists() else None
    report["q2_resume"] = r
    if r["exit"] != 0:
        r = run(["exec", "resume", "--json", "--skip-git-repo-check", "-C", str(CWD), tid, "Reply with exactly the word AGAIN."])
        r["summary"] = summarise(events(r["stdout"]))
        report["q2b_resume_with_cd"] = r

# q3: cancel mid-run, then resume.
story = CWD / "story.txt"
if story.exists():
    story.unlink()
argv = [CLI, "exec", "--json", "-s", "workspace-write"] + NO_CODE_MODE + COMMON + [
    "Write a 900-word story about a lighthouse keeper into story.txt, then reply done."]
t0 = time.time()
p = subprocess.Popen(argv, cwd=str(CWD), env=clean_env(), stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, encoding="utf-8", errors="replace")
lines = []
try:
    for _ in range(3):
        lines.append(p.stdout.readline())
    time.sleep(3)
    subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
    out, err = p.communicate(timeout=60)
except subprocess.TimeoutExpired:
    p.kill()
    out, err = p.communicate()
ctid = None
for ln in lines:
    try:
        o = json.loads(ln)
        if o.get("type") == "thread.started":
            ctid = o.get("thread_id")
    except (json.JSONDecodeError, AttributeError):
        pass
report["q3_cancel"] = {"argv": argv[1:], "first_lines": [redact(x) for x in lines], "exit": p.returncode,
                       "stdout_tail": redact((out or "")[-1200:]), "stderr_tail": redact((err or "")[-600:]),
                       "seconds": round(time.time() - t0, 2), "story_exists": story.exists(), "thread_id": ctid,
                       "session_file": session_file_for(ctid)}
if ctid:
    r = run(["exec", "resume", "--json", "--skip-git-repo-check", ctid, "Reply with exactly the word RESUMED and nothing else."])
    r["summary"] = summarise(events(r["stdout"]))
    report["q3_resume_after_cancel"] = r

# q4: the app-server rate-limit read over stdio JSON-RPC (two method-name guesses).
init = {"jsonrpc": "2.0", "id": 1, "method": "initialize",
        "params": {"clientInfo": {"name": "ashlar-probe", "version": "0.0.0"}, "capabilities": {}}}
initialized = {"jsonrpc": "2.0", "method": "initialized", "params": {}}
for i, method in enumerate(("account/rateLimits/read", "getAccountRateLimits")):
    req = {"jsonrpc": "2.0", "id": 2, "method": method, "params": {}}
    text = "\n".join(json.dumps(x) for x in (init, initialized, req)) + "\n"
    r = run(["app-server"], timeout=60, stdin_text=text)
    report[f"q4_{i}_app_server_{method.replace('/', '_')}"] = {"exit": r["exit"], "timeout": r.get("timeout"),
                                                               "stdout": r["stdout"][:4000], "stderr": r["stderr"][-1500:],
                                                               "seconds": r["seconds"]}

# q5: what the session file records about the model.
if report.get("q1_session_file"):
    txt = pathlib.Path(report["q1_session_file"]).read_text(encoding="utf-8", errors="replace")
    models = sorted(set(re.findall(r'"model":\s*"([^"]+)"', txt)))
    report["q5_models_in_session_file"] = models
    report["q5_session_head"] = redact(txt[:1500])

report["finished"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
OUT.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
print(json.dumps({k: (v.get("summary") if isinstance(v, dict) and "summary" in v else
                      (v if not isinstance(v, dict) else {kk: v[kk] for kk in v if kk in ("exit", "seconds", "timeout", "thread_id", "story_exists", "hello_txt")}))
                  for k, v in report.items()}, indent=1, default=str))
