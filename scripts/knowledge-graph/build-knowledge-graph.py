#!/usr/bin/env python3
"""
Derive docs/knowledge-graph.json (and its readable view docs/knowledge-graph.md) FROM THE REPOSITORY.

WHY THIS EXISTS, and why it is a generator rather than a document
----------------------------------------------------------------
A hand-maintained map of a repository is a liability: it is believed long after it stops being true.
Everything here is extracted from the tree on every run, and
scripts/knowledge-graph/verify-knowledge-graph-current.py fails a required check when the committed
artifact and the tree disagree. That is the whole mechanism: the graph cannot go stale quietly,
because going stale is a red gate.

WHAT IS DELIBERATELY NOT IN IT
------------------------------
* SPEC-006 rules (S-1..S-7). They are not structured headings, so extracting them would be a guess,
  and a graph with plausible-but-wrong edges is worse than no graph in a repository whose whole
  subject is evidence.
* The residual -> fact -> spec binding. That is ALREADY enforced, two-directionally and better than
  a graph could, by GateSignatureResidualTests
  (`SPEC_006_names_every_residual_this_file_demonstrates`,
  `Every_residual_row_names_a_fact_on_this_class_or_says_why_it_cannot`, `No_residual_fact_is_skipped`).
  Re-deriving it here would create a second source of truth that can disagree with the authoritative
  one, which is the exact anti-pattern this file exists to avoid.
* Which checks are REQUIRED on master. That lives in branch protection and cannot be read offline, so
  claiming it here would be a guess presented as data. Workflow names and triggers are extracted;
  "required" is not.

DETERMINISM IS A CORRECTNESS PROPERTY HERE
------------------------------------------
The verifier compares a regeneration against the committed file, so any nondeterminism turns the gate
into a coin flip that everyone learns to rerun. No timestamps, no absolute paths, no set iteration
order: every collection is sorted before it is written.

Run:  python3 scripts/knowledge-graph/build-knowledge-graph.py
      python3 scripts/knowledge-graph/build-knowledge-graph.py --stdout   # print, write nothing
"""
from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

SCHEMA_VERSION = 1

# A variable is "operator-facing" if code names it as a string literal. That is deliberately broader
# than "documented": the gap between the two is the interesting number.
ASHLAR_VAR = re.compile(r'"(ASHLAR_[A-Z0-9_]+)"')
PROJECT_REFERENCE = re.compile(r'<ProjectReference\s+Include="([^"]+)"', re.IGNORECASE)
IS_PACKABLE_TRUE = re.compile(r"<IsPackable>\s*true\s*</IsPackable>", re.IGNORECASE)
TARGET_FRAMEWORKS = re.compile(r"<TargetFrameworks?>([^<]+)</TargetFrameworks?>", re.IGNORECASE)
NAMESPACE = re.compile(r"^\s*namespace\s+([A-Za-z0-9_.]+)\s*;", re.MULTILINE)
XUNIT_FACT = re.compile(r"^\s*\[(Fact|Theory)[\(\]]", re.MULTILINE)
CERT_GATE_FILTER = re.compile(r"CERT_GATE_FILTER='([^']*)'")
FQN_CLAUSE = re.compile(r"FullyQualifiedName~([A-Za-z0-9_.]+)")
WORKFLOW_NAME = re.compile(r"^name:\s*(.+?)\s*$", re.MULTILINE)


def repo_root() -> Path:
    return Path(__file__).resolve().parent.parent.parent


def read(path: Path) -> str:
    # errors="replace" so one oddly-encoded file cannot silently drop a whole scan; the floors in the
    # verifier would catch a collapse, but losing ONE file quietly is the subtler failure.
    return path.read_text(encoding="utf-8", errors="replace")


def rel(root: Path, path: Path) -> str:
    return path.relative_to(root).as_posix()


def cert_gate_namespaces(root: Path) -> list[str]:
    """The namespace prefixes cert-gate selects, read from its single source of truth.

    Hardcoding these would put a second copy of the filter in the tree, which is the thing that let
    the filter and its documentation drift apart before.
    """
    config = root / "scripts/cert-gate-config.sh"
    if not config.is_file():
        return []
    match = CERT_GATE_FILTER.search(read(config))
    if not match:
        return []
    return sorted(set(FQN_CLAUSE.findall(match.group(1))))


def collect_projects(root: Path) -> dict[str, dict]:
    projects: dict[str, dict] = {}
    for csproj in sorted(root.glob("src/**/*.csproj")) + sorted(root.glob("application/**/*.csproj")):
        text = read(csproj)
        path = rel(root, csproj)
        name = csproj.stem
        tfms = TARGET_FRAMEWORKS.search(text)
        refs = []
        for include in PROJECT_REFERENCE.findall(text):
            refs.append(Path(include.replace("\\", "/")).stem)
        projects[name] = {
            "name": name,
            "path": path,
            "layer": "application" if path.startswith("application/") else "src",
            "packable": bool(IS_PACKABLE_TRUE.search(text)),
            "target_frameworks": sorted(
                f.strip() for f in (tfms.group(1).split(";") if tfms else []) if f.strip()
            ),
            "references": sorted(set(refs)),
        }
    return projects


def owning_project(root: Path, file_path: Path, projects: dict[str, dict]) -> str | None:
    """The nearest ancestor directory that holds a .csproj. Nearest, not first-matching, because
    nested projects exist (e.g. a test project's own scripts/ helper project)."""
    by_dir = {(root / p["path"]).parent: p["name"] for p in projects.values()}
    for parent in file_path.parents:
        if parent in by_dir:
            return by_dir[parent]
        if parent == root:
            break
    return None


# This file's own output names every variable it reports as undocumented, so including it in the
# documentation corpus would make each one "documented" by virtue of being listed as undocumented.
# That is not hypothetical: the first measurement of this reported 65/65 documented for exactly that
# reason. Any future "does the tree mention X" scan added here must exclude these two paths.
GENERATED_ARTIFACTS = frozenset({"docs/knowledge-graph.json", "docs/knowledge-graph.md"})


def collect_config_variables(root: Path, projects: dict[str, dict]) -> list[dict]:
    doc = root / "docs/Configuration.md"
    in_reference = set(re.findall(r"ASHLAR_[A-Z0-9_]+", read(doc))) if doc.is_file() else set()

    anywhere: set[str] = set()
    for md in sorted((root / "docs").rglob("*.md")):
        if rel(root, md) in GENERATED_ARTIFACTS:
            continue
        anywhere.update(re.findall(r"ASHLAR_[A-Z0-9_]+", read(md)))

    found: dict[str, set[str]] = {}
    for source in sorted(root.glob("src/**/*.cs")) + sorted(root.glob("application/**/*.cs")):
        if "/obj/" in source.as_posix() or "/bin/" in source.as_posix():
            continue
        names = set(ASHLAR_VAR.findall(read(source)))
        if not names:
            continue
        owner = owning_project(root, source, projects) or "(unowned)"
        for name in names:
            found.setdefault(name, set()).add(owner)

    out = []
    for name in sorted(found):
        owners = sorted(found[name])
        out.append({
            "name": name,
            "declared_in": owners,
            # Read by a TEST project only: not an operator-facing knob, and counting it as
            # undocumented would inflate the gap with things no operator should ever set.
            "test_only": all(".Tests" in o or o.startswith("Ashlar.Tests") for o in owners),
            # Two distinct facts, kept distinct because conflating them overstates the problem.
            # "Not in the central reference" is a discoverability gap; "in no document at all" is
            # the sharper one. 19 of the first group are described somewhere else.
            "in_configuration_reference": name in in_reference,
            "in_any_document": name in anywhere,
        })
    return out


def collect_test_facts(root: Path, projects: dict[str, dict], gate_namespaces: list[str]) -> list[dict]:
    out = []
    for source in sorted(root.glob("src/**/*.cs")) + sorted(root.glob("application/**/*.cs")):
        posix = source.as_posix()
        if "/obj/" in posix or "/bin/" in posix:
            continue
        text = read(source)
        count = len(XUNIT_FACT.findall(text))
        if count == 0:
            continue
        ns_match = NAMESPACE.search(text)
        namespace = ns_match.group(1) if ns_match else ""
        out.append({
            "file": rel(root, source),
            "project": owning_project(root, source, projects) or "(unowned)",
            "namespace": namespace,
            "declared_facts": count,
            # "cert-gate selects this namespace", NOT "this is merge-blocking" — whether cert-gate is
            # required lives in branch protection, which cannot be read here.
            "in_cert_gate_filter": any(namespace.startswith(ns) for ns in gate_namespaces),
        })
    return out


def collect_workflows(root: Path) -> list[dict]:
    out = []
    for wf in sorted((root / ".github/workflows").glob("*.yml")):
        text = read(wf)
        name_match = WORKFLOW_NAME.search(text)
        triggers = sorted({
            t for t in ("pull_request", "push", "schedule", "workflow_dispatch", "workflow_call", "release")
            if re.search(rf"^\s{{2,}}{t}:", text, re.MULTILINE)
        })
        out.append({
            "file": rel(root, wf),
            "name": (name_match.group(1).strip().strip("\"'") if name_match else wf.stem),
            "triggers": triggers,
        })
    return out


def build(root: Path) -> dict:
    gate_namespaces = cert_gate_namespaces(root)
    projects = collect_projects(root)
    config_variables = collect_config_variables(root, projects)
    test_facts = collect_test_facts(root, projects, gate_namespaces)
    workflows = collect_workflows(root)

    operator_vars = [v for v in config_variables if not v["test_only"]]
    not_in_reference = sorted(v["name"] for v in operator_vars if not v["in_configuration_reference"])
    undocumented_anywhere = sorted(v["name"] for v in operator_vars if not v["in_any_document"])

    return {
        "schema_version": SCHEMA_VERSION,
        "generated_by": "scripts/knowledge-graph/build-knowledge-graph.py",
        "note": (
            "Derived from the tree on every run and gated by "
            "scripts/knowledge-graph/verify-knowledge-graph-current.py. Do not hand-edit: a manual "
            "change is reverted by the next regeneration and reported as drift by the gate."
        ),
        "cert_gate_selected_namespaces": gate_namespaces,
        "counts": {
            "projects": len(projects),
            "packable_projects": sum(1 for p in projects.values() if p["packable"]),
            "config_variables": len(config_variables),
            "operator_facing_config_variables": len(operator_vars),
            "operator_vars_missing_from_configuration_reference": len(not_in_reference),
            "operator_vars_absent_from_all_documentation": len(undocumented_anywhere),
            "test_files_declaring_facts": len(test_facts),
            "declared_facts": sum(f["declared_facts"] for f in test_facts),
            "workflows": len(workflows),
        },
        # Named rather than buried in a count, because the gap is the actionable part: an operator
        # cannot set a knob they cannot discover, and #661 shipped a variable the docs described
        # under a name nothing read. The two lists are different problems and are kept apart: the
        # first is a discoverability gap in the central reference, the second is a knob no document
        # mentions at all.
        "operator_vars_missing_from_configuration_reference": not_in_reference,
        "operator_vars_absent_from_all_documentation": undocumented_anywhere,
        "projects": [projects[k] for k in sorted(projects)],
        "config_variables": config_variables,
        "test_files": test_facts,
        "workflows": workflows,
    }


def render_markdown(graph: dict) -> str:
    c = graph["counts"]
    lines = [
        "# Knowledge graph",
        "",
        "<!-- GENERATED FILE. Do not edit by hand.",
        "     Source: scripts/knowledge-graph/build-knowledge-graph.py",
        "     Gated by: scripts/knowledge-graph/verify-knowledge-graph-current.py -->",
        "",
        "Derived from the tree, regenerated on demand, and gated: if this file and the repository",
        "disagree, a required check fails. It is not a description of the repository that someone",
        "remembered to update — staleness here is a red build.",
        "",
        "## Shape",
        "",
        "| | count |",
        "|---|---|",
        f"| projects | {c['projects']} |",
        f"| of those, packable (they ship to consumers) | {c['packable_projects']} |",
        f"| `ASHLAR_*` variables named in code | {c['config_variables']} |",
        f"| of those, operator-facing (not test-only) | {c['operator_facing_config_variables']} |",
        f"| missing from `docs/Configuration.md` | {c['operator_vars_missing_from_configuration_reference']} |",
        f"| **mentioned in no document at all** | **{c['operator_vars_absent_from_all_documentation']}** |",
        f"| test files declaring xUnit facts | {c['test_files_declaring_facts']} |",
        f"| declared facts | {c['declared_facts']} |",
        f"| workflows | {c['workflows']} |",
        "",
        "## Namespaces cert-gate selects",
        "",
        "Read from `scripts/cert-gate-config.sh`, so this cannot drift from the filter itself.",
        "",
    ]
    for ns in graph["cert_gate_selected_namespaces"]:
        lines.append(f"- `{ns}`")
    lines += [
        "",
        "## Operator-facing variables no document mentions",
        "",
        "An operator cannot set a knob they cannot discover, and the inverse already bit once: a",
        "variable was documented under a name nothing read, so following the docs left pinning off.",
        "This list is data rather than a failure — the gate refuses *growth*, not the existing debt.",
        "",
        "Kept separate from \"missing from `docs/Configuration.md`\" on purpose: a variable described in",
        "some other document is a discoverability problem, not an undocumented one, and conflating the",
        "two overstates the gap by the difference between the counts above.",
        "",
    ]
    if graph["operator_vars_absent_from_all_documentation"]:
        for name in graph["operator_vars_absent_from_all_documentation"]:
            lines.append(f"- `{name}`")
    else:
        lines.append("_None._")
    lines += [
        "",
        "## Packable projects",
        "",
        "These ship to people building **on** Ashlar, so their public surface is a compatibility",
        "commitment and ambient process configuration reaching them is a design decision, not a",
        "convenience.",
        "",
    ]
    for p in graph["projects"]:
        if p["packable"]:
            lines.append(f"- `{p['name']}` ({p['path']})")
    lines.append("")
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser()
    parser.add_argument("--stdout", action="store_true", help="print the JSON and write nothing")
    args = parser.parse_args()

    root = repo_root()
    graph = build(root)
    payload = json.dumps(graph, indent=2, sort_keys=False, ensure_ascii=False) + "\n"

    if args.stdout:
        sys.stdout.write(payload)
        return 0

    (root / "docs/knowledge-graph.json").write_text(payload, encoding="utf-8", newline="\n")
    (root / "docs/knowledge-graph.md").write_text(render_markdown(graph), encoding="utf-8", newline="\n")
    print(f"knowledge-graph: wrote docs/knowledge-graph.json and docs/knowledge-graph.md")
    for key, value in graph["counts"].items():
        print(f"  {key}: {value}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
