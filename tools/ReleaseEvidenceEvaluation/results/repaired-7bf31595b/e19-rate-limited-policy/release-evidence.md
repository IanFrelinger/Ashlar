## Pull-request evidence snapshot

[`eval/fixture` PR #1](https://github.com/eval/fixture/pull/1)

GitHub verdict: **ready**. Scoped assurance: **unknown**.

Head: `d6dd1bc61244a980faab96218c961c728c9217ea`; base: `main` (`151d359e25133bb51c977a61f7244063d12c2dcb`).

Collected: 2026-11-02T02:01:00.1741107+00:00; schema 1.0; Ashlar `0.4.2+7bf31595b7b67940f62441ecb29d132c1561d0ec`; brick `1.3.1`.

- **pass**: A 40-character hexadecimal head commit was observed at collection start. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The final PR read agreed on head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/eval/fixture/rules/branches/main) [source](https://api.github.com/repos/eval/fixture/branches/main)

- **unknown**: Active rules were unavailable, malformed or at the pagination limit; absence of rules was not established. [source](https://api.github.com/repos/eval/fixture/rules/branches/main)

- **unknown**: Approval count alone does not establish current code-owner, last-push or stale-review requirements. [source](https://github.com/eval/fixture/pull/1)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
