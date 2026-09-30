#!/usr/bin/env python3
"""Decide what the Full Platform Readiness Gate proved about one commit.

Called by scripts/release/readiness-verdict-for-sha.sh, which does the GitHub fetching; this file
only reads JSON and decides, so every branch of the decision can be driven offline from a fixture
(tests/scripts/readiness-verdict-for-sha.test.sh). Not meant to be run by hand.

  plan <dir>                    read <dir>/check-runs.ndjson and print what else the shell must fetch
  evaluate <sha> dir <dir>      decide from what the shell fetched into <dir>
  evaluate <sha> fixture <file> decide from one evidence document (offline; never evidence itself)

An evidence document is {"check_runs": [...], "annotations": {"<check run id>": [...]},
"workflow_runs": [...]}, each object shaped as the GitHub REST API returns it. workflow_runs holds
the Actions run behind each check run, plus any run of the readiness workflow for the SHA that has
no check run yet (one still in progress).

Exit 0 VERIFIED, 1 REFUSED, 2 UNDETERMINED, and 3 for a VERIFIED read from an offline fixture: a
fixture is never evidence, so it never exits 0. The last line of output always names which, and why.
"""
import io
import json
import os
import re
import sys

for _stream in (sys.stdout, sys.stderr):
    try:
        # Windows python writes CRLF by default, and a trailing CR on the verdict line makes it
        # compare unequal to the same text on Linux.
        _stream.reconfigure(newline="\n")
    except AttributeError:
        pass

PREFIX = "readiness-verdict-for-sha"
CHECK_NAME = "Readiness summary"
ANNOTATION_TITLE = "Readiness verdict"
WORKFLOW_FILE = "full-platform-readiness-gate.yml"
WORKFLOW_PATH = ".github/workflows/" + WORKFLOW_FILE
ACTIONS_APP = "github-actions"
# Written by the `Summarize results` step of the readiness-summary job. Change both together.
MESSAGE = re.compile(
    r"^verdict=(verified|partial|not-verified|failed) lanes_ran=([0-9]+) lanes_skipped=([0-9]+)$")
JOB_URL = re.compile(r"/actions/runs/([0-9]+)/job/[0-9]+")

EXIT = {"VERIFIED": 0, "REFUSED": 1, "UNDETERMINED": 2}
# A fixture is never evidence, so a VERIFIED reading of one must not exit 0: otherwise
# `READINESS_VERDICT_FIXTURE=<file> bash readiness-verdict-for-sha.sh $SHA && git tag ...` tags on a
# test file, and so would any gate built on this exit code. REFUSED and UNDETERMINED already exit
# non-zero and keep their codes in fixture mode, so a test can still tell them apart.
FIXTURE_VERIFIED_EXIT = 3


def evidence_route(sha):
    short = sha[:12]
    return [
        "",
        "To get evidence for exactly this commit, run the gate on a ref that points at it, wait for",
        "the run to complete, and run this script again:",
        "",
        "  git push origin %s:refs/heads/readiness-evidence/%s" % (sha, short),
        "  gh workflow run %s --ref readiness-evidence/%s" % (WORKFLOW_FILE, short),
        "  # ...when that run has completed:",
        "  bash scripts/release/readiness-verdict-for-sha.sh %s" % sha,
        "  git push origin --delete readiness-evidence/%s" % short,
        "",
        "A workflow_dispatch runs every lane group unconditionally. But `gh workflow run --ref`",
        "takes a branch or tag, not a SHA, and the run evaluates whatever the ref points at when it",
        "starts. Dispatching on master is evidence for this SHA only if master still points at it",
        "then, and a master dispatch shares the push runs' concurrency group, so the next merge",
        "that starts a readiness run cancels it. A throwaway branch pinned to the SHA has neither",
        "problem. Never use a v* tag for this: that is the release trigger. This script reads only",
        "check runs attached to this exact SHA, so a dispatch that resolved to some other commit",
        "can never count here.",
    ]


def rerun_caution():
    # Every piece of advice that says `gh run rerun` carries this. A re-run keeps the original
    # run's ref (GitHub documents that it reuses GITHUB_SHA and GITHUB_REF), and the workflow's
    # concurrency group is keyed on that ref with cancel-in-progress: true.
    return [
        "",
        "A re-run is not free on master. It keeps the original run's ref, so re-running a push,",
        "schedule or dispatch run of master puts it in master's concurrency group",
        "(full-platform-readiness-refs/heads/master, cancel-in-progress: true): starting it cancels",
        "any master readiness run in flight - whose own summary then records 'failed' - and the next",
        "merge that touches a readiness path cancels the re-run. Re-run only when this shows no run",
        "queued or in progress, and hold readiness-path merges until the re-run has completed:",
        "  gh run list --workflow %s --branch master --limit 5" % WORKFLOW_FILE,
    ]


class Outcome(Exception):
    def __init__(self, status, reason, explanation):
        Exception.__init__(self, reason)
        self.status = status
        self.reason = reason
        self.explanation = explanation


def refuse(reason, *lines):
    raise Outcome("REFUSED", reason, list(lines))


def undetermined(reason, *lines):
    raise Outcome("UNDETERMINED", reason, list(lines))


def read_ndjson(path):
    out = []
    with io.open(path, encoding="utf-8") as fh:
        for number, line in enumerate(fh, 1):
            line = line.strip()
            if not line:
                continue
            try:
                out.append(json.loads(line))
            except ValueError as exc:
                undetermined("malformed-evidence", "%s:%d is not JSON: %s" % (path, number, exc))
    return out


def job_run_id(check_run):
    match = JOB_URL.search(str(check_run.get("details_url") or ""))
    return match.group(1) if match else None


def plan(directory):
    """Print, one per line, what the shell still has to fetch: annotations and Actions runs."""
    runs = read_ndjson(os.path.join(directory, "check-runs.ndjson"))
    seen = set()
    for check_run in runs:
        if not isinstance(check_run, dict) or check_run.get("name") != CHECK_NAME:
            continue
        cid = str(check_run.get("id", ""))
        if cid.isdigit():
            print("annotations " + cid)
        rid = job_run_id(check_run)
        if rid and rid not in seen:
            seen.add(rid)
            print("run " + rid)


def load_directory(directory):
    check_runs = read_ndjson(os.path.join(directory, "check-runs.ndjson"))
    listing = os.path.join(directory, "workflow-runs.ndjson")
    workflow_runs = read_ndjson(listing) if os.path.isfile(listing) else []
    annotations = {}
    for check_run in check_runs:
        if not isinstance(check_run, dict):
            continue
        cid = str(check_run.get("id", ""))
        path = os.path.join(directory, "annotations-%s.ndjson" % cid)
        if cid.isdigit() and os.path.isfile(path):
            annotations[cid] = read_ndjson(path)
        rid = job_run_id(check_run)
        path = os.path.join(directory, "run-%s.json" % rid)
        if rid and os.path.isfile(path):
            with io.open(path, encoding="utf-8") as fh:
                try:
                    # Fetched by id, so it is authoritative; it goes last and wins over the listing.
                    workflow_runs.append(json.load(fh))
                except ValueError as exc:
                    undetermined("malformed-evidence", "%s is not JSON: %s" % (path, exc))
    return {"check_runs": check_runs, "annotations": annotations, "workflow_runs": workflow_runs}


def load_fixture(path):
    try:
        with io.open(path, encoding="utf-8") as fh:
            return json.load(fh)
    except (IOError, OSError) as exc:
        undetermined("fixture-unreadable", "READINESS_VERDICT_FIXTURE=%s cannot be read: %s" % (path, exc))
    except ValueError as exc:
        undetermined("malformed-evidence", "READINESS_VERDICT_FIXTURE=%s is not JSON: %s" % (path, exc))


def read_attempt(check_run, annotations):
    """One check run -> (state, verdict, detail). state is ok|pending|missing|malformed|inconsistent."""
    status = check_run.get("status")
    conclusion = check_run.get("conclusion")
    if status != "completed":
        return "pending", None, "status %s" % status
    entries = annotations.get(str(check_run.get("id")))
    titled = [a for a in (entries or []) if isinstance(a, dict) and a.get("title") == ANNOTATION_TITLE]
    if not titled:
        return "missing", None, "completed/%s, no '%s' annotation" % (conclusion, ANNOTATION_TITLE)
    if len(titled) > 1:
        return "malformed", None, "%d annotations titled '%s'" % (len(titled), ANNOTATION_TITLE)
    message = str(titled[0].get("message") or "").strip()
    match = MESSAGE.match(message)
    if not match:
        return "malformed", None, "annotation message %r does not parse" % message
    verdict, ran, skipped = match.group(1), int(match.group(2)), int(match.group(3))
    shape_ok = {
        "verified": ran > 0 and skipped == 0,
        "partial": ran > 0 and skipped > 0,
        "not-verified": ran == 0,
        "failed": True,
    }[verdict]
    expected_conclusion = "failure" if verdict == "failed" else "success"
    if not shape_ok or conclusion != expected_conclusion:
        return "inconsistent", verdict, "%s: check conclusion is %s" % (message, conclusion)
    return "ok", verdict, "completed/%s, %s" % (conclusion, message)


def attempt_order(check_run):
    # A re-run of the same workflow run adds a check run; the one that started last is the attempt
    # branch protection shows, and it is the one that counts. The id breaks a tie.
    cid = check_run.get("id")
    return (str(check_run.get("started_at") or ""), cid if isinstance(cid, int) else -1)


def evaluate(sha, doc, lines):
    if not isinstance(doc, dict) or not isinstance(doc.get("check_runs"), list):
        undetermined("malformed-evidence", "the evidence has no check_runs list.")
    annotations = doc.get("annotations") or {}
    workflow_runs = {}
    for run in doc.get("workflow_runs") or []:
        if isinstance(run, dict) and run.get("id") is not None:
            workflow_runs[str(run["id"])] = run

    candidates = [c for c in doc["check_runs"] if isinstance(c, dict) and c.get("name") == CHECK_NAME]
    if not candidates:
        mine = [r for r in workflow_runs.values()
                if r.get("path") == WORKFLOW_PATH and r.get("head_sha") == sha]
        active = sorted(str(r["id"]) for r in mine if r.get("status") != "completed")
        if active:
            refuse("in-progress",
                   "Actions run(s) %s of %s are still running for this commit and have not" % (", ".join(active), WORKFLOW_FILE),
                   "reached the '%s' job yet. Wait for them to complete and run this script again." % CHECK_NAME)
        if mine:
            refuse("no-readiness-run", *([
                "%d completed run(s) of %s exist for this commit (%s), but none has a" % (len(mine), WORKFLOW_FILE, ", ".join(sorted(str(r["id"]) for r in mine))),
                "'%s' check run, so none recorded a verdict. Re-run it: gh run rerun <run id>" % CHECK_NAME,
            ] + rerun_caution()))
        refuse("no-readiness-run", *([
            "No '%s' check run exists for this commit, so nothing says any platform was checked." % CHECK_NAME,
            "",
            "That is expected for some commits and it is NOT a pass: on push to master the readiness",
            "gate is PATH-FILTERED, so a commit whose diff touches no readiness path (a docs-only",
            "release commit, for example) gets no run at all. Pull request runs attach to the PR's",
            "head commit, which after a squash merge is not the commit on master. (If the commit was",
            "pushed moments ago, its run may not be listed yet: wait a minute and retry first.)",
        ] + evidence_route(sha)))

    # Provenance, before anything is read. A check run of this name from some other app or workflow
    # is refused rather than ignored: ignoring it is the one mistake that could let it count.
    by_run = {}
    for check_run in candidates:
        cid = check_run.get("id")
        if check_run.get("head_sha") != sha:
            refuse("sha-mismatch", "check run %s is attached to %s, not %s." % (cid, check_run.get("head_sha"), sha))
        slug = (check_run.get("app") or {}).get("slug")
        if slug != ACTIONS_APP:
            refuse("foreign-check-run",
                   "check run %s named '%s' was created by app '%s', not GitHub Actions." % (cid, CHECK_NAME, slug))
        rid = job_run_id(check_run)
        run = workflow_runs.get(rid) if rid else None
        if not isinstance(run, dict):
            refuse("foreign-check-run",
                   "check run %s (details_url %s) cannot be tied to an Actions run of %s."
                   % (cid, check_run.get("details_url"), WORKFLOW_PATH))
        if run.get("path") != WORKFLOW_PATH:
            refuse("foreign-check-run",
                   "check run %s comes from workflow '%s', not %s." % (cid, run.get("path"), WORKFLOW_PATH))
        if run.get("head_sha") != sha:
            refuse("sha-mismatch", "Actions run %s evaluated %s, not %s." % (rid, run.get("head_sha"), sha))
        by_run.setdefault(rid, []).append(check_run)

    readings = []  # (rid, event, state, verdict, check_run)
    for rid in sorted(by_run, key=int):
        attempts = sorted(by_run[rid], key=attempt_order)
        latest = attempts[-1]
        run = workflow_runs[rid]
        state, verdict, detail = read_attempt(latest, annotations)
        if run.get("status") != "completed":
            # A re-run in progress has not created its new summary check run yet, so the latest
            # check run is still the previous attempt's. The run's own status is what says so.
            state, verdict = "pending", None
            detail += "; but Actions run status is %s (a newer attempt is running)" % run.get("status")
        lines.append("  Actions run %s (%s): check run %s %s"
                     % (rid, run.get("event", "?"), latest.get("id"), detail))
        lines.append("      %s" % (latest.get("details_url") or run.get("html_url") or ""))
        for older in attempts[:-1]:
            _, _, older_detail = read_attempt(older, annotations)
            lines.append("      superseded by the later attempt: check run %s %s" % (older.get("id"), older_detail))
        readings.append((rid, run.get("event", "?"), state, verdict, latest))

    # A run of the workflow with no summary check run at all yet: still running, still able to fail.
    for rid in sorted(workflow_runs, key=lambda k: int(k) if k.isdigit() else -1):
        run = workflow_runs[rid]
        if (rid not in by_run and run.get("path") == WORKFLOW_PATH and run.get("head_sha") == sha
                and run.get("status") != "completed"):
            lines.append("  Actions run %s (%s): status %s, no '%s' check run yet"
                         % (rid, run.get("event", "?"), run.get("status"), CHECK_NAME))
            readings.append((rid, run.get("event", "?"), "pending", None, None))

    def runs_in(*states):
        return [r for r in readings if r[2] in states]

    pending = runs_in("pending")
    if pending:
        refuse("in-progress", *[
            "%d run(s) above have not completed, and an unfinished run could still report 'failed'." % len(pending),
            "Wait for them to complete and run this script again.",
        ])
    missing = runs_in("missing")
    if missing:
        refuse("missing-annotation", *([
            "%d run(s) above carry no '%s' annotation, so what they proved cannot be read." % (len(missing), ANNOTATION_TITLE),
            "Either the workflow at this commit predates the annotation (any verdict it computed is",
            "then only in the run's step summary, which the Checks API does not expose), or the",
            "summary job ended before emitting it (cancelled, or an earlier step failed). A green tick",
            "without the annotation does not distinguish 'every platform passed' from 'none ran'.",
            "If the run was cancelled or broke early, re-run it (gh run rerun <run id>): the later",
            "attempt supersedes this one. A commit that predates the annotation cannot be certified",
            "by this script at all; read its verdict from the run summary and say so in the release.",
        ] + rerun_caution()))
    broken = runs_in("malformed", "inconsistent")
    if broken:
        refuse("malformed-annotation", *[
            "The '%s' annotation on %d run(s) above does not parse, or contradicts its own check" % (ANNOTATION_TITLE, len(broken)),
            "conclusion. The workflow and this script disagree about the format; fix them together",
            "(%s, job readiness-summary) rather than reading around it." % WORKFLOW_PATH,
        ])

    verdicts = sorted(set(r[3] for r in readings))
    if "failed" in verdicts:
        failed_ids = ", ".join(r[0] for r in readings if r[3] == "failed")
        advice = [
            "Open the failed run (Actions run %s). If a lane was cancelled because a newer push to" % failed_ids,
            "the same ref superseded it, or failed on a known flake, re-run THAT run with",
            "gh run rerun <run id>; its latest attempt then supersedes the failure. Otherwise the",
            "commit is not ready.",
            "",
            "A dispatch on a throwaway branch does NOT clear this. The failed run stays attached to",
            "this commit, and a verified dispatch beside it is conflicting-verdicts. Only a later",
            "attempt of the failed run itself supersedes its failure.",
        ] + rerun_caution()
        if len(verdicts) > 1:
            refuse("conflicting-verdicts", *([
                "Runs of this commit disagree: %s. A failure is not outweighed by a pass in" % ", ".join(verdicts),
                "another run, and this script will not choose between them.",
            ] + advice))
        refuse("failed", *(["A lane group failed or was cancelled on this commit."] + advice))

    # A pull_request run's check is attached to the PR head, but it checked out and tested
    # refs/pull/N/merge - this commit merged into its base, not this commit. Today such a run can
    # never be `verified` (it skips docker-all-images); if the workflow ever changes that, it still
    # must not certify this SHA.
    verified_here = [r for r in readings if r[3] == "verified" and r[1] != "pull_request"]
    if verified_here:
        weaker = sorted(set(r[3] if r[1] != "pull_request" else r[3] + " (pull_request)"
                            for r in readings if r not in verified_here))
        note = []
        if weaker:
            note = ["Other runs reported %s: passes that exercised fewer lanes or another tree," % ", ".join(weaker),
                    "which do not contradict a run in which every lane group ran and passed here."]
        raise Outcome("VERIFIED", "verified", [
            "Every lane group of the readiness gate ran and passed on this exact commit.",
        ] + note)

    if "verified" in verdicts:
        refuse("merge-commit-only", *([
            "The only verified run is a pull_request run. Its check is attached to this commit, but",
            "it tested this commit merged into its base branch, not this commit alone.",
        ] + evidence_route(sha)))

    if "partial" in verdicts:
        refuse("partial", *([
            "Some lane groups ran and passed and others were skipped, so this is not platform",
            "verification of the commit, and it is refused. On push, schedule and workflow_dispatch",
            "every lane group runs; a partial verdict comes from a pull_request or merge_group run,",
            "which never builds the production Docker images (docker-all-images).",
        ] + evidence_route(sha)))

    refuse("not-verified", *([
        "Every lane group was skipped: the check is green because nothing in the diff reached a",
        "readiness path, not because any platform was exercised.",
    ] + evidence_route(sha)))


def main(argv):
    if len(argv) >= 2 and argv[0] == "plan":
        plan(argv[1])
        return 0
    if len(argv) != 4 or argv[0] != "evaluate" or argv[2] not in ("dir", "fixture"):
        sys.stderr.write(__doc__)
        return 2
    sha, source, where = argv[1], argv[2], argv[3]
    lines = []
    if source == "fixture":
        lines.append("%s: OFFLINE FIXTURE MODE - evidence read from %s, not from GitHub." % (PREFIX, where))
    lines.append("%s: '%s' check runs for %s" % (PREFIX, CHECK_NAME, sha))
    try:
        doc = load_fixture(where) if source == "fixture" else load_directory(where)
        evaluate(sha, doc, lines)
        outcome = Outcome("UNDETERMINED", "no-decision", ["internal error: no decision was reached."])
    except Outcome as reached:
        outcome = reached
    for line in lines + [""] + outcome.explanation + [""]:
        print(line)
    suffix = " [offline fixture - not evidence]" if source == "fixture" else ""
    if outcome.status == "VERIFIED":
        print("%s: %s VERIFIED%s" % (PREFIX, sha, suffix))
        if source == "fixture":
            return FIXTURE_VERIFIED_EXIT
    else:
        print("%s: %s %s (%s)%s" % (PREFIX, sha, outcome.status, outcome.reason, suffix))
    return EXIT[outcome.status]


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
