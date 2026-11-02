# Development validation — 2026-11-01

## Outcome and remaining gates

The internal read-only PR workflow now produces versioned JSON/Markdown evidence with
conservative assurance, host-held scoped credentials and optional signed exports. Held
dogfood citation checks and witness-kind regressions were strengthened. No push, merge,
release, deployment, outreach or customer-data use was performed.

- **Verified locally:** host builds with zero warnings/errors; **112 GitHub/credential tests**;
  **29 MCP audit tests**; **3,756 certification tests**; committed red/green mutation checks;
  **all 47 repository gates**. The final gate run
  also preserved the caller's complete index and staged/unstaged graph/documents byte for byte.
  The final public PR #900 run produced a schema-valid, attributable blocked/fail snapshot in **6.747555**
  seconds; a temporary-key export of that real pack also verified.
- **Runtime scope:** the final certification implementation on the CI .NET 8 target passed **3,756/3,756**
  with zero skips; comparer/loader mutations failed then returned green after clean restoration.
  The supplemental 3,752-test .NET 10 run preceded the aggregate repair and has that narrower
  provenance. Native process-reaper failure and its local wrapper are disclosed. No new-tree
  full-platform/hosted CI run, Windows/macOS rerun or cold-install benchmark is claimed.
- **Still open:** independent 30-snapshot evaluation, operator identity bridge/custody proof,
  reviewed elapsed scorecard evidence and a real human-reviewed loop PR. P4 durable tasks
  and P5's framework example/compatibility CI were not implemented in this batch. Marketing,
  design-partner pilot and autonomy holds remain in force.

Baseline: `5e0beeae3b037508f23b475d98d13cc7e64c25de` (`master`). See the
[claims and remote-CI baseline](development-baseline-2026-11-01.md) for the supported surfaces,
hold status and external evidence. New working-tree results below do not inherit that remote
CI's green status. This record distinguishes execution, source review and unavailable checks.

## Environment

Managed Linux workspace, Debian GNU/Linux 13.6 (trixie), Python 3.12.14; no configured
production credentials. Builds/tests using .NET must follow the repository's Linux devtest
container rule. No host `dotnet` execution is counted. Customer repositories/data and
operator-held private keys were not used.

## Repository checks and the process-reaper dependency

The first `bash scripts/ci/run-repo-gates.sh` attempt reported **44/46 checks passed**.
The knowledge-graph freshness check found the expected unstaged-new-file drift. The other
failure was the pre-existing autonomy-controller cancellation test:
`tests.test_cursor_adapter.CancelAndReconcile.test_cancel_records_first_then_kills_the_whole_tree`.
The wrapper had discovered 555 tests and completed in 116.984 seconds; its failure is not
reported as a pass.

The exact cancellation test was reproduced against the unchanged baseline clone:

```bash
cd /workspace/scratch/cert-baseline/tools/autonomy-controller
git rev-parse HEAD
PYTHONDONTWRITEBYTECODE=1 PYTHONWARNINGS=ignore python3 -m unittest \
  tests.test_cursor_adapter.CancelAndReconcile.test_cancel_records_first_then_kills_the_whole_tree
```

Result: **1 test, 1 failure, 20.168 seconds**, assertion at line 442: “the launcher's child
survived cancel: not a tree kill”. During the wait, the test's two session children were
observed in `/proc`/`ps` as **Z (zombie)**, reparented to PID 1. PID 1 is `tail -f /dev/null`
in this workspace and does not reap them. The test's POSIX `pid_alive` uses `os.kill(pid, 0)`,
which continues to report the unreaped zombie's PID as present. This establishes a baseline
environment failure; it does not establish that the child continued executing after cancel.

A process-local Linux subreaper provided a positive control without modifying the test or
production code. An inline run passed **1/1 in 0.150 seconds**; a saved session-only wrapper
then passed **1/1 in 0.147 seconds** and reaped both adopted descendants with SIGTERM exit
status `-15`. The wrapper is `/workspace/scratch/run-with-subreaper.py` and contains:

```python
import ctypes
import os
import subprocess
import sys

libc = ctypes.CDLL(None, use_errno=True)
if libc.prctl(36, 1, 0, 0, 0) != 0:  # Linux PR_SET_CHILD_SUBREAPER
    raise OSError(ctypes.get_errno(), "subreaper setup failed")
process = subprocess.Popen(sys.argv[1:])
while True:
    pid, status = os.waitpid(-1, 0)
    if pid == process.pid:
        raise SystemExit(os.waitstatus_to_exitcode(status))
```

Invocation from the same baseline directory:

```bash
PYTHONDONTWRITEBYTECODE=1 PYTHONWARNINGS=ignore \
  python3 /workspace/scratch/run-with-subreaper.py python3 -m unittest \
  tests.test_cursor_adapter.CancelAndReconcile.test_cancel_records_first_then_kills_the_whole_tree
```

The full repository check rerun used this wrapper to supply the missing process-reaping
behavior:

```bash
cd /workspace/Ashlar
python3 /workspace/scratch/run-with-subreaper.py bash scripts/ci/run-repo-gates.sh \
  > /workspace/scratch/repo-gates-final.log 2>&1
```

Result: **45/47 gates passed**. The autonomy-controller suite passed **555/555 in 83.408
seconds**, confirming the cancellation assertion with normal child reaping. The two failures
were `verify-zero-test-guard-wiring.py` (the new certification reproduction helper lacked
explicit TRX/guards) and knowledge-graph freshness/documentation (new variables/test files
were still being integrated). Neither was converted into a pass. No unrelated controller
change or weakened assertion was made.

After the reproduction helper was corrected, `python3 scripts/ci/verify-zero-test-guard-wiring.py`
passed with **108 pinned invocations, 93 guarded, 15 exempt, 0 problems**. A targeted
knowledge-graph rerun confirmed the GitHub variables were documented but still found derived
artifact drift and the then-untracked `AggregateWitnessLoaderRegressionTests.cs`; the final
post-staging/regeneration result is recorded below. The source continued changing during
integration, so this mixed run is not claimed as a single clean final-tree 47/47 result.

After the final source was staged and the graph regenerated, the same wrapper ran the entire
repository gate command again:

```bash
python3 /workspace/scratch/run-with-subreaper.py bash scripts/ci/run-repo-gates.sh \
  > /workspace/scratch/repo-gates-integrated.log 2>&1
```

**Final integrated result: all 47 gates passed (exit 0).** The autonomy-controller suite
passed **555/555 in 61.172 seconds**. The graph reported **62 projects, 5,502 declared facts,
zero undocumented operator variables**. Zero-test wiring reported **108 pinned invocations,
93 guarded, 15 exempt, zero problems**. This is a local check result under the explicitly
documented subreaper wrapper; the native PID-1 environment failure above remains disclosed.
No new GitHub CI run or release is implied.

A later preservation check found the existing
`tests/scripts/resolve-knowledge-graph-rebase.test.sh` was destructively restoring the caller's
generated files/index to HEAD after testing regeneration. The green gate run above therefore
did not establish preservation of staged graph updates. In an isolated baseline clone, the
old script exited zero while changing **both working-tree files, both staged files and the
complete index**. The repaired test runs the current helper and builder only in disposable
local clones, uses unique temporary logs, and includes a nested regression with distinct
staged/unstaged sentinels and an exact index snapshot. The fixed probe preserved all five
surfaces. No sentinels or destructive baseline probe were placed in the real caller checkout.
The repair is committed as `758f7c2a05f813c712e404420aa7afffc16e4782`; focused Bash syntax,
helper behavior and preservation checks passed. Container `shellcheck -S error` of the
updated test via standard input also passed. Session probes/logs:
`/workspace/scratch/kg-helper-fixed.log`, `/workspace/scratch/kg-preservation-probe.py` and
`/workspace/scratch/kg-preservation-probe.log`. Regeneration and post-repair checks follow.

After regeneration at `70d1989b9`, the final post-repair command ran all 47 gates again,
writing `/workspace/scratch/repo-gates-preserving.log`: **47/47 passed, exit 0**. The entire
caller index was compared as bytes, and both staged and working-tree versions of the graph,
baseline and validation documents were hashed before/after: **all identical**. Snapshot
evidence is `/workspace/scratch/repo-gates-preserving-{before,after}.json` and
`repo-gates-preserving-before-index.bin`. The native environment caveat and subreaper wrapper
remain the same; this run establishes preservation after the harness repair.

After the Python deadline-only fix at `93cc1f237989c8e797177efc543109e64254c4dd`, the frozen
code passed the entire command once more: **47/47 gates, exit 0**, including the **11/11**
Action unit tests, **30/30** legacy assertions and **555/555** controller tests (63.901
seconds for the controller suite). Log: `/workspace/scratch/repo-gates-deadline.log`.
The complete caller index, both staged graph/documents and their working-tree bytes were
again **identical before/after**. Snapshots are
`/workspace/scratch/repo-gates-deadline-{before,after}.json` and
`repo-gates-deadline-before-index.bin`. No .NET production code changed after its separate
validation runs; this final run checks the Python/test integration and preservation.

## Documentation and evidence review

- `git diff --check`: passed after documentation edits.
- A Python relative-link existence check found **13/13 local links** across the six baseline,
  scorecard/ledger/commercial/claims and evaluation-plan documents at the time checked.
- After integration, a repeat against every Markdown file changed since baseline found
  **40/40 local targets present across 17 documents**. The repository's full gate run also
  passed its documentation-anchor checks.
- The Oct 30 dogfood artifact ZIP and inner record digests matched the metadata, sidecar and
  run citation; exact identities and verification limits are in the baseline document.
- P1 independent source review identified malformed-policy, unsupported-rule/app-bound-check,
  conflicting-approval and version-labeling risks and sent them to the implementation agent
  for focused repairs. Source review is not counted as a test pass.

## Focused implementation checks

The implementation workers reported these completed focused checks on the new working tree;
they are deterministic development fixtures, not independent GitHub accuracy results:

| Command | Outcome | Coverage |
|---|---|---|
| `bash tests/scripts/pr-blockers-action.test.sh` | **30 assertions passed** | Existing Action client behavior and compatibility |
| `bash tests/scripts/pr-evidence-action.test.sh` | **11 unit tests passed** | New export outputs, audit correlation, unknown assurance, old-host behavior, hostile metadata, protocol mismatch, invalid schema/verdict and response-deadline enforcement |
| `bash tests/scripts/evidence-export.test.sh` | **24 unit tests passed** | Signed manifest/anchor, altered or missing files, wrong signer, malformed inventory and unsupported schema |
| `bash tests/scripts/dogfood-sweep-verdict.test.sh` | **42 assertions passed** | Held sweep classification and retained record/sidecar validation |

The new dogfood retained-evidence assertions were also run against the unchanged baseline
classifier in a disposable reproduction: **nine new regression assertions failed**, establishing
that baseline accepted missing/modified/malformed retained-record evidence that the new helper
refuses. The helper checks record bytes and citation consistency; signature verification remains
the .NET archive's responsibility. It does not establish operator custody or independent anchoring.

The following later results came from the Linux validation container, not host .NET.
Validation began Nov 1 and continued into Nov 2 UTC.

| Container command/check | Actual outcome |
|---|---|
| `dotnet build src/Ashlar.Mcp.Server.Host -c Release --nologo -v minimal -p:NuGetAudit=false` | **PASS**, 58.13 seconds, zero warnings/errors. Final origin-metadata rebuild also **PASS**, 3.96 seconds, zero warnings/errors. |
| `dotnet test src/Ashlar.Tests.Infrastructure/Ashlar.Tests.Infrastructure.csproj --framework net10.0 --filter 'FullyQualifiedName~GitHubBlockersBrickTests\|FullyQualifiedName~GitHubReadAccessTests' --no-restore --nologo -v minimal -p:NuGetAudit=false -m:2` | **105 passed, 0 failed, 0 skipped**. Container network was disconnected for execution. |
| Same combined GitHub/credential filter on final clean implementation commit `70d1989b9f96fd7bd8d930f265164daad25cf279` | **112 passed, 0 failed, 0 skipped**, 684 ms test time; includes the public-summary source-link repair. Log: `final-github-tests.log`. |
| `python3 /evidence/real_host_scope_smoke.py -- dotnet /repo/src/Ashlar.Mcp.Server.Host/bin/Release/net10.0/Ashlar.Mcp.Server.Host.dll` | **PASS** through the actual stdio host: protocol `2025-06-18`, allowlist, host-held token omitted from tool schema, off-scope/caller-token bypass refusal, partial-configuration startup refusal, no secret in logs/audit. |
| CI-equivalent ShellCheck with `-S error` across `scripts/` and `.claude/hooks/` | **PASS**. An initial default-severity invocation included informational findings and a nonexistent path glob; it was not the CI-equivalent check. |

Session evidence is in `/workspace/scratch/live-evidence/`: `validate.sh`, `host-build.log`,
`host-build-final.log`, `github-tests.log` and `host-scope.log`. The host smoke exercises the
supported scoped dispatch path; it does not establish network/shell containment of a model
process that can bypass that host. The protocol result covers the pinned version only.
Certification reproduction and final repository gate outcomes are recorded separately.

The additional MCP audit suite initially requested `net10.0` and was rejected before test
execution with `NETSDK1005`: `src/Ashlar.Mcp.Server.Tests` targets `net8.0` only. No tests ran
in that attempt and it is not counted as .NET 10 MCP unit coverage. The corrected offline
`.NET 8` run filters `BrickMcpToolContributorTests`, `McpBrickAuditChainTests` and
`McpBrickHostAuditRetentionTests`: **29 passed, 0 failed, 0 skipped**, duration 9 seconds.
The completed log was inspected at `/workspace/scratch/live-evidence/mcp-audit-tests-net8.log`.
These cover actual audit-chain, tool-contributor and host-retention behavior in the supported
test target; they do not broaden the authenticated-caller or isolation claims.

## Certification reproduction

The isolated, network-disconnected .NET 10 container ran
`ASHLAR_CERT_DISPOSABLE=1 bash scripts/certification/reproduce-493-462.sh /evidence/certification`.
The reproduction helper verifies nonzero executed-test counts from unique TRX files. Restoring
the baseline witness comparer/runner for the red control produced **7 failures, 22 passes,
0 skips (29 total)**. Restoring the repaired implementation produced **29 passes, 0 failures,
0 skips**, with both TRX files inspected. A supplemental broader certification filter on
`.NET 10` subsequently passed **3,752/3,752, 0 failed, 0 skipped**, duration **10 minutes
7 seconds** (`cert-gate.log`/`cert-gate.trx`). That run preceded the final aggregate-witness
loader repair, so it does not establish the final tree's complete certification result.
The exact CI-target `.NET 8` gate and the aggregate red/green leg are recorded separately
below.

The focused outcome confirms residual witness-kind coercion at baseline and tests the repaired
behavior. The compile-set probes exercise the current gate-owned compiler's handling of the
reported artifact/source classes; they are not a fresh general audit of arbitrary MSBuild or
untrusted code. See the [issue reproduction record](certification/issue-493-462-reproduction-2026-11-01.md)
for exact cases, supported path and remaining limitations. Session evidence:
`/workspace/scratch/live-evidence/certification/{baseline-red.log,baseline-red.trx,fixed-green.log,fixed-green.trx}`.

The final certification implementation (`2e0d237068af156e94612a8f2e89c56aea788196`) passed
the CI-target command `bash scripts/run-cert-gate.sh` on `.NET 8`: **3,756 passed, 0 failed,
0 skipped**, total time **9.2228 minutes**. Its
list-derived nonzero floor (`expected >= 3751`) and zero-skip baseline guard both passed.
The retained `/workspace/scratch/live-evidence/cert-gate-net8.trx` was independently parsed:
`total=executed=passed=3756`, `failed=error=aborted=notExecuted=0`. The human-readable log is
`/workspace/scratch/live-evidence/cert-gate-net8.log`.
Later changes enriched a P1 source link (covered by the 112-test run at `70d1989b9`), fixed
the Python deadline (`93cc1f237`, covered by its client/mutation/full repository checks), and
updated test/docs metadata. The certification production implementation remained unchanged;
this does not imply every suite was rerun after every unrelated text change.

Postcommit `.NET 10` mutations at `2e0d237068af156e94612a8f2e89c56aea788196` also passed
their red/clean/green contract. Reintroducing comparer coercion caused **3 failures of 15**;
restoring the committed source gave empty porcelain and **15/15 passed**. Reintroducing
aggregate-loader kind loss caused **2 failures of 2**; clean restoration gave **2/2 passed**.
All four unique TRXs passed executed-test guards. Their counters and the two empty restored
porcelain files were independently checked in `/workspace/scratch/cert-postcommit/`.

## Actual public-PR collection

A clean clone at P1 commit `7eb86b9a47d4bafa4f99a06e9507fb364cb649fe` built the host and
collected public Ashlar PR [#900](https://github.com/IanFrelinger/Ashlar/pull/900) through the
documented Action client. The JSON and matching Markdown are at
`/workspace/scratch/live-evidence/public-pr-900/snapshot-22eb26l4/`.
The JSON passed its Draft 2020-12 schema and format checks; audit ID, head and assurance
matched the report, and every finding had nonempty source references.

Observed snapshot: head `023e90a698abc11456a1c7d7665a629ae0b3436c`, base
`5e0beeae3b037508f23b475d98d13cc7e64c25de`, **verdict blocked, assurance fail**, 16 findings,
audit `b8034ae5ffc949e39bf63c217f9daa4b`. Collection ran from
`2026-11-02T00:15:07.2502534Z` to `00:15:12.9453092Z` (**5.695056 seconds**).
The source build took **92.97 seconds with a warm shared NuGet cache**; this is not a cold
installation benchmark. JSON SHA-256:
`ccb0b055ccff71abd323405b25e4e4a4de3176f67aad713df64dfb01cdadf521`.
This single public run is not the independent 30-snapshot evaluation or a customer result.

The real pack, report and one audit file were also exported and verified using a **temporary
demonstration Ed25519 key** and a locally supplied retained hash. Bundle:
`/workspace/scratch/live-evidence/signed-public-pr-900`; manifest SHA-256:
`41eb2d2a8abecf01d7b05227a78ffa96bfecf1a4a49d629d67574faef4b88517`.
This checks the actual-artifact export/verifier path. It does **not** establish operator-key
custody or an independently operated retention domain.

A final collection repeated the path from clean implementation commit
`70d1989b9f96fd7bd8d930f265164daad25cf279`. Files:
`/workspace/scratch/live-evidence/final-public-pr-900/snapshot-rfuuyfv0/release-evidence.{json,md}`.
Schema/formats, matching audit/head/assurance and the public branch-summary source reference
were checked. Verdict/assurance remained **blocked/fail**, audit
`32cd1b7023a341789751504def3a2543`; collection took **6.747555 seconds**. Final JSON SHA-256:
`d6bfcf03c638f685c7eaa96f5174c7b4a3307a769e3cb66659319d5d4a23f2eb`.
Its host build took **48.77 seconds with existing warm build artifacts**, so it is not
comparable to a cold installation. The final pack plus one audit file also verified in
`/workspace/scratch/live-evidence/signed-final-public-pr-900` with manifest anchor
`ca01a074ddc7f09df6f7a09250b254b061701bded7ae24d49d4427cf73a2ad02`.
This again used a temporary demonstration key; its private half was deleted and the public
half retained. No operator custody or independently operated retention claim is made.

## Committed mutation checks

The exporter mutation used an isolated shared clone, detached at
`477d628bbeaaa64a977a760cfce591242941f34e`. Removing the comparison with the independently
retained manifest SHA-256 caused `bash tests/scripts/evidence-export.test.sh` to fail
**1/24 tests** (`test_rewritten_manifest_and_valid_signature_cannot_replace_retained_anchor`,
5.390 seconds). The exact mutation diff was recorded. Restoring the committed file produced
empty `git status --porcelain`; the same command then passed **24/24** (2.444 seconds).
Session log: `/workspace/scratch/evidence-export-mutation-477d628bb.log`.

The held-dogfood mutation used commit
`6152ed11db0ff4eda6643964a7d0d18de2218e25` in an isolated clone. Removing the retained-record
SHA-256 comparison produced **40 passes, 2 failures**; restoration was clean and the suite
returned to **42 passes**. A separate workflow mutation removing the quote around `SWEEP_GAP`
made the hostile-output probe exit **1**; restoring the quote returned **PASS**. These checks
exercise citation integrity and shell handling, not Ed25519 verification or operator custody.
Session evidence: `/workspace/scratch/dogfood-postcommit-sehbzku9/` contains the exact
`hash-diff.patch`, `hash-red.log`, `hash-green.log`, `workflow-diff.patch`, `workflow-red.log`
and `workflow-green.log`. The red/green logs were inspected for this record.

The standard mutation checker ran against committed P1 identity guard
`7eb86b9a47d4bafa4f99a06e9507fb364cb649fe`. Removing the complete-commit-identity check in
`ReleaseEvidencePack.cs` produced **7 failed, 89 passed (96 total)**; committed restoration
gave clean porcelain and **96/96 passed**. Log verdict:
`mutation pr-evidence-identity-guard: KILLED red=failed:7/96 green=passed:96/96`.
The applied diff, test counts, restoration and final verdict were inspected in
`/workspace/scratch/evidence-identity-mutation.log`.

The committed graph-test repair at `758f7c2a05f813c712e404420aa7afffc16e4782` was also
mutation-checked in an isolated clone. Reintroducing a final restore of the **caller's**
graph/index caused the nested sentinel assertion to fail (`caller json worktree changed`,
exit **1**). Restoring the committed test gave empty porcelain, and the same invocation
passed (exit **0**). The real caller checkout was not the mutation target. Exact diff,
red/green logs and verdict are retained in `/workspace/scratch/kg-preservation-mutation.log`
and `/workspace/scratch/kg-preservation-mutation-cm0g9hfj/`.

The Action response-deadline fix at `93cc1f237989c8e797177efc543109e64254c4dd` also passed
both client suites (**11/11 unit tests and 30/30 legacy assertions**). In an isolated clone,
removing the explicit elapsed-deadline refusal reproduced acceptance of a queued late
response: the deterministic fake-clock test
`test_queued_notifications_cannot_extend_response_deadline` **failed 1/1** (0.005 seconds).
Exact restoration left empty porcelain; the test then **passed 1/1** (0.004 seconds).
The applied diff, red/green logs and restored-porcelain file are at
`/workspace/scratch/deadline-mutation-z6y9u874/`. This was a Python-client-only change; it
did not change the .NET implementation validated above.

## Remaining validation boundaries

At this batch's delivery, the independently reviewed evaluation was planned, not executed;
its fixture and unit results did not satisfy that gate. The subsequent
[2026-11-02 evaluation](benchmarks/release-evidence-evaluation-2026-11-02.md) executed 36 cases
with separate AI review, retained initial failures and repaired reruns, and disclosed remaining
coverage gaps. It does not claim human/customer validation. No attended operator-key custody proof,
seven-day threshold window, real loop-produced human-reviewed PR, customer pilot, release or
deployment was produced by the checks above. Existing marketing/autonomy holds remain in force.

The P3 operator-to-certification identity bridge, P4 durable selected-workflow tasks and
P5's end-to-end .NET agent-framework MCP example and
compatibility CI were **not implemented in this batch**; they are remaining engineering work,
not presented as externally blocked or complete. Receiver-side mesh test reruns remain deferred
pending a concrete use case. Operator custody needs the owner's attended key; seven-day scorecard evidence
requires elapsed runs and review; and the real loop-produced PR needs human review. Those are
distinct dependencies from the implementation work still outstanding. Customer pilots/data and
outreach require separate authorization.
