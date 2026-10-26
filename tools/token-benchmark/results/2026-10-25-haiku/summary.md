Model `claude-haiku-5-5`; prices $0.1/MTok input, $0.5/MTok output, cache write 1.25×, cache read 0.1× input; prompts over 100000 tokens at $0.5/$2.5. Medians over repetitions.

| Workload | Arm | Runs | Requests | Tool calls | Input | Cache write | Cache read | Output | Cost (USD) | Seconds | Verdicts agreeing with brick | Verdicts agreeing with reference |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| w1-single-pr | brick | 3 | 2 | 1 | 6 | 0 | 1962 | 470 | 0.0003 | 3.3 | 1/1, 1/1, 1/1 | 1/1, 1/1, 1/1 |
| w1-single-pr | ghmcp | 3 | 3 | 7 | 8 | 17151 | 16660 | 2354 | 0.0035 | 11.1 | 1/1, 1/1, 1/1 | 1/1, 1/1, 1/1 |
| w2-release | brick | 3 | 2 | 1 | 6 | 0 | 1836 | 482 | 0.0003 | 3.4 | 1/1, 1/1, 1/1 | 1/1, 1/1, 1/1 |
| w2-release | ghmcp | 3 | 3 | 6 | 8 | 55014 | 62882 | 2905 | 0.0090 | 14.9 | 1/1, 1/1, 1/1 | 1/1, 1/1, 1/1 |
| w3-triage-three-prs | brick | 3 | 2 | 3 | 6 | 0 | 4280 | 1316 | 0.0007 | 7.2 | 3/3, 3/3, 3/3 | 3/3, 3/3, 3/3 |
| w3-triage-three-prs | ghmcp | 3 | 4 | 17 | 10 | 20196 | 29318 | 4540 | 0.0051 | 20.4 | 2/3, 3/3, 3/3 | 2/3, 3/3, 3/3 |

Total spent: $0.0592. Tool responses fetched live on first use: 13.
