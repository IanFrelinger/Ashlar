# Ashlar PR blockers (GitHub Action)

Answers "what is blocking this pull request?" on every run, in the job summary: a verdict
(`blocked`, `ready`, `unknown`, `merged`, `closed`), each blocker with a link to its evidence, and every
fact it could not read, with the reason. Nothing it failed to read is reported as passing.

It runs the open-source `github.blockers` brick through Ashlar's MCP brick host
([`src/Ashlar.Mcp.Server.Host`](../../src/Ashlar.Mcp.Server.Host/README.md)) on the runner. Free, Apache-2.0,
no account, no API key, no telemetry; it reads GitHub with the job's own token.

## Use it

```yaml
name: PR blockers
on:
  pull_request:
permissions:
  contents: read
  pull-requests: read
  checks: read
  statuses: read
jobs:
  blockers:
    runs-on: ubuntu-24.04
    steps:
      - uses: IanFrelinger/Ashlar/actions/pr-blockers@v0.4.1
```

Pin a release tag such as `v0.4.1`, or a commit SHA, for a reproducible run. The action builds the brick from
its own checkout, so `@master` picks up brick fixes as soon as they merge.

| Input | Default | Meaning |
|-------|---------|---------|
| `url` | the triggering pull request | A pull request or `.../releases/tag/<tag>` URL. |
| `github-token` | `${{ github.token }}` | Read access is enough. |
| `fail-on-blocked` | `false` | `true` fails the step when the verdict is `blocked`. |
| `setup-dotnet` | `true` | `false` skips installing the .NET 10 SDK. |

Outputs: `verdict` (`error` when no answer came back), `blocker-count`, `audit-id`.

The step fails (exit 2) when the brick gives no answer (tool error, protocol error, timeout), whatever
`fail-on-blocked` says.

## Known limits

- **It sees its own pending check.** On a `pull_request` trigger this job is itself still running, so the
  verdict usually lists pending checks. Use it as a summary there. To gate on it, trigger it after your
  CI finishes (for example `workflow_run` on your CI workflow, passing the PR URL as `url`).
- **Built from source on each run.** The action builds the host from its own checkout (a few minutes). The
  host has been on nuget.org since 0.4.0, but the action has not switched to `dnx Ashlar.Mcp.Server.Host` yet.
- **Branch protection is readable only with admin rights.** With the default token, required checks and
  approvals come back as unknowns and GitHub's `mergeable_state` decides; see the brick's limits in
  [`src/Ashlar.Bricks.GitHub/README.md`](../../src/Ashlar.Bricks.GitHub/README.md#githubblockersbrick).
- **Linux and macOS runners.** The step uses `bash` and `python3`.

The audit record of each call (hashes of the arguments and the answer, never the token) is written under
`$RUNNER_TEMP/ashlar-mcp-audit`.
