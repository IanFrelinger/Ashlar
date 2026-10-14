Phase-end validation on master `03b5a569ebe0`, 2026-10-14T06:09:11Z: 2 pass, 6 warn, 0 fail, 0 error, 4 info.

| Check | Status | Detail |
|---|---|---|
| Master's head | info | origin/master is 03b5a569ebe0 |
| Required checks on master's head | warn | 2 of 5 green on 03b5a569ebe0; pending: cert-gate; not reported on the push (PR-only or path-filtered): shell-lint, Readiness summary |
| Newest master run of every workflow | warn | 6 of the 61 workflow(s) on master end red there: Air-Gapped Multi-Env Tests (failure, 2026-05-23), Cross-Platform Tests (failure, 2026-08-11), Mesh lab TLS gate (failure, 2026-09-08), Mesh lab stress gate (failure, 2026-08-10), Mesh virtual lab gate (failure, 2026-08-11), Runtime Release Promotion (failure, 2026-05-11) |
| Readiness verdict for master's head | warn | readiness-verdict-for-sha: 03b5a569ebe045ef46a3ecc25bd844412f9cd704 REFUSED (in-progress) |
| Last verified readiness commit | info | 1f45a9e5b80d is VERIFIED (push run, 2026-10-14); 1 commit(s) on master since |
| Open pull requests | warn | 15 open (BEHIND 12, BLOCKED 1, DIRTY 2); 4 not updated for more than 3 day(s): #750, #751, #752, #753 |
| Open issues | info | 52 open: 34 labelled bug, 13 agent-task card(s) |
| Repository security settings | warn | public repository with dependabot_security_updates, secret_scanning, secret_scanning_non_provider_patterns, secret_scanning_push_protection, secret_scanning_validity_checks disabled |
| Commits since the last release tag | info | 103 commit(s) on master since v0.2.0; VERSION is 0.2.0 |
| Local work on no remote | warn | 65 local branch(es) hold work that is on no remote and not on master: bx/WS-A/A2, bx/WS-A/A3, bx/WS-A/A4, bx/WS-A/A5, bx/WS-A/A6, bx/WS-A/A7, bx/WS-B/B1, bx/WS-B/B2 ... |
| Paths referenced in the docs exist | pass | ✓ All referenced paths exist (0 missing) |
| The knowledge graph is current | pass | knowledge-graph: current (60 projects, 5183 declared facts, 0 undocumented operator variables) |
