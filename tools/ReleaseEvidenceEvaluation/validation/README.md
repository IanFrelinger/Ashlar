# Evaluation engineering validation

These are local Linux-container development checks, separate from the 36-case accuracy
sample. They do not establish human review, customer outcomes, operator custody, external
retention, hosted CI status or readiness for marketing/autonomy. Exact platform, runtimes,
image and network/cache limits are in `environment.json`.

The evaluated final producer is `b4b77b7f47975c64873dd8dc458329a584228d36`.
The subsequent validation checkout is `5b01673b02ba31808ccdd74be3ac5a1b350951c5`:
only the evaluation transport self-test changed after the producer run. Its in-memory
dispatcher no longer needs an HTTP client primitive. The actual replay/collector path is
unchanged; `producer-validation-source-comparison.json` records the production-path comparison.
Frozen labels, scoring and original outputs remain intact.

## Recorded checks

- The final required .NET 8 certification gate passed **3,931/3,931**, zero failed or skipped,
  at the validation checkout. Both the derived-count guard (`expected >= 3,926`) and skip guard
  passed. The earlier full run's failure is retained below rather than replaced.
- All **174** deterministic GitHub tests passed on .NET 10 at the same checkout, zero skipped.
  Three selector-convention tests also pass in the full .NET 8 gate. The final focused artifact
  filenames say `177` because the scratch controller omitted those three from its focused filter;
  its actual TRX/summary totals are **174**, and the filenames are preserved as recorded.
- The replay tool builds with zero errors and its transport self-test passes **10/10**.
- All **107** retained JSON packs (35 baseline, 36 first repair, 36 final) validate against
  schema 1.0 with the JSON Schema 2020-12 format checker and have a paired Markdown report.
- The required offline evaluation wrapper passes **11 scorer, 5 runner-status and 6
  retained-evidence controls**, then re-scores all three retained runs byte-for-byte.
  `retained-run-controls.txt` records the output. Nonzero original findings are retained.
- All **48** repository gates passed through
  `python3 /workspace/scratch/run-with-subreaper.py bash scripts/ci/run-repo-gates.sh`.
  The session's PID 1 does not reap orphaned children; the subreaper supplies ordinary init
  behavior required by the unchanged autonomy cancellation test. The same native failure and
  subreaper success were established on the original baseline in the implementation phase.
  `repo-gates.txt.gz` retains this run; it is not presented as a native PID-1 pass.
  The identical session adapter is archived as `session-subreaper.py`; use that repository path
  in place of the original scratch path to reproduce in this kind of Linux environment.
- `Ashlar.LocalDevCore.slnf` restored and built at the validation commit with zero errors and
  two expected local-clone SourceLink warnings. The first offline attempt failed on uncached
  CLI dependencies; it did not reach build. The successful retry used the normal proxy/CA
  network path. Both logs are retained. NuGet vulnerability auditing was disabled; these
  are build checks, not dependency security audits.
- ShellCheck 0.9.0 at severity `error`, Bash syntax and `git diff --check` passed for the
  changed shell files. The full hosted Readiness matrix and networked lychee job were not
  run; no branch was pushed. Changed Markdown file links were checked separately.

All .NET commands use `scripts/test-in-container.sh`, a read-only source mount and disposable
clone. To reproduce the core build:

```bash
ASHLAR_DEVTEST_IMAGE=ashlar-devtest:slim bash scripts/test-in-container.sh \
  --ref 5b01673b02ba31808ccdd74be3ac5a1b350951c5 -- \
  'dotnet restore Ashlar.LocalDevCore.slnf -p:NuGetAudit=false && dotnet build Ashlar.LocalDevCore.slnf --no-restore -v minimal'
```

The full certification command is `NuGetAudit=false bash scripts/run-cert-gate.sh` through
the same container helper. Accuracy replay used the offline wrapper in the parent README;
the normal helper alone does not disable networking or install the session CA trust.

## Failures and mutation evidence

The initial full .NET 8 gate at `7bf31595b` ran **3,931 tests: 3,930 passed, 1 failed,
0 skipped**. The sole failure was `EgressGuardConventionTests.F1_no_file_has_more_outbound_paths_than_its_pin`
on the self-test's `HttpMessageInvoker`. `initial-net8/` retains its complete TRX/console
bytes and machine-readable summary. The subsequent correction removed that primitive;
it did not widen an exemption or raise an inventory allowance.

`wrapper-mutation/` restores the original frozen wrapper in a disposable committed clone:
**3/5 assertions fail**, then exact source restoration and empty porcelain precede **5/5 green**.
`verifier-mutation/` disables the single retained-file SHA comparison: **3/6 assertions fail**,
then exact restoration and clean porcelain precede **6/6 green**. These are actual Python tests;
their fake commands/fixtures are not GitHub snapshots. Transcript paths are original session
paths, subsequently archived here; the checkout itself is not included.

Eight .NET source mutations were killed with real assertion failures and passing restored runs:

| Mutation | Failed / executed on mutant | Restored passed / executed |
|---|---:|---:|
| Accept malformed PR identity | 5 / 36 | 36 / 36 |
| Keep stale legacy `ready` | 4 / 36 | 36 / 36 |
| Reverse pagination-link guard | 16 / 36 | 36 / 36 |
| Treat unverified provider names as proof | 6 / 26 | 26 / 26 |
| Lose required-pending failure classification | 8 / 26 | 26 / 26 |
| Drop required GitHub suite selector | 1 / 3 | 3 / 3 |
| Restore the unlisted self-test HTTP primitive | 1 / 1 | 1 / 1 |
| Infer missing requirements from partial CI | 2 / 36 | 36 / 36 |

The first six use commit `7bf31595b`; the last two and final gates use `5b01673b`.
`production-controls/` preserves exact patches, commands, TRXs, failed test names, source
restoration/empty-porcelain proofs and controller provenance. Its `archive-index.json` maps
original filenames to compressed copies with uncompressed hashes; `verified-counts.json`
independently checks actual TRX counters and all eight restored-source proofs. The initial controller's six
mutation cycles completed, then its obsolete final validation was stopped during build;
its original partial status remains. A conflicting container-name launch and a superseded
controller-command launch executed no tests and are not counted as validation. No ref shim
was installed and no test outcome or reported checkout was rewritten.

The verifier requires the original baseline and checks each present result directory. It does
not detect deletion of an entire later repaired-run directory, establish independent log retention,
or prove that the reviewers were blinded. Git commits and retained hashes supply local provenance
within the stated trust model.

Logs, patches and affected transcripts use gzip without a timestamp; they can be read with
Python's `gzip` module. `retained-byte-archives.json` maps whitespace-bearing proof files to
their compressed copies, preserving every original byte.
The retained file manifest binds bytes, not the truth of assertions or independent custody.
