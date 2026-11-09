"""Advisory workflow cost/reliability candidates, never budget enforcement.

The 60-minute review threshold is an integrator choice, not a GitHub limit.
These rules do not estimate money or runtime, evaluate expressions, change jobs,
or determine whether a timeout/concurrency policy is appropriate for deployment.
Reusable-workflow jobs are left to the called workflow's own timeout policy.
"""
import re

from security_rules import MAX_BYTES, _finding, _mapping, _scalar, _workflow_root


LONG_JOB_REVIEW_MINUTES = 60
BUILD_TEST_COMMAND = re.compile(
    r"(?m)^\s*(?:dotnet\s+(?:build|test|pack|publish|run)\b"
    r"|(?:npm|pnpm|yarn)\s+(?:run\s+)?(?:build|test)\b"
    r"|docker\s+(?:build|compose)\b|make(?:\s|$))"
)


def findings(text: str, path: str) -> list[dict]:
    if not isinstance(text, str) or not isinstance(path, str):
        raise ValueError("Cost review requires text and a repository path")
    if len(text.encode("utf-8")) > MAX_BYTES or "\0" in text:
        raise ValueError("Cost review input exceeds limits or contains NUL")
    if not path.startswith(".github/workflows/") or not path.endswith((".yml", ".yaml")):
        return []
    root = _workflow_root(text)
    top = _mapping(root)
    event = top.get("on")
    if event is None:
        raise ValueError("Workflow trigger is missing; cost coverage is incomplete")
    if event.id == "mapping":
        events = set(_mapping(event))
    elif event.id == "sequence":
        events = {_scalar(child) for child in event.value}
    else:
        events = {_scalar(event)}
    jobs = top.get("jobs")
    if jobs is None or jobs.id != "mapping":
        raise ValueError("Workflow jobs must be a mapping; cost coverage is incomplete")
    result = []
    for job in _mapping(jobs).values():
        if job.id != "mapping":
            raise ValueError("Workflow job is not a mapping")
        fields = _mapping(job)
        if "uses" in fields:
            continue
        steps = fields.get("steps")
        if steps is None or steps.id != "sequence":
            raise ValueError("Executable workflow jobs require a step sequence")
        timeout = fields.get("timeout-minutes")
        if timeout is None:
            result.append(_finding("cost/job-timeout-missing", job.start_mark.line + 1,
                "An executable job relies on the platform's default timeout instead of an explicit budget.",
                "Set timeout-minutes from observed clean-run duration plus a documented margin; retain enough time for diagnostics and cleanup."))
        elif re.fullmatch(r"[0-9]+", _scalar(timeout)) and int(timeout.value) > LONG_JOB_REVIEW_MINUTES:
            result.append(_finding("cost/long-job-timeout", timeout.start_mark.line + 1,
                "The job timeout exceeds this reviewer's 60-minute review threshold.",
                "Compare the budget with observed job duration and hangs; justify long jobs or split independent work before reducing the timeout.", "medium"))
        build_or_test = False
        for step in steps.value:
            if step.id != "mapping":
                raise ValueError("Workflow step is not a mapping")
            if BUILD_TEST_COMMAND.search(_scalar(_mapping(step).get("run"))):
                build_or_test = True
        if (build_or_test and "pull_request" in events and "concurrency" not in top
                and "concurrency" not in fields):
            result.append(_finding("cost/pr-build-without-concurrency", job.start_mark.line + 1,
                "A PR job with build/test-like commands has no workflow or job concurrency policy.",
                "Consider a workflow-and-PR-scoped concurrency group and cancelling superseded validation; preserve deployment, evidence, and cleanup requirements.", "medium"))
    return sorted(result, key=lambda row: (row["line"], row["rule"]))
