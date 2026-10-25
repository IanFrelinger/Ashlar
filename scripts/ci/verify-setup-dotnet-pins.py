#!/usr/bin/env python3
"""Every actions/setup-dotnet step pins exact versions, one per band, and the SDK band satisfies global.json.

Why exact: given a channel (`10.0.x`, or `global-json-file` with a roll-forward policy) dotnet-install resolves
the download through an aka.ms link, and on 2026-10-25 every aka.ms .NET link redirected to a storage account that
answers 409, which turned every job red at "Setup .NET". Given an exact version it downloads straight from
builds.dotnet.microsoft.com (then ci.dot.net) and never consults aka.ms.

global.json stays the source of truth for the SDK: the pin for its major.minor must be one that `dotnet` would
accept under its `rollForward` policy, so a global.json bump that the pins no longer satisfy fails here.

Usage: verify-setup-dotnet-pins.py [--root DIR]   (default: the repository containing this script)
"""
import argparse
import json
import re
import sys
from pathlib import Path

EXACT = re.compile(r"^\d+\.\d+\.\d+$")
SETUP = re.compile(r"^(\s*)(-\s+)?uses:\s*actions/setup-dotnet@")


def indent(line):
    return len(line) - len(line.lstrip(" "))


def step_block(lines, i):
    """Lines of the step whose `uses:` is on line i (excluding that line)."""
    m = SETUP.match(lines[i])
    if m.group(2):
        dash = len(m.group(1))
    else:
        col = indent(lines[i])
        dash = next((indent(lines[k]) for k in range(i - 1, -1, -1)
                     if lines[k].lstrip().startswith("- ") and indent(lines[k]) < col), col - 2)
    block = []
    for line in lines[i + 1:]:
        stripped = line.strip()
        if stripped and not stripped.startswith("#") and indent(line) <= dash:
            break
        block.append(line)
    return block


def versions(block):
    """(dotnet-version entries or None, uses global-json-file)."""
    found, global_json = None, False
    for n, line in enumerate(block):
        stripped = line.strip()
        if stripped.startswith("global-json-file:"):
            global_json = True
        if stripped.startswith("dotnet-version:"):
            value = stripped.split(":", 1)[1].strip()
            if value in ("|", "|-", ">", ">-"):
                own = indent(line)
                found = []
                for more in block[n + 1:]:
                    if more.strip() and indent(more) <= own:
                        break
                    if more.strip() and not more.strip().startswith("#"):
                        found.append(more.strip().strip("'\""))
            else:
                found = [v.strip().strip("'\"") for v in value.split("#", 1)[0].split(",") if v.strip()]
    return found, global_json


def satisfies(pin, wanted, policy):
    p, w = [int(x) for x in pin.split(".")], [int(x) for x in wanted.split(".")]
    if p[:2] != w[:2] or p < w:
        return False
    if policy == "disable":
        return p == w
    if policy in ("patch", "latestPatch"):
        return p[2] // 100 == w[2] // 100
    return True


INSTALL_DIRS = (".docker", ".devcontainer", ".github")
CHANNEL = re.compile(r"(dotnet-install|install-dotnet)\S*\s.*(--channel|-Channel)\b")


def channel_installs(root):
    """dotnet-install invocations (continuation lines joined) that pick a channel in CI images and workflows."""
    found = []
    for top in INSTALL_DIRS:
        for path in sorted((root / top).rglob("*")):
            if not path.is_file():
                continue
            try:
                text = path.read_text(encoding="utf-8")
            except (UnicodeDecodeError, OSError):
                continue
            lines, n = text.split("\n"), 0
            while n < len(lines):
                start, command = n, lines[n]
                while command.rstrip().endswith("\\") and n + 1 < len(lines):
                    n += 1
                    command = command.rstrip()[:-1] + " " + lines[n]
                if CHANNEL.search(command) and not command.lstrip().startswith("#"):
                    found.append(f"{path.relative_to(root).as_posix()}:{start + 1}")
                n += 1
    return found


def check(root):
    errors, pins, steps = [], {}, 0
    for where in channel_installs(root):
        errors.append(f"{where}: dotnet-install with a channel resolves through aka.ms; pass an exact --version")
    files = sorted(root.glob(".github/workflows/*.y*ml")) + sorted(root.glob("actions/**/action.y*ml"))
    for path in files:
        lines = path.read_text(encoding="utf-8").split("\n")
        for i, line in enumerate(lines):
            if not SETUP.match(line):
                continue
            steps += 1
            where = f"{path.relative_to(root).as_posix()}:{i + 1}"
            found, global_json = versions(step_block(lines, i))
            if global_json:
                errors.append(f"{where}: global-json-file resolves through aka.ms; pin dotnet-version exactly")
            if not found:
                errors.append(f"{where}: setup-dotnet without an exact dotnet-version")
                continue
            for v in found:
                if not EXACT.match(v):
                    errors.append(f"{where}: dotnet-version '{v}' is not exact (X.Y.Z); a channel resolves through aka.ms")
                    continue
                pins.setdefault(".".join(v.split(".")[:2]), {}).setdefault(v, []).append(where)

    for band, seen in sorted(pins.items()):
        if len(seen) > 1:
            detail = "; ".join(f"{v} at {', '.join(w)}" for v, w in sorted(seen.items()))
            errors.append(f"band {band} is pinned to {len(seen)} different versions: {detail}")

    sdk = json.loads((root / "global.json").read_text(encoding="utf-8")).get("sdk") or {}
    wanted, policy = sdk.get("version"), sdk.get("rollForward", "latestPatch")
    band = ".".join(str(wanted).split(".")[:2])
    if not wanted or not EXACT.match(wanted):
        errors.append(f"global.json: sdk.version '{wanted}' is not X.Y.Z")
    elif band not in pins:
        errors.append(f"no setup-dotnet step pins the global.json band {band}")
    else:
        for v in pins[band]:
            if not satisfies(v, wanted, policy):
                errors.append(f"pin {v} does not satisfy global.json sdk.version {wanted} with rollForward {policy}")

    return errors, steps, {b: sorted(s) for b, s in pins.items()}


def main():
    parser = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    root = parser.parse_args().root.resolve()
    errors, steps, pins = check(root)
    for error in errors:
        print(f"::error::verify-setup-dotnet-pins: {error}")
    if errors:
        return 1
    if steps == 0:
        print("::error::verify-setup-dotnet-pins: no setup-dotnet step found; the scan matched nothing")
        return 1
    print(f"verify-setup-dotnet-pins: OK ({steps} setup-dotnet steps; pins {pins}; global.json satisfied)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
