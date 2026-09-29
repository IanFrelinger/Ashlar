#!/usr/bin/env python3
"""
Fail when docs/SdkCompatibilityPolicy.md describes a state this repository is no longer in.

WHY. That document is the compatibility promise: it tells a consumer what is stable, and it tells a
contributor which of the two API files an addition goes in. It said "Nothing has shipped yet, so all
of the current surface lives in PublicAPI.Unshipped.txt, and every PublicAPI.Shipped.txt contains
only the #nullable enable header" for three releases after that stopped being true - by which point
the reverse held: 399 lines shipped in Ashlar.Abstractions, 465 in Ashlar.Brick.Contracts, and every
Unshipped.txt back to a bare header.

A wrong compatibility policy is worse than none. It is read and followed: a contributor who believes
Shipped.txt is empty treats a line in it as unclaimed, and changes a promise already published.

WHAT THIS CHECKS
  1. The document does not claim nothing has shipped while `git tag` lists a release tag.
  2. Every package named in its stable-tier table exists and carries both PublicAPI files - the
     mechanism the document says enforces the promise.
  3. Each of those has a NON-EMPTY PublicAPI.Shipped.txt, except where the document itself says the
     surface is empty by design (the metapackage). An empty baseline means nothing is promised, which
     contradicts the package being listed as stable.

It deliberately does NOT check line counts against numbers quoted in prose: that would fail on every
legitimate addition and teach people to edit the check. It checks the CLAIMS THAT CHANGE MEANING.

Exit 1 on any finding.
"""
from __future__ import annotations

import re
import subprocess
import sys
from pathlib import Path

POLICY = "docs/SdkCompatibilityPolicy.md"

# Phrases that assert a pre-first-release state. Kept as a small explicit list rather than something
# clever: the failure mode is a stale sentence, and matching the sentence is the point.
PRE_RELEASE_CLAIMS = (
    "Nothing has shipped yet",
    "nothing has shipped yet",
)

# The document states this one is empty on purpose; a metapackage declares no surface of its own.
EMPTY_BY_DESIGN = {"Ashlar.Hosting.Bundle"}

STABLE_ROW = re.compile(r"^\|\s*`(Ashlar\.[A-Za-z.]+)`")


def fail(message: str) -> None:
    print(f"compat-policy: ERROR: {message}", file=sys.stderr)


def release_tags(root: Path) -> list[str]:
    out = subprocess.run(
        ["git", "-C", str(root), "tag", "--list", "v*"],
        capture_output=True, text=True, check=False,
    ).stdout
    return sorted(t.strip() for t in out.splitlines() if t.strip())


def stable_tier_packages(text: str) -> list[str]:
    """Package names from the stable-tier table: the rows between its heading and the next heading."""
    start = text.find("| Package | Enforced by |")
    if start == -1:
        return []
    end = text.find("\n#", start)
    block = text[start:end if end != -1 else len(text)]
    names: list[str] = []
    for line in block.split("\n"):
        match = STABLE_ROW.match(line)
        if match:
            names.append(match.group(1))
    return names


def main(argv: list[str]) -> int:
    root = Path(argv[1]).resolve() if len(argv) > 1 else Path(__file__).resolve().parents[2]
    policy = root / POLICY
    if not policy.is_file():
        fail(f"{POLICY} is missing")
        return 1

    text = policy.read_text(encoding="utf-8")
    failures = 0

    # --- 1. the stale-state claim ---------------------------------------------------------------
    tags = release_tags(root)
    for claim in PRE_RELEASE_CLAIMS:
        if claim in text:
            if tags:
                fail(
                    f"{POLICY} says {claim!r}, but this repository has release tags "
                    f"({', '.join(tags)}). Every line already in a PublicAPI.Shipped.txt is a "
                    f"promise made to a published package, and a contributor who believes otherwise "
                    f"will change one. Update the 'Release step' section to describe the current "
                    f"state."
                )
                failures += 1
            break

    # --- 2 and 3. the mechanism the document points at ------------------------------------------
    packages = stable_tier_packages(text)

    # POSITIVE CONTROL: a table that parses to nothing would satisfy every check below.
    if len(packages) < 3:
        fail(
            f"parsed only {len(packages)} stable-tier package(s) from {POLICY}. The table's shape "
            f"has changed, so the checks below are inspecting nothing. Fix the parse; do not relax "
            f"this bound."
        )
        return 1

    print(f"compat-policy: {len(packages)} stable-tier packages: {', '.join(packages)}")

    for name in packages:
        project_dir = root / "src" / name
        if not project_dir.is_dir():
            fail(f"{POLICY} lists `{name}` as stable-tier, but src/{name}/ does not exist")
            failures += 1
            continue

        for api_file in ("PublicAPI.Shipped.txt", "PublicAPI.Unshipped.txt"):
            path = project_dir / api_file
            if not path.is_file():
                fail(
                    f"`{name}` is listed as stable-tier and enforced by PublicApiAnalyzers, but "
                    f"src/{name}/{api_file} does not exist - so nothing enforces its promise."
                )
                failures += 1

        shipped = project_dir / "PublicAPI.Shipped.txt"
        if shipped.is_file() and name not in EMPTY_BY_DESIGN:
            declared = [
                line for line in shipped.read_text(encoding="utf-8").splitlines()
                if line.strip() and not line.strip().startswith("#")
            ]
            if not declared:
                fail(
                    f"src/{name}/PublicAPI.Shipped.txt declares no API, but `{name}` is listed as a "
                    f"stable-tier package with a compatibility promise. Either it has not been "
                    f"released yet - in which case say so in {POLICY} rather than listing it as "
                    f"stable - or the promotion step was skipped at the last tag."
                )
                failures += 1

    if failures:
        print(f"compat-policy: {failures} finding(s)", file=sys.stderr)
        return 1

    print("compat-policy: current")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
