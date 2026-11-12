# Gate decision records

`GateStore.DecideAsync` records a human decision on a held proposal. The actor is a caller-supplied attribution label, not an authenticated principal. Automatic outcomes use `gate`.

## New human actor labels

The #478 hardening uses these integrator defaults:

- At most 256 UTF-16 code units, with no leading or trailing whitespace. A supplementary Unicode character occupies two units.
- No C0/C1/DEL controls, directional marks/overrides/isolates, Unicode line or paragraph separators, or malformed surrogate sequences. Exceptions identify the actor argument without echoing its unsafe value.
- Reserve `gate` case-insensitively. For this comparison only, apply NFKC, ignore format characters and variation selectors, and trim whitespace exposed by that removal. Reject labels containing only those ignored characters and whitespace. This is not a general Unicode-confusable or authentication guarantee.

If the runtime cannot perform Unicode normalization, including invariant globalization mode, human decisions fail closed before persistence. Automatic outcomes and historical reads remain available. Fresh-process tests exercise both modes on each supported Manifest runtime.

Accepted labels are stored exactly as supplied. Ordinary Unicode, combining marks, CJK/Arabic names, emoji, ZWJ and ZWNJ remain supported. Validation runs before acquiring the store lock, sweeping orphan files, signing or persisting a decision.

## Historical records and presentation

Historical records remain readable even if their actor would be rejected by today's human-write API. No read-time normalization or new serialized field is introduced. Existing JSON files, canonical payloads, signatures and nullable `StoreId` compatibility remain unchanged. A historical `gate` label alone cannot prove whether a person or an automatic outcome originally chose it.

Human CLI output escapes and bounds untrusted decision fields and store errors using the shared `UntrustedText` renderer. Machine-readable JSON preserves its data. Gate diffs retain intentional newlines within an 8,000-character / 80-line display budget, with each line bounded and escaped; truncation is visible. These display limits never rewrite the persisted record. They are per field/diff, not a bound on the number of records or courses listed.

## Remaining scope

A gate record contains the current state and latest decision. It is not an append-only decision journal, and refused re-decide attempts are not recorded. Caller-supplied `DecidedAt`, clock/budget semantics and a decision-history design remain outstanding in [#478](https://github.com/IanFrelinger/Ashlar/issues/478). This patch does not establish same-user or container storage isolation; that remains [#480](https://github.com/IanFrelinger/Ashlar/issues/480).

Regression coverage is in `GateDecisionIdentityTests`, `GateDecisionNormalizationProcessTests`, `GateDecisionDisplaySecurityTests`, `KeysGateDisplaySecurityTests` and `ReportBackgroundAgentCommandTests`. Exact production mutations are declared in [`decision-records.json`](../scripts/mutations/decision-records.json); a manifest is a test plan, not execution evidence.
