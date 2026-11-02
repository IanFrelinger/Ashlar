## Pull-request evidence snapshot

[`cli/cli` PR #14781](https://github.com/cli/cli/pull/14781)

GitHub verdict: **blocked**. Scoped assurance: **fail**.

Head: `2e734c0e0a194e9b323e1b2aea98aea16b0f49d7`; base: `trunk` (`a81663f7d80d620b092dab3056778a79ff3e279c`).

Collected: 2026-11-02T01:09:06.6793329+00:00; schema 1.0; Ashlar `0.4.2+47a7c8506b89670d977265ce342a962d35858cd5`; brick `1.3.0`.

- **fail**: The pull request is a draft. [source](https://github.com/cli/cli/pull/14781)

- **fail**: GitHub reports that an approving review is required (review decision REVIEW_REQUIRED); the pull request has 0 approving review(s). [source](https://github.com/cli/cli/pull/14781)

- **pass**: A 40-character hexadecimal head commit was observed at collection start. [source](https://github.com/cli/cli/pull/14781)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/cli/cli/pull/14781)

- **pass**: The final PR read agreed on head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/cli/cli/pull/14781)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/cli/cli/branches/trunk/protection/required_status_checks) [source](https://api.github.com/repos/cli/cli/branches/trunk/protection/required_pull_request_reviews) [source](https://api.github.com/repos/cli/cli/rules/branches/trunk) [source](https://api.github.com/repos/cli/cli/branches/trunk)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/cli/cli/rules/branches/trunk)

- **unknown**: Detected mandatory rule `copilot_code_review` is outside this collector's supported policy evaluation. [source](https://api.github.com/repos/cli/cli/rules/branches/trunk)

- **fail**: GitHub reports required review approval as unsatisfied. [source](https://github.com/cli/cli/pull/14781)

- **pass**: Observed required check `build (macos-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39586635090/job/119177050864)

- **pass**: Observed required check `build (ubuntu-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39586635090/job/119177050855)

- **pass**: Observed required check `build (windows-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39586635090/job/119177051096)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
