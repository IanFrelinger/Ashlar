# SPEC-008 — Flight recorder and evidence bundle (`ashlar-evidence/v1`), stub

**Status: STUB — decisions log only (2026-10-25).** This file holds the owner decisions that
bind the Flight Recorder and the shared evidence-bundle format before any implementation PR.
The normative body (record envelope, span mapping, capture levels, commitments, checkpoints,
anchors, `ashlar-evidence/v1`, verification algorithm, threat model) lands in the MVP docs-only
spec PR after 0.4.0 ships and after #869, #870 and #873 merge. Until then, no code may assume
behaviour beyond the rows below.

Design context (not a repo path): the consumer-pilot design that produced O1–O8 is
`flight-recorder-design.md` in the ashlar-consumer-pilot project store. Product language today:
prefer "flight recorder" as a description; CLI verb `ashlar flight`; short name "Ashlar Flight"
until a trademark search clears "Ashlar Flight Recorder" (O1).

Signing of checkpoints and exports reuses SPEC-006 (`OperatorKey`, `CanonicalJson`). Label and
egress constraints on capture and export follow SPEC-007.

---

## Decisions log (added 2026-10-25)

On 2026-10-25 the owner delegated the Flight Recorder decisions and approved every recommended
default, O1–O8. Each row below is one of those defaults, recorded as an owner decision by
delegation. Anything not covered here is an integrator decision and is recorded in the PR that
takes it.

| Date | Applies to | Decision |
|---|---|---|
| 2026-10-25 | O1 product name (owner-delegated default) | Keep "flight recorder" as a description. Ship the CLI verb as `ashlar flight`. Ship the product as "Ashlar Flight Recorder" only after a trademark search; **defer that search**. Until then, docs and UI use `ashlar flight` and "Ashlar Flight". |
| 2026-10-25 | O2 signed exports (owner-delegated default) | **Signed exports are open** (LICENSING covenants 1, 4 and 7; ledger signing is already open). Paid = compliance reports, Fleet aggregation, org retention, witness/TSA, and key custody. |
| 2026-10-25 | O3 spec number and scope (owner-delegated default) | **One SPEC-008** covers the flight recorder and `ashlar-evidence/v1`, shared with release evidence. Covenant 7's promised evidence-bundle format is this spec. |
| 2026-10-25 | O4 MCP audit trail (owner-delegated default) | Emit both `MCP_BRICK_INVOKED` (barrier NDJSON) and the flight run for **one minor release**, then make the flight run the only MCP audit trail. |
| 2026-10-25 | O5 default capture level (owner-delegated default) | Default capture level in the MCP host is **`actions`**. Content and reasoning are opt-in. |
| 2026-10-25 | O6 key custody (owner-delegated default) | Checkpoints use the **operator key** in the MVP (SPEC-006). A separate-user or hardware-backed key is a phase-2 option. |
| 2026-10-25 | O7 reasoning under AirGapped / SecureWorkstation (owner-delegated default) | A `reasoning` opt-in is **allowed** under `AirGapped` and `SecureWorkstation`: capture stays local, and export applies the label ceiling. |
| 2026-10-25 | O8 Tracekit / C2SP interop (owner-delegated default) | Evaluate Tracekit bundle or C2SP witness interop **after the MVP**; do not block the MVP on it. |
