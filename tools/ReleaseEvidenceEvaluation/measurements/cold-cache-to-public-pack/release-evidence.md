## Pull-request evidence snapshot

[`IanFrelinger/Ashlar` PR #900](https://github.com/IanFrelinger/Ashlar/pull/900)

GitHub verdict: **blocked**. Scoped assurance: **fail**.

Head: `023e90a698abc11456a1c7d7665a629ae0b3436c`; base: `master` (`5e0beeae3b037508f23b475d98d13cc7e64c25de`).

Collected: 2026-11-02T01:11:20.6995673+00:00; schema 1.0; Ashlar `0.4.2+47a7c8506b89670d977265ce342a962d35858cd5`; brick `1.3.0`.

- **fail**: The check `Readiness summary` concluded failure. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/41022627472/job/123954337446)

- **fail**: The check `build-core` concluded failure. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/41022627419/job/123953237389)

- **fail**: The check `cert-gate` concluded failure. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/41022627372/job/123953236854)

- **unknown**: Required approvals: Branch protection is not readable with this token (it needs admin rights), and GitHub's review decision could not be read; the pull request has 0 approving review(s). [source](https://github.com/IanFrelinger/Ashlar/pull/900)

- **pass**: A 40-character hexadecimal head commit was observed at collection start. [source](https://github.com/IanFrelinger/Ashlar/pull/900)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/IanFrelinger/Ashlar/pull/900)

- **pass**: The final PR read agreed on head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/IanFrelinger/Ashlar/pull/900)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master/protection/required_status_checks) [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master/protection/required_pull_request_reviews) [source](https://api.github.com/repos/IanFrelinger/Ashlar/rules/branches/master) [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/IanFrelinger/Ashlar/rules/branches/master)

- **unknown**: Required check app/provider bindings could not be verified; a matching check name alone is insufficient. [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master/protection/required_status_checks) [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master/protection/required_pull_request_reviews) [source](https://api.github.com/repos/IanFrelinger/Ashlar/rules/branches/master) [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master)

- **unknown**: Approval count alone does not establish current code-owner, last-push or stale-review requirements. [source](https://github.com/IanFrelinger/Ashlar/pull/900)

- **fail**: Observed required check `cert-gate` has a failed or conflicting conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/41022627372/job/123953236854)

- **fail**: Observed required check `build-core` has a failed or conflicting conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/41022627419/job/123953237389)

- **pass**: Observed required check `shell-lint` has a passing conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/41022627474/job/123953237664)

- **pass**: Observed required check `lychee (README + docs)` has a passing conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/41022627380/job/123953236623)

- **fail**: Observed required check `Readiness summary` has a failed or conflicting conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/41022627472/job/123954337446)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.

Audit id: `0d313de488a4422780603aad137ffc34`.
