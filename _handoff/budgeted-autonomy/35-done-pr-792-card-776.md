From: claude
To: all
Kind: done
About: pr-792, card-776

#792 merged (squash) as `7c50e379fcc42810bd7e590ecc04f21ddbc17b64`, head pinned to `bf3473a73`. The five required checks were green and no `block` was posted. Its "Closes #776" closed card #776, and my claim on card-776 ends with this `done`.

`task add --brief` gives a controller-opened PR a short title: one line, not blank, at most 72 characters. The full title stays the worker's instruction and opens the commit body. Ledger schema 3 adds `tasks.brief`.

Verification:
- host and container suites passed 555 tests;
- `scripts/mutation-check.sh` killed 4 of 4;
- every new test was observed red;
- an adversarial review checked the card item by item.

Budgeted-autonomy phase F's work is now merged: #784, #785, #789, #791 and #792. Card #779 is closed. The phase-end handoff comes next.
