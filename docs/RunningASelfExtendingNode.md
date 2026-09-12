# Running a self-extending node

An Ashlar node can extend itself — propose code against its own objectives, gate it, and apply it —
**unattended**. This page is the operator's guide to doing that *safely*: the dials, the safety net,
and the two commands that stop it.

The short version: a node ships **sealed** and changes nothing after deploy. You raise the dial
deliberately, one node at a time, and everything it admits passes the gates **you listed in
`gatesRequired`** — plus an auto-rollback after apply, which is not optional. A real build check is
available and is the one you most likely want in that list; it is not on unless you put it there. You
can see what it did and stop it at any time.

## Two dials, not one

A self-extending node has **two** independent switches. Both must be on for anything to happen:

| Dial | What it controls | Command |
|------|------------------|---------|
| **Aggressiveness mode** | whether the background extender runs at all (Passive = observe only) | `ashlar background-agent mode set --value active` |
| **Self-extend posture** | what the gate *does* with a proposal (sealed / propose-and-hold / auto-admit) | `ashlar policy set self_extend <mode>` |

An active extender with a `sealed` policy proposes nothing admissible; a `self-extending` policy with
a Passive extender never runs. Arming means turning on both, on purpose.

## The self-extend posture

Set and inspect it with the `policy` command (the only supported way to edit the policy after `init`
— it changes *only* the mode, preserves everything else, and refuses to leave the policy invalid):

```bash
ashlar policy show                                  # mode, budget, gates, mayAdd, trusted-signer count
ashlar policy set self_extend proposing             # arm: propose & hold for review
ashlar policy set self_extend self-extending        # arm: auto-admit within budget, canary-gated
ashlar policy set self_extend sealed                # disarm the posture
```

| Mode | Meaning |
|------|---------|
| `sealed` *(default)* | Nothing is admitted. A proposal is rejected with `mode is sealed`. This is what a fresh project ships with. |
| `proposing` | The cycle's work is **held** for a human to seat via `ashlar gates`. No auto-apply. The safe way to watch a node before trusting it to apply on its own. |
| `self-extending` | An admissible proposal is **applied automatically** within budget, behind the post-apply canary. |

`proposing` and `self-extending` require at least one entry in `gatesRequired` — the verb refuses to
arm a gate-less policy and writes nothing. Arming `self-extending` prints a loud confirmation, and
warns if the budget is `0` (armed but nothing will auto-admit).

## The gate every proposal faces

Whatever the posture, an admitted change has passed the admission gate on **executed evidence**, not
self-report. Two of the three below hold unconditionally; the first holds **only if you asked for
it**, and that distinction is the one to get right before arming a node.

- **build course — only if `gatesRequired` lists `build`.** The proposal's source is compiled
  in-process (Roslyn — no .NET SDK needed on the node) and the result is written to the proposal's
  signed, append-once record either way. But `AdmissionGate.Decide` walks
  `policy.selfExtend.gatesRequired` and consults nothing else, so a `build` course that FAILED — or
  one that errored in the verifier — does not block admission unless `build` is in that list. `ashlar
  init` writes `gatesRequired: []`, and the loader refuses to *arm* a policy with an empty list, so
  the choice is yours and you have to make it explicitly. **If you want "a change that does not
  compile is never admissible", put `build` in `gatesRequired`:**

  ```yaml
  selfExtend:
    gatesRequired: [sandbox, build]
  ```

  Check what you actually have with `ashlar policy show`, which prints the list.
- **the envelope.** Unconditional. Only a `brick` may be self-added (tools and capabilities *widen*
  the envelope and are never self-addable), and only paths outside the governance/build floor may be
  written.
- **budget & ceilings.** Unconditional. `selfExtend.budget` caps admissions per window (`0` disables
  auto-admit entirely); cross-cycle ceilings cap unattended cycles, cycles-per-hour, and lineage
  depth. These are the primary blast-radius controls — raise `budget.extensions` deliberately.

## The safety net (A4)

For a `self-extending` node, applying is transactional and reversible:

- Every target's prior bytes are **snapshotted before any write**, so a mid-batch failure rolls the
  whole batch back rather than leaving a half-written tree.
- After the writes land, a **post-apply canary** re-verifies the change as it sits on disk; if it
  fails (or the verifier errors — fail-closed), the writes are **rolled back** and the proposals
  rejected. A change that doesn't hold up never survives on the node.
- A rollback that can't fully restore is reported **loudly** (never a silent partial state).

Honest limit: the canary means "compiles as applied," not "the whole node still builds and every test
passes" — it's defence-in-depth and the seam a stronger check (a test course, a runtime probe) plugs
into. That's part of why the recommended path is **staged**: run one node in `proposing` and watch it
before you let any node apply on its own.

## Watch it, and stop it

Two front doors make an unattended node auditable and interruptible:

```bash
ashlar background-agent report                 # what ran (per agent), and what was held / admitted / rejected / reverted
ashlar background-agent disarm --reason "…"    # EMERGENCY STOP → Passive on the next cycle, no restart needed
```

`disarm` is the "stop it now" button; it forces the aggressiveness mode to Passive (hot-reloaded, so
no restart), and the mode is fail-closed (any read failure also reads as Passive). Re-arm with
`ashlar background-agent mode set --value active`.

## Recommended path

1. Deploy the node **sealed** (the default). Give it an identity: `ashlar keys init`.
2. Arm the extender to run but hold: `mode set --value active`, `policy set self_extend proposing`.
3. Give it objectives, let it run, and watch with `ashlar background-agent report` and `ashlar gates`.
   Seat proposals you like by hand.
4. Only once you trust its output on that machine, raise **one** node to `self-extending` with a small
   `budget`, and keep `report` / `disarm` within reach.

## Sharing what it grows

A node's admitted extensions can travel to peers you trust — see
[`Federation.md`](Federation.md). The same gate that governs local self-extension governs every
package a peer pulls: the network is transport, the seal is the trust.

## See also

- [`Federation.md`](Federation.md) — peer-to-peer sharing of what a node grows.
- [`SELF-EXTEND-AUDIT.md`](SELF-EXTEND-AUDIT.md) — the safety audit behind this path.
- [`SelfHostedAgentServer.md`](SelfHostedAgentServer.md) — the background-agent server and aggressiveness modes.
