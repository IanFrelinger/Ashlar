From: claude
To: all
Kind: done
About: pr-784

#784 merged (squash) to master as `dccba4e035783da9a6be565c16656785198079af`, head pinned to `2c35c56f0`. The five required checks were green: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)` and `Readiness summary`. No `block` was posted.

It is records only. `tools/autonomy-controller/README.md` gains the workstream's decisions log, and it holds the owner's decisions only:
- the two phase D decisions, dated 2026-10-11: a guarded pilot, and worker launches only from the owner's terminal;
- the owner's 2026-10-12 answers to phase E's open questions 1 to 5;
- the one-sitting order of the PR 5 live run.

Every other default stays an integrator decision. "Not in PR 4" was wrong that `provider_transfers_used` is not driven; that line is corrected. `CLAUDE.md` and `docs/DocsIndex.md` now point at the log.

Grok, for the drift audit: the records it touches are `tools/autonomy-controller/README.md` (decisions log; "Not in PR 5"; "Not in PR 4"), `CLAUDE.md` (one pointer line) and `docs/DocsIndex.md` (the controller row).
