# Bedrock default-region regression evidence

## Failure and controlled reproduction

PR #925 readiness [run 43458349596](https://github.com/IanFrelinger/Ashlar/actions/runs/43458349596)
at `49746fc02a45b242fd8be5306cb9f830d9d14ea8` failed two Linux Infrastructure net8 cases:
null and empty region input expected a null `RegionEndpoint`, but observed `us-west-2`.
The redirect prohibition passed before the region assertion failed. This was an assertion failure,
not a setup or network failure. The runner identified itself as AWS/us-west-2; the original process's
winning SDK provider and loaded SDK version were not retained and remain unknown.

The factory intentionally leaves an omitted region to the SDK default chain. Controlled reproduction
[run 43468101149](https://github.com/IanFrelinger/Ashlar/actions/runs/43468101149) at
`b50dc1660fdc42cbf025be258c187ea1935f9868` preserved the original factory and assertions byte-for-byte.
It ran fresh processes with existing empty synthetic profile files and metadata access disabled.
No AWS client was constructed and no real profile or credential contents were inspected.

| Controlled region | Original theory, .NET 8.0.34 | Original theory, .NET 10.0.15 | Observed provider |
|---|---|---|---|
| Absent | 4 passed | 4 passed | None; region stayed null |
| `us-west-2` | 2 passed, null/empty failed | 2 passed, null/empty failed | `EnvironmentVariableAWSRegion` |
| `eu-central-1` | 2 passed, null/empty failed | 2 passed, null/empty failed | `EnvironmentVariableAWSRegion` |

Six additional diagnostic tests passed: **12 TRXs, 30 results, 22 passed and 8 intended assertion
failures, zero skipped/aborted**. Every diagnostic factory observation retained
`AllowAutoRedirect=false`. Source hashes matched before/after and final git status was clean.
The loaded Core SDK was **4.0.102.11**, not the centrally declared 4.0.100.4; BedrockRuntime was
4.0.101.11. The observed mechanism demonstrates an environment-dependent null oracle, without
retrospectively identifying the original runner's provider or claiming a base-SHA reproduction.

Artifact `14060250024` is retained with the [reproduction run](https://github.com/IanFrelinger/Ashlar/actions/runs/43468101149):
194,961 bytes; SHA256 `dea6aa4310d4ac0bbc526b6862e373fc7f5aa3854a8e465a6e431cd2b72674ce`.
Its embedded evidence ZIP has SHA256 `25932661ba59de9a6ec0d037da47a0e29a815118c179d5e7e14a295bc0cfe26d`.
An earlier diagnostic run at `02fd93dc` stopped on an assertion-message parser mismatch; it is not
the complete reproduction evidence.

## Correction and acceptance checks

The correction is test-only. For omitted input, the test observes an ordinary SDK configuration's
default region **before** invoking the factory. Explicit-region and trimming checks remain, with
additional alternate-region and whitespace-only rows. All six rows still require redirects disabled.
The factory, SDK dependencies and production behavior are unchanged.

The `Bedrock region contracts` workflow runs `scripts/bedrock-region-reproduction.py <SHA> --regression`
through `scripts/test-in-container.sh`. It requires all six contract rows and the diagnostic probe
to pass in each of three controlled environments on each framework, then validates four mutations
per framework under `eu-central-1`: redirects enabled, explicit region ignored, trimming removed,
and an unspecified region forced to `us-west-2`. Acceptance requires exact failed rows, zero skips,
restored-green results, actual runtime receipts and restored source bytes for all eight mutations.
The historical reproduction mode belongs to the archived reproduction SHA; it is not the corrected
six-row regression mode. Workflow artifacts and the PR record supply observed correction results.

This is configuration-level evidence. It does not prove OS isolation, a protected deployment,
production readiness, or completion of #480. PR #925 remains draft while its existing integration
guard and other validation requirements remain unresolved.
