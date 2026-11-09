# Steps shared across customers

**Status:** design, not built (2026-11-09). This is Phase 2 of [STUDIO.md](STUDIO.md) §6, by owner decision
(2026-11-09): a step written for one customer serves every customer, so the library, and the token savings,
compound with each new customer.

## The problem

Each customer runs on a server of their own: that is the privacy pitch. Each server's library is shared only among
its own users. Ten customers who each need "flag postings that mention on-call" pay for that step ten times, and a
new customer starts with only the four starter steps.

## How a step travels

```
customer server                     the platform's library bucket                  every customer server
───────────────                     ─────────────────────────────                  ─────────────────────
step shared inside this customer    submissions/<server>/<step>.json
  + the customer contributes   ──►  (host job, hourly)
                                         │
                                    operator review: re-certify, re-scan,
                                    dedupe, approve; seal with the
                                    library publisher key
                                         │
                                    published/<step>.json
                                    published/index.json (sealed)  ──────────────►  host job pulls (hourly);
                                                                                    the studio verifies the seal,
                                                                                    re-certifies from source,
                                                                                    signs with its own key, and
                                                                                    adds the step as origin "global"
```

1. **Submit.** A step can leave a customer's server only if all of these hold:
   - it is already shared inside that customer: it passed the PII gate, and the final check before publishing found
     nothing;
   - the customer has turned contributing on;
   - it is not quarantined.

   The host's hourly job exports it with `jobsearch library export` into the bucket's `submissions/` prefix. The job
   is the same pattern as backups: the studio container itself still holds no AWS credentials.
2. **Review.** The operator runs `jobsearch library review` (a new command). For each submission it:
   - re-certifies the step from its source;
   - re-runs the PII scan on every text field;
   - shows the closest published steps, so duplicates are caught;
   - on approval, gives the step a global id, seals it with the library publisher key, and writes it with a new
     sealed index.
3. **Receive.** Every server's hourly job pulls the index and any new packages. The studio:
   - verifies the seal against the publisher key it pins;
   - re-certifies the step from source with its own gate;
   - signs it with its own platform key;
   - adds it to its library, shared with all its users.

   The forge's "reuse" decision then finds it like any other library step.
4. **Revoke.** The index also lists revoked steps. A server that has one quarantines it on its next sync. A step that
   hangs or crashes on one customer's server is reported to the operator, who decides whether to revoke it for
   everyone.

## Trust

This follows Ashlar federation's rule ([docs/Federation.md](../../../docs/Federation.md)): **the network is transport;
the seal is the trust, and the receiver's own gate decides.**

- **A package is sealed.** Each one is Ed25519-sealed by the library publisher key. A server refuses a package whose
  seal fails, or whose sealer it does not pin. An empty trust set refuses everything.
- **No foreign assembly is ever loaded.** A package carries source, witness and metadata, never a DLL. Every server
  certifies the step itself, with its own certifier, in a child process, exactly as it certifies a step its own forge
  wrote. The origin's certification record travels as evidence only.
- **The publisher key lives with the operator.** It never sits on a customer server. Its public half is pinned in the
  image, as `trusted-signer.ed25519.pub` is for the certified rules today.

## Privacy: what crosses between customers

| Crosses | Never crosses |
|---|---|
| The step's generic code, witness and made-up examples | Anyone's setting values (they live in each user's workflow, not in the step) |
| Its generic name, description, behaviour and setting labels | Who asked for it, which customer it came from, which users use it |
| Its kind (check or column) | Any résumé, profile, tracker, posting or typed request |

How that holds:

- **The design was made generic on the private model,** and passed the PII gate before Claude ever saw it.
- **The server's own last check** keeps a step private if its code or examples name the user, so it is never
  eligible.
- **Review re-scans every text field** and puts a person in front of every step.
- **Packages carry no author** (I7) and no customer id. The `submissions/<server>/` prefix tells the operator which
  server sent a step; the published package does not say.

## Cost

One S3 bucket in the platform's account: versioned, encrypted with the platform's KMS key, holding JSON packages of a
few kilobytes each. Cents a month. There is no server to run, which matters for the runway.

Each customer server's role may write only under its own `submissions/<server>/` prefix and read `published/`.

## Reused from Ashlar, and what is missing

- **Reused:**
  - the certification gate (every server re-certifies);
  - Ed25519 signing (`CertificationRecordEd25519`);
  - federation's trust rule: re-gate on receipt, trust only pinned signers.
- **Missing: a portable package for certified bricks** ([DOGFOOD-FINDINGS F15](DOGFOOD-FINDINGS.md)). Ashlar's
  `.ashpkg` seals a self-extend gate record, not a brick certification. So the studio defines its own step package
  rather than reusing `.ashpkg`, and STUDIO.md no longer promises `.ashpkg`.

## Defaults until the owner decides

| Question | Default | Why |
|---|---|---|
| Do customers receive global steps? | Yes, on by default | Reviewed, generic code; it risks nothing for the receiver and is the point of the feature. |
| Do customers contribute their shared steps? | Off until the customer turns it on | Privacy is the pitch, and B2B buyers will ask. The platform seeds the global library itself, from the starter steps and the demo server. |
| Who approves a step for everyone? | The operator, for every step | The PII gate is a detector, not a proof. At tens of steps a month, a person reviewing each one is cheap and also curates names and duplicates. Automate it later, with sampling, once the volume justifies it. |

## Build plan

1. The step package format, plus `library export` and `library import` with re-certification on import.
2. The sealed index and revocation.
3. The host job in `deploy/host/jobsearch`; the library bucket and per-server IAM in Terraform.
4. `library review`, for the operator.
5. Each customer's receive and contribute switches: operator CLI first, an admin page later.
6. One number: the library reuse rate per server, meaning the share of step requests answered without writing a
   new step.

Tests that must fail if the feature breaks:
- a tampered package is refused;
- a package sealed by an unpinned key is refused;
- a package carrying a DLL is refused;
- setting values never appear in an export;
- a revoked step is quarantined on the next sync;
- a submission containing a profile string is refused at review;
- two synthetic servers in Compose exchange a step end to end.
