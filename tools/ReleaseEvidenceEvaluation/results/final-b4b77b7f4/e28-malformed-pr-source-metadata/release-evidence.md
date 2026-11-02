## Pull-request evidence snapshot

[`eval/fixture` PR #1](https://github.com/eval/fixture/pull/1)

GitHub verdict: **unknown**. Scoped assurance: **unknown**.

Head: `0d01ba026aa717328b476d9dc35646a13e4231bf`; base: `main` (`8a3b1472997bfb8d8468ce89ec96e4b6d78bd487`).

Collected: 2026-11-02T02:05:53.9499094+00:00; schema 1.0; Ashlar `0.4.2+b4b77b7f47975c64873dd8dc458329a584228d36`; brick `1.3.1`.

- **unknown**: Pull-request metadata: GitHub returned malformed or mismatched PR identity metadata; its source URL or branch references could not be verified. [source](https://github.com/eval/fixture/pull/1)

- **pass**: A 40-character hexadecimal head commit was observed at collection start. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/eval/fixture/pull/1)

- **unknown**: The final PR read changed or could not establish the initial head, base and mergeability facts; collect again. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/eval/fixture/rules/branches/main) [source](https://api.github.com/repos/eval/fixture/branches/main)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/eval/fixture/rules/branches/main)

- **pass**: GitHub's review decision, or a fully read zero-approval rule, establishes the review requirement. [source](https://github.com/eval/fixture/pull/1)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
