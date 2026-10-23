# Orchestration run isolation evidence — 2026-10-23

Tested source: `9273386b6680fcb6f2a8f6ac3d8e9f0736714a29`. [Linux validation run](https://github.com/IanFrelinger/Ashlar/actions/runs/39903105847). All tests ran through `scripts/test-in-container.sh` on net8.0.

The unchanged production baseline at `924c3bf` produced **12 assertion failures, 1 pass, 0 skips** in [the corrected regression run](https://github.com/IanFrelinger/Ashlar/actions/runs/39901640289). An earlier run included a test-setup error and is not counted as behavioral evidence. The candidate full orchestration suite passed **333/333, 0 skips**. The SDK newcomer probe registered, resolved, and executed a custom brick twice. Its first compilation exposed the missing extension namespace, now corrected in the guides.

| Mutation | Verdict | Red failed / total | Restored passed / total |
|---|---|---|---|
| `run-dependency-scope` | KILLED | 1/1 | 1/1 |
| `run-progress-scope` | KILLED | 1/1 | 1/1 |
| `run-health-key` | KILLED | 1/1 | 1/1 |
| `run-allocator-ownership` | KILLED | 1/1 | 1/1 |
| `run-allocator-release-other-owner` | KILLED | 1/1 | 1/1 |
| `run-allocator-leak-reservation` | KILLED | 1/1 | 1/1 |
| `run-bus-retains-subscription` | KILLED | 1/1 | 1/1 |
| `run-result-empty-progress` | KILLED | 1/1 | 1/1 |
| `run-result-other-escalations` | KILLED | 1/1 | 1/1 |
| `run-progress-summary-membership` | KILLED | 1/1 | 1/1 |
| `run-skip-exception-cleanup` | KILLED | 5/5 | 5/5 |
| `run-cleanup-masks-primary` | KILLED | 3/5 | 5/5 |
| `run-cleanup-health-retained` | KILLED | 5/5 | 5/5 |
| `run-cleanup-unbounded-wait` | KILLED | 1/2 | 2/2 |

Every case applied the exact mutation to committed source, observed the named assertion failure, restored original bytes, proved clean Git status, and rebuilt/reran green. The unbounded-wait mutation produced the expected test-level `TimeoutException`; it was not a build failure or runner timeout.

The [validation artifact](https://github.com/IanFrelinger/Ashlar/actions/runs/39903105847/artifacts/12480320728) retains suite, SDK and mutation logs. Its embedded mutation archive SHA-256 is `80757961ea55cdc4e964d780bf656ed96d5e8a0ca75b508eb0e656e48067ee85` (162955 bytes). The repository decoder verified and extracted it; per-case TRX, counts, commands, source hashes and clean-restoration records are included.

This focused manifest does **not** complete issue #764, whose separate SPEC-007 campaign requires every current security mutation on one master SHA. It is also not release certification. Agent code that ignores cancellation can outlive bounded cleanup; shutdown callbacks must not register new agents. See the architecture contract.
