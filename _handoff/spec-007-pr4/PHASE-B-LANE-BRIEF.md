# Phase B lane-fix brief (spec-007-pr4)

`$SP` = `/tmp/claude-0/-home-user-Ashlar/7e9a762d-8493-5055-bb39-534bc69d5750/scratchpad`

You finish ONE PR 4 lane so the integrator can open its PR. Read first:
- `/home/user/Ashlar/CLAUDE.md` (rules: never run `dotnet` on the host; mutation-check every behaviour change).
- `$SP/ws/handoff.md` §3 (decisions), §4 (your lane's fix list), §5 (routine), §6 (environment facts).
- `$SP/ws/waveA-findings.md`: your lane's section (file/line detail for each finding).
- `$SP/ws/LANE-RULES.md` (the lane rules from phase A; still apply, except the paths in it are stale — use `$SP` above).
- `$SP/ws/DESIGN-4-final.md` §4 row for your PR and the §2/§3.B parts it names, as needed.
- `$SP/ws/pr-<lane>-body.md`: the PR body draft. You update it.

## Steps

1. **Clone.** `git clone -q https://github.com/IanFrelinger/Ashlar $SP/b-<lane>` (fetch from GitHub, not the
   host checkout). If the clone exists, continue from its state. `git checkout -b <lane branch> origin/<lane branch>`.
   Squash to one commit if it has several (4.1 has 11). Never edit `/home/user/Ashlar`.
2. **Fix** every item in handoff §4 for your lane. Where a twin is asked for, write it before the fix and, for
   a behaviour fix, show it red first (commit the twin alone, run it, record the count).
   If you hit something that needs an OWNER decision (policy, security posture, compatibility), do not
   choose: stop and report it as a question.
3. **Merge master in:** `git fetch origin master && git merge origin/master` (the merge commit is fine for
   now; the integrator squash-merges the PR). Resolve conflicts (expect SPEC-007 PR 4 plan bullets,
   CHANGELOG Unreleased, `docs/EgressInventory.md`, `ProcessGlobalEnvironmentConventionTests`, the
   knowledge graph). Then recount, for the merged tree:
   - the `ci/cert-gate-assertions.md` Certification count paragraph (`git ls-files`);
   - row 64's TSV sums (`ci/egress-inventory.tsv`);
   - the `EgressGuardConventionTests` floors comment.
   `git add -A`, then `python scripts/knowledge-graph/build-knowledge-graph.py`, commit.
4. **Verify in the container only** (`bash scripts/test-in-container.sh --repo $SP/b-<lane> ...`; it tests
   committed state; NEVER pipe it through `head`, save the full output to a log under `$SP/logs-<lane>/`):
   - full cert-gate on net8.0: `-- bash scripts/run-cert-gate.sh` (or however the script expects; read it);
   - your lane's touched test projects/filters on net8.0 and net10.0 (Tests.Orchestration is net8.0 only);
   - repo gates: `PATH=$SP/sc-py/bin:$PATH bash scripts/ci/run-repo-gates.sh` on the host (it is not dotnet);
     all 25 must pass.
   Run ONE container at a time from your agent. Another agent shares the 4 CPUs; be patient.
   If docker is down: `CLAUDE_CODE_REMOTE=true bash /home/user/Ashlar/.claude/hooks/session-start.sh`.
   Background commands die at 2 hours: split long batches.
5. **Mutation-check** each new or changed behaviour with `bash scripts/mutation-check.sh --repo $SP/b-<lane>
   --ref <committed head> ...` (one at a time). An `INVALID ... reason=red-no-tests` means the mutant did
   not compile; pick one that compiles. Record every summary line verbatim.
6. **Commit and push.** End state: the lane branch = phase-A squashed commit + fix commit(s) + master merge,
   (or one squashed commit then merge — your choice, but keep it reviewable). Commit messages end with:
   ```
   Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>
   Claude-Session: https://claude.ai/code/session_01NNv8ZAWjwkJYwgB4yFMLBs
   ```
   Push: `git push --force-with-lease -u origin <lane branch>` (force is fine: it is Claude's own lane branch
   with no PR yet). Never push `wip/*` helper branches. Retry network failures up to 4 times (2s,4s,8s,16s).
7. **Update the PR body** `$SP/ws/pr-<lane>-body.md` in place: the phase B fixes, the new counts, each
   mutation's verbatim summary line, and a "Records" list (which docs/records this PR touches, for the drift
   audit). Keep "(this PR)" placeholders for the PR number; the integrator fills them.
   Do NOT open a PR and do NOT post on the agent-bus — the integrator does both.

## Report back (final message)

- branch and pushed head SHA; base master SHA merged in;
- each §4 finding: fixed how / answered why not;
- red-first evidence for each behaviour fix (counts);
- cert-gate count, other test runs with counts, repo gates N/25;
- every mutation summary line verbatim;
- anything not run, anything that needs an owner decision, and expected conflicts with sibling lanes.
