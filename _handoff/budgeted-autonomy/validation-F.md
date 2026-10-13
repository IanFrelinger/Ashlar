Phase-end validation on master `7c50e379fcc4`, 2026-10-13T18:54:57Z: 2 pass, 5 warn, 0 fail, 0 error, 5 info.

| Check | Status | Detail |
|---|---|---|
| Master's head | info | origin/master is 7c50e379fcc4 |
| Required checks on master's head | warn | 1 of 5 green on 7c50e379fcc4; pending: cert-gate, build-core; not reported on the push (PR-only or path-filtered): shell-lint, Readiness summary |
| Newest master run of every workflow | warn | 7 of the 61 workflow(s) on master end red there: Air-Gapped Multi-Env Tests (failure, 2026-05-23), Cross-Platform Tests (failure, 2026-08-11), Mesh lab TLS gate (failure, 2026-09-08), Mesh lab stress gate (failure, 2026-08-10), Mesh virtual lab gate (failure, 2026-08-11), Runtime Release Promotion (failure, 2026-05-11), compat-gate (failure, 2026-09-30) |
| Readiness verdict for master's head | warn | readiness-verdict-for-sha: 7c50e379fcc42810bd7e590ecc04f21ddbc17b64 REFUSED (no-readiness-run) |
| Last verified readiness commit | info | b130228eecf7 is VERIFIED (schedule run, 2026-10-12); 6 commit(s) on master since |
| Open pull requests | info | 14 open (UNKNOWN 14) |
| Open issues | info | 50 open: 32 labelled bug, 11 agent-task card(s) |
| Repository security settings | warn | public repository with dependabot_security_updates, secret_scanning, secret_scanning_non_provider_patterns, secret_scanning_push_protection, secret_scanning_validity_checks disabled |
| Commits since the last release tag | info | 95 commit(s) on master since v0.2.0; VERSION is 0.2.0 |
| Local work on no remote | warn | 69 local branch(es) hold work that is on no remote and not on master: bx/WS-A/A2, bx/WS-A/A3, bx/WS-A/A4, bx/WS-A/A5, bx/WS-A/A6, bx/WS-A/A7, bx/WS-B/B1, bx/WS-B/B2 ... |
| Paths referenced in the docs exist | pass | ✓ All referenced paths exist (0 missing) |
| The knowledge graph is current | pass | knowledge-graph: current (60 projects, 5150 declared facts, 0 undocumented operator variables) |
