From: claude
To: grok
Kind: handoff
About: pr-773

PR #773 (card #772, budgeted autonomy PR 4: operator controls, evidence retention, pilot) is ready: head `1f186b8ab` on `feat/budgeted-autonomy-operator-controls`, base master `d2c11b347`.

**What changed.** The operator sees and steers the controller without a model: `status` names each task's PR record and the evidence of its last run (run directory, manifest, stdout, stderr, the transcript located by session id, workspace, archive); `report` renders status and metrics as Markdown from the ledger; `metrics` is counts and sums from the ledger with the estimated API value kept apart from turns; `backup`/`restore` move the ledger through the SQLite backup API (an unknown schema and an un-flagged live state are refused, the restored file is opened and verified); `evidence archive <run>` tars the run directory with the run's ledger rows and never overwrites. `pause`, `resume`, `provider disable|enable` and `run cancel` are proven to take effect at the next tick, including the cancel of a running repair (round spent, PR open and watched, nothing launches).

**Verified.** 383 tests on the host and in the devtest container (floor raised 290 to 360); `check-docs-phantom-paths` clean; the knowledge graph unchanged; six lane mutations observed red by hand (in the PR body); a 20-mutation batch through `scripts/mutation-check.sh` is running against `1f186b8ab` and its summary lines go into the PR body before the merge.

**Not done, and said so.** The live run of the PR 3 lifecycle and the pilot of three real tasks did not run: the Claude Code session's classifier refuses `tick --adapter claude --gh cli` (third phase in a row). Both are prepared for the owner's terminal (`live-run-pr3.md`, `pilot-pr4.md` on the storage branch); whether the controller may merge to master in the pilot is an open owner question.

**Records and docs touched** (for the drift audit): `tools/autonomy-controller/README.md` (new "Operator controls and evidence (PR 4)" section, five new command rows, two layout rows, "Not in PR 4", the intro paragraph), `CHANGELOG.md` `[Unreleased]` (new PR 4 block), `docs/DocsIndex.md` (the controller row), `tests/scripts/autonomy-controller.test.sh` (floor 360), new `ashlar_controller/{metrics,evidence}.py`, `tests/{test_metrics,test_evidence,test_operator_controls,test_cli_operator}.py`.
