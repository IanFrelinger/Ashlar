# Open PRs assessment — #749 #750 #751 #752 #753 (2026-10-11)

Reviewer: Claude (review agent), read-only on GitHub. Clone: $WORK/ashlar-g-prs (pr-749..pr-753 fetched).
origin/master at start: 9faefe46c "test: prove ashlar docker no longer throws MissingMethodException (#749)".

## Snapshot (gh pr view / gh pr checks --required, 2026-10-11)

| PR | head | state | mergeStateStatus | required checks on head |
|---|---|---|---|---|
| #749 | ecb33c3a0 | MERGED 04:24Z (merge 9faefe46c) | — | all 5 pass (cert-gate, build-core, shell-lint, lychee x2, Readiness summary) |
| #750 | dec6f6587 | OPEN | DIRTY / CONFLICTING | Readiness summary FAIL (Windows lane failed); cert-gate, build-core, shell-lint, lychee pass; advisory `verify` fail |
| #751 | c2ecd4a75 | OPEN | DIRTY / CONFLICTING | all 5 pass; advisory `verify` fail (expected, application/-only) |
| #752 | 0924e1553 | OPEN | BEHIND / MERGEABLE | shell-lint FAIL (repo-gates: scripts/knowledge-graph/verify-knowledge-graph-current.py — new test file, graph not regenerated); other 4 pass |
| #753 | 8c32d9acf | OPEN | BEHIND / MERGEABLE | all 5 pass |

Conflicts (git merge-tree origin/master pr-N): #750 and #751 conflict ONLY in docs/knowledge-graph.json and docs/knowledge-graph.md (regenerable, caused by #749 landing its own graph regen).

Deps log read: $WORK/g-refs/deps-logs/grpc-twins-284.log (17 lines, container run 871039008):
  `Passed!  - Failed:     0, Passed:    10, Skipped:     0, Total:    10, Duration: 590 ms - Ashlar.Tests.Transport.dll (net8.0)`
  BUT branch deps-grpc-284 (ashlar-g-deps) = gRPC trio 2.84 + server csproj Grpc.Tools ref only; it does NOT carry #753's Google.Protobuf 3.34.0 -> 3.36.2 bump nor the other six bumps. Not fully equivalent to pr-753.

