# Additional public operational-clean PR captures

Three public, open PR metadata snapshots were captured on **2026-11-02**, before followup
candidate execution or expected-label review. These observations supplement, and do not modify,
the original frozen 36-case evaluation. The data author produced no expected verdict, assurance,
or blocker labels. Independent review must determine those from the retained facts.

| Case | Public PR | UTC collection interval | Check pages / runs |
|---|---|---|---:|
| public-ready-01 | [cli/cli#14716](https://github.com/cli/cli/pull/14716) | 03:05:42.454444–03:05:46.214381 | 1 / 16 |
| public-ready-02 | [jqlang/jq#3432](https://github.com/jqlang/jq/pull/3432) | 03:05:46.218258–03:05:49.409869 | 2 / 32 |
| public-ready-03 | [jqlang/jq#3641](https://github.com/jqlang/jq/pull/3641) | 03:05:49.415316–03:05:52.292477 | 1 / 29 |

Both bracketing PR reads reported `open`, non-draft, `mergeable: true` and
`mergeable_state: clean`. Head SHA, base SHA/reference and branch tip observations stayed
stable within each capture. All 77 observed check runs were completed with success or skipped
conclusions. These are source facts used for selection, **not assurance-pass labels**.

Selection began with recent/open metadata from Ashlar, cli/cli and dotnet/runtime, then a
public-only successful-CI/approved-review search over cli/cli, BurntSushi/ripgrep,
sharkdp/bat, jqlang/jq and astral-sh/uv. `discovery.json` retains inspected PR identity/state
metadata, the search criteria and rejected alternatives. No candidate outputs were used to
select or replace a case. Search matches were re-read through PR metadata; search alone was
not treated as ground truth. Public bodies returned by generic discovery tools were discarded.

## Permission and transport limits

The GitHub plugin was used first for discovery. Successful fetch content exposed no HTTP
status or pagination headers. Its GraphQL URL request was rejected locally with 400
`INVALID_ARGUMENT`; that is a connector limitation, not a GitHub denial.

All final case routes therefore use one consistent Python HTTP transport through inherited
proxy and CA trust. The capture code supplied no Authorization header, read no credentials,
and persisted no credentials. Current cloud readiness reported enforced unrestricted HTTP
policy and no configured secrets or outbound identities. Proxy upstream authentication is
not observable: do **not** describe this as proven anonymous GitHub access or known token scopes.
`credentialSupplied: false` records caller/capture behavior only.

Both required-policy subresources returned actual **403** in all three cases. Active rules
and the original read-only GraphQL query returned 200. No denied policy was replaced or
inferred empty, and no fully observed passing assurance is asserted. Actual response status,
pagination/rate-limit/request-ID/cache headers and the precise query/results remain retained.

## Retained evidence and reuse

Each case has `capture.json`, a compatible offline `routes.json`, and `sha256.json`.
`manifest.json` freezes identities, intervals, observed counts/statuses and hashes; its SHA256 is
`84191b5f1fa6f5acc7743c8ed0ce8852ed6e6a1f2df0c97513880f31e599fc2d`.
`integrity-check.json` records structural/privacy checks. Metadata allowlists are identical to
the original capture path: no PR/review bodies, diffs, code, check output, artifacts, full
profiles or contact details are retained. API field names, envelopes and retained values are
unchanged. Actual Link pages were followed with no retries; PR cases run sequentially with up
to four independent endpoint requests in parallel.

The source is `capture-public-ready.py`, a selection-only copy of the original capture script.
It uses the same aliased GraphQL request as the original evaluation contract and refuses to
overwrite existing case directories. Running it again requires a new output directory and
creates new live facts. Stable bracketing identities do not establish an atomic check/review/
rule snapshot. GraphQL requests first 100 contexts without pagination, a retained limit.

`baseline-observations.json` is separate read-only current-master/CI/issues/bus context and is
excluded from these accuracy cases. At master `5e0beeae`, returned checks show cert-gate,
build-core, lychee and Readiness success, while `shell-lint` was not found in the returned
checks or combined statuses and remains unverified. This does not validate local unpushed
changes. No bus message, PR, release, workflow dispatch or repository mutation was made.
