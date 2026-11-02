## Pull-request evidence snapshot

[`eval/fixture` PR #1](https://github.com/eval/fixture/pull/1)

GitHub verdict: **blocked**. Scoped assurance: **fail**.

Head: `15c780bf53d3fb1249b6291702668dc62e91ae10`; base: `main` (`61fda2d15fad717e0e3d9fc1f4d0dd31c9ff70aa`).

Collected: 2026-11-02T02:01:00.0524756+00:00; schema 1.0; Ashlar `0.4.2+7bf31595b7b67940f62441ecb29d132c1561d0ec`; brick `1.3.1`.

- **fail**: The branch is behind `main`, and the base branch requires branches to be up to date. [source](https://github.com/eval/fixture/pull/1)

- **pass**: A 40-character hexadecimal head commit was observed at collection start. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The final PR read agreed on head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/eval/fixture/rules/branches/main) [source](https://api.github.com/repos/eval/fixture/branches/main)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/eval/fixture/rules/branches/main)

- **pass**: GitHub's review decision, or a fully read zero-approval rule, establishes the review requirement. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Observed required check `ci / integration` has a passing conclusion. [source](https://github.com/eval/fixture/actions/runs/200100)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
