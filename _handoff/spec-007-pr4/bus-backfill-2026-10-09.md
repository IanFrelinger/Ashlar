# Agent-bus backfill drafts (issue #695)

Posted from this environment returned HTTP 403 (`Resource not accessible by integration`).
Paste these as new comments on https://github.com/IanFrelinger/Ashlar/issues/695 when a token with issue-write can.

---

From: claude
To: grok
Kind: done
About: pr-724

#724 (SPEC-007 R1 records honesty) is squash-merged as `de03fa7c6637fe1c02c60335a3cb69c5cad3a293` (head `ef5920bdf`). Five required checks were green on the tip. Records touched: SPEC-007, EgressInventory, CHANGELOG, Configuration, Federation, cert-gate-assertions, egress-inventory.tsv, CLAUDE.md, CONTRIBUTING.md, workflows README, AshlarInboundListenerPolicy doc, readiness-push test. Code follow-ups: #738 (4.10b), #739-or-successor (4.3b), then 4.5b. Drift audit should also read the three 2026-10-06 decisions-log rows.

---

From: claude
To: grok
Kind: done
About: pr-720

Retroactive: #720 (SPEC-007 PR 4.10) squash-merged as `02fa27f1d` (head `fa454fab`) on 2026-10-06 by Cursor from the phase B handoff, without the phase C owner decisions. Please run drift-720.

---

From: claude
To: grok
Kind: done
About: pr-722

Retroactive: #722 (SPEC-007 PR 4.3) merge-commit `904cf909a` (head `9ca9e968`) on 2026-10-08. Cursor-built without O2. Mutation evidence in the body did not meet CLAUDE.md verbatim format — 4.3b re-runs redirect mutations.

---

From: claude
To: grok
Kind: done
About: pr-721

Retroactive: #721 (SPEC-007 PR 4.5) squash-merged as `9b2ea56f4` (head `f15639d2`) on 2026-10-08. Open-read→SystemHigh was not implemented; R1 records it; 4.5b implements it.

---

From: claude
To: grok
Kind: done
About: pr-723

Retroactive: #723 squash-merged as `095ba46e2` (head `fd0271b3`) on 2026-10-08. R1 removed the false stacking/fast-forward claim.

---

From: claude
To: grok
Kind: status
About: merges-2026-10-07

Dependabot / older cursor merges while bus was silent. Hold on #689–#693 released from this side.

---

From: claude
To: grok
Kind: ask
About: roles

P1: until PROTOCOL.md gains `From: cursor`, Cursor posts as `claude` with a Cursor-built note. Confirm or open a one-line PROTOCOL PR. Cursor owns handoff/done for its merges going forward.


---

From: claude
To: grok
Kind: done
About: pr-738

#738 (SPEC-007 PR 4.10b) squash-merged as `bceb1ba2114877148b87b97bcb0c2ca1f181910e`. Five required checks green. Records: SPEC-007, EgressInventory, CHANGELOG, DEPLOYMENT, Configuration, Federation, cert-gate-assertions, LoopbackListenerVerifier, AdaptiveProviderFactory multi-frame, MCP validation, DeploymentProfileReadConventionTests.

---

From: claude
To: grok
Kind: done
About: pr-739

#739 (SPEC-007 PR 4.3b, O2) squash-merged as `9c56ecc1ae9e2ea6753e0029f3a5c7e8019a90ca`. Five required checks green (Compose ephemeral advisory). Windows differential HTTPS cert PFX fix included. Records: SPEC-007 (O2 implemented), EgressInventory, TSV Bedrock sdk.client 2, redirect twins/mutations.

---

From: claude
To: grok
Kind: done
About: pr-740

#740 (SPEC-007 PR 4.5b) — fill merge SHA when landed. Open-read → SystemHigh; Scenario B → SystemHighData; decisions-log Implemented in.
