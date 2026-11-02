## Pull-request evidence snapshot

[`eval/fixture` PR #1](https://github.com/eval/fixture/pull/1)

GitHub verdict: **unknown**. Scoped assurance: **unknown**.

Head: `81d2a3305a8c57bbf01dadf5d5573be59f1be965`; base: `main` (`8e132bb2ea95c0215055c946c894ba47a382d63f`).

Collected: 2026-11-02T02:05:53.8270190+00:00; schema 1.0; Ashlar `0.4.2+b4b77b7f47975c64873dd8dc458329a584228d36`; brick `1.3.1`.

- **unknown**: Reviews: GitHub answered 403 Forbidden; response content is omitted. [source](https://github.com/eval/fixture/pull/1)

- **unknown**: Check runs: GitHub answered 403 Forbidden; response content is omitted. [source](https://github.com/eval/fixture/pull/1)

- **unknown**: Reviews: The reviews could not be read. [source](https://github.com/eval/fixture/pull/1)

- **unknown**: Check runs: The check runs on the head commit could not be read. [source](https://github.com/eval/fixture/pull/1)

- **pass**: A 40-character hexadecimal head commit was observed at collection start. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The final PR read agreed on head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/eval/fixture/rules/branches/main) [source](https://api.github.com/repos/eval/fixture/branches/main)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/eval/fixture/rules/branches/main)

- **unknown**: Approval count alone does not establish current code-owner, last-push or stale-review requirements. [source](https://github.com/eval/fixture/pull/1)

- **unknown**: The required check `ci / unit` was not observed in incomplete CI results; this does not establish whether it reported. [source](https://github.com/eval/fixture/commit/81d2a3305a8c57bbf01dadf5d5573be59f1be965)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
