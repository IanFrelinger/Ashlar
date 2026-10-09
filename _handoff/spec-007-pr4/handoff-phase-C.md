# SPEC-007 PR 4 handoff: end of phase C, start of phase D

*Written 2026-10-09 ~22:40 UTC near the end of phase C. Start the next thread with the prompt in §9, or with `/start-phase spec-007-pr4`.*

## 0. How we work: one thread per phase

This workstream runs one phase per thread (`_handoff/phases/README.md`):
- end each phase with `/handoff spec-007-pr4 C`;
- start each phase with `/start-phase spec-007-pr4`.

The handoff is published to `claude/spec-007-pr4-workspace`, sent to the owner, and the owner is notified. Push every in-flight branch to GitHub before writing the handoff.

## 1. Where things stand

- **Master:** `bc1777dfc` (#740 squash-merged).
- **Merged this phase / remediation after Cursor phase C:**

| PR | What | Merge SHA |
|---|---|---|
| #720 | SPEC-007 PR 4.10 AG/SW hygiene | `02fa27f1d` |
| #722 | SPEC-007 PR 4.3 redirects | `904cf909a` (merge commit) |
| #721 | SPEC-007 PR 4.5 producers | `9b2ea56f4` |
| #723 | CI: no readiness double-fire on `cursor/**` | `095ba46e2` |
| #724 | R1 records honesty (2026-10-06 decisions) | `de03fa7c6` |
| #738 | 4.10b AG/SW inbound + hygiene | `bceb1ba21` |
| #739 | 4.3b never follow into Host (O2) | `9c56ecc1a` |
| #740 | 4.5b open-read → SystemHigh | `bc1777dfc` |

- **Superseded drafts closed:** #725, #727, #726 (Codex tips harvested onto `cursor/spec-007-*-d476`).
- **Readiness verdict for master's head:** tip `bc1777dfc` — PR tip `af999cc92` had Readiness summary SUCCESS; master-push readiness may still be catching up (re-check with `scripts/release/readiness-verdict-for-sha.sh`).
- **Agent-bus (#695):** silent since 2026-10-06; Cursor token cannot comment (HTTP 403). Drafts: `scratchpad/bus-backfill-2026-10-09.md` (also attach). Paste when issue-write is available.
- **This phase's exit criteria:** three 2026-10-06 owner decisions recorded (R1) and implemented (4.5b open-read, 4.3b O2; O1/RAG already in #721); AG/SW inbound hardened (4.10b); false stacking/FF claim removed. Met: #740 on master `bc1777dfc`; required checks were green on tip `af999cc92`.

## 2. Read these first

- **In the repo:** `CLAUDE.md`, `docs/specs/SPEC-007-security-labels-and-reference-monitor.md` (decisions log), `docs/EgressInventory.md`, `ci/cert-gate-assertions.md`, `CONTINUITY.md`.
- **On the storage branch:** prior pause handoff; lane branches `claude/spec-007-pr4-{4.3-redirects,4.5-producers,4.10-agsw}` (archive after content confirmed on master). Publish `ws/design-4.5-amendment.md` if not already on the storage branch.

## 3. Decisions

Recorded in SPEC-007 decisions log (R1 + code PRs):
1. **2026-10-06 open-read → SystemHigh** (Scenario B → `SystemHighData`) — implemented in #740 / 4.5b.
2. **2026-10-06 Q8 clarified / O1** — custom RAG levels → SystemHigh; C3 deferred — implemented in #721.
3. **2026-10-06 O2** — never follow redirect into Host — implemented in #739.

Owner Q1–Q9 from the 2026-10-08 review were **not answered** in this remediation. Defaults taken in code PRs where a choice was required to land:
- Q1 mesh serve: fail-boot without loopback bind (literal Q6).
- Q4 multi-frame on AG: refuse cloud resolve (4.10b).
- Q5 profile options: `PostConfigure` reasserts strictest.
- Q6 D16: observe non-`local:`/`cloud:` (4.5b D16 row 2026-10-08).
- Q2 merge method / Q3 bus identity / Q7–Q9: still open.

## 4. Plan and live status

| Phase | Scope | Ends when |
|---|---|---|
| A–B | … prior | done (pause handoff) |
| **C** | Owner decisions + phase C PRs + records honesty | **done when #740 on master + readiness verified** |
| **D** | PR 4.7 / 4.8 enforcement path | starts after C exit criteria |

Work items: all listed in §1. Lane branches: keep until archive-tag after #740.

## 5. How to do the next phase's routine work

1. Build/test only via `scripts/test-in-container.sh`.
2. Mutation-check every behavioural change; quote `mutation …: KILLED red=failed:…` lines in the PR body.
3. Bus: `Kind: handoff` before merge, `Kind: done` after (From: `claude` until PROTOCOL gains `cursor`).
4. Squash-merge by default; required five green under `strict`.
5. After each merge, expect every other PR BEHIND; land non-core-path first; one settled tip then wait.

## 6. Environment facts

- Five required checks: `cert-gate`, `build-core`, `shell-lint`, `lychee (README + docs)`, `Readiness summary`.
- Layer-boundary `verify` fails advisory when a PR targeting master touches `application/` (runtime/kernel-first rule) — did not block #738/#739.
- UAT `k8s-manifests-parse` and Compose ephemeral lane have been advisory red / flake on some tips.
- Windows differential HTTPS needs PFX-persisted certs (fixed in #739 tip).
- Bus issue comments from Cursor integration token: 403.

## 7. Queued work

- Paste bus backfill (`scratchpad/bus-backfill-2026-10-09.md`) + done for #738/#739/#740.
- Phase C handoff publish (this file) + notify owner.
- Archive-tag lane branches after harvest confirmed.
- R2: re-measure floors / TSV / cert counts once after #740.
- Owner Q2, Q3, Q7–Q9 still open.
- Phase D: 4.7 enforcement twins (including those listed "for 4.7" in the review).

## 8. Ways of working the owner has endorsed

- One thread per phase; handoffs on storage branch.
- Claude merges on green; Grok drift-audits (when bus posts exist).
- Squash preferred; stacking does **not** skip Readiness under `strict` (R1 corrected the false claim).

## 9. Starting prompt for the next phase

> Execute **phase D** of `spec-007-pr4`. Run `/start-phase spec-007-pr4`, or read this handoff in full, starting with §0. Scope: SPEC-007 PR 4.7 (and 4.8 as scoped in the design) — enforcement routes that act on `Refused`, building on the 4.3b enforcement twins and the open-read/`SystemHighData` Scenario B expectation. Build and test only through `scripts/test-in-container.sh`. If anything the owner has not decided blocks you, ask instead of choosing. When the phase is done, run `/handoff spec-007-pr4 D` and notify me.
