## Pull-request evidence snapshot

[`cli/cli` PR #14716](https://github.com/cli/cli/pull/14716)

GitHub verdict: **ready**. Scoped assurance: **unknown**.

Head: `936921fa9e67cdbf78fb25abc6252afabafef056`; base: `trunk` (`b1c8c0ae5aec75ec8caf6e537071efc15d274f45`).

Collected: 2026-11-02T03:35:32.5704873+00:00; schema 1.0; Ashlar `0.4.2+359edd2a731a43c7f14f31233ca312b96a0a5bb0`; brick `1.4.0`.

Collection attempts: 1; final PR comparison: stable.

- **pass**: A 40-character hexadecimal head commit was observed for this snapshot's collection attempt. [source](https://github.com/cli/cli/pull/14716)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/cli/cli/pull/14716)

- **pass**: The final PR read agreed with this attempt's head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/cli/cli/pull/14716)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/cli/cli/branches/trunk/protection/required_status_checks) [source](https://api.github.com/repos/cli/cli/branches/trunk/protection/required_pull_request_reviews) [source](https://api.github.com/repos/cli/cli/rules/branches/trunk) [source](https://api.github.com/repos/cli/cli/branches/trunk)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/cli/cli/rules/branches/trunk)

- **unknown**: Detected mandatory rule `copilot_code_review` is outside this collector's supported policy evaluation. [source](https://api.github.com/repos/cli/cli/rules/branches/trunk)

- **pass**: GitHub's review decision, or a fully read zero-approval rule, establishes the review requirement. [source](https://github.com/cli/cli/pull/14716)

- **pass**: Required check `build (macos-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39272694853/job/118128877286)

- **pass**: Required check `build (ubuntu-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39272694853/job/118128877234)

- **pass**: Required check `build (windows-latest)` has a passing conclusion. [source](https://github.com/cli/cli/actions/runs/39272694853/job/118128877056)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, legacy-status provider identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
