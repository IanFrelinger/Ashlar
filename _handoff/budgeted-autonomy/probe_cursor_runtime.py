"""Bounded verification of the Cursor agent CLI on this host for the budgeted-autonomy Cursor adapter.

Usage: python probe_cursor_runtime.py <cursor-agent.cmd> <out.json> <probe-cwd> [model]
Every probe is one `-p` (print) run with `--output-format stream-json` in a cleaned environment.
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
MODEL = sys.argv[4] if len(sys.argv) > 4 else None
CWD.mkdir(parents=True, exist_ok=True)
REDACT = re.compile(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}")


def clean_env():
    return {k: v for k, v in os.environ.items() if not (k.startswith("CLAUDE_CODE_") or k == "CLAUDECODE")}


def redact(s):
    return REDACT.sub("<email>", s) if isinstance(s, str) else s


def run(args, timeout=300):
    t0 = time.time()
    try:
        p = subprocess.run([CLI] + args, cwd=str(CWD), env=clean_env(), capture_output=True, timeout=timeout,
                           encoding="utf-8", errors="replace", stdin=subprocess.DEVNULL, shell=False)
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
    s = {"types": [], "keys_by_type": {}, "session_id": None, "result": None, "count": len(evs)}
    for e in evs:
        t = e.get("type")
        sub = e.get("subtype")
        s["types"].append(f"{t}/{sub}" if sub else t)
        s["keys_by_type"].setdefault(f"{t}/{sub}" if sub else str(t), sorted(e.keys()))
        for k in ("session_id", "sessionId", "chat_id", "chatId"):
            if e.get(k):
                s["session_id"] = e[k]
        if t == "result":
            s["result"] = {k: e.get(k) for k in e if k not in ("result",)}
            s["result_text"] = str(e.get("result"))[:300]
    return s


report = {"cli": CLI, "cwd": str(CWD), "model_arg": MODEL, "started": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
model_args = ["--model", MODEL] if MODEL else []
report["c0_version"] = run(["--version"], timeout=60)
report["c0_status"] = run(["status"], timeout=60)
report["c0_about"] = run(["about"], timeout=60)
report["c0_models"] = run(["models"], timeout=120)

# c1: smallest headless run, json then stream-json.
r = run(["-p", "--output-format", "json", "--trust", "--workspace", str(CWD)] + model_args +
        ["Reply with exactly the word OK and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["c1_print_json"] = r
r = run(["-p", "--output-format", "stream-json", "--trust", "--workspace", str(CWD)] + model_args +
        ["Reply with exactly the word OK and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["c1b_print_stream_json"] = r
sid = r["summary"]["session_id"]

# c2: resume by chat id.
if sid:
    r = run(["-p", "--output-format", "stream-json", "--trust", "--workspace", str(CWD), "--resume", sid] + model_args +
            ["Reply with exactly the word AGAIN and nothing else."])
    r["summary"] = summarise(events(r["stdout"]))
    report["c2_resume"] = r

# c3: a file edit (force so no approval prompt blocks a headless run).
hello = CWD / "hello.txt"
if hello.exists():
    hello.unlink()
r = run(["-p", "--output-format", "stream-json", "--trust", "--workspace", str(CWD), "-f"] + model_args +
        ["Create a file named hello.txt in the current directory containing the single line hello, then reply done."])
r["summary"] = summarise(events(r["stdout"]))
r["hello_txt"] = hello.read_text(encoding="utf-8", errors="replace") if hello.exists() else None
report["c3_edit_force"] = r

# c4: an unrecognised model and the policy's candidate.
r = run(["-p", "--output-format", "json", "--trust", "--workspace", str(CWD), "--model", "no-such-model-0",
         "Reply with exactly the word OK and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["c4_unrecognised_model"] = r
r = run(["-p", "--output-format", "json", "--trust", "--workspace", str(CWD), "--model", "composer-standard",
         "Reply with exactly the word OK and nothing else."])
r["summary"] = summarise(events(r["stdout"]))
report["c5_model_composer_standard"] = r

# c6: cancel mid-run, then resume.
story = CWD / "story.txt"
if story.exists():
    story.unlink()
argv = [CLI, "-p", "--output-format", "stream-json", "--trust", "--workspace", str(CWD), "-f"] + model_args + [
    "Write a 900-word story about a lighthouse keeper into story.txt, then reply done."]
t0 = time.time()
p = subprocess.Popen(argv, cwd=str(CWD), env=clean_env(), stdin=subprocess.DEVNULL, stdout=subprocess.PIPE,
                     stderr=subprocess.PIPE, encoding="utf-8", errors="replace")
lines = []
try:
    for _ in range(2):
        lines.append(p.stdout.readline())
    time.sleep(3)
    subprocess.run(["taskkill", "/T", "/F", "/PID", str(p.pid)], capture_output=True)
    out, err = p.communicate(timeout=60)
except subprocess.TimeoutExpired:
    p.kill()
    out, err = p.communicate()
csid = None
for ln in lines:
    try:
        o = json.loads(ln)
        for k in ("session_id", "sessionId", "chat_id", "chatId"):
            if o.get(k):
                csid = o[k]
    except (json.JSONDecodeError, AttributeError):
        pass
report["c6_cancel"] = {"argv": argv[1:], "first_lines": [redact(x) for x in lines], "exit": p.returncode,
                       "stdout_tail": redact((out or "")[-1200:]), "stderr_tail": redact((err or "")[-600:]),
                       "seconds": round(time.time() - t0, 2), "story_exists": story.exists(), "session_id": csid}
if csid:
    r = run(["-p", "--output-format", "stream-json", "--trust", "--workspace", str(CWD), "--resume", csid] + model_args +
            ["Reply with exactly the word RESUMED and nothing else."])
    r["summary"] = summarise(events(r["stdout"]))
    report["c6_resume_after_cancel"] = r

report["finished"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
OUT.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
print(json.dumps({k: (v.get("summary") if isinstance(v, dict) and "summary" in v else
                      (v if not isinstance(v, dict) else {kk: (v[kk][:600] if isinstance(v[kk], str) else v[kk]) for kk in v if kk in ("exit", "seconds", "timeout", "session_id", "story_exists", "hello_txt", "stdout", "stderr")}))
                  for k, v in report.items()}, indent=1, default=str))
