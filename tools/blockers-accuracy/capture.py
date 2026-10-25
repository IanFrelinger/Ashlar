#!/usr/bin/env python3
"""Record one snapshot per pull request: ground-truth facts (GraphQL), raw REST responses, and the brick's answer.

Usage: capture.py <out-dir> [--extra NAME=COMMAND]... -- <command that starts Ashlar.Mcp.Server.Host>
Needs GITHUB_TOKEN. Runs in CI, where the host can be built. For each target it writes, back to back:
  truth/<slug>.json      GraphQL facts the rubric is computed from (truth.py)
  github/<name>.json     the REST responses the native arm replays (same layout as run.py reads)
  brick/<slug>.json      the github.blockers payload, exactly as the MCP tool returned it
  brick-NAME/<slug>.json the same from each --extra host (for example an older build), asked right after
"""
from __future__ import annotations

import datetime
import json
import os
import shlex
import pathlib
import sys
import time
import urllib.error
import urllib.request

HERE = pathlib.Path(__file__).resolve().parent
sys.path.insert(0, str(HERE))
sys.path.insert(0, str(HERE.parent.parent / "actions" / "pr-blockers"))
import pr_blockers  # noqa: E402
from fixtures import GitHubFixtures, slug  # noqa: E402

GRAPHQL = """
query($o:String!,$r:String!,$n:Int!){repository(owner:$o,name:$r){pullRequest(number:$n){
 url state merged closed isDraft mergeable mergeStateStatus reviewDecision baseRefName headRefOid
 latestOpinionatedReviews(first:50){nodes{author{login} state}}
 commits(last:1){nodes{commit{oid statusCheckRollup{state contexts(first:100){totalCount nodes{
  __typename
  ... on CheckRun{name status conclusion isRequired(pullRequestNumber:$n)}
  ... on StatusContext{context state isRequired(pullRequestNumber:$n)}
 }}}}}}
}}}
"""


def graphql(token: str, owner: str, repo: str, number: int) -> dict:
    body = json.dumps({"query": GRAPHQL, "variables": {"o": owner, "r": repo, "n": number}}).encode()
    for attempt in range(4):
        req = urllib.request.Request("https://api.github.com/graphql", data=body, method="POST", headers={
            "Authorization": f"Bearer {token}", "User-Agent": "ashlar-blockers-accuracy"})
        try:
            with urllib.request.urlopen(req, timeout=60) as r:
                data = json.loads(r.read())
            if data.get("errors"):
                raise RuntimeError(json.dumps(data["errors"])[:500])
            return data["data"]["repository"]["pullRequest"]
        except (urllib.error.URLError, RuntimeError, json.JSONDecodeError):
            if attempt == 3:
                raise
            time.sleep(5 * (attempt + 1))
    raise RuntimeError("unreachable")


def main(argv: list[str]) -> int:
    out = pathlib.Path(argv[0])
    command = argv[argv.index("--") + 1:]
    head = argv[1:argv.index("--")]
    extras = {}
    while head:
        flag, spec, head = head[0], head[1], head[2:]
        if flag != "--extra" or "=" not in spec:
            raise SystemExit(f"unexpected argument {flag} {spec}")
        name, cmd = spec.split("=", 1)
        extras[name] = shlex.split(cmd)
        (out / f"brick-{name}").mkdir(parents=True, exist_ok=True)
    token = os.environ["GITHUB_TOKEN"]
    (out / "truth").mkdir(parents=True, exist_ok=True)
    (out / "brick").mkdir(parents=True, exist_ok=True)
    gh = GitHubFixtures(out / "github", token)
    targets = json.loads((HERE / "targets.json").read_text())["prs"]
    log = []
    for t in targets:
        url = t["url"]
        owner, repo, _, number = url.removeprefix("https://github.com/").split("/")
        n = int(number)
        started = datetime.datetime.now(datetime.timezone.utc).isoformat()

        pr_rest = None
        for _ in range(4):
            pr_rest = json.loads(gh.record(f"/repos/{owner}/{repo}/pulls/{n}", refresh=True))
            if pr_rest.get("mergeable") is not None or pr_rest.get("state") != "open":
                break
            time.sleep(3)
        truth = graphql(token, owner, repo, n)
        brick = pr_blockers.ask(command, url, token, 180)
        extra_answers = {name: pr_blockers.ask(cmd, url, token, 180) for name, cmd in extras.items()}

        sha, base = pr_rest["head"]["sha"], pr_rest["base"]["ref"]
        for path in (
            f"/repos/{owner}/{repo}/pulls/{n}/reviews",
            f"/repos/{owner}/{repo}/pulls/{n}/requested_reviewers",
            f"/repos/{owner}/{repo}/commits/{sha}/check-runs",
            f"/repos/{owner}/{repo}/commits/{sha}/check-runs?per_page=100",
            f"/repos/{owner}/{repo}/commits/{sha}/status",
            f"/repos/{owner}/{repo}/commits/{sha}/statuses",
            f"/repos/{owner}/{repo}/branches/{base}",
            f"/repos/{owner}/{repo}/branches/{base}/protection",
            f"/repos/{owner}/{repo}/rules/branches/{base}",
            f"/repos/{owner}/{repo}/issues/{n}",
            f"/repos/{owner}/{repo}",
        ):
            gh.record(path, refresh=True)

        finished = datetime.datetime.now(datetime.timezone.utc).isoformat()
        (out / "truth" / f"{slug(url)}.json").write_text(json.dumps(
            {"url": url, "captured_from": started, "captured_to": finished, "graphql": truth,
             "rest_head_sha": sha}, indent=1))
        (out / "brick" / f"{slug(url)}.json").write_text(json.dumps(brick, indent=1))
        for name, answer in extra_answers.items():
            (out / f"brick-{name}" / f"{slug(url)}.json").write_text(json.dumps(answer, indent=1))
        log.append(f"{url} truth={truth['state']}/{truth['mergeStateStatus']} brick={brick['outputs']['verdict']}"
                   f" sha_match={truth['headRefOid'] == sha}")
        print(log[-1], flush=True)
    (out / "capture-log.txt").write_text("\n".join(log) + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
