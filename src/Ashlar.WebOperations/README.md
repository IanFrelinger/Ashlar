# Ashlar.WebOperations

Websites as callable operations, natively in Ashlar. A website's own data requests are declared as **operations** in a
versioned JSON **spec**. Ashlar runs them over plain HTTP behind the egress guard, classifies every answer, learns new
operations from captured traffic, and repairs an operation when the site moves its data. No browser, model, sidecar
process or third-party runtime is involved.

## Pieces

| Piece | What it does |
|---|---|
| `SiteSpec` / `OperationSpec` | Declares hosts, parameters (with patterns), a request template (`{param}` encoded for where it appears), how to read the answer (`json`, `embedded-json` after a marker, or `json-ld` by `@type`), a `where` filter, canonical output `fields` with transforms (`date`, `epoch-ms-date`, `html-text`, `cents`, `text`), and the `shape` every item must keep. A spec never holds a credential: secrets are references (`cookie:NAME`, `header:NAME`, `query:NAME`) resolved per call; a literal that looks like one is refused at load. |
| `WebOperationClient` | Validates arguments; refuses undeclared hosts, loopback and IP targets for open-host reads; asks the egress guard about the request's host before sending (inventory row EG-HTTP-09); paces requests per host (default 1 s apart); caches public reads (`MaxAge`), keyed by credential scope so one caller's answer never reaches another; reads at most 8 MB. |
| Result classes | `ok`, `input` (an input status such as 404, an empty filtered answer, a bad argument), `drift` (the answer no longer has the declared data or shape), `auth`, `rate` (with `Retry-After`), `blocked` (a challenge page), `refused` (host list, egress guard or write guard; nothing sent), `duplicate`, `error`. Each comes with `Next` and `Retry`. |
| Writes | `kind: write` is refused unless the caller sets `AllowWrites` and an `IdempotencyKey`; a repeated key answers `duplicate`; a write is never retried and reports `sent`, `not_sent` or `may_have_sent`. A write may not target an open host. |
| `OperationLearner` | Builds an operation from two or more captured exchanges (or a browser's HAR export) made with different argument values. A request part that is the same in every sample is kept. A part that follows one argument (whole, or embedded with the same text around it) becomes that argument's slot. A part that changes with no argument to explain it is refused (a nonce, timestamp or session value), except well-known cache-busters, which are dropped. Cookies and secret-named headers or query values become references; user agents, `sec-*` and similar headers are dropped. The answer's data is the largest list of objects, unless hinted; its shape is the keys every item shares. |
| Verification | `VerifyAndSaveAsync` runs the learned operation live with arguments the captures did not use, against a trial store. It is saved as a new version only if that call answers `ok` with data. |
| `Healer` | On `drift`, looks in the response already received for a list of objects that still carries the declared shape, re-points the operation there, and saves a new version. No second request is made, so a repair cannot return another request's data. `SpecStore.Revert` undoes it. |
| `SpecStore` | The bundled specs plus learned and repaired versions in a directory (`{dir}/{site}/v{N}.json`); the newest version wins; reverted versions are kept as `.reverted`. |

## Bricks

| Brick | Inputs → outputs |
|---|---|
| `web.call` | `site`, `operation`, `args` (JSON), `maxAgeSeconds` → `class`, `data` (JSON), `reason`, `next`, `host`, `healed` |
| `web.learn` | `site`, `operation`, `har` + `harArgs` or `exchanges`, `hints`, `verifyArgs` → `saved`, `version`, `spec`, `credentials`, `notes`, `reason` |
| `jobs.list-board` | `board` (`greenhouse:<slug>`, `lever:<slug>`, `ashby:<slug>`, optionally `\|Display Name`) → `signal`, `entries` (`BoardEntry`), `detail` |
| `jobs.get-posting` | `url` or `posting` (a `PostingRef` key), `employer`, `today` → `signal` (`found`, `gone`, or a failure class), `provesOpen`, `posting` (`JobPosting`), `detail` |

Every brick declares its deterministic implementation, so it runs as an Ashlar workflow node. A host passes a caller's
credentials as an `ICredentialSource` in the execution context variable `webCredentials`.

## Bundled specs

| Site | Operations | Source |
|---|---|---|
| `greenhouse` | `listJobs(company)`, `getJob(company, jobId)` (with pay range where published) | Greenhouse Job Board API (public) |
| `lever` | `listJobs(company)`, `getJob(company, postingId)` | Lever Postings API v0 (public) |
| `ashby` | `listJobs(company)`, `getJob(company, jobId)` (read from the board, filtered by id) | Ashby job posting API (public) |
| `schema-org` | `getJobPosting(url)` | The schema.org `JobPosting` any career page embeds for search engines |

They emit canonical job fields, so `JobPostings` normalizes any of them the same way. A posting URL on one of the three
boards reads through its API; any other https URL reads the page's `JobPosting`. An expired `validThrough` answers `gone`.

## What is verified

- `WebOperationsTests` (in `Ashlar.Tests.Infrastructure`, run by cert-gate), behind the real egress guard against a stub
  transport: spec validation, refusals that send nothing, every result class, repair and revert, per-caller
  credentials and cache scoping, pacing, the write guard, learning (slots, credential references, refused nonces,
  dropped cache-busters), verify-before-save, HAR input, and each bundled spec against recorded response shapes.
- **Not** called live: this environment's network blocks the board hosts. The bundled specs are written from each
  vendor's public API documentation; their notes say so. The first live call is the check.
