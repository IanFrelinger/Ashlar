Model `claude-sonnet-5-5`; prices $2.0/MTok input, $10.0/MTok output, cache write 1.25×, cache read 0.05× input. Medians over repetitions.

| Workload | Arm | Runs | Requests | Tool calls | Input | Cache write | Cache read | Output | Cost (USD) | Verdicts agreeing with brick |
|---|---|---|---|---|---|---|---|---|---|---|
| w1-single-pr | brick | 3 | 2 | 1 | 6 | 0 | 1962 | 402 | 0.0043 | 1/1, 1/1, 1/1 |
| w1-single-pr | native | 3 | 5 | 7 | 12 | 32598 | 89223 | 1560 | 0.1060 | 1/1, 1/1, 1/1 |
| w2-release | brick | 3 | 2 | 1 | 6 | 0 | 1836 | 351 | 0.0037 | 1/1, 1/1, 1/1 |
| w2-release | native | 3 | 4 | 6 | 10 | 62532 | 132252 | 1890 | 0.1877 | 1/1, 1/1, 1/1 |
| w3-triage-three-prs | brick | 3 | 2 | 3 | 6 | 0 | 4280 | 1112 | 0.0126 | 3/3, 3/3, 3/3 |
| w3-triage-three-prs | native | 3 | 6 | 22 | 14 | 100678 | 104888 | 3804 | 0.3001 | 3/3, 3/3, 3/3 |

| Workload | Native median cost | Brick median cost | Brick vs native |
|---|---|---|---|
| w1-single-pr | 0.1060 | 0.0043 | -96% |
| w2-release | 0.1877 | 0.0037 | -98% |
| w3-triage-three-prs | 0.3001 | 0.0126 | -96% |

Total spent: $1.9838. GitHub responses fetched live on first use: 30.
