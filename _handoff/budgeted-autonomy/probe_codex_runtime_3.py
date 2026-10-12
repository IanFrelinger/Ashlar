"""Codex probe 3: the adapter's exact command lines. (r1) the fresh form with every override; (r2) the same with
--ignore-user-config (observed rejected by hand on 2026-10-12, recorded here); (r3) `exec resume` WITH the
overrides; (r4) `exec resume --help`, to see which flags resume accepts.

Usage: python probe_codex_runtime_3.py <codex.exe> <out.json> <probe-cwd>
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
OVERRIDES = ["-s", "workspace-write", "-c", 'approval_policy="never"', "-c", 'model_reasoning_effort="medium"',
             "-c", "mcp_servers={}", "-m", "gpt-6-astra"]


def clean_env():
    return {k: v for k, v in os.environ.items() if not (k.startswith("CLAUDE_CODE_") or k == "CLAUDECODE")}


def run(args, timeout=300):
    t0 = time.time()
    p = subprocess.run([CLI] + args, cwd=str(CWD), env=clean_env(), capture_output=True, timeout=timeout,
                       encoding="utf-8", errors="replace", stdin=subprocess.DEVNULL)
    evs = []
    for line in p.stdout.splitlines():
        if line.strip().startswith("{"):
            try:
                evs.append(json.loads(line))
            except json.JSONDecodeError:
                pass
    thread = next((e.get("thread_id") for e in evs if e.get("type") == "thread.started"), None)
    return {"argv": args, "exit": p.returncode, "seconds": round(time.time() - t0, 2), "thread_id": thread,
            "events": evs, "stderr": REDACT.sub("<email>", p.stderr[-1500:])}


def reset(name):
    f = CWD / name
    if f.exists():
        f.unlink()
    return f


report = {"cli": CLI, "cwd": str(CWD), "started": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())}
hello = reset("hello.txt")
reset("last.txt")
r = run(["exec", "--json", "--skip-git-repo-check", "-C", str(CWD)] + OVERRIDES + ["-o", str(CWD / "last.txt"),
         "Create a file named hello.txt in the current directory containing the single line hello, then reply done."])
r["hello_txt"] = hello.read_text(encoding="utf-8", errors="replace") if hello.exists() else None
r["last_txt"] = (CWD / "last.txt").read_text(encoding="utf-8", errors="replace") if (CWD / "last.txt").exists() else None
report["r1_fresh_adapter_argv"] = r
tid = r["thread_id"]

other = reset("other.txt")
r = run(["exec", "--json", "--skip-git-repo-check", "--ignore-user-config", "-C", str(CWD)] + OVERRIDES +
        ["Create a file named other.txt in the current directory containing the single line other, then reply done."])
r["other_txt"] = other.read_text(encoding="utf-8", errors="replace") if other.exists() else None
report["r2_ignore_user_config"] = r

if tid:
    r = run(["exec", "resume", "--json", "--skip-git-repo-check", "-c", 'approval_policy="never"',
             "-c", 'model_reasoning_effort="medium"', "-c", "mcp_servers={}", tid,
             "Append a second line again to hello.txt, then reply done."])
    r["hello_txt"] = hello.read_text(encoding="utf-8", errors="replace") if hello.exists() else None
    report["r3_resume_with_c_overrides"] = r
    r = run(["exec", "resume", "--json", "--skip-git-repo-check", "-m", "gpt-6-astra", tid, "Reply with exactly the word M."])
    report["r3b_resume_with_model_flag"] = r

h = subprocess.run([CLI, "exec", "resume", "--help"], capture_output=True, encoding="utf-8", errors="replace",
                   env=clean_env(), stdin=subprocess.DEVNULL, timeout=60)
report["r4_resume_help"] = h.stdout
report["finished"] = time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime())
OUT.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
for k, v in report.items():
    if isinstance(v, dict):
        print(k, "exit", v["exit"], "thread", v["thread_id"], [e.get("type") + (":" + e["item"]["type"] if "item" in e else "") for e in v["events"]],
              {kk: v[kk] for kk in v if kk.endswith("_txt")}, "stderr:", v["stderr"][-300:].replace("\n", " | "))
print("resume flags:", sorted(set(re.findall(r"\s(--?[a-z][a-z-]*)", report["r4_resume_help"]))))
