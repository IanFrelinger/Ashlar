#!/usr/bin/env python3
"""Offline controls for missing, changed, and failed retained evaluation reports."""

import json
import shutil
import tempfile
import unittest
from pathlib import Path

import score
from test_score import COMMIT, write_bundle
from verify_retained import EVALUATION_PATH, FROZEN_REVIEW_RECORDS, verify_retained


def retained_study(repo_root: Path):
    evaluation = repo_root / EVALUATION_PATH
    directory = evaluation / "results" / ("baseline-" + COMMIT[:9])
    directory.mkdir(parents=True)
    label_path, run_path, review_path = write_bundle(directory)
    run = score.load_json(run_path)
    run["groundTruthPath"] = label_path.name
    manifest = score.load_json(directory / run["fixtureManifestPath"])
    source_manifest = repo_root / "artificial-source-manifest.json"
    source_manifest.write_text('{"source": "artificial fixed snapshot"}\n', encoding="utf-8")
    manifest["source_manifests"] = [{"path": source_manifest.name, "sha256": score.sha256(source_manifest)}]
    capture = repo_root / "artificial-public-capture.json"
    capture.write_text('{"authorization": "anonymous", "interval": "fixed artificial interval"}\n', encoding="utf-8")
    manifest["cases"][0]["capture_files"] = {capture.name: score.sha256(capture)}
    manifest_path = directory / run["fixtureManifestPath"]
    manifest_path.write_text(json.dumps(manifest), encoding="utf-8")
    run["fixtureManifestSha256"] = score.sha256(manifest_path)
    labels = score.load_json(label_path)
    labels["fixture_manifest_sha256"] = run["fixtureManifestSha256"]
    label_path.write_text(json.dumps(labels), encoding="utf-8")
    run["groundTruthSha256"] = score.sha256(label_path)
    for row in manifest["cases"]:
        shutil.copyfile(directory / row["path"], repo_root / row["path"])
    (evaluation / "review").mkdir()
    canonical_labels = evaluation / "review/adjudicated-ground-truth.json"
    shutil.copyfile(label_path, canonical_labels)
    canonical_manifest = evaluation / "input-manifest.json"
    shutil.copyfile(directory / run["fixtureManifestPath"], canonical_manifest)
    frozen_files = [{"path": str(EVALUATION_PATH / "input-manifest.json"), "sha256": score.sha256(canonical_manifest)},
                    {"path": str(EVALUATION_PATH / "review/adjudicated-ground-truth.json"), "sha256": score.sha256(canonical_labels)}]
    for relative in FROZEN_REVIEW_RECORDS:
        path = repo_root / relative
        path.write_text("Artificial independent pre-output review record\n", encoding="utf-8")
        frozen_files.append({"path": str(relative), "sha256": score.sha256(path)})
    (evaluation / "review/pre-execution-freeze.json").write_text(json.dumps({
        "schema_version": 1, "candidate_commit": COMMIT,
        "files": frozen_files,
    }), encoding="utf-8")
    (directory / "run-manifest.json").write_text(json.dumps(run), encoding="utf-8")
    review = score.load_json(review_path)
    review["ground_truth_sha256"] = run["groundTruthSha256"]
    review["run_manifest_sha256"] = score.sha256(directory / "run-manifest.json")
    (directory / "output-review.json").write_text(json.dumps(review), encoding="utf-8")
    report = score.score_files(canonical_labels, directory / "run-manifest.json", directory / "output-review.json")
    (directory / "score.json").write_text(json.dumps(report, indent=2, sort_keys=True, ensure_ascii=True) + "\n", encoding="utf-8")
    (directory / "score.md").write_text(score.markdown(report), encoding="utf-8")
    return directory


class RetainedEvidenceControls(unittest.TestCase):
    def test_nonzero_original_failures_are_valid_retained_evidence(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            directory = retained_study(root)
            self.assertEqual(verify_retained(root), [directory.name])
            retained = score.load_json(directory / "score.json")
            self.assertGreater(retained["metrics"]["assurance_false_pass_combined"]["numerator"], 0)

    def test_no_results_or_missing_original_baseline_is_refused(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            directory = retained_study(root)
            directory.rename(directory.with_name("some-other-result"))
            with self.assertRaisesRegex(score.InvalidEvaluation, "Missing original retained baseline"):
                verify_retained(root)
            shutil.rmtree(directory.with_name("some-other-result"))
            with self.assertRaisesRegex(score.InvalidEvaluation, "No retained result directories"):
                verify_retained(root)

    def test_every_directory_requires_score_and_review_pairs(self):
        for missing in ("score.json", "score.md", "output-review.json", "run-manifest.json"):
            with self.subTest(missing=missing), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                directory = retained_study(root)
                (directory / missing).unlink()
                with self.assertRaisesRegex(score.InvalidEvaluation, "Missing retained report pair/input"):
                    verify_retained(root)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            directory = retained_study(root)
            (directory.parent / "unscored-new-run").mkdir()
            with self.assertRaisesRegex(score.InvalidEvaluation, "unscored-new-run"):
                verify_retained(root)

    def test_changed_report_bytes_and_changed_canonical_labels_are_refused(self):
        for changed in ("score.json", "score.md"):
            with self.subTest(changed=changed), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                directory = retained_study(root)
                with (directory / changed).open("a", encoding="utf-8") as handle:
                    handle.write("\n")
                with self.assertRaisesRegex(score.InvalidEvaluation, "differs from deterministic re-score"):
                    verify_retained(root)
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            retained_study(root)
            with (root / EVALUATION_PATH / "review/adjudicated-ground-truth.json").open("a", encoding="utf-8") as handle:
                handle.write("\n")
            with self.assertRaisesRegex(score.InvalidEvaluation, "Changed canonical frozen labels"):
                verify_retained(root)

    def test_post_output_mapping_coverage_is_rechecked(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            directory = retained_study(root)
            review_path = directory / "output-review.json"
            review = score.load_json(review_path)
            case = next(row for row in review["cases"] if row["id"] == "detected-blocker")
            case["evidence_matches"] = []
            review_path.write_text(json.dumps(review), encoding="utf-8")
            with self.assertRaisesRegex(score.InvalidEvaluation, "coverage mismatch"):
                verify_retained(root)

    def test_source_manifest_and_public_capture_metadata_are_frozen(self):
        for name, error in (("artificial-source-manifest.json", "Changed canonical source manifest"),
                            ("artificial-public-capture.json", "Changed public capture")):
            with self.subTest(name=name), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                retained_study(root)
                (root / name).write_text('{"replacement": "changed after review"}\n', encoding="utf-8")
                with self.assertRaisesRegex(score.InvalidEvaluation, error):
                    verify_retained(root)


if __name__ == "__main__":
    unittest.main()
