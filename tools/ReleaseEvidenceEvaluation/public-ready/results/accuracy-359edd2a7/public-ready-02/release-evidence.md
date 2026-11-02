## Pull-request evidence snapshot

[`jqlang/jq` PR #3432](https://github.com/jqlang/jq/pull/3432)

GitHub verdict: **ready**. Scoped assurance: **pass**.

Head: `65f4b31f9148dced73f782ff21c024539776eae6`; base: `master` (`ac35c7dcbb991fa0f3c3196dd00ca479211e9847`).

Collected: 2026-11-02T03:35:32.7359301+00:00; schema 1.0; Ashlar `0.4.2+359edd2a731a43c7f14f31233ca312b96a0a5bb0`; brick `1.4.0`.

Collection attempts: 1; final PR comparison: stable.

- **pass**: A 40-character hexadecimal head commit was observed for this snapshot's collection attempt. [source](https://github.com/jqlang/jq/pull/3432)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/jqlang/jq/pull/3432)

- **pass**: The final PR read agreed with this attempt's head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/jqlang/jq/pull/3432)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/jqlang/jq/branches/master/protection/required_status_checks) [source](https://api.github.com/repos/jqlang/jq/branches/master/protection/required_pull_request_reviews) [source](https://api.github.com/repos/jqlang/jq/rules/branches/master) [source](https://api.github.com/repos/jqlang/jq/branches/master)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/jqlang/jq/rules/branches/master)

- **pass**: GitHub's review decision, or a fully read zero-approval rule, establishes the review requirement. [source](https://github.com/jqlang/jq/pull/3432)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, legacy-status provider identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
