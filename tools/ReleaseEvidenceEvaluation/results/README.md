# Evaluation replay artifacts

These are retained **offline evaluation outputs**, not fresh GitHub release evidence.
The pack's `collectedAt` records replay execution; the frozen public captures' actual
collection intervals are in `../public/manifest.json`. Synthetic cases describe invented
repositories/events. Neither their links nor their passing decisions establish real PR state.
Do not use these replay packs to gate an actual release. Each run manifest binds its exact
production candidate, frozen inputs, pre-output reviewed labels and output hashes.

`baseline-47a7c8506/` retains the original failure results and reviewed score. Repaired
candidates, when present, get separate directories; the baseline is never overwritten.
