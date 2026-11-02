## Pull-request evidence snapshot

[`cli/cli` PR #14781](https://github.com/cli/cli/pull/14781)

GitHub verdict: **blocked**. Scoped assurance: **fail**.

Head: `2e734c0e0a194e9b323e1b2aea98aea16b0f49d7`; base: `trunk` (`a81663f7d80d620b092dab3056778a79ff3e279c`).

Collected: 2026-11-02T03:31:21.6087865+00:00; schema 1.0; Ashlar `0.4.2+359edd2a731a43c7f14f31233ca312b96a0a5bb0`; brick `1.4.0`.

Collection attempts: 1; final PR comparison: stable.

- **fail**: The pull request is a draft. [source](https://github.com/cli/cli/pull/14781)

- **fail**: GitHub reports that an approving review is required (review decision REVIEW_REQUIRED); the pull request has 0 approving review(s). [source](https://github.com/cli/cli/pull/14781)

- **pass**: A 40-character hexadecimal head commit was observed for this snapshot's collection attempt. [source](https://github.com/cli/cli/pull/14781)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/cli/cli/pull/14781)

- **pass**: The final PR read agreed with this attempt's head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/cli/cli/pull/14781)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/cli/cli/branches/trunk/protection/required_status_checks) [source](https://api.github.com/repos/cli/cli/branches/trunk/protection/required_pull_request_reviews) [source](https://api.github.com/repos/cli/cli/rules/branches/trunk) [source](https://api.github.com/repos/cli/cli/branches/trunk)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/cli/cli/rules/branches/trunk)

- **unknown**: Detected mandatory rule `copilot_code_review` is outside this collector's supported policy evaluation. [source](https://api.github.com/repos/cli/cli/rules/branches/trunk)

- **fail**: GitHub reports required review approval as unsatisfied. [source](https://github.com/cli/cli/pull/14781)

- **pass**: Required check `build (macos-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39586635090/job/119177050864)

- **pass**: Required check `build (ubuntu-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39586635090/job/119177050855)

- **pass**: Required check `build (windows-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39586635090/job/119177051096)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, legacy-status provider identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
