# Ashlar PR blockers and release evidence

Runs the open `github.blockers` brick through the [MCP host](../../src/Ashlar.Mcp.Server.Host/README.md).
It produces the existing GitHub-derived verdict plus a compact JSON snapshot and matching Markdown report.
A human remains responsible for releasing. This pack does not prove compliance or authorize a release.

**The evidence outputs described here are unreleased.** Pin a reviewed commit containing this change.
Older tags (including `v0.4.2`) provide the legacy summary, verdict and audit ID only.

## Run on a pull request

```yaml
name: PR evidence
on:
  pull_request:
permissions:
  contents: read
  pull-requests: read
  checks: read
  statuses: read
jobs:
  evidence:
    name: PR evidence
    runs-on: ubuntu-24.04
    timeout-minutes: 20
    steps:
      # Replace with a reviewed immutable commit containing the evidence feature.
      - uses: IanFrelinger/Ashlar/actions/pr-blockers@REPLACE_WITH_REVIEWED_COMMIT_SHA
        id: evidence
        with:
          collection-check-name: PR evidence
      - uses: actions/upload-artifact@v6
        with:
          name: release-evidence
          retention-days: 7
          path: |
            ${{ steps.evidence.outputs.evidence-json }}
            ${{ steps.evidence.outputs.evidence-markdown }}
```

No checkout or execution of the pull request's code is needed. The action builds **its own pinned source**,
then reads GitHub APIs. Linux and macOS runners with Bash and Python 3 are supported; .NET 10 setup is
included. The source build/restore can take minutes and makes outbound package requests. Collection makes
multiple GitHub API calls (including pagination and a final PR reread); installation and collection times
have not yet been benchmarked on a customer workflow. Anonymous access is heavily rate limited.

| Input | Default | Meaning |
|---|---|---|
| `url` | triggering PR | `https://github.com/owner/repo/pull/number`; legacy release-tag URLs also supported. |
| `github-token` | `${{ github.token }}` | Read-only GitHub credential for the selected repository. |
| `collection-check-name` | empty | Exact check name for this collection job. Only a check proven optional may be excluded from assurance. |
| `fail-on-blocked` | `false` | Preserve legacy behavior: fail only for verdict `blocked`. This does **not** gate on assurance. |
| `setup-dotnet` | `true` | `false` if the runner already has the pinned compatible SDK. |

Existing outputs remain `verdict` (`blocked`, `ready`, `unknown`, `merged`, `closed`, or `error`),
`blocker-count`, and `audit-id`. Additive outputs: `assurance` (`pass`, `fail`, `unknown`,
`not-applicable`), `evidence-json` and `evidence-markdown` (absolute paths on this runner).
Artifacts are not uploaded automatically. Each collection writes a new directory; use the output paths.
No answer, unsupported protocol/schema, conflicting verdict, or failed export exits 2 and emits
`verdict=error`. Old hosts produce no assurance output; never treat an absent output as passing.

## Verdict, assurance, and freshness

The legacy verdict is preserved for existing consumers. GitHub can report a PR `ready` while branch
policy is unreadable. **Only assurance `pass` means all mandatory facts in the supported inspection scope
were observed to pass.** An unknown, missing permission, partial read, or circular required collection
check cannot pass assurance. If you require a gate, explicitly require `assurance == 'pass'`; absence,
unknown, fail and not-applicable must fail that gate.

The default token usually cannot read branch protection. Use a narrowly scoped GitHub App installation
token or fine-grained token when policy evidence is needed: repository metadata, contents, pull requests,
checks and commit statuses read access, plus administration **read** access for branch protection.
Do not grant administration write access. Organization rules or other unavailable policy facts can still
leave assurance unknown. The pack records supplied credential mode and limitations, not a claim that the
actor is authenticated or every token permission was discovered.

This is a time-bound, non-atomic snapshot for one repository and PR. Head/base changes detected during
collection invalidate assurance. A matching SHA later does not establish that approvals, checks, rules,
base branch or bypass permissions stayed unchanged. Recollect before review/release and after relevant
changes. [Contract and inspection limits](../../docs/release-evidence.md).

## Safe post-CI collection

`workflow_run` can run with privileges even when the triggering workflow processed untrusted PR code.
Never check out that PR's head, execute its files, restore its caches, or consume its uploaded artifacts
in this context. Do not use `pull_request_target` to build PR code. See GitHub's
[secure-use guidance](https://docs.github.com/en/actions/reference/security/secure-use) and
[token guidance](https://docs.github.com/en/actions/tutorials/authenticate-with-github_token).

The following deliberately supports same-repository PRs only, uses the trusted event file solely for
its numeric PR identity, and executes only pinned action code. Forks and events with no unique PR are
skipped or refused. Rename `CI` to your actual workflow. Its completion does not imply its success; the
brick inspects current facts again.

```yaml
name: Post-CI evidence
on:
  workflow_run:
    workflows: [CI]
    types: [completed]
permissions:
  contents: read
  pull-requests: read
  checks: read
  statuses: read
jobs:
  evidence:
    if: >-
      github.event.workflow_run.event == 'pull_request' &&
      github.event.workflow_run.head_repository.full_name == github.repository
    runs-on: ubuntu-24.04
    timeout-minutes: 20
    steps:
      - name: Select PR identity from event metadata
        id: target
        shell: bash
        run: |
          python3 - <<'PY'
          import json, os
          with open(os.environ['GITHUB_EVENT_PATH']) as f:
              prs = json.load(f)['workflow_run']['pull_requests']
          if len(prs) != 1 or type(prs[0].get('number')) is not int or prs[0]['number'] <= 0:
              raise SystemExit('Expected exactly one PR; collect explicitly instead')
          with open(os.environ['GITHUB_OUTPUT'], 'a') as f:
              f.write(f"url=https://github.com/{os.environ['GITHUB_REPOSITORY']}/pull/{prs[0]['number']}\n")
          PY
      - uses: IanFrelinger/Ashlar/actions/pr-blockers@REPLACE_WITH_REVIEWED_COMMIT_SHA
        id: evidence
        with:
          url: ${{ steps.target.outputs.url }}
          collection-check-name: Post-CI evidence
```

Avoid requiring the evidence job as one of the very checks it waits for. A provably required, pending
self-check is circular and unsatisfied (`fail`); unverifiable requirements remain `unknown`. A supplied
name alone never waives policy. A trigger after CI also does not freeze
external state or solve all required-workflow/ruleset policy cases.

The collector matches required app bindings per context. A failed same-name check from a known
different app is advisory; missing or ambiguous provider identity stays unknown. If compared PR facts
change during collection, it recollects once with fresh checks, reviews and policy. A second unstable
or unverifiable collection returns unknown. The JSON and report include collection-attempt metadata.
This can add a second set of API calls and up to four mergeability waits (eight seconds total);
network latency and caller deadlines still apply. See the [contract and freshness limits](../../docs/release-evidence.md).

## Credentials, privacy, and integrity

The client passes the token to the host environment, scoped to the selected repository, rather than in
model-visible tool arguments. The host rejects off-repository requests with this configured credential.
The process and its operator can still read its environment; use a dedicated runner and read-only token.
Do not execute untrusted code in that process. This host is a tool allowlist and repository credential
boundary, not a sandbox for an agent that has other tools or network access.

The compact pack omits PR body, patches and token values; check names, repository identifiers, source
links and blockers may still be private. The MCP audit logs hashes of arguments/results under
`$RUNNER_TEMP/ashlar-mcp-audit`; hashes do not anonymize guessable values. Keep reports and logs in
appropriately restricted storage, choose retention explicitly, and avoid uploading raw logs unnecessarily.
GitHub artifact visibility follows repository/workflow access. No telemetry or external Ashlar account.

An audit ID alone is not a signature. The local audit chain cannot prove deleted tails or sessions were
retained. For a signed export verified against a previously trusted key and independently retained hash,
see [evidence integrity and anchoring](../../docs/release-evidence-integrity.md). Signing proves retained
bytes and signer identity within that trust model, not the correctness of GitHub's assertions.

The generic pack and verifier remain Apache-2.0; compliance mapping and organizational packaging are
separate commercial concerns under [the existing boundary](../../LICENSING.md).
