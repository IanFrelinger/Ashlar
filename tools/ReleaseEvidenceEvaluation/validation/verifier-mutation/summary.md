# Retained-verifier mutation check

Candidate: `7bf31595b7b67940f62441ecb29d132c1561d0ec` in disposable `checkout/` only.
No .NET, live APIs, shared source edits, or frozen-input edits were used.

`mutant.patch` changes exactly one source line: `checked_file`'s content-SHA256
equality check becomes `require(True, ...)`. Digest syntax and file-existence checks
remain. The exact diff is also in `source-flip.stdout.txt`.

| State | Tests run | Assertion failures | Runtime/import/compile errors | Exit |
|---|---:|---:|---:|---:|
| Mutant | 6 | 3 | 0 | 1 |
| Restored commit | 6 | 0 | 0 | 0 |

Two mutant failures directly prove the source-manifest and public-capture metadata
changes were no longer refused. The third observes canonical-label tampering reaching
a later annotation-hash guard rather than the expected canonical-freeze guard. All
are unittest assertion failures; the suite imported and ran its actual six tests.

After `git restore --source=HEAD -- tools/ReleaseEvidenceEvaluation/verify_retained.py`,
`git diff --exit-code` passed and porcelain output was empty both before and after the
restored six-test run. Original and restored source SHA256 are identical:
`81b2f8c35dbcbaf95bf01c4ae11ff825e4d819e3d56d87c39297c1cc29684b81`.

Exact argv, working directories and exit codes are in `commands.json`. Each command's
stdout and stderr is retained separately. `summary.json` records the machine counts.
