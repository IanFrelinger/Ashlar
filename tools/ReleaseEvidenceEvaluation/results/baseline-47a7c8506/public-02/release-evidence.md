## Pull-request evidence snapshot

[`IanFrelinger/Ashlar` PR #853](https://github.com/IanFrelinger/Ashlar/pull/853)

GitHub verdict: **blocked**. Scoped assurance: **fail**.

Head: `8b9fda6a16e3bde4b9051e1ae385e813c0df87f6`; base: `master` (`6bc6f1b7eb327c6de4870f37bb2cc4d6361b64a5`).

Collected: 2026-11-02T01:09:06.6716729+00:00; schema 1.0; Ashlar `0.4.2+47a7c8506b89670d977265ce342a962d35858cd5`; brick `1.3.0`.

- **fail**: The branch has merge conflicts with `master`. [source](https://github.com/IanFrelinger/Ashlar/pull/853)

- **unknown**: Required approvals: Branch protection is not readable with this token (it needs admin rights), and GitHub's review decision could not be read; the pull request has 0 approving review(s). [source](https://github.com/IanFrelinger/Ashlar/pull/853)

- **pass**: A 40-character hexadecimal head commit was observed at collection start. [source](https://github.com/IanFrelinger/Ashlar/pull/853)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/IanFrelinger/Ashlar/pull/853)

- **pass**: The final PR read agreed on head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/IanFrelinger/Ashlar/pull/853)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master/protection/required_status_checks) [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master/protection/required_pull_request_reviews) [source](https://api.github.com/repos/IanFrelinger/Ashlar/rules/branches/master) [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/IanFrelinger/Ashlar/rules/branches/master)

- **unknown**: Required check app/provider bindings could not be verified; a matching check name alone is insufficient. [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master/protection/required_status_checks) [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master/protection/required_pull_request_reviews) [source](https://api.github.com/repos/IanFrelinger/Ashlar/rules/branches/master) [source](https://api.github.com/repos/IanFrelinger/Ashlar/branches/master)

- **unknown**: Approval count alone does not establish current code-owner, last-push or stale-review requirements. [source](https://github.com/IanFrelinger/Ashlar/pull/853)

- **pass**: Observed required check `cert-gate` has a passing conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/40159555673/job/121068063234)

- **pass**: Observed required check `build-core` has a passing conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/40159555703/job/121068062721)

- **pass**: Observed required check `shell-lint` has a passing conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/40159555687/job/121068062509)

- **pass**: Observed required check `lychee (README + docs)` has a passing conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/40159555665/job/121068062574)

- **pass**: Observed required check `Readiness summary` has a passing conclusion. [source](https://github.com/IanFrelinger/Ashlar/actions/runs/40161484103/job/121080265709) [source](https://github.com/IanFrelinger/Ashlar/actions/runs/40159555642/job/121073604995)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, app-bound check identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
