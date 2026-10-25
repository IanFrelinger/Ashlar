Model `claude-sonnet-5-5`; prices $2.0/MTok input, $10.0/MTok output, cache write 1.25×, cache read 0.05× input. Medians over repetitions.

| Workload | Arm | Runs | Requests | Tool calls | Input | Cache write | Cache read | Output | Cost (USD) | Seconds | Verdicts agreeing with brick | Verdicts agreeing with reference |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| w1-single-pr | ghmcp | 3 | 4 | 8 | 10 | 11743 | 32196 | 2149 | 0.0541 | 18.7 | 1/1, 1/1, 1/1 | 1/1, 1/1, 1/1 |
| w2-release | ghmcp | 3 | 3 | 5 | 8 | 11938 | 94475 | 1770 | 0.0570 | 13.2 | 1/1, 1/1, 1/1 | 1/1, 1/1, 1/1 |
| w3-triage-three-prs | ghmcp | 3 | 4 | 16 | 10 | 21633 | 39846 | 3617 | 0.0943 | 26.5 | 3/3, 3/3, 3/3 | 3/3, 3/3, 3/3 |

Total spent: $0.7576. Tool responses fetched live on first use: 30.
