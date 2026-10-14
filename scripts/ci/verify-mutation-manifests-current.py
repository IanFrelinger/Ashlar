#!/usr/bin/env python3
"""Check exact mutation targets against the current checkout, without running tests.

WHY: a stale or ambiguous replacement invalidates a mutation run before its assertions
execute. Derive the population from spec007-*.json, with floors so an empty or
truncated scan cannot pass. This checks currency only, never mutation kill evidence.
"""
import argparse
import json
from pathlib import Path, PurePosixPath
import sys

MIN_MANIFESTS = 7
MIN_MUTATIONS = 285


def scan(root):
    manifests = sorted((root / "scripts/mutations").glob("spec007-*.json"))
    problems = []
    count = 0
    for manifest in manifests:
        try:
            data = json.loads(manifest.read_text(encoding="utf-8-sig"))
            cases = data["mutations"]
            if not isinstance(cases, list):
                raise ValueError("mutations must be a list")
        except (OSError, UnicodeError, ValueError, KeyError, TypeError) as error:
            problems.append(f"{manifest.name}: invalid manifest: {error}")
            continue
        count += len(cases)
        for case in cases:
            if not isinstance(case, dict):
                problems.append(f"{manifest.name}: invalid mutation object")
                continue
            relative = case.get("file")
            label = f"{manifest.name}: {case.get('id', '<missing id>')} ({relative})"
            if (not isinstance(relative, str) or not relative or "\\" in relative
                    or PurePosixPath(relative).is_absolute() or ":" in relative
                    or ".." in PurePosixPath(relative).parts):
                problems.append(f"{label}: file must be a relative repository path")
                continue
            path = root / relative
            if not path.is_file():
                problems.append(f"{label}: source file missing")
            else:
                old = case.get("old")
                if not isinstance(old, str) or not old:
                    problems.append(f"{label}: old must be nonempty text")
                else:
                    try:
                        matches = path.read_text(encoding="utf-8-sig").count(old)
                        if matches != 1:
                            problems.append(f"{label}: old occurs {matches} times; expected exactly 1")
                    except (OSError, UnicodeError) as error:
                        problems.append(f"{label}: cannot read source: {error}")
            if "expected_failures" in case:
                expected = case["expected_failures"]
                if (not isinstance(expected, list) or not expected
                        or any(not isinstance(name, str) or not name.strip() for name in expected)):
                    problems.append(f"{label}: expected_failures must be a nonempty list of test-name fragments")
    if len(manifests) < MIN_MANIFESTS:
        problems.append(f"found {len(manifests)} manifests; expected at least {MIN_MANIFESTS}")
    if count < MIN_MUTATIONS:
        problems.append(f"found {count} mutations; expected at least {MIN_MUTATIONS}")
    return problems, len(manifests), count


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[2])
    args = parser.parse_args()
    problems, manifests, mutations = scan(args.root)
    for problem in problems:
        print("mutation-manifests-current: ERROR: " + problem)
    print(f"mutation-manifests-current: {manifests} manifest(s), {mutations} mutation(s), {len(problems)} problem(s)")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main())
