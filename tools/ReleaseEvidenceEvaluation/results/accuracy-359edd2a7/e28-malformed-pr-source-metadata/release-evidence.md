## Pull-request evidence snapshot

[`eval/fixture` PR #1](https://github.com/eval/fixture/pull/1)

GitHub verdict: **unknown**. Scoped assurance: **unknown**.

Head: `0d01ba026aa717328b476d9dc35646a13e4231bf`; base: `main` (`8a3b1472997bfb8d8468ce89ec96e4b6d78bd487`).

Collected: 2026-11-02T03:31:21.5313797+00:00; schema 1.0; Ashlar `0.4.2+359edd2a731a43c7f14f31233ca312b96a0a5bb0`; brick `1.4.0`.

Collection attempts: 1; final PR comparison: unknown.

- **unknown**: Pull-request metadata: GitHub returned malformed or mismatched PR identity metadata; its source URL or branch references could not be verified. [source](https://github.com/eval/fixture/pull/1)

- **pass**: A 40-character hexadecimal head commit was observed for this snapshot's collection attempt. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/eval/fixture/pull/1)

- **unknown**: The final PR read changed or could not establish the collected head, base and mergeability facts; collect again. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/eval/fixture/rules/branches/main) [source](https://api.github.com/repos/eval/fixture/branches/main)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/eval/fixture/rules/branches/main)

- **pass**: GitHub's review decision, or a fully read zero-approval rule, establishes the review requirement. [source](https://github.com/eval/fixture/pull/1)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, legacy-status provider identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
