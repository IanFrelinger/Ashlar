## Pull-request evidence snapshot

[`eval/fixture` PR #1](https://github.com/eval/fixture/pull/1)

GitHub verdict: **blocked**. Scoped assurance: **unknown**.

Head: `ee90d0ebec2b37ef365f5dc0b3af424400daf359`; base: `main` (`d2fc73fb8c742aae57722d9035fb44e5b6916da9`).

Collected: 2026-11-02T02:01:00.3414638+00:00; schema 1.0; Ashlar `0.4.2+7bf31595b7b67940f62441ecb29d132c1561d0ec`; brick `1.3.1`.

- **unknown**: The legacy verdict reports: The check `ci / unit` concluded failure. Its app/provider identity was not verified against the required check; this does not establish a failed requirement. [source](https://github.com/eval/fixture/actions/runs/200292)

- **pass**: A 40-character hexadecimal head commit was observed at collection start. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The final PR read agreed on head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/eval/fixture/rules/branches/main) [source](https://api.github.com/repos/eval/fixture/branches/main)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/eval/fixture/rules/branches/main)

- **unknown**: Required check app/provider bindings could not be verified; a matching check name alone is insufficient. [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/eval/fixture/rules/branches/main) [source](https://api.github.com/repos/eval/fixture/branches/main)

- **unknown**: Detected mandatory rule `required_deployments` is outside this collector's supported policy evaluation. [source](https://api.github.com/repos/eval/fixture/rules/branches/main)

- **pass**: GitHub's review decision, or a fully read zero-approval rule, establishes the review requirement. [source](https://github.com/eval/fixture/pull/1)

- **unknown**: Observations named `ci / unit` could not be bound to the required app/provider; their conclusions do not establish satisfaction or failure of that requirement. [source](https://github.com/eval/fixture/actions/runs/200291) [source](https://github.com/eval/fixture/actions/runs/200292)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
