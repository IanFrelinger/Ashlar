#!/usr/bin/env python3
"""Exercise per-job readiness evidence, skips, failures, and machine-readable SHA binding."""

import importlib.util
import json
import re
import tempfile
import unittest
from argparse import Namespace
from pathlib import Path


SCRIPT = Path(__file__).with_name("readiness-run-report.py")
WORKFLOW = SCRIPT.parents[2] / ".github" / "workflows" / "full-platform-readiness-gate.yml"
SPEC = importlib.util.spec_from_file_location("readiness_run_report", SCRIPT)
REPORT = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(REPORT)


def args(**overrides):
    values = {
        "changes_result": "success",
        "event": "pull_request",
        "run_heavy": "true",
        "verdict": "partial",
        "run_id": "1234",
        "sha": "a" * 40,
        "repository": "IanFrelinger/Ashlar",
        "server_url": "https://github.com",
        "native_result": "success",
        "container_result": "success",
        "docker_cli_result": "success",
        "docker_all_result": "skipped",
        "artifact_download_result": "success",
    }
    values.update(overrides)
    return Namespace(**values)


def job(name, conclusion, steps=None, url=None):
    return {
        "name": name,
        "conclusion": conclusion,
        "status": "completed",
        "html_url": url or f"https://github.com/IanFrelinger/Ashlar/actions/runs/1234/job/{name}",
        "steps": steps or [],
    }


def jobs_response(items, total_count=None):
    return {"total_count": len(items) if total_count is None else total_count, "jobs": items}


def artifacts_response(items, total_count=None):
    return {
        "total_count": len(items) if total_count is None else total_count,
        "artifacts": items,
    }


class ReadinessRunReportTests(unittest.TestCase):
    def test_docker_prerequisites_are_checked_before_image_builds(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        for job_name, build_step in (
            ("container-platform:", "Setup — build container image"),
            ("docker-cli-image:", "Setup — build CLI Docker image"),
            ("docker-all-images:", "Build ${{ matrix.label }}"),
        ):
            match = re.search(
                rf"(?ms)^  {re.escape(job_name)}\n(.*?)(?=^  [a-z][a-z-]*:\n|\Z)",
                workflow,
            )
            self.assertIsNotNone(match, f"workflow job {job_name} was not found")
            section = match.group(1)
            preflight = section.index("Preflight — Docker daemon")
            build = section.index(build_step)
            self.assertLess(preflight, build, f"{job_name} starts building before checking Docker")

    def test_matrix_rows_use_actual_job_results_not_group_result(self):
        data = REPORT.render_report(
            jobs_response(
                [
                    job("Changes — core paths touched?", "success"),
                    job("Linux — setup · discover · dry-run", "success"),
                    job("macOS — setup · discover · dry-run", "failure"),
                    job("Windows — setup · discover · dry-run", "success"),
                    job("Docker api — build · smoke", "success"),
                    job("Docker quickstart — build · smoke", "failure"),
                    job("Docker agent-server — build · smoke", "success"),
                ]
            ),
            artifacts_response([]),
            args(),
        )

        rows = {item["name"].split(" —")[0]: item["result"] for item in data["jobs"]}
        self.assertEqual(rows["Linux"], "success")
        self.assertEqual(rows["macOS"], "failure")
        self.assertEqual(rows["Windows"], "success")
        self.assertEqual(rows["Docker api"], "success")
        self.assertEqual(rows["Docker quickstart"], "failure")
        self.assertEqual(rows["Docker agent-server"], "success")
        self.assertEqual(data["verdict"], "failed")
        self.assertEqual(data["tested_sha"], "a" * 40)
        self.assertEqual(data["workflow_run_id"], "1234")

    def test_failure_and_timeout_name_the_step(self):
        data = REPORT.render_report(
            jobs_response(
                [
                    job("Changes — core paths touched?", "success"),
                    job(
                        "Ubuntu-Container (container) — setup · discover · dry-run",
                        "timed_out",
                        [
                            {"name": "Setup — build container image", "conclusion": "timed_out"},
                            {"name": "Discover + Dry-run in container", "conclusion": "skipped"},
                        ],
                    ),
                    job(
                        "Linux — setup · discover · dry-run",
                        "failure",
                        [{"name": "Setup — restore dependencies", "conclusion": "failure"}],
                    ),
                ]
            ),
            artifacts_response([]),
            args(),
        )

        by_name = {item["name"]: item for item in data["jobs"]}
        container = by_name["Ubuntu-Container (container) — setup · discover · dry-run"]
        linux = by_name["Linux — setup · discover · dry-run"]
        self.assertEqual(container["result"], "timed_out")
        self.assertEqual(container["failed_step"], "Setup — build container image")
        self.assertEqual(linux["failed_step"], "Setup — restore dependencies")
        self.assertEqual(data["verdict"], "failed")

    def test_failed_change_detection_explains_blocked_skips(self):
        data = REPORT.render_report(
            jobs_response(
                [
                    job("Changes — core paths touched?", "failure"),
                    job("Linux — setup · discover · dry-run", "skipped"),
                ]
            ),
            artifacts_response([]),
            args(changes_result="failure"),
        )

        self.assertTrue(data["report_complete"])
        self.assertEqual(data["jobs"][0]["skip_reason"], "Blocked: the change-detection job did not succeed.")
        self.assertEqual(data["verdict"], "failed")

    def test_artifact_api_failure_makes_the_report_incomplete(self):
        workflow = WORKFLOW.read_text(encoding="utf-8")
        self.assertIn(
            'echo \'{"artifacts":null}\' > "$api_dir/artifacts.json"',
            workflow,
        )

        data = REPORT.render_report(
            jobs_response(
                [
                    job("Changes — core paths touched?", "success"),
                    job("Linux — setup · discover · dry-run", "success"),
                ]
            ),
            {"artifacts": None},
            args(),
        )

        self.assertFalse(data["report_complete"])
        self.assertIn("The Actions artifacts API returned no usable artifact list.", data["report_errors"])
        self.assertEqual(data["verdict"], "failed")

    def test_incomplete_or_unaccounted_api_pages_fail_closed(self):
        complete_jobs = [
            job("Changes — core paths touched?", "success"),
            job("Linux — setup · discover · dry-run", "success"),
        ]
        incomplete = REPORT.render_report(
            jobs_response(complete_jobs, total_count=3),
            artifacts_response([], total_count=2),
            args(),
        )
        self.assertFalse(incomplete["report_complete"])
        self.assertIn(
            "The Actions jobs API response is incomplete: received 2 of 3 records.",
            incomplete["report_errors"],
        )
        self.assertIn(
            "The Actions artifacts API response is incomplete: received 0 of 2 records.",
            incomplete["report_errors"],
        )
        self.assertEqual(incomplete["verdict"], "failed")

        missing_counts = REPORT.render_report(
            {"jobs": complete_jobs},
            {"artifacts": []},
            args(),
        )
        self.assertFalse(missing_counts["report_complete"])
        self.assertIn(
            "The Actions jobs API response has no valid total_count.",
            missing_counts["report_errors"],
        )
        self.assertIn(
            "The Actions artifacts API response has no valid total_count.",
            missing_counts["report_errors"],
        )

    def test_conditional_skips_are_explicit_and_unexpected_skips_fail(self):
        expected = REPORT.render_report(
            jobs_response(
                [
                    job("Changes — core paths touched?", "success"),
                    job("Docker ${{ matrix.label }} — build · smoke", "skipped"),
                ]
            ),
            artifacts_response([]),
            args(docker_all_result="skipped"),
        )
        self.assertTrue(expected["report_complete"])
        self.assertIn("disabled for pull_request", expected["jobs"][0]["skip_reason"])

        unexpected = REPORT.render_report(
            jobs_response(
                [
                    job("Changes — core paths touched?", "success"),
                    job("Linux — setup · discover · dry-run", "skipped"),
                ]
            ),
            artifacts_response([]),
            args(native_result="skipped"),
        )
        self.assertFalse(unexpected["report_complete"])
        self.assertEqual(unexpected["unexpected_skips"], ["Linux — setup · discover · dry-run"])
        self.assertEqual(unexpected["verdict"], "failed")

    def test_unexpanded_matrix_names_are_reported_as_expected_skips(self):
        data = REPORT.render_report(
            jobs_response(
                [
                    job("Changes — core paths touched?", "success"),
                    job("${{ matrix.label }} — setup · discover · dry-run", "skipped"),
                    job("${{ matrix.label }} (container) — setup · discover · dry-run", "skipped"),
                    job("Docker CLI image — build · smoke · discover", "skipped"),
                    job("Docker ${{ matrix.label }} — build · smoke", "skipped"),
                ]
            ),
            artifacts_response([]),
            args(
                run_heavy="false",
                native_result="skipped",
                container_result="skipped",
                docker_cli_result="skipped",
                docker_all_result="skipped",
                verdict="not-verified",
            ),
        )

        self.assertTrue(data["report_complete"])
        self.assertEqual(data["verdict"], "not-verified")
        self.assertEqual(
            [item["role"] for item in data["jobs"]],
            ["native-platform", "container-platform", "docker-cli-image", "docker-all-images"],
        )
        self.assertTrue(all(item["skip_reason"] for item in data["jobs"]))

    def test_variant_artifact_link_is_attached_to_the_matching_job(self):
        data = REPORT.render_report(
            jobs_response(
                [
                    job("Changes — core paths touched?", "success"),
                    job("Linux — setup · discover · dry-run", "success"),
                    job("Docker api — build · smoke", "success"),
                ]
            ),
            artifacts_response(
                [
                    {"name": "readiness-Linux", "id": 88, "expired": False, "size_in_bytes": 100},
                    {"name": "readiness-api", "id": 89, "expired": False, "size_in_bytes": 200},
                ]
            ),
            args(),
        )
        self.assertEqual(data["jobs"][0]["artifacts"][0]["name"], "readiness-Linux")
        self.assertEqual(
            data["jobs"][0]["artifacts"][0]["url"],
            "https://github.com/IanFrelinger/Ashlar/actions/runs/1234/artifacts/88",
        )
        self.assertEqual(data["jobs"][1]["artifacts"][0]["name"], "readiness-api")
        self.assertEqual(
            data["jobs"][1]["artifacts"][0]["url"],
            "https://github.com/IanFrelinger/Ashlar/actions/runs/1234/artifacts/89",
        )

    def test_cli_writes_sha_bound_json_and_links_the_job_summary(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            jobs_path = root / "jobs.json"
            artifacts_path = root / "artifacts.json"
            summary_path = root / "summary.md"
            output_path = root / "readiness.json"
            jobs_path.write_text(
                json.dumps(
                    {
                        "total_count": 2,
                        "jobs": [
                            job("Changes — core paths touched?", "success"),
                            job(
                                "Linux — setup · discover · dry-run",
                                "failure",
                                [{"name": "Setup — restore dependencies", "conclusion": "failure"}],
                            ),
                        ]
                    }
                ),
                encoding="utf-8",
            )
            artifacts_path.write_text('{"total_count":0,"artifacts":[]}', encoding="utf-8")
            code = REPORT.main(
                [
                    "--jobs", str(jobs_path),
                    "--artifacts", str(artifacts_path),
                    "--run-id", "1234",
                    "--sha", "a" * 40,
                    "--event", "pull_request",
                    "--run-heavy", "true",
                    "--changes-result", "success",
                    "--native-result", "failure",
                    "--container-result", "success",
                    "--docker-cli-result", "success",
                    "--docker-all-result", "skipped",
                    "--artifact-download-result", "success",
                    "--verdict", "failed",
                    "--repository", "IanFrelinger/Ashlar",
                    "--server-url", "https://github.com",
                    "--summary", str(summary_path),
                    "--output", str(output_path),
                ]
            )

            data = json.loads(output_path.read_text(encoding="utf-8"))
            summary = summary_path.read_text(encoding="utf-8")
            self.assertEqual(code, 0)
            self.assertEqual(data["tested_sha"], "a" * 40)
            self.assertEqual(data["verdict"], "failed")
            self.assertIn("Setup — restore dependencies", summary)
            self.assertIn("https://github.com/IanFrelinger/Ashlar/actions/runs/1234/job/", summary)


if __name__ == "__main__":
    unittest.main()
