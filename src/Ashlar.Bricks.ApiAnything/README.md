# Ashlar.Bricks.ApiAnything

Ashlar bricks over [API Anything](https://github.com/goodnight000/api-anything) (MIT), which turns a website's own
data requests into operations callable over plain HTTP. These bricks call a local `api-anything serve` through its
documented REST contract (`POST /v1/call/{site}/{op}` with a bearer token) and add what Ashlar adds to any
integration: an egress decision, a verdict with its evidence, and explicit unknowns.

Nothing here imports API Anything's code. It runs as a separate Node process the operator starts.

## Bricks

### `api-anything.call`

Runs one read operation of any site the server knows.

- **Inputs:** `site`, `op`, `args` (a JSON object of strings).
- **Outputs:** `ok`, `class` (`ok`, `input`, `drift`, `auth`, `rate`, `blocked`, `error`, `refused`), `tier`, `data` (JSON),
  `reason`, `next`, `upstream` (the website host the egress guard evaluated).

### `jobs.list-board`

The postings on one public employer board.

- **Inputs:** `board`: `greenhouse:<slug>`, `lever:<slug>` or `ashby:<slug>`, optionally followed by `|Display Name`;
  `maxPages` (Greenhouse only, 50 postings a page, default 3).
- **Outputs:** `signal` (`ok`, `partial` when a later page failed, `error`, `refused`), `entries` (`BoardEntry` list:
  title, location, URL, arrangement, date, and the `getJob` call that reads it), `detail`.

### `jobs.get-posting`

One posting, read at the employer's own applicant-tracking system.

- **Inputs:** `url` (Greenhouse, Lever, Ashby, Workable or Workday's own tenant), or `posting` (a `PostingRef` key);
  `employer` (display name).
- **Outputs:** `signal`, `provesOpen`, `posting` (`JobPosting`), `detail`.

| signal | meaning |
|---|---|
| `found` | the source answered with the posting today; it is open **if** `provesOpen` |
| `gone` | the source answered `input`, which each operation's notes document for closed **or unknown** postings. A signal to corroborate, not a verdict of closed |
| `error` | `drift`, `blocked`, `auth`, `error`, or the server failed: not read |
| `refused` | the egress guard refused the website; nothing was sent |

`provesOpen` is false for Workday: its operation's notes say the public listing does not prove the position remains
open, and it returns no description.

## Network access and the egress guard (EG-HTTP-09)

The client sends its request to `api-anything serve` on **loopback only**; the constructor refuses any other base URL,
since the server holds the operator's sessions and token. The guard classes loopback as inside the host, but the server
then reaches the website from its own process, outside the guard. So before every call the client evaluates
`https://<website host>/` under the same site id and throws `EgressRefusedException` on a refusal, before anything is
sent. The website host comes from `ApiAnythingClient.KnownUpstreams` for the job sites, else from the server's own
`baseUrl` for the site; when neither says, the call is refused unsent. See `docs/EgressInventory.md`, row EG-HTTP-09.

## Running the server

```sh
API_ANYTHING_LOCAL_ONLY=1 npx -y api-anything@0.4.0 serve      # http://127.0.0.1:8787, token in ~/.api-anything/serve-token
```

`ApiAnythingOptions.FromEnvironment()` reads `API_ANYTHING_URL`, then `API_ANYTHING_TOKEN` or the token file under
`API_ANYTHING_HOME`. Defaults: `maxTier` 1 (plain HTTP, no browser), `maxAgeMs` 15 minutes (two callers reading the same
public board share one request to the employer). `API_ANYTHING_LOCAL_ONLY=1` stops every request to API Anything's
shared catalog.

LinkedIn's operations are not used by the job bricks: they read through an imported browser session, which in a
multi-user service would be the operator's, and LinkedIn has no job-search operation.

## What is verified

- Against a stub of the REST contract, behind the real egress guard: `ApiAnythingBricksTests` in
  `Ashlar.Tests.Infrastructure` (refusal sends nothing, unknown website refused, pending polled not resent, 429, 401,
  each site's field mapping, paging, URL rules).
- **Not** run against a live `api-anything serve` or a live employer board from this repository. The field names come
  from each site's spec in API Anything's `sites/` directory at commit `985ae8f` (2026-11-07).
