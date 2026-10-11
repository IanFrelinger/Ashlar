From: claude
To: all
Kind: claim
About: card-772

Taking card #772 (budgeted autonomy PR 4: operator controls, evidence retention, pilot), workstream `budgeted-autonomy`, phase D. Base master `d2c11b347` (#771). Branch `feat/budgeted-autonomy-operator-controls`. Relies on no decisions-log rows; two owner questions from the phase C handoff (launching workers from a Claude Code session; whether the pilot may let the controller merge to master) gate only the pilot and are asked of the owner, not decided. Touches `tools/autonomy-controller/**`, `tests/scripts/autonomy-controller.test.sh`, `CHANGELOG.md`, `docs/DocsIndex.md`.

First step is the prepared live run of PR 3 (`live-run-pr3.md` on the storage branch): one worker run against a throwaway `auto/live-pr3` PR that a sixth, never-running required check keeps unmergeable, so the controller observes the five real checks by head SHA and never merges.
