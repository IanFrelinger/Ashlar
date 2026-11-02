#!/usr/bin/env python3
"""Deterministic arithmetic over frozen review labels and retained replay artifacts."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import sys
from pathlib import Path
from typing import Any


class InvalidEvaluation(ValueError):
    """A retained evaluation record is incomplete, inconsistent, or undecided."""


def require(condition: Any, message: str) -> None:
    if not condition:
        raise InvalidEvaluation(message)


def unique_object(pairs: list[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        require(key not in result, f"Duplicate JSON key: {key}")
        result[key] = value
    return result


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def load_json(path: Path) -> dict[str, Any]:
    try:
        data = json.loads(path.read_text(encoding="utf-8"), object_pairs_hook=unique_object)
    except (OSError, UnicodeError, json.JSONDecodeError) as exc:
        raise InvalidEvaluation(f"Cannot read JSON {path}: {exc}") from exc
    require(isinstance(data, dict), f"Expected JSON object: {path}")
    return data


def digest(value: Any, context: str) -> str:
    require(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value),
            f"Invalid SHA256 for {context}")
    return value


def index_rows(rows: Any, key: str, context: str) -> dict[str, dict[str, Any]]:
    require(isinstance(rows, list), f"{context} must be a list")
    indexed: dict[str, dict[str, Any]] = {}
    for row in rows:
        require(isinstance(row, dict), f"{context} row must be an object")
        row_id = row.get(key)
        require(isinstance(row_id, str) and bool(row_id.strip()), f"Missing {context} {key}")
        require(row_id not in indexed, f"Duplicate {context} ID: {row_id}")
        indexed[row_id] = row
    return indexed


def require_resolved(record: dict[str, Any], context: str, *, required: bool = False) -> None:
    if required or "status" in record:
        require(record.get("status") == "resolved", f"Unresolved {context}")
    require(record.get("conflicts", []) == [], f"Adjudication conflicts in {context}")
    require(record.get("undecided", False) is False, f"Undecided {context}")
    if "adjudication" in record:
        require(isinstance(record["adjudication"], dict), f"Invalid adjudication in {context}")
        require_resolved(record["adjudication"], f"{context} adjudication", required=True)


def validate_labels(labels: dict[str, Any]) -> dict[str, dict[str, Any]]:
    require(labels.get("schema_version") == 1, "Unsupported ground-truth schema")
    require(isinstance(labels.get("candidate_commit"), str) and
            re.fullmatch(r"[0-9a-f]{40}", labels["candidate_commit"]), "Need full frozen candidate commit")
    digest(labels.get("fixture_manifest_sha256"), "ground-truth fixture manifest")
    process = labels.get("review_process", {})
    for field in ("ai_only", "blinded_to_candidate_outputs", "adjudicated"):
        require(process.get(field) is True, f"Ground-truth review_process.{field} must be true")
    require("adjudication" in labels, "Ground truth lacks adjudication record")
    require_resolved(labels, "ground truth")
    cases = index_rows(labels.get("cases"), "id", "ground-truth cases")
    require(cases, "Ground-truth dataset is empty")
    for case_id, case in cases.items():
        require_resolved(case, f"ground-truth case {case_id}")
        require(case.get("expected_conservative_readiness") in
                ("ready", "blocked", "unknown", "not-applicable"), f"Invalid readiness label: {case_id}")
        require(case.get("expected_scoped_assurance") in
                ("pass", "fail", "unknown", "not-applicable"), f"Invalid assurance label: {case_id}")
        index_rows(case.get("confirmed_blockers"), "id", f"{case_id} confirmed blockers")
        allowed = case.get("legacy_compatibility", {}).get("allowable_verdicts")
        require(isinstance(allowed, list) and all(x in ("ready", "blocked", "unknown") for x in allowed)
                and len(allowed) == len(set(allowed)), f"Invalid allowable legacy verdicts: {case_id}")
    return cases


def artifact_path(row: dict[str, Any], name: str, base: Path, *, required: bool) -> Path | None:
    value = row.get(name + "Path")
    expected = row.get(name + "Sha256")
    if value is None:
        require(not required, f"Missing {name} artifact for {row['caseId']}")
        require(expected is None, f"Hash without {name} artifact for {row['caseId']}")
        return None
    require(isinstance(value, str) and value.strip(), f"Invalid {name} path for {row['caseId']}")
    path = Path(value)
    path = path if path.is_absolute() else base / path
    expected = digest(expected, f"{row['caseId']} {name}")
    require(path.is_file(), f"Missing artifact {path}")
    require(sha256(path) == expected, f"Changed {name} artifact for {row['caseId']}")
    return path


def validate_candidate_binding(labels: dict[str, Any], run: dict[str, Any], *,
                               allow_candidate_rerun: bool = False) -> str:
    baseline, actual = labels["candidate_commit"], run.get("candidateCommit")
    require(isinstance(actual, str) and re.fullmatch(r"[0-9a-f]{40}", actual), "Need full actual run candidate commit")
    if "baselineCandidateCommit" in run:
        require(run["baselineCandidateCommit"] == baseline, "Run baseline differs from frozen labels")
    if actual != baseline:
        require(allow_candidate_rerun, "Run candidate differs from frozen labels; repaired rerun requires --allow-candidate-rerun")
        require(run.get("candidateMode") == "repaired-rerun", "Changed candidate lacks declared repaired-rerun mode")
        require(run.get("baselineCandidateCommit") == baseline, "Repaired rerun lacks matching frozen baseline candidate")
    return actual


def read_run(run: dict[str, Any], base: Path, labels: dict[str, Any], labels_hash: str, *,
             allow_candidate_rerun: bool = False
             ) -> tuple[dict[str, dict[str, Any]], dict[str, dict[str, Any]]]:
    require(run.get("schemaVersion") == "ashlar.release-evidence-evaluation-run/1", "Unsupported run schema")
    validate_candidate_binding(labels, run, allow_candidate_rerun=allow_candidate_rerun)
    require(run.get("fixtureManifestSha256") == labels["fixture_manifest_sha256"], "Run fixture manifest differs from frozen labels")
    require(run.get("groundTruthSha256") == labels_hash, "Run ground-truth hash differs from frozen labels")
    manifest_value = run.get("fixtureManifestPath")
    require(isinstance(manifest_value, str) and manifest_value, "Missing retained fixture manifest path")
    manifest_path = Path(manifest_value)
    manifest_path = manifest_path if manifest_path.is_absolute() else base / manifest_path
    require(manifest_path.is_file() and sha256(manifest_path) == run["fixtureManifestSha256"], "Changed retained fixture manifest")
    fixture_manifest = load_json(manifest_path)
    frozen_cases = index_rows(fixture_manifest.get("cases"), "id", "frozen fixture manifest")
    for baseline_key in ("baselineCommit", "candidate_commit"):
        if baseline_key in fixture_manifest:
            require(fixture_manifest[baseline_key] == labels["candidate_commit"], "Fixture baseline differs from frozen candidate")
    if "groundTruthPath" in run:
        retained_label = Path(run["groundTruthPath"])
        retained_label = retained_label if retained_label.is_absolute() else base / retained_label
        require(retained_label.is_file() and sha256(retained_label) == labels_hash, "Changed retained ground truth")
    cases = index_rows(run.get("cases"), "caseId", "run cases")
    require(set(cases) == set(frozen_cases), "Case IDs differ between frozen fixture manifest and run")
    artifacts: dict[str, dict[str, Any]] = {}
    for case_id, row in cases.items():
        require(row.get("status") in ("answered", "collector-error", "harness-error"), f"Invalid run status: {case_id}")
        require(row.get("exportStatus", "success") in ("success", "error", "not-attempted"), f"Invalid export status: {case_id}")
        answered = row["status"] == "answered"
        require(not answered or row.get("exportStatus") != "not-attempted", f"Answered case has unattempted export: {case_id}")
        if not answered or row.get("exportStatus") == "error":
            require(isinstance(row.get("errorMessage"), str) and row["errorMessage"].strip(), f"Undescribed error: {case_id}")
        dataset = row.get("datasetPath")
        require(isinstance(dataset, str) and dataset, f"Missing dataset path: {case_id}")
        dataset_path = Path(dataset)
        dataset_path = dataset_path if dataset_path.is_absolute() else base / dataset_path
        require(dataset_path.is_file(), f"Missing dataset file: {case_id}")
        require(sha256(dataset_path) == digest(row.get("datasetSha256"), f"{case_id} dataset"), f"Changed dataset: {case_id}")
        require(row["datasetSha256"] == digest(frozen_cases[case_id].get("sha256"), f"{case_id} frozen dataset"),
                f"Dataset differs from pre-output fixture freeze: {case_id}")
        paths = {name: artifact_path(row, name, base, required=answered and row.get("exportStatus") != "error")
                 for name in ("evidence", "markdown", "payload", "transcript")}
        artifact_path(row, "error", base, required=False)
        evidence = load_json(paths["evidence"]) if paths["evidence"] else None
        payload = load_json(paths["payload"]) if paths["payload"] else None
        outputs = payload.get("outputs") if payload else None
        if payload:
            require(isinstance(outputs, dict), f"Missing payload outputs: {case_id}")
            require(isinstance(outputs.get("blockers"), list), f"Missing legacy blockers: {case_id}")
            require(outputs.get("verdict") in ("ready", "blocked", "unknown"), f"Invalid payload verdict: {case_id}")
            require(outputs.get("assurance") in ("pass", "fail", "unknown"), f"Invalid payload assurance: {case_id}")
        if evidence:
            require(evidence.get("verdict") in ("ready", "blocked", "unknown"), f"Invalid actual legacy verdict: {case_id}")
            require(evidence.get("assurance") in ("pass", "fail", "unknown"), f"Invalid actual assurance: {case_id}")
            findings = index_rows(evidence.get("findings"), "id", f"{case_id} evidence findings")
            for finding in findings.values():
                require(finding.get("status") in ("pass", "fail", "unknown"), f"Invalid finding status: {case_id}")
            if outputs:
                require(outputs.get("verdict") == evidence["verdict"], f"Verdict artifact disagreement: {case_id}")
                require(outputs.get("assurance") == evidence["assurance"], f"Assurance artifact disagreement: {case_id}")
        artifacts[case_id] = {"evidence": evidence, "outputs": outputs}
    return cases, artifacts


def review_matches(rows: Any, reported_ids: set[str], reference_ids: set[str], context: str
                   ) -> tuple[set[str], list[str], list[str]]:
    indexed = index_rows(rows, "result_id", context)
    require(set(indexed) == reported_ids,
            f"{context} coverage mismatch: missing={sorted(reported_ids - set(indexed))}, extra={sorted(set(indexed) - reported_ids)}")
    matched: set[str] = set()
    incorrect: list[str] = []
    unassessable: list[str] = []
    for result_id, row in indexed.items():
        require_resolved(row, f"{context} {result_id}")
        classification = row.get("classification")
        require(classification in ("matched", "incorrect", "unassessable"), f"Undecided match in {context}: {result_id}")
        require(isinstance(row.get("rationale"), str) and row["rationale"].strip(), f"Missing match rationale in {context}: {result_id}")
        ids = row.get("ground_truth_ids")
        require(isinstance(ids, list) and all(isinstance(x, str) for x in ids) and len(ids) == len(set(ids)), f"Invalid match IDs in {context}: {result_id}")
        require(set(ids) <= reference_ids, f"Unknown frozen blocker ID in {context}: {result_id}")
        require(bool(ids) == (classification == "matched"), f"Classification/mapping conflict in {context}: {result_id}")
        matched.update(ids)
        if classification == "incorrect":
            incorrect.append(result_id)
        if classification == "unassessable":
            unassessable.append(result_id)
    return matched, sorted(incorrect), sorted(unassessable)


def evaluate(labels: dict[str, Any], run: dict[str, Any], annotations: dict[str, Any],
             artifacts: dict[str, dict[str, Any]], *, allow_candidate_rerun: bool = False) -> dict[str, Any]:
    """Pure counting; file hash and artifact validation are performed by score_files."""
    label_cases = validate_labels(labels)
    actual_commit = validate_candidate_binding(labels, run, allow_candidate_rerun=allow_candidate_rerun)
    run_cases = index_rows(run.get("cases"), "caseId", "run cases")
    annotation_cases = index_rows(annotations.get("cases"), "id", "annotation cases")
    require(set(label_cases) == set(run_cases) == set(annotation_cases) == set(artifacts),
            "Case IDs differ between frozen labels, replay, annotations, or artifacts")
    metrics: dict[str, dict[str, Any]] = {}

    def metric(name: str, numerator: int, denominator: int, unit: str, case_ids: list[str]) -> None:
        metrics[name] = {"numerator": numerator, "denominator": denominator,
                         "rate": numerator / denominator if denominator else None,
                         "unit": unit, "case_ids": sorted(case_ids)}

    cases: list[dict[str, Any]] = []
    for case_id in sorted(label_cases):
        label, row, review, data = label_cases[case_id], run_cases[case_id], annotation_cases[case_id], artifacts[case_id]
        require_resolved(review, f"output review {case_id}", required=True)
        require(review.get("evidence_sha256") == row.get("evidenceSha256"), f"Reviewed evidence hash differs: {case_id}")
        require(review.get("legacy_sha256") == row.get("payloadSha256"), f"Reviewed legacy hash differs: {case_id}")
        evidence, outputs = data["evidence"], data["outputs"]
        verdict = evidence.get("verdict") if evidence else (outputs.get("verdict") if outputs else None)
        assurance = evidence.get("assurance") if evidence else (outputs.get("assurance") if outputs else None)
        confirmed = set(index_rows(label["confirmed_blockers"], "id", f"{case_id} confirmed blockers"))
        reported = {"evidence": {finding["id"] for finding in evidence["findings"] if finding["status"] == "fail"} if evidence else set(),
                    "legacy": {f"blocker:{i}" for i in range(len(outputs["blockers"]))} if outputs else set()}
        details: dict[str, Any] = {}
        for surface in ("evidence", "legacy"):
            matched, incorrect, unassessable = review_matches(review.get(surface + "_matches"), reported[surface], confirmed, f"{case_id} {surface} matches")
            details[surface] = {"reference_blocker_ids": sorted(confirmed), "reported_blocker_ids": sorted(reported[surface]),
                                "matched_reference_ids": sorted(matched), "missed_blocker_ids": sorted(confirmed - matched),
                                "incorrect_result_ids": incorrect, "unassessable_result_ids": unassessable}
        cases.append({"id": case_id, "run_status": row["status"], "export_status": row.get("exportStatus", "success"),
                      "error_type": row.get("errorType"), "error_message": row.get("errorMessage"),
                      "expected_readiness": label["expected_conservative_readiness"], "actual_legacy_verdict": verdict,
                      "expected_assurance": label["expected_scoped_assurance"], "actual_assurance": assurance,
                      "allowable_legacy_verdicts": label["legacy_compatibility"]["allowable_verdicts"], "blockers": details})

    def case_metric(name: str, eligible: Any, failure: Any) -> None:
        population = [case for case in cases if eligible(case)]
        failures = [case["id"] for case in population if failure(case)]
        metric(name, len(failures), len(population), "cases", failures)

    all_cases = lambda c: True
    answered_cases = lambda c: c["run_status"] == "answered"
    case_metric("legacy_false_ready_known_blockers", lambda c: c["expected_readiness"] == "blocked", lambda c: c["actual_legacy_verdict"] == "ready")
    case_metric("legacy_ready_unknown_readiness", lambda c: c["expected_readiness"] == "unknown", lambda c: c["actual_legacy_verdict"] == "ready")
    case_metric("legacy_compatibility_disagreements", lambda c: bool(c["allowable_legacy_verdicts"]),
                lambda c: c["actual_legacy_verdict"] is not None and c["actual_legacy_verdict"] not in c["allowable_legacy_verdicts"])
    for suffix, expected in (("known_fail", ("fail",)), ("unknown", ("unknown",)), ("combined", ("fail", "unknown"))):
        case_metric("assurance_false_pass_" + suffix, lambda c, e=expected: c["expected_assurance"] in e, lambda c: c["actual_assurance"] == "pass")
    for surface, field in (("legacy", "actual_legacy_verdict"), ("assurance", "actual_assurance")):
        case_metric(surface + "_unknown_results", all_cases, lambda c, f=field: c[f] == "unknown")
        case_metric(surface + "_unknown_among_answered", answered_cases, lambda c, f=field: c[f] == "unknown")
    for name, status in (("collection_failures", "collector-error"), ("harness_failures", "harness-error")):
        case_metric(name, all_cases, lambda c, s=status: c["run_status"] == s)
    case_metric("export_failures", all_cases, lambda c: c["export_status"] == "error")
    case_metric("answered_cases", all_cases, answered_cases)
    for surface in ("evidence", "legacy"):
        for suffix, numerator_field, denominator_field in (
                ("missed_blockers", "missed_blocker_ids", "reference_blocker_ids"),
                ("incorrect_blockers", "incorrect_result_ids", "reported_blocker_ids"),
                ("unassessable_blockers", "unassessable_result_ids", "reported_blocker_ids")):
            failing = [case["id"] for case in cases if case["blockers"][surface][numerator_field]]
            numerator = sum(len(case["blockers"][surface][numerator_field]) for case in cases)
            denominator = sum(len(case["blockers"][surface][denominator_field]) for case in cases)
            metric(surface + "_" + suffix, numerator, denominator, "blocker instances", failing)
            case_metric(surface + "_cases_with_" + suffix,
                        lambda c, s=surface, d=denominator_field: bool(c["blockers"][s][d]),
                        lambda c, s=surface, n=numerator_field: bool(c["blockers"][s][n]))
    failure_names = [name for name in metrics if any(token in name for token in
                    ("false_ready", "ready_unknown", "disagreements", "false_pass", "failures", "missed_blockers", "incorrect_blockers", "unassessable_blockers"))]
    for case in cases:
        case["disclosures"] = [name for name in sorted(failure_names) if case["id"] in metrics[name]["case_ids"]]
    return {"schema_version": 1, "candidate_commit": actual_commit,
            "baseline_candidate_commit": labels["candidate_commit"],
            "candidate_mode": run.get("candidateMode", "frozen-baseline"), "case_count": len(cases),
            "review_scope": "AI-only independent review; no human, customer, productivity, or release-approval claim",
            "metrics": dict(sorted(metrics.items())), "cases": cases,
            "failure_case_ids": [case["id"] for case in cases if case["disclosures"]],
            "limitations": ["A small fixed sample does not establish general accuracy.",
                            "Expected policy and blockers come only from frozen independent review labels.",
                            "Artifact hashes establish retained-record consistency, not proof of reviewer blinding chronology.",
                            "Legacy readiness and scoped assurance are separate metrics; collector errors remain errors."]}


def score_files(labels_path: Path, run_path: Path, annotations_path: Path, *,
                allow_candidate_rerun: bool = False) -> dict[str, Any]:
    labels, run, annotations = load_json(labels_path), load_json(run_path), load_json(annotations_path)
    validate_labels(labels)
    actual_commit = validate_candidate_binding(labels, run, allow_candidate_rerun=allow_candidate_rerun)
    labels_hash, run_hash = sha256(labels_path), sha256(run_path)
    require(annotations.get("schema_version") == 1, "Unsupported annotation schema")
    require(annotations.get("candidate_commit") == actual_commit, "Annotation candidate differs from actual run candidate")
    require(annotations.get("ground_truth_sha256") == labels_hash, "Annotation ground-truth hash differs from frozen labels")
    require(annotations.get("run_manifest_sha256") == run_hash, "Annotation run-manifest hash differs")
    for field in ("ai_only", "performed_after_outputs", "ground_truth_unchanged"):
        require(annotations.get("review", {}).get(field) is True, f"Annotation review.{field} must be true")
    require(isinstance(annotations.get("review", {}).get("reviewer"), str) and annotations["review"]["reviewer"].strip(), "Missing output reviewer identifier")
    require_resolved(annotations, "annotations")
    _, artifacts = read_run(run, run_path.parent, labels, labels_hash, allow_candidate_rerun=allow_candidate_rerun)
    report = evaluate(labels, run, annotations, artifacts, allow_candidate_rerun=allow_candidate_rerun)
    report["inputs"] = {"ground_truth_sha256": labels_hash, "run_manifest_sha256": run_hash,
                        "annotations_sha256": sha256(annotations_path),
                        "fixture_manifest_sha256": labels["fixture_manifest_sha256"]}
    return report


def markdown(report: dict[str, Any]) -> str:
    lines = ["# Release-evidence evaluation score", "", f"Actual candidate: `{report['candidate_commit']}`. "
             f"Frozen label baseline: `{report['baseline_candidate_commit']}`. Mode: `{report['candidate_mode']}`. "
             f"Cases: **{report['case_count']}**.", "",
             report["review_scope"] + ".", "", "| Metric | Count / denominator | Rate |", "|---|---:|---:|"]
    for name, result in report["metrics"].items():
        rate = "n/a" if result["rate"] is None else f"{100 * result['rate']:.2f}%"
        lines.append(f"| {name} | {result['numerator']} / {result['denominator']} {result['unit']} | {rate} |")
    lines += ["", "## Every case and disclosed disagreement", ""]
    for case in report["cases"]:
        lines += [f"### {case['id']}", "", f"Run: `{case['run_status']}`; export: `{case['export_status']}`. "
                  f"Legacy: `{case['actual_legacy_verdict']}` (readiness label `{case['expected_readiness']}`); "
                  f"assurance: `{case['actual_assurance']}` (label `{case['expected_assurance']}`).", ""]
        if case["error_message"]:
            lines += ["Runner error:", "", "    " + json.dumps(case["error_message"], ensure_ascii=True), ""]
        lines += ["Disclosures: " + (", ".join(f"`{name}`" for name in case["disclosures"]) or "none under the preregistered metrics") + ".", ""]
        for surface, details in case["blockers"].items():
            lines += [f"- {surface}: missed {json.dumps(details['missed_blocker_ids'])}; "
                      f"incorrect {json.dumps(details['incorrect_result_ids'])}; "
                      f"unassessable {json.dumps(details['unassessable_result_ids'])}."]
        lines.append("")
    lines += ["## Retained input hashes", ""]
    for key, value in report.get("inputs", {}).items():
        lines.append(f"- {key}: `{value}`")
    lines += ["", "## Limits", ""] + ["- " + limit for limit in report["limitations"]] + [""]
    return "\n".join(lines)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--ground-truth", required=True, type=Path)
    parser.add_argument("--run-manifest", required=True, type=Path)
    parser.add_argument("--annotations", required=True, type=Path)
    parser.add_argument("--allow-candidate-rerun", action="store_true",
                        help="Allow an explicitly declared repaired-rerun against the unchanged frozen baseline labels")
    parser.add_argument("--json-output", required=True, type=Path)
    parser.add_argument("--markdown-output", required=True, type=Path)
    args = parser.parse_args()
    try:
        for destination in (args.json_output, args.markdown_output):
            require(not destination.exists(), f"Refusing to overwrite retained report: {destination}")
            require(destination.parent.is_dir(), f"Output directory does not exist: {destination.parent}")
        require(args.json_output.resolve() != args.markdown_output.resolve(), "JSON and Markdown paths must differ")
        report = score_files(args.ground_truth, args.run_manifest, args.annotations,
                             allow_candidate_rerun=args.allow_candidate_rerun)
        serialized = json.dumps(report, indent=2, sort_keys=True, ensure_ascii=True) + "\n"
        rendered = markdown(report)
        with args.json_output.open("x", encoding="utf-8") as handle:
            handle.write(serialized)
        with args.markdown_output.open("x", encoding="utf-8") as handle:
            handle.write(rendered)
        print(f"Scored {report['case_count']} cases; disclosed cases: {len(report['failure_case_ids'])}")
        return 0
    except (InvalidEvaluation, OSError) as exc:
        print(f"Invalid evaluation: {exc}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    raise SystemExit(main())
