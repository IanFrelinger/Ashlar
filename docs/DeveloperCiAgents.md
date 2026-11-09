# Developer CI workers

Ashlar has independent **deterministic CI workers** for developer dogfooding,
redundancy, efficiency, security and cost/reliability review. A coordinator produces
an issue-ready verification queue. These are not autonomous LLM reviewers: no model
provider, account credentials or operator spending policy is configured by this change.
The jobs never write fixes, file issues, post comments, approve, merge or release.

## What runs

| Role | Execution and evidence |
|---|---|
| Developer dogfood | The existing Distribution Matrix standalone-brick job installs the packed CLI outside the repository, scaffolds and tests a brick, then edits it into a word counter and tests typed results, whitespace and cancellation. Two TRX receipts are retained. Existing packed-host and external-product jobs continue testing other consumer journeys. |
| Redundancy reviewer | Reports exact repeated windows of 24 nonblank lines across product C# files, with related locations. This is a candidate, not proof that duplication is unnecessary. |
| Efficiency reviewer | Flags synchronous async waits and adjacent collection materializations. It does not infer hot paths or claim measured regressions. |
| Security reviewer | Checks obvious TLS validation bypasses, BinaryFormatter construction, privileged contributor checkouts, direct interpolation of untrusted event text into shell scripts, broad token grants and moving action branches. This is not a comprehensive security audit. |
| Cost/reliability reviewer | Checks executable CI jobs for missing/long timeouts and overlapping PR build/test work without concurrency controls. Dynamic values are not evaluated. |
| Coordinator | Deduplicates stable fingerprints, prioritizes security triage, proposes owners by role and emits structured follow-up records. It does not create or assign GitHub issues. |
| Regression role | Adds a reproduction, named failing test or benchmark, restoration and affected-suite/mutation verification plan to every candidate. Existing test and mutation harnesses execute real verification; this role does not automatically author product tests. |
| Release-readiness role | Adds compatibility, documentation, dependency and final-SHA gate requirements. It never declares a release ready from static findings. Existing required gates and Distribution Matrix remain execution evidence. |

`.github/workflows/developer-ci-agents.yml` runs four independent review jobs on
relevant PR changes and combines their artifacts. PRs report candidates added since
the immutable base SHA; Friday and manual runs inspect the full snapshot, including
existing debt. Workers do not consume each other's conclusions. The coordinator
requires all four receipts to name the same source SHA. Missing, oversized, invalid,
truncated or unparseable inputs produce an incomplete result and a failed job, never
a clean claim. Findings themselves are advisory and do not add branch protection.

Developer dogfooding reuses the existing Distribution Matrix schedule and path filters;
it does not add a second consumer matrix. Fixture changes trigger that lane. Scaffold
and edited-consumer tests must execute: zero discovery or all-skipped receipts fail
the existing zero-test guard. This is a scripted developer journey, not evidence of
a model autonomously learning an unfamiliar API.

## Permissions, costs and reproducibility

- Review jobs use `contents: read`, no persisted checkout credentials, no secrets and
  no `pull_request_target`. Scanners read immutable Git blobs as data, never import
  or execute them. YAML is parsed without constructors, tags, aliases or ambiguous
  keys. Findings omit source snippets and secret values.
- The workflow and tooling are reviewed repository code. Ordinary PR CI executes
  proposed code on an ephemeral runner. These jobs are not a secure sandbox for
  hostile repository changes; do not grant them credentials.
- There are **zero model/API calls** and no automatically provisioned services.
  Each review job has a ten-minute ceiling; coordination has five minutes. Four jobs
  plus coordination replace an unbounded spawning loop. New pushes cancel stale
  runs. No retries are added. Actual runner minutes depend on repository size.
- Scans accept at most 4,000 source files, 1 MiB per file, 64 MiB of source contents
  and 100 findings per worker. These are input/report limits, not a 64 MiB process
  memory guarantee. Runner limits and job timeouts provide the process backstop.
- Results are deterministic for the source/base pair and tool revision. CI pins
  PyYAML; source SHA, scope and limits accompany JSON and Markdown. Packages,
  runners and remote action version tags can change; no bit-for-bit reproducibility
  claim is made. Artifacts expire after 30 days; retain important evidence in a
  reviewed issue or fix PR when it must survive longer.
- Tests/generated code are excluded from product review. Exact duplication can be
  intentional; syntax signals miss aliases, reflection, interpolation bodies, data
  flow and runtime context. This does not replace Security Gate, dependency scans,
  performance measurements, human review or threat modeling. For example, an
  existing TLS bypass behind a development-only option still requires context review.

## Acting on a finding

Open the run's `developer-verification-queue` artifact. Every item has a fingerprint,
source SHA, location, rationale, proposed route and next action. Verify reachability
and intended behavior first. Establish a failing assertion or benchmark before
changing code, then restore green and use `scripts/mutation-check.sh` or the existing
manifest harness for the proposed fix. Confirm affected tests and the five required
merge gates on its final SHA. The coordinator performs no side effects on your behalf.

Model-driven agents remain a separate, disabled extension. Enabling them requires an
operator-approved provider, credentials outside contributor-controlled code, an
enforceable per-run budget, a sandbox/tool policy, and a decision on issue/fix
publishing. The operator-local autonomy controller is not silently enabled in CI.
Repository content, issues and findings must remain untrusted data rather than new
instructions. Regression code writing and automatic fix assignment are therefore
**not implemented by these deterministic workers**.

Local checks (Python plus PyYAML, no Docker or .NET):

```bash
python3 scripts/ci/test-developer-ci-agents.py
python3 tools/ci-agents/review.py --head "$(git rev-parse HEAD)" --worker security --output /tmp/ashlar-security-review
```

The required `shell-lint` job runs these tests through the shared repository-gate
runner. Each discovered check has a 60-second deadline, with a 180-second total
budget that leaves time to publish diagnostics before the five-minute job limit.
Failures and stalls remain nonzero. Later checks run while budget remains; checks
not reached are explicitly `not_run`, never green. The `repository-gate-diagnostics`
artifact retains individual logs, exact reproduction commands, elapsed times and
JSON statuses for 30 days. The job summary identifies failed or timed-out checks.
The last verbose test output helps identify a stuck test; no issue is created or
automatically assigned.

For local investigation, set `ASHLAR_GATE_REPORTS` to a fresh output directory and
run `bash scripts/ci/run-repo-gates.sh`. The default directory is printed when the
runner starts. `ASHLAR_GATE_TIMEOUT_SECONDS` and `ASHLAR_GATE_BUDGET_SECONDS` can
adjust the limits for a deliberate slow run. Running receipts and partial logs are
written before completion; a hard runner termination can still prevent upload,
and a missing artifact means missing evidence, not success. These deadlines cover
the discovered shell/Python checks, not every .NET test job in the repository.
The standalone developer journey reuses the tested consumer-path guard and retains
its log with the TRX receipts. An unavailable scanner or a read error fails the
journey instead of being treated as a clean path scan.

Run the developer journey through the repository's devtest container:

```bash
bash scripts/test-in-container.sh -- 'bash scripts/verify-standalone-brick-authoring.sh'
```

Security rationale: [GitHub secure Actions use](https://docs.github.com/en/actions/reference/security/secure-use),
[privileged PR workflows](https://docs.github.com/en/actions/reference/security/securely-using-pull_request_target),
[BinaryFormatter security](https://learn.microsoft.com/en-us/dotnet/standard/serialization/binaryformatter-security-guide),
and [accept-any TLS validation](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpclienthandler.dangerousacceptanyservercertificatevalidator?view=net-10.0).
