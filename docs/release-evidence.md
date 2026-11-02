# Read-only pull-request evidence schema 1.0

The open `github.blockers` brick adds `evidencePack`, `evidenceMarkdown`, and `assurance`
outputs for a github.com pull request. Existing `verdict`, `blockers`, `unknowns`, `notes`,
`evidence`, and `answer` outputs retain their contract. Release-tag URLs retain those existing
outputs and return no PR evidence pack. The [PR-blockers Action](../actions/pr-blockers/README.md)
exports the JSON pack and its matching Markdown rendering; it remains the supported on-ramp.
No repository code is checked out or executed by the brick. Compliance mapping and organization-wide
operational packaging remain separate from these Apache-2.0 trust primitives.

## Contract

The [formal JSON Schema](schemas/release-evidence-1.0.schema.json) describes this contract. JSON field names use camel case. Schema `1.0` is an additive contract: consumers must ignore
unknown fields and reject unsupported major versions. Producers must keep unknown mandatory facts
out of passing assurance decisions.

| Field | Meaning |
| --- | --- |
| `schemaVersion` | `1.0` |
| `repository`, `pullRequest`, `url` | Canonical owner/repository, integer PR number, and source PR URL |
| `headSha`, `baseRef`, `baseSha` | Observed input identities; null means unavailable; supported GitHub commit identities are 40 hexadecimal characters |
| `collectionStartedAt`, `collectedAt` | UTC ISO timestamps bounding this sequential collection |
| `ashlarVersion`, `brickVersion` | Package assembly informational version (including build provenance when supplied) and brick contract version; `unknown` if no assembly version exists |
| `auditId` | Null in the brick. A host can correlate its per-call audit externally; this brick does not invent an audit record or authenticated actor |
| `scope` | One read-only PR on github.com; checks, reviews, and branch-rule observations |
| `authorization` | Credential presence, effective permissions (`not-enumerated`) and authenticated actor (`unknown`); never the credential itself |
| `verdict` | Existing GitHub-derived `ready`, `blocked`, `unknown`, `merged`, or `closed` |
| `assurance` | Conservative `pass`, `fail`, `unknown`, or `not-applicable` for the supported observed requirements |
| `findings` | Objects with per-snapshot `id`, `status` (`pass`, `fail`, `unknown`), `required`, `explanation`, and source URL array `sources` |
| `limitations` | What this collector did not inspect or establish |
| `freshness` | Final PR identity comparison and the revalidation contract |

A confirmed unsatisfied requirement gives `fail`. This includes a required check that is still pending,
or a required context proven absent from complete CI results, when its policy and provider requirement
are known. `fail` describes the requirement's current satisfaction, not necessarily a failed test run.
Partial or unreadable CI, unverified provider identity and unrecognized states remain `unknown`.
Otherwise a mandatory unknown gives `unknown`.
Only a completely observed supported scope can give `pass`; merged or closed PRs give
`not-applicable`. A legacy `ready` may coexist with `unknown` assurance when GitHub's mergeability
is known but detailed policy is unreadable. A `pass` concerns the limited observed scope. It never
authorizes a release or establishes regulatory compliance.

Each finding links the source endpoint or GitHub page it relies on. A link for an unknown is the
source that could not establish the fact, not proof that the fact passed. Explanations use bounded
inline-code formatting for untrusted GitHub names. The PR title, body, diff, file contents, and
artifact payloads are excluded. API error bodies are omitted to avoid copying arbitrary response
content into evidence.

## Conservative boundaries

Required checks are compared by exact context/name. A missing requirement, pending result,
unrecognized conclusion, contradictory same-name observations, or incomplete check/status response
cannot pass. Check-run collection follows at most 100 pages, only through HTTPS links on
`api.github.com` for the same repository/commit endpoint with pagination parameters. GitHub's
canonical `/repositories/{id}` form is allowed only for the ID in the PR base repository whose full
name matches the requested repository; unverified IDs are rejected. Unsafe or
repeated links, failed later pages, changing counts, overlapping or missing paginated run identities,
and unmatched totals leave collection incomplete; already observed check rows are retained. A page
explicitly reporting check rows for a different head is rejected. Commit-status counts also detect incomplete results.
Review listing uses Octokit's paginated list path. Branch rules
are requested with `per_page=100`; a response at that limit is unknown rather than assumed complete.
Malformed rule parameters are unknown. Active rule types this collector does not evaluate are
mandatory unknown findings. App-bound required checks are also unknown until their provider identity
can be established; omitted provider metadata is also unknown, while explicit null denotes an unbound provider.
A matching name alone establishes neither satisfaction nor failure of an app-bound requirement. The
legacy name-based verdict can therefore still be blocked while scoped assurance records an unknown.

Approving-review counts alone do not establish code-owner, last-push, or stale-review requirements.
GitHub's head-matching GraphQL review decision is used when available; otherwise an incompletely
observed review policy remains unknown. An approval for another or unknown commit is reported as
uncertain when current eligibility cannot be established. Unknown effective token scopes remain
explicit even when individual reads succeed. Failed reads, rate limits, timeouts, malformed responses,
and unavailable permissions never establish an empty requirement list.

An optional `collectionCheckName` input identifies this collector's exact check name. The caller
must supply the actual display name; job IDs and display names can differ. A provably optional
collection check remains advisory. A provably required, pending self check is circular and currently
unsatisfied, so complete observations give `fail`; this does not waive that requirement or solve the
circular workflow. When policy or observations cannot establish its requirement or state, assurance
remains `unknown`.
The legacy verdict still reports GitHub's existing check semantics. This input does not waive any
requirement or grant permission to merge.

A final PR read compares head SHA, base SHA/reference, draft/open state and mergeability with the
initial read. If either commit identity is missing or malformed, or the PR changes or cannot be re-read, assurance is unknown and original evidence is
kept under the original head. This cannot detect every concurrent check, review or rule update.
An otherwise `ready` legacy verdict also becomes `unknown` when the final read changes or cannot
verify the initial facts. Confirmed blockers and merged/closed semantics remain unchanged. Malformed
API-owned PR identity URLs and branch references are reported as unknown; display titles alone do not
invalidate identity and are not copied into the pack.
Even a stable, matching commit is a time-bound snapshot: recollect when relevant facts might have
changed. No fixed validity period or atomic GitHub snapshot is promised.

## Operations and verification limits

Use a repository-scoped read-only GitHub token in the supported host configuration, outside model
arguments when possible. Anonymous collection is possible for public repositories but has lower
rate limits and cannot establish every policy fact. No customer repository access is implied by
this documentation. Collection requires several sequential API calls, plus up to two 2-second waits
for mergeability; network latency, pagination, throttling and timeouts add to that. Only github.com
is supported; GitHub Enterprise hosts are rejected.
The existing release-tag path makes one extra repository-metadata read only when a canonical-ID
pagination link needs that identity; an unavailable or mismatched lookup leaves the remaining pages unknown.

This schema and Markdown report are unsigned observations. A JSON file on disk does not establish
signer identity, retention, or tamper resistance. Host audit chains have their own documented
boundaries and require an independently retained anchor to detect removed tails or entire sessions.
Optional [signed exports and externally retained anchors](release-evidence-integrity.md) provide a separate integrity path; the bare brick output is unsigned.
Artifact access and retention are the repository operator's responsibility. Check names, source
URLs, commit identities and review metadata can still reveal private project information; publish
or retain them only under the operator's access policy.
