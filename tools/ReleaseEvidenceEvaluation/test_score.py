#!/usr/bin/env python3
"""Small artificial counterexamples; never use the candidate or production rubric."""

import copy
import json
import tempfile
import unittest
from pathlib import Path

import score


COMMIT = "1" * 40
MANIFEST_HASH = "2" * 64


def small_study():
    # Six independently specified examples, with arithmetic checked in the tests.
    labels = {
        "schema_version": 1, "candidate_commit": COMMIT,
        "fixture_manifest_sha256": MANIFEST_HASH,
        "review_process": {"ai_only": True, "blinded_to_candidate_outputs": True, "adjudicated": True},
        "adjudication": {"status": "resolved", "conflicts": []}, "cases": [],
    }
    run = {"schemaVersion": "ashlar.release-evidence-evaluation-run/1", "candidateCommit": COMMIT,
           "fixtureManifestSha256": MANIFEST_HASH, "cases": []}
    annotations = {"schema_version": 1, "candidate_commit": COMMIT,
                   "review": {"ai_only": True, "performed_after_outputs": True,
                              "ground_truth_unchanged": True, "reviewer": "artificial-test-review"}, "cases": []}
    artifacts = {}
    table = [
        # ID, expected readiness, expected assurance, actual verdict, actual assurance, confirmed blockers
        ("unsafe-ready", "blocked", "fail", "ready", "pass", ["required-job"]),
        ("unsupported-ready", "unknown", "unknown", "ready", "pass", []),
        ("unknown-output", "ready", "pass", "unknown", "unknown", []),
        ("collection-error", "blocked", "fail", None, None, ["merge-conflict"]),
        ("detected-blocker", "blocked", "fail", "blocked", "fail", ["review-required"]),
        ("correct-ready", "ready", "pass", "ready", "pass", []),
    ]
    for case_id, readiness, expected_assurance, verdict, assurance, blockers in table:
        labels["cases"].append({"id": case_id, "expected_conservative_readiness": readiness,
                                "expected_scoped_assurance": expected_assurance,
                                "confirmed_blockers": [{"id": blocker} for blocker in blockers],
                                "legacy_compatibility": {"allowable_verdicts": [readiness] if readiness != "unknown" else ["unknown"]}})
        run["cases"].append({"caseId": case_id, "status": "answered" if verdict else "collector-error",
                             **({"errorType": "Timeout", "errorMessage": "Artificial timeout"} if not verdict else {})})
        evidence, outputs = None, None
        evidence_matches, legacy_matches = [], []
        if verdict:
            findings = []
            legacy_blockers = []
            if case_id == "unknown-output":
                # A disproven blocker and an explicitly unassessable one remain separate.
                findings = [{"id": "invented", "status": "fail"}, {"id": "unassessable", "status": "fail"}]
                legacy_blockers = [{"reason": "Invented restriction"}]
                evidence_matches = [
                    {"result_id": "invented", "classification": "incorrect", "ground_truth_ids": [], "rationale": "No such restriction in the independent artificial policy."},
                    {"result_id": "unassessable", "classification": "unassessable", "ground_truth_ids": [], "rationale": "This artificial claim cannot be mapped to the specified policy."},
                ]
                legacy_matches = [{"result_id": "blocker:0", "classification": "incorrect", "ground_truth_ids": [], "rationale": "No such restriction in the independent artificial policy."}]
            if case_id == "detected-blocker":
                findings = [{"id": "review", "status": "fail"}]
                legacy_blockers = [{"reason": "Review is required"}]
                evidence_matches = [{"result_id": "review", "classification": "matched", "ground_truth_ids": ["review-required"], "rationale": "The stated required review is absent."}]
                legacy_matches = [{"result_id": "blocker:0", "classification": "matched", "ground_truth_ids": ["review-required"], "rationale": "The stated required review is absent."}]
            evidence = {"verdict": verdict, "assurance": assurance, "findings": findings}
            outputs = {"verdict": verdict, "assurance": assurance, "blockers": legacy_blockers}
        artifacts[case_id] = {"evidence": evidence, "outputs": outputs}
        annotations["cases"].append({"id": case_id, "status": "resolved", "evidence_sha256": None,
                                      "legacy_sha256": None, "evidence_matches": evidence_matches,
                                      "legacy_matches": legacy_matches})
    return labels, run, annotations, artifacts


def write_bundle(directory: Path):
    labels, run, annotations, artifacts = small_study()

    def save(path, value):
        path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")
        return score.sha256(path)

    label_path = directory / "labels.json"
    run_path = directory / "run.json"
    annotation_path = directory / "review.json"
    annotation_rows = {row["id"]: row for row in annotations["cases"]}
    frozen_cases = []
    for row in run["cases"]:
        case_id = row["caseId"]
        row["datasetPath"] = case_id + ".dataset.json"
        row["datasetSha256"] = save(directory / row["datasetPath"], {"id": case_id, "raw": "artificial"})
        frozen_cases.append({"id": case_id, "path": row["datasetPath"], "sha256": row["datasetSha256"]})
        data = artifacts[case_id]
        if data["evidence"] is not None:
            for name, value in (("evidence", data["evidence"]), ("payload", {"outputs": data["outputs"]}),
                                ("transcript", {"requests": []})):
                row[name + "Path"] = case_id + "." + name + ".json"
                row[name + "Sha256"] = save(directory / row[name + "Path"], value)
            row["markdownPath"] = case_id + ".md"
            (directory / row["markdownPath"]).write_text("Artificial rendered artifact\n", encoding="utf-8")
            row["markdownSha256"] = score.sha256(directory / row["markdownPath"])
            annotation_rows[case_id]["evidence_sha256"] = row["evidenceSha256"]
            annotation_rows[case_id]["legacy_sha256"] = row["payloadSha256"]
    run["fixtureManifestPath"] = "frozen-fixtures.json"
    labels["fixture_manifest_sha256"] = save(directory / run["fixtureManifestPath"], {"cases": frozen_cases})
    run["fixtureManifestSha256"] = labels["fixture_manifest_sha256"]
    labels_hash = save(label_path, labels)
    run["groundTruthSha256"] = labels_hash
    annotations["ground_truth_sha256"] = labels_hash
    annotations["run_manifest_sha256"] = save(run_path, run)
    save(annotation_path, annotations)
    return label_path, run_path, annotation_path


class CountingCounterexamples(unittest.TestCase):
    def setUp(self):
        self.labels, self.run, self.annotations, self.artifacts = small_study()

    def test_false_pass_and_unknown_are_distinct_from_collection_failure(self):
        report = score.evaluate(self.labels, self.run, self.annotations, self.artifacts)
        expected = {
            "legacy_false_ready_known_blockers": (1, 3),
            "legacy_ready_unknown_readiness": (1, 1),
            "assurance_false_pass_known_fail": (1, 3),
            "assurance_false_pass_unknown": (1, 1),
            "assurance_false_pass_combined": (2, 4),
            "assurance_unknown_results": (1, 6),
            "assurance_unknown_among_answered": (1, 5),
            "legacy_unknown_results": (1, 6),
            "collection_failures": (1, 6),
            "answered_cases": (5, 6),
            "evidence_missed_blockers": (2, 3),
            "legacy_missed_blockers": (2, 3),
            "evidence_incorrect_blockers": (1, 3),
            "evidence_unassessable_blockers": (1, 3),
            "legacy_incorrect_blockers": (1, 2),
        }
        for name, pair in expected.items():
            with self.subTest(name=name):
                metric = report["metrics"][name]
                self.assertEqual((metric["numerator"], metric["denominator"]), pair)
        self.assertEqual(report["metrics"]["evidence_missed_blockers"]["case_ids"], ["collection-error", "unsafe-ready"])
        self.assertIn("collection-error", report["failure_case_ids"])
        self.assertNotIn("correct-ready", report["failure_case_ids"])

    def test_no_opportunities_is_not_a_perfect_score(self):
        self.labels["cases"] = [row for row in self.labels["cases"] if row["id"] == "correct-ready"]
        self.run["cases"] = [row for row in self.run["cases"] if row["caseId"] == "correct-ready"]
        self.annotations["cases"] = [row for row in self.annotations["cases"] if row["id"] == "correct-ready"]
        self.artifacts = {"correct-ready": self.artifacts["correct-ready"]}
        report = score.evaluate(self.labels, self.run, self.annotations, self.artifacts)
        self.assertIsNone(report["metrics"]["assurance_false_pass_combined"]["rate"])
        self.assertEqual(report["metrics"]["evidence_missed_blockers"]["denominator"], 0)

    def test_collection_failure_keeps_all_reference_blockers_in_denominator(self):
        self.labels["cases"][3]["confirmed_blockers"].append({"id": "failing-required-test"})
        self.run["cases"][3]["exportStatus"] = "not-attempted"
        report = score.evaluate(self.labels, self.run, self.annotations, self.artifacts)
        self.assertEqual(report["metrics"]["evidence_missed_blockers"]["numerator"], 3)
        self.assertEqual(report["metrics"]["evidence_missed_blockers"]["denominator"], 4)

    def test_missing_extra_duplicate_cases_and_undecided_review_are_refused(self):
        changes = [
            lambda: self.run["cases"].pop(),
            lambda: self.run["cases"].append({"caseId": "unreviewed", "status": "answered"}),
            lambda: self.labels["cases"].append(copy.deepcopy(self.labels["cases"][0])),
            lambda: self.annotations["cases"][0].update(status="undecided"),
            lambda: self.labels["adjudication"].update(conflicts=["unresolved disagreement"]),
        ]
        for change in changes:
            with self.subTest(change=change):
                self.setUp()
                change()
                with self.assertRaises(score.InvalidEvaluation):
                    score.evaluate(self.labels, self.run, self.annotations, self.artifacts)

    def test_annotations_cannot_invent_reference_ids_or_omit_reported_blockers(self):
        review = next(row for row in self.annotations["cases"] if row["id"] == "detected-blocker")
        review["evidence_matches"][0]["ground_truth_ids"] = ["not-in-frozen-labels"]
        with self.assertRaisesRegex(score.InvalidEvaluation, "Unknown frozen blocker"):
            score.evaluate(self.labels, self.run, self.annotations, self.artifacts)
        review["evidence_matches"] = []
        with self.assertRaisesRegex(score.InvalidEvaluation, "coverage mismatch"):
            score.evaluate(self.labels, self.run, self.annotations, self.artifacts)


class FileIntegrity(unittest.TestCase):
    def test_changed_code_requires_explicit_mode_baseline_and_opt_in(self):
        with tempfile.TemporaryDirectory() as temporary:
            paths = write_bundle(Path(temporary))
            run = json.loads(paths[1].read_text(encoding="utf-8"))
            run["candidateCommit"] = "3" * 40
            paths[1].write_text(json.dumps(run), encoding="utf-8")
            with self.assertRaisesRegex(score.InvalidEvaluation, "requires --allow-candidate-rerun"):
                score.score_files(*paths)
            with self.assertRaisesRegex(score.InvalidEvaluation, "lacks declared repaired-rerun mode"):
                score.score_files(*paths, allow_candidate_rerun=True)
            run["candidateMode"] = "repaired-rerun"
            paths[1].write_text(json.dumps(run), encoding="utf-8")
            with self.assertRaisesRegex(score.InvalidEvaluation, "lacks matching frozen baseline"):
                score.score_files(*paths, allow_candidate_rerun=True)
            run["baselineCandidateCommit"] = "4" * 40
            paths[1].write_text(json.dumps(run), encoding="utf-8")
            with self.assertRaisesRegex(score.InvalidEvaluation, "baseline differs from frozen labels"):
                score.score_files(*paths, allow_candidate_rerun=True)

    def test_declared_repaired_rerun_uses_identical_labels_and_denominators(self):
        with tempfile.TemporaryDirectory() as temporary:
            paths = write_bundle(Path(temporary))
            original_labels_bytes = paths[0].read_bytes()
            baseline = score.score_files(*paths)
            run = json.loads(paths[1].read_text(encoding="utf-8"))
            run.update(candidateCommit="3" * 40, candidateMode="repaired-rerun", baselineCandidateCommit=COMMIT)
            paths[1].write_text(json.dumps(run), encoding="utf-8")
            annotations = json.loads(paths[2].read_text(encoding="utf-8"))
            annotations.update(candidate_commit="3" * 40, run_manifest_sha256=score.sha256(paths[1]))
            paths[2].write_text(json.dumps(annotations), encoding="utf-8")
            rerun = score.score_files(*paths, allow_candidate_rerun=True)
            self.assertEqual(paths[0].read_bytes(), original_labels_bytes)
            self.assertEqual(rerun["inputs"]["ground_truth_sha256"], baseline["inputs"]["ground_truth_sha256"])
            self.assertEqual(rerun["metrics"], baseline["metrics"])
            self.assertEqual(rerun["candidate_commit"], "3" * 40)
            self.assertEqual(rerun["baseline_candidate_commit"], COMMIT)
            self.assertEqual(rerun["candidate_mode"], "repaired-rerun")

    def test_full_bundle_is_deterministic_and_changed_artifact_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            paths = write_bundle(Path(temporary))
            first = score.score_files(*paths)
            second = score.score_files(*paths)
            self.assertEqual(json.dumps(first, sort_keys=True), json.dumps(second, sort_keys=True))
            self.assertIn("1 / 3 cases", score.markdown(first))
            (Path(temporary) / "unsafe-ready.evidence.json").write_text("{}\n", encoding="utf-8")
            with self.assertRaisesRegex(score.InvalidEvaluation, "Changed evidence artifact"):
                score.score_files(*paths)

    def test_changed_labels_require_refreeze_of_run_and_review(self):
        with tempfile.TemporaryDirectory() as temporary:
            paths = write_bundle(Path(temporary))
            with paths[0].open("a", encoding="utf-8") as handle:
                handle.write("\n")
            with self.assertRaisesRegex(score.InvalidEvaluation, "Annotation ground-truth hash"):
                score.score_files(*paths)

    def test_changed_dataset_cannot_be_hidden_by_updating_mutable_run_records(self):
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            paths = write_bundle(directory)
            run = json.loads(paths[1].read_text(encoding="utf-8"))
            changed_case = run["cases"][0]
            dataset = directory / changed_case["datasetPath"]
            dataset.write_text('{"raw": "replacement after freeze"}\n', encoding="utf-8")
            changed_case["datasetSha256"] = score.sha256(dataset)
            paths[1].write_text(json.dumps(run), encoding="utf-8")
            review = json.loads(paths[2].read_text(encoding="utf-8"))
            review["run_manifest_sha256"] = score.sha256(paths[1])
            paths[2].write_text(json.dumps(review), encoding="utf-8")
            with self.assertRaisesRegex(score.InvalidEvaluation, "pre-output fixture freeze"):
                score.score_files(*paths)

    def test_duplicate_json_keys_are_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            path = Path(temporary) / "duplicate.json"
            path.write_text('{"cases": [], "cases": []}', encoding="utf-8")
            with self.assertRaisesRegex(score.InvalidEvaluation, "Duplicate JSON key"):
                score.load_json(path)


if __name__ == "__main__":
    unittest.main()
