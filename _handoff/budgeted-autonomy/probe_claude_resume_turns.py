"""One question for the installed Claude Code CLI: on `--resume`, is `--max-turns` enforced per invocation,
and what does `num_turns` count? The PR 4 pilot (2026-10-11) saw a resumed run report num_turns 48 under
`--max-turns 12` with subtype `success`, while two other resumed runs reported 13 with `error_max_turns`.

Three small invocations in a cleaned environment:
  1. a fresh session with --max-turns 8 and a prompt that needs about four tool calls (Read of four files);
  2. --resume of that session with --max-turns 2 and a prompt that needs about four more tool calls;
  3. --resume again with --max-turns 8 and a one-line answer.
Recorded: subtype, terminal_reason, num_turns, stop_reason, the count of assistant records the transcript
gained per invocation.

Usage: python probe_claude_resume_turns.py <out.json> <probe-cwd> [model]
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
MODEL = sys.argv[3] if len(sys.argv) > 3 else "claude-sonnet-5"
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
            if not (k.startswith("CLAUDE_CODE_") or k in ("CLAUDECODE", "ANTHROPIC_BASE_URL", "ANTHROPIC_AUTH_TOKEN", "ANTHROPIC_API_KEY"))}


def redact(s):
    return REDACT.sub("<email>", s) if isinstance(s, str) else s


def run(args, timeout=400):
    t0 = time.time()
    p = subprocess.run([CLI] + args, cwd=str(CWD), env=clean_env(), capture_output=True, timeout=timeout,
                       encoding="utf-8", errors="replace")
    result = None
    assistant = 0
    for line in p.stdout.splitlines():
        line = line.strip()
        if not line.startswith("{"):
            continue
        try:
            obj = json.loads(line)
        except json.JSONDecodeError:
            continue
        if obj.get("type") == "result":
            result = obj
        if obj.get("type") == "assistant":
            assistant += 1
    keep = {k: result.get(k) for k in ("subtype", "is_error", "terminal_reason", "num_turns", "stop_reason", "total_cost_usd",
                                        "duration_ms", "session_id")} if result else None
    return {"argv": args, "exit": p.returncode, "seconds": round(time.time() - t0, 2), "result": keep,
            "assistant_stream_records": assistant, "stderr_tail": redact(p.stderr[-600:])}


def transcript(sid):
    st = json.loads(subprocess.run([CLI, "auth", "status", "--json"], env=clean_env(), capture_output=True,
                                   encoding="utf-8", errors="replace").stdout)
    projects = pathlib.Path(st["projectsDirectory"])
    for entry in projects.iterdir():
        f = entry / f"{sid}.jsonl"
        if f.is_file():
            return f
    return None


def assistant_records(path):
    if not path or not path.is_file():
        return None
    n = 0
    for line in path.read_text(encoding="utf-8", errors="replace").splitlines():
        try:
            if json.loads(line).get("type") == "assistant":
                n += 1
        except (json.JSONDecodeError, AttributeError):
            pass
    return n


for i in range(1, 5):
    (CWD / f"f{i}.txt").write_text(f"file {i}: the number is {i * 11}\n", encoding="utf-8")

sid = str(uuid.uuid4())
base = ["--output-format", "stream-json", "--verbose", "--strict-mcp-config", "--model", MODEL, "--effort", "low",
        "--permission-prompts", "none", "--permission-mode", "acceptEdits", "--no-chrome", "--tools", "Read"]
report = {"cli": CLI, "model": MODEL, "session_id": sid, "cwd": str(CWD)}

r1 = run(["-p", "Read f1.txt, f2.txt, f3.txt and f4.txt one at a time, one Read call per turn, then reply with the four numbers.",
          "--max-turns", "8", "--session-id", sid] + base)
r1["transcript_assistant_records_after"] = assistant_records(transcript(sid))
report["p1_fresh_max8"] = r1

r2 = run(["-p", "Now read f4.txt, f3.txt, f2.txt and f1.txt again one at a time, one Read call per turn, then reply with the sum.",
          "--max-turns", "2", "--resume", sid] + base)
r2["transcript_assistant_records_after"] = assistant_records(transcript(sid))
report["p2_resume_max2"] = r2

r3 = run(["-p", "Reply with exactly the word DONE and nothing else.", "--max-turns", "8", "--resume", sid] + base)
r3["transcript_assistant_records_after"] = assistant_records(transcript(sid))
report["p3_resume_max8_one_line"] = r3

OUT.write_text(json.dumps(report, indent=2, sort_keys=True), encoding="utf-8")
print(json.dumps(report, indent=1, default=str))
