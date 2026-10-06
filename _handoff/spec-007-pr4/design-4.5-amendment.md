# DESIGN-4-final.md §5: amendments from PR 4.5 (for the storage branch `claude/spec-007-pr4-workspace`)

Three replacements in `_handoff/spec-007-pr4/DESIGN-4-final.md`, §5 "The seeded leak test". Cause: the owner's decision of 2026-10-06 (while a read scope is open and unreported, every egress decided on the flows inside it is decided at SystemHigh, for every tool, RAGTool included), implemented in PR 4.5 as "until the scope is disposed".

## 1. Scenario B, the "Expect" list, last bullet

Replace:

> - the decision is `LevelTooLow`, WebSearch / Confidential, `ModeBasis == "profile:secure-workstation"`.

with:

> - the decision is **`SystemHighData`**, WebSearch / Confidential, `ModeBasis == "profile:secure-workstation"`: the search runs inside the `web_search` tool call, so inside the agent's open read scope, and a read that has not ended counts as SystemHigh (owner decision 2026-10-06; PR 4.5). B and C5 then share a reason and differ by site and family: B's refused record is the tool's (`EG-WEB-01`, family `web-search`, WebSearch / Confidential), C5's the model's (`EG-MDL-01`, family `model.meai`, ExternalModel / Internal). The verifier asserts the site and the family, not the reason alone.

## 2. Controls table, row C4

Replace:

> | C4 | a test tool that enters `Enter("inner", new HighWaterMark())` and, **inside that frame**, sends through a guarded stub | that send is refused, `Current = Secret`, `LevelTooLow` | monotone nesting. A frame set inside an async tool never flows back to the agent, so the egress must happen inside it. |

with:

> | C4 | a test tool that enters `Enter("inner", new HighWaterMark())` and, **inside that frame**, sends through a guarded stub | that send is refused, `Current = SystemHigh`, `SystemHighData`, basis `subject:inner` | the send is made inside the agent's open read scope, so it is decided at SystemHigh whatever the frames hold (PR 4.5); it no longer isolates monotone nesting, which PR 4.4's `EgressSubjectNestingTests` pin on their own (a `Public` frame inside a `Secret` one decides `Secret`). A frame set inside an async tool never flows back to the agent, so the egress must happen inside it. |

## 3. Controls table, row C5

Replace:

> | C5 | an unlabelled test tool instead of `rag_search` | call 2 refused, `SystemHighData` | the unreported-read rule |

with:

> | C5 | an unlabelled test tool instead of `rag_search` | call 2 refused, `SystemHighData`, at the model's site (`EG-MDL-01`, `model.meai`, ExternalModel / Internal) | the unreported-read rule. Same reason as Scenario B since PR 4.5; told apart by site and family. |

## 4. Mutations table (no text change, one note)

M6 ("drop the unreported-read rule → C5") and M14/M15 stand. Add after the table: "The open-read rule (PR 4.5) is mutation m01 of that PR: `BeginRead` not entering the read's frame makes a tool's own in-call egress decide at the pre-read mark; B then reads `LevelTooLow` again."
