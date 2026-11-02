# Release-evidence evaluation plan

Status: **executed on 2026-11-02**, with frozen inputs and two separately contextualized AI
reviewers; no human/customer review is claimed. The [evaluation report](release-evidence-evaluation-2026-11-02.md)
retains original failures, repair results and remaining gaps. This plan is for the open
read-only PR evidence pack. The existing [blockers accuracy study](blockers-accuracy.md)
does not establish this pack's assurance accuracy or cover the failure cases below.
Unit tests and a stub MCP server validate development behavior; they are not user research
or an independent accuracy evaluation.

## Dataset and ground truth

Use at least 30 fixed snapshots, initially synthetic or from public repositories. Never use
customer/private repository evidence without the owner's and customer's authorization.
Capture the raw responses needed to reproduce each answer under the observed token scope,
plus a compact manifest containing repository/PR, initial and final head/base identity,
collection interval, endpoint/result inventory, and sanitized authorization context. Retain
only necessary metadata; exclude tokens, PR bodies, code and artifact payloads.

An independent reviewer determines the expected legacy verdict, scoped assurance, mandatory
requirements, blockers, unknowns and source references **before seeing Ashlar's result**.
Use GitHub's UI and policy/API sources, not the production collector's inferred labels or
the prior benchmark's same-source rubric. A second reviewer adjudicates conflicts and
records rationale. Cases whose policy cannot be established get an explicit unknown
ground-truth label; missing permission is never labeled ready by absence of a visible rule.
Synthetic cases require a reviewer to inspect their intended policy and exact responses.

The following preregistered minimum allocation was implemented as 30 synthetic cases, with
six additional public captures across three repositories. See the report for frozen manifests:

| Cases | Count | Required variation |
|---|---:|---|
| Ready within the documented scope | 4 | No requirements; required CI success; GitHub-approved reviews; mixed optional CI |
| Observed blockers | 6 | Required CI failure; merge conflict; draft; review required; changes requested; behind protected base |
| Pending/missing requirements | 4 | Queued check; missing required context; conflicting same-name results; check with no conclusion |
| Missing/restricted authorization | 4 | Anonymous GraphQL unavailable; policy 403; private PR 404; CI/review endpoint denied |
| API and partial-response failures | 4 | Rate limit; timeout; malformed policy response; truncated/paginated response |
| Freshness and changed facts | 4 | Head changes during collection; base changes; approval for another commit; checks/reviews change without a new head |
| Hostile/malformed text | 2 | Markdown/workflow-command payload in check metadata; malformed PR metadata or source URL |
| Unsupported policy and own check | 2 | App-bound/unsupported mandatory rule; collection check required or policy unreadable |
| **Total** | **30** | Expand after finding a new failure class; keep failures in the dataset |

Use several public repositories, PR sizes, CI providers and review policies where available;
record which rows are public captures and which are synthetic. Do not silently replace a
difficult public case with a simpler fixture. A workflow that needs a separately authorized
test repository must remain marked pending until that repository is available.

## Scoring and delivery

Run the deterministic collector and renderer against the fixed responses, retain JSON and
Markdown, and compare findings with the independently reviewed expected requirements.
Report counts and denominators for missed blockers, incorrect blockers, false-ready legacy
verdicts, false-passing assurance, unknown results and collection/export failures. Keep legacy
GitHub verdict and conservative assurance metrics separate. Aim for zero false-ready and zero
false-passing assurance cases; disclose every failure and the limits of a 30-case sample.
After repairing a finding, retain the original result and report the rerun's commit separately.

Record clean installation time, restore/build time, API collection time, and total time to
produce both artifacts. Identify machine/OS, runtime, cache state, commit/version, network
conditions and retries. Do not present synthetic timing as customer productivity savings.
If an authorized pilot later becomes available, compare with that team's actual review
workflow and record disagreements; do not infer that comparison from the benchmark.

Acceptance for this gate requires the dataset/ground-truth manifest, reviewer/adjudication
record, reproducible commands, exact implementation commit, result artifacts and a disclosed
summary. Passing it supports the evidence-pack evaluation gate only. Design-partner
marketing/pilot and autonomy claims retain their separate [hold](../dogfood-scorecard.md).
