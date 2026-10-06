# Phase B integration notes (carry into each PR's integration and the phase C handoff)

## Owner decisions this phase
- 2026-10-06, 4.2 / D31: **exception only**. No NoDecision record for a synchronous Send refused on the
  netstandard2.0 asset; the NotSupportedException reaches the caller. Record in SPEC-007's decisions log
  (dated 2026-10-06) as an amendment of D31, and reword SPEC-007's "the only place one could be" to
  "the only place Ashlar code runs, short of a process-wide first-chance-exception hook". Cost to state: under
  AG/SW enforcement such a refusal never appears in Ashlar's egress log.

## Integrator decisions this phase (record in PR bodies)
- 4.1: a URL-shaped `file:` name is recorded as written (design default: never URL-parsed), userinfo included;
  state the full exception in the EgressDecision docs (class remarks + Destination) at 4.1 integration.
- 4.4: unwind past a disposed frame only when its _previous is a subject frame; never past an outermost frame
  or a detachment (fail closed, decision 2). Cost: one flow that disposes its outermost frame out of order
  stays in that frame; label sticks.
- 4.2: the hop stays unconditional under NETSTANDARD2_0 (not gated on RuntimeHasSynchronousSend), so the
  refusal does not depend on the reflection probe.

## Queued for later phases
- 4.7: the startup line when a host's own IEgressGuard is the container's guard (AddAshlar keeps it, but the
  activator logs the composed guard's line). Suppress, name the host guard, or keep as the process mode?
  Depends on 4.7's route rule. (4.6 repair report.)
- 4.11: 4.6 F6; composed guards keep their composition-time profile and override; a later AddAshlar still
  selects its own module set (4.10 hygiene).
- 4.9: 17 explicit sites, including EG-MDL-07's cloud decision, which must refuse before SendAsync.
- 4.5: require `using` around every production Enter (frame disposed on the flow that entered it).
- Out of PR 4: OllamaProvider.BuildChatPayload extra `}` (task card queued, task_533c8f04).

## Progress log
- 2026-10-06 09:2xZ: #717 (4.1) merged as bbc5d71 (order changed: 4.1 before 4.4). done posted on #695.
- 4.4 final rule (integrator): no-skip for subject frames + callback-shaped RunDetached (D1 closed by construction).
  History: rounds 2-4 tried bounded unwinding (frame-keyed record, round-3 subject-prev rule, flow-local Skips);
  each had a verified write-down. #716 is draft until the final rule lands and re-checks clean.
- Master readiness verdict for bbc5d71: check after the push run completes.
- Master bbc5d71 readiness: verdict=verified lanes_ran=4 lanes_skipped=0 (check run 112210421021).
- #718 (4.6) opened, head d3ac8a49; handoff posted. Old no-skip workflow's leftover repair agent pushed 3 commits
  to the 4.4 branch (master merge + async-iterator records); new implementer told to ff.
- #718 (4.6) merged as 3196ba11; done posted.
- #719 (4.2) opened, head 11847416; handoff posted.
- #719 (4.2) merged as ad3d570b; done posted.
- Master 3196ba11 (4.6) readiness: verdict=verified lanes_ran=4 (check run 112252298505). ad3d570b (4.2) readiness run pending.
- Master ad3d570b (4.2) readiness: verdict=verified lanes_ran=4.
- 4.4 last round pushed 91cf8d74 (abort-window fix, created-vs-started records, IL-fact twin). Integrator: keep the 4.5 'create and start inside the using' sentence. Merge-gate check running.
- #716 (4.4) merged as de41a8ac; done posted. uat tier0 doctor-container-truthful runner docker hiccup, re-run green.
