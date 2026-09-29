#!/usr/bin/env python3
"""Fail when a Markdown link points at a heading that does not exist.

WHY. README.md is packed into EVERY package this repository publishes
(Directory.Build.props sets PackageReadmeFile and packs the repo-root README), so a broken
in-page anchor is not an internal docs nit - it renders as a dead link on the nuget.org page of all
22 packages, for as long as that version exists. nuget.org allows unlisting but not deletion.

Nothing caught this class. The required `lychee (README + docs)` check runs without
`--include-fragments`, so it resolves that a FILE exists and never looks at the `#fragment`. Adding
that flag was considered and rejected here: lychee is already a required check that needed a retry
step for install flakes, and fragment checking widens it toward the network. This gate is local,
deterministic and needs no network.

WHAT IT CHECKS
  - a bare `#anchor` resolves against the headings of the SAME file;
  - a `path/to/file.md#anchor` resolves against the headings of THAT file.

Anchors are slugged the way GitHub does it: lower-case, inline Markdown stripped, characters that
are not word/space/hyphen removed, each remaining space turned into a hyphen (runs are NOT
collapsed, which is why an em-dash heading yields a double hyphen), and a repeated slug gets the
`-1`, `-2` suffix GitHub appends.

Exit 1 on any finding.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

# Below this, the scan has stopped seeing the docs tree and every assertion under it is vacuous.
MINIMUM_FILES = 100
MINIMUM_ANCHORS = 5

HEADING = re.compile(r"^(#{1,6})\s+(.*?)\s*#*$", re.M)
LINK = re.compile(r"\]\(([^)\s]*?)#([^)\s]+)\)")
FENCE = re.compile(r"^```.*?^```", re.M | re.S)
MD_LINK_TEXT = re.compile(r"\[([^\]]*)\]\([^)]*\)")


def slug(heading: str) -> str:
    text = MD_LINK_TEXT.sub(r"\1", heading)          # [text](url) -> text
    text = re.sub(r"[`*_~]", "", text)               # inline emphasis / code ticks
    text = text.strip().lower()
    text = re.sub(r"[^\w\s-]", "", text)             # GitHub drops the rest of the punctuation
    return re.sub(r"\s", "-", text)                  # EACH space, not each run


def anchors_of(text: str) -> set[str]:
    """Every anchor GitHub would mint for this document, including duplicate suffixes."""
    body = FENCE.sub("", text)
    seen: dict[str, int] = {}
    out: set[str] = set()
    for _, raw in HEADING.findall(body):
        base = slug(raw)
        if not base:
            continue
        n = seen.get(base, 0)
        out.add(base if n == 0 else f"{base}-{n}")
        seen[base] = n + 1
    return out


def main(argv: list[str]) -> int:
    root = Path(argv[1]).resolve() if len(argv) > 1 else Path(__file__).resolve().parents[2]
    files = [root / "README.md"] + sorted(root.glob("docs/**/*.md"))
    files = [f for f in files if f.is_file()]

    cache: dict[Path, set[str]] = {}

    def anchors_for(path: Path) -> set[str] | None:
        if path not in cache:
            if not path.is_file():
                return None
            cache[path] = anchors_of(path.read_text(encoding="utf-8", errors="replace"))
        return cache[path]

    findings: list[str] = []
    checked = 0

    for path in files:
        text = FENCE.sub("", path.read_text(encoding="utf-8", errors="replace"))
        for target, anchor in LINK.findall(text):
            if target.startswith(("http://", "https://", "mailto:")):
                continue
            dest = path if target == "" else (path.parent / target).resolve()
            if dest.suffix.lower() != ".md":
                continue
            available = anchors_for(dest)
            if available is None:
                continue  # a missing FILE is the phantom-path check's job, not this one
            checked += 1
            if anchor not in available:
                rel = dest.relative_to(root).as_posix()
                near = sorted(a for a in available if a[:6] == anchor[:6])[:3]
                hint = f" Closest: {', '.join(near)}." if near else ""
                findings.append(
                    f"{path.relative_to(root).as_posix()}: link to '{target}#{anchor}' - "
                    f"{rel} has no such heading.{hint}"
                )

    # POSITIVE CONTROL: a glob that stopped matching, or a link regex that stopped matching, leaves
    # nothing to check and every assertion above trivially satisfied.
    if len(files) < MINIMUM_FILES or checked < MINIMUM_ANCHORS:
        print(
            f"doc-anchors: ERROR: scanned {len(files)} file(s) and resolved {checked} anchor(s). "
            f"Below {MINIMUM_FILES} files or {MINIMUM_ANCHORS} anchors this gate is inspecting "
            f"nothing. Fix the scan; do not lower the bounds.",
            file=sys.stderr,
        )
        return 1

    if findings:
        print(
            "doc-anchors: ERROR: a Markdown link points at a heading that does not exist.\n"
            "  README.md is packed into every published package, so a dead anchor there renders "
            "on the nuget.org page of all 22 of them.",
            file=sys.stderr,
        )
        for f in findings:
            print(f"    {f}", file=sys.stderr)
        print(f"doc-anchors: {len(findings)} finding(s)", file=sys.stderr)
        return 1

    print(f"doc-anchors: {checked} anchor(s) across {len(files)} file(s) all resolve")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
