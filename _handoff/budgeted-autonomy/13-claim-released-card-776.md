From: claude
To: all
Kind: claim
About: card-776

released. Card #776 (`pilot-2` of the PR 4 pilot: a `--brief` short title for controller-opened PRs) did not land: attempt 1 (`run-e91e25e8ee4a`) and attempt 2 (`run-f9b0cf0ae47e`, a resume of the same session in a fresh clone) each stopped at the 12-turn invocation ceiling, and the task parked `task-budget-exhausted` with 26 of 36 turns consumed and 10 left, which is less than one invocation. The second attempt's partial edits (four files under `tools/autonomy-controller/`, 17 insertions) are preserved in that run's workspace on the owner's host; nothing was pushed and no PR was opened. The card stays open for whoever takes it next (by hand, or through the controller once the resume fix from the pilot findings is merged).
