# Release-evidence offline replay

This tool evaluates the existing `github.blockers` collector and renderer against frozen GitHub
HTTP responses. It does not implement a second blocker analyzer. Ground truth is authored and
adjudicated separately, before candidate output exists; the runner copies and hashes those files
without using labels to produce an answer.

The initial candidate is `47a7c8506b89670d977265ce342a962d35858cd5`. Run only after the phase's
fixture manifest and independently reviewed/adjudicated ground truth have been frozen:

```bash
# Use the repository's devtest image; all dotnet execution stays in the container.
ASHLAR_DEVTEST_IMAGE=ashlar-devtest:slim \
  bash tools/ReleaseEvidenceEvaluation/run-replay.sh \
  --candidate-commit 47a7c8506b89670d977265ce342a962d35858cd5 \
  --fixture-manifest tools/ReleaseEvidenceEvaluation/input-manifest.json \
  --ground-truth /path/to/frozen-adjudicated-ground-truth.json \
  --output /path/to/new-replay-output
```

`run-replay.sh` creates a disposable clone at the exact candidate commit, overlays only the
current evaluation project and frozen input bytes, then calls `scripts/test-in-container.sh`.
The brick assembly informational version must include the full requested commit. No production
source or assembly friend declaration is changed for evaluation. The runner reflects the existing
internal client-factory and constructor injection seams. The production client factory, egress
wrapper, Octokit deserialization, collector, deterministic analysis, pack generation and Markdown
renderer execute unchanged.

The supported script needs Bash, Python 3, Git and Docker. It refuses an existing output directory,
validates every selected fixture's ID and SHA-256, and retains each input verbatim in the result.
Dependency restore/build can use the network and the configured NuGet cache. Follow the managed
cloud environment's proxy/CA setup; the phase used a session-only Docker wrapper to mount the
public combined CA without disabling TLS verification. Package restore/build time is not replay
collection time. A warm cache is not a clean-install benchmark.

For a repaired candidate, keep the exact same frozen inputs and adjudicated labels and choose a
new output directory. Supply the new full commit and the explicit `--allow-candidate-rerun` flag.
The run manifest records both `baselineCandidateCommit` and actual `candidateCommit`, with
`candidateMode: "repaired-rerun"`. Without that flag a candidate different from the original
frozen manifest is refused. Never overwrite or silently relabel the original failed run.

The HTTP handler never falls through to the network. It accepts only `https://api.github.com`,
serves recorded responses and refuses unmatched requests or more than 256 requests in one case.
A separate transport-contract probe confirmed Octokit requests reviews, check-runs and combined
status with the bare paths captured in this dataset. The replay transport can additionally be run
in a container with networking disabled after dependencies are available; the wrapper itself does
not claim to create an agent sandbox or confine other processes.

## Input and sequencing contract

Each case has an `id`, PR `target` URL, `credentialSupplied` boolean, optional
`collectionCheckName`, factual metadata, and `routes`:

```json
{
  "method": "GET",
  "path": "/repos/example/repo/pulls/1",
  "responses": [
    {"status": 200, "headers": {}, "body": {"state": "open"}}
  ]
}
```

The abbreviated body above illustrates the transport envelope, not a valid complete PR response.
The runner serves `body` JSON or exact `rawBody` text. A `fault: "timeout"` throws a transport
timeout; it is not HTTP success. Method and `PathAndQuery` match first. A route with no query may
match the same path with pagination defaults; query-specific routes take precedence. Same-route
responses are consumed in sequence and the last repeats. Response reuse, unread responses and
unused routes are recorded. Unused later observations never silently enter the collector's result.
They remain available to the independent reviewer when judging freshness and coverage.

Synthetic supplied credentials use a fixed non-secret placeholder; no real token is read from the
case or environment. Transcripts retain only whether Authorization was present, never its value.
Mergeability waiting periods are elided while preserving request sequence. Actual brick timestamps
record replay execution, **not** the original public capture interval or simulated scenario time.

## Artifacts and interpretation

The new output directory contains `run-manifest.json`, byte-identical frozen fixture and ground-truth
copies, and one directory per case:

- `input.json`: exact frozen case bytes;
- `payload.json`: complete brick outputs, including legacy verdict/blockers and scoped assurance;
- `release-evidence.json` and `release-evidence.md`: unchanged production pack and renderer output;
- `transcript.json`: ordered requests, selected response indices and SHA-256 values;
- `error.json` when collection or export did not complete.

The run manifest records the candidate, assembly provenance, input/ground-truth hashes, per-artifact
hashes, replay elapsed time, route coverage and answered/collector-error/harness-error outcomes.
An incomplete replay is not a passing assurance result. Retain the whole result directory: an
isolated pack's collection timestamp must not be mistaken for a fresh GitHub observation. These are
**evaluation-only offline artifacts**, not live release evidence or release authorization.

Replaying frozen cases measures deterministic behavior over this corpus. It does not repeat the
MCP host access-control, Action trigger-security or signed-export checks from the implementation
validation. Those have separate evidence and supported boundaries. The report must disclose public
capture authorization, sampling limits, AI-only review if applicable, disagreements and failures.
The independent evaluation gate does not lift marketing, pilot or autonomy holds.

## Harness-only setup checks

These checks make no candidate verdict/assurance decisions and can run before labels are frozen:

```bash
# Inside a disposable devtest clone after building the project:
dotnet tools/ReleaseEvidenceEvaluation/bin/Debug/net10.0/Ashlar.ReleaseEvidenceEvaluation.dll --transport-probe
dotnet tools/ReleaseEvidenceEvaluation/bin/Debug/net10.0/Ashlar.ReleaseEvidenceEvaluation.dll --self-test
```

The probe calls only direct Octokit APIs against empty in-memory responses. The self-test covers
response sequencing/reuse, query-route precedence, timeouts, unmatched endpoints, foreign-host
refusal, unused-source disclosure and omitted authorization values. Neither counts toward the
independent snapshot sample.
