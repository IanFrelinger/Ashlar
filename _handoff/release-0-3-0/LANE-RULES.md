# release-0-3-0 phase A: rules for every lane (2026-10-13)

`WORK=/c/Users/icfre/Documents/Codex/2026-09-12/f-2/work` (Git Bash form). Master at launch: `60689412e`. The owner
approved four lanes in parallel. The main thread integrates, posts on the bus and merges. You do none of those three.

## Your clone and nothing else
- Work only in the clone your prompt names. It is already on your branch from `origin/master`, with
  `user.name t`, `user.email t@t` and `core.longpaths true`.
- Never touch `$WORK/ashlar-record-correction` (the owner's checkout), `$WORK/ashlar-phase-f` (the main thread's clone),
  another lane's clone, or `$WORK/ashlar-g-review`.
- Never push to master, never merge, never post on GitHub issues or the agent-bus (#695). You may:
  - `git push -u origin <your branch>`;
  - `gh pr create`;
  - `gh issue create`, only if your prompt says so.
- `git add` takes named paths only, never `-A` or `.`. Then run `python scripts/knowledge-graph/build-knowledge-graph.py`.
  - On this Windows checkout the rebuild can mark `docs/knowledge-graph.{json,md}` modified for line endings only.
    If `git diff -- docs/knowledge-graph.json docs/knowledge-graph.md` is empty, restore them with
    `git checkout -- docs/knowledge-graph.json docs/knowledge-graph.md`.
  - Commit them only if the content changed, and confirm with
    `python scripts/knowledge-graph/verify-knowledge-graph-current.py`.
- Keep each file's line endings. Markdown and most text files are CRLF on disk here; `*.sh` and `*.py` are LF.
  Check with python (`b.count(b'\r\n')`) after editing.
- Before you open your PR: `git fetch origin master && git rebase origin/master`. Another lane, or the main thread's
  records PR (R3b, which edits the SPEC-007 decisions log), may have merged. Resolve a conflict by keeping master's
  content and re-applying yours. Re-run what the rebase invalidates.

## Never run `dotnet` on the host. One container at a time, machine-wide
- Build and test only with `bash scripts/test-in-container.sh --repo <your clone> ...`. It tests the COMMITTED tree, so
  commit before every run. Read its usage header first.
- The Docker VM has 5 GB, and four lanes share it. **Every container run takes the lock**, and so does every
  `scripts/mutation-check.sh` run, which makes two:
  ```bash
  LOCK=$WORK/g-refs/release-A/docker.lock
  until mkdir "$LOCK" 2>/dev/null; do
    # break a stale lock only if no devtest container runs AND the lock is over 120 minutes old
    if [ -z "$(docker ps -q --filter ancestor=ashlar-devtest:local)" ] && [ -n "$(find "$LOCK" -maxdepth 0 -mmin +120 2>/dev/null)" ]; then rmdir "$LOCK"; fi
    sleep 30
  done
  echo "<your lane> $(date -u +%FT%TZ)" > "$LOCK/owner"
  ... the container run(s) ...
  rm -f "$LOCK/owner"; rmdir "$LOCK"
  ```
  - Always release the lock, also after a failed run. Use `trap` in a script file you run by path.
  - Hold it for one logical run, not across your thinking.
- Tee every run to a log under `$WORK/g-refs/release-A/<lane>-logs/`, and read the real `Passed!`/`Failed!` or summary
  lines. Never pipe a run through `head`.
- Host python is `python` (3.13). `python3` is the Microsoft Store stub. For `scripts/mutation-check.sh`, put this shim
  first on PATH, in POSIX form:
  `export PATH="/c/Users/icfre/AppData/Local/Temp/claude/C--Users-icfre-Documents-Codex-2026-09-12-f-2-work-ashlar-record-correction/875065c3-5d64-44f0-9643-39122a4af911/scratchpad/mutcheck/pybin:$PATH"`.
  A Windows-form PATH entry is silently ignored, and every mutation then reports INVALID.
- Write scripts with your file-writing tool and run them by path. Heredocs in the shell tool mangle `\n` and
  backslashes.

## Evidence (CLAUDE.md: a check never observed failing is not evidence)
- **A behaviour change gets its test first.** Commit the test alone, run it in the container and see it RED, with the
  failing test named and the counts. Then commit the fix and see it GREEN.
- **Then mutation-check each behaviour** against a commit, with `scripts/mutation-check.sh`, or with a manifest entry
  under `scripts/mutations/` run through `python scripts/run-spec007-mutation-batch.py` (read its docstring).
  Quote every summary line verbatim.
- **Count, never quote.** Every number you write comes from a command you ran, and the PR body shows the command.
- **Repo gates:** run `bash scripts/ci/run-repo-gates.sh` on the host at the end. On untouched master, 2 gates fail on
  this Windows host (`tests/scripts/mutation-check.test.sh`, `tests/scripts/session-start-hook.test.sh`). Report the
  totals, and name any other failure.
- **CI is the compiler of record.** If you cannot run something locally, say so in the PR body; never claim it.

## Delivery
- Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Open the PR with `gh pr create --base master --body-file <file>`, written from `.github/PULL_REQUEST_TEMPLATE.md`.
  The body:
  - quotes the red and green lines, the mutation lines and the counts;
  - lists integrator decisions you took, as integrator decisions, never as owner decisions;
  - lists what was not observed failing;
  - ends with `🤖 Generated with [Claude Code](https://claude.com/claude-code)`.
- Add `[coordinated-integration]` to the title only if non-test code under both `src/` and `application/` changes, with
  the rationale in the body (CONTINUITY.md describes the token).
- **Do not merge.** Your final message returns:
  - branch, head SHA, PR number and URL;
  - the verification lines;
  - what you could not do and why;
  - a bus `handoff` paragraph (`From: claude` / `To: grok` / `Kind: handoff` / `About: pr-<n>`) naming every record and
    doc the PR touches, saved as `$WORK/g-refs/release-A/<lane>-logs/bus-handoff.md`.

## The owner's decisions this phase relies on
Dated rows in the SPEC-007 decisions log, `docs/specs/SPEC-007-security-labels-and-reference-monitor.md`.

**On master since #795 (2026-10-13):**
- the next release is 0.3.0;
- the 150 unshipped `Ashlar.Abstractions.Security` / `.Security.Egress` lines ship `[Experimental]` under a new id,
  `ASHLAREXP002`;
- so do the 10 `AshlarResolvedDeploymentProfileOptions` lines.

**Given 2026-10-13, landing as rows in the main thread's R3b records PR:**
- None of the three rows SPEC-007 dates 2026-10-08 (D16) and 2026-10-10 (§8 Q3, §8 Q4) was the owner's. They are struck
  to "Design as merged" as integrator defaults, and §8 Q3/Q4 stay open.
- The gRPC server host (`Ashlar.Transport.Grpc.Server.Host`) and `Ashlar.Commercial.Fleet.Host` get the AG/SW
  loopback-or-fail-boot rule before the 0.3.0 tag. That wiring is phase B work, not this phase's.
- `[Experimental]` symbols stay in `PublicAPI.Unshipped.txt` across the 0.3.0 tag. The promotion moves only
  non-experimental lines.

**Integrator decision (main thread):** SPEC-007's public members in Substrate packages (Infrastructure, Hosting,
Manifest, BackgroundAgents, AI.Pipeline) stay Substrate tier per `docs/SdkCompatibilityPolicy.md` "Substrate". They are
disclosed in the release notes and not marked `[Experimental]`.

Background for SPEC-007 work: `$WORK/g-refs/review2/REVIEW-2026-10-11.md` (§3, §4, §6, §8, §9) and the lens notes
beside it. The storage-branch copies are under `_handoff/spec-007-pr4/` on `claude/spec-007-pr4-workspace`.
