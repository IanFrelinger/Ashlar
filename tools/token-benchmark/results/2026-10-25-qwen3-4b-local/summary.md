Local model `qwen3:4b` on Ollama (num_ctx 8192 (brick) / 32768 (ghmcp)); no API cost. Medians over repetitions.

| Workload | Arm | Runs | Requests | Tool calls | Input | Cache write | Cache read | Output | Cost (USD) | Seconds | Verdicts agreeing with brick | Verdicts agreeing with reference |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| w1-single-pr | brick | 1 | 2 | 1 | 1808 | 0 | 0 | 2452 | 0.0000 | 350.3 | 1/1 | 1/1 |
| w1-single-pr | ghmcp | 1 | 2 | 1 | 6006 | 0 | 0 | 2780 | 0.0000 | 753.8 | 1/1 | 1/1 |
| w2-release | brick | 1 | 2 | 1 | 1823 | 0 | 0 | 1221 | 0.0000 | 155.4 | 1/1 | 1/1 |
| w3-triage-three-prs | brick | 1 | 2 | 3 | 3392 | 0 | 0 | 4096 | 0.0000 | 739.6 | 3/3 | 3/3 |

Total spent: $0.0000. Tool responses fetched live on first use: 0.
