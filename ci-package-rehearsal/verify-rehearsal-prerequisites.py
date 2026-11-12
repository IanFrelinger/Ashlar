#!/usr/bin/env python3
"""Fail-closed gate over retained reconciliation plus completed semantic reviews."""
import argparse
import hashlib
import importlib.util
import json
from pathlib import Path
import sys

SHA = "792446d09b59f98b47e08279907cb3d4df314caa"


def require(condition, message):
    if not condition:
        raise ValueError(message)


def check(repo, reconciliation_path, reviews_path):
    spec = importlib.util.spec_from_file_location(
        "campaign_evidence", Path(__file__).with_name("campaign_evidence.py"))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    plan = module.make_plan(repo, SHA, 20)
    require((plan["manifest_count"], plan["case_count"]) == (8, 295),
            "Pinned source inventory does not match audited campaign")
    raw = reconciliation_path.read_bytes()
    reconciliation = json.loads(raw)
    semantic = json.loads(reviews_path.read_bytes())
    require(reconciliation["schema_version"] == 1, "Unsupported reconciliation schema")
    for key, value in (
        ("source_sha", SHA), ("inventory_sha256", plan["inventory_sha256"]),
        ("manifest_count", 8), ("expected_cases", 295), ("verified_cases", 295),
    ):
        require(reconciliation[key] == value, "Reconciliation mismatch: " + key)
    require(reconciliation["all_killed"] is True, "Campaign is not all KILLED")
    require(reconciliation["issues"] == [], "Reconciliation has unresolved issues")
    records = reconciliation["records"]
    inventory_keys = [c["manifest"] + "::" + c["id"] for c in plan["inventory"]]
    require([r["manifest"] + "::" + r["id"] for r in records] == inventory_keys,
            "Reconciled rows are not the exact ordered source inventory")
    manifests = {m["path"]: m["document"] for m in plan["manifests"]}
    source_cases = {
        path + "::" + case["id"]: case
        for path, manifest in manifests.items() for case in manifest["mutations"]
    }
    required = [key for key in inventory_keys if not source_cases[key].get("expected_failures")]
    require(len(required) == 65, "Audited source does not have exactly 65 semantic reviews")
    require(reconciliation["failure_relevance_review"] == required,
            "Required semantic review population differs from pinned source")
    records_by_key = {}
    for row in records:
        key = row["manifest"] + "::" + row["id"]
        require(row["verified"] is True and row["errors"] == [], "Unverified case: " + key)
        require(row["expected_failures"] == source_cases[key].get("expected_failures", []),
                "Expected failure contract mismatch: " + key)
        require(row["framework"] == source_cases[key]["framework"], "Framework mismatch: " + key)
        failures = row["observed_failure_names"]
        require(isinstance(failures, list) and failures and
                all(isinstance(n, str) and n.strip() for n in failures),
                "Missing actual failure names: " + key)
        require(row["red"]["failed"] > 0 and row["red"]["exit"] == 1,
                "Missing actual red assertion failure: " + key)
        require(row["green"]["failed"] == 0 and row["green"]["exit"] == 0,
                "Restored green failed: " + key)
        for expected in row["expected_failures"]:
            require(any(expected in failure for failure in failures),
                    "Intended failure missing: " + key)
        records_by_key[key] = row

    reconciliation_digest = hashlib.sha256(raw).hexdigest()
    require(semantic["schema_version"] == 1, "Unsupported semantic review schema")
    require(semantic["source_sha"] == SHA, "Semantic review source SHA mismatch")
    require(semantic["inventory_sha256"] == plan["inventory_sha256"],
            "Semantic review inventory mismatch")
    require(semantic["reconciliation_sha256"] == reconciliation_digest,
            "Semantic reviews are not bound to these exact reconciliation bytes")
    reviews = semantic["reviews"]
    require(isinstance(reviews, list) and len(reviews) == 65,
            "Exactly 65 completed semantic reviews required")
    review_keys = [r["manifest"] + "::" + r["id"] for r in reviews]
    require(len(set(review_keys)) == 65 and set(review_keys) == set(required),
            "Missing, extra, or duplicate semantic review case")
    for review in reviews:
        key = review["manifest"] + "::" + review["id"]
        require(review["accepted"] is True, "Semantic review not accepted: " + key)
        for field in ("reviewer", "rationale"):
            require(isinstance(review[field], str) and review[field].strip(),
                    "Missing semantic review " + field + ": " + key)
        require(review["evidence_kind"] in ("runtime-assertion", "convention-tripwire"),
                "Invalid semantic evidence kind: " + key)
        names = review["observed_failure_names"]
        require(isinstance(names, list) and names and
                all(isinstance(n, str) and n.strip() for n in names) and
                len(set(names)) == len(names),
                "Missing or duplicate reviewed failure names: " + key)
        require(set(names) <= set(records_by_key[key]["observed_failure_names"]),
                "Review cites a failure absent from actual red evidence: " + key)
    return {
        "schema_version": 1, "source_sha": SHA,
        "inventory_sha256": plan["inventory_sha256"],
        "reconciliation_sha256": reconciliation_digest,
        "semantic_reviews_sha256": hashlib.sha256(reviews_path.read_bytes()).hexdigest(),
        "verified_cases": 295, "manifest_count": 8, "completed_semantic_reviews": 65,
        "ready_for_package_rehearsal": True,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-repo", type=Path, required=True)
    parser.add_argument("--reconciliation", type=Path, required=True)
    parser.add_argument("--semantic-reviews", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    require(not args.output.exists(), "Preserve existing gate receipt; output already exists")
    result = check(args.source_repo, args.reconciliation, args.semantic_reviews)
    args.output.write_text(json.dumps(result, indent=2) + "\n")
    print("PACKAGE_REHEARSAL_PREREQUISITES=OK (295/295; 65/65 semantic reviews)")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except (OSError, ValueError, KeyError, TypeError, StopIteration) as error:
        print("package-rehearsal-prerequisites: " + str(error), file=sys.stderr)
        raise SystemExit(2)
