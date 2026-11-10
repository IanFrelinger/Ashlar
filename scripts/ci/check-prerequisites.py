#!/usr/bin/env python3
"""Read-only, job-specific dependency checks. JSON on stdout; diagnostics on stderr.

Run developer-dogfood INSIDE the CI/devtest environment, not on a developer host.
A successful receipt covers these explicit probes, not restore/network availability,
credentials, arbitrary transitive dependencies, or the correctness of the subsequent job.
"""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

PROFILES = ("ci-review", "ci-coordinate", "repo-gates", "developer-dogfood", "container-tests")
COMMON = ("bash", "git")
COMMANDS = {
    "ci-review": ("git",),
    "ci-coordinate": ("git",),
    "repo-gates": COMMON + ("dirname", "find", "sort", "xargs", "ls", "mktemp",
                            "mkdir", "rm", "cp", "cat", "grep", "sed", "awk", "head",
                            "tail", "cut", "tr", "wc", "diff", "tee", "timeout"),
    "developer-dogfood": COMMON + ("dotnet", "dirname", "mktemp", "mkdir", "cp",
                                   "cat", "tee", "grep", "sed", "head"),
    "container-tests": COMMON + ("docker", "dirname", "uname", "mktemp", "rm", "tar", "gzip", "tr"),
}
SCOPE = {
    "ci-review": "Python, pinned PyYAML and git for offline CI-agent review.",
    "ci-coordinate": "Python and git for combining existing CI-agent receipts; no parser, model or daemon dependency.",
    "repo-gates": "Common shell-lint and discovered gate prerequisites; not an exhaustive transitive dependency inventory.",
    "developer-dogfood": "CI/container toolchain for standalone brick authoring and hosting graph packing; no package-feed/network guarantee.",
    "container-tests": "Host container harness tools and daemon access; no host dotnet requirement or image/build/registry guarantee.",
}


def probe(argv, timeout):
    """Every external process is time-bounded; never install, build or run a container."""
    try:
        result = subprocess.run(argv, capture_output=True, text=True, timeout=timeout, check=False)
        output = (result.stdout + result.stderr).strip()
        return result.returncode == 0, output[:2000] or "exit %s (no output)" % result.returncode
    except (OSError, subprocess.TimeoutExpired) as exc:
        return False, str(exc)


def check(profile, timeout=10):
    checks = []

    def record(name, ok, detail, fix, required=True):
        checks.append(dict(name=name, required=required, status="pass" if ok else
                           ("fail" if required else "warning"), detail=detail, fix="" if ok else fix))

    version = sys.version_info[:3]
    record("python", (3, 11) <= version < (4, 0), sys.version.split()[0],
           "Install Python 3.11+ (below 4), then run this checker with that interpreter.")
    present = {}
    for command in COMMANDS[profile]:
        path = shutil.which(command)
        present[command] = bool(path)
        hint = "Install %s and put its executable on PATH." % command
        if command == "dotnet":
            hint = "Use the devtest container/CI environment with .NET SDK 10.0.403; do not build on the host."
        record("command:" + command, bool(path), path or "not found on PATH", hint)
    record("optional:rg", bool(shutil.which("rg")), "Optional search convenience; not used by these jobs.",
           "Optional: install ripgrep for interactive searches; no job fix is required.", required=False)

    if profile == "repo-gates":
        record("optional:shellcheck", bool(shutil.which("shellcheck")),
               "Required by the separate shell-lint step, not the bare repo-gates runner.",
               "Install shellcheck before running the shell-lint step.", required=False)
    if profile in ("ci-review", "repo-gates"):
        ok, detail = probe([sys.executable, "-c", "import yaml; print(yaml.__version__)"], timeout)
        pinned = profile == "ci-review"
        record("pyyaml", ok and bool(re.fullmatch(r"\d+\.\d+(?:\.\d+)?", detail)) and (not pinned or detail == "6.0.3"), detail,
               "Run the selected Python interpreter with -m pip install 'PyYAML==6.0.3' in the job's environment.")
    if present.get("git"):
        ok, detail = probe(["git", "--version"], timeout)
        record("git:executable", ok and bool(re.search(r"git version \d+\.\d+", detail)), detail,
               "Install a working git executable and correct PATH (a shim alone is insufficient).")
    if present.get("bash"):
        ok, detail = probe(["bash", "-c", "printf '%s' \"$BASH_VERSION\""], timeout)
        match = re.match(r"(\d+)\.", detail)
        record("bash:version", ok and bool(match) and int(match.group(1)) >= 4, detail,
               "Use Bash 4+ (CI uses Linux Bash); macOS system Bash 3 is insufficient.")
    if profile == "developer-dogfood" and present.get("dotnet"):
        ok, detail = probe(["dotnet", "--version"], timeout)
        match = re.fullmatch(r"10\.0\.(\d+)", detail)
        record("dotnet:sdk", ok and bool(match) and int(match.group(1)) >= 403, detail,
               "Install/select stable .NET SDK 10.0.403 or a later 10.0 patch/feature band in CI/devtest; check global.json and PATH.")
        ok, detail = probe(["dotnet", "--list-runtimes"], timeout)
        for runtime in ("Microsoft.NETCore.App", "Microsoft.AspNetCore.App"):
            found = re.search(r"^" + re.escape(runtime) + r" 8\.0\.\d+ \[", detail, re.MULTILINE)
            record("dotnet:runtime:" + runtime, ok and bool(found), detail,
                   "Install the actual ASP.NET Core 8 runtime (including .NET 8) in CI/devtest; DOTNET_ROLL_FORWARD is not a substitute.")
    if profile == "container-tests" and present.get("docker"):
        ok, detail = probe(["docker", "--version"], timeout)
        record("docker:cli", ok and bool(re.search(r"Docker version \d+\.", detail)), detail,
               "Install a working Docker CLI and correct PATH.")
        ok, detail = probe(["docker", "info", "--format", "{{.ServerVersion}}"], timeout)
        record("docker:daemon", ok and bool(re.match(r"\d+\.\d+", detail)), detail,
               "Start Docker Desktop/Engine, select the intended Docker context, and grant this user daemon/socket access; retry docker info.")
    return dict(schema_version=1, profile=profile, scope=SCOPE[profile],
                ok=all(c["status"] != "fail" for c in checks), checks=checks,
                source_sha=os.environ.get("GITHUB_SHA"), run_id=os.environ.get("GITHUB_RUN_ID"))


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--profile", required=True, choices=PROFILES)
    parser.add_argument("--json-output", type=Path, help="Also save the JSON receipt at this path")
    parser.add_argument("--timeout", type=float, default=10, help="Per-probe seconds (0 < timeout <= 60)")
    args = parser.parse_args(argv)
    if not 0 < args.timeout <= 60:
        parser.error("--timeout must be greater than zero and at most 60 seconds")
    receipt = check(args.profile, args.timeout)
    for item in receipt["checks"]:
        print("preflight: %s %s: %s%s" % (item["status"].upper(), item["name"], item["detail"],
              (" Fix: " + item["fix"]) if item["fix"] else ""), file=sys.stderr)
    encoded = json.dumps(receipt, indent=2) + "\n"
    print(encoded, end="")
    if args.json_output:
        try:
            args.json_output.parent.mkdir(parents=True, exist_ok=True)
            args.json_output.write_text(encoded, encoding="utf-8")
        except OSError as exc:
            print("preflight: FAIL cannot write receipt: %s" % exc, file=sys.stderr)
            return 1
    return 0 if receipt["ok"] else 1


if __name__ == "__main__":
    sys.exit(main())
