#!/usr/bin/env python3
"""Check all retained evaluation reports offline, including the original failed baseline."""

from __future__ import annotations

import argparse
import subprocess
import sys
import tempfile
from pathlib import Path

import score


EVALUATION_PATH = Path("tools/ReleaseEvidenceEvaluation")
FROZEN_REVIEW_RECORDS = tuple(EVALUATION_PATH / "review" / name for name in (
    "ground-truth-protocol.md", "second-preregistration.md", "ground-truth.json",
    "second-ground-truth.json", "adjudication.json"))


def require(condition: object, message: str) -> None:
    if not condition:
        raise score.InvalidEvaluation(message)


def checked_file(path: Path, expected: str, context: str) -> None:
    score.digest(expected, context)
    require(path.is_file(), f"Missing {context}: {path}")
    require(score.sha256(path) == expected, f"Changed {context}: {path}")


def retained_path(directory: Path, manifest: dict, key: str) -> Path:
    value = manifest.get(key)
    require(isinstance(value, str) and value, f"Missing {key} in {directory.name}")
    path = Path(value)
    return path if path.is_absolute() else directory / path


def verify_retained(repo_root: Path, results_root: Path | None = None) -> list[str]:
    """Regenerate every report into temporary files, preserving nonzero findings."""
    repo_root = repo_root.resolve()
    evaluation = repo_root / EVALUATION_PATH
    results_root = results_root if results_root is not None else evaluation / "results"
    freeze = score.load_json(evaluation / "review/pre-execution-freeze.json")
    require(freeze.get("schema_version") == 1, "Unsupported pre-execution freeze schema")
    frozen_files = score.index_rows(freeze.get("files"), "path", "pre-execution frozen files")
    canonical = {
        "manifest": EVALUATION_PATH / "input-manifest.json",
        "labels": EVALUATION_PATH / "review/adjudicated-ground-truth.json",
    }
    hashes: dict[str, str] = {}
    for name, relative in canonical.items():
        require(relative.as_posix() in frozen_files, f"Pre-execution freeze lacks {relative}")
        hashes[name] = frozen_files[relative.as_posix()]["sha256"]
        checked_file(repo_root / relative, hashes[name], f"canonical frozen {name}")
    # Tool hashes describe the initial execution. Repaired code and later wrapper fixes
    # deliberately differ; only the unchanged inputs and independent review records bind here.
    for relative in FROZEN_REVIEW_RECORDS:
        require(relative.as_posix() in frozen_files, f"Pre-execution freeze lacks {relative}")
        checked_file(repo_root / relative, frozen_files[relative.as_posix()]["sha256"], "frozen pre-output review record")

    labels = score.load_json(repo_root / canonical["labels"])
    label_cases = score.validate_labels(labels)
    manifest = score.load_json(repo_root / canonical["manifest"])
    manifest_cases = score.index_rows(manifest.get("cases"), "id", "canonical manifest cases")
    require(set(label_cases) == set(manifest_cases), "Canonical manifest and label case IDs differ")
    require(labels["fixture_manifest_sha256"] == hashes["manifest"], "Canonical labels reference a different manifest")
    require(freeze.get("candidate_commit") == labels["candidate_commit"], "Pre-execution freeze and labels name different baselines")
    for row in manifest_cases.values():
        require(isinstance(row.get("path"), str) and row["path"], "Canonical manifest case lacks path")
        checked_file(repo_root / row["path"], row["sha256"], f"canonical raw case {row['id']}")
        captures = row.get("capture_files", {})
        require(isinstance(captures, dict), f"Invalid capture inventory: {row['id']}")
        for path, expected in captures.items():
            checked_file(repo_root / path, expected, f"public capture for {row['id']}")
    for row in manifest.get("source_manifests", []):
        checked_file(repo_root / row["path"], row["sha256"], "canonical source manifest")

    require(results_root.is_dir(), f"Missing retained results directory: {results_root}")
    directories = sorted((path for path in results_root.iterdir() if path.is_dir()), key=lambda path: path.name)
    require(directories, "No retained result directories; absence of reports is not a pass")
    baseline_name = "baseline-" + labels["candidate_commit"][:9]
    require(any(path.name == baseline_name for path in directories), f"Missing original retained baseline: {baseline_name}")
    verified: list[str] = []
    for directory in directories:
        for name in ("run-manifest.json", "output-review.json", "score.json", "score.md"):
            require((directory / name).is_file(), f"Missing retained report pair/input: {directory.name}/{name}")
        run = score.load_json(directory / "run-manifest.json")
        require(run.get("fixtureManifestSha256") == hashes["manifest"], f"Retained run uses different frozen manifest: {directory.name}")
        require(run.get("groundTruthSha256") == hashes["labels"], f"Retained run uses different frozen labels: {directory.name}")
        checked_file(retained_path(directory, run, "fixtureManifestPath"), hashes["manifest"], f"retained manifest in {directory.name}")
        checked_file(retained_path(directory, run, "groundTruthPath"), hashes["labels"], f"retained labels in {directory.name}")
        if directory.name == baseline_name:
            require(run.get("candidateCommit") == labels["candidate_commit"], "Original baseline directory names a different candidate")
            require(run.get("candidateMode") != "repaired-rerun", "Original baseline directory contains a repaired rerun")

        with tempfile.TemporaryDirectory(prefix="release-evidence-rescore-") as temporary:
            json_output, markdown_output = Path(temporary) / "score.json", Path(temporary) / "score.md"
            command = [sys.executable, "-B", str(Path(score.__file__).resolve()),
                       "--ground-truth", str(repo_root / canonical["labels"]),
                       "--run-manifest", str(directory / "run-manifest.json"),
                       "--annotations", str(directory / "output-review.json"),
                       "--json-output", str(json_output), "--markdown-output", str(markdown_output)]
            if run.get("candidateMode") == "repaired-rerun":
                command.append("--allow-candidate-rerun")
            result = subprocess.run(command, capture_output=True, text=True, check=False)
            require(result.returncode == 0, f"Cannot re-score {directory.name}: {result.stderr.strip() or result.stdout.strip()}")
            for generated, name in ((json_output, "score.json"), (markdown_output, "score.md")):
                require(generated.read_bytes() == (directory / name).read_bytes(), f"Retained {name} differs from deterministic re-score: {directory.name}")
        verified.append(directory.name)
    return verified


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[2])
    parser.add_argument("--results-root", type=Path)
    args = parser.parse_args()
    try:
        verified = verify_retained(args.repo_root, args.results_root)
        print(f"Verified {len(verified)} retained evaluation run(s): {', '.join(verified)}")
        print("Nonzero baseline findings are retained; this check verifies evidence consistency, not zero error rates.")
        return 0
    except (score.InvalidEvaluation, OSError, KeyError, TypeError) as exc:
        print(f"Invalid retained evaluation: {exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
