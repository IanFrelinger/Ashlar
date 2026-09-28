#!/usr/bin/env python3
"""
Fail when docs/knowledge-graph.{json,md} and the repository disagree.

This is the whole reason the graph is worth having. A derived map that nobody is forced to regenerate
becomes a confident description of a repository that has moved on, which is worse than no map: it is
read and believed. So regenerating is not a courtesy, it is a required check.

THREE THINGS THIS CHECKS, and the third is the one that is easy to forget
------------------------------------------------------------------------
1. DRIFT. Regenerate in memory and compare against what is committed. Any difference is a failure
   with a diff, in the shape scripts/verify-pack-ashlar-hosting-graph-alignment.py already uses.

2. FLOORS. A drift check alone is satisfied by a graph that describes nothing. If an extractor's glob
   or regex silently stops matching, the regeneration and the committed file agree perfectly — on
   emptiness — and the gate goes green while the map is blank. This is the same failure the
   cert-gate zero-test guard exists for: a filter that matches nothing exits 0. Each entity kind
   therefore carries a floor far below its real count but far above zero.

3. THE DEBT RATCHET. The graph reports how many operator-facing variables no document mentions. That
   number must not grow. It cannot be ratcheted against a value inside the graph itself — an author
   would regenerate, the number would rise, the file would still match the tree, and the gate would
   pass. The baseline therefore lives in its own committed file, and lowering it is required when the
   debt falls, so the ratchet cannot be left slack after someone improves things.

Exit 1 on any failure. Exit 0 only when the committed artifacts are exactly what the tree produces.
"""
from __future__ import annotations

import difflib
import json
import sys
from pathlib import Path

# The generator's filename has hyphens, so it cannot be imported by name. Load it by path rather than
# renaming it: the name appears in the workflow step, in docs/Configuration.md and inside both
# generated artifacts, and reusing its build()/render_markdown() is what guarantees this check
# compares against the SAME code that produced the committed files rather than a second copy of the
# extraction rules.
import importlib.util  # noqa: E402

_spec = importlib.util.spec_from_file_location(
    "kg_builder", Path(__file__).resolve().parent / "build-knowledge-graph.py"
)
kg = importlib.util.module_from_spec(_spec)
assert _spec.loader is not None
_spec.loader.exec_module(kg)

# Deliberately far below the real values (61 projects, 161 variables, 4353 facts, 62 workflows as of
# 2026-09-28) and far above zero. The point is not to pin the counts — that is what the drift check
# does — but to make a COLLAPSE impossible to mistake for agreement.
FLOORS = {
    "projects": 40,
    "config_variables": 100,
    "operator_facing_config_variables": 80,
    "test_files_declaring_facts": 500,
    "declared_facts": 3000,
    "workflows": 40,
}

BASELINE_FILE = "scripts/knowledge-graph/documentation-debt.baseline"


def fail(message: str) -> None:
    print(f"knowledge-graph: ERROR: {message}", file=sys.stderr)


def main() -> int:
    root = kg.repo_root()
    failures = 0

    graph = kg.build(root)
    expected_json = json.dumps(graph, indent=2, sort_keys=False, ensure_ascii=False) + "\n"
    expected_md = kg.render_markdown(graph)

    # --- 1. drift -------------------------------------------------------------------------------
    for rel_path, expected in (
        ("docs/knowledge-graph.json", expected_json),
        ("docs/knowledge-graph.md", expected_md),
    ):
        path = root / rel_path
        if not path.is_file():
            fail(f"{rel_path} is missing. Run: python3 scripts/knowledge-graph/build-knowledge-graph.py")
            failures += 1
            continue
        actual = path.read_text(encoding="utf-8")
        if actual != expected:
            fail(
                f"{rel_path} does not match the repository. The graph is DERIVED, so this means the "
                f"tree changed and the artifact was not regenerated. Run:\n"
                f"         python3 scripts/knowledge-graph/build-knowledge-graph.py\n"
                f"       and commit the result. First differences:"
            )
            diff = difflib.unified_diff(
                actual.splitlines(), expected.splitlines(),
                fromfile=f"committed/{rel_path}", tofile=f"regenerated/{rel_path}", lineterm="", n=1,
            )
            for line in list(diff)[:40]:
                print(f"         {line}", file=sys.stderr)
            failures += 1

    # --- 2. floors ------------------------------------------------------------------------------
    counts = graph["counts"]
    for key, floor in sorted(FLOORS.items()):
        value = counts.get(key)
        if value is None:
            fail(f"counts.{key} is missing from the generated graph; the floor cannot be applied")
            failures += 1
        elif value < floor:
            fail(
                f"counts.{key} is {value}, below the floor of {floor}. An extractor has probably "
                f"stopped matching — a graph can agree with itself perfectly while describing "
                f"nothing, which is what this floor exists to catch. Fix the extractor; do not "
                f"lower the floor to make this pass."
            )
            failures += 1

    if not graph["cert_gate_selected_namespaces"]:
        fail(
            "cert_gate_selected_namespaces is empty, so scripts/cert-gate-config.sh could not be "
            "parsed. Every 'in_cert_gate_filter' flag in the graph is therefore false, which reads "
            "as 'nothing is gated' rather than as a broken parse."
        )
        failures += 1

    # --- 3. the debt ratchet --------------------------------------------------------------------
    baseline_path = root / BASELINE_FILE
    current = counts.get("operator_vars_absent_from_all_documentation")
    if not baseline_path.is_file():
        fail(f"{BASELINE_FILE} is missing; the documentation-debt ratchet cannot be applied")
        failures += 1
    elif current is None:
        fail("the graph does not report operator_vars_absent_from_all_documentation")
        failures += 1
    else:
        raw = [
            line.strip()
            for line in baseline_path.read_text(encoding="utf-8").splitlines()
            if line.strip() and not line.strip().startswith("#")
        ]
        if len(raw) != 1 or not raw[0].isdigit():
            fail(f"{BASELINE_FILE} must contain exactly one integer (comments with # are allowed)")
            failures += 1
        else:
            baseline = int(raw[0])
            if current > baseline:
                added = sorted(graph["operator_vars_absent_from_all_documentation"])
                fail(
                    f"{current} operator-facing variables are mentioned in no document, up from the "
                    f"baseline of {baseline}. A knob an operator cannot discover is a knob they "
                    f"cannot set. Document the new one in docs/Configuration.md, or if it is not "
                    f"operator-facing, stop naming it as a bare string literal in non-test code.\n"
                    f"       Current list ({len(added)}): {', '.join(added)}"
                )
                failures += 1
            elif current < baseline:
                fail(
                    f"good news, handled strictly on purpose: only {current} variables are now "
                    f"undocumented, below the baseline of {baseline}. Lower the number in "
                    f"{BASELINE_FILE} to {current} so the ratchet holds the improvement. A baseline "
                    f"left slack is how debt quietly grows back."
                )
                failures += 1

    if failures:
        print(f"knowledge-graph: {failures} check(s) failed", file=sys.stderr)
        return 1

    print(
        "knowledge-graph: current "
        f"({counts['projects']} projects, {counts['declared_facts']} declared facts, "
        f"{counts['operator_vars_absent_from_all_documentation']} undocumented operator variables)"
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())
