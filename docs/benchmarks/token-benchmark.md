# Token benchmark: one brick call versus a native GitHub tool loop

Run 2026-10-25 on the Anthropic API, live, with prompt caching on. This is the benchmark the go-to-market
decisions log requires before any token-savings figure appears in public copy
([D9](../commercial/go-to-market.md#decisions-log-added-2026-10-25)). It is published as measured.

**Result.** On three GitHub release-engineering questions, an agent that called the `github.blockers` brick
once per URL cost **96–98% less** (median per run) than the same agent answering from the raw GitHub REST API
through a generic GET tool. It was also about 3–5× faster. Both arms reached the same verdict on every URL in
every run (18 runs, 30 verdicts).

**Scope of that result, before anyone quotes it.** The native arm here returns GitHub's API responses as they
come (large JSON), which is what an agent with `gh api` or a pass-through GitHub tool sees. Most of the saving
is not passing that JSON through the model. A native tool that trimmed its output, tool search, or programmatic
tool calling would narrow the gap; none of those was measured. One model, three workloads, three repetitions.

## Results

Model `claude-sonnet-5-5`. Prices used: $2 per million input tokens, $10 per million output tokens; a cache write
costs 1.25× input and a cache read 0.05× input (Anthropic's published Sonnet 5.5 prices on the run date). Token
counts are the API's own `usage` fields summed over every request in a run; the table shows medians of three
runs.

| Workload | Arm | Requests | Tool calls | Uncached input | Cache write | Cache read | Output | Cost (USD) | Seconds |
|---|---|---|---|---|---|---|---|---|---|
| W1: one pull request | native | 5 | 7 | 12 | 32,598 | 89,223 | 1,560 | 0.1060 | 15.4 |
| W1: one pull request | brick | 2 | 1 | 6 | 0 | 1,962 | 402 | 0.0043 | 4.4 |
| W2: one release | native | 4 | 6 | 10 | 62,532 | 132,252 | 1,890 | 0.1877 | 17.0 |
| W2: one release | brick | 2 | 1 | 6 | 0 | 1,836 | 351 | 0.0037 | 5.4 |
| W3: triage three pull requests | native | 6 | 22 | 14 | 100,678 | 104,888 | 3,804 | 0.3001 | 40.2 |
| W3: triage three pull requests | brick | 2 | 3 | 6 | 0 | 4,280 | 1,112 | 0.0126 | 8.2 |

| Workload | Native median cost | Brick median cost | Difference |
|---|---|---|---|
| W1 | $0.1060 | $0.0043 | −96% |
| W2 | $0.1877 | $0.0037 | −98% |
| W3 | $0.3001 | $0.0126 | −96% |

Per-run cost spread (min–max): native W1 $0.070–$0.107, W2 $0.171–$0.269, W3 $0.230–$0.474; brick W1
$0.004–$0.008, W2 $0.0036–$0.0039, W3 $0.012–$0.018. The brick arm's first run of each workload paid the
cache write; later runs read the cached prefix, which is why its median cache write is 0.

Verdict agreement: every native run's final verdict matched the brick's verdict for every URL (W1 1/1, W2 1/1,
W3 3/3 in all three runs). Agreement with the brick is not proof that either is right.

Total API spend for this benchmark, including two smoke runs: **$2.13**.

## Method

- **Same question, same model, same system prompt, same data.** The two arms differ only in their tools.
  - *native*: one tool, `github_api_get(path)`, returning the GitHub REST response body as received.
  - *brick*: one tool, `github_blockers(url)`, returning the JSON text `Ashlar.Mcp.Server.Host` returns for the
    `github.blockers` brick.
- **Real data, replayed.** Brick answers were produced by the real MCP host in CI (run `40204440315`,
  2026-10-25 14:58 UTC) and are in `tools/token-benchmark/fixtures/brick/`. GitHub API responses were recorded
  from the live API (24 at 15:05 UTC, 30 more fetched on first use during the run at about 15:50 UTC) and are in
  `tools/token-benchmark/fixtures/github/`; every repetition replays the same bytes.
- **Workloads** (`tools/token-benchmark/targets.json`): W1 `modelcontextprotocol/csharp-sdk#1952`; W2
  `IanFrelinger/Ashlar` release `v0.3.0`; W3 `dotnet/aspire#21307`, `modelcontextprotocol/csharp-sdk#1945` and
  `microsoft/semantic-kernel#14655` in one question.
- **Caching on in both arms**: a cache breakpoint on the system prompt and one on the last block of the
  conversation each turn, the incremental pattern agent loops use.
- **Order**: arms interleaved per repetition (native W1, brick W1, native W2, …) so API drift hits both alike.
- **Stop**: the model's own `end_turn` in all 18 runs (limit 15 requests per run; no run reached it).
- **Cost** is computed from the usage fields and the prices above, not read from an invoice.

## Limits

- **Native arm is the raw API.** A curated GitHub tool that returned only the needed fields would cost less;
  this does not measure that, nor tool search or programmatic tool calling.
- **The brick's own work is not in the token count.** It makes its GitHub API calls in deterministic code;
  that costs requests and time on the machine running it, not model tokens.
- **Snapshot drift.** The brick answers and the GitHub responses were recorded up to about 50 minutes apart,
  on open pull requests. Verdicts agreed, so drift did not change an answer here; it could on a busier PR.
- **Small sample.** One model, three workloads, three repetitions each. The medians are stable relative to the
  gap between arms, but this is not a distribution.
- **Not a correctness benchmark.** Both arms' verdicts are compared with each other, not with a human audit.

## Rerun it

```bash
cd tools/token-benchmark
GITHUB_TOKEN=... ANTHROPIC_API_KEY=... python3 run.py --model claude-sonnet-5-5 \
  --price-in 2 --price-out 10 --cache-write-mult 1.25 --cache-read-mult 0.05 \
  --reps-native 3 --reps-brick 3 --budget-usd 4.5 --out results/<date>
```

The key is read from the environment and sent only as a request header. To refresh the brick answers, run
`capture_brick.py` where `Ashlar.Mcp.Server.Host` can be built (see its docstring); delete
`fixtures/github/` to re-record GitHub responses. Raw per-run records, including each final answer, are in
`tools/token-benchmark/results/2026-10-25/runs.jsonl`.
