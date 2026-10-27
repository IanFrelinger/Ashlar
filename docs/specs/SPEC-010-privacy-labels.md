# SPEC-010 — Privacy labels replace classification levels, v1

**Status: PROPOSED — 2026-10-27.** Written by Claude Code after the owner accepted a security review on 2026-10-27
(decisions log, below). Owner: @IanFrelinger.

**Start here:** implement **PR 1 (§7)** and stop. Each later PR waits for the one before it to merge. Ask the owner
about anything in §11 rather than guessing.

**What this replaces.** SPEC-007's data model:
- ordered classification levels and `SystemHigh`
- compartments and caveats
- clearances, the read-up check and composition by clearance
- the planned downgrade path

That is [SPEC-007](SPEC-007-security-labels-and-reference-monitor.md) §2.1–§2.4, §8, PR 5 and PR 6.

**What it keeps.** SPEC-007's egress guard:
- the inventory of outbound paths and its convention test
- subject frames
- report and enforce modes, and the profiles that enforce
- the refusal surface
- the leak tests

Those keep following SPEC-007, its decisions log and [the disposition index](SPEC-007-disposition-index.md), except
where this spec says otherwise.

Master at the time of writing: `0e89da03` (0.4.0, plus #883 to #886).

## 1. Why

On 2026-10-27 the owner relayed a security reviewer's advice and accepted it the same day:

- Drop classification levels. Some uses of classification need formal proofs that the application can't leak.
- Simple label blockers for privacy are a different, smaller claim.
- Security claims need proof, which is hard for a system under attack that can be in many states.
- Frame data controls in privacy terms.

SPEC-007 modelled data protection on Bell–LaPadula, the model behind national-security classification. The
multilevel-security literature explains why that model promises more than an application library can deliver.
The sources are Ross Anderson's *Security Engineering* (3rd edition, chapters 9, 10 and 28); Karger and Schell,
"Thirty Years Later: Lessons from the Multics Security Evaluation" (2002); and Lampson, "A Note on the Confinement
Problem" (1973).

- **The monitor needs a kernel.** The model's guarantees rest on a reference monitor that is tamper-proof, always
  invoked and small enough to verify. Historically that was an operating-system kernel. Ashlar's monitor is a class
  in the same process as the code it mediates.
- **Downgrading can't be verified.** Moving data down a level through trusted subjects is outside what the model can
  verify, and a system that obeys the model can still declassify everything. SPEC-007 planned exactly that path:
  certified downgrade skills and a human sign-off.
- **High-water marks drift up.** They only rise, so real systems ended up running "system high", with a guard at the
  boundary. The guard is the part that survived in practice.
- **The LLM is the hostile program.** An LLM inside a subject is the program the no-write-down rule was designed
  against. Its text output can carry anything it read, and no filter or reviewer can certify that output clean.
- **Privacy is lateral.** Privacy problems are about who may see what, consent, audit and aggregation, not about
  levels. Medical-records systems rejected multilevel labels for that reason.
- **Tests aren't proof.** Testing shows the cases it covers. It does not show that nothing leaks.

What SPEC-007 built that survives this critique is the egress guard. This spec keeps the guard and replaces what it
decides on. Ordered classification levels become flat privacy labels, and each label names where the data must not
go.

Ashlar provides *privacy checks at the egress boundary*. It is not a multilevel-secure system or a cross-domain
solution, and it does not prevent leaks. Nothing in code or docs may claim otherwise (§10).

## 2. What runs today (read before writing)

This section was read from `origin/master` on 2026-10-27. PR 3a confirms the first point with a test.

- **Every production egress decision is made at `SystemHigh`.**
  - The only production `EgressSubject.Enter` is in `SelfExtendRunnerAdapter.cs:251`, with the floor `SystemHigh`.
  - A read scope is pinned at `SystemHigh` until it reports.
  - With no live frame the label is `SystemHigh`, with the basis `no-subject`.
  - A mark only rises.

  So on `AirGapped` and `SecureWorkstation` only `Host` destinations pass. The `Internal` and `Confidential`
  destination labels matter only in tests.
- **Clearances do nothing in production.**
  - The `clearances` values in `ashlar.policy.yaml` are parsed and validated when a policy loads
    (`PolicyLoader.cs:170-216`), then never used.
  - These have no production caller: `SkillComposition.Decide`, `SubjectClearance`, `ReferenceMonitor.CanRead`,
    `SecurityLabel.Meet`, `AshlarHostingOptions.Clearance` and `ExtensionPackage.HighestLevel`.
- **No production code creates a compartment or a caveat.**
- **The destination table already comes from the data-sensitivity flags.** `EgressDestinations.LabelOf` gives each
  class the highest built-in level whose flags allow that class. This is pinned by
  `EgressGuardDecisionTests.The_destination_labels_are_the_highest_built_in_level_allowing_that_destination`.

| Class | Matches (first match wins) | Label today |
|---|---|---|
| `Host` | `unix` and `npipe`, `localhost`, `*.localhost`, loopback addresses, `host:` names | `SystemHigh` |
| `ExternalModel` | family `model.*` | `Internal` |
| `WebSearch` | family `web-search` | `Confidential` |
| `NetworkExport` | `a2a`, `grpc`, `mcp`, `http`, `http.factory`, `mesh.*`, `file.export`, `process`, `telemetry` | `Internal` |
| `Unknown` | anything else | `Public` |

The flags (`DataSensitivityLevels`):

| Level | Value | `AllowsExternalLLM` | `AllowsWebSearch` | `RequiresLocalOnly` | `AllowsNetworkExports` |
|---|---|---|---|---|---|
| `Public` | 0 | yes | yes | no | yes |
| `Internal` | 1 | yes | yes | no | yes |
| `Confidential` | 2 | no | yes | no | no |
| `Secret` | 3 | no | no | no | no |
| `TopSecret` | 4 | no | no | yes | no |

The same flags are already enforced elsewhere as simple blockers. `BackgroundAgentConfigLoader.cs:230-233` turns
them into an `ExfiltrationPolicy`, which `DataExfiltrationPolicy` applies as tool deny lists. With privacy labels,
the guard speaks the same language.

## 3. The model (normative)

### 3.1 Labels

A **privacy label** names a kind of destination the data must not reach. Version 1 has four, and the set is closed.

| Label | Text | The data must not reach | Comes from |
|---|---|---|---|
| Local only | `local-only` | anything outside the host boundary | `RequiresLocalOnly` |
| No external model | `no-external-model` | a model that is not on this machine (`ExternalModel`) | `AllowsExternalLLM` false |
| No web search | `no-web` | web search (`WebSearch`) | `AllowsWebSearch` false |
| No network export | `no-network-export` | network, file-export and process destinations (`NetworkExport`) | `AllowsNetworkExports` false |

- Data carries a **label set** of zero or more labels. The empty set, written `none`, means no restriction.
- Labels have no order. There are no clearances, compartments or caveats. Nothing removes a label from data or from
  a subject, so there is no downgrade.
- **Fail closed.** Unlabelled data, an unknown label name and text that does not parse MUST be treated as
  `local-only`. *Enforced by:* PR 1.
- **Text form.** Labels are sorted ordinally and joined by commas, as in `no-external-model,no-network-export`. The
  empty set is `none`. Parsing accepts exactly the canonical forms, so text and sets correspond one to one; anything
  else fails closed to `local-only`. JSON and type converters write and read only the canonical text and refuse
  `null`, as SPEC-007's converter does. *Enforced by:* PR 1.

### 3.2 Destinations

The classification in §2's table is unchanged. Destinations no longer carry a label.

### 3.3 The rule

A send from a subject whose read set is R, to a destination of class D, is decided in this order:

1. If D is `Host`, the send is allowed.
2. If R contains `local-only`, it is refused with `LocalOnlyData`.
3. If D is `Unknown`, it is refused with `UnknownDestination`.
4. If R contains the label that forbids D, it is refused with `LabelForbidsDestination`, which names the label. The
   label is `no-external-model` for `ExternalModel`, `no-web` for `WebSearch` and `no-network-export` for
   `NetworkExport`.
5. Otherwise it is allowed.

A decision that faults is refused with `NoDecision`, as today. In report mode the same decision is recorded and
nothing is refused, as today. *Enforced by:* PR 1, with a decision table covering every class against every subset
of the four labels (5 × 16 rows).

### 3.4 Subjects carry a read set

- An `EgressSubject` frame holds a **read set**, in place of a high-water mark. The read set is the union of the
  label sets observed in the frame.
- Every frame rule from SPEC-007 PR 4.4 and 4.5 stands, with "join" read as "union". That covers monotone nesting,
  `Observe`, read scopes, detachment and the `using` obligations.
- `Observe` is atomic, so no concurrent observation is lost. The property that
  `HighWaterMarkTests.ConcurrentObservations_AreNeverLost` pins carries over.
- A read scope is pinned at `local-only` until it reports. When it is disposed, it observes the union of its reports
  if the read completed, and `local-only` otherwise.
- With no live frame the read set is `local-only`, with the basis `no-subject`.
- `SelfExtendRunnerAdapter` enters at `local-only`, where today it enters at `SystemHigh`. Agent-backed targets
  (SPEC-007's D16) observe `local-only`.

### 3.5 Bridges from today's vocabularies

- A data-sensitivity level maps to the labels its flags imply:

  | Level | Labels |
  |---|---|
  | `Public`, `Internal` | `none` |
  | `Confidential` | `no-external-model,no-network-export` |
  | `Secret` | `no-external-model,no-network-export,no-web` |
  | `TopSecret` | `local-only,no-external-model,no-network-export,no-web` |

- A null value or a value above 4 maps to `local-only`. A value below 0 maps to `none`; the owner's 2026-10-04
  decision for data (SPEC-007, PR 2 row) stands.
- A custom `IDataSensitivityLevel` maps to `local-only`, as RAG maps it to `SystemHigh` today (SPEC-007's D15).
  Mapping custom levels by their flags is §11 Q4.
- A trust tier maps by its level, and an unknown tier maps to `local-only`.
- `RAGTool` reports the union of its hits' label sets. With no hits it reports `none`, where today it reports
  `Public`.
- The caller side (`TrustTierOrder.CallerLabel`, `DataSensitivityLabelBridge.TryToClearance`) goes away with
  clearances.

### 3.6 Profiles and modes

These are unchanged, and SPEC-007's decisions-log rows Q1, Q5 and 4.6 stand:
- `air-gapped` and `secure-workstation` enforce. Every other profile reports.
- An unknown profile fails closed to enforce.
- `ASHLAR_EGRESS_MODE` and `AshlarHostingOptions.EgressMode` can only raise the mode.
- `report` is a break-glass on `SecureWorkstation`, and `AirGapped` ignores it.
- The report-only exceptions stay as listed in SPEC-007.

### 3.7 Refusals

- **`EgressDecision`.** It replaces `Current`, `DestinationLabel` and `Access` with `ReadSet`, `Reason` and
  `ForbiddenBy`. It keeps every other member, including `Refused`, `Refuses`, `Ref` and `ThrowIfRefused()`.
- **`EgressRefusalReason`** replaces `AccessDenialReason` for egress. Its values are `None`, `LocalOnlyData`,
  `LabelForbidsDestination`, `UnknownDestination` and `NoDecision`, and they are pinned and only ever appended.
  `Reason == None` holds exactly when the send is allowed, and a default-initialised decision is a refusal; the
  property that `ReferenceMonitorDecisionTests.DefaultAccessDecision_FailsClosed` pins carries over.
- **What each party sees.** The refused subject still gets only the reason category, site, family, destination class
  and a random reference (SPEC-007 Q7). Remote parties still get fixed text, and the CLI still exits 77.
- **The `Ashlar-Egress` event source.** Its field names and order are a public contract (`EgressEventSource.cs:14`).
  The default, open as §11 Q2:
  - keep every field
  - `current` carries the read set's canonical text, and `destinationLabel` carries the empty string
  - append `forbiddenBy`, and bump each event's `Version`

### 3.8 What goes

- **Experimental types (`ASHLAREXP002`):** `SecurityLevel`, `SecurityLabel`, `SecurityLabelJsonConverter`,
  `AccessDenialReason`, `AccessDecision`, `ReferenceMonitor`, `HighWaterMark` and `SubjectClearance`.
- **Substrate members:**
  - `AshlarHostingOptions.Clearance`
  - `SkillComposition.Decide`
  - `DataSensitivityLabelBridge.TryToClearance` and `TrustTierOrder.CallerLabel`
  - `AshlarPolicy.Clearances`, `PolicyClearances`, `ExtensionPackage.HighestLevel` and
    `ExtensionPackaging.Pack(…, highestLevel)`, each after one release of deprecation (§5)
- **Plans:** the read-up check, composition by clearance, trusted subjects and downgrade receipts (SPEC-007 PR 5 and
  PR 6).

New types stay in `Ashlar.Abstractions.Security` or `Ashlar.Abstractions.Security.Egress`, because
`ExperimentalSecurityApiConventionTests` allows exactly those two namespaces. Suggested names:
- `PrivacyLabel`
- `PrivacyLabelSet`, with its JSON converter
- `PrivacyRule`
- `ReadSet`
- `EgressRefusalReason`

## 4. Why the switch changes no production decision

- **Production behaviour is the same.** Today every production decision is made at `SystemHigh`, so under enforcement
  only `Host` passes. Under this spec every production decision is made at `local-only`, so only `Host` passes.
- **Exactly one cell differs.** Across every pair of a level and a destination class, the new rule agrees with
  today's, with one exception. Data with no restriction (today `Public`) sent to an `Unknown` destination is allowed
  today and is refused under this spec.
  - The reason: `Public` and `Internal` both map to `none`, and today `Internal` data is refused at `Unknown`. One of
    the two cells had to change, and refusing is the fail-closed choice.
  - It is a tightening, and no production path reaches it.
- **PR 3a MUST show this** with a characterization test. The test runs both rules over every pair and names the one
  cell that differs. *Enforced by:* PR 3a.

## 5. Compatibility

- **Experimental API.** `ASHLAREXP002` symbols may change or be removed in any release
  ([`docs/SdkCompatibilityPolicy.md`](../SdkCompatibilityPolicy.md)).
  - Their lines stay in `src/Ashlar.Abstractions/PublicAPI.Unshipped.txt`, and the PublicAPI analyzers keep that
    file honest.
  - The PR that removes types lowers the floors of `ExperimentalSecurityApiConventionTests` (types, lines and
    exported types) in a visible diff.
  - It also removes `ASHLAREXP002` opt-ins that projects no longer need, which `DiagnosticSuppressionConventionTests`
    checks.
- **Substrate members** change in a minor release, recorded under `### Breaking`.
- **The `clearances` block in `ashlar.policy.yaml`.** The policy deserializer has no `IgnoreUnmatchedProperties`
  (`PolicyLoader.cs:68-70`), so deleting the property would reject every policy that still has the block.
  - For one release the property stays and is ignored: its values are no longer validated, and loading logs a
    Warning that names the file.
  - The release after rejects it (§11 Q3).
- **`.ashpkg` `HighestLevel`.** No shipped code writes it. It stays for one release, ignored by every decision, so
  existing seals still verify. Then it is removed.
- **Logs and event-source values** change from level names to label text (§3.7). The CHANGELOG says so.
- **Version:** the next minor release, 0.5.0 (§11 Q5).

## 6. Out of scope

Each of these is an owner decision first.

- Per-label approved destinations, such as an in-house model endpoint approved for `no-external-model` data.
- Data categories such as `personal` or `credentials`, mapped to labels by policy.
- Renaming the legacy data-sensitivity tier names (`Secret`, `TopSecret`) in `DataTaxonomy.json`, vector stores and
  agent config, with the old names still accepted on read.
- The legacy RAG read filter (`RagSensitivity`), which still compares tier ranks: replacing it with lateral access
  rules about who may see which records.
- Removing the deprecated `clearances` and `HighestLevel` fields (§5).

## 7. PRs, in order (each small, each merged before the next starts)

| PR | What | Behaviour |
|---|---|---|
| 0 | Records: this spec, the decisions-log row, `docs/DocsIndex.md` and a dated note at the head of SPEC-007 | none |
| 1 | The label set and the rule, in `Ashlar.Abstractions` (§3.1, §3.3, §3.7's reason values, `ReadSet`'s atomic union). Nothing calls it yet. | none |
| 2 | The bridges (§3.5), as new members beside the old ones. Nothing calls them yet. | none |
| 3a | Frames, read scopes, reporters and the guard carry read sets next to marks. The guard computes both rules but still decides on today's. The characterization test (§4) runs on both. | none |
| 3b | The guard decides on the read set. Marks leave the frames. Event-source fields change (§3.7). | the one cell in §4 |
| 4 | Retire the classification surface (§3.8). Deprecate and ignore `clearances` and `HighestLevel` (§5). | none |
| 5 | Docs (§9), and the SPEC-007 header marked superseded in part | none |

**Files PR 3a and 3b touch** (from the 2026-10-27 inventory):
- `EgressGuard.cs`, `EgressDecision.cs`, `EgressSubject.cs`, the read scope and reporter types, `IEgressLabelledTool`,
  `EgressDestinations.cs` (`LabelOf` goes) and `EgressEventSource.cs`
- `RAGTool.cs` (`MapHitLabel`), `EgressGuardChatClient.cs:149`, `SelfExtendRunnerAdapter.cs:251`, the read scopes in
  `ToolCallingAgent.cs`, and `ChatInvocationAuditRecord`
- their tests (§8)

**PR-body tokens.** A PR that changes non-test code under `application/` carries `[coordinated-integration]`. A PR
that changes `src/Ashlar.Hosting/` without ProdStyle tests carries `[skip-prod-style]`, or adds the tests.

## 8. Tests and mutation checks

- **Every MUST names a passing test** (SPEC-007 §7). The PR that adds the test fills in the *Enforced by* line here.
- **New tests go where cert-gate runs them:** `Ashlar.Tests.Infrastructure.Tests.Certification`. The existing classes
  are rewritten, not left behind:
  - `SecurityLabelLatticeLawTests`, `SecurityLabelTextFormTests`, `SecurityLabelLevelParityTests`,
    `ReferenceMonitorDecisionTests` and `HighWaterMarkTests` become tests of the label set, the rule and the read set
    (PR 1, then their removal in PR 4).
  - `DataSensitivityLabelBridgeTests` and `TrustTierLabelBridgeTests` cover §3.5 (PR 2).
  - `SubjectClearanceCompositionTests` goes in PR 4.
  - `EgressGuardDecisionTests`, the `EgressSubject*` classes, `EgressEnforcementLeakTests`, the producer tests and the
    route twins move to read sets in PR 3a and 3b. The leak scenarios A, B, C and C0–C12 keep their meaning.
- **Mutation-check every behavioural change,** following `CLAUDE.md`:
  1. Commit.
  2. Apply the mutation and show the named assertion red, with counts.
  3. Restore, and show `git status --porcelain` is clean.
  4. Run green.

  PR 1's minimum set of mutations:
  - drop step 3 (`Unknown`)
  - let `local-only` through to `ExternalModel`
  - lose a label in the union
  - make a failed parse return `none`
- **A new manifest, `scripts/mutations/spec010-privacy-labels.json`,** covers the set, the rule, the parse and the
  bridges. SPEC-007 had no manifest for its label code, and this closes that gap.
  - Every PR that changes code a `spec007-*.json` mutation targets updates that manifest in the same PR, because
    `scripts/ci/verify-mutation-manifests-current.py` runs in `shell-lint`.
  - The floors of at least 7 manifests and 285 cases hold.
- **Build and test only through `scripts/test-in-container.sh`,** from a clone rather than a worktree, on `net8.0` and
  `net10.0`.

## 9. Records each PR updates

- `CHANGELOG.md` under `[Unreleased]`, with `### Breaking` for PR 3b and PR 4.
- `ci/cert-gate-assertions.md`, for any new blocking convention.
- `docs/knowledge-graph.json` and `.md`, regenerated after `git add`.
- `docs/EgressInventory.md` (including its "Label at the site" column) and `ci/egress-inventory.tsv`, when egress
  changes.
- The mutation manifests (§8).
- `docs/Configuration.md`, where privacy labels replace "Agent clearances" (PR 4 and 5).
- `docs/TrustAndInformationArchitecture.md`, `docs/SdkCompatibilityPolicy.md` (the `ASHLAREXP002` row),
  `docs/DocsIndex.md`, `docs/sdk.md`, `docs/governed-pipeline.md` and SPEC-008's pointer to SPEC-007 (PR 5).
- The agent-bus `claim`, `handoff` and `done` posts, and the status table at the head of this spec.

## 10. What we may claim

Allowed once PR 3b merges:

- "On the `AirGapped` and `SecureWorkstation` profiles, Ashlar refuses an outbound call when the agent has read data
  whose privacy labels forbid that destination, on every outbound path in the egress inventory, and records the
  refusal."
- "Data without a label is treated as local-only."
- "The evidence is the leak tests and the inventory convention test, run in CI on every change."

Never say these, in code, docs or marketing (they are also in `docs/marketing/claims-cheat-sheet.md`):

- classification levels, clearances, compartments, caveats, Bell–LaPadula, "multilevel security", "reference
  monitor", cross-domain, or safe for classified or compartmented work
- "can't leak", "prevents data leaks", "prompt-injection proof", or any guarantee about what an agent's text output
  contains
- "proven" or "proof" for a test result

## 11. Open questions for the owner (ask; don't guess)

1. **Label names.** Are `local-only`, `no-external-model`, `no-web` and `no-network-export` right?
   `no-external-model` is deliberately broader than "cloud": it covers any model that is not on this machine,
   in-house servers included.
2. **The event source.** Should the payload keep its field names and order, write `destinationLabel` as the empty
   string and append `forbiddenBy` (the default in §3.7)? Or should it publish a new event id instead?
3. **The `clearances` block.** Should it be ignored with a Warning for one release and rejected after (the default in
   §5)? Or rejected at once?
4. **Custom `IDataSensitivityLevel` values.** Should they keep mapping to `local-only` in v1 (the default)? Or map by
   their flags, which is a behaviour change?
5. **Release.** Does this ship in 0.5.0?
6. **What comes next from §6?** The recommendation is renaming the legacy tier names, since `Secret` and `TopSecret`
   still appear in `DataTaxonomy.json` and the docs.

## Decisions log

| Date | Applies to | Decision |
|---|---|---|
| 2026-10-27 | SPEC-007's data model | **Replace classification levels with privacy labels, and keep the egress guard.** The owner relayed a security reviewer's advice: drop classification levels, frame data controls in privacy terms, and don't make security claims that need proof. The owner then answered the integrator's question in chat with "Yeah replace the classification labels". |

## Starting prompt for Claude Code

> You're working in IanFrelinger/Ashlar. Read `CLAUDE.md`, then this spec, then SPEC-007's decisions log. Implement
> **PR 1 (§7)** exactly. That means:
> - the privacy label set, the rule and the read set in `Ashlar.Abstractions` (§3.1, §3.3, §3.4's atomic union and
>   §3.7's reason values)
> - the cert-gate tests, the four mutation checks in §8 and the new `spec010-privacy-labels.json` manifest
> - the CHANGELOG entry and the regenerated knowledge graph
>
> Build and test only through `scripts/test-in-container.sh`, from a clone. Open the PR with a Testing section that
> reports each mutation with its counts, and post the agent-bus `handoff`. When all five required checks are green,
> ask the owner to merge, then post `done` and stop before PR 2. If anything in §11 blocks you, ask the owner
> instead of choosing.
