# Job Search Studio: record of processing

This is a draft of the record an operator keeps of what personal data the studio processes, why, where it goes and
how it is protected (GDPR / UK GDPR Art. 30; CCPA/CPRA "notice at collection" inputs). It describes what the software
does. The operator adds their own details (marked **operator**) and their own legal judgement. This is not legal
advice.

The controls behind each line are in [STUDIO.md](STUDIO.md) §3 and in the code it names.

## Who

| Role | Who |
|---|---|
| Controller | **operator**: the organisation that runs the studio, with its contact details and, if it has one, its data protection officer. |
| Data subjects | Job seekers who sign up. Managers whom a job seeker grants access. People named in a résumé a user uploads (for example, references). |
| Processors | AWS: hosting, storage, backups, keys, secrets. Anthropic: the Claude API, which receives no personal data by design (below). Each needs a data processing agreement with the operator (**operator**). |
| Not processors | Google: the user signs in with their own account, and the studio acts in the user's own Drive and Sheets with the user's grant. The private model runs on the operator's own server. Job boards receive no user data. |

## What, why, and for how long

| Data | From | Kept in | Purpose | Retention |
|---|---|---|---|---|
| Account: email, display name, time zone | Google sign-in, the user | Database on the server's data volume | Run the account | Until the account is erased |
| Consent records: kind, notice version, time given or withdrawn | The user | Database | Show what the user agreed to (Art. 7(1)) | Until erasure |
| Résumé text and its earlier versions | The user | Database; the user's own Drive | Build the profile, check fit, tailor materials | Until erasure; the Drive copy is the user's |
| Profile fields, each with its evidence and whether the user confirmed it, including work authorisation and sponsorship need | Derived from the résumé, then confirmed or edited by the user; unknown stays unknown | Database | Screen postings against what the user can truthfully claim | Until erasure |
| Preferences: roles, places, arrangements, salary floor, boards, schedule | The user | Database | Search and screen | Until erasure |
| Jobs found, checks, assessments, applications, notes, documents index | Runs, the user | Database; the tracker sheet and documents in the user's Drive | The tracker and materials | Archived jobs 365 days, unless the user applied (then kept as their history). Run records 180 days. |
| Activity log | The system | Database; exported to the user's own sheet | Show the user what happened on their behalf | 400 days after export |
| Workflow: the user's steps and their settings | The user | Database | The user's pipeline | Until erasure |
| Requests for new steps (free text) | The user | Database while the request is worked on | Design the step on the private model | Cleared when the request finishes. The job record keeps the generic design and its progress notes for 400 days. |
| Google access and refresh tokens | Google, at the user's grant | Secret store, encrypted (AES-GCM) under the master key | Act in the user's Drive and Sheets | Until disconnect or erasure |
| Manager grants: who, which scopes, until when | The user | Database | Let a user's chosen manager help | Until revoked or expired; removed with either account |
| Tombstones: keyed hash of an erased account's id, time, row count | Erasure | Database; the erasure ledger beside the backups | Keep an erasure in force after a restore from backup | Kept. The hash is keyed with a secret and identifies no one without the database it was erased from. |
| Backups of all of the above | Hourly copy | S3, encrypted with the operator's KMS key | Recovery | 35 days, then deleted by the bucket's lifecycle |
| Visitors' IP addresses | — | Not kept: Caddy writes no access log and does not pass them to the studio | — | — |
| Session cookie | The studio | The user's browser (HttpOnly, SameSite=Lax) | Keep the user signed in | 12 hours, sliding |

The profile has no fields for special categories of data (GDPR Art. 9), such as health, ethnicity or religion. A
résumé can still mention them, and the studio stores the résumé as text. Work authorisation and immigration status are
"sensitive personal information" under the CPRA. The studio uses them only to screen postings for the user, and
records them only when the résumé or the user states them.

**Legal basis (operator).** The usual analysis:

- Contract (Art. 6(1)(b)) for the service the user signs up for.
- Consent (Art. 6(1)(a)) for model-worded cover letters. The studio asks for it separately (`model-wording`), and the
  user can withdraw it on the Privacy page.
- Legitimate interests for security.

Automated decisions: the studio's fit checks are advice to the user about postings. They are not decisions about the
user by anyone else (Art. 22).

## Where personal data goes

| Recipient | What it receives | What keeps it to that |
|---|---|---|
| The private model (Ollama on the same server, loopback only) | Résumé, profile and typed requests, to design steps and to word cover letters | The model client accepts only a loopback address. The egress guard, in enforce mode, admits only this host to frames that read personal data. |
| Claude (Anthropic API) | Generic step designs and made-up example postings; never a résumé, profile, tracker or typed words | The design must pass the PII gate inside a frame entered as public; the guard refuses Claude from any frame that read personal data. |
| Google (the user's own account) | The user's documents and tracker, written at the user's direction | The user's own OAuth grant; the user can revoke it in their Google account. |
| AWS | Everything at rest, encrypted | The operator's account and chosen region; the KMS key; IAM roles that cannot delete backups. |
| Job boards | Nothing about users: the server fetches public postings | — |
| Let's Encrypt | The studio's domain and the operator's contact address | — |

International transfers (**operator**): AWS stores data in the region the operator chooses. Anthropic, a US company,
receives no personal data by design. Google holds what the user keeps in their own account.

## Security of processing (Art. 32)

**In transit.** TLS at Caddy, with HSTS.

**At rest:**

- EBS volumes are encrypted.
- Backups are encrypted with the operator's KMS key.
- OAuth tokens are encrypted with AES-GCM under a master key. The key is kept in the parameter store, never on disk
  and never in a backup.

**Access:**

- No SSH; administration goes through Session Manager, and AWS logs every session.
- The server's role can read only the studio's own parameters, and can write backups but never delete them.
- Containers cannot reach the instance role (IMDSv2, hop limit 1).
- The studio runs as a non-root user, on a read-only root filesystem, with no Linux capabilities.

**Generated code.** Steps written by a model pass Ashlar's certification gate: an import fence, witness cases, mutation
testing and a determinism check. They then run in a separate process with no secrets in its environment, a timeout
and a memory cap.

**The web app:**

- no third-party scripts, fonts or analytics;
- a strict Content-Security-Policy;
- a CSRF header on every write;
- no request bodies or query strings in the logs.

**Data minimisation:** visitors' IP addresses are not kept, and request text is cleared once a request finishes.

## Data-subject rights

| Right | How |
|---|---|
| Access and portability (Arts. 15, 20; CCPA right to know) | The Privacy page, *Download everything*: one JSON file of every row about the user (`GET /api/me/export`). Tokens are listed by reference, never exported. A request by email: `jobsearch user export --email E --out FILE` on the server. |
| Erasure (Art. 17; CCPA right to delete) | The Privacy page, *Delete my account* (`POST /api/me/delete`): every row with the user's id, their grants both ways, their tokens and their private steps. Shared steps they asked for stay, without an author. A request by email: `jobsearch user erase --email E --confirm ERASE`. Documents in the user's Drive are theirs and stay there. |
| Erasure and backups | Backups expire after 35 days. A restore re-applies every erasure in the newest ledger before the studio starts (`jobsearch erasures apply`; deploy/README.md, "Restore"). |
| Rectification (Art. 16) | The Profile page: every field can be edited, confirmed or rejected; a new résumé can be uploaded. |
| Withdraw consent (Art. 7(3)) | Model-worded cover letters: the Privacy page's *Withdraw* button, as easy as giving it. The notice, terms and age confirmation are conditions of the service rather than consent to processing; a user who no longer agrees deletes the account. |
| Objection, restriction | **operator**, by hand: the studio has no switch for them (known limit). |

## The operator's own tasks

- A privacy notice with the operator's identity, contact, legal bases and retention. The studio shows notice version
  `2026-11-08` and records consent to it. Bump `Compliance.NoticeVersion` when the text changes; users then accept
  again.
- Data processing agreements with AWS and Anthropic.
- A breach procedure. Sources of evidence:
  - Session Manager history;
  - CloudTrail for the IAM roles, KMS and parameter-store reads;
  - container logs on the server (Docker keeps 3 × 10 MB per container).
- Answering requests within the legal time limits, with the commands above for requests that do not come through the
  app.

## Known limits

- An erasure made in the hour before a total loss of the data volume is not in any backup or ledger yet, so a restore
  brings that account back. Re-run the erasure if the user's request is on file.
- The master key cannot be rotated yet.
- Restriction and objection have no in-app switch.
- The PII gate is a detector, not a proof. Free text never goes to Claude in the first place, which is why the gate is
  a second line, not the only one.
