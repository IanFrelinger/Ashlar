# Ashlar Trust Loop — Extension Spec: Autonomous Self-Extension

Specification v1.0 — companion to `ashlar-trust-loop-spec.md` v1.0 and
the Analyzer Gate & Container Isolation extension spec.
Status: Draft for integration
Scope: The closed loop in which Ashlar detects a need, proposes an
extension to itself, certifies it, admits it, hot-swaps it into the
running system, observes it, and rolls it back — with human authority
positioned exactly where it is load-bearing and nowhere else.
Builds on master as of `423bcfbc`: Invariants A–D
(`SelfProducedBrickCertificationPolicy`,
`AgentPolicyNarrowingValidator`, human admission, recursion ceiling),
`CertifiedBrickHotSwapHost`, `ProposalContextAssembler`,
`BrickConstraintManifest`, session sandboxes, analyzer gate.

Keywords MUST / MUST NOT / SHOULD / MAY per RFC 2119.

---

## 0. Core Invariants

> **I-1. The loop may extend the system; it may never extend its own
> authority.** Gates, policies, admission tiers, the swap host, the
> certifier, budgets, and this spec's enforcement code are the
> *trust kernel*. No artifact produced by the loop may modify the
> trust kernel without human admission — under any tier, at any
> depth, under any objective.

> **I-2. Autonomy is bounded by blast radius, not by confidence.**
> What runs unattended is determined by a static classification of
> what the artifact can touch — never by gate scores, proposer
> self-assessment, or accumulated success history.

> **I-3. Every autonomous action is reversible or it does not run.**
> An extension without a proven rollback path MUST NOT be admitted
> at any autonomous tier.

---

## 1. Objective Intake: Needs Are Untrusted Input

The loop begins when the system decides something should exist. That
decision channel is an attack surface and MUST be treated as one.

- R1.1 Permitted objective sources, in trust order: (a) explicit human
  objective; (b) diagnosed failure class from CI/ledger triage;
  (c) observed workflow gap from watch/adapt telemetry. The source
  MUST be recorded on the objective and flows into the certificate's
  `inputs`.
- R1.2 Objectives derived from observed data (source c) are
  *untrusted*: content from watched workflows MUST NOT be
  interpolated into proposer instructions verbatim. It MUST pass
  through deterministic extraction (schema-constrained fields only)
  so that instruction-shaped text in watched data cannot become
  instruction (prompt-injection fence at the objective layer).
- R1.3 Every objective MUST be tier-classified (§3) *before* any
  proposal session starts, from its declared touch-set — the files,
  namespaces, and capabilities the extension is permitted to affect.
  The touch-set becomes the session's constraint manifest and its
  sandbox mount table (single declaration, three enforcement
  surfaces).
- R1.4 Objectives whose touch-set cannot be statically determined
  MUST be classified at the most restrictive applicable tier.

## 2. The Loop (normative shape)

```
observe → objective (tiered) → propose (session sandbox, cluster)
       → fences → analyzer gate → mutation gate → certify
       → admit (tier gate) → swap (generation) → watch
       → [regress? → rollback + quarantine + fence candidate]
       → ledger + provenance (always, every arc)
```

- R2.1 Every arc MUST emit provenance events; there is no unlogged
  path from objective to runtime.
- R2.2 The propose→certify segment is the parent spec unchanged. This
  spec adds no new acceptance authority: certification remains
  certificate-iff-gate-pass, minted only by the gate runner.
- R2.3 A single loop iteration MUST terminate in one of exactly:
  admitted-and-swapped, certified-but-held (awaiting human),
  explained-failure-with-artifacts, or budget-exhaustion-with-
  artifacts. (Parent R4.7 extended to the full loop.)

## 3. Admission Tiers

- R3.1 Tier classification is a function of touch-set only:
  - **Tier 0 — autonomous.** Leaf bricks: constraint manifest
    present, no reference into the trust kernel, no policy/security/
    tool-whitelist surface, no modification of another brick's
    contract. Certified Tier 0 artifacts MAY hot-swap without human
    approval.
  - **Tier 1 — human admission.** Everything touching the trust
    kernel (I-1's enumeration), agent capability surfaces, network
    allowlists, or any cross-brick contract change. Certification
    MAY proceed autonomously; *admission* MUST wait for the
    Invariant C human gate. Held artifacts persist with full
    evidence for review.
  - **Tier 2 — human objective.** Changes to tier classification
    rules themselves, budgets, or this spec's enforcement: the
    *objective* MUST originate from a human (R1.1 source a); machine
    sources MUST NOT even open a proposal session.
- R3.2 Tier enforcement MUST be structural, not conventional:
  the classifier operates on the touch-set manifest; the analyzer
  gate enforces the declared touch-set against the actual reference
  graph (undeclared reference into the kernel = gate FAIL); the
  swap host independently refuses Tier-mismatched admissions
  (defense in depth — three checks, one declaration).
- R3.3 Tier 0 autonomy MUST NOT be earnable. No success streak,
  metric, or human toggle may promote a kernel-touching artifact
  class to Tier 0 (I-2). Widening Tier 0's definition is itself a
  Tier 2 change.

## 4. Recursion Discipline

Extensions can produce extensions. Depth is where autonomy quietly
becomes autonomy².

- R4.1 Every artifact MUST carry a `generation_depth`: human-authored
  code is depth 0; an artifact proposed by a loop whose proposer/
  tooling includes depth-n artifacts is depth n+1.
- R4.2 A hard depth ceiling MUST be enforced (default 2) by the
  certifier and independently by the swap host. Raising the ceiling
  is Tier 2.
- R4.3 Self-produced artifacts MUST NOT serve as gates, probes,
  fences, or analyzers for other candidates until a human admits
  them into the verification role (Tier 1 regardless of touch-set) —
  the loop may not grow its own judges autonomously.
- R4.4 `AgentPolicyNarrowingValidator` semantics extend transitively:
  a depth-n+1 session's capabilities MUST be a subset of the
  depth-n session that produced its proposer context. Capability
  monotonically narrows with depth; it never widens.

## 5. Post-Swap Observation and Rollback

Certification proves the artifact passed its gates; it does not prove
the gates were sufficient. The runtime is the last gate.

- R5.1 The swap host MUST retain generation N (bytes + certificate)
  when activating N+1, for a configured retention window. Rollback =
  reactivate N; it MUST require no build, no network, and no model.
- R5.2 Every Tier 0 swap MUST enter a watch window: declared-contract
  conformance (bag reads/writes vs. interface), error-rate and
  latency deltas vs. the generation baseline, and resource ceilings.
  Threshold breach MUST trigger automatic rollback.
  A rollback is not an absorption. The pacing and authority controls
  — lineage demotion (R5.5), the cadence floor and in-flight watch
  window (R6.1), the global pause (R6.2) and the recursion ceiling
  (R4.2) — bound how the runtime takes on CHANGE and MUST NOT gate a
  containment rollback; revocation (R5.3) and verify-at-load do. The
  restored generation keeps the pre-breach baseline as its comparand,
  and neither refreshes the cadence clock nor is itself retained.
  When no earlier retained generation with unrevoked certificate
  hashes remains, or the restore does not land, the breach MUST
  terminate in a paused loop with `rollback-exhausted` provenance and
  the breaching generation still serving — loudly, never in a silent
  retry. The real time-to-rollback bound is the retiring generation's
  drain timeout, not zero.
- R5.3 A rolled-back artifact is *quarantined*: its certificate is
  marked revoked in the record store, the swap host MUST refuse the
  hash permanently, and the failure MUST be triaged into a probe,
  fence, or analyzer rule (parent R3.3) before any successor
  objective for the same gap is opened — the loop learns before it
  retries.
- R5.4 Revocation MUST propagate through certificate chains: any
  certificate whose `inputs` include a revoked artifact becomes
  suspect and MUST be flagged for re-verification.
- R5.5 Repeated rollback on one objective lineage (default: 2) MUST
  demote the lineage to Tier 1 — autonomy is lost on evidence, even
  though it is never gained on it (I-2 is asymmetric by design).
  One rollback is one piece of evidence per lineage, recorded after
  the rollback lands; the demotion bounds what the lineage may absorb
  next and never gates the rollback whose evidence produced it (R5.2).

## 6. Cadence, Budgets, and the Pause

- R6.1 Cluster budgets (parent R4.6, extension B11.2) apply per loop
  iteration; additionally the loop MUST enforce a generation cadence
  floor (minimum interval between autonomous swaps) so the runtime
  is never absorbing changes faster than the watch window can
  clear them. In-flight watch windows MUST block the next autonomous
  swap of the same lineage. Neither the floor nor an in-flight window
  gates a containment rollback, and a rollback does not restart the
  floor (R5.2).
- R6.2 A global pause MUST exist, honoring the platform's pause
  semantics: pause halts objective intake and swaps immediately;
  in-flight proposal sessions run to a clean terminal state and
  hold. Pause MUST NOT corrupt state or lose artifacts. Resume
  requires no reconstruction. Pause halts absorption, not
  containment: a breach rollback lands while paused and its
  provenance says so (R5.2).
- R6.3 Pause, rollback, quarantine, and tier demotion MUST function
  fully offline (air-gap invariant: no autonomy control depends on
  egress).

## 7. Human Visibility Without Human Bottleneck

- R7.1 Tier 0 autonomy MUST NOT mean silence: every autonomous swap,
  rollback, and quarantine MUST appear in a human-reviewable digest
  (per-session and periodic), with one-hop links into the provenance
  graph.
- R7.2 A human MUST be able to, at any time: demote any lineage to
  Tier 1, revoke any certificate, roll back any generation, and
  pause the loop — each as a single operation not requiring a
  proposal session.
- R7.3 The digest MUST distinguish "the loop did X" from "the loop
  proposes X and is holding" — held Tier 1 artifacts are presented
  with evidence, never auto-expired into abandonment without a
  recorded human-visible disposition.

## 8. Adversarial Validation (acceptance for autonomy)

Production-readiness for unattended Tier 0 operation additionally
requires, with zero false certificates and zero unauthorized swaps:

- objective injection: instruction-shaped content planted in watched
  workflow data → never reaches proposer instructions as instruction
  (R1.2 fence holds)
- kernel-touch smuggling: Tier 0 objective whose candidate references
  the trust kernel indirectly (transitive dependency, reflection,
  generated code) → analyzer gate FAIL and swap-host refusal both
  fire (R3.2 triple-check proven independently, each with the others
  disabled in harness)
- judge-growth attempt: self-produced artifact registered as a gate/
  probe/analyzer without human admission → refused (R4.3)
- depth laundering: depth-2 artifact proposing via a fresh session to
  reset `generation_depth` → detected via input-chain hashes,
  refused (R4.1/R4.2)
- capability re-widening at depth: depth-n+1 session requesting a
  tool absent from its parent's whitelist → refused (R4.4)
- regression theater: candidate healthy in gates, misbehaving only
  post-swap → watch window catches, auto-rollback within threshold,
  quarantine + revocation propagate (R5.2–R5.4)
- revoked-hash resubmission (bit-identical and near-variant) →
  refused permanently / re-gated respectively
- pause under load: pause issued mid-cluster with swaps queued →
  no swap after pause acknowledgment, all sessions reach clean
  terminal states, resume is lossless (R6.2)
- one full "ends good" arc: machine-sourced objective (R1.1b) →
  Tier 0 classification → cluster proposal → certification →
  autonomous swap → clean watch window → digest entry with complete
  provenance chain, end to end with no human touch — and its mirror
  "ends bad" arc terminating in quarantine with the fence catalog
  grown by one.

## 9. Design Rationale (non-normative)

- The tier boundary is placed on *authority*, not on *difficulty*.
  Models will get better at hard changes; they must never get closer
  to the kernel by being better. I-2's asymmetry (autonomy lost on
  evidence, never gained on it) is deliberate: it makes the safe
  direction the cheap direction permanently.
- Rollback-before-admission (I-3) converts autonomy from a courage
  question into an engineering question. The system is allowed to be
  wrong at Tier 0 precisely because being wrong is bounded, observed,
  reversible, and — via quarantine-to-fence — educational.
- The recursion rules encode the SX-HARDEN lesson as arithmetic:
  depth increments, capability narrows, judges require humans. A
  loop under these constraints can compound capability indefinitely
  while its authority stays exactly where it started — which is the
  only version of "self-improving system" an auditor, a customer, or
  its author should accept.
