## Pull-request evidence snapshot

[`dotnet/runtime` PR #136812](https://github.com/dotnet/runtime/pull/136812)

GitHub verdict: **blocked**. Scoped assurance: **fail**.

Head: `33eb87c9872b320aeed78454a76070b3f768a355`; base: `main` (`8586c680f0c3337fd707e3eb891d8250f8f5544c`).

Collected: 2026-11-02T03:31:21.6657302+00:00; schema 1.0; Ashlar `0.4.2+359edd2a731a43c7f14f31233ca312b96a0a5bb0`; brick `1.4.0`.

Collection attempts: 1; final PR comparison: stable.

- **fail**: The pull request is a draft. [source](https://github.com/dotnet/runtime/pull/136812)

- **fail**: 0 of 1 required approving reviews. [source](https://github.com/dotnet/runtime/pull/136812)

- **fail**: The check `Build Analysis` is still in progress. [source](https://github.com/dotnet/runtime/runs/125025485320)

- **pass**: A 40-character hexadecimal head commit was observed for this snapshot's collection attempt. [source](https://github.com/dotnet/runtime/pull/136812)

- **pass**: The relevant base reference and 40-character hexadecimal commit were observed. [source](https://github.com/dotnet/runtime/pull/136812)

- **pass**: The final PR read agreed with this attempt's head, base, draft, open state and mergeability. Checks, reviews and rules are not an atomic snapshot. [source](https://github.com/dotnet/runtime/pull/136812)

- **pass**: Required check names were read from GitHub policy sources. [source](https://api.github.com/repos/dotnet/runtime/branches/main/protection/required_status_checks) [source](https://api.github.com/repos/dotnet/runtime/branches/main/protection/required_pull_request_reviews) [source](https://api.github.com/repos/dotnet/runtime/rules/branches/main) [source](https://api.github.com/repos/dotnet/runtime/branches/main)

- **pass**: Active branch rules were read (this collector evaluates check and review requirements only). [source](https://api.github.com/repos/dotnet/runtime/rules/branches/main)

- **unknown**: Detected mandatory rule `deletion` is outside this collector's supported policy evaluation. [source](https://api.github.com/repos/dotnet/runtime/rules/branches/main)

- **unknown**: Detected mandatory rule `non_fast_forward` is outside this collector's supported policy evaluation. [source](https://api.github.com/repos/dotnet/runtime/rules/branches/main)

- **unknown**: Detected mandatory rule `creation` is outside this collector's supported policy evaluation. [source](https://api.github.com/repos/dotnet/runtime/rules/branches/main)

- **fail**: GitHub reports required review approval as unsatisfied. [source](https://github.com/dotnet/runtime/pull/136812)

- **pass**: Required check `license/cla` has a passing conclusion. [source](https://github.com/dotnet/runtime/runs/125028785577)

- **fail**: Required check `Build Analysis` from GitHub App 216604 is pending; its requirement is currently unmet. [source](https://github.com/dotnet/runtime/runs/125025485320)

Limitations:

- Human release decision required; this pack does not prove compliance or authorize a release.

- No repository code, PR body, diff, private file content, artifact payload or access token is included.

- GitHub rules are not fully reimplemented: deployment, merge-queue, legacy-status provider identity, signatures, bypass permissions and custom rules require separate review.

- API sources are collected sequentially; facts may change during collection or immediately afterward. A matching head does not prove freshness.

- No authenticated caller identity or host audit ID is established by this brick; the host may correlate its audit separately.
