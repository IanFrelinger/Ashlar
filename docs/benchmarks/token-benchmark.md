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

**Against a fairer baseline** ([follow-up runs](#follow-up-runs-same-day)): the official GitHub MCP server's
pull request, checks and release tools already halve to third the raw-API cost, and the brick still cost
**87–94% less** than that on Sonnet 5.5 and **86–97% less** on Haiku 5.5, with the same verdicts. A local 4B
model on this CPU-only machine answered correctly from the brick; the native loop was not practical there. The
figure to quote is the one against the GitHub MCP server, with its scope.

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

## Follow-up runs (same day)

Approved after the first run: a fairer native baseline, and smaller models. Same three workloads, same system
prompt and question, same recorded data, prompt caching on. Spend: **$0.86** ($0.80 against a $3 cap for the
GitHub MCP arm on Sonnet, $0.06 against a $2 cap for Haiku; the local model costs nothing).

### A fairer baseline: the official GitHub MCP server

A third arm, `ghmcp`, gives the agent the official [GitHub MCP server](https://github.com/github/github-mcp-server)'s
own tools (v2.0.3, `--read-only`), restricted to the six a blockers question needs: `pull_request_read` (whose
methods include `get`, `get_status`, `get_reviews` and `get_check_runs`), `get_release_by_tag`, `get_tag`,
`get_commit`, `actions_list` and `actions_get`, with the server's own names, descriptions and schemas
(`tools/token-benchmark/fixtures/ghmcp/tools.json`). Answers are the server's own tool results, recorded from the
live server on first use and replayed after (`fixtures/ghmcp/calls/`). None of the four pull requests had
changed since before the brick answers were captured (same head commit and `updated_at`, checked at about 16:30 UTC),
so the recorded responses describe the same state.

Medians of three runs, `claude-sonnet-5-5`, prices as above:

| Workload | Raw API (native) | GitHub MCP server | Brick | Brick vs GitHub MCP | GitHub MCP vs raw API |
|---|---|---|---|---|---|
| W1: one pull request | $0.1060 | $0.0541 | $0.0043 | −92% | −49% |
| W2: one release | $0.1877 | $0.0570 | $0.0037 | −94% | −70% |
| W3: triage three pull requests | $0.3001 | $0.0943 | $0.0126 | −87% | −69% |

GitHub MCP arm detail: 8, 5 and 16 tool calls (median) on W1–W3; 18.7, 13.2 and 26.5 seconds against the brick's
4.4, 5.4 and 8.2. Per-run cost spread: W1 $0.054–$0.056, W2 $0.057–$0.189 (the first run paid the cache write for a
long release response), W3 $0.093–$0.103. Every verdict matched the brick's and the first run's (15 of 15).

### Smaller models: Claude Haiku 5.5

The brick arm and the cheapest native arm (the GitHub MCP server) on `claude-haiku-5-5`, three runs each. Prices:
$0.10 per million input and $0.50 per million output tokens for prompts up to 100,000 tokens ($0.50 and $2.50
above that, applied per request), cache write 1.25× and cache read 0.10× input.

| Workload | GitHub MCP server | Brick | Brick vs GitHub MCP | Seconds (GitHub MCP / brick) |
|---|---|---|---|---|
| W1 | $0.0035 | $0.0003 | −91% | 11.1 / 3.3 |
| W2 | $0.0090 | $0.0003 | −97% | 14.9 / 3.4 |
| W3 | $0.0051 | $0.0007 | −86% | 20.4 / 7.2 |

**Verdicts against Sonnet's** (the first run's verdicts, which every Sonnet run agreed on): brick arm 15 of 15;
GitHub MCP arm 14 of 15 by strict URL match. The miss is one W3 run that wrote its verdict line for
`microsoft/aspire/pull/21307`, the name GitHub now returns for the repository the question called `dotnet/aspire`;
its verdict, `blocked`, is Sonnet's. Counting that, 15 of 15.

### A local model: qwen3:4b on Ollama

This machine has 4 CPU cores, no GPU and about 5 GB of free memory, so an 8B model (about 5 GB of weights at
4-bit, before any context) did not fit; `qwen3:4b` (2.5 GB, tool calling) did. Ollama 0.40.11, temperature 0,
8,192-token context for the brick arm and 32,768 for the GitHub MCP arm. One run per cell, because each takes
minutes: prompt processing ran at about 16–115 tokens a second and generation at about 4.6.

| Workload | Arm | Tool calls | Seconds | Verdict matches Sonnet |
|---|---|---|---|---|
| W1 | brick | 1 | 350 | yes |
| W2 | brick | 1 | 155 | yes |
| W3 | brick | 3 | 740 | 3 of 3 |
| W1 | GitHub MCP server | 1 | 754 | yes |

The brick arm answered every question correctly on a CPU-only 4B model. The GitHub MCP arm was run on W1 only: it
made one call (`pull_request_read` `get`), read `mergeable_state: blocked`, and stopped there without reading the
checks or reviews, so its verdict was right but it named no specific blocker. W2 and W3 on that arm were not run:
at the measured speed a run takes 15–40 minutes, and W3's responses do not fit the largest context this memory
holds. Qwen3 emitted its reasoning despite `think: false`; `<think>` blocks are removed before verdicts are read.
Local token counts are Ollama's `prompt_eval_count` and `eval_count`; the first excludes a reused prefix, so they
are not comparable with the API's usage fields and are not tabulated here (they are in `runs.jsonl`).

### What changes in the claim

The defensible figure is against the GitHub MCP server, not the raw API: **in this benchmark, one brick call cost
87–94% less than an agent using the official GitHub MCP server's tools on Sonnet 5.5 (86–97% on Haiku 5.5), with the
same verdicts**. The raw-API figure stays above as the first measurement. Limits as before: three workloads, one
question type, recorded data, verdict agreement rather than an audited ground truth. The GitHub MCP server's
tool set was restricted to six tools; its full default set would add definition tokens to every request.

Raw records: `tools/token-benchmark/results/2026-10-25-ghmcp-sonnet/`, `results/2026-10-25-haiku/`,
`results/2026-10-25-qwen3-4b-local/`.

## Rerun it

```bash
cd tools/token-benchmark
GITHUB_TOKEN=... ANTHROPIC_API_KEY=... python3 run.py --model claude-sonnet-5-5 \
  --price-in 2 --price-out 10 --cache-write-mult 1.25 --cache-read-mult 0.05 \
  --reps-native 3 --reps-brick 3 --budget-usd 4.5 --out results/<date>
```

For the GitHub MCP arm add `--reps ghmcp=3` (or `--reps native=3,ghmcp=3,brick=3`); calls missing from
`fixtures/ghmcp/calls/` are recorded through `GHMCP_COMMAND` (for example
`github-mcp-server stdio --read-only --toolsets=pull_requests,repos,actions`, with `GITHUB_PERSONAL_ACCESS_TOKEN`
set). Haiku's tiered prices: `--long-threshold 100000 --long-price-in 0.5 --long-price-out 2.5`. A local model:
`--backend ollama --model qwen3:4b --num-ctx 8192`. `--reference results/2026-10-25/runs.jsonl` adds verdict
agreement with the first run.

The key is read from the environment and sent only as a request header. To refresh the brick answers, run
`capture_brick.py` where `Ashlar.Mcp.Server.Host` can be built (see its docstring); delete
`fixtures/github/` to re-record GitHub responses. Raw per-run records, including each final answer, are in
`tools/token-benchmark/results/2026-10-25/runs.jsonl`.
