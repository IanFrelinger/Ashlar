Phase-end validation on master `b130228eecf7`, 2026-10-12T18:23:05Z: 4 pass, 3 warn, 0 fail, 0 error, 5 info.

| Check | Status | Detail |
|---|---|---|
| Master's head | info | origin/master is b130228eecf7 |
| Required checks on master's head | pass | 4 of 5 green on b130228eecf7; not reported on the push (PR-only or path-filtered): shell-lint |
| Newest master run of every workflow | warn | 7 of the 61 workflow(s) on master end red there: Air-Gapped Multi-Env Tests (failure, 2026-05-23), Cross-Platform Tests (failure, 2026-08-11), Mesh lab TLS gate (failure, 2026-09-08), Mesh lab stress gate (failure, 2026-08-10), Mesh virtual lab gate (failure, 2026-08-11), Runtime Release Promotion (failure, 2026-05-11), compat-gate (failure, 2026-09-30) |
| Readiness verdict for master's head | pass | readiness-verdict-for-sha: b130228eecf7a76ff38cb2cb09425d8b41ca13eb VERIFIED |
| Last verified readiness commit | info | b130228eecf7 is VERIFIED (schedule run, 2026-10-12); 0 commit(s) on master since |
| Open pull requests | info | 15 open (BEHIND 12, CLEAN 1, DIRTY 2) |
| Open issues | info | 52 open: 32 labelled bug, 13 agent-task card(s) |
| Repository security settings | warn | public repository with dependabot_security_updates, secret_scanning, secret_scanning_non_provider_patterns, secret_scanning_push_protection, secret_scanning_validity_checks disabled |
| Commits since the last release tag | info | 89 commit(s) on master since v0.2.0; VERSION is 0.2.0 |
| Local work on no remote | warn | 65 local branch(es) hold work that is on no remote and not on master: bx/WS-A/A2, bx/WS-A/A3, bx/WS-A/A4, bx/WS-A/A5, bx/WS-A/A6, bx/WS-A/A7, bx/WS-B/B1, bx/WS-B/B2 ... |
| Paths referenced in the docs exist | pass | ✓ All referenced paths exist (0 missing) |
| The knowledge graph is current | pass | knowledge-graph: current (60 projects, 5150 declared facts, 0 undocumented operator variables) |
