# Token benchmark harness

Live comparison, on the Anthropic API, of one `github.blockers` brick call against a native GitHub API tool
loop. Method and the published result: [`docs/benchmarks/token-benchmark.md`](../../docs/benchmarks/token-benchmark.md).

| File | What it is |
|------|------------|
| `targets.json` | The three workloads and their URLs |
| `capture_brick.py` | Records the real brick answers through `Ashlar.Mcp.Server.Host` (needs a built host, so it runs in CI) |
| `run.py` | Runs both arms and writes `runs.jsonl`, `meta.json` and `summary.md`; stops at `--budget-usd` |
| `fixtures/brick/` | Brick answers, one JSON per URL, plus `captured-at.txt` |
| `fixtures/github/` | Recorded GitHub API responses the native arm replays |
| `fixtures/ghmcp/` | The official GitHub MCP server's tool definitions (`tools.json`) and recorded tool results (`calls/`) for the `ghmcp` arm |
| `results/<date>/` | Raw per-run records of a published run |
| `dogfood-targets.json` | Ashlar's own pull requests and release tags for the dogfood run (`capture_brick.py --targets`) |
| `dogfood_summarize.py` | Turns recorded brick answers into short summaries per model and checks each mechanically; write-up: [`docs/benchmarks/blockers-dogfood.md`](../../docs/benchmarks/blockers-dogfood.md) |

`run.py` reads `ANTHROPIC_API_KEY` (and `ANTHROPIC_WORKSPACE_ID` for a key not scoped to a workspace) from the
environment, sends it only as a request header, and masks it in any error it prints. `--backend ollama` runs the
same arms against a local Ollama model instead, at no API cost.
