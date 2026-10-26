#!/usr/bin/env python3
"""Record the real github.blockers answer for every target URL, through Ashlar.Mcp.Server.Host.

Usage: capture_brick.py <out-dir> [--targets <file>] -- <command that starts Ashlar.Mcp.Server.Host>
Writes <out-dir>/<slug>.json (the brick's full JSON payload, exactly what the MCP tool returned)
and <out-dir>/captured-at.txt. --targets defaults to targets.json next to this script.
Reads GITHUB_TOKEN if set. Runs in CI, where dotnet is available.
"""
import datetime
import json
import os
import pathlib
import sys

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE.parent.parent / "actions" / "pr-blockers"))
import pr_blockers  # noqa: E402


def slug(url: str) -> str:
    return url.removeprefix("https://github.com/").replace("/", "_")


def main(argv):
    out = pathlib.Path(argv[0])
    split = argv.index("--")
    command = argv[split + 1:]
    options = argv[1:split]
    targets_file = pathlib.Path(options[options.index("--targets") + 1]) if "--targets" in options \
        else HERE / "targets.json"
    out.mkdir(parents=True, exist_ok=True)
    targets = json.loads(targets_file.read_text())
    token = os.environ.get("GITHUB_TOKEN", "")
    for w in targets["workloads"]:
        for url in w["urls"]:
            try:
                payload = pr_blockers.ask(command, url, token, 180)
            except pr_blockers.NoAnswer as e:
                payload = {"no_answer": str(e), "outputs": {"verdict": "no-answer"}}
            (out / f"{slug(url)}.json").write_text(json.dumps(payload, indent=1))
            print(f"{url}: {payload['outputs']['verdict']}")
    (out / "captured-at.txt").write_text(datetime.datetime.now(datetime.timezone.utc).isoformat() + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
