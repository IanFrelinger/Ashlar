# Phase C lane brief (spec-007-pr4)

`$SP` = `/tmp/claude-0/-home-user-Ashlar/a81481a5-34f6-5b00-a61f-74b03c502c7b/scratchpad`

You build ONE PR 4 lane from scratch (phase C: 4.3 redirects, 4.5 producers, or 4.10 AG/SW hygiene) so the
integrator can open its PR. Other lanes run at the same time in their own clones; 4 CPUs are shared.

## Read first
- `/home/user/Ashlar/CLAUDE.md` (never run `dotnet` on the host; mutation-check every behaviour change).
- `$SP/ws/handoff.md` §3 (decisions), §4 (your lane's row and carry-ins), §5 (routine), §6 (environment facts).
- `$SP/ws/DESIGN-4-final.md`: §1, the §4 row for your PR, the §2 subsections it implements, the §3.B defaults it
  names, and the owner answers at the end (they override §3.A). Phase B superseded: D9 (IVT list also has
  `Ashlar.Orchestration`), D31 (no record for the ns2.0 refusal), §2.2's `Detach()` (now `RunDetached(Action)`).
- `$SP/ws/LANE-RULES.md` (the lane rules from phase A; paths in it are stale, use `$SP` above and the clone below).
- `$SP/ws/pr-4.4-body.md` and `pr-4.6-body.md` as examples of the PR body shape and evidence level.
- `docs/specs/SPEC-007-security-labels-and-reference-monitor.md` (status, decisions log), `docs/EgressInventory.md`,
  `ci/egress-inventory.tsv`, `ci/cert-gate-assertions.md` (rows 56, 64-67), `src/Ashlar.Abstractions/Security/Egress/`.

## Steps
1. **Clone.** `git clone -q https://github.com/IanFrelinger/Ashlar $SP/c-<lane>`; `git checkout -b <lane branch>`
   from `origin/master`. If the clone exists, continue from its state (read `git log`, `git status`). One agent per
   clone. Never edit `/home/user/Ashlar`.
2. **Build** exactly your §4 row (no more, no less). Twin first; for a behaviour change commit the twin alone, run it
   in the container, and record it red with counts; then the change, and green. If the design is wrong or impossible
   against the code, record it as a `deviation` with the reason; do not silently diverge. If you hit something that
   needs an OWNER decision (policy, security posture, compatibility, a default the owner accepted), do not choose:
   stop and report it as a question. Otherwise take the fail-closed design default and record it.
3. **Records:** SPEC-007 status line for your PR marked "(this PR)"; CHANGELOG Unreleased; `docs/EgressInventory.md`,
   TSV, `ci/cert-gate-assertions.md` rows and the Certification count paragraph (`git ls-files`); row 64 TSV sums;
   the `EgressGuardConventionTests` floors comment re-measured; `PublicAPI.Unshipped.txt` for any public API.
   Merge latest `origin/master` in before the final verification. `git add -A`, then
   `python scripts/knowledge-graph/build-knowledge-graph.py`, commit.
4. **Verify in the container only** (`bash scripts/test-in-container.sh --repo $SP/c-<lane> ...`; tests committed
   state; NEVER pipe it through `head`; save full logs under `$SP/logs-<lane>/`):
   - full cert-gate on net8.0 (`-- bash scripts/run-cert-gate.sh`, read the script for how);
   - touched test classes on net8.0 and net10.0; Abstractions on netstandard2.0/net8.0/net10.0 when it changes;
     build-core when a project reference or TFM changes;
   - repo gates on the host (not dotnet): `PATH=$SP/sc-py/bin:$PATH bash scripts/ci/run-repo-gates.sh` — all 26 pass.
   Run ONE container at a time from your agent. If docker is down:
   `CLAUDE_CODE_REMOTE=true bash /home/user/Ashlar/.claude/hooks/session-start.sh`. Background commands die at
   2 hours: split long batches.
5. **Mutation-check** each behaviour with `bash scripts/mutation-check.sh` on the committed head (read its usage).
   `INVALID ... reason=red-no-tests` = the mutant did not compile; pick another. Record every summary line verbatim.
6. **Adversarial self-check** with two lenses (code / write-down: can labelled data leave without a decision, can a
   decision be made below the true mark; records / evidence: every count and claim matches the tree). Repair; repeat.
7. **Squash** your work to ONE commit on top of master (merge commits fine if master moved; keep it reviewable).
   Message ends with:
   ```
   Co-Authored-By: Claude Fable 5.1 <noreply@anthropic.com>
   Claude-Session: https://claude.ai/code/session_017TjtzCLb6VaYxD1MtRJ1ja
   ```
   Push `git push --force-with-lease -u origin <lane branch>` (retry network failures 4x: 2s,4s,8s,16s).
   Never push helper branches.
8. **PR body** draft at `$SP/ws/pr-<lane>-body.md`: Summary; Changes; Behaviour changes (what stays report-only);
   Testing with real counts and a mutation table quoting each summary line verbatim; what was NOT observed failing;
   Deviations and integrator decisions; Records (which docs/records this PR touches, for the drift audit);
   Checklist; Release. Keep "(this PR)" placeholders. Write `[coordinated-integration]` plus a rationale if non-test
   code under `application/` changes.
   Do NOT open a PR and do NOT post on the agent-bus — the integrator does both.

## Report back (final message, concise)
branch and pushed head SHA; master SHA merged in; what was built per the §4 row; deviations; red-first evidence;
cert-gate count and other runs with counts; repo gates N/26; every mutation summary line verbatim; anything not run;
owner questions; expected conflicts with sibling lanes.
