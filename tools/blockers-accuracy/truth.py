"""Ground truth: the written rubric (docs/benchmarks/blockers-accuracy.md, "Rubric") as code.

Input is the GraphQL snapshot capture.py recorded. Output is a verdict and a set of blocker keys:
  draft, conflict, behind, changes_requested, review_required,
  check_failing:<name>, check_pending:<name>, other
Check names are compared case-insensitively. GitHub's own fields decide everything; nothing is inferred.

A required check is one GraphQL marks isRequired, or one named in the base branch's public protection summary
(REST /branches/{base}, protection.required_status_checks) or in a required_status_checks rule of the rulesets
that apply to it (REST /rules/branches/{base}). A required check with no run or status on the head commit is
"check_pending:<name>": GraphQL's rollup lists only checks that reported, so it cannot show this case.
"""
from __future__ import annotations

import json

FAILED_CONCLUSIONS = {"FAILURE", "TIMED_OUT", "CANCELLED", "ACTION_REQUIRED", "STARTUP_FAILURE", "STALE"}
FAILED_STATES = {"FAILURE", "ERROR"}
PENDING_STATES = {"PENDING", "EXPECTED"}
MERGE_ALLOWED = {"CLEAN", "UNSTABLE", "HAS_HOOKS"}

# Blockers neither arm can see without admin rights or GraphQL: reported separately.
REST_INVISIBLE = {"review_required"}


def contexts(g: dict) -> list[dict]:
    nodes = (g.get("commits") or {}).get("nodes") or []
    if not nodes:
        return []
    rollup = nodes[0]["commit"].get("statusCheckRollup") or {}
    return ((rollup.get("contexts") or {}).get("nodes")) or []


def rest_required_checks(branch_body: str | None, rules_body: str | None) -> set[str]:
    names: set[str] = set()
    try:
        prot = (json.loads(branch_body or "{}") or {}).get("protection") or {}
        rsc = prot.get("required_status_checks") or {}
        names |= {c.lower() for c in rsc.get("contexts") or []}
        names |= {c["context"].lower() for c in rsc.get("checks") or []}
    except (json.JSONDecodeError, AttributeError, TypeError):
        pass
    try:
        rules = json.loads(rules_body or "[]")
        for r in rules if isinstance(rules, list) else []:
            if r.get("type") == "required_status_checks":
                for c in (r.get("parameters") or {}).get("required_status_checks") or []:
                    names.add(c["context"].lower())
    except (json.JSONDecodeError, AttributeError, TypeError):
        pass
    return names


def check_facts(g: dict) -> dict[str, dict]:
    """name (lower-case) -> {failing, pending, required} over every check run and commit status."""
    facts: dict[str, dict] = {}
    for c in contexts(g):
        if c["__typename"] == "CheckRun":
            name = c["name"]
            pending = c["status"] != "COMPLETED"
            failing = not pending and (c.get("conclusion") or "") in FAILED_CONCLUSIONS
        else:
            name = c["context"]
            pending = c["state"] in PENDING_STATES
            failing = c["state"] in FAILED_STATES
        f = facts.setdefault(name.lower(), {"name": name, "failing": False, "pending": False, "required": False})
        f["failing"] |= failing
        f["pending"] |= pending
        f["required"] |= bool(c.get("isRequired"))
    return facts


def ground_truth(g: dict, rest_required: set[str] = frozenset()) -> tuple[str, set[str]]:
    if g["merged"] or g["state"] == "MERGED":
        return "merged", set()
    if g["closed"] or g["state"] == "CLOSED":
        return "closed", set()

    blockers: set[str] = set()
    if g["isDraft"]:
        blockers.add("draft")
    if g["mergeable"] == "CONFLICTING":
        blockers.add("conflict")
    if g["mergeStateStatus"] == "BEHIND":
        blockers.add("behind")
    if g["reviewDecision"] == "CHANGES_REQUESTED":
        blockers.add("changes_requested")
    if g["reviewDecision"] == "REVIEW_REQUIRED":
        blockers.add("review_required")
    facts = check_facts(g)
    for key, f in facts.items():
        required = f["required"] or key in rest_required
        if required and f["failing"]:
            blockers.add(f"check_failing:{key}")
        elif required and f["pending"]:
            blockers.add(f"check_pending:{key}")
    for key in rest_required - facts.keys():
        blockers.add(f"check_pending:{key}")
    if not blockers and g["mergeStateStatus"] == "BLOCKED":
        blockers.add("other")

    if blockers:
        return "blocked", blockers
    if g["mergeStateStatus"] in MERGE_ALLOWED:
        return "ready", blockers
    return "unknown", blockers


def classify_claim(g: dict, claim: str, truth: set[str]) -> str:
    """A claimed blocker that is not in the truth set: 'overcall' if the snapshot shows the fact but the
    rubric says it does not block; 'fabricated' if the snapshot contradicts the fact."""
    kind, _, name = claim.partition(":")
    facts = check_facts(g)
    if kind in ("check_failing", "check_pending"):
        f = facts.get(name)
        if f is None:
            return "fabricated"
        if kind == "check_failing":
            return "overcall" if f["failing"] else "fabricated"
        return "overcall" if f["pending"] else "fabricated"
    if kind == "draft":
        return "overcall" if g["isDraft"] else "fabricated"
    if kind == "conflict":
        return "overcall" if g["mergeable"] in ("CONFLICTING", "UNKNOWN") else "fabricated"
    if kind == "behind":
        # REST and GraphQL report "behind" only when it blocks, so a branch that is merely behind is unprovable.
        return "unsupported"
    if kind == "changes_requested":
        reviews = ((g.get("latestOpinionatedReviews") or {}).get("nodes")) or []
        return "overcall" if any(r["state"] == "CHANGES_REQUESTED" for r in reviews) else "fabricated"
    if kind == "review_required":
        if g["reviewDecision"] == "APPROVED":
            return "fabricated"
        return "unsupported" if g["reviewDecision"] is None else "overcall"
    if kind == "other":
        return "overcall" if g["mergeStateStatus"] == "BLOCKED" else "fabricated"
    return "unsupported"
