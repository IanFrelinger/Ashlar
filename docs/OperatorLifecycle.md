# The operator lifecycle: `init` → `verify` → `run`

Ashlar has a second persona alongside the developer embedding the kernel: the **operator** who owns
a deployed project — its contract, its envelope, and the decision about how much it is allowed to
change itself. This page is that lifecycle, driven entirely from the `ashlar` CLI.

You need the CLI as a .NET tool. It is on nuget.org:

```bash
dotnet tool install --global Ashlar.CLI --version 0.1.1
ashlar --help
```

(`--tool-path <dir>` instead of `--global` if you would rather keep it beside the project. Inside a
checkout of this repository you can substitute
`dotnet run --project application/src/Ashlar.CLI -- <args>` everywhere below.)

## The two documents

`ashlar init <name>` scaffolds a project: exactly two YAML files, and the split between them **is**
the security model.

```bash
ashlar init invoice-triage
```

```
  ashlar.yaml           project contract for 'invoice-triage'
  ashlar.policy.yaml    sandbox: .  ·  self-extend: sealed

  review the policy before you deploy. it is the only file
  the running app can never change.
```

| File | Owner | Says |
|------|-------|------|
| `ashlar.yaml` | the project | **What the application IS**: agents, their model providers, their tool grants, gates, bricks, targets. Agents may propose changes to it. |
| `ashlar.policy.yaml` | the **operator** | **What the application may BECOME**: the sandbox root, the self-extend dial and its budget, and the `never` list. The running application cannot read, propose, or modify it. The gate can. |

That asymmetry is the whole safety model. Read the scaffolded `ashlar.policy.yaml` before you deploy
anything — its comments explain the model better than prose does, and the file it describes is the
one the application can never touch.

`init` refuses rather than surprises you: it will not overwrite an existing `ashlar.yaml` or
`ashlar.policy.yaml` ("*refusing to overwrite existing project files … Remove them first if you
really mean to start over*"), it refuses a `--path` that names a file, and it refuses a project name
that is not letters/digits/hyphens starting with a letter, or is longer than 100 characters. It also
refuses to hand you a project its own loaders would reject, and refuses to scaffold anything other
than `mode: sealed` — "*Self-extension is raised deliberately, by a person, never by a template.*"

Use `--path <dir>` to scaffold somewhere other than the current directory.

## `ashlar verify` — the two-step reveal

```bash
ashlar verify           # --path <dir> to point elsewhere
```

`verify` runs the **courses** against the two documents and renders the wall. A fresh project has
three: `contract` (both documents load), `composition` (agents gated, targets declared) and
`envelope` (the policy’s sandbox and floor). A fourth, `provenance`, appears only once the project
has a signed ledger — a project that has never been certified stays at three rather than showing a
vacuous pass. Green ✓ is a per-course pass; red × is a failure; gold marks the verdict and nothing
else. Exit codes are `0` verified, `65` verification failed, `1` usage/environment. `NO_COLOR` is
honoured.

With no operator key, the verdict is:

```
  ✓ VERIFIED   3 courses · unsigned — run `ashlar keys init` to certify
```

That word is deliberate. **VERIFIED means the courses passed. CERTIFIED means signed.** With no key
there is no signature to claim, and the command says so rather than implying more than it has.

Generate a key and the same command upgrades:

```bash
ashlar keys init                    # --rotate to replace an existing key
ashlar verify
```

```
  ✓ CERTIFIED  4 courses · signed <fingerprint> · ledger #1
```

Now a real Ed25519 signature over this verification is the head of the project's instance ledger
under `.ashlar/`. The key lives in `$ASHLAR_KEY_DIR`, else `~/.ashlar/keys`, and is machine-global —
not per project. `--rotate` retains the old **public** key under `trusted/` so records it already
signed keep verifying.

Two behaviours worth knowing:

- **The `provenance` course fails when the documents no longer match the certification.** For an
  operator holding the key that is a re-certification, not a dead end: if every other course passes,
  `verify` appends a fresh signed entry over the *current* documents and they become the certified
  ones again. This is also what refuses a downloaded bundle whose documents were altered after
  signing.
- **Failures fail closed.** A corrupt operator key is a refusal, never a silent fall-back to
  unsigned. A corrupt ledger cannot be re-certified over, because appending verifies the chain
  first.

### When the ledger itself refuses

`ashlar ledger status` runs the same fail-closed check and prints the refusal verbatim:

```bash
ashlar ledger status          # exit 0 intact · exit 65 with the refusal
```

Most ledger refusals are cleared by a signed `ashlar verify` — an append re-verifies the whole chain,
re-pins the head anchor, and records the disagreement as a failed `ledger-anchor` course inside the
entry it writes, so the repair joins the history instead of erasing it.

Two are not, and deliberately so: a chain **shorter** than its anchor, and an anchor with its entries
**gone**. Both are what truncation looks like, and letting `verify` clear them would mean a fresh,
valid-looking head could always be written over a history someone deleted. The repair that keeps the
history is to restore `.ashlar/ledger` and `.ashlar/ledger.head.json` from backup. Accepting the loss
instead is a separate, signed decision:

```bash
ashlar ledger reanchor --yes  # re-verifies every surviving entry, then re-pins the anchor over them
```

When **nothing** survived — the anchor is alive and every entry under it is gone — the same command
starts the history again and writes the destruction down as its first signed entry: the destroyed
anchor's sequence and hash, recorded as a failed `ledger-anchor` course. An anchor cannot honestly
pin an empty directory, so the loss is put on the record instead. It reports that outcome as what it
is, and does not count that first entry as a survivor:

```
OK loss accepted  NOTHING survived - signed ed25519:…
every entry under the anchor was gone, so there was nothing to re-verify and nothing to re-pin.
the history has been started again, and the destruction is written down as its first signed
entry - that entry is now the only surviving evidence this project ever had a history.
this is not a recovery. nothing was recovered.
```

If that anchor's own signature does not verify, the recorded sequence and hash are written down as
the anchor's **claim** rather than as fact — they are what was found on disk, not a length anything
attested. The state is still accepted, because refusing it would strand every message that names this
command as its fix.

Do **not** delete `ledger.head.json` to clear this state: that is the one act that makes the
destruction invisible, and it is exactly what the refusal is detecting.

Without `--yes` it prints exactly what would be accepted and changes nothing. It recovers nothing —
whatever is missing stays missing — which is why it is its own verb and not a side effect of
verifying. It still refuses a chain whose entries do not verify: it accepts a shorter history, never
a forged one.

A re-anchor is a **signed** act, so it needs the operator key. If there is none it refuses, naming
the directory it searched and a `keys init` that puts a key exactly there — note the `--key-dir`,
which matters whenever you passed one, because a bare `ashlar keys init` writes to
`$ASHLAR_KEY_DIR`/`~/.ashlar/keys` instead:

```bash
ashlar keys init --key-dir "<the directory the refusal named>"
ashlar ledger reanchor --key-dir "<same directory>" --path <project> --yes
```

After either outcome the ledger verifies again, and the documents are re-certified over the new
history with `ashlar verify`.

## `ashlar run` — you cannot run what does not verify

```bash
ashlar run "classify the invoices in ./inbox"
```

`run` verifies the project first and executes only if every course passes. On failure it names the
course and stops:

```
refusing to run: course 'envelope' failed — <detail>
you cannot run what does not verify. fix it, then:  ashlar verify
```

Exit `65`, the same code `verify` uses. If there is no project in the directory at all, both commands
say so and point at `ashlar init <name>`.

## The self-extend dial

`ashlar.policy.yaml` starts `sealed`: a freshly deployed project changes nothing after deploy. The
dial has three positions, and raising it is a deliberate act, one node at a time.

```bash
ashlar policy show
ashlar policy set self_extend proposing
```

| Mode | What it means |
|------|---------------|
| `sealed` | Nothing changes after deploy. The default for every new project. |
| `proposing` | Self-extend cycles run but are **held for review** (`ashlar gates`). No auto-apply. |
| `self-extending` | Admitted cycles **auto-apply** within budget, gated by the post-apply canary. |

`policy show` prints the mode, the budget (`extensions per window`), `gatesRequired`, `mayAdd`, the
count of trusted signers, and a plain-English line about what the current mode implies.

`policy set` deliberately reaches only the dial. The governance floor — `never`, `sandbox`,
`trustedSigners` — is not editable through the command:

> unsupported key '…'. Only `self_extend` (the mode) can be set; the governance floor (never,
> sandbox, trustedSigners) is not editable through this command — edit ashlar.policy.yaml directly
> for those.

Editing the floor is an operator action on the file, in a review, with the diff visible. For the full
picture of running a node that extends itself unattended — the build course, the post-apply canary
and auto-rollback, budgets, and the `background-agent report` / `background-agent disarm` safety
front doors — see [`RunningASelfExtendingNode.md`](RunningASelfExtendingNode.md).

## `ashlar gates` — the held queue

In `proposing` mode, proposals wait for a person.

```bash
ashlar gates                          # list held proposals
ashlar gates --show <id>              # one proposal in full
ashlar gates --admit <id>             # seat the stone
ashlar gates --refuse <id> --reason "…"
```

`--reason` is required with `--refuse`; it is recorded and fed back to the proposer. `--as <who>`
records who decided (defaults to the current username). Reads never require a key — a mangled
operator key must not stop you seeing the queue — while admit/refuse sign the verdict when a key
exists.

## Sharing what a node admitted

Certified extensions move between nodes as signed `.ashpkg` packages, and every hop is re-gated by
the receiving node's own policy.

```bash
ashlar pkg export --id <extension> --out <file.ashpkg>   # seal an admitted extension
ashlar pkg show <file>                # verify and describe it, touching no project
ashlar pkg import <file>              # verify, then submit to THIS project's gate
ashlar pkg publish <file>             # place it in the mesh store for peers to pull
ashlar pkg share --id <extension>     # export + publish in one step
ashlar pkg pull --from <peer-store>   # pull from a peer, each through THIS project's gate
```

Trust between operators is fingerprint-based:

```bash
ashlar keys show                      # this machine's fingerprint, or that none exists
ashlar keys trust <fingerprint>       # trust a signer's packages on this machine
ashlar keys untrust <fingerprint>
ashlar keys peers                     # trusted fingerprints and the trust-set digest
```

`import` refuses a package from an untrusted signer and names the fix — adding the fingerprint to
`selfExtend.trustedSigners` in `ashlar.policy.yaml`. The peer-to-peer story is
[`Federation.md`](Federation.md).

## Shipping the project

```bash
ashlar export native                  # portable, self-proving single-file application bundle
```

The bundle carries `ashlar.yaml` and `ashlar.policy.yaml` alongside the binary, which is what lets
the recipient run their own `ashlar verify` against it — a tampered document fails the `provenance`
course on their machine, not yours. `export` refuses a directory that is not an Ashlar project.

## Command index

Every command below takes `--path <dir>` to point at a project other than the current directory, and
`--help` for its own options.

| Command | Description (from the CLI itself) |
|---------|-----------------------------------|
| `ashlar init <name>` | Scaffold a new project: `ashlar.yaml` and its operator-owned policy. |
| `ashlar verify` | Run the courses against this project and render the wall. |
| `ashlar run <request>` | Run a request through this project — verified first, then executed. |
| `ashlar policy show` / `set` | Inspect and set the project's self-extend policy dial. |
| `ashlar gates` | List held proposals; seat the stone or refuse, with a reason. |
| `ashlar keys` | Manage the local operator signing key. |
| `ashlar ledger status` / `reanchor` | Verify the signed instance ledger; re-anchor it when the operator accepts a loss. |
| `ashlar pkg` | Export, inspect, share, and import certified extension packages (`.ashpkg`). |
| `ashlar export native` | Export a certified project as a portable application bundle. |
| `ashlar new brick <Name>` | Scaffold a standalone brick project ([`AuthoringBricks.md`](AuthoringBricks.md)). |
| `ashlar mesh` | Peer discovery and admission ([`Federation.md`](Federation.md)). |
| `ashlar background-agent report` / `disarm` | What ran overnight; emergency stop ([`RunningASelfExtendingNode.md`](RunningASelfExtendingNode.md)). |

Run `ashlar --help` for the full root command list; this page covers the project lifecycle, not the
development and diagnostic commands ([`GettingStarted.md`](GettingStarted.md) covers those).

---

*The commands, options, exit codes and quoted output on this page were transcribed from
`application/src/Ashlar.CLI/Commands/` and `src/Ashlar.Manifest/` at the `0.1.1` line. They were not
re-executed while this page was written; where a message is quoted it is quoted from the source that
emits it.*
