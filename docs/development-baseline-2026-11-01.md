# Development baseline — 2026-11-01

Reviewed default branch `master`, commit `5e0beeae3b037508f23b475d98d13cc7e64c25de`
(2026-10-29). GitHub's repository/branch API and a fresh clone agreed; the clone initially had
no working-tree changes. No `AGENTS.md` was found. `CLAUDE.md`, the context skill, licensing,
open-core boundary, commercial decisions, scorecard, ledger, workflow sources and agent-bus
were read. The release workspace's retained readiness handoff describes an older August
environment; it is not evidence about this execution environment. No bus message, issue
mutation, push, merge, release, deployment or customer outreach was performed for this review.

This is a bounded development baseline, not a security audit or a release decision.

## Readiness and claims

**Autonomy and design-partner marketing/pilot remain HOLD.** Internal read-only prototype,
evidence-pack evaluation, design-partner pilot/marketing and autonomy claims are separate gates.
The October commercial offer/pricing decisions do not explicitly waive the scorecard hold.
No customer adoption or compliance result was established.

| Claim | Supported surface/configuration | Implementation | Test or observed run | Known limitation |
|---|---|---|---|---|
| Explain GitHub PR blockers | One GitHub.com PR; REST/GraphQL access determines coverage | `GitHubBlockersBrick`, existing `pr-blockers` Action | Existing `GitHubBlockersBrickTests`; see local validation record below | Existing GitHub verdict may be ready while policy is unreadable; a conservative assurance decision must remain separate |
| Record MCP calls | Allowlisted bricks in the stdio host | `BrickMcpToolContributor`, `McpBrickAuditChain` | Existing host/audit tests; source reviewed | Host registration does not certificate-check bricks; stdio caller identity is unknown; brick can run before an audit failure |
| Detect audit-chain edits | A retained session chain with known links | `McpBrickAuditChain.Verify` | Existing `McpBrickAuditChainTests`; source reviewed | No independent anchor means tail/session deletion or whole-chain rewriting is not established by chain verification |
| Certify the bytes that execute | Gate-owned compiler/artifact path | `BrickCertificationProjectLoader`, `GateEmittedArtifactCompiler`, trust verifier | Issues [493](https://github.com/IanFrelinger/Ashlar/issues/493) and [462](https://github.com/IanFrelinger/Ashlar/issues/462) remained open at review; current reproduction recorded separately | Open issue status alone establishes neither current exploitability nor a complete repair |
| Persist and strictly re-read held certification evidence | Configured `EvidenceArchiveDirectory`; archive separate from admission store | `CertificationEvidenceArchive`, harness before hold, `SweepMode` | [Oct 30 dogfood run](https://github.com/IanFrelinger/Ashlar/actions/runs/41050179155), record digest checked below; existing archive tests | Pipeline instance uses temporary CI Ed25519 key, unpinned, development HMAC; not operator custody or unattended admission |
| Pin certification signers | Trusted public key set plus optional required-pinning switch | `CertificationTrustPolicy`, `ASHLAR_CERT_TRUSTED_ED25519_KEYS`, `ASHLAR_CERT_PINNING_REQUIRED` | Existing `TrustedKeyPinningConfigurationTests`, pinned hot-swap and wiring tests; source reviewed | Static Strict preset alone is unpinned; no attended operator-key pass established here |
| Enforce execution isolation | Only the host/transport that implements it | `AgentExecutionIsolation.md`; explicit Docker sandbox components on selected autonomy paths | Source/documentation review | Naming a tier does not create containers; default in-process transport is not container isolation; gateway cannot control bypass calls |
| Keep tasks across restart | Some components have checkpoint/record stores | Existing checkpoint abstractions and stores | Source/documentation review | A2A server's current tasks are synchronous and in-memory; durable A2A task recovery/streaming remain deferred |
| Verify private mesh imports | Receiver trusts origin key, checks signatures/hashes, applies local policy | Private mesh demo/package import | Full readiness CLI image job's private-mesh smoke passed | Receiver uses origin's signed gate results; it does not rerun origin tests or execute a post-apply canary |

Test names above locate existing coverage; they do not assert a new local pass. Exact commands
and outcomes belong in the local validation record, including unsuccessful attempts.

## Remote CI actually inspected

GitHub REST `actions/runs?head_sha=5e0beeae3b037508f23b475d98d13cc7e64c25de&per_page=100`
returned 14 runs on 2026-11-01. These are remote results for the baseline commit, not results
for the new working tree. Relevant completed successes:

| Workflow | Run | Observation |
|---|---|---|
| Full Platform Readiness Gate | [40925431246](https://github.com/IanFrelinger/Ashlar/actions/runs/40925431246) | Job steps inspected: Linux, macOS, Windows, Debian/Alpine/Ubuntu containers and four image jobs succeeded. CLI image private-mesh smoke succeeded. Summary log annotation: `verdict=verified lanes_ran=4 lanes_skipped=0`. |
| Cert gate | [40925431365](https://github.com/IanFrelinger/Ashlar/actions/runs/40925431365) | API reports success; full log/test artifacts not re-executed locally by this baseline review. |
| Build Gate | [40925431561](https://github.com/IanFrelinger/Ashlar/actions/runs/40925431561) | API reports success. |
| Docs Link Check | [40925431270](https://github.com/IanFrelinger/Ashlar/actions/runs/40925431270) | API reports success. |
| RC Gate | [41307446575](https://github.com/IanFrelinger/Ashlar/actions/runs/41307446575) | Scheduled Nov 1 run; API reports success. |
| Dogfood Continuous Proof | [41050179155](https://github.com/IanFrelinger/Ashlar/actions/runs/41050179155) | Actual job log inspected, not inferred from workflow status; held fixture pass and record citation below. |

The other eight results include successful UAT, production-readiness, container-publish and
onboarding runs plus Dependabot update runs (two cancelled). This is not a claim that every
check in the repository ran. Required branch contexts observed were `cert-gate`, `build-core`,
`shell-lint`, `lychee (README + docs)` and `Readiness summary`; a context requirement is not
itself a result. No new CI was dispatched.

## Dogfood evidence and contradictions reconciled

The scorecard previously checked “thresholds met” and “real hygiene PR proof” while its status
said no real PR existed. Those indicators are now explicit unmet criteria. Its September
statement that no persist/re-read verifier or production pinning assignment existed is stale:
the current archive and trust-policy implementations support both. September ledger rows
continue to describe their historical runs and are not retroactively promoted.

The Oct 30 run log records `CertifiedButHeld`, 18.0 seconds, record
`.ashlar/campaign/20261030-114403/records/rgb-hex-parse.json`, signer
`ed25519:cc192ceea4b97d6b`, `pinned=false`, `dev-hmac=true`. Artifact
`dogfood-ledger-41050179155` (ID `12974780877`, expires 2026-11-29) was downloaded and
inspected. Its ZIP SHA-256 matches GitHub's advertised digest:
`80becdfc16ebd550a8a299f62ef8ff072cc628f16f2198b160dd56584d65c6d9`.
The included record's SHA-256 matches the sidecar and run citation:
`4a3472a6e7ea452b8d128718765275ee2e8c335aa0ca87c8ae00a09fb4059461`.
It contains schema version 2, an Ed25519 public key/signature and a verification sidecar
reporting `verified=true`. This review checked bytes and provenance; it did not independently
run the Strict verifier or establish trust in an operator-held key. It does not add seven
reviewed ledger entries, establish a current threshold window, or satisfy custody/real-PR gates.

The ledger's claim that scheduled CI automatically updates the tracked ledger was incorrect:
the workflow prepares artifacts with read-only repository permissions and requires human
review/landing. The planned design-partner CTA and commercial plan now explicitly carry the
unchanged hold. No owner decision was invented or hold revised.

## Existing release-manager consumer

The separate [Release Manager](https://github.com/IanFrelinger/ashlar-release-manager) default
branch `main` was inspected at `b3ab7dd4453ccb3b702a034da76f4fe49d6f3e52`. It pins
`Ashlar.Hosting.Bundle` **0.1.1** through central package management, restores from nuget.org,
and hosts four deterministic background agents through `AddAshlar`: repository monitor, smoke
test runner, local SLO artifact collector and local report generator. Its configuration names
local `.ashlar/` evidence paths; neither the thin host nor config implements this handoff's
GitHub PR snapshot schema, conservative assurance decision or GitHub API collection contract.
It was source-reviewed, not run or upgraded. Extend the existing blockers/Action path; do not
introduce another overlapping release-manager application. The generic pack and verifier
belong in the open tier under D4 and the licensing covenants; a new compliance-mapped commercial
product remains distinct.

## Open work reviewed

The open-PR collection on 2026-11-01 returned five PRs: [#900](https://github.com/IanFrelinger/Ashlar/pull/900)
(Microsoft extensions updates), [#853](https://github.com/IanFrelinger/Ashlar/pull/853)
(embedded workflow safeguards), [#852](https://github.com/IanFrelinger/Ashlar/pull/852)
(signed artifact runtime portability), [#844](https://github.com/IanFrelinger/Ashlar/pull/844)
(PR Blocker Assistant) and [#840](https://github.com/IanFrelinger/Ashlar/pull/840)
(test SDK/xunit updates). Their titles and relevant bodies/file inventories were read; their
reported test results were not adopted as baseline results.

PR #844 at `bc36efa5030dc676155acaa7f0f55c0379bf2738` adds a separate read-only CLI/custom
brick pinned to Ashlar 0.3.0, with direct Octokit collection. Source review found no mutating
GitHub API calls, but it does not reuse the current `github.blockers` implementation, does
not establish required-check/review policy, retains check output summaries and calculates
`ready` from the absence of listed blockers even after a caught check-run read failure.
Those semantics do not meet this pack's conservative assurance contract. Its ten-assessment
plan and proposed time-savings target are not measured customer results. Keep this work on
the existing blockers/Action path, without adding another consumer application or silently
merging/replacing #844.

PR #853 at `8b9fda6a16e3bde4b9051e1ae385e813c0df87f6` proposes verified deployment hosting,
operation-intent/recovery and MCP response-boundary changes. It overlaps the host's
`Program.cs` and trust-policy wiring; integration must review those changes together rather
than treat the unmerged branch as shipped. PR #852 at
`eee59f2253b9d44cb699f8fbbe04df11d6e8d00f` proposes a synthetic signed-artifact actual-runtime
matrix; its portability evidence is distinct from a real gate admission or operator-custody
proof. No source from either PR was merged into this working tree.

The bus read returned 183 comments through the Oct 23 rc1 follow-up. Relevant open claims
concerned the SPEC-007 mutation campaign and critical lineage/locking/persistence/YAML defects;
no claim naming the blockers/Action, scorecard reconciliation, signed snapshot exporter or new
focused regression files was observed. This is a coordination snapshot, not a lock against
future work. No external coordination message was sent.

## Dependencies and next gates

- Local validation and adversarial reproductions require the repository's Linux devtest
  container path; record setup failures rather than substituting host .NET results.
- The prior blockers study contains 44 public PRs but uses same-source fixtures/rubric and
  reports its sampling limits. It does not satisfy the requested independently reviewed
  evaluation of at least 30 varied snapshots with permission/failure cases. No new accuracy
  measurement or false-ready rate is claimed here.
- P3 still needs the operator identity bridge, an attended owner-key pass with its intended
  public key alone pinned, reviewed dated pipeline records/threshold windows and a real
  human-reviewed Ashlar PR through the loop. Elapsed time, owner custody and human review
  cannot be synthesized from fixtures or temporary CI keys. Keep held admission containment.
- Customer repositories/data, pilot comparison and outreach await explicit owner/customer
  authorization; no customer results were manufactured. Publishing, merging, releasing and
  production configuration changes remain outside this local implementation review.
- Durable selected-workflow tasks follow P1; framework-client compatibility follows the
  supported protocol versions. Fleet expansion and receiver-side reruns require a concrete need.

## Local validation record

This baseline review ran `git status --short --branch`, `git rev-parse HEAD`, repository
instruction discovery, `scripts/ctx.py` queries and bounded source reads in the fresh
`/workspace/Ashlar` clone on Linux. Remote reads used the connected GitHub tools. Artifact
inspection used Python `zipfile`, `json` and SHA-256 and matched both digests above.
No host `dotnet` was run. Focused implementation tests, adversarial reproduction outcomes
and final repository checks are recorded with the implementation delivery; remote green
results above must not be reused as validation of uncommitted changes.
