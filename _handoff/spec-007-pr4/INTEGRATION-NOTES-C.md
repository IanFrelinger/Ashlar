# Phase C integration notes (carry into each PR's integration and the phase D handoff)

## Owner decisions this phase (all 2026-10-06; each gets a dated SPEC-007 decisions-log row in the PR that implements it)
- **4.5 read scopes (asked at phase start):** while a read scope is open and unreported, every egress decided on the
  flows inside it is decided at SystemHigh, for every tool, RAGTool included. Scenario B's expected reason becomes
  SystemHighData. (Recorded by 4.5.)
- **4.5 / Q8 clarification (critic O1):** D15 stands. A custom `IDataSensitivityLevel` read through RAG is SystemHigh
  in PR 4; Q8's C3 normalisation applies to the first producer that labels data from a custom level on purpose, not
  yet (PR 5 territory). (Recorded by 4.5, same row.)
- **4.3 redirects into Host (critic O2):** a hop from a non-Host authority to a Host authority (loopback, link-local,
  `*.localhost`) is never followed, for factory (P1) and `EgressHttp` (P2) clients alike; the 3xx is returned to the
  caller with the hop's decision recorded. Reason: the local-service CSRF path `MeshAutoPullService` already names.
  Cost: a host client behind a remote that legitimately redirects to a local service gets the 3xx. (Recorded by 4.3.)

## Integrator decisions this phase (record in PR bodies; the owner can object)
- **4.5 read frame is PINNED:** its mark is SystemHigh for its whole life (never lowers at the end of the read). Both
  pinned and "settled" pass the model's P1–P4 (`model-4.5/REPORT.md` §4); pinned keeps every mark monotone and the
  4.4 one-line invariant unchanged; the cost is availability only for a flow that could not leave a read frame
  (limit (a) extended to reads). Flips `EgressSubjectReadScopeTests.A_read_observes_into_every_live_frame_of_its_chain_from_whatever_flow_it_ends_on`
  (:179) by name to SystemHighData; `A_completed_read_with_no_report_observes_SystemHigh` (:50) flips for the owner's rule.
- **4.5 basis inside an open read scope:** the nearest live SUBJECT frame names the decision; the read frame
  contributes its mark only; with no subject frame the basis stays `no-subject`.
- **4.5 "open until disposed":** a Report (even with Complete) before Dispose does not lower an open scope. Stricter
  than the decision's wording ("open and unreported"), on purpose: the only reading that keeps D14 without immediate
  observation.
- **4.5 G5 (critic):** the labelled tool gets a report-only surface (or the agent owns completion), so a tool that
  calls `Complete()` and throws still observes SystemHigh (D14 against the tool itself). Changes the public marker's
  shape in `PublicAPI.Unshipped.txt`.
- **4.10 mesh serve on AG/SW:** the owner's recorded Q6 text literally — bind loopback (`ListenLocalhost`) and keep
  serving; not "refuse to serve, daemon stays up" (which would have needed a dated row). The API's Kestrel URLs keep
  the fail-boot rule.
- **4.3 Q-A:** 4.3 lands no enforcing route: the follower records `Refused = true` on a would-refuse hop and sends,
  as every route does until 4.7 (D21, D2, the 4.6 record "no route acts on the mode until 4.7"). The enforcement
  twins (stub and differential) are listed "for 4.7"; 4.11's Scenario C(ii) is the end state.
- **4.3 Q-B:** the 4.2 ns2.0 hop stays as is (the D31 amendment argues against a record from the hop); the chain
  walker steps through its internal `Inner`; Known limit `docs/EgressInventory.md:23` narrows.
- **4.3 Q-C:** Bedrock's SDK config gets `AllowAutoRedirect = false` (the SDK exposes it, per the lane); routing the
  SDK's HTTP through `EgressHttp` is a follow-up outside the 4.3 row.
- **4.3 item 13:** runtime parity clears `Authorization` only; custom credential headers (the consumer SDK's
  `X-Ashlar-Api-Key`) following an allowed cross-host hop is a Known limit; refused on AG/SW at 4.11 where the label forbids.
- **4.10 Bedrock composition-time refusal** inside `AddAshlar(AirGapped)` (stricter than `ValidateOnStart`, which
  cannot bind a pre-built `Options.Create` instance). Recorded by the lane. 4.11's C7 shape throws at composition.
- **4.10 G9/G12:** 4.3's IVT grant to `Ashlar.Infrastructure` turns "Infrastructure never reads `Effective` directly"
  into a convention; 4.10 adds the convention fact and its cert-gate row (or says the rule is a convention).

## Queued for later phases
- 4.7: the two 4.3 enforcement twins flip to a thrown refusal; the startup-line question (from phase B).
- 4.11: Scenario B's reason is SystemHighData (owner's 2026-10-06 rule); Scenario C(ii) end state; `AddAshlar(AirGapped)`
  with Bedrock on throws at composition (4.10); G15 (ProcessDefault re-reads the profile per decision — 4.6 finding 6).
- 4.9: EG-MDL-07 cloud decision refuses before SendAsync (phase B).
- DESIGN-4-final.md §5 amendment on the storage branch (Scenario B reason, C4, C5): the 4.5 lane drafts
  `ws/design-4.5-amendment.md`; the integrator publishes it with the phase C handoff.
- Follow-ups outside PR 4 (from the scouts): route Bedrock's SDK HTTP through `EgressHttp`; UDP discovery, the gRPC
  host and Fleet.Host inbound surfaces under Q6 (ask on the bus); EG-MDL-08 video left to the guard on AG.

## Environment facts learned this phase
- The container can restart mid-phase (it did at ~18:45Z): every lane pushes after every commit; the scratchpad
  survived, running processes did not.
- A workflow resumed with `resumeFromRunId` replays finished agents from cache and re-runs the lost ones.
- The three lanes plus the scouts shared 4 CPUs; one container run per lane at a time kept everything moving.

## Progress log
- 17:5xZ phase C started from the phase B handoff; owner answered the read-scope question (open scope = SystemHigh).
- 18:0xZ lanes 4.3/4.10/4.5 launched (one clone each); scouts workflow launched beside them.
- ~18:45Z container restart; lanes resumed on their clones; scouts resumed from cache.
- 19:1xZ all three lane branches pushed for the first time.
- 20:1xZ model-4.5/REPORT.md landed (41,755,599 sequences, B1–B4 caught in 2–5 ops, 1,900,357 reproduces 4.4).
- 20:3xZ critic landed; O1/O2 asked and answered; O3 resolved by the owner's Q6 text; pull-forward items sent to lanes.
