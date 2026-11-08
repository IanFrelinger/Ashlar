# Job search on Ashlar

A multi-user job-search and application tracker, built on Ashlar as a dogfooding exercise. Each user connects their own
Google Drive. That Drive holds their tracker, master résumé, tailored drafts, attachment-ready PDFs, archive and
activity log. Discovery reads employer applicant-tracking boards and career pages through
[`Ashlar.WebOperations`](../../src/Ashlar.WebOperations/README.md), Ashlar's native capability for calling a website's
own data requests as declared operations. Each posting runs through an Ashlar workflow: read, screen, draft, validate.

- Architecture, data model, what is authoritative where, and how sheet edits are reconciled:
  [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).
- What building this on Ashlar taught us about Ashlar: [docs/DOGFOOD-FINDINGS.md](docs/DOGFOOD-FINDINGS.md).

## What it does

| Area | Behaviour |
|---|---|
| Accounts | One account per user; a user grants a manager specific scopes (read, notes, run a search, materials, preferences), optionally until a date. The following are never delegated: connecting Drive, granting access, asserting an application happened, and changing sharing. The system itself never changes sharing. |
| Onboarding | Upload a résumé; the profile it derives (name, contact, employment with dates, skills, education, certifications, total years, any clearance *claim*) is shown for the user to confirm, correct or reject, each field with the résumé line it came from. Work authorization, sponsorship, clearance status, travel and relocation are only ever set by the user. Unknown stays unknown. |
| Drive workspace | `Job Search — <name>/` with `Master résumé & profile`, `Applications/<employer — title>`, `Submitted (frozen copies)`, `Archive`, the `Job Tracker` sheet and the `Activity Log` sheet. |
| Discovery | The user lists employer boards (`greenhouse:<slug>`, `lever:<slug>`, `ashby:<slug>`) and pastes single postings (a board posting URL, or any https career page that publishes schema.org `JobPosting` data). Titles are matched to the user's target roles; reads are capped per run. Source and check date are recorded for every listing. |
| Deduplication | Same source posting; else same employer + requisition number; else same canonical URL; else same employer + title + location. |
| Listing status | `open`: the employer's own source returned it today. `unverified`: the source stopped returning it once, or it could not be read. `closed`: the source stopped returning it **and** it is absent from the board listing, or two checks in a row. `stale`: not verified open for 7 days (configurable). |
| Screening | Required/preferred skills, years, degree, clearance (active vs. obtainable), citizenship, visa sponsorship, travel, pay, deadline, remote restrictions, seniority, each with the quote it came from. The candidate side carries evidence by résumé line. Recommendation: `strong_fit`, `conditional_fit`, `stretch` or `not_currently_eligible`, with thresholds per user. It never states that a role is remote from every state or that the employer sponsors a clearance unless the posting says so. |
| Materials | For qualifying, verified-open jobs only (at most N new per run), the user gets an editable résumé (their own bullets, verbatim, reordered by relevance) and a cover letter (only facts with résumé evidence, plus `[CONFIRM: …]` placeholders), both as Google Docs, with matching PDFs. |
| Validation | Every bullet, date line, employer, skill, years figure and clearance mention is traced to the résumé or the user's confirmed profile. Missing requirements and items needing confirmation are listed. Each PDF is read back to prove its text is extractable. The record is stored with the document and summarized in the tracker. No applicant-tracking outcome is predicted. |
| Staleness | Editing a draft makes its PDF stale; `refresh-pdf` re-renders it from the user's text and re-validates (a new unsupported claim is flagged, not removed). Review (`approve`) is refused while a PDF is stale. |
| Application tracking | Status (`not_applied` … `offer`), applied date, next steps and notes are the user's, editable in the sheet or the CLI. `applied`, `interviewing`, `offer` and `rejected` need the user's own statement. When a job becomes `applied`, the materials then in the workspace are copied to `Submitted`, the PDF revision is kept forever, and hashes and warnings (stale, unreviewed) are recorded. The system never submits anything. |
| Cleanup | Closed listings without an application are archived and their unsubmitted drafts moved to `Archive`. Drafts from a replaced résumé or for a no-longer-eligible job are archived too. Nothing is deleted. |
| Schedule | Each user's own time zone; default 08:00 local; the dispatcher runs everyone due, in parallel. |
| Snapshots (optional) | One git repository per user: tracker, profile (contact redacted), document manifest, run history. Commits happen only on change. Push requires a remote the caller has confirmed private. Restore works from a reviewed plan. |

## Running it

Build and test in the repository's devtest container (never `dotnet` on the host; see the repository's `CLAUDE.md`):

```bash
bash scripts/test-in-container.sh --dirty -- "dotnet test apps/job-search/tests/JobSearch.Tests/JobSearch.Tests.csproj"
```

### Demo (no network, no credentials)

```bash
bash scripts/test-in-container.sh --dirty -- \
  "dotnet build apps/job-search/src/JobSearch.Host && dotnet apps/job-search/src/JobSearch.Host/bin/Debug/net10.0/jobsearch.dll demo --out /tmp/demo"
```

Two synthetic users run concurrently: Jordan (data engineer, Denver) and Sam (nurse, Columbus, with work authorization
and clearance deliberately unknown). They share the Northwind board, but their records stay separate. On day 2 Jordan
marks a job `applied` and adds a note in their own sheet, and an employer closes a posting. The demo writes each user's
tracker, activity log, Drive listing, editable drafts and PDFs. Boards come from `fixtures/boards.json`, answered at
the boards' public API endpoints by an in-process stand-in, behind the real web-operations client, the bundled specs
and the egress check. Drive and Sheets are simulated.

### With real services

1. **Employer sites**: nothing to install. Allow the hosts the bundled specs call (`boards-api.greenhouse.io`,
   `api.lever.co`, `api.ashbyhq.com`) and the career-page hosts users paste, in the network policy. Specs the app learns
   or repairs are kept in `<data>/specs`; `SpecStore.Revert` undoes a bad repair.
2. **Google**: create a Google Cloud OAuth client (desktop app) with the Drive API and Sheets API enabled. Set
   `GOOGLE_CLIENT_ID` / `GOOGLE_CLIENT_SECRET`. Each user runs `jobsearch --google google connect-google --user <email>`,
   approves in their own browser, then reruns with `--code`. The scope is `drive.file`: the app sees only files it creates.
3. **Users**:
   ```bash
   jobsearch --google google user add --email you@example.com --name "You" --tz America/Chicago
   jobsearch --google google resume --user you@example.com --file resume.txt
   jobsearch --google google profile --user you@example.com                       # review; --confirm / --reject / --set
   jobsearch --google google profile --user you@example.com --set work_authorization="US citizen"
   jobsearch --google google prefs --user you@example.com --roles "data engineer;analytics engineer" \
       --locations "Chicago, IL" --arrangements remote,hybrid --salary-min 140000 --board "lever:spotify|Spotify"
   jobsearch --google google run --user you@example.com
   jobsearch --google google dispatch                                              # every 15 min: runs whoever is due
   ```

## What is verified, and what is not

Verified in this repository (container, .NET 10), by tests that fail when the behaviour breaks
(see the mutation checks in the PR):

- 19 app tests: isolation of two concurrent users (database rows, Drive files, and a cross-account read refused);
  concurrent and repeated runs for one user without duplicate jobs, files, documents or rows; an interrupted run
  resumed without duplicating its orphaned upload; sheet edits imported, edited system cells kept, both-sides
  conflicts kept, deleted rows restored; evidence required for application statuses and never delegated; screening
  evidence, gaps, unknowns and blockers; drafts free of unsupported claims, with PDFs that extract at ≥ 95 %; stale-PDF
  detection and re-validation; closed / unverified / stale transitions and archiving; expired access, Google rate
  limits, a site's `rate`, and refused egress; per-user time zones; snapshots with no secrets.
- The real Ashlar composition root (`AddAshlarSdk` + `AddAshlar`) hosting all six bricks, and the real
  `WorkflowExecutor` running every posting.
- `Ashlar.WebOperations` (engine, learning, repair, write guard, bundled job specs) against a stub transport, behind
  the real egress guard (`WebOperationsTests`, in the cert-gate suite).

**Not verified against real services** (none was reachable or authorized from this environment):

- **The bundled job specs, live.** They are written from each vendor's public API documentation, and the board hosts
  are blocked by this environment's network policy, so no live call has been made. The stand-in answers in the
  documented shapes; the first real run is the check (a `drift` result would say what moved).
- **Google Drive and Sheets.** `GoogleRestWorkspace` and `connect-google` were never called against Google. No OAuth
  client exists. Behaviours that depend on Google's side (HTML-to-Doc conversion, `appProperties` search,
  `keepForever`, Sheets value ranges) are written to the documented APIs but unobserved.
- **GitHub snapshot push.** `Push` runs `git push`; nothing confirms privacy for you. The caller must check it with the
  GitHub API under the user's own authorization.
- **Nothing is deployed or scheduled.** `dispatch` is a foreground loop; no service unit, container or cron exists.
- **Not yet observed in CI.** `apps/job-search/**` is in both readiness path lists, and the readiness lane's
  `native ci verify` discovers `JobSearch.Tests` (`ci/test-ownership.tsv`), but no CI run of it has been seen yet.
  `apps/` is outside Ashlar's egress-scan roots (DOGFOOD-FINDINGS F5).

## Remaining steps to go live

1. Allow the three board hosts in the network policy and run one `jobsearch run` against a real board of each kind to
   confirm the bundled specs.
2. Create the Google OAuth client (Drive + Sheets APIs, scope `drive.file`), connect one test user, and run once to
   confirm workspace creation, Doc conversion, the `appProperties` lookup and Sheets writes.
3. Decide where it runs (one host with `dispatch` under a service manager), and where `secrets.json` lives.
4. Confirm the readiness lane runs `JobSearch.Tests` on the first PR, and decide whether `apps/` joins the
   egress-scan roots (DOGFOOD-FINDINGS F5).
5. For GitHub snapshots: a private repository per user, created and authorized by that user; the remote's privacy is
   checked with the GitHub API before each push.

## Limits

- Screening is rule-based over a fixed skills vocabulary plus the posting's own words. It misses requirements phrased
  in ways the rules don't know (for example "RN license"), and says so when it finds none at all.
- Writing a sheet cell is not conditional. An edit the user makes between the app's read and its write in the same sync
  (milliseconds) can be overwritten; the next sync records it as a conflict.
- Discovery covers only boards the user names and pasted links. There is no keyword search across all employers.
  LinkedIn is not covered: its job data is behind a signed-in session, which a multi-user service would have to share.
- If the process dies between appending the activity log to the sheet and marking those rows exported, the same rows
  are appended again on the next run.
