# Public snapshot capture

Six real public PR metadata snapshots were frozen at
`2026-11-02T00:54:26.456672Z`, before candidate execution or expected labels.
The capture agent did not inspect the production collector, tests, or candidate
outputs and did not write ground-truth labels.

| Case | Repository / PR | Collection interval (UTC) | Check pages / runs |
|---|---|---|---:|
| public-01 | [IanFrelinger/Ashlar#900](https://github.com/IanFrelinger/Ashlar/pull/900) | 00:53:31.973718–00:53:35.419739 | 2 / 40 |
| public-02 | [IanFrelinger/Ashlar#853](https://github.com/IanFrelinger/Ashlar/pull/853) | 00:53:35.426966–00:53:38.705033 | 2 / 53 |
| public-03 | [cli/cli#14781](https://github.com/cli/cli/pull/14781) | 00:53:38.711335–00:53:41.901333 | 1 / 18 |
| public-04 | [cli/cli#14845](https://github.com/cli/cli/pull/14845) | 00:53:41.905218–00:53:44.984971 | 1 / 11 |
| public-05 | [dotnet/runtime#136790](https://github.com/dotnet/runtime/pull/136790) | 00:53:44.988399–00:53:49.159789 | 4 / 97 |
| public-06 | [dotnet/runtime#136812](https://github.com/dotnet/runtime/pull/136812) | 00:53:49.168412–00:53:53.861103 | 5 / 145 |

All dates above are 2026-11-02. Selection used two open PRs per repository from
recent/open metadata, with draft/non-draft variation. These public cases supplement
the separate synthetic corpus. No public case was replaced with synthetic data.

The immutable raw metadata and endpoint inventories are in
`public/public-01/` through `public/public-06/`. Each contains `capture.json`, the
offline route case `routes.json`, and `sha256.json`. `public/manifest.json` records
initial/final head and base identity, branch tip identity, collection intervals,
source URLs, actual statuses, pagination counts, and file hashes. The structural
privacy/hash checks are recorded in `public/integrity-check.json`.

## Access and provenance

Connected GitHub fetch was tried first. It returned raw PR/check metadata and an
actual upstream 403 for a branch-protection endpoint, but exposed no successful
HTTP status or pagination headers. Its GraphQL URL attempt was rejected by the
connector allowlist with local 400 `INVALID_ARGUMENT`; this was not a GitHub denial.
`public/connector-probes.json` preserves those capability distinctions. Connector
probe responses are not substituted into the final case routes.

All final case requests use the same Python HTTP transport through inherited
`HTTP_PROXY`/`HTTPS_PROXY` and inherited CA trust. The capture code supplied no
`Authorization` header, read no credentials, and persisted no credentials. Current
cloud readiness reported enforced unrestricted HTTP policy and no configured
credentials or outbound identities. The proxy's upstream identity is not
observable. Rate-limit headers and successful GraphQL are compatible with proxy
credential injection, so these captures must not be described as proven anonymous
GitHub access or as observations under a known token scope. Effective permissions
remain unknown. `credentialSupplied:false` records that no token was supplied by
the caller/capture code; it does not assert anonymous upstream authentication.

Actual response status, `Link`, date, rate-limit, request-ID, retry, content-type,
cache, and exposed OAuth-scope headers are retained when returned. Both required
branch-protection subresources returned actual HTTP 403 in all six cases. Active
branch-rule and GraphQL reads returned HTTP 200. Missing permission is preserved;
no policy is invented or inferred from a denial. The exact read-only GraphQL query
supplied by the replay agent and its original aliased JSON response are frozen.

Each PR was collected in sequence, with at most four concurrent independent
metadata requests inside that case. Initial and final PR metadata and branch
summaries bracket collection. Their recorded head/base and branch tip identities
were unchanged. This establishes those observations during the interval; it does
not make checks, reviews, and policy an atomic snapshot or exclude same-head
changes. No retries occurred. Actual REST `Link` pages were followed until there
was no next page. The GraphQL query requests the first 100 contexts without
pagination, a separate completeness limit retained in the raw request.

## Retained metadata and omissions

Retained API field names, response envelopes, statuses, and values are unchanged.
Metadata allowlists omit PR bodies, code, diffs, patches, artifacts, full user
profiles/contact information, review bodies, all check `output`, and unused
repository/app/commit details. Review user login, state, commit ID, submitted time,
and canonical URL are retained. Check identity, head SHA, state, conclusion, times,
app identity, and canonical URLs are retained. Policy/rule and query-limited
GraphQL JSON are retained without reprojection. Every omission is listed in each
capture's `sanitization` record. There are no synthetic replacements or privacy
substitutions of retained values.

`public/pilot-transport/` is a real preliminary transport capture, explicitly
excluded from scoring. It predates the replay request contract and uses a smaller
GraphQL query and explicit per-page parameters. Final cases were recollected in
full using the supplied request contract; the pilot was retained rather than
silently discarded.

## Recollection

The source is `public/capture-public.py`. It refuses to overwrite an existing
case directory. A fresh output directory or an explicit archival operation is
needed to run it again. Recollection creates new live evidence, not a reproduction
of the frozen facts. Offline replay uses only the frozen `routes.json` files.

The capture environment was Linux with Python 3.12.14; the exact platform string is
in `public/manifest.json`. Per-case live API capture durations are recorded there.
They exclude earlier tool discovery, connector probes, and the pilot and must not
be presented as customer productivity measurements.
