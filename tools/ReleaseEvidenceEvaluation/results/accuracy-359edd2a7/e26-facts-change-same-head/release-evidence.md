## Pull-request evidence snapshot

[`eval/fixture` PR #1](https://github.com/eval/fixture/pull/1)

GitHub verdict: **blocked**. Scoped assurance: **fail**.

Head: `c66930012bbe52fcfd1a1ac138610a3524c92f7d`; base: `main` (`c95498a0a58839741c7ce98c091efe5e23a0297e`).

Collected: 2026-11-02T03:31:21.5110762+00:00; schema 1.0; Ashlar `0.4.2+359edd2a731a43c7f14f31233ca312b96a0a5bb0`; brick `1.4.0`.

Collection attempts: 2; final PR comparison: stable.

- **fail**: `synthetic-reviewer-2` requested changes. [source](https://github.com/eval/fixture/pull/1#pullrequestreview-400262)

- **fail**: The check `ci / unit` concluded failure. [source](https://github.com/eval/fixture/actions/runs/200262)

- **pass**: A 40-character hexadecimal head commit was observed for this snapshot's collection attempt. [source](https://github.com/eval/fixture/pull/1)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Recollected once after an observed PR change. The final PR read agreed with this attempt's head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/eval/fixture/pull/1)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/eval/fixture/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/eval/fixture/rules/branches/main) [source](https://api.github.com/repos/eval/fixture/branches/main)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/eval/fixture/rules/branches/main)

- **fail**: GitHub reports required review approval as unsatisfied. [source](https://github.com/eval/fixture/pull/1)

- **fail**: Required check `ci / unit` has a failed or conflicting conclusion from the required provider. [source](https://github.com/eval/fixture/actions/runs/200262)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, legacy-status provider identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
