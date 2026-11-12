# SPEC-007 security mutation evidence

Pinned master source: [`792446d09b59f98b47e08279907cb3d4df314caa`](https://github.com/IanFrelinger/Ashlar/commit/792446d09b59f98b47e08279907cb3d4df314caa).
Campaign: [GitHub Actions run 43257772109](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109); controller commit [`4a2e3c3aa2decb07c5b79026685518d1628dd9e3`](https://github.com/IanFrelinger/Ashlar/commit/4a2e3c3aa2decb07c5b79026685518d1628dd9e3). The controller commit carries the isolated runner; all source mutations and restored green runs use the pinned source SHA above.

All **295/295 cases across 8 manifests** were KILLED and reconciled against the same source inventory. The case/framework distribution is 72 on net10.0, 223 on net8.0. Each result includes an actual failing red run, the applied diff, original and restored source hashes, a green restored run, test counters and clean-source attestations. Cached baseline references are retained explicitly.

All 295 cases also received actual-failure relevance review: **280 runtime-classified cases** and **15 convention-only cases**. The runtime category includes relevant assertion failures, diagnostic/configuration checks, and source-caused exceptions from executed test bodies. These classify cases, not every failed test result. A runtime case can also include convention failures, and its classification does not establish downstream assertions or network sends that were not observed. The full aggregate retains every canonical review and original analyst record with its explicit limits. The convention-only appendix covers the complete campaign, including cases outside the 65 below.

The 65 cases without manifest expected-failure declarations received individual semantic review against the retained actual red failures and applied mutations. Their complete reviews appear below. Convention tripwires establish that repository guardrails detect the prohibited source change; they do not by themselves demonstrate the corresponding changed runtime behavior. Runtime-classified cases show relevant detections in executed test bodies, within the observations and limits recorded for each case. Both evidence kinds are distinguished in retained reviews. A KILLED line must be read with its actual failed tests and evidence kind, rather than as an interchangeable runtime proof.

Reviewed evidence kinds among those 65 cases: runtime-assertion: 65.

Executed on 2026-11-12. All 21 campaign jobs succeeded. The 20 pinned execution intervals ran serially with zero overlaps; 1,084 actual red failure rows were retained. The subsequent nonpublishing clean-package trust rehearsal is recorded in its own section below. These results do not resolve the separate behavior limits in the [SPEC-007 disposition index](../../docs/specs/SPEC-007-disposition-index.md).

## Inventory and provenance

- Source inventory SHA-256: `e58877f52c21852748a89a6eea923f733484141a4af25dc3f4663954c3256aa2`
- Exact reconciliation JSON SHA-256: `944dca4de3c0d87df05fba36c76108c01fb037dbfd9febae0f2fa9ffd82210ce`
- Completed semantic review JSON SHA-256: `674f57db9bbd5175d15256a451707730fc5bd3064860c204bdf434193f53de2e`
- Whole-campaign actual-review JSON SHA-256: `0de42fca960d3612c33366df47b3799101f5cc3aa6dfe0c905ddee2216f51b36`
- [Full review aggregate](https://github.com/IanFrelinger/Ashlar/blob/c7c896c40c927551c41caa17486a6dae89573993/ci-package-rehearsal/actual-review-all295.json), [reconciliation](https://github.com/IanFrelinger/Ashlar/blob/c7c896c40c927551c41caa17486a6dae89573993/ci-package-rehearsal/reconciliation.json), and [65-case semantic review receipt](https://github.com/IanFrelinger/Ashlar/blob/c7c896c40c927551c41caa17486a6dae89573993/ci-package-rehearsal/semantic-reviews.json) are retained at the immutable controller commit. All original analyst-part files and digests were revalidated before this document was written.
- Serial timing receipt SHA-256: `148f360fafac608f798a3f81cead09a4a68dd92c0654e6c5855267bbf8c5b6f0`.
- Separate full-readiness baseline: [run 43123301578](https://github.com/IanFrelinger/Ashlar/actions/runs/43123301578) at the same source SHA emitted `verdict=verified lanes_ran=4 lanes_skipped=0`. A records-only PR may skip heavy readiness lanes; its green status is not a substitute for that baseline or this campaign.

| Manifest | Cases | Runtime cases | Convention-only cases | Frameworks |
|---|---:|---:|---:|---|
| [spec007-default-enforcement.json](https://github.com/IanFrelinger/Ashlar/blob/792446d09b59f98b47e08279907cb3d4df314caa/scripts/mutations/spec007-default-enforcement.json) | 28 | 26 | 2 | net8.0 |
| [spec007-enforcement-routes.json](https://github.com/IanFrelinger/Ashlar/blob/792446d09b59f98b47e08279907cb3d4df314caa/scripts/mutations/spec007-enforcement-routes.json) | 16 | 16 | 0 | net8.0 |
| [spec007-explicit-egress.json](https://github.com/IanFrelinger/Ashlar/blob/792446d09b59f98b47e08279907cb3d4df314caa/scripts/mutations/spec007-explicit-egress.json) | 85 | 78 | 7 | net10.0, net8.0 |
| [spec007-hygiene.json](https://github.com/IanFrelinger/Ashlar/blob/792446d09b59f98b47e08279907cb3d4df314caa/scripts/mutations/spec007-hygiene.json) | 21 | 19 | 2 | net10.0 |
| [spec007-inbound-hosts.json](https://github.com/IanFrelinger/Ashlar/blob/792446d09b59f98b47e08279907cb3d4df314caa/scripts/mutations/spec007-inbound-hosts.json) | 2 | 2 | 0 | net10.0 |
| [spec007-producers.json](https://github.com/IanFrelinger/Ashlar/blob/792446d09b59f98b47e08279907cb3d4df314caa/scripts/mutations/spec007-producers.json) | 29 | 25 | 4 | net8.0 |
| [spec007-redirects.json](https://github.com/IanFrelinger/Ashlar/blob/792446d09b59f98b47e08279907cb3d4df314caa/scripts/mutations/spec007-redirects.json) | 33 | 33 | 0 | net10.0, net8.0 |
| [spec007-refusal-surfaces.json](https://github.com/IanFrelinger/Ashlar/blob/792446d09b59f98b47e08279907cb3d4df314caa/scripts/mutations/spec007-refusal-surfaces.json) | 81 | 81 | 0 | net10.0, net8.0 |

## Retained campaign artifacts

The ZIP SHA-256 values below were checked against the retained artifact metadata and downloaded archives. Every extracted file, including the per-case TRX and original container log, was compared with its archive entry before this record was generated. Artifact retention can expire; retain the downloaded ZIPs and these digests.

| Serial batch | Artifact | Downloaded ZIP SHA-256 |
|---|---|---|
| batch_00 | [13963268165](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13963268165) | `e7324a02d345c84381949da3ec8499feb99d7fe50ad5670a94d3850d6c165c48` |
| batch_01 | [13963659101](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13963659101) | `031a0432de78ea7fa7111784cd3970c947b88ce056dbe3805cd4522b4d74e489` |
| batch_02 | [13964473522](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13964473522) | `875dbf5e3c741fb69442ed1586fe4dca93987ea2ef2e8c496ec8a0c0abf0405a` |
| batch_03 | [13966785995](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13966785995) | `cd516fa5eec31392218c2e95aea3a92c3cd3c6067b598e27d094782151cf6d1b` |
| batch_04 | [13966878061](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13966878061) | `3663502c882ff30559896ef6cb528c536fbb0175195b58c8b4c2d64b4ef51c94` |
| batch_05 | [13968397861](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13968397861) | `92861709291986ce6fef2f7e34ab6cb7cff7b2bd1712c8cbf9c52f9b064c1433` |
| batch_06 | [13970750391](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13970750391) | `37218b56d3b9cac6fc027c9168f4d132299d153b2e0f4e206ba51c0d075e5169` |
| batch_07 | [13971230808](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13971230808) | `433162dc1f571ee14590a621eed9cfae9af575a0a4fd764486efb52876fa7b14` |
| batch_08 | [13971633926](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13971633926) | `0e9508b319cfcbd6ee6f6e8514b9200be71a223c1214df3eb0edc815b653189c` |
| batch_09 | [13971309848](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13971309848) | `04fcd559f49545834eb3d122424f7ee7682f8b0acb5e678b8241a6f21726fdbf` |
| batch_10 | [13973170739](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13973170739) | `23199406b88df9dfea5bcabe8127c9ab82258f45b7198d182894d0f6ea4a5763` |
| batch_11 | [13974356770](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13974356770) | `5a80c393ab304329ba66485a67122568b026ce19d0edae89bbd30d44caebbc3c` |
| batch_12 | [13974354331](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13974354331) | `d966e04b672d4d86d46434761f51f4434db35b2b76a38cce1e7dca381746501e` |
| batch_13 | [13975413408](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13975413408) | `0ede23d5489f8bf5bfe314ab595488b93e822873531534d4261ee41124cfd1db` |
| batch_14 | [13977092041](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13977092041) | `9f1f08a35fb3750be6ad959597b272dc4468172728866bd712b945823243f9d8` |
| batch_15 | [13978376964](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13978376964) | `935a877fdc4afb4fc58cc521ef79bd84c5231e9f24fedc6944972779d90369de` |
| batch_16 | [13977674917](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13977674917) | `7698149a068b3168b69e74e719d666c3ad64f5a6b640dbe1100e5ed2212b83c8` |
| batch_17 | [13980331042](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13980331042) | `b42e33020d6d94250b5b12051f64eba243670c3131a747829130878b228ed727` |
| batch_18 | [13981222532](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13981222532) | `bee9ee4709166f5da8ed9e8a72976be5f682ce6e47f8d398bd0a55216a723d23` |
| batch_19 | [13982036134](https://github.com/IanFrelinger/Ashlar/actions/runs/43257772109/artifacts/13982036134) | `38e27d2764134b860f101561fa5915fdcbd17fd0a09232f7c541cea78461974e` |

## Verbatim mutation summary lines

These lines are copied from each artifact's `container.log`, in the exact planned manifest and case order. The original runner's `red=failed`, `green=passed`, source SHA and framework fields are preserved. No normalized replacement lines or preflight counts are substituted.

### batch_00 — spec007-default-enforcement.json

```text
mutation mode-sw-default: KILLED red=failed:7/107 green=passed:107/107 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation mode-ag-default: KILLED red=failed:12/107 green=passed:107/107 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation mode-ag-report-ignored: KILLED red=failed:2/107 green=passed:107/107 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation mode-sw-break-glass: KILLED red=failed:3/107 green=passed:107/107 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation mode-sw-break-glass-basis: KILLED red=failed:3/107 green=passed:107/107 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation route-owned-ag-floor: KILLED red=failed:4/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation route-host-ag-floor: KILLED red=failed:4/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation route-ag-profile: KILLED red=failed:12/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation opt-out-owned-mode: KILLED red=failed:7/30 green=passed:30/30 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation opt-out-protected-name: KILLED red=failed:10/30 green=passed:30/30 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m01-chat-discards: KILLED red=failed:7/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m02-rag-no-report: KILLED red=failed:3/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m03-sw-reports: KILLED red=failed:17/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m04-always-refuse: KILLED red=failed:12/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m07-stop-error: KILLED red=failed:6/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m08-message-detail: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m09-unknown-public: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m16-bing-discards: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m05-innermost-only: KILLED red=failed:3/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m06-unreported-public: KILLED red=failed:2/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_01 — spec007-default-enforcement.json

```text
mutation leak-m15-thrown-read-discarded: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m10-classification-fault-allows: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m11-composition-full: KILLED red=failed:3/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m12-factory-removes-handler: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m13-process-host: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation leak-m14-observe-reports-read: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation registry-new-client: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation registry-missing-client: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_02 — spec007-enforcement-routes.json

```text
mutation r1-refusal-helper-disabled: KILLED red=failed:26/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r2-http-decision-discarded: KILLED red=failed:14/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r3-chat-decision-discarded: KILLED red=failed:12/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r4-host-fault-replaced-with-ordinary-decision: KILLED red=failed:4/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r5-refusal-warning-hidden: KILLED red=failed:1/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r6-summary-not-scheduled: KILLED red=failed:1/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r7-event-two-hidden: KILLED red=failed:1/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r8-chat-refusal-audit-lost: KILLED red=failed:4/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r9-resolution-counter-lost: KILLED red=failed:2/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r10-http-fallback-warning-hidden: KILLED red=failed:1/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r11-chat-fallback-warning-hidden: KILLED red=failed:1/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r12-reentrant-refusal-counter-lost: KILLED red=failed:1/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r13-netstandard-enforcement-lost: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r14-startup-claims-records-only: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r15-opaque-redirect-warning-lost: KILLED red=failed:4/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation r16-warning-window-merges-reasons: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_03 — spec007-explicit-egress.json

```text
mutation action-dotnetrunner-1: KILLED red=failed:6/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-filebasedsharedadaptationstore-1: KILLED red=failed:1/57 green=passed:57/57 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-sneakernettransport-1: KILLED red=failed:1/57 green=passed:57/57 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-sneakernettransport-2: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-dotnetinstancespawner-1: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-validationserviceadapter-1: KILLED red=failed:1/57 green=passed:57/57 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-validationserviceadapter-2: KILLED red=failed:1/57 green=passed:57/57 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-dockerservice-1: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-dockerservice-2: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-dockerexecutionplatform-1: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-dockerexecutionplatform-2: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-postgresephemerallifecycle-1: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-ollamaephemerallifecycle-1: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-ollamaprovider-1: KILLED red=failed:1/57 green=passed:57/57 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-dotnetregressiontestrunner-1: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-ollamaproposalsource-1: KILLED red=failed:1/57 green=passed:57/57 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-bingwebsearchprovider-1: KILLED red=failed:1/57 green=passed:57/57 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation action-bootstrapruntime-1: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-dogfoodtestcommand-1: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-exportcommand-1: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
```

### batch_04 — spec007-explicit-egress.json

```text
mutation action-multiplatformtestcommand-1: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-nativebundle-1: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-nativebundle-2: KILLED red=failed:5/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-pkgcommand-1: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-pkgcommand-2: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-pkgcommand-3: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-testportablecommand-1: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-testportablecommand-2: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation action-workflowcommand-1: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation graceful-mesh-serve: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation graceful-discovery: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation graceful-otlp: KILLED red=failed:3/8 green=passed:8/8 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation graceful-admission: KILLED red=failed:1/18 green=passed:18/18 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation operator-requires-initiator: KILLED red=failed:4/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation operator-requires-file-family: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation operator-requires-named-site: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation operator-mode-fault-stays-enforcing: KILLED red=failed:4/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation operator-mode-basis-recorded: KILLED red=failed:4/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation timed-public-process-is-offhost: KILLED red=failed:2/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation timed-internal-rechecks-decision: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_05 — spec007-explicit-egress.json

```text
mutation timed-bounded-public-process-is-offhost: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation timed-bounded-internal-rechecks-decision: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation process-funnel-records-once: KILLED red=failed:1/57 green=passed:57/57 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation docker-network-proof-required: KILLED red=failed:17/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation docker-pull-never-required: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation docker-context-precedence: KILLED red=failed:5/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation docker-tcp-daemon-is-offhost: KILLED red=failed:2/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation docker-persisted-selection-read: KILLED red=failed:2/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation docker-malformed-selection-fails-closed: KILLED red=failed:1/55 green=passed:55/55 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation native-operator-initiator: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cloud-operator-initiator: KILLED red=failed:4/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mesh-operator-initiator: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation pkg-operator-initiator: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mesh-index-refusal-count: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mesh-package-refusal-count: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mesh-refusal-warning-not-windowed: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mesh-discovery-refusal-warning-not-windowed: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation ide-sse-redacts-wrapper: KILLED red=failed:2/8 green=passed:8/8 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation ide-model-discovery-warning: KILLED red=failed:1/8 green=passed:8/8 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation release-probe-does-not-execute-command: KILLED red=failed:2/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
```

### batch_06 — spec007-explicit-egress.json

```text
mutation improve-refusal-audit-outcome: KILLED red=failed:2/3 green=passed:3/3 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation improve-refusal-not-promoted: KILLED red=failed:2/3 green=passed:3/3 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation improve-refusal-audit-message: KILLED red=failed:2/3 green=passed:3/3 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation improve-wrapped-refusal: KILLED red=failed:1/3 green=passed:3/3 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cli-export-refusal-exit-1: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cli-export-refusal-exit-2: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cli-pkg-refusal-exit-1: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cli-pkg-refusal-exit-2: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cli-pkg-refusal-exit-3: KILLED red=failed:2/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cli-mesh-refusal-exit-1: KILLED red=failed:1/40 green=passed:40/40 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation convention-discarded-decision: KILLED red=failed:1/222 green=passed:222/222 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation convention-requires-local-action: KILLED red=failed:1/222 green=passed:222/222 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation convention-refuses-cannot-escape-scope: KILLED red=failed:2/222 green=passed:222/222 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation convention-evaluated-entry-confined: KILLED red=failed:1/222 green=passed:222/222 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation convention-process-funnel-no-direct-launch: KILLED red=failed:2/222 green=passed:222/222 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation convention-process-guard-exemption-narrow: KILLED red=failed:1/222 green=passed:222/222 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation startup-states-explicit-enforcement: KILLED red=failed:4/44 green=passed:44/44 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation cli-runtime-publish-refusal-exit: KILLED red=failed:2/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation operator-refusal-precedes-bundle-cleanup: KILLED red=failed:3/6 green=passed:6/6 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation operator-allowed-export-cleans-stale-bundle: KILLED red=failed:3/6 green=passed:6/6 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
```

### batch_07 — spec007-explicit-egress.json

```text
mutation cli-all-refusal-exit: KILLED red=failed:3/7 green=passed:7/7 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cli-caught-refusal-retention: KILLED red=failed:4/7 green=passed:7/7 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation cli-invocation-isolation: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation docker-bind-mounts-remain-network-export: KILLED red=failed:4/18 green=passed:18/18 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation initiator-writer-removed: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_08 — spec007-hygiene.json

```text
mutation api-missing-addresses: KILLED red=failed:4/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation loopback-http-ports: KILLED red=failed:3/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation api-prebind: KILLED red=failed:6/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation api-postbind: KILLED red=failed:2/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation api-verifier-wiring: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation loopback-ipv6: KILLED red=failed:5/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation listener-scheme: KILLED red=failed:2/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation adaptive-multiframe: KILLED red=failed:2/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation profile-postconfigure: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation d5-strictest: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mcp-direct-http: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation validator-bedrock: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation validator-brickhost: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation validator-meshlab: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation router-peer-refusal: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation profile-reader-convention: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mcp-callers-convention: KILLED red=failed:1/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mesh-anyip: KILLED red=failed:4/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation mesh-refusal: KILLED red=failed:4/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation loopback-mapped-address: KILLED red=failed:2/72 green=passed:72/72 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
```

### batch_09 — spec007-hygiene.json

```text
mutation mesh-mapped-socket: KILLED red=failed:1/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
```

### batch_10 — spec007-inbound-hosts.json

```text
mutation inbound-prebind: KILLED red=failed:14/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation inbound-postbind: KILLED red=failed:8/36 green=passed:36/36 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
```

### batch_11 — spec007-producers.json

```text
mutation m01-begin-read-does-not-enter: KILLED red=failed:14/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m02-live-does-not-skip-read: KILLED red=failed:8/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m03-read-frame-observes-outward: KILLED red=failed:45/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m04-complete-before-tool-invoke: KILLED red=failed:2/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m05-labelled-tool-never-reports: KILLED red=failed:35/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m06-rag-does-not-report-hits: KILLED red=failed:29/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m07-unknown-tier-public: KILLED red=failed:7/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m08-rag-does-not-trim: KILLED red=failed:9/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m09-rag-accepts-custom-same-level: KILLED red=failed:1/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m10-refusal-not-read-nothing: KILLED red=failed:2/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m11-empty-hits-not-read-nothing: KILLED red=failed:2/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m12-no-target-is-agent-backed: KILLED red=failed:12/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m13-stream-observed-only-at-end: KILLED red=failed:1/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m14-selfextend-no-frame: KILLED red=failed:5/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m15-selfextend-public-floor: KILLED red=failed:5/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m16-selfextend-not-using: KILLED red=failed:1/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m17-every-target-is-agent-backed: KILLED red=failed:7/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m18-stream-end-not-observed: KILLED red=failed:2/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m19-response-end-not-observed: KILLED red=failed:4/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m20-synchronous-response-throw-not-observed: KILLED red=failed:2/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_12 — spec007-producers.json

```text
mutation m21-synchronous-stream-throw-not-observed: KILLED red=failed:1/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m22-production-tool-declares-labelled: KILLED red=failed:1/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m23-all-argument-exceptions-read-nothing: KILLED red=failed:2/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m24-no-canonical-spelling-check: KILLED red=failed:5/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m25-production-egresssubject-alias: KILLED red=failed:1/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m26-report-completes-read: KILLED red=failed:2/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m27-rag-accepts-wrong-canonical-primitive: KILLED red=failed:4/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m28-production-labelled-marker-alias: KILLED red=failed:1/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m29-unrankable-message-spoof: KILLED red=failed:1/1266 green=passed:1266/1266 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_13 — spec007-redirects.json

```text
mutation m1-per-authority-decision-dropped: KILLED red=failed:4/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m2-egresshttp-installs-no-follower: KILLED red=failed:26/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m3-factory-filter-installs-no-follower: KILLED red=failed:33/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m4-guard-handler-not-restored: KILLED red=failed:7/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m5-p2-cross-origin-followed: KILLED red=failed:6/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m6-https-to-http-followed: KILLED red=failed:3/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m7-authorization-not-cleared: KILLED red=failed:21/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m8-post-send-check-dropped-async: KILLED red=failed:4/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m9-original-setting-forgotten: KILLED red=failed:63/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m10-method-rewrite-dropped: KILLED red=failed:5/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m11-walker-stops-at-the-hop: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m12-credentials-ignored: KILLED red=failed:2/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m13-api-sns-follows: KILLED red=failed:1/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation m14-fleet-sns-follows: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation m15-bedrock-follows: KILLED red=failed:4/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m17-unknown-primary-warning-inverted: KILLED red=failed:3/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m18-redirect-limit-off-by-one: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m19-no-note-treated-as-decided: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m20-hop-decided-by-process-default: KILLED red=failed:5/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m21-started-primary-as-if-flipped: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_14 — spec007-redirects.json

```text
mutation m22-known-primary-by-exact-type: KILLED red=failed:33/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m23-composite-primary-skipped: KILLED red=failed:3/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m24-cancellation-between-hops-ignored: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m25-intermediate-response-not-disposed: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m26-follower-follows-into-the-host-boundary: KILLED red=failed:9/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m27-unknown-warning-not-deduplicated: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m28-nested-P2-flag-ignored: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m29-sockets-credentials-ignored: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m30-primary-auto-redirect-not-disabled: KILLED red=failed:11/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m31-filter-before-factory-logging: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m32-per-hop-decision-dropped: KILLED red=failed:4/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m33-client-no-follow-marker-dropped: KILLED red=failed:10/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation m34-started-sockets-redundant-setter: KILLED red=failed:1/137 green=passed:137/137 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_15 — spec007-refusal-surfaces.json

```text
mutation s1-httpremotebrickcatalog-catch-1: KILLED red=failed:1/11 green=passed:11/11 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s2-httpremotebrickcatalog-catch-2: KILLED red=failed:1/11 green=passed:11/11 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s3-httpremotebrickcatalog-catch-3: KILLED red=failed:1/11 green=passed:11/11 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s4-compositebrickregistry-catch-1: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s5-compositebrickregistry-catch-2: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s6-modelartifactcatalogservice-catch-1: KILLED red=failed:2/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s7-remoteexecutionplatform-catch-1: KILLED red=failed:1/3 green=passed:3/3 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s8-remoteexecutionplatform-catch-2: KILLED red=failed:1/3 green=passed:3/3 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s9-remoteexecutionplatform-catch-3: KILLED red=failed:1/3 green=passed:3/3 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s10-validationserviceadapter-catch-1: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s11-validationserviceadapter-catch-2: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s12-meshlabworkerexecutorbackgroundservice-catch-1: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s13-elasticworkloadautoscaleservice-catch-1: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s14-ollamaprovider-catch-1: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s15-runpodhttpclient-catch-1: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s16-runpodhttpclient-catch-2: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s17-runpodhttpclient-catch-3: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s18-runpodhttpclient-catch-4: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s19-runpodhttpclient-catch-5: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s20-ashlarpeerbrickexecutor-catch-1: KILLED red=failed:1/24 green=passed:24/24 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_16 — spec007-refusal-surfaces.json

```text
mutation s21-mcpclientconnectionmanager-catch-1: KILLED red=failed:1/7 green=passed:7/7 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s22-mcpclientconnectionmanager-catch-2: KILLED red=failed:1/7 green=passed:7/7 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s23-mcpclientconnectionmanager-catch-3: KILLED red=failed:1/7 green=passed:7/7 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s24-toolboxmcptoolcontributor-catch-1: KILLED red=failed:2/14 green=passed:14/14 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s25-inprocessagenttransport-catch-1: KILLED red=failed:2/5 green=passed:5/5 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s26-endpointhealthmonitor-catch-1: KILLED red=failed:1/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s27-autonomyloopservice-catch-1: KILLED red=failed:1/11 green=passed:11/11 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s28-autonomyloopservice-catch-2: KILLED red=failed:1/11 green=passed:11/11 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s29-warning-repeat-count-lost: KILLED red=failed:3/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s30-warning-summary-expiry-lost: KILLED red=failed:1/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s31-warning-disposal-summary-lost: KILLED red=failed:2/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s32-aggregate-branches-lost: KILLED red=failed:16/21 green=passed:21/21 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s33-reference-validation-lost: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s34-echo-refusal-1: KILLED red=failed:2/21 green=passed:21/21 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s35-echo-refusal-2: KILLED red=failed:2/21 green=passed:21/21 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s36-provider-first-replaced: KILLED red=failed:2/12 green=passed:12/12 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s37-provider-refusal-lost: KILLED red=failed:6/12 green=passed:12/12 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s38-ollama-health-refusal-not-retained: KILLED red=failed:2/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s39-ollama-chat-refusal-consumed: KILLED red=failed:1/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s40-network-refusal-retried: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_17 — spec007-refusal-surfaces.json

```text
mutation s41-workflow-first-refusal-lost-1: KILLED red=failed:1/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s42-workflow-first-refusal-lost-2: KILLED red=failed:1/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s43-toolcallingagent-refusal-1: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s44-toolcallingagent-refusal-2: KILLED red=failed:3/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s45-toolcallingagent-refusal-3: KILLED red=failed:1/19 green=passed:19/19 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s46-auditingchatclient-refusal-1: KILLED red=failed:3/21 green=passed:21/21 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s47-auditingchatclient-refusal-2: KILLED red=failed:4/21 green=passed:21/21 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s48-auditingchatclient-refusal-3: KILLED red=failed:3/21 green=passed:21/21 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s49-auditingchatclient-refusal-4: KILLED red=failed:2/21 green=passed:21/21 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s50-a2aagenttransport-refusal-1: KILLED red=failed:2/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s51-a2aagenttransport-refusal-2: KILLED red=failed:2/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s52-agenttransportserviceimpl-refusal-1: KILLED red=failed:1/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s53-ashlara2aagenthandler-refusal-1: KILLED red=failed:1/8 green=passed:8/8 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation s54-agent-cap-raised: KILLED red=failed:2/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s55-inner-exception-edge-lost: KILLED red=failed:2/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s56-orchestrator-refusal-counts-as-breaker-failure: KILLED red=failed:4/15 green=passed:15/15 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s57-warning-reasons-collapsed: KILLED red=failed:1/4 green=passed:4/4 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s58-remote-format-leaks-extra-text: KILLED red=failed:1/1 green=passed:1/1 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s59-agenttransportserviceimpl-result-conversion-lost: KILLED red=failed:2/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s60-ashlara2aagenthandler-result-conversion-lost: KILLED red=failed:2/8 green=passed:8/8 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
```

### batch_18 — spec007-refusal-surfaces.json

```text
mutation s61-grpc-client-code-ignored: KILLED red=failed:3/10 green=passed:10/10 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s62-a2a-client-code-ignored: KILLED red=failed:3/8 green=passed:8/8 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net10.0
mutation s63-health-refusal-becomes-degraded: KILLED red=failed:1/9 green=passed:9/9 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s64-grpc-local-catch-1: KILLED red=failed:2/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s65-grpc-local-catch-2: KILLED red=failed:2/2 green=passed:2/2 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s66-orchestration-echo-answers-refusal: KILLED red=failed:6/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s67-provider-backed-model-drops-probe-refusal: KILLED red=failed:6/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s68-factory-probe-refusal-not-retained-outside-adaptive: KILLED red=failed:9/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s69-behavior-step-drops-probe-refusal: KILLED red=failed:1/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s70-ollama-probe-warning-not-windowed: KILLED red=failed:1/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s71-factory-dispose-does-not-flush-window: KILLED red=failed:1/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s72-planning-agent-canned-recommendation-answers-refusal: KILLED red=failed:4/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s73-synthesis-fallback-answers-refusal: KILLED red=failed:2/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s74-behavior-in-chain-refusal-not-kept: KILLED red=failed:3/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s75-behavior-fallback-error-outranks-refusal: KILLED red=failed:4/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s76-material-heuristics-answer-refusal: KILLED red=failed:2/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s77-vector-map-pass-through-answers-refusal: KILLED red=failed:2/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s78-behavior-refusal-flag-sticks: KILLED red=failed:2/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s79-behavior-probe-refusal-not-seeded: KILLED red=failed:1/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
mutation s80-behavior-refusal-flag-ignores-written-text: KILLED red=failed:4/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

### batch_19 — spec007-refusal-surfaces.json

```text
mutation s81-behavior-refusal-duplicated-when-last: KILLED red=failed:2/31 green=passed:31/31 ref=792446d09b59f98b47e08279907cb3d4df314caa framework=net8.0
```

## Convention-only cases across the complete campaign

These accepted cases demonstrate source-convention tripwires. They do not themselves establish changed runtime behavior, external sends, or downstream assertions not reached. Rationales and structured observation limits below retain the reviewers' wording. Repeated identical limit values are shown once with every original location. Fields whose provenance path includes source_prediction retain the earlier prediction; they are not additional observed results. Actual acceptance rests on the canonical rationale and verified failure records.

### Convention — spec007-default-enforcement.json / registry-new-client

Reviewer: codex/audit_campaign/map_refusals_23_44. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch01.json/reviews/6`; SHA-256 `90c13cd61c7b87f01e5de6289f7e190428ec3a1718160de3c206ee358b72920b`.
Original analyst record: `actual-review-batch01-04-07.json/entries/2`; SHA-256 `7f3abd91788f2133021c1468e4d6971a7424b2e8d440268b052e6e3c8bdbb03c`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.Every_Ashlar_factory_registration_has_a_protected_opt_out_name`

Rationale:

> The selected test scans production AddHttpClient registrations with Roslyn and compares names to compiled AshlarFactoryClientNames.All via reflection. Adding unreserved-mutation-client causes its exact inventory-equality assertion at FactoryNames.cs line80 to report that missing name. This is the intended new-client registry convention kill, with a real test assertion; it is not an observed HTTP opt-out refusal at runtime.

Preserved observation limits:

Locations: `analyst_row/concerns`.

```json
[]
```

Locations: `analyst_row/limitations`.

```json
[
  "Convention-only evidence for registry completeness. Does not by itself prove runtime enforcement/boot refusal for the injected client."
]
```

Locations: `batch_global/limits`.

```json
[
  "Batch01 evidence alone is not completion of the295-case campaign.",
  "registry-new-client and registry-missing-client demonstrate convention correspondence, not dynamic network enforcement.",
  "leak-m12-factory-removes-handler demonstrates runtime pipeline composition; no send-bypass claim is made for that mutant."
]
```

Locations: `analyst_global/review_scope`.

> batch_01 indexes004–007 only

Locations: `analyst_global/limits`.

```json
[
  "Review scope is four cases, not the whole campaign.",
  "Convention-only classification is intentional for registry-new-client and registry-missing-client; do not describe them as dynamic enforcement proofs.",
  "No build, rerun, source mutation or remote action was performed in this review."
]
```

### Convention — spec007-default-enforcement.json / registry-missing-client

Reviewer: codex/audit_campaign/map_refusals_23_44. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch01.json/reviews/7`; SHA-256 `90c13cd61c7b87f01e5de6289f7e190428ec3a1718160de3c206ee358b72920b`.
Original analyst record: `actual-review-batch01-04-07.json/entries/3`; SHA-256 `7f3abd91788f2133021c1468e4d6971a7424b2e8d440268b052e6e3c8bdbb03c`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.Every_Ashlar_factory_registration_has_a_protected_opt_out_name`

Rationale:

> Replacing protected AshlarExecution with mutation-retired-client makes the same Roslyn-inventory versus compiled-registry assertion fail at FactoryNames.cs line80. Actual message names mutation-retired-client versus AshlarExecution. The failure directly detects the exact missing-and-stale registry entry. It is a convention kill, not an observed network refusal.

Preserved observation limits:

Locations: `analyst_row/concerns`.

```json
[]
```

Locations: `analyst_row/limitations`.

```json
[
  "Convention-only evidence for registry correspondence. Runtime host-opt-out refusal is not exercised by this selected test."
]
```

Locations: `batch_global/limits`.

```json
[
  "Batch01 evidence alone is not completion of the295-case campaign.",
  "registry-new-client and registry-missing-client demonstrate convention correspondence, not dynamic network enforcement.",
  "leak-m12-factory-removes-handler demonstrates runtime pipeline composition; no send-bypass claim is made for that mutant."
]
```

Locations: `analyst_global/review_scope`.

> batch_01 indexes004–007 only

Locations: `analyst_global/limits`.

```json
[
  "Review scope is four cases, not the whole campaign.",
  "Convention-only classification is intentional for registry-new-client and registry-missing-client; do not describe them as dynamic enforcement proofs.",
  "No build, rerun, source mutation or remote action was performed in this review."
]
```

### Convention — spec007-explicit-egress.json / convention-discarded-decision

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch06.json/reviews/10`; SHA-256 `775489d00ca83dc8ac3d88a0af46fdd68dfa08f16c4d9b72da0b282a83c3417e`.
Original analyst record: `actual-review-batch06-10-19.json/entries/0`; SHA-256 `424ee7d840531b0cd53dcade0f7be37f9b0043aa4a57f649f17c65a7850ef85c`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F10_every_explicit_decision_is_acted_on`

Rationale:

> Accepted convention-only kill: removing DotnetRunner.ThrowIfRefused produces the exact production-source diagnostic src/Ashlar.Tools.Dev/DotnetRunner.cs:16: explicit decision is not acted on. F10 BeEmpty fails at ExplicitActions.cs27. This source scan does not execute DotnetRunner.

Preserved observation limits:

Locations: `analyst_row/concerns`.

```json
[]
```

Locations: `analyst_row/observation_limits`.

> Convention-only; no process execution/refusal observation from this case. Behavioral twins elsewhere must retain their own evidence identities.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of local010–019, explicit-egress manifest70–79

Locations: `analyst_global/review_scope`.

> Actual case payloads and originals match pinned manifest/source expectations. Parsed red/baseline/restored TRX and matched exact failure names/messages/stacks and multiset test-name inventories. All six source-convention/checker controls remain identified as convention-tripwire evidence; four runtime cases concern diagnostics, exit classification, and temporary-file replacement/preservation. Root cumulative verifier owns full diff/campaign reconciliation. No builds, source edits, or remote actions.

### Convention — spec007-explicit-egress.json / convention-requires-local-action

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch06.json/reviews/11`; SHA-256 `775489d00ca83dc8ac3d88a0af46fdd68dfa08f16c4d9b72da0b282a83c3417e`.
Original analyst record: `actual-review-batch06-10-19.json/entries/1`; SHA-256 `424ee7d840531b0cd53dcade0f7be37f9b0043aa4a57f649f17c65a7850ef85c`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F10_explicit_action_controls(body: "field = g.Evaluate(new EgressRequest(f, s, d)); if"···, accepted: False)`

Rationale:

> Accepted convention-checker negative control: making the local declaration keyword optional incorrectly accepts the field-assignment snippet. Its accepted:False theory row has no ExplicitActionProblems, so Any() is false instead of true at ExplicitActions.cs48. The snippet is source text, not executed policy logic.

Preserved observation limits:

Locations: `analyst_row/concerns`.

```json
[]
```

Locations: `analyst_row/observation_limits`.

> The checker is mutated in test source. Long fixture display is xUnit-truncated but its field-assignment prefix uniquely identifies the source InlineData row41.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of local010–019, explicit-egress manifest70–79

Locations: `analyst_global/review_scope`.

> Actual case payloads and originals match pinned manifest/source expectations. Parsed red/baseline/restored TRX and matched exact failure names/messages/stacks and multiset test-name inventories. All six source-convention/checker controls remain identified as convention-tripwire evidence; four runtime cases concern diagnostics, exit classification, and temporary-file replacement/preservation. Root cumulative verifier owns full diff/campaign reconciliation. No builds, source edits, or remote actions.

### Convention — spec007-explicit-egress.json / convention-refuses-cannot-escape-scope

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch06.json/reviews/12`; SHA-256 `775489d00ca83dc8ac3d88a0af46fdd68dfa08f16c4d9b72da0b282a83c3417e`.
Original analyst record: `actual-review-batch06-10-19.json/entries/2`; SHA-256 `424ee7d840531b0cd53dcade0f7be37f9b0043aa4a57f649f17c65a7850ef85c`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F10_explicit_action_controls(body: "var decision = g.Evaluate(new EgressRequest(f, s, "···, accepted: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F10_explicit_action_controls(body: "{ var decision = g.Evaluate(new EgressRequest(f, s"···, accepted: False)`

Rationale:

> Accepted convention-checker negative controls: extending the scan to code.Length incorrectly accepts both an another-member Refuses read and a nested-block escape. Exactly the two accepted:False fixture prefixes fail Any()==true at ExplicitActions.cs48. Synthetic source is scanned, not compiled or executed.

Preserved observation limits:

Locations: `analyst_row/concerns`.

```json
[]
```

Locations: `analyst_row/observation_limits`.

> Both xUnit names are truncated. Their distinct prefixes, accepted:False, actual failed count2, and source InlineData rows42–43 identify the intended member/block escape controls. No runtime policy path is executed.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of local010–019, explicit-egress manifest70–79

Locations: `analyst_global/review_scope`.

> Actual case payloads and originals match pinned manifest/source expectations. Parsed red/baseline/restored TRX and matched exact failure names/messages/stacks and multiset test-name inventories. All six source-convention/checker controls remain identified as convention-tripwire evidence; four runtime cases concern diagnostics, exit classification, and temporary-file replacement/preservation. Root cumulative verifier owns full diff/campaign reconciliation. No builds, source edits, or remote actions.

### Convention — spec007-explicit-egress.json / convention-evaluated-entry-confined

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch06.json/reviews/13`; SHA-256 `775489d00ca83dc8ac3d88a0af46fdd68dfa08f16c4d9b72da0b282a83c3417e`.
Original analyst record: `actual-review-batch06-10-19.json/entries/3`; SHA-256 `424ee7d840531b0cd53dcade0f7be37f9b0043aa4a57f649f17c65a7850ef85c`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F11_evaluated_process_entry_is_confined_to_the_two_guarded_funnels`

Rationale:

> Accepted convention-only confinement kill: F11 finds the extra src/Ashlar.Infrastructure/ParallelTesting/DotNetInstanceSpawner.cs file from the injected RunEvaluatedAsync reference, violating the two-file inventory at ExplicitActions.cs12. The injected MutationControl is never called.

Preserved observation limits:

Locations: `analyst_row/concerns`.

```json
[]
```

Locations: `analyst_row/observation_limits`.

> Confinement is an exact textual source-path inventory. Null arguments in the unused injected method are not evaluated; no runtime process API call is proven.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of local010–019, explicit-egress manifest70–79

Locations: `analyst_global/review_scope`.

> Actual case payloads and originals match pinned manifest/source expectations. Parsed red/baseline/restored TRX and matched exact failure names/messages/stacks and multiset test-name inventories. All six source-convention/checker controls remain identified as convention-tripwire evidence; four runtime cases concern diagnostics, exit classification, and temporary-file replacement/preservation. Root cumulative verifier owns full diff/campaign reconciliation. No builds, source edits, or remote actions.

### Convention — spec007-explicit-egress.json / convention-process-funnel-no-direct-launch

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch06.json/reviews/14`; SHA-256 `775489d00ca83dc8ac3d88a0af46fdd68dfa08f16c4d9b72da0b282a83c3417e`.
Original analyst record: `actual-review-batch06-10-19.json/entries/4`; SHA-256 `424ee7d840531b0cd53dcade0f7be37f9b0043aa4a57f649f17c65a7850ef85c`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F9_each_route_control_holds_or_fails_as_declared(name: "ProcessFunnel rejects a call in another member")`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F9_each_route_control_holds_or_fails_as_declared(name: "ProcessFunnel rejects a direct launch")`

Rationale:

> Accepted convention-checker negative controls: bypassing ProcessFunnelProblems yields an empty problem collection for exactly the direct-launch and other-member fixtures. Both F9 rows fail the required ProcessFunnel requires diagnostic at Routes.cs997; neither synthetic process is launched.

Preserved observation limits:

Locations: `analyst_row/concerns`.

```json
[]
```

Locations: `analyst_row/observation_limits`.

> Only source provenance diagnostics are established. The source fixture contains Process.Start text but does not launch a process.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of local010–019, explicit-egress manifest70–79

Locations: `analyst_global/review_scope`.

> Actual case payloads and originals match pinned manifest/source expectations. Parsed red/baseline/restored TRX and matched exact failure names/messages/stacks and multiset test-name inventories. All six source-convention/checker controls remain identified as convention-tripwire evidence; four runtime cases concern diagnostics, exit classification, and temporary-file replacement/preservation. Root cumulative verifier owns full diff/campaign reconciliation. No builds, source edits, or remote actions.

### Convention — spec007-explicit-egress.json / convention-process-guard-exemption-narrow

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch06.json/reviews/15`; SHA-256 `775489d00ca83dc8ac3d88a0af46fdd68dfa08f16c4d9b72da0b282a83c3417e`.
Original analyst record: `actual-review-batch06-10-19.json/entries/5`; SHA-256 `424ee7d840531b0cd53dcade0f7be37f9b0043aa4a57f649f17c65a7850ef85c`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F9_each_route_control_holds_or_fails_as_declared(name: "Reason fails: process GuardImpl in another file")`

Rationale:

> Accepted convention-checker negative control: broadening process GuardImpl exemption to every path causes the other-file fixture to return no problem. F9 requires Exempt:GuardImpl is accepted only and fails at Routes.cs997. This exercises test-only scanner logic.

Preserved observation limits:

Locations: `analyst_row/concerns`.

```json
[]
```

Locations: `analyst_row/observation_limits`.

> A test-only exemption checker is mutated. The legitimate process GuardImpl path passes, but this is not independent runtime enforcement evidence.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of local010–019, explicit-egress manifest70–79

Locations: `analyst_global/review_scope`.

> Actual case payloads and originals match pinned manifest/source expectations. Parsed red/baseline/restored TRX and matched exact failure names/messages/stacks and multiset test-name inventories. All six source-convention/checker controls remain identified as convention-tripwire evidence; four runtime cases concern diagnostics, exit classification, and temporary-file replacement/preservation. Root cumulative verifier owns full diff/campaign reconciliation. No builds, source edits, or remote actions.

### Convention — spec007-explicit-egress.json / initiator-writer-removed

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch07.json/reviews/4`; SHA-256 `eff125cff03c835d0aadb82108dddf60d23d8c4d59d8e42c5c7a2e68c28b87e0`.
Original analyst record: `actual-review-batch07-00-04.json/entries/4`; SHA-256 `d5f72c11e5d2db5a1d2daae6558707e7844c1e6de02d92bcb6f09c8b33ab6c9b`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressGuardConventionTests.F12_operator_initiator_writers_are_confined_to_the_three_export_files`

Rationale:

> The sole F12 convention Fact fails EgressGuardConventionTests.Initiators.cs:16 because the expected three-file writer set lacks application/src/Ashlar.CLI/Commands/PkgCommand.cs. The applied diff removes that file sole Initiator assignment. This is precisely the Roslyn source-inventory tripwire intended by the manifest, and is accepted only as convention evidence, with no runtime export/enforcement proof.

Preserved observation limits:

Locations: `analyst_row/first_assertion_limitations`.

> Convention-only. The test parses assignments in source; it does not execute pkg export, prove operator authorization, validate assignment values, or exclude reflection-based writers. The preliminary scan-size assertion passed, but cannot turn this into behavioral evidence.

Locations: `analyst_row/source_prediction/runtime_scope`.

> No production behavior is executed by this test. Roslyn parses source files and identifies AssignmentExpressionSyntax whose left identifier/member is named Initiator. An empty initializer removes PkgCommand from that source writer set.

Locations: `analyst_row/source_prediction/limitations`.

> This is explicitly a convention tripwire, not runtime behavioral evidence. It proves the exact source inventory check detects a missing writer. It does not prove authorized exports succeed, refusal enforcement holds, reflection cannot write the property, or assignment values are correct. Runtime behavior must be supported by separate cases. The preliminary scan-files>1000 assertion is a scan sanity check, not an acceptable intended failure.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/review_scope`.

> Semantic relevance of all5 actual batch07 red TRX cases, explicit manifest80-84. Compared source, applied diffs, actual assertion messages/stacks and prior predictions. Parent verifier owns byte integrity, baseline/green validity, inventory and restoration.

Locations: `analyst_global/qualifications`.

```json
[
  "initiator-writer-removed is accepted as an intended source-convention mutation only; it provides no runtime behavioral proof.",
  "CLI subprocess security exceptions are captured by the intended test and asserted as wrong exit statuses; the campaign harness itself did not crash."
]
```

### Convention — spec007-hygiene.json / profile-reader-convention

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch08.json/reviews/15`; SHA-256 `9f4131533a4099e1a99fb9238abfdf76cac6b81781e7d0f577f6fdc712ba6346`.
Original analyst record: `actual-review-batch08-10-19.json/entries/5`; SHA-256 `1f712e3cb3b2afaa79d76320895af0f96e48274dc8a11cc8228d435d1e8db438`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.DeploymentProfileReadConventionTests.Infrastructure_the_API_and_the_CLI_never_reference_the_profile_source`

Rationale:

> The Roslyn source scan identifies AdaptiveProviderFactory.cs as the new forbidden profile-source reader, failing the intended empty-offenders assertion at line 37. The mutation inserted a discarded environment read.

Preserved observation limits:

Locations: `analyst_row/first_assertion_limitations`.

> Convention tripwire only: the constructor is not executed by this assertion, the discarded value does not alter profile behavior, and no runtime policy bypass is proved.

Locations: `analyst_row/source_prediction/runtime_scope`.

> Roslyn scans production syntax for the profile source identifier or literal ASHLAR_DEPLOYMENT_PROFILE. Adding a discarded Environment.GetEnvironmentVariable call introduces that forbidden literal, which the scan detects.

Locations: `analyst_row/source_prediction/limitations`.

> Convention-only: the extra read result is discarded and this test does not establish any runtime profile change. It does not execute the constructor, trace environment access or defend against reflection/dynamically constructed names. Scan-count failures would not be the intended evidence.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/review_scope`.

> Semantic relevance of actual red TRX names, messages and stacks against pinned source mutation and test assertions. Parent cumulative verifier handles archive integrity, exact byte inventory, baseline/green TRX validity and restoration.

Locations: `analyst_global/qualifications`.

```json
[
  "Per-case acceptance does not establish completion of the full campaign.",
  "Runtime and source-convention evidence are separated; first-assertion limits are retained per case."
]
```

### Convention — spec007-hygiene.json / mcp-callers-convention

Reviewer: codex/independent-semantic-audit. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch08.json/reviews/16`; SHA-256 `9f4131533a4099e1a99fb9238abfdf76cac6b81781e7d0f577f6fdc712ba6346`.
Original analyst record: `actual-review-batch08-10-19.json/entries/6`; SHA-256 `1f712e3cb3b2afaa79d76320895af0f96e48274dc8a11cc8228d435d1e8db438`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.DeploymentProfileReadConventionTests.Production_HTTP_MCP_callers_use_the_Ashlar_transport_wrapper`

Rationale:

> The production invocation scan finds application/src/Ashlar.API/Program.cs as an additional direct SDK WithHttpTransport caller, failing the exact allowed-callers inventory at line 25.

Preserved observation limits:

Locations: `analyst_row/first_assertion_limitations`.

> Convention tripwire only: this failure does not demonstrate an HTTP listener opening. Unchanged runtime MCP validation also detects direct SDK HTTP transport.

Locations: `analyst_row/source_prediction/runtime_scope`.

> Roslyn parses invocation expressions and matches WithHttpTransport. Replacing the API Ashlar wrapper with the SDK call violates the pinned caller set. This convention complements runtime detection of direct HTTP registration.

Locations: `analyst_row/source_prediction/limitations`.

> Convention-only: it does not itself show HTTP can boot on a restricted profile. The unchanged runtime validator should still detect direct SDK HTTP. Only the actual call-set mismatch, not scan sanity or parsing/build failures, is intended evidence.

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/review_scope`.

> Semantic relevance of actual red TRX names, messages and stacks against pinned source mutation and test assertions. Parent cumulative verifier handles archive integrity, exact byte inventory, baseline/green TRX validity and restoration.

Locations: `analyst_global/qualifications`.

```json
[
  "Per-case acceptance does not establish completion of the full campaign.",
  "Runtime and source-convention evidence are separated; first-assertion limits are retained per case."
]
```

### Convention — spec007-producers.json / m16-selfextend-not-using

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch11.json/reviews/15`; SHA-256 `7a5c59c9bd115bccf6571c33e549a03c5e3c6eec49d3a5345f0167e14dbf8aeb`.
Original analyst record: `actual-review-batch11-10-19.json/entries/5`; SHA-256 `9dc64d52281febe7bcb1f5d8d76485cd1e88b5f22e80f124af32346d5e2e27f3`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressSubjectProducerConventionTests.Every_production_frame_entry_is_a_using_in_a_method_that_is_not_an_iterator`

Rationale:

> Accepted convention-only disposal tripwire: replacing using(EgressSubject.Enter(...)) with a discarded returned frame makes the actual source scanner report IsUsing=False for SelfExtendRunnerAdapter.RunAsync. The only failing result is Every_production_frame_entry_is_a_using_in_a_method_that_is_not_an_iterator at aggregate line87; the message identifies the exact altered source site and required using resource. The other1265 selected tests pass in the red run.

Preserved observation limits:

Locations: `analyst_row/observation_limits`.

```json
[
  "This is source-structure evidence only. The sole observed failure does not demonstrate a runtime leaked frame, incorrect label, network send or disposal-order failure.",
  "The source scan's static-import, alias and nonempty-site controls precede the aggregate; its message reports only the IsUsing condition, not an iterator violation."
]
```

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of batch11 local010–019; all red results and precise observation limits retained.

### Convention — spec007-producers.json / m22-production-tool-declares-labelled

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch12.json/reviews/1`; SHA-256 `d43b2cdf9e9bbfb415e7919b4aca0c1b0401e1d889b40fd5ce001535e5a6c2d9`.
Original analyst record: `actual-review-batch12-00-08.json/entries/1`; SHA-256 `53b96d209613d3b60cbb6cbebdfad0fe9f2676b15ea023ade844fc48d52e5017`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressSubjectProducerConventionTests.Only_RAGTool_declares_itself_labelled_in_production`

Rationale:

> Accepted convention-only trusted-reader inventory tripwire: adding IEgressLabelledTool and a Public-reporting ReportRead to RepoFsReadTool makes the sole actual failure list that additional production implementer. Only_RAGTool_declares_itself_labelled_in_production fails at100 after the preceding source-scan-size assertion passes. The actual inventory exactly identifies src/Ashlar.Tools.Dev/RepoFsReadTool.cs | RepoFsReadTool.

Preserved observation limits:

Locations: `analyst_row/observation_limits`.

```json
[
  "The observed failure is a source implementer-inventory check; it does not demonstrate executing the added ReportRead, lowering an actual read label, or allowing a send.",
  "The other1265 selected tests pass in the red run; no additional runtime behavioral evidence is claimed."
]
```

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of batch12 local000–008; all18 actual red failures and first-assertion limits retained.

### Convention — spec007-producers.json / m25-production-egresssubject-alias

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch12.json/reviews/4`; SHA-256 `d43b2cdf9e9bbfb415e7919b4aca0c1b0401e1d889b40fd5ce001535e5a6c2d9`.
Original analyst record: `actual-review-batch12-00-08.json/entries/4`; SHA-256 `53b96d209613d3b60cbb6cbebdfad0fe9f2676b15ea023ade844fc48d52e5017`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressSubjectProducerConventionTests.Every_production_frame_entry_is_a_using_in_a_method_that_is_not_an_iterator`

Rationale:

> Accepted convention-only alias tripwire: inserting the unused ES alias of EgressSubject into PlannerScratchpad makes the sole failed convention assertion at85 report exactly src/Ashlar.BackgroundAgents.HostRunners/PlannerScratchpad.cs | ES. The static-import check at84 passes before the alias check fails. The mutation adds no actual frame-entry operation.

Preserved observation limits:

Locations: `analyst_row/observation_limits`.

```json
[
  "This detects syntax that could hide a production frame from the scanner; it is not runtime frame, label or send evidence.",
  "The source-site population, using-resource and iterator assertions at86–90 are not reached after the alias failure."
]
```

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of batch12 local000–008; all18 actual red failures and first-assertion limits retained.

### Convention — spec007-producers.json / m28-production-labelled-marker-alias

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `convention-tripwire`.
Canonical review: `actual-review-batch12.json/reviews/7`; SHA-256 `d43b2cdf9e9bbfb415e7919b4aca0c1b0401e1d889b40fd5ce001535e5a6c2d9`.
Original analyst record: `actual-review-batch12-00-08.json/entries/7`; SHA-256 `53b96d209613d3b60cbb6cbebdfad0fe9f2676b15ea023ade844fc48d52e5017`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressSubjectProducerConventionTests.Every_production_frame_entry_is_a_using_in_a_method_that_is_not_an_iterator`

Rationale:

> Accepted convention-only labelled-marker alias tripwire: adding the unused L alias of IEgressLabelledTool to RepoFsReadTool makes the sole failed convention assertion at85 identify src/Ashlar.Tools.Dev/RepoFsReadTool.cs | L. The preceding static-import assertion passes. This is the intended protection against hiding a trusted-reader declaration from the syntactic implementer scanner.

Preserved observation limits:

Locations: `analyst_row/observation_limits`.

```json
[
  "The mutation introduces no alias-backed implementer and does not execute a trusted read or send.",
  "The source-site population, using-resource and iterator assertions at86–90 are not reached after the alias failure."
]
```

Locations: `batch_global/limits`.

```json
[
  "Only this batch is reviewed here; campaign completeness is a separate reconciliation gate.",
  "Runtime diagnostic or composition assertions do not alone establish dynamic send refusal; per-case rationale states the actual demonstrated behavior."
]
```

Locations: `analyst_global/scope`.

> Actual semantic review of batch12 local000–008; all18 actual red failures and first-assertion limits retained.


## Semantic review of the 65 cases without expected-failure declarations

### spec007-refusal-surfaces.json / s1-httpremotebrickcatalog-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/000-s1-httpremotebrickcatalog-catch-1`. Baseline evidence: `batch_15/decoded/000-s1-httpremotebrickcatalog-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.HttpRemoteBrickCatalogCapabilityFallbackTests.Refusal_returns_empty_results_without_reusing_a_stale_manifest`

Rationale:

> The sole actual failure is a runtime Moq verification at HttpRemoteBrickCatalogCapabilityFallbackTests:43: a non-7307 Warning was invoked once although none is permitted. Its invocation log identifies GetAllAsync's generic Failed to fetch brick catalog warning with the injected IOException private-canary and inner EgressRefusedException; the recorded production frame is HttpRemoteBrickCatalog:53. The mutation adds an empty-message prerequisite to the dedicated refusal catch at line 68, while the fixture wrapper has a nonempty message, so control reaches the generic catch at 73–76 instead of reporting through EgressRefusalWarnings. Earlier assertions passed: refused capabilities are null/nonstale, GetAllAsync returns empty, GetByIdAsync returns null, four stub calls occurred, and one 7307 warning exists. The discriminating detection is the added generic diagnostic, not a returned-data or transport failure. There are no extra failed results.

### spec007-refusal-surfaces.json / s2-httpremotebrickcatalog-catch-2

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/001-s2-httpremotebrickcatalog-catch-2`. Baseline evidence: `batch_15/decoded/000-s1-httpremotebrickcatalog-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.HttpRemoteBrickCatalogCapabilityFallbackTests.Refusal_returns_empty_results_without_reusing_a_stale_manifest`

Rationale:

> The sole actual failure is the same runtime no-generic-warning verification at HttpRemoteBrickCatalogCapabilityFallbackTests:43, now with the recorded warning Failed to fetch brick brick from https://remote.example/ and the GetByIdAsync production frame at line 87. The mutation disables its refusal catch at line 96 for the injected nonempty IOException private-canary, so the generic catch at 105–108 logs that wrapper. The preceding null/nonstale capability, empty list, null item, four-call and one-7307-warning assertions pass. Thus this distinct mutation is detected through the item-fetch diagnostic path even though the safe null return remains unchanged. No additional failed result needs another explanation.

### spec007-refusal-surfaces.json / s3-httpremotebrickcatalog-catch-3

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/002-s3-httpremotebrickcatalog-catch-3`. Baseline evidence: `batch_15/decoded/000-s1-httpremotebrickcatalog-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.HttpRemoteBrickCatalogCapabilityFallbackTests.Refusal_returns_empty_results_without_reusing_a_stale_manifest`

Rationale:

> The sole actual failure occurs earlier in the same fixture, at HttpRemoteBrickCatalogCapabilityFallbackTests:36: refused.Capabilities should be null but is a NodeCapabilityManifestDto with NodeId node-a. The first stub response seeded that cached manifest; subsequent requests throw IOException private-canary around a refusal. Adding the nonempty-message-blocking condition to GetCapabilitiesWithStalenessAsync's refusal catch at line 152 sends the exception to the generic catch at 170–180, which selects an eligible cached snapshot rather than returning an empty CapabilitiesFetchResult. This is direct runtime evidence of forbidden stale-capability reuse after refusal. It is not merely a changed log event, and there are no other failed rows.

### spec007-refusal-surfaces.json / s4-compositebrickregistry-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/003-s4-compositebrickregistry-catch-1`. Baseline evidence: `batch_15/decoded/003-s4-compositebrickregistry-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.CompositeCatalogRefusalTests.Refused_catalog_does_not_hide_an_available_catalog_and_warnings_are_windowed`

Rationale:

> The sole actual failure at CompositeCatalogRefusalTests:38 rejects one non-7307 Warning. The recorded invocation is Failed to get brick brick from catalog (null), carrying IOException wrapper with the nested refusal and the GetBrick production frame at CompositeBrickRegistry:48. The exact change makes the refusal catch at line 64 reject that nonempty wrapper, so the generic line-70 warning executes. Both the allowed catalog's GetBrick result and its GetAllBricks result were successfully retained before this assertion, and the one-7307-warning assertion also passed from the other still-protected path. This mutation is semantically detected as incorrect runtime refusal logging for GetBrick, not loss of available-catalog results. No extra red result is present.

### spec007-refusal-surfaces.json / s5-compositebrickregistry-catch-2

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/004-s5-compositebrickregistry-catch-2`. Baseline evidence: `batch_15/decoded/003-s4-compositebrickregistry-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.CompositeCatalogRefusalTests.Refused_catalog_does_not_hide_an_available_catalog_and_warnings_are_windowed`

Rationale:

> The sole actual failure at CompositeCatalogRefusalTests:38 again rejects one non-7307 Warning, but its invocation is Failed to get bricks from catalog (null), with IOException wrapper and the GetAllBricks production frame at CompositeBrickRegistry:89. Disabling the line-106 refusal catch for the nonempty wrapper directs this call to the generic line-112 warning. Earlier allowed-catalog GetBrick/GetAllBricks result assertions and the single 7307 event assertion pass. The two composite mutations therefore have distinct recorded causal generic-log paths, while both retain the available catalog's results. There is no unrelated additional failure.

### spec007-refusal-surfaces.json / s6-modelartifactcatalogservice-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/005-s6-modelartifactcatalogservice-catch-1`. Baseline evidence: `batch_15/decoded/005-s6-modelartifactcatalogservice-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.ModelArtifacts.ModelArtifactCatalogServiceTests.Refused_source_preserves_other_results_and_emits_windowed_warnings(duringAvailability: True)`
- `Ashlar.Tests.Infrastructure.Tests.ModelArtifacts.ModelArtifactCatalogServiceTests.Refused_source_preserves_other_results_and_emits_windowed_warnings(duringAvailability: False)`

Rationale:

> Both actual failed rows are relevant runtime diagnostic assertions at ModelArtifactCatalogServiceTests:34, one with duringAvailability=True and one False. Each expected exactly one Warning event 7307 after three polls, but observed zero; each complete Moq invocation list contains three generic event-0 warnings carrying IOException wrapped and the refusal. The availability variant points to production line 53; the listing variant points to line 58. The added empty-message condition bypasses their common dedicated catch at line 64, sending both to the generic continuation catch at 72–74. The three preceding polls each retained the local model m1, so successful result preservation is already observed and the failed contract is refusal-warning classification/windowing. These two theory rows account for every red result in the four-test filter.

### spec007-refusal-surfaces.json / s7-remoteexecutionplatform-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/006-s7-remoteexecutionplatform-catch-1`. Baseline evidence: `batch_15/decoded/006-s7-remoteexecutionplatform-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Testing.RemoteExecutionPlatformGapCoverageTests.Refused_operations_return_redacted_failures_and_one_windowed_warning`

Rationale:

> The sole actual failure is the no-non-7307-log verification at RemoteExecutionPlatformGapCoverageTests:44. Its invocation list contains event-0 Warning Remote Ashlar API is not reachable with IOException private-canary, whose production frame is IsAvailableAsync at line 43, plus one correct refusal event from the still-protected operations. The mutation disables only the availability refusal catch at line 46 for this nonempty wrapper and therefore executes the generic warning at line 53. Earlier assertions confirm availability=false, build/run success=false, their redacted messages, empty stdout, three stub calls and one event-7307 warning. The distinguishing observation is the extra generic availability diagnostic, not an attempted real remote build or container run.

### spec007-refusal-surfaces.json / s8-remoteexecutionplatform-catch-2

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/007-s8-remoteexecutionplatform-catch-2`. Baseline evidence: `batch_15/decoded/006-s7-remoteexecutionplatform-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Testing.RemoteExecutionPlatformGapCoverageTests.Refused_operations_return_redacted_failures_and_one_windowed_warning`

Rationale:

> The sole actual failure at RemoteExecutionPlatformGapCoverageTests:36 reports build.ErrorMessage equal to private-canary instead of the redacted Egress refused by policy message. The availability=false and build.Success=false assertions already passed. The exact mutation rejects the nonempty wrapper in BuildImageAsync's refusal catch at line 80, causing its generic catch at 85–89 to return ex.Message rather than refusal.Message. This directly detects incorrect returned error redaction at the build-result surface. It is an assertion failure after a controlled handler throws the injected refusal, not an actual build failure or infrastructure exception. No additional red result is present.

### spec007-refusal-surfaces.json / s9-remoteexecutionplatform-catch-3

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/008-s9-remoteexecutionplatform-catch-3`. Baseline evidence: `batch_15/decoded/006-s7-remoteexecutionplatform-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Testing.RemoteExecutionPlatformGapCoverageTests.Refused_operations_return_redacted_failures_and_one_windowed_warning`

Rationale:

> The sole actual failure at RemoteExecutionPlatformGapCoverageTests:39 reports run.StandardError equal to private-canary rather than the redacted Egress refused by policy message. Availability, build failure/redaction and run.Success=false assertions have already passed. Disabling the RunContainerAsync refusal catch at line 125 for the nonempty wrapper selects the generic catch at 130–134, which puts ex.Message into StandardError instead of refusal.Message. This is direct runtime output-redaction evidence specific to the run-result surface. The controlled HttpMessageHandler supplies the exception, so no real remote container operation is established by the row.

### spec007-refusal-surfaces.json / s10-validationserviceadapter-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/009-s10-validationserviceadapter-catch-1`. Baseline evidence: `batch_15/decoded/009-s10-validationserviceadapter-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Validation.ValidationServiceAdapterGapCoverageTests.Refusal_preserves_completed_evidence_without_inventing_failed_tests(outerCatch: False)`

Rationale:

> The sole actual failure is the outerCatch=False theory row at ValidationServiceAdapterGapCoverageTests:55: result.TestsFailed is 1 instead of 0. Earlier result.Passed=false, TestsRun=2 and TestsPassed=2 assertions passed. The fixture parser returns two completed pass records for its first call, then throws IOException private-canary with an inner refusal on its second call. Bypassing the adapter's per-project refusal catch at line 237 routes this exception into the generic catch, which increments totalTestsFailed at line 249 instead of preserving the completed counts and recording an evidence error. This causally explains the invented failed-test count while completed run/pass counts remain two. The separate outerCatch=True row passes because it exercises the unchanged outer catch. There are no additional red failures in the two-row filter.

### spec007-refusal-surfaces.json / s11-validationserviceadapter-catch-2

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/010-s11-validationserviceadapter-catch-2`. Baseline evidence: `batch_15/decoded/009-s10-validationserviceadapter-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Validation.ValidationServiceAdapterGapCoverageTests.Refusal_preserves_completed_evidence_without_inventing_failed_tests(outerCatch: True)`

Rationale:

> Accepted from the actual runtime failure despite the absent expected_failures field: only the outerCatch:true theory row fails at ValidationServiceAdapterGapCoverageTests line 53, with TestsRun=0 instead of 2. The exact mutation adds ex.Message.Length == 0 to the outer refusal catch at product line 287. The progress callback supplies IOException("private-canary", refusal), so the mutant skips that catch and reaches the generic catch at 302, which explicitly resets TestsRun/TestsPassed/TestsFailed at 309-311. The preceding Passed=false assertion at test line 52 passes; the outerCatch:false control also passes. This directly detects loss of completed validation evidence through the outer wrapped-refusal path.

### spec007-refusal-surfaces.json / s12-meshlabworkerexecutorbackgroundservice-catch-1

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/011-s12-meshlabworkerexecutorbackgroundservice-catch-1`. Baseline evidence: `batch_15/decoded/011-s12-meshlabworkerexecutorbackgroundservice-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.MeshLab.MeshLabWorkerExecutorBackgroundServiceGapCoverageTests.Refused_polls_warn_once_and_the_hosted_loop_continues`

Rationale:

> Accepted from the actual runtime warning failure: MeshLabWorkerExecutorBackgroundServiceGapCoverageTests line 41 expects one Warning/EventId 7307 but observes zero. Adding the empty-message predicate bypasses the product refusal catch at 52 for two injected HttpRequestException("wrapper", refusal) polls, routing them into Debug logging at 56-58. The full actual Moq message retains both refusal-bearing Debug invocations and a third Debug invocation containing a shutdown TaskCanceledException during JSON parsing. That last invocation is context from stopping the third request, not a separate failed test or campaign timeout. The third-poll signal, service stop and calls>=3 assertion at line 40 complete before the warning failure, so continuation is observed.

### spec007-refusal-surfaces.json / s13-elasticworkloadautoscaleservice-catch-1

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/012-s13-elasticworkloadautoscaleservice-catch-1`. Baseline evidence: `batch_15/decoded/012-s13-elasticworkloadautoscaleservice-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Scaling.AutoscaleRefusalTests.Refused_ticks_are_windowed_and_do_not_stop_later_ticks`

Rationale:

> Accepted from the actual runtime diagnostic failure: AutoscaleRefusalTests line 37 expects warnings {7307}, but receives {0, 0}. The mutation bypasses the refusal catch at ElasticWorkloadAutoscaleService line 57 for two IOException("wrapper", refusal) ticks, causing generic Warning logging at 65-67 instead of the refusal warning window. The third-tick signal, service stop and calls>=3 assertion at line 36 pass first. Both the warning identity/count regression and loop continuation are therefore causally observed.

### spec007-refusal-surfaces.json / s14-ollamaprovider-catch-1

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/013-s14-ollamaprovider-catch-1`. Baseline evidence: `batch_15/decoded/013-s14-ollamaprovider-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Real_Ollama_health_refusal_survives_provider_availability_conversion(wrapped: True)`

Rationale:

> Accepted from the actual runtime exception-identity failure: only Real_Ollama_health_refusal_survives_provider_availability_conversion(wrapped:true) fails at EgressProviderFallbackTests line 69. It expects the original EgressRefusedException object but receives ModelUnavailableException from AdaptiveProviderFactory line 59. Adding the empty-message predicate bypasses OllamaProvider.RefreshModelsAsync refusal catch 122; the wrapped HttpRequestException reaches catch 135 and becomes OLLAMA_UNREACHABLE. ProviderFactory returns ordinary unavailability at 802-803 instead of recording a refusal through exception handling at 807-812, so adaptive resolution throws ordinary model unavailability. The direct-refusal control passes because its exception is not consumed by the HTTP catch.

### spec007-refusal-surfaces.json / s15-runpodhttpclient-catch-1

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/014-s15-runpodhttpclient-catch-1`. Baseline evidence: `batch_15/decoded/014-s15-runpodhttpclient-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.RunPodHttpClientTests.Every_RunPod_operation_preserves_refusal_as_a_distinct_failure`

Rationale:

> Accepted from the actual operation-specific runtime mismatch, not merely the shared test name: Every_RunPod_operation_preserves_refusal_as_a_distinct_failure fails its final OnlyContain predicate at RunPodHttpClientTests line 39. The reported offending error is runpod.spinup_exception, Message="RunPod spin up failed.", Detail="wrapper", rather than runpod.egress_refused with the original refusal message. The fixture supplies HttpRequestException("wrapper", refusal) at line 26; the exact added ex.Message.Length == 0 predicate skips the SpinUpInstance refusal catch at RunPodHttpClient line 68 and reaches its generic return at 75. The SpinUpInstance call is at test line 32. All five operation calls at 32-36 complete, and calls==5 at 38 passes before the common assertion. The actual output identifies exactly this mutated operation error. PollJobStatus_MapsCancelledState_ToFailed passes in the same red run.

### spec007-refusal-surfaces.json / s16-runpodhttpclient-catch-2

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/015-s16-runpodhttpclient-catch-2`. Baseline evidence: `batch_15/decoded/014-s15-runpodhttpclient-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.RunPodHttpClientTests.Every_RunPod_operation_preserves_refusal_as_a_distinct_failure`

Rationale:

> Accepted from the actual operation-specific runtime mismatch, not merely the shared test name: Every_RunPod_operation_preserves_refusal_as_a_distinct_failure fails its final OnlyContain predicate at RunPodHttpClientTests line 39. The reported offending error is runpod.dispatch_exception, Message="RunPod dispatch failed.", Detail="wrapper", rather than runpod.egress_refused with the original refusal message. The fixture supplies HttpRequestException("wrapper", refusal) at line 26; the exact added ex.Message.Length == 0 predicate skips the DispatchJob refusal catch at RunPodHttpClient line 118 and reaches its generic return at 125. The DispatchJob call is at test line 33. All five operation calls at 32-36 complete, and calls==5 at 38 passes before the common assertion. The actual output identifies exactly this mutated operation error. PollJobStatus_MapsCancelledState_ToFailed passes in the same red run.

### spec007-refusal-surfaces.json / s17-runpodhttpclient-catch-3

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/016-s17-runpodhttpclient-catch-3`. Baseline evidence: `batch_15/decoded/014-s15-runpodhttpclient-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.RunPodHttpClientTests.Every_RunPod_operation_preserves_refusal_as_a_distinct_failure`

Rationale:

> Accepted from the actual operation-specific runtime mismatch, not merely the shared test name: Every_RunPod_operation_preserves_refusal_as_a_distinct_failure fails its final OnlyContain predicate at RunPodHttpClientTests line 39. The reported offending error is runpod.poll_exception, Message="RunPod polling failed.", Detail="wrapper", rather than runpod.egress_refused with the original refusal message. The fixture supplies HttpRequestException("wrapper", refusal) at line 26; the exact added ex.Message.Length == 0 predicate skips the PollJobStatus refusal catch at RunPodHttpClient line 154 and reaches its generic return at 161. The PollJobStatus call is at test line 34. All five operation calls at 32-36 complete, and calls==5 at 38 passes before the common assertion. The actual output identifies exactly this mutated operation error. PollJobStatus_MapsCancelledState_ToFailed passes in the same red run.

### spec007-refusal-surfaces.json / s18-runpodhttpclient-catch-4

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/017-s18-runpodhttpclient-catch-4`. Baseline evidence: `batch_15/decoded/014-s15-runpodhttpclient-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.RunPodHttpClientTests.Every_RunPod_operation_preserves_refusal_as_a_distinct_failure`

Rationale:

> Accepted from the actual operation-specific runtime mismatch, not merely the shared test name: Every_RunPod_operation_preserves_refusal_as_a_distinct_failure fails its final OnlyContain predicate at RunPodHttpClientTests line 39. The reported offending error is runpod.pull_exception, Message="RunPod result pull failed.", Detail="wrapper", rather than runpod.egress_refused with the original refusal message. The fixture supplies HttpRequestException("wrapper", refusal) at line 26; the exact added ex.Message.Length == 0 predicate skips the PullResults refusal catch at RunPodHttpClient line 182 and reaches its generic return at 189. The PullResults call is at test line 35. All five operation calls at 32-36 complete, and calls==5 at 38 passes before the common assertion. The actual output identifies exactly this mutated operation error. PollJobStatus_MapsCancelledState_ToFailed passes in the same red run.

### spec007-refusal-surfaces.json / s19-runpodhttpclient-catch-5

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/018-s19-runpodhttpclient-catch-5`. Baseline evidence: `batch_15/decoded/014-s15-runpodhttpclient-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.RunPodHttpClientTests.Every_RunPod_operation_preserves_refusal_as_a_distinct_failure`

Rationale:

> Accepted from the actual operation-specific runtime mismatch, not merely the shared test name: Every_RunPod_operation_preserves_refusal_as_a_distinct_failure fails its final OnlyContain predicate at RunPodHttpClientTests line 39. The reported offending error is runpod.terminate_exception, Message="RunPod instance termination failed.", Detail="wrapper", rather than runpod.egress_refused with the original refusal message. The fixture supplies HttpRequestException("wrapper", refusal) at line 26; the exact added ex.Message.Length == 0 predicate skips the TerminateInstance refusal catch at RunPodHttpClient line 209 and reaches its generic return at 216. The TerminateInstance call is at test line 36. All five operation calls at 32-36 complete, and calls==5 at 38 passes before the common assertion. The actual output identifies exactly this mutated operation error. PollJobStatus_MapsCancelledState_ToFailed passes in the same red run.

### spec007-refusal-surfaces.json / s20-ashlarpeerbrickexecutor-catch-1

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_15/decoded/019-s20-ashlarpeerbrickexecutor-catch-1`. Baseline evidence: `batch_15/decoded/019-s20-ashlarpeerbrickexecutor-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Execution.AshlarPeerBrickExecutorGapCoverageTests.Peer_fallback_preserves_first_refusal_unless_an_alternative_succeeds(succeeds: False)`

Rationale:

> Accepted from the actual runtime fallback result failure: Peer_fallback_preserves_first_refusal_unless_an_alternative_succeeds(succeeds:false) fails at AshlarPeerBrickExecutorGapCoverageTests line 42 with peer-routing.all_peers_failed instead of peer.egress_refused. The exact empty-message condition skips the product refusal catch at 177 for the first peer HttpRequestException("wrapped", refusal), so the following HTTP catch classifies it as peer.http_request_failed. The fallback loop therefore cannot retain it as firstRefusal at 77/88; after the second peer returns 503, the final generic all-peers-failed result is returned. The preceding hosts==[peer-a, peer-b] assertion at 37 and IsFailure assertion at 41 pass. The succeeds:true control and other 22 tests pass.

### spec007-refusal-surfaces.json / s21-mcpclientconnectionmanager-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/000-s21-mcpclientconnectionmanager-catch-1`. Baseline evidence: `batch_16/decoded/000-s21-mcpclientconnectionmanager-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Mcp.Client.Tests.McpEgressTwinTests.Refused_connections_contribute_no_tools_and_share_a_disposable_warning_window`

Rationale:

> The sole actual failure is McpEgressTwinTests:41: logger.Warnings is empty instead of [7307]. The fixture overrides the client factory to throw IOException private-canary around the same refusal for two configured servers. Its preceding assertions confirm two connection attempts and no contributed tools. Adding an empty-message prerequisite to StartAsync's catch at McpClientConnectionManager:98 excludes that nonempty wrapper; source then enters the generic error-log path at 102–106 instead of the refusal warning window. The recording logger captures only Warning-level IDs, so absence of 7307 is the directly observed defect; generic Error output is source-explained rather than separately recorded by this logger. This is a diagnostic runtime detection with no extra red rows.

### spec007-refusal-surfaces.json / s22-mcpclientconnectionmanager-catch-2

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/001-s22-mcpclientconnectionmanager-catch-2`. Baseline evidence: `batch_16/decoded/000-s21-mcpclientconnectionmanager-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Mcp.Client.Tests.McpRoundTripTests.Local_refusal_during_drift_and_invocation_preserves_pins_and_the_typed_error`

Rationale:

> The sole actual failure is McpRoundTripTests:135: error should be the identical EgressRefusedException but is null. The fixture establishes a real MCP SDK session over in-memory pipes, captures a pinned tool, then makes RefusingWriteStream throw IOException private-canary with that refusal before each underlying write. The preceding refresh and same-tool pin assertions pass. Disabling InvokeProxiedToolAsync's refusal catch at McpClientConnectionManager:182 skips EgressRefusal.ThrowIfPresent; its generic catch at 192–197 returns a Failure ToolResult, so Record.ExceptionAsync observes no exception. This establishes loss of typed refusal propagation from invocation, rather than a timeout or connection setup failure. The later warning assertion is not reached; there are no additional red results.

### spec007-refusal-surfaces.json / s23-mcpclientconnectionmanager-catch-3

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/002-s23-mcpclientconnectionmanager-catch-3`. Baseline evidence: `batch_16/decoded/000-s21-mcpclientconnectionmanager-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Mcp.Client.Tests.McpRoundTripTests.Local_refusal_during_drift_and_invocation_preserves_pins_and_the_typed_error`

Rationale:

> The sole actual failure is McpRoundTripTests:136: logger.Warnings is [0,7307] rather than [7307]. The exact change bypasses RefreshOnceAsync's refusal catch at McpClientConnectionManager:213 for the injected nonempty stream-write wrapper. The generic catch at 222–226 retains the existing pins but emits event-0 drift-check warning; the later invocation still uses the unchanged refusal catch and emits the proper 7307 warning. Earlier assertions confirm the pinned tool is unchanged and the identical typed refusal propagates from invocation. Thus this mutation is detected solely in diagnostic classification/window sharing during drift refresh, while pin preservation and typed invocation refusal remain observed. No other actual failed result is present.

### spec007-refusal-surfaces.json / s24-toolboxmcptoolcontributor-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/003-s24-toolboxmcptoolcontributor-catch-1`. Baseline evidence: `batch_16/decoded/003-s24-toolboxmcptoolcontributor-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Mcp.Server.Tests.ToolboxMcpToolContributorTests.Egress_refusal_crosses_the_Mcp_boundary_as_fixed_text_only(wrapped: True)`
- `Ashlar.Mcp.Server.Tests.ToolboxMcpToolContributorTests.Egress_refusal_crosses_the_Mcp_boundary_as_fixed_text_only(wrapped: False)`

Rationale:

> Both actual failures at ToolboxMcpToolContributorTests:42 directly detect loss of fixed boundary text. The wrapped=True row returns Tool 'tool-canary' failed: One or more errors occurred. (unrelated-canary) (wrapper-canary), rather than egress refused by policy (ref ...). The wrapped=False row returns the generic tool failure prefix plus the detailed refusal containing site-canary, family, class and reference instead of the same fixed form. Both rows already passed outcome.IsError=true. The mutation disables the line-101 refusal catch for these nonempty messages and routes them to the generic line-115 formatting path. These exact actual output strings establish boundary-output redaction failures for both nested aggregate and direct refusal; all two red results are accounted for.

### spec007-refusal-surfaces.json / s25-inprocessagenttransport-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/004-s25-inprocessagenttransport-catch-1`. Baseline evidence: `batch_16/decoded/004-s25-inprocessagenttransport-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Orchestration.Transport.InProcessAgentTransportTests.SendAsync_maps_refusal_to_structured_failure_without_wrapper_details(wrapped: False)`
- `Ashlar.Tests.Orchestration.Transport.InProcessAgentTransportTests.SendAsync_maps_refusal_to_structured_failure_without_wrapper_details(wrapped: True)`

Rationale:

> Both direct and wrapped variants fail InProcessAgentTransportTests:28 with ErrorCode TRANSPORT_ERROR rather than EGRESS_REFUSED, after result.Success=false passes. The fixture registers a RefusingAgent whose OnExecuteAsync throws the chosen refusal or IOException private-canary wrapper. Adding the empty-message condition to the dedicated catch at InProcessAgentTransport:53 sends both cases into the generic catch at 105–123, which explicitly produces TRANSPORT_ERROR. This causally detects wrong structured failure classification in actual returned AgentResult objects. The remaining metadata, reference, message, output and call-count assertions follow the first failure and are not independent observations from these red rows. Both failed results are relevant runtime behavior.

### spec007-refusal-surfaces.json / s26-endpointhealthmonitor-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/005-s26-endpointhealthmonitor-catch-1`. Baseline evidence: `batch_16/decoded/005-s26-endpointhealthmonitor-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Orchestration.Routing.EndpointHealthMonitorTests.Refused_health_probe_is_unhealthy_without_a_degraded_warning(registryRefuses: True)`

Rationale:

> The sole failed row is EndpointHealthMonitorTests:58 with registryRefuses=True: expected one Warning event 7307 but observed zero. The complete mock log instead records event-0 Endpoint health probe failed ... after 1 failed probes, carrying the injected registry-wrapped refusal; its source frames are the one-shot UpdateHealth throw at test line 414 and ProbeAllAsync line 90. The earlier assertions confirm one unhealthy registry update and one channel-factory call. Bypassing the dedicated catch at EndpointHealthMonitor:117 selects the generic catch at 127–138, which performs the retrying unhealthy update, increments failure accounting and logs the ordinary failure warning. The registryRefuses=False control remains green because its typed health.Refusal value takes the unchanged branch at 92–96. This is a relevant runtime diagnostic-path detection, not a probe timeout or real remote-network failure.

### spec007-refusal-surfaces.json / s27-autonomyloopservice-catch-1

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/006-s27-autonomyloopservice-catch-1`. Baseline evidence: `batch_16/decoded/006-s27-autonomyloopservice-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.BackgroundAgents.Autonomy.AutonomyLoopServiceTests.Refused_store_sweeps_warn_once_and_the_timer_keeps_running`

Rationale:

> The sole actual failure is AutonomyLoopServiceTests:58: the count of messages containing Autonomy egress refused is zero instead of one. The mock store throws a wrapped refusal for its first two pending-list calls, then signals a continuation task on a later successful empty sweep. The test reaches this assertion only after that continuation completes and the service stops, so timer-loop survival after the refused store calls is observed. Adding the empty-message condition to ExecuteAsync's sweep catch at AutonomyLoopService:184 routes the nonempty wrappers to the generic cadence-continues warning at 188–192 instead of the refusal window configured at 129–132. The detection is missing typed refusal diagnostics while the timer continues; there are no extra failed rows or timeout-based acceptance.

### spec007-refusal-surfaces.json / s28-autonomyloopservice-catch-2

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/007-s28-autonomyloopservice-catch-2`. Baseline evidence: `batch_16/decoded/006-s27-autonomyloopservice-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.BackgroundAgents.Autonomy.AutonomyLoopServiceTests.Refused_proposal_is_counted_and_later_sweeps_remain_available`

Rationale:

> The sole actual failure is AutonomyLoopServiceTests:75 on the first sweep: result.Refused is zero instead of one. The RefusingProposer throws IOException wrapped around the refusal before attempted is incremented. The mutation bypasses SweepAsync's dedicated catch at AutonomyLoopService:267, whose separate failed++ and refused++ accounting is replaced by the generic catch's failed++ only; the returned SweepOutcome at 297 therefore lacks the refusal count. This directly detects incorrect refusal accounting rather than an arbitrary failure of the scripted harness. Later Failed, Attempted, second-sweep, proposer-call and warning/disposal assertions are not reached by this red test. No other row fails in the filter.

### spec007-refusal-surfaces.json / s29-warning-repeat-count-lost

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/008-s29-warning-repeat-count-lost`. Baseline evidence: `batch_16/decoded/008-s29-warning-repeat-count-lost/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalWarningTests.A_faulty_sink_does_not_escape_report_or_dispose`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalWarningTests.Repeats_are_summarized_on_expiry_and_a_new_window_can_start`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalWarningTests.Dispose_flushes_suppressed_counts_cancels_timers_and_is_idempotent`

Rationale:

> All three actual failures are caused by replacing existing.Suppressed++ with +=0 in EgressRefusalWarnings.Report. In actual TRX order, the faulty-sink fixture reaches EgressRefusalWarningTests:79 with one callback rather than two: the initial throwing callback is contained, but zero suppressed count prevents the expected disposal-summary callback. The expiry fixture reaches line 43 with the last write still (two,0,false), instead of (one,2,true), after initial writes, two timers and disposal of the fired timer passed. The dispose fixture reaches line 65 with only two initial (0,false) writes and no expected (1,true) summary after repeated disposal, reporting after disposal and firing all callbacks; its timers-disposed assertion already passed. The production expiry and dispose paths write summaries only for nonzero suppressed counts, explaining every additional red result. These tests instantiate the compiled internal class and execute it with a deterministic manual scheduler, so they are runtime diagnostic behavior assertions, not source conventions.

### spec007-refusal-surfaces.json / s30-warning-summary-expiry-lost

Reviewer: /root/audit_resume/review_producers_00_09. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/009-s30-warning-summary-expiry-lost`. Baseline evidence: `batch_16/decoded/008-s29-warning-repeat-count-lost/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalWarningTests.Repeats_are_summarized_on_expiry_and_a_new_window_can_start`

Rationale:

> The sole actual failure is EgressRefusalWarningTests:43: after the first manual timer fires, the last write remains (two,0,false) rather than the expected (one,2,true) summary. Initial report suppression, two timer creations and disposal of the expired timer have already passed their assertions. Changing Expire's nonzero condition at EgressRefusalWarnings:66 to suppressed<0 omits the summary for the observed two positive suppressed reports. The separate disposal and faulty-sink tests remain green because the mutation affects only expiry emission, not Dispose's condition. The relevant red row is a deterministic runtime callback/output comparison, not a five-minute wall-clock timeout or convention scan.

### spec007-refusal-surfaces.json / s31-warning-disposal-summary-lost

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/010-s31-warning-disposal-summary-lost`. Baseline evidence: `batch_16/decoded/008-s29-warning-repeat-count-lost/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalWarningTests.Dispose_flushes_suppressed_counts_cancels_timers_and_is_idempotent`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalWarningTests.A_faulty_sink_does_not_escape_report_or_dispose`

Rationale:

> Accepted from both actual runtime failures: changing Suppressed != 0 to Suppressed < 0 at EgressRefusalWarnings line 82 skips the disposal write for a positive suppressed count. Dispose_flushes_suppressed_counts_cancels_timers_and_is_idempotent fails EgressRefusalWarningTests line 65 because its actual writes contain the two initial (0,false) records but omit the (1,true) summary. Its timer-disposed assertion at 64 passes; repeated disposal, post-disposal reporting and manual callbacks have executed. A_faulty_sink_does_not_escape_report_or_dispose fails line 79 because the sink is called once instead of twice: the initial throwing sink was contained and explicit disposal returned, but the skipped summary never calls it again. The remaining two warning tests pass.

### spec007-refusal-surfaces.json / s32-aggregate-branches-lost

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/011-s32-aggregate-branches-lost`. Baseline evidence: `batch_16/decoded/011-s32-aggregate-branches-lost/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Unkeyed_DI_router_audits_refused_calls_and_stream_creation_or_iteration(stage: 0)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 1, auditThrows: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 4, auditThrows: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 4, auditThrows: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 2, auditThrows: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 1, auditThrows: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Echo_fallback_never_masks_a_direct_or_wrapped_refusal(deterministic: True, wrapped: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 0, auditThrows: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 2, auditThrows: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Network_classifier_checks_every_exception_branch_before_retrying`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Unkeyed_DI_router_audits_refused_calls_and_stream_creation_or_iteration(stage: 3)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Echo_fallback_never_masks_a_direct_or_wrapped_refusal(deterministic: False, wrapped: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 3, auditThrows: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Unkeyed_DI_router_audits_refused_calls_and_stream_creation_or_iteration(stage: 1)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 0, auditThrows: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 3, auditThrows: True)`

Rationale:

> Accepted from all 16 actual runtime failures. The exact loop mutation in EgressRefusal.Find visits only aggregate child0; the test wrapper puts an unrelated exception in child0 and nests the refusal in child1. Three unkeyed DI/router rows fail their first identity assertion at EgressRefusalPropagationTests line 49: stages0/1 retain HttpRequestException("wrapper-canary"), while stage3 ends with InvalidOperationException("dispose-canary"). All ten direct auditing rows (stages0-4 by auditThrows false/true) fail identity at 153. Seven retain the wrapper: stage0/false and both variants of stages1,2,4. Stage0/true instead exposes audit-canary because missing refusal detection selects ordinary fault emission at AuditingChatClient line 67, whose auditor throws. Both stage3 variants expose dispose-canary because activeRefusal remains unset and the disposal protection at line 168 cannot apply; in stage3/true disposal can also supersede the auditor exception. Two wrapped echo rows, deterministic true/false, fail identity at test113 with actual null because ThrowIfPresent misses the refusal and fallback returns. The classifier row fails124 because wrapped-refusal classification becomes true, after direct-refusal false at123 passes. All extra failures therefore follow from the omitted aggregate branch, including the changed audit/disposal exception identities. Five controls pass: both direct-refusal echo rows, both direct guard-refusal DI rows and reference formatting.

### spec007-refusal-surfaces.json / s33-reference-validation-lost

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/012-s33-reference-validation-lost`. Baseline evidence: `batch_16/decoded/012-s33-reference-validation-lost/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Remote_text_contains_only_fixed_text_and_a_canonical_random_reference`

Rationale:

> Accepted from the actual runtime formatting failure at EgressRefusalPropagationTests line 170. Replacing the canonical 16-lowercase-hex reference validation with reference is not null lets RemoteMessage retain arbitrary supplied text. The actual output is egress refused by policy (ref secret-site-label-sequence-canary), which fails the canonical-reference regex. Valid-reference passthrough at line 168 passes first. The exact failure text independently exposes the marker in the generated message; this is execution of the formatter through reflection, not a source-convention check.

### spec007-refusal-surfaces.json / s34-echo-refusal-1

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/013-s34-echo-refusal-1`. Baseline evidence: `batch_16/decoded/011-s32-aggregate-branches-lost/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Echo_fallback_never_masks_a_direct_or_wrapped_refusal(deterministic: True, wrapped: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Echo_fallback_never_masks_a_direct_or_wrapped_refusal(deterministic: True, wrapped: True)`

Rationale:

> Accepted from exactly two actual failures: deterministic:true with wrapped:false and wrapped:true both fail the first identity assertion at EgressRefusalPropagationTests line 113, reporting null instead of the original refusal. Removing ThrowIfPresent at HotSwappableModel line 70 lets the changed catch reach deterministic echo at72. Both nondeterministic variants and the rest of the 21-test suite pass, isolating the changed deterministic fallback path.

### spec007-refusal-surfaces.json / s35-echo-refusal-2

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/014-s35-echo-refusal-2`. Baseline evidence: `batch_16/decoded/011-s32-aggregate-branches-lost/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Echo_fallback_never_masks_a_direct_or_wrapped_refusal(deterministic: False, wrapped: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Echo_fallback_never_masks_a_direct_or_wrapped_refusal(deterministic: False, wrapped: True)`

Rationale:

> Accepted from exactly two actual failures: deterministic:false with wrapped:false and wrapped:true both fail the first identity assertion at EgressRefusalPropagationTests line 113, reporting null instead of the original refusal. Removing ThrowIfPresent at HotSwappableModel line 88 lets the catch reach fallback98 under the fixture ASHLAR_ALLOW_MOCK=1 setting. Both deterministic variants and the rest of the suite pass, isolating this ordinary provider fallback path.

### spec007-refusal-surfaces.json / s36-provider-first-replaced

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/015-s36-provider-first-replaced`. Baseline evidence: `batch_16/decoded/015-s36-provider-first-replaced/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Refused_attempt_keeps_first_refusal_unless_an_alternative_succeeds(vision: False, succeeds: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Refused_attempt_keeps_first_refusal_unless_an_alternative_succeeds(vision: True, succeeds: False)`

Rationale:

> Accepted from both actual identity failures at EgressProviderFallbackTests line 98: the unsuccessful LLM and vision fallback rows receive a different, later EgressRefusedException instance instead of the first refusal object. Replacing CompareExchange(ref scope._first, refusal, null) with Exchange at ProviderRefusalScope line 22 overwrites the retained first refusal. The actual error paths surface the later refusal through scope30 and adaptive LLM87/vision130. Both successful-alternative variants pass as controls; the red failure is exact object-identity preservation, not just exception type.

### spec007-refusal-surfaces.json / s37-provider-refusal-lost

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/016-s37-provider-refusal-lost`. Baseline evidence: `batch_16/decoded/015-s36-provider-first-replaced/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Refusal_state_does_not_escape_into_a_concurrent_invocation`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Real_Ollama_health_refusal_survives_provider_availability_conversion(wrapped: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Real_Ollama_health_refusal_survives_provider_availability_conversion(wrapped: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Refused_attempt_keeps_first_refusal_unless_an_alternative_succeeds(vision: True, succeeds: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Refused_availability_probe_is_retained_without_blocking_an_available_provider(alternative: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Refused_attempt_keeps_first_refusal_unless_an_alternative_succeeds(vision: False, succeeds: False)`

Rationale:

> Accepted from all six actual failures caused by adding refusal.Message.Length == 0 to ProviderRefusalScope.ThrowIfRefused at line 30. Each nonempty retained refusal is suppressed and replaced by ModelUnavailableException. In actual TRX order: concurrent invocation fails EgressProviderFallbackTests line148 on the first invocation identity, with ordinary-failure IOException inside the generic exception; wrapped and direct Ollama-health rows fail69 on generic unavailability from adaptive59; unsuccessful vision fallback fails98 on No vision model available from adaptive131, with a later refusal inside; availability probe without an alternative fails121 on generic unavailability from adaptive59 after its IsProviderAvailable("ollama") Times.Once verification119 passes; unsuccessful LLM fallback fails98 on generic unavailability from adaptive88 with a later refusal inside. The concurrency synchronization wait145, second invocation146 and release147 complete before failure148. Six controls pass: both chat variants, both successful-alternative fallbacks, availability with an alternative, and multiframe vision. Direct chat and multiframe paths preserve their refusal before the scope fallback and are controls, not extra failures.

### spec007-refusal-surfaces.json / s38-ollama-health-refusal-not-retained

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/017-s38-ollama-health-refusal-not-retained`. Baseline evidence: `batch_16/decoded/017-s38-ollama-health-refusal-not-retained/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Real_Ollama_health_refusal_survives_provider_availability_conversion(wrapped: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Real_Ollama_health_refusal_survives_provider_availability_conversion(wrapped: False)`

Rationale:

> Accepted from both actual health identity failures, wrapped:true and wrapped:false, at EgressProviderFallbackTests line 69. Replacing ProviderRefusalScope.Record(refusal) with a discard at ProviderFactory line 810 permits the health false-return without retaining the discovered refusal. Both runs receive ModelUnavailableException from AdaptiveProviderFactory line 59 instead of the original refusal instance. This directly exposes the missing handoff from health availability conversion into the invocation refusal scope.

### spec007-refusal-surfaces.json / s39-ollama-chat-refusal-consumed

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/018-s39-ollama-chat-refusal-consumed`. Baseline evidence: `batch_16/decoded/018-s39-ollama-chat-refusal-consumed/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressProviderFallbackTests.Real_Ollama_chat_preserves_refusal_instead_of_an_unreachable_result(wrapped: True)`

Rationale:

> Accepted from the sole wrapped:true chat failure at EgressProviderFallbackTests line 39: the captured error is null rather than the original EgressRefusedException. Adding the empty-message condition to the OllamaProvider refusal catch at line284 bypasses it for the HttpRequestException("private-canary", refusal) wrapper. Source shows the following HttpRequestException catch at295 returns OLLAMA_UNREACHABLE, but the actual test does not capture or assert that result; the observed kill is that the expected typed refusal does not escape. RefreshModelsAsync success at test37 passes first, and the unwrapped-refusal variant passes as a control.

### spec007-refusal-surfaces.json / s40-network-refusal-retried

Reviewer: codex/audit_resume/review_producers_10_19. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_16/decoded/019-s40-network-refusal-retried`. Baseline evidence: `batch_16/decoded/019-s40-network-refusal-retried/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Network_classifier_checks_every_exception_branch_before_retrying`

Rationale:

> Accepted from the actual boolean assertion at EgressRefusalPropagationTests line124: TransientClassifiers.Network(Wrap(refusal)) returns true instead of false. The exact mutation replaces the EgressRefusal.Find(exception) is null exclusion with a mere nonnull check. Wrap constructs an HttpRequestException containing an aggregate with the refusal in its second branch, so the mutant accepts the outer HttpRequestException as transient without consulting the refusal tree. The preceding direct-refusal classification at123 still returns false because that exception is not a listed transient type. The failed wrapped-refusal assertion directly detects the missing exclusion.

### spec007-refusal-surfaces.json / s41-workflow-first-refusal-lost-1

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/000-s41-workflow-first-refusal-lost-1`. Baseline evidence: `batch_17/decoded/000-s41-workflow-first-refusal-lost-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Application.WorkflowExecutorEdgeCaseTests.Fallback_preserves_first_refusal_unless_an_alternative_succeeds(clusterNode: False, succeeds: False)`

Rationale:

> The mutated first-refusal throw in WorkflowExecutor.cs:325 is skipped for the nonempty original refusal. In the actual clusterNode=False, succeeds=False row, WorkflowExecutorEdgeCaseTests.cs:53 expected that same refusal object but observed InvalidOperationException('later ordinary failure') from product line326. The fixture first throws a wrapped refusal and then an ordinary failure from its alternative; the changed throw therefore loses the preserved refusal when fallback is exhausted. The other three theory rows pass, and restoration returns all four rows to passing.

### spec007-refusal-surfaces.json / s42-workflow-first-refusal-lost-2

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/001-s42-workflow-first-refusal-lost-2`. Baseline evidence: `batch_17/decoded/000-s41-workflow-first-refusal-lost-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Application.WorkflowExecutorEdgeCaseTests.Fallback_preserves_first_refusal_unless_an_alternative_succeeds(clusterNode: True, succeeds: False)`

Rationale:

> The mutation skips the cluster fallback's preserved-refusal throw at WorkflowExecutor.cs:407. The actual clusterNode=True, succeeds=False theory fails the identity assertion at WorkflowExecutorEdgeCaseTests.cs:53: expected the original refusal, observed InvalidOperationException('DomainBrick execution failed: fallback-brick') from product409. This is the source's Abort failure path reached after the preserved refusal is suppressed. The other three rows pass and restoration returns all four to passing.

### spec007-refusal-surfaces.json / s43-toolcallingagent-refusal-1

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/002-s43-toolcallingagent-refusal-1`. Baseline evidence: `batch_17/decoded/002-s43-toolcallingagent-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.BackgroundAgents.Agents.ToolCallingAgentReActTests.Model_refusal_is_recorded_and_stops_the_cycle_but_Think_rethrows_it`

Rationale:

> The altered ThinkAsync refusal catch at ToolCallingAgent.cs:117 rejects the synthetic aggregate's nonempty message and reaches the generic catch129, whose source returns no actions. In the actual Model_refusal_is_recorded_and_stops_the_cycle_but_Think_rethrows_it row, the cycle assertions at ToolCallingAgentReActTests.cs:86-88 pass (egress-refused stop reason, one refusal, no executed tools). The separate ThinkAsync invocation89 then produces no exception; identity assertion90 expected the original refusal and observed null. Thus the observed kill concerns failure to propagate the refusal from ThinkAsync, rather than failure of the preceding cycle.

### spec007-refusal-surfaces.json / s44-toolcallingagent-refusal-2

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/003-s44-toolcallingagent-refusal-2`. Baseline evidence: `batch_17/decoded/002-s43-toolcallingagent-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.BackgroundAgents.Agents.ToolCallingAgentReActTests.Egress_refusal_cap_stops_both_loops_without_signing_or_advancing(chain: True)`
- `Ashlar.Tests.BackgroundAgents.Agents.ToolCallingAgentReActTests.Egress_refusal_cap_stops_both_loops_without_signing_or_advancing(chain: False)`
- `Ashlar.Tests.BackgroundAgents.Agents.ToolCallingAgentReActTests.Below_the_cap_the_model_can_choose_an_allowed_tool`

Rationale:

> The per-tool refusal catch at ToolCallingAgent.cs:248 is bypassed for the tool's nonempty HttpRequestException('wrapper', refusal). The unchanged outer refusal catch292 counts one refusal and ends the cycle. Both actual cap-theory variants pass the stop-reason assertion44 and fail ToolCallingAgentReActTests.cs:45 with one refusal instead of three. The actual below-cap row fails at71 with egress_refused instead of empty. These three failures directly establish altered refusal accounting and premature termination under the mutation, with all 19 selected rows restored to passing afterward.

### spec007-refusal-surfaces.json / s45-toolcallingagent-refusal-3

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/004-s45-toolcallingagent-refusal-3`. Baseline evidence: `batch_17/decoded/002-s43-toolcallingagent-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.BackgroundAgents.Agents.ToolCallingAgentReActTests.Model_refusal_is_recorded_and_stops_the_cycle_but_Think_rethrows_it`

Rationale:

> The mutation skips the outer cycle refusal catch at ToolCallingAgent.cs:292 for the scripted aggregate refusal and reaches generic error handling315-319. The actual Model_refusal_is_recorded_and_stops_the_cycle_but_Think_rethrows_it row fails its first assertion after RunCycleAsync, ToolCallingAgentReActTests.cs:86: error was returned instead of egress_refused. This directly binds the mutation to incorrect cycle refusal classification; restoration returns all 19 rows to passing.

### spec007-refusal-surfaces.json / s46-auditingchatclient-refusal-1

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/005-s46-auditingchatclient-refusal-1`. Baseline evidence: `batch_17/decoded/005-s46-auditingchatclient-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 0, auditThrows: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 0, auditThrows: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Unkeyed_DI_router_audits_refused_calls_and_stream_creation_or_iteration(stage: 0)`

Rationale:

> The mutation bypasses the nonstream refusal catch at AuditingChatClient.cs:45. All three actual failures occur at the first refusal-identity assertion: EgressRefusalPropagationTests.cs:153 for stage0 auditThrows=False/True and test49 for the unkeyed DI stage0 row. Without an audit exception, the observed error is the original HttpRequestException wrapper-canary containing the refusal, rather than the refusal object itself. With auditThrows=True, the observed error is audit-canary from Audit.Record187 through Emit216 and generic fault emission67: ordinary auditing masks the pending refusal. The DI row similarly observes the wrapper through the sanitizer/router/auditing composition. The caught-refusal path's protected EmitRefusal and ThrowIfPresent are skipped. Restoration passes all 21 rows.

### spec007-refusal-surfaces.json / s47-auditingchatclient-refusal-2

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/006-s47-auditingchatclient-refusal-2`. Baseline evidence: `batch_17/decoded/005-s46-auditingchatclient-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 1, auditThrows: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 2, auditThrows: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 1, auditThrows: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 2, auditThrows: False)`

Rationale:

> The mutation bypasses stream-creation refusal handling at AuditingChatClient.cs:92. Four actual main-theory rows (stage1 False, stage2 True, stage1 True, stage2 False in TRX order) fail identity assertion EgressRefusalPropagationTests.cs:153, observing HttpRequestException wrapper-canary instead of the embedded refusal. Actual stacks identify RefusingClient.GetStreamingResponseAsync199 for stage1 and GetAsyncEnumerator201 for stage2, followed by product89/181. The nonempty wrapper fails the new condition, so activeRefusal assignment94, EmitRefusal96 and ThrowIfPresent97 are skipped. Enumerator assignment does not complete; the finally has no enumerator to dispose. Both audit settings consequently expose the wrapper along these fixture paths; all 21 rows pass after restoration.

### spec007-refusal-surfaces.json / s48-auditingchatclient-refusal-3

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/007-s48-auditingchatclient-refusal-3`. Baseline evidence: `batch_17/decoded/005-s46-auditingchatclient-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 3, auditThrows: True)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Unkeyed_DI_router_audits_refused_calls_and_stream_creation_or_iteration(stage: 3)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 3, auditThrows: False)`

Rationale:

> The mutation bypasses iteration refusal handling at AuditingChatClient.cs:108, leaving activeRefusal null. The actual stage3 auditThrows=True, unkeyed DI stage3, and stage3 auditThrows=False rows each observe InvalidOperationException dispose-canary instead of the original refusal at their first identity assertions (main test153, DI49). Actual stacks include RefusingClient.DisposeAsync208 and product disposal166; DI propagation also crosses sanitizer/router/outer auditing. Source analysis shows generic fault handling128-132 followed by finally disposal, where activeRefusal suppression168 is disabled and the ordinary disposal error is not a refusal for catch172. Disposal therefore replaces the pending wrapper or audit-path error. The recorded final error is dispose-canary in all three rows; restoration passes all 21 selected rows.

### spec007-refusal-surfaces.json / s49-auditingchatclient-refusal-4

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/008-s49-auditingchatclient-refusal-4`. Baseline evidence: `batch_17/decoded/005-s46-auditingchatclient-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 4, auditThrows: False)`
- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Auditing_preserves_refusal_from_call_stream_creation_iteration_and_disposal(stage: 4, auditThrows: True)`

Rationale:

> The mutation bypasses the disposal refusal catch at AuditingChatClient.cs:172. The two actual stage4 main-theory rows, auditThrows=False and True, both fail EgressRefusalPropagationTests.cs:153: the observed error is HttpRequestException wrapper-canary instead of the embedded refusal object. Actual stacks place the throw in RefusingClient.DisposeAsync206 reached from product166. In source, stage4 MoveNext returns false, the loop marks completed, then finally disposal throws with activeRefusal still null. The altered catch skips protected EmitRefusal and ThrowIfPresent; success auditing after the finally is also unreached. All 21 selected rows pass after restoration.

### spec007-refusal-surfaces.json / s50-a2aagenttransport-refusal-1

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/009-s50-a2aagenttransport-refusal-1`. Baseline evidence: `batch_17/decoded/009-s50-a2aagenttransport-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Transport.A2A.Tests.A2AAgentTransportTests.Refusals_are_classified_before_retrying_the_same_destination(wrapped: False)`
- `Ashlar.Transport.A2A.Tests.A2AAgentTransportTests.Refusals_are_classified_before_retrying_the_same_destination(wrapped: True)`

Rationale:

> The altered A2AAgentTransport.cs:99 refusal catch rejects the nonempty direct or wrapped refusal. The actual wrapped=False row fails before any assertion: the original EgressRefusedException escapes transport.SendAsync at test27, with a stack through HttpClient/A2AClient and product96. The wrapped=True row reaches A2AAgentTransportTests.cs:29 and reports four handler calls instead of one. Source retry handling115-123 retries the HttpRequestException three times under MaxRetries=3, then the ordinary transport-failure path125-128 returns. These are distinct relevant runtime regressions: escaped direct refusal and retrying the same destination after a wrapped refusal. Restored code passes all ten selected tests.

### spec007-refusal-surfaces.json / s51-a2aagenttransport-refusal-2

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/010-s51-a2aagenttransport-refusal-2`. Baseline evidence: `batch_17/decoded/009-s50-a2aagenttransport-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Transport.A2A.Tests.A2AAgentTransportTests.Refusals_are_classified_before_retrying_the_same_destination(wrapped: False)`
- `Ashlar.Transport.A2A.Tests.A2AAgentTransportTests.Refusals_are_classified_before_retrying_the_same_destination(wrapped: True)`

Rationale:

> Accepted from both actual wrapped/unwrapped health-message failures. Adding ex.Message.Length == 0 to the endpoint-health refusal catch in A2AAgentTransport.cs159 skips typed refusal conversion for these nonempty exceptions. CheckEndpointAsync falls through to its generic error response: the actual unwrapped health message includes the detailed EgressRefusedException text and the wrapped variant says Agent card fetch failed: HTTP request failed, instead of the fixed refusal text and reference. Both fail A2AAgentTransportTests.cs35. The preceding SendAsync checks at29–32 (one handler call, unsuccessful result, typed refusal code and reference metadata) and unhealthy health flag at34 pass; the mutated catch is the separate health path. The remaining eight rows pass.

### spec007-refusal-surfaces.json / s52-agenttransportserviceimpl-refusal-1

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/011-s52-agenttransportserviceimpl-refusal-1`. Baseline evidence: `batch_17/decoded/011-s52-agenttransportserviceimpl-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Egress_refusal_round_trip_clears_output_and_preserves_only_code_and_random_ref(form: 0)`

Rationale:

> Accepted from the actual form0 gRPC round-trip failure: the new nonempty-message gate on AgentTransportServiceImpl.cs51 suppresses the typed-refusal exception catch. The injected transport throws an AggregateException containing an unrelated IOException and an RpcException whose inner exception is the refusal. The raw InvokeAsync at GrpcAgentTransportTests.cs85 now throws RpcException StatusCode Unknown with Detail Exception was thrown by handler, instead of producing a refused response. This is causally the missing server catch, not an unrelated runner or network outage: the scoped baseline and restored run pass, while the other nine red rows, including returned-result forms1/2, pass.

### spec007-refusal-surfaces.json / s53-ashlara2aagenthandler-refusal-1

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/012-s53-ashlara2aagenthandler-refusal-1`. Baseline evidence: `batch_17/decoded/012-s53-ashlara2aagenthandler-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Transport.A2A.Server.Tests.A2AServerRoundTripTests.Egress_refusal_round_trip_preserves_code_and_reference_without_raw_details(form: 0)`

Rationale:

> Accepted from the actual form0 A2A result classification failure. Adding the nonempty-message gate to AshlarA2AAgentHandler.cs96 skips the typed-refusal catch for the injected AggregateException/HttpRequestException chain; the generic exception path at110–118 emits an ordinary failed task instead of RefusalMessage. A2AServerRoundTripTests.cs110 has already confirmed result.Success is false, then line111 observes a2a.task.failed instead of a2a.egress_refused. The other seven rows, including returned-result forms1/2, pass, and the baseline and restored runs pass all eight.

### spec007-refusal-surfaces.json / s54-agent-cap-raised

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/013-s54-agent-cap-raised`. Baseline evidence: `batch_17/decoded/013-s54-agent-cap-raised/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.BackgroundAgents.Agents.ToolCallingAgentReActTests.Egress_refusal_cap_stops_both_loops_without_signing_or_advancing(chain: True)`
- `Ashlar.Tests.BackgroundAgents.Agents.ToolCallingAgentReActTests.Egress_refusal_cap_stops_both_loops_without_signing_or_advancing(chain: False)`

Rationale:

> Accepted from both actual chain true/false failures: changing the outer ToolCallingAgent.cs289 break threshold from three refusals to four allows another scripted iteration after the unchanged inner cap has set StoppedReason to egress_refused. Both ToolCallingAgentReActTests variants pass StoppedReason at44, then fail EgressRefusals at45 with actual4 versus expected3. The scripted model presents four refusal-bearing turns and RefusingTool throws a wrapped refusal; the mutant count directly demonstrates the extra refusal. Baseline and restored runs pass both rows.

### spec007-refusal-surfaces.json / s55-inner-exception-edge-lost

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/014-s55-inner-exception-edge-lost`. Baseline evidence: `batch_17/decoded/011-s52-agenttransportserviceimpl-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Egress_refusal_round_trip_clears_output_and_preserves_only_code_and_random_ref(form: 0)`
- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Local_transport_refusals_are_not_reported_as_rpc_outages(wrapped: True)`

Rationale:

> Accepted from both actual failures caused by losing the ordinary InnerException edge in EgressRefusal.Find at40 when the wrapper message is nonempty. The local wrapped RpcException variant first establishes the SDK DebugException/InnerException identity at GrpcAgentTransportTests.cs39–40 and an unsuccessful result at47, then observes TRANSPORT_UNAVAILABLE rather than EGRESS_REFUSED at48. The form0 server round trip also fails at raw InvokeAsync85 with RpcException Unknown because traversal through the aggregate reaches the RpcException but cannot follow its inner refusal, so server refusal conversion is missed. The direct-refusal and returned-result control rows remain among the eight passing tests.

### spec007-refusal-surfaces.json / s56-orchestrator-refusal-counts-as-breaker-failure

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/015-s56-orchestrator-refusal-counts-as-breaker-failure`. Baseline evidence: `batch_17/decoded/015-s56-orchestrator-refusal-counts-as-breaker-failure/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Orchestration.Coordination.OrchestratorTransportTests.Egress_refusals_never_enter_the_circuit_breaker_or_retry(code: "a2a.egress_refused", metadataOnly: True)`
- `Ashlar.Tests.Orchestration.Coordination.OrchestratorTransportTests.Egress_refusals_never_enter_the_circuit_breaker_or_retry(code: "a2a.egress_refused", metadataOnly: False)`
- `Ashlar.Tests.Orchestration.Coordination.OrchestratorTransportTests.Egress_refusals_never_enter_the_circuit_breaker_or_retry(code: "EGRESS_REFUSED", metadataOnly: True)`
- `Ashlar.Tests.Orchestration.Coordination.OrchestratorTransportTests.Egress_refusals_never_enter_the_circuit_breaker_or_retry(code: "EGRESS_REFUSED", metadataOnly: False)`

Rationale:

> Accepted from all four actual direct-code/metadata-only and EGRESS_REFUSED/a2a.egress_refused failures. Adding initialResult.Success to Orchestrator.cs583 makes refusal recognition contradictory because EgressRefusal.IsRefused requires an unsuccessful result; the early refusal return is lost and execution enters the circuit-breaker path. Every OrchestratorTransportTests theory row completes four outer calls and unsuccessful-result assertions at42, then fails VerifyNoOtherCalls at44 with exactly four unverified ICircuitBreaker.ExecuteAsync invocations. The other eleven scoped tests pass; baseline and restored runs pass all fifteen.

### spec007-refusal-surfaces.json / s57-warning-reasons-collapsed

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/016-s57-warning-reasons-collapsed`. Baseline evidence: `batch_17/decoded/016-s57-warning-reasons-collapsed/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalWarningTests.The_same_site_with_different_reasons_has_independent_windows`

Rationale:

> Accepted from the actual independent-warning-window failure. Replacing refusal.Reason with default in EgressRefusalWarnings.cs21 merges different reasons for the same site into one window. EgressRefusalWarningTests.cs19 first confirms the two reasons differ; both reports at23–24 execute. The line25 collection assertion then sees only the first refusal, while the expected sequence contains first and second. The remaining three warning rows pass, and baseline/restored runs pass all four.

### spec007-refusal-surfaces.json / s58-remote-format-leaks-extra-text

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/017-s58-remote-format-leaks-extra-text`. Baseline evidence: `batch_17/decoded/017-s58-remote-format-leaks-extra-text/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Infrastructure.Tests.Certification.EgressRefusalPropagationTests.Remote_text_contains_only_fixed_text_and_a_canonical_random_reference`

Rationale:

> Accepted from the actual formatting-helper assertion. Adding the literal private-canary to EgressRefusal.RemoteMessage at53 causes the reflected helper call with a valid refusal reference to return the fixed refusal string plus that suffix. EgressRefusalPropagationTests.cs168 records the exact expected fixed text and reference versus the actual extra text. Baseline and restored runs pass the single selected test.

### spec007-refusal-surfaces.json / s59-agenttransportserviceimpl-result-conversion-lost

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/018-s59-agenttransportserviceimpl-result-conversion-lost`. Baseline evidence: `batch_17/decoded/011-s52-agenttransportserviceimpl-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Egress_refusal_round_trip_clears_output_and_preserves_only_code_and_random_ref(form: 1)`
- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Egress_refusal_round_trip_clears_output_and_preserves_only_code_and_random_ref(form: 2)`

Rationale:

> Accepted from both actual returned-refusal form1/form2 output failures. Adding result.Success to AgentTransportServiceImpl.cs187 disables refusal conversion because IsRefused requires !Success. The ordinary SerializeOutput/copy path therefore emits the synthetic output payload. The raw responses pass Success false at GrpcAgentTransportTests.cs86, then fail the empty Output assertion at87: each contains result with {"secret":"output-canary"}. Form0 typed-exception handling and seven other red rows pass; baseline/restored runs pass all ten.

### spec007-refusal-surfaces.json / s60-ashlara2aagenthandler-result-conversion-lost

Reviewer: /root/audit_finish/review17_last. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_17/decoded/019-s60-ashlara2aagenthandler-result-conversion-lost`. Baseline evidence: `batch_17/decoded/012-s53-ashlara2aagenthandler-refusal-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Transport.A2A.Server.Tests.A2AServerRoundTripTests.Egress_refusal_round_trip_preserves_code_and_reference_without_raw_details(form: 1)`
- `Ashlar.Transport.A2A.Server.Tests.A2AServerRoundTripTests.Egress_refusal_round_trip_preserves_code_and_reference_without_raw_details(form: 2)`

Rationale:

> Accepted from both actual form1/form2 returned-result classification failures. Adding result.Success to AshlarA2AAgentHandler.cs74 disables IsRefused conversion for unsuccessful results; the ordinary failed-task branch runs instead of RefusalMessage. Both A2AServerRoundTripTests rows pass Success false at110 and then observe a2a.task.failed instead of a2a.egress_refused at111. The typed-exception form0 and five other scoped rows pass, and baseline/restored runs pass all eight.

### spec007-refusal-surfaces.json / s61-grpc-client-code-ignored

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_18/decoded/000-s61-grpc-client-code-ignored`. Baseline evidence: `batch_18/decoded/000-s61-grpc-client-code-ignored/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Egress_refusal_round_trip_clears_output_and_preserves_only_code_and_random_ref(form: 0)`
- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Egress_refusal_round_trip_clears_output_and_preserves_only_code_and_random_ref(form: 2)`
- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Egress_refusal_round_trip_clears_output_and_preserves_only_code_and_random_ref(form: 1)`

Rationale:

> Inverting the gRPC response refusal-code condition at GrpcAgentTransport.cs:69 produces three actual failures (forms0,2,1 in TRX order). Each fails at GrpcAgentTransportTests.cs:95 with KeyNotFoundException for egressRef. The raw-wire assertions86-90 have already passed: unsuccessful response, empty output, refusal code/reference and sanitized message. Client result success/code assertions93-94 also pass. The mutation bypasses refusal conversion and takes ordinary metadata construction at product274-284, which retains errorCode but omits egressRef. Thus the observed kill specifically establishes lost client refusal-reference metadata. Restoration passes all ten selected rows.

### spec007-refusal-surfaces.json / s62-a2a-client-code-ignored

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_18/decoded/001-s62-a2a-client-code-ignored`. Baseline evidence: `batch_18/decoded/001-s62-a2a-client-code-ignored/baseline`.

Actual reviewed red failures:

- `Ashlar.Transport.A2A.Server.Tests.A2AServerRoundTripTests.Egress_refusal_round_trip_preserves_code_and_reference_without_raw_details(form: 0)`
- `Ashlar.Transport.A2A.Server.Tests.A2AServerRoundTripTests.Egress_refusal_round_trip_preserves_code_and_reference_without_raw_details(form: 1)`
- `Ashlar.Transport.A2A.Server.Tests.A2AServerRoundTripTests.Egress_refusal_round_trip_preserves_code_and_reference_without_raw_details(form: 2)`

Rationale:

> Changing the A2A mapper's refusal predicate at A2AAgentTransport.cs:104 to require TaskState.Completed prevents recognition of the server's failed refusal task. Three actual forms0,1,2 fail A2AServerRoundTripTests.cs:111: expected a2a.egress_refused, observed a2a.task.failed. The unsuccessful-result check110 passes. Source follows ordinary failure mapping124 after the refusal branch is skipped. Restoration passes all eight selected rows.

### spec007-refusal-surfaces.json / s63-health-refusal-becomes-degraded

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_18/decoded/002-s63-health-refusal-becomes-degraded`. Baseline evidence: `batch_18/decoded/002-s63-health-refusal-becomes-degraded/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Orchestration.Routing.EndpointHealthMonitorTests.Refused_health_probe_is_unhealthy_without_a_degraded_warning(registryRefuses: False)`

Rationale:

> Adding health.IsHealthy to EndpointHealthMonitor.cs:92 prevents refusal-warning handling for an unhealthy refused health result. The actual registryRefuses=False row fails EndpointHealthMonitorTests.cs:58 because the expected event7307 warning occurs zero times. The full Moq failure independently records an event0 degraded warning containing the refused-health message, consistent with the alternate product99-109 branch. Before this failure, assertions56-57 pass for one unhealthy registry update and one channel-factory call. The registryRefuses=True variant remains passing via a separate exception catch. Restoration passes all nine selected rows.

### spec007-refusal-surfaces.json / s64-grpc-local-catch-1

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_18/decoded/003-s64-grpc-local-catch-1`. Baseline evidence: `batch_18/decoded/003-s64-grpc-local-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Local_transport_refusals_are_not_reported_as_rpc_outages(wrapped: False)`
- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Local_transport_refusals_are_not_reported_as_rpc_outages(wrapped: True)`

Rationale:

> Adding the nonempty-message exclusion to GrpcAgentTransport.cs:88 skips local invocation refusal handling. Both actual rows first fail GrpcAgentTransportTests.cs:48: direct refusal maps to TRANSPORT_ERROR via generic catch109, while the injected wrapped RpcException(Unavailable) maps to TRANSPORT_UNAVAILABLE via catch95/status mapping316, instead of EGRESS_REFUSED. The unsuccessful-result assertion47 passes; wrapped dependency-chain checks39-40 also pass. Restoration passes both selected rows.

### spec007-refusal-surfaces.json / s65-grpc-local-catch-2

Reviewer: /root/audit_finish/review17_first. Evidence kind: `runtime-assertion`. Accepted: **yes**.

Per-case evidence directory: `batch_18/decoded/004-s65-grpc-local-catch-2`. Baseline evidence: `batch_18/decoded/003-s64-grpc-local-catch-1/baseline`.

Actual reviewed red failures:

- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Local_transport_refusals_are_not_reported_as_rpc_outages(wrapped: False)`
- `Ashlar.Tests.Transport.GrpcAgentTransportTests.Local_transport_refusals_are_not_reported_as_rpc_outages(wrapped: True)`

Rationale:

> Bypassing local health refusal catch177 in GrpcAgentTransport.cs lets the generic health catch181-188 return ex.Message. Both actual rows fail GrpcAgentTransportTests.cs:55, which expects only the sanitized 'egress refused by policy (ref ...)' message. The direct row exposes the full local refusal message including site grpc.client, family and class; the wrapped row exposes StatusCode Unavailable, Detail private-canary and the embedded refusal. Invocation checks47-52 have already passed, as has the unhealthy-state assertion54. This is a causally relevant runtime health-message sanitization regression; restoration passes both rows.

## Nonpublishing clean-package trust rehearsal

[Run 43314654903, attempt 1](https://github.com/IanFrelinger/Ashlar/actions/runs/43314654903) completed successfully. The independent artifact audit returned `VERIFIED` after checking the original [artifact 13987163804](https://github.com/IanFrelinger/Ashlar/actions/runs/43314654903/artifacts/13987163804), all 90 extracted files, the executing [controller commit](https://github.com/IanFrelinger/Ashlar/commit/c7c896c40c927551c41caa17486a6dae89573993), and the reviewed [workflow](https://github.com/IanFrelinger/Ashlar/blob/c7c896c40c927551c41caa17486a6dae89573993/.github/workflows/spec007-package-rehearsal-20261112.yml) and [controller files](https://github.com/IanFrelinger/Ashlar/tree/c7c896c40c927551c41caa17486a6dae89573993/ci-package-rehearsal). This is actual execution evidence; preparation fixtures are excluded.

The source was `792446d09b59f98b47e08279907cb3d4df314caa` throughout. The package run started at `2026-11-12T21:53:24Z` after the mutation campaign had completed and passed its 295-case/65-review prerequisite gate. Run metadata was last updated at `2026-11-12T21:57:05Z` with conclusion `success`. The package workflow shared the campaign's serial concurrency group with cancellation disabled.

The unique local package version was `0.0.0-rehearsal.792446d09b59.43314654903.1`. The artifact retains **24 `.nupkg` packages (220,048,971 bytes)** and **6 `.snupkg` symbol packages (908,208 bytes)**, their manifest and SHA-256 sidecars. The exact pinned 24-package build recipe, NuSpec IDs/versions, raw and content digests, and unchanged before/after consumption snapshots all passed validation. The manifest SHA-256 is `02916326a4a102a22b13b367b964767dab74cc2ebf45b5da1feb7d9a046994b7`.

The original ZIP is 222,128,689 bytes, with SHA-256 `52e42e9b02b4517be7e318a9532b93ba02febc56ac88cf47a6572bc9cdedd33a`. Packages remained in the local feed and retained artifact; no package publication, deployment, tag, or release-workflow invocation was performed.

### Produced package bytes

Every row uses the unique version above. Raw SHA-256 covers the entire `.nupkg`; content SHA-256 uses the pinned repository's length-prefixed entry-content digest.

| Package ID | Bytes | Raw SHA-256 | Content SHA-256 |
| --- | ---: | --- | --- |
| `Ashlar.AI.Pipeline` | 182,235 | `aa140dcc51097bf09708779b5f48c9fdcb3e1adbb6c88da88c36add5395628b6` | `e86b9bc35cfd2f039c2595823b9dca239ee38bf49c1510e43ebdf2e284988276` |
| `Ashlar.Abstractions` | 351,335 | `662838dbade0f0b42219d10964bd5fc1cf7f0af4c14edfd96aa98d9e24f678ec` | `251748e92c2148e231671cbd45966645a3a374593d52e91f285bb760494a56de` |
| `Ashlar.Analyzers` | 48,845 | `9f24879e5573fe7f6b9a3935bd8969b90b74d9102dddf740b5e1cd9220ba6bed` | `fcf5eb498fa9bf91e72b1d0faaf948834f08a426a61af4bf99a638a39e3db958` |
| `Ashlar.Authoring` | 33,060 | `f4b896fe83fcf01affc4ce0202cd8d2472de98420f8081b14d9ce92bc2de6fbe` | `980027426c800d67dba505fb2158f1faf6bfc732be7351931f657bfe9e93a6f2` |
| `Ashlar.BackgroundAgents` | 299,017 | `22ab5c171cbe8890eb7e7df0324ec4fd0ad23a59a3035f44dc72ba20f3b26539` | `0051ec23ce8d31f186a59ce827d3ddd234b586533271032c7c55f805dbe70efd` |
| `Ashlar.Brick.Contracts` | 143,630 | `27eb05ae6e942fa9f5c1444e65fdab635ca0c6b48f65c664482527b6c798ac55` | `b5bd433aae2fd04a195581de647498f05a090dd65473521d8e48ecf140c61302` |
| `Ashlar.Bricks.GitHub` | 174,438 | `8a68ffb115438409137e4c886168c7a6ec682ddcad51dcfd2a62666590bc2693` | `0c10d67d221bb5d4ea9dc1def715f9e2b9f649a6c5b5ed4166887358723c50cb` |
| `Ashlar.CLI` | 121,261,866 | `d35328ec694f2d01dd0775b694a1ff35b2acd375479cade3dd7a545d6c391176` | `d1a2f46a4baf7cf28c69f9daede46b3909772b400c78578b41ad80b1f5c595b0` |
| `Ashlar.Certification.Contracts` | 121,182 | `982a1868f1dc68b2302696a2d90e185d0986014e69bacc79b74c364ddb1365bb` | `8beb5afa40ab376c26f4ac74ab2c3facb86f2d8472d8d19c655dfff022804df6` |
| `Ashlar.Client` | 43,701 | `ddc6cb0829440cf4b39dc1e20f2a6d5e5d41c97ffd76742339e4e84f77042caa` | `56b19941db149896bd7b5786798c7ef1e797f6f24527d421e5859b87cbc426e7` |
| `Ashlar.Contracts` | 83,864 | `228802599a97efc5227b192b9a67efe6101f9eeb93acd7f4c65c9efce8a3b3b4` | `717493771430e233a2ea85d95e63a620dce7e7d29b8f760fc409cec1d672bcf2` |
| `Ashlar.Core.Application` | 990,870 | `d4b58bd86f8d215cc8b95c7a091365eb534e470a8ae8b389dff5ff88178e1db0` | `70d4d3633df5e9d3d5110a6ddaf206c0b9daa3b38daa26c9fa4032145d5e41a3` |
| `Ashlar.Core.Domain` | 206,152 | `838f5e02e840d06bef55f9cf69187c7f403a71e503af244be4935adbf40749fb` | `9898d1cecdb89d8613c5249662b39d9478260304cb1ee9f4d41e12ecbf5a6a67` |
| `Ashlar.Hosting` | 107,158 | `fef74c41602306b2100992f75904bebd6b65536a0987b0a6e4bbb6725bce98b7` | `cb7de9fcead63c0282793091f74a38c86871868df8e2fc5fc3e9c791a0d2943d` |
| `Ashlar.Hosting.Bundle` | 14,726 | `2fbcc30c2362adb235e14c82a808de21fa8ce6025e48866c87f7c7d126f0053c` | `2a607a2553d36ffa11dd919c2b2ed9b5e488e90f7f050e528a6825ea13005bcb` |
| `Ashlar.Infrastructure` | 1,533,025 | `60aaf96bf89b431eac7b40a4ed25dce59aab54b462750cd16feea658dbdfd896` | `f9d3a1012acc5047d43ee4f785d7646dee9bf9aca15ee57eccedbecab1d859be` |
| `Ashlar.Mcp.Server.Host` | 93,644,872 | `da9c0a182e8bdf6e09d0dda850c98145254540e3fc4f76b17341b12218e09dcf` | `b4487c8f29b7d15d2ecf5add61345a6647acc01ff0ca00cd50ca6af1a75cd694` |
| `Ashlar.Orchestration` | 417,637 | `d6000009946b33e98ff4ecd61a9e04efc5c7540d3f604991c5255db700707095` | `57f0bad325024e08109503239f79321791271c6cc4e7921bee1c6ae61c1eb3c2` |
| `Ashlar.Policies` | 36,904 | `d23453e01eb628c7a16553844bd049cca5320b8febac1f038ba93ca6011b394c` | `538982d4021ce0f4d023a6fe533e9c89c3e227920b683aa14c16da6f7c5efe3e` |
| `Ashlar.Runtime` | 117,564 | `57f53fc8e5b5679dbc30c7056b8e98e8809e36c12104a816a266dbd636e84d50` | `35c7d282c471a464384060511a7144e46416bb2f7266df4cdada244a4767b696` |
| `Ashlar.Sdk` | 32,469 | `1197b17fc66a20c810b232842ed49c327f4fc75c1f4235a5c7ccfb89e895a417` | `f662d09ac647d3c57b9bae40532dbfde38bec3c724c7773f3e46e0935e2b3371` |
| `Ashlar.Tools.Assembly` | 47,723 | `86e2bc3ec7f9cf3f974f71a62476241e33de4899840dab09138ec9d08931ab95` | `23647e76d8f90ffbdb333e640ffde902b1d6fdf23ff4b14dd1d39ac650dba52e` |
| `Ashlar.Tools.Dev` | 91,675 | `121695d93f9ef69422c9e681850355e69f532c3849876f108bf25a229b0ca17e` | `fd9358e44c9696e513c2c6f3eaadd24851ed15e4dd1b02cde85cac58a8126d88` |
| `Ashlar.Transport.Grpc` | 65,023 | `536b38c2a49c6d8fccac54d4e36a8fb3116bf3f1c0b1c786d153338fe09517ce` | `0c121c0ef692a3b0b2607fba91672d0d4c807cd91324deaf259e0a41cd1ce8a9` |

Retained symbol packages have the same unique version and verified NuSpec identities:

| Symbol package ID | Bytes | Raw SHA-256 |
| --- | ---: | --- |
| `Ashlar.Authoring` | 18,596 | `007e9be589e846006ebecb6725b800f8e5c4ee7a3ce1f8819503c0a916fabbd6` |
| `Ashlar.Bricks.GitHub` | 43,629 | `9d9e611863a2c904a72c47a2efd9f159b7af740b8a732479f39692e601cc7b0c` |
| `Ashlar.CLI` | 769,154 | `907803fe220911b154082efdfffc5597f4c60716ff288f5a2fbbe96bb2d8eb69` |
| `Ashlar.Client` | 19,175 | `03453a77d57ede643238beaf94b3fc9c977a2b85c87eb730f51c1bdf29209119` |
| `Ashlar.Hosting` | 39,516 | `78a4b54a11b9a66751396fc4fcd2130b04823008c1536738c98e11c75d50c2b0` |
| `Ashlar.Sdk` | 18,138 | `b90238d827e04f8da5904f790666d6d3abc82cc54b85936ba45e6134197fd826` |

### Isolated consumers and named trust assertions

The SDK consumer restored into a fresh isolated cache, built and ran the package-consumption sample, and emitted both `Stable SDK host sample bootstrapped successfully.` and `verify-stable-sdk-host-sample-packages: OK`. Its cache receipt observed **18 Ashlar packages**, including `Ashlar.Hosting.Bundle`; every observed `.nupkg` matched the produced feed's exact version and raw SHA-256. This is evidence for the SDK dependency subset, not a claim that the SDK sample consumed all 24 produced packages.

The trust consumer used a different fresh isolated cache. Its receipt observed **one Ashlar package**, `Ashlar.Certification.Contracts`, with the exact produced bytes. Both helpers consumed the same produced feed and version without repacking. The trust consumer selected `lib/net8.0/Ashlar.Certification.Contracts.dll` and `lib/net10.0/Ashlar.Certification.Contracts.dll` for their respective frameworks.

| Observed named assertion | net8.0 | net10.0 |
| --- | --- | --- |
| `bound-asset-matches-consumer-tfm` | PASS | PASS |
| `content-hash-mismatch-refused` | PASS | PASS |
| `default-refuses-hmac-only-record` | PASS | PASS |
| `default-refuses-schema-downgrade` | PASS | PASS |
| `default-reports-itself-strict` | PASS | PASS |
| `default-requires-ed25519` | PASS | PASS |
| `default-schema-floor-is-trust-loop` | PASS | PASS |
| `hmac-fallback-reports-dev-key` | PASS | PASS |
| `legacy-reports-itself-not-strict` | PASS | PASS |
| `omitted-options-refuses-hmac-only-record` | PASS | PASS |
| `positive-control-legacy-trusts-valid-hmac-record` | PASS | PASS |
| `strict-refuses-hmac-only-record` | PASS | PASS |
| `strict-requires-certifier-identity` | PASS | PASS |
| `strict-requires-ed25519` | PASS | PASS |
| `strict-requires-gate-emitted-artifact` | PASS | PASS |
| `strict-schema-floor-is-trust-loop` | PASS | PASS |
| `stripped-hmac-signature-refused` | PASS | PASS |
| `unknown-schema-version-refused` | PASS | PASS |

Each framework emitted exactly `ASSERTIONS_RUN=18`, `ASSERTIONS_FAILED=0` and `RESULT=OK`. The final transcript emitted `PACKED_CERTIFICATION_TRUST=OK` for the unique local version, and the independent auditor requires complete, consistent assertion records and rejects missing, duplicate, skipped, failed or contradictory assertion records. All **36/36 named assertions** passed.

### Clean source, environment and audit provenance

Host, container and rehearsal exit-status files all recorded `0`. Initial and final container source status files and the final host source status file were present and empty; source SHA checks remained pinned. The produced feed and manifest matched before and after both consumers.

Actual retained environment records show SDK **10.0.403**, `Microsoft.NETCore.App` **8.0.34** and **10.0.14**, and `Microsoft.AspNetCore.App` **8.0.34** and **10.0.14**. The controller unset roll-forward and cache-bypass variables before execution.

Devtest image identity: `sha256:8105fcf191b0c9aa08e4b0aaf64fa4c506b54040f3e78824d4223b5462a6505c`. All build and consumer commands ran through the repository's Linux devtest container wrapper. The independent artifact audit executed local reviewed validation code and did not execute downloaded artifact scripts.

The following hashes bind the retained evidence files. Paths beginning `package-rehearsal/` refer to files inside the original artifact; workflow/API metadata and the independent receipt are retained beside the original ZIP.

| Retained evidence | SHA-256 |
| --- | --- |
| `Independent audit receipt: package-audit-results/package-audit.json` | `1df2667944838a87d219db5f6750226e90dfaf09d0a377b9c4bbe18dca627fa5` |
| `Raw workflow run metadata: package-artifacts/workflow-run.json` | `f2d40dc201f89dccb245ee79b0697c507691c62da08bc89843eaf2cfdff0e6bb` |
| `Raw artifact metadata: package-artifacts/artifact-metadata.json` | `1ff0dc93add43b15fdde0e9f283a63592093ce4758b395cf84b9b77dc4e51215` |
| `Transport/source ingestion receipt: package-artifacts/ingest.json` | `c4c1b19740d0ba440877cb0a81fa9174e443dbba97ddee8c2b12c9f004eeadca` |
| `prerequisites.json` | `abc0aae0b7ac05115ed9bb096a3c299a84e3c2810e6935d93fc4f508c2e2ffb9` |
| `package-rehearsal/packages/nuget-publish-manifest.json` | `02916326a4a102a22b13b367b964767dab74cc2ebf45b5da1feb7d9a046994b7` |
| `package-rehearsal/package-digests-before.json` | `585b16f0c206e0132a1e3de23204685625339bf3db1c7a05ad8e410e6f2c5cdb` |
| `package-rehearsal/package-digests-after.json` | `585b16f0c206e0132a1e3de23204685625339bf3db1c7a05ad8e410e6f2c5cdb` |
| `package-rehearsal/consumer-cache-receipts.jsonl` | `e0908d5fa81b9e131819982042bda7cad1fb6dbe5acc5948b4ca6fd29fab5ad0` |
| `package-rehearsal/sdk-consumer.log` | `61c6fe8a7f2277e1eef7ee208e7078bb8f2873119593330916386ce498d76786` |
| `package-rehearsal/packed-trust.log` | `6e1b040f822efa034c5315db79858294725279db764e4774ffe911c142a87bf1` |
| `package-rehearsal/trust-assertion-receipts.json` | `d739c8357db76dd3a349b815419d8580f3763d78ea95bffc7779d27b1ae0d7e3` |
| `package-rehearsal/trust-net8.0.txt` | `888880fd018d64333cb68e60839b1eb6e3ba597eddd53ec254a9c4bc3652163c` |
| `package-rehearsal/trust-net10.0.txt` | `2aade8ebe59ea96fd01c5561277eee2a95d589485c26cea9209c573b06ba54d6` |
| `package-rehearsal/runtimes.txt` | `1357f5aad91db4b3924c99fbfad81fa2880ad0319e1ab58e6b00a26c862f2374` |
| `package-rehearsal/sdks.txt` | `11f01ed99051f672615dd296411ee38be266f33c490ad2915b56afc06ad43fca` |
| `image.json` | `a08bb834572805b998376b730163d83f4d2f3ba6078dc5670d12903c9c7fda38` |

### Observation limits

The consumer helpers deleted their isolated caches after execution. The audit verifies their retained hash observations against actual retained package bytes and the reviewed executing controller; it cannot rehash those vanished caches. The package prerequisite receipt revalidates the campaign/review inputs; the original mutation TRX archives and full per-case provenance remain separate evidence.

This rehearsal establishes the recorded local package behavior and named assertions on both frameworks. It does not establish NuGet publishing credentials, public package indexing, repository/package signing, deployment, or other publication behavior.
