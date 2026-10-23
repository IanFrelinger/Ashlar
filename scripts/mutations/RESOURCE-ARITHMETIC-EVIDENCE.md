# Resource allocator arithmetic evidence — 2026-10-23

Tested source: `bc4c040c5e4dc12d716c30ec534940766494ee42`. [Linux validation run](https://github.com/IanFrelinger/Ashlar/actions/runs/39906000763). Tests ran through the repository container harness on net8.0.

The unchanged allocator produced five public-API expectation mismatches and zero probe errors in [run 39905386780](https://github.com/IanFrelinger/Ashlar/actions/runs/39905386780). The [15-case regression baseline](https://github.com/IanFrelinger/Ashlar/actions/runs/39905564902) had **14 failures, 1 pass, 0 skips**.

The repair passed **348/348 orchestration tests, 0 skips**, and the unchanged five public-API probes reported zero expectation failures and zero probe errors. Rollback on rejection, negative-budget rejection, and finite zero utilization are explicit integrator policies added by this change, not prior owner decisions. Null-maxima behavior remains unchanged.

| Mutation | Verdict | Red failed / total | Restored passed / total |
|---|---|---|---|
| `allocator-preemption-deficit` | KILLED | 3/3 | 3/3 |
| `allocator-preemption-transaction` | KILLED | 1/1 | 1/1 |
| `allocator-negative-request` | KILLED | 3/3 | 3/3 |
| `allocator-negative-budget` | KILLED | 3/3 | 3/3 |
| `allocator-zero-utilization` | KILLED | 1/1 | 1/1 |

The [validation artifact](https://github.com/IanFrelinger/Ashlar/actions/runs/39906000763/artifacts/12480733217) retains the suite, probe, and mutation logs. Its embedded mutation archive SHA-256 is `5e2799da5082de1d829704f569ed91c69e0c9d86dc7662bb4a68481475d1c44b` (47929 bytes). The repository decoder verified and extracted the archive. Every mutation has actual assertion failures, exact-byte restoration, clean Git status, and a green rebuild; per-case TRX and commands are retained.

This focused evidence is separate from the full SPEC-007 campaign in issue #764. Preemption remains reservation bookkeeping and does not guarantee that an agent stops executing.
