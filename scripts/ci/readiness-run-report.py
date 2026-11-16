#!/usr/bin/env python3
"""Render per-job readiness evidence from the GitHub Actions run API."""

import argparse
import json
import sys
from pathlib import Path
from urllib.parse import quote


FAILURE_CONCLUSIONS = {"failure", "timed_out", "cancelled", "action_required"}
PLATFORM_PREFIXES = ("linux ", "macos ", "windows ", "ubuntu-", "alpine-", "debian-")


def load_json(path, default):
    try:
        with open(path, encoding="utf-8") as handle:
            return json.load(handle)
    except (OSError, json.JSONDecodeError):
        return default


def classify_job(job):
    conclusion = job.get("conclusion")
    if conclusion:
        return conclusion
    status = job.get("status")
    return "unknown" if status == "completed" else (status or "unknown")


def failure_step(job):
    for step in job.get("steps") or []:
        conclusion = step.get("conclusion")
        if conclusion in FAILURE_CONCLUSIONS:
            return step.get("name") or f"step {step.get('number', '?')}", conclusion
    if job.get("conclusion") == "timed_out":
        return "Job timed out before a failed step was recorded.", "timed_out"
    return None, None


def role_for(name):
    lowered = name.lower()
    if lowered.startswith("docker cli image"):
        return "docker-cli-image"
    if "(container)" in lowered:
        return "container-platform"
    if lowered.startswith("${{ matrix.label }}"):
        return "native-platform"
    if lowered.startswith("docker "):
        return "docker-all-images"
    if any(lowered.startswith(prefix) for prefix in PLATFORM_PREFIXES):
        return "native-platform"
    return None


def expected_skip_reason(job, role, args):
    result = classify_job(job)
    if result != "skipped":
        return None
    if args.changes_result != "success":
        return "Blocked: the change-detection job did not succeed."
    if role == "docker-all-images" and args.event in {"pull_request", "merge_group"}:
        return f"Expected: production image builds are disabled for {args.event} events."
    if args.run_heavy != "true":
        return "Expected: no readiness path changed, so heavy lanes were skipped."
    return None


def artifact_matches(job_name, artifacts):
    normalized = job_name.lower().replace(" (container)", "").split(" —", 1)[0].strip()
    normalized = normalized.removeprefix("docker ")
    matches = []
    for artifact in artifacts:
        name = artifact.get("name", "")
        if not name.startswith("readiness-"):
            continue
        suffix = name[len("readiness-") :].lower()
        if normalized == suffix or normalized.endswith("-" + suffix):
            matches.append(artifact)
    return matches


def artifact_url(server_url, repository, run_id, artifact_id):
    return (
        f"{server_url.rstrip('/')}/{quote(repository, safe='/')}/actions/runs/"
        f"{quote(str(run_id), safe='')}/artifacts/{quote(str(artifact_id), safe='')}"
    )


def render_report(jobs_data, artifacts_data, args):
    jobs = jobs_data.get("jobs") if isinstance(jobs_data, dict) else None
    artifacts = artifacts_data.get("artifacts") if isinstance(artifacts_data, dict) else None
    errors = []
    if not isinstance(jobs, list) or not jobs:
        jobs = []
        errors.append("The Actions jobs API returned no usable job records.")
    if not isinstance(artifacts, list):
        artifacts = []
        errors.append("The Actions artifacts API returned no usable artifact list.")

    changes_result = args.changes_result
    if not any((job.get("name") or "").startswith("Changes") for job in jobs):
        errors.append("The change-detection job is missing from the Actions jobs response.")

    summarized_jobs = []
    unexpected_skips = []
    failed_jobs = []
    for job in jobs:
        name = job.get("name") or "Unnamed job"
        role = role_for(name)
        if not role:
            continue
        result = classify_job(job)
        failed_step_name, failed_step_result = failure_step(job)
        skip_reason = expected_skip_reason(job, role, args)
        if result == "skipped" and skip_reason is None:
            skip_reason = "Unexpected skip: inspect the job and its dependency results."
            unexpected_skips.append(name)
        if result in FAILURE_CONCLUSIONS:
            failed_jobs.append(name)
        if result in {"queued", "in_progress", "unknown"}:
            errors.append(f"Readiness job did not reach a conclusion: {name} ({result}).")

        matched_artifacts = []
        for item in artifact_matches(name, artifacts):
            if item.get("expired"):
                continue
            matched_artifacts.append(
                {
                    "name": item.get("name"),
                    "id": item.get("id"),
                    "url": artifact_url(
                        args.server_url, args.repository, args.run_id, item.get("id")
                    ),
                    "size_in_bytes": item.get("size_in_bytes"),
                }
            )

        summarized_jobs.append(
            {
                "name": name,
                "role": role,
                "result": result,
                "url": job.get("html_url"),
                "failed_step": failed_step_name,
                "failed_step_result": failed_step_result,
                "skip_reason": skip_reason,
                "artifacts": matched_artifacts,
                "steps": [
                    {
                        "name": step.get("name"),
                        "status": step.get("status"),
                        "conclusion": step.get("conclusion"),
                    }
                    for step in job.get("steps") or []
                ],
            }
        )

    if not summarized_jobs:
        errors.append("No native, container, or Docker readiness jobs were found.")

    report_complete = not errors and not unexpected_skips
    effective_verdict = (
        "failed"
        if not report_complete
        or changes_result != "success"
        or failed_jobs
        else args.verdict
    )
    data = {
        "schema_version": 1,
        "workflow_run_id": args.run_id,
        "tested_sha": args.sha,
        "server_url": args.server_url,
        "event": args.event,
        "run_heavy": args.run_heavy == "true",
        "verdict": effective_verdict,
        "group_results": {
            "changes": changes_result,
            "native_platform": args.native_result,
            "container_platform": args.container_result,
            "docker_cli_image": args.docker_cli_result,
            "docker_all_images": args.docker_all_result,
        },
        "artifact_download_result": args.artifact_download_result,
        "report_complete": report_complete,
        "report_errors": errors,
        "unexpected_skips": unexpected_skips,
        "failed_jobs": failed_jobs,
        "jobs": summarized_jobs,
        "available_readiness_artifacts": [
            {
                "name": item.get("name"),
                "id": item.get("id"),
                "url": artifact_url(
                    args.server_url, args.repository, args.run_id, item.get("id")
                ),
                "expired": bool(item.get("expired")),
                "size_in_bytes": item.get("size_in_bytes"),
            }
            for item in artifacts
            if (item.get("name") or "").startswith("readiness-")
        ],
    }
    return data


def append_summary(data, summary_path):
    with open(summary_path, "a", encoding="utf-8") as handle:
        handle.write("\n### Per-job evidence\n\n")
        handle.write(
            f"Tested SHA: `{data['tested_sha']}` · run: "
            f"[{data['workflow_run_id']}]"
            f"({data['server_url'].rstrip('/')}/"
            f"{data.get('repository', '')}/actions/runs/{data['workflow_run_id']})\n\n"
        )
        handle.write("| Job / variant | Result | Failed or stalled step / skip reason | Logs | Artifact |\n")
        handle.write("|---|---|---|---|---|\n")
        for job in data["jobs"]:
            detail = job["failed_step"] or job["skip_reason"] or "—"
            if job["failed_step_result"]:
                detail += f" ({job['failed_step_result']})"
            log_link = f"[job logs]({job['url']})" if job["url"] else "unavailable"
            artifact_link = (
                f"[{job['artifacts'][0]['name']}]({job['artifacts'][0]['url']})"
                if job["artifacts"]
                else "none"
            )
            escaped_name = job["name"].replace("|", "\\|").replace("\n", " ")
            escaped_detail = detail.replace("|", "\\|").replace("\n", " ")
            handle.write(
                f"| {escaped_name} | {job['result']} | "
                f"{escaped_detail} | {log_link} | {artifact_link} |\n"
            )

        if data["available_readiness_artifacts"]:
            handle.write("\nAvailable readiness artifacts:\n")
            for item in data["available_readiness_artifacts"]:
                state = " (expired)" if item["expired"] else ""
                handle.write(f"- [{item['name']}]({item['url']}){state}\n")
        if data["report_errors"]:
            handle.write("\n**Detailed reporting incomplete:**\n")
            for error in data["report_errors"]:
                handle.write(f"- {error}\n")
        if data["unexpected_skips"]:
            handle.write("\n**Unexpectedly skipped readiness jobs; this run is failed:**\n")
            for name in data["unexpected_skips"]:
                handle.write(f"- `{name}`\n")
        if data["failed_jobs"]:
            handle.write("\nFailed, cancelled, or timed-out readiness jobs:\n")
            for name in data["failed_jobs"]:
                handle.write(f"- `{name}`\n")
        if data["artifact_download_result"] != "success":
            handle.write(
                "\n**Artifact download did not succeed** "
                f"(`{data['artifact_download_result']}`); check the run artifacts above.\n"
            )


def parse_args(argv=None):
    parser = argparse.ArgumentParser()
    parser.add_argument("--jobs", required=True)
    parser.add_argument("--artifacts", required=True)
    parser.add_argument("--run-id", required=True)
    parser.add_argument("--sha", required=True)
    parser.add_argument("--event", required=True)
    parser.add_argument("--run-heavy", required=True)
    parser.add_argument("--changes-result", required=True)
    parser.add_argument("--native-result", required=True)
    parser.add_argument("--container-result", required=True)
    parser.add_argument("--docker-cli-result", required=True)
    parser.add_argument("--docker-all-result", required=True)
    parser.add_argument("--artifact-download-result", required=True)
    parser.add_argument("--verdict", required=True)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--server-url", required=True)
    parser.add_argument("--summary", required=True)
    parser.add_argument("--output", required=True)
    return parser.parse_args(argv)


def main(argv=None):
    args = parse_args(argv)
    jobs_data = load_json(args.jobs, {})
    artifacts_data = load_json(args.artifacts, {})
    data = render_report(jobs_data, artifacts_data, args)
    data["repository"] = args.repository
    append_summary(data, args.summary)
    Path(args.output).write_text(
        json.dumps(data, indent=2, sort_keys=True) + "\n", encoding="utf-8"
    )
    return 0 if data["report_complete"] else 1


if __name__ == "__main__":
    sys.exit(main())
