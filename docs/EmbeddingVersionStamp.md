# Stamping an embedding with the generator that produced it

**Status: design, not implemented.** Nothing in this document has been built. It exists so that the
decision can be reviewed before the code is written, because the migration detail in section 6 is
the kind that is cheap to get right on paper and expensive to get wrong in a released schema.

---

## 1. Why now, and what changed

PR #582 fixed a per-process hash in the local embedding generators: a token was seeded with
`string.GetHashCode()`, which .NET randomizes at every process start, so a vector written in one run
scored as noise in the next. It landed FNV-1a in both generators, pinned them with golden constants,
and added a dimension guard to both vector stores.

It also declined the stamp, and said why:

> it would require putting a version identity on the public `IEmbeddingGenerator` interface and
> threading it through `RAGService`, and it would buy nothing for the only two states that exist
> today … If a persistent store is ever wired up for real, a version column is the right thing to
> add then, alongside a migration.

That reasoning is the charter for this document, and it sets the bar: #582 declined the stamp
because it bought nothing **today**. So the question this design has to answer first is what has
changed. Three things have.

**(a) The dimension guard is known to be insufficient, in writing.** The changelog entry for #582
already records the limit: a pre-fix row written at the shipped default dimension has the right
blob length, passes the guard untouched, and scores as noise. Dimension cannot see an algorithm
change. Nothing else can either.

**(b) A third store exists, and it is the one the hosts search.** `AshlarKernelRegistrar` phase 13b
registers `MeaiVectorDataRagAdapter` as `IRAGService` unconditionally, so both shipped hosts search
`InProcessChunkCollection` in `src/Ashlar.AI.Pipeline/Rag/InProcessVectorStore.cs`, not either
`Ashlar.BackgroundAgents` store. Any stamp that covers only the interface #582 named leaves the
generator that actually ships unstamped. Section 4 is about that.

**(c) The rest of the "score 0.0 means three different things" family has now been closed.** The
zero-norm work removed the other two ways a store returned a number where there was no number:
a zero-magnitude vector and a length mismatch now say so out of band rather than arriving as the
value 0.0. A stale-embedding row is the one remaining member of that family that the store still
cannot see, and it is the only one that needs information the vectors themselves do not carry.

**What has NOT changed: there is nothing on disk to migrate.** See section 7. That is load-bearing
for the whole proposal and is also, itself, a defect.

---

## 2. What a stamp has to identify

Three things vary independently in this tree, and a stamp that misses any of them is a stamp that
says two incompatible corpora are the same.

**Algorithm.** The hash basis, the mixer, the float mapping, the normalization. This is what #582
changed, and it is invisible to every other check.

**Dimension.** Already guarded at read time in both `Ashlar.BackgroundAgents` stores and, since the
zero-norm work, in the third store too. Including it in the stamp is still right, and the reason is
not redundancy: the guard and the stamp fail at different moments and produce different messages.
The guard silently drops a row mid-query; the stamp refuses the store at open time and names both
values. Only one of those is diagnosable.

**Tokenizer.** The two generators tokenize differently and incompatibly. `TokenEmbeddingGenerator`
keeps letters, digits and apostrophes and lowercases. `TokenHashEmbeddingGenerator` splits on a
fixed punctuation set. Same text, same dimension, same hash function, different bags of tokens. A
stamp recording only a model name would call these two identical.

**Proposal: a composite opaque string**, compared by ordinal equality — for example a generator
identity, an algorithm version and a dimension joined into one token such as
`ashlar.token-embed/v1/d64`.

**Argued against: a hash of the generator's configuration.** It is tempting because it cannot be
forgotten. It is wrong because the value's only job on the day it matters is to appear in an error
message an operator has to act on, and a hex digest tells them nothing about which two things
disagreed or which way to re-index.

---

## 3. Ashlar's own `IEmbeddingGenerator`

`Ashlar.BackgroundAgents.RAG.IEmbeddingGenerator` has exactly one implementation in the whole tree
(`TokenEmbeddingGenerator`), one consumer (`RAGService`), one registration, and no test fake. So the
in-repo cost of adding a member is one property on one class.

The cost is external. `Ashlar.BackgroundAgents` is packed and shipped by the packaging scripts, so
this is a public API a third party can implement, and adding a required member is a source-breaking
change for them. That project has no `PublicAPI.Shipped.txt` — only six projects do — so the public
API analyzer would not flag it, and there is no ApiCompat gate that would either.

**Proposal: a default interface member, not an abstract one.** Both target frameworks are net8.0 and
net10.0, so default interface members are available. The default returns a conservative "unknown"
identity; `TokenEmbeddingGenerator` overrides it with its real one.

**Say this out loud in the review:** no gate in this repository would have caught the breaking
version of this change. The choice is a judgement being made here, not one CI will enforce.

---

## 4. The generator that actually ships

The interface #582 named is not the one in the hot path. The live generator in every `AddAshlar`
host implements `Microsoft.Extensions.AI.IEmbeddingGenerator<string, Embedding<float>>`, which
Ashlar cannot add a member to. Its only identity channel is the `GetService(Type, object?)` escape
hatch, which `TokenHashEmbeddingGenerator` already implements.

It is also wrapped twice. `SanitizingEmbeddingGenerator` wraps `AuditingEmbeddingGenerator` wraps
`TokenHashEmbeddingGenerator`, both derived from `DelegatingEmbeddingGenerator` and neither
overriding `GetService`. So a `GetService`-based identity read has to traverse two decorators to
reach the only object that knows the answer.

**It already traverses them, and an earlier draft of this section got that wrong.** That draft
proposed adding `GetService` forwarding to both decorators. There is no forwarding to add:
`DelegatingEmbeddingGenerator<,>.GetService` in Microsoft.Extensions.AI 10.9.0 already returns
`this` when the requested type matches and otherwise hands the call to the inner generator, and
because neither Ashlar decorator overrides it, both inherit exactly that. Measured in the devtest
container on net8.0, composing the chain from
`MeaiPipelineServiceCollectionExtensions.RegisterVectorDataRag` (lines 146–156) by hand:
`GetService(typeof(TokenHashEmbeddingGenerator))` on the outermost decorator returned the innermost
generator through two inherited forwards, `GetService(typeof(AuditingEmbeddingGenerator))` returned
the middle one, `GetService(typeof(string))` returned null, and reflection reports `GetService` on
both Ashlar decorators as declared on `Microsoft.Extensions.AI.DelegatingEmbeddingGenerator<,>`. The
premise above — *neither overriding `GetService`* — was true; the conclusion drawn from it was not.

That error is worth more than the afternoon it would have cost, because section 10's mutation was
written against the phantom work. An implementer who adds two no-op overrides and then deletes one
to check the test has teeth gets a test that is **green in both states**, since the base class
supplies the same behaviour either way — the exact shape `docs/HowGatesGoQuiet.md` exists to name,
reached through a design document instead of through code.

**Proposal: two mechanisms, deliberately.** A default interface member on Ashlar's own interface; a
`GetService(typeof(EmbeddingIdentity))` lookup for the MEAI one. The only code that second mechanism
needs is in `TokenHashEmbeddingGenerator.GetService`, which today answers
`serviceType.IsInstanceOfType(this) ? this : null` and so returns null for an identity type. The
decorators deliver the question to it unchanged and need no edit at all.

**The test that matters here** reads the identity through the fully composed, DI-registered
generator — not off a bare `TokenHashEmbeddingGenerator`, because the composed read is the one a
caller performs. Be clear about what it can pin, though: it pins
`TokenHashEmbeddingGenerator.GetService` answering the identity type, and it pins the chain being
composed at all. It does **not** pin decorator forwarding, because no code in this repository
performs that forwarding — MEAI does. The honest mutation is in section 10.

---

## 5. Where the stamp is checked, and what happens when it disagrees

`IVectorStore.SearchAsync` returns a list and has no failure channel. Every existing mismatch path
is a silent `continue`. That leaves two bad options and one good one.

Throwing per row from `SearchAsync` is a behaviour change for `RAGService`, `RAGTool` and
`MeaiVectorDataRagAdapter`, none of which expects an exception from a query — though note the
zero-norm work has already established the pattern for a *query-level* refusal and hardened
`RAGTool` to report one, so the shape is no longer unfamiliar.

Silently skipping every row is worse: it is indistinguishable from an empty corpus, which is the
exact symptom #582 was diagnosed from ("Expected collection to contain 1 item(s), but found 0").

**Proposal: refuse at OPEN time, not at row time.** The schema initialization reads the distinct
stamps present once and throws if any stored stamp disagrees with the configured generator's. The
operator gets one clear error naming both stamps and the re-index instruction, before a single
query has silently under-returned. Row-level handling stays "ignore", because a single odd row
should not take a corpus down — but if a row is ever skipped for this reason, the count must
surface somewhere, or the original defect comes back in a new costume.

**Semantics for the value itself — mirror the precedent this repository already has.**
`CertificationRecordData.SchemaVersion` is nullable, where null means the legacy lane;
`CertificationRecordSigning.IsKnownSchemaVersion` accepts null or the current version and refuses
anything else; an unknown version is refused rather than ignored. The asymmetry there — throw on
write, typed failure on read — is the right shape here too.

One deviation from that precedent, and it needs stating: **null must map to REFUSE, not accept.**
A pre-stamp row was written by a generator whose identity is unrecoverable, because (in the
changelog's words for #582) the basis that produced those vectors left with the process that chose
it. "Written before the stamp existed" is not a compatibility claim. It is an admission that no
claim can be made.

---

## 6. The migration, which is the detail most likely to be got wrong

`rag_vectors` is created in exactly one place, as `CREATE TABLE IF NOT EXISTS` with four columns.
There is **no migration mechanism of any kind** in `Ashlar.BackgroundAgents`: no `PRAGMA
user_version`, no schema-version row, no `ALTER TABLE`, no version check anywhere.

Two consequences, both easy to walk into:

1. Because the statement is `IF NOT EXISTS`, **adding a fifth column to it silently does nothing to
   an existing file.** The reader's `SELECT` would then throw "no such column" at search time, on
   every database that already exists.
2. SQLite **rejects `ALTER TABLE ADD COLUMN` with `NOT NULL` and no default on a non-empty table.**
   The column must be nullable — which section 5 already wants, for a different reason.

**Proposal:** inside the initialization path, read `PRAGMA table_info(rag_vectors)`, and when the
stamp column is absent, `ALTER TABLE ... ADD COLUMN <stamp> TEXT NULL` before any read happens.

**Test it by writing a four-column database by hand and opening it.** Not by creating one with the
new code and reopening it — that exercises the `IF NOT EXISTS` path and passes whether or not the
`ALTER TABLE` exists.

---

## 7. Nothing is on disk today, and the reason is a second defect

The honest answer to "what happens to rows already written" is: there are none, and that is not
reassuring.

`SqliteVectorStore` is constructed at exactly three sites, all of them in its own test file.
`AddBackgroundAgentsRAG` hardwires `InMemoryVectorStore`. And `RAGConfig.VectorStoreProvider` is
validated non-empty by the config loader — it fails with "RAG enabled but no provider specified" —
and then **never read to select a store**. An agent configuration declaring `"sqlite"` is accepted,
validated, and silently served an in-memory store. A shipped example does exactly that.

So: the `ALTER TABLE` path in section 6 exists for third parties and for the future, and must be
tested against a hand-written database because no shipped composition can produce one.

This is also the sharpest thing in this document, and it is worth separating from the stamp: the
provider-is-ignored defect is why #582's "buys nothing today" argument holds, and **fixing it is
what would make the stamp observable end to end.** Whoever picks this up should consider doing that
first — the stamp is hard to justify while the only store that could hold a stale row cannot be
selected.

---

## 8. Scope and the layer boundary

Every change proposed here is under `src/`: the two generators, the two BackgroundAgents RAG types,
the AI.Pipeline generator and its decorators. Nothing needs to move under `application/`, which
matters because `.github/workflows/layer-boundary.yml` refuses `application/` changes on a `master`
base unless every changed path is a test project.

The RAG command-line surface does live under `application/`, and if provider selection (section 7)
is taken up, **the store-selection logic belongs in the service-collection extension in `src/` where
`AddBackgroundAgentsRAG` already is** — no CLI change is needed for it. Stated explicitly so nobody
discovers the refusal the hard way.

---

## 9. What CI would and would not run

Verified against the live workflow, not inferred from its filters, because the filters mislead.

**These tests do run.** Not through any `dotnet test` line you can grep for — the four literal ones
in `.github/workflows/full-platform-readiness-gate.yml` use filters that cannot select a RAG test.
They run through the `ci verify` step in the native-platform matrix (Linux, macOS, Windows), which
reaches `ashlar validate`, which discovers and runs **every** project whose name or directory
contains "test". Confirmed against a real failing run, not by reading code.

**Three limits on that lane, each of which a claim in this design must respect.** Only the
native-platform matrix runs `ci verify`; the container lanes run two smoke filters, so the RAG suite
is not exercised there. Validation runs **one** target framework per project, net8.0 for a
multi-target project — so net10.0 is never covered for these tests by any lane, and any measurement
recorded here should say which framework it came from. And the readiness summary is a required check
on `master`.

**A path gap that should be closed in the same work.** `src/Ashlar.AI.Pipeline/` and
`src/Ashlar.Tests.AI.Pipeline/` are in **neither** of the readiness gate's two path lists — not the
push paths, not the readiness paths the change-detection job uses — while the BackgroundAgents
equivalents are in both. The only other runner named for that project is `make-test`, and no
workflow invokes `make test`. So a pull request touching only the generator that is live in every
host fires no lane that runs its tests, and #582's golden constants for that generator have never
been exercised by CI on their own change. That is a two-line fix with a measurable before and after,
and it belongs here rather than in a separate ticket, because otherwise this design writes down a
test strategy for a file CI does not watch.

**If a merge-blocking assertion is wanted,** note that `cert-gate` is the one required check that
runs on every pull request with no path filter — and per `scripts/cert-gate-config.sh` its filter
selects by namespace. An assertion outside that namespace is not merge-blocking, and moving or
renaming the namespace disarms it silently.

---

## 10. The mutations each proposed test must survive

Stated here so the implementation cannot ship a test that passes in both states, which this
repository has done twice. Per `docs/HowGatesGoQuiet.md`, an inventory-style guard needs **both**
facts, and a test whose interesting assertion is a refusal needs a positive control beside it.

A mutation is only worth writing down once someone has checked that the code it deletes exists.
The row about the generator chain originally said "delete the `GetService` forwarding from either
decorator", and there is no such forwarding to delete — see section 4. A proposed mutation against
code that does not exist is a test that cannot fail, written in advance.

| Proposed assertion | Mutation that must make it fail |
| --- | --- |
| A pre-existing four-column database opens and is readable | Delete the `ALTER TABLE`; it must fail to open |
| Identity survives the composed generator chain | Make `TokenHashEmbeddingGenerator.GetService` stop answering the identity type (it is the only object in the chain that knows it) |
| A mismatched stamp is refused at open time | Make the comparison accept anything |
| A null stamp is refused | Make null accept |
| The current stamp is accepted | — this is the positive control for the four above; without it, "refuse everything" passes them all |
| A new unstamped call site is caught | Add one; it must fail |
| A stale inventory row is caught | Delete the call the row describes; it must fail |

---

## 11. Deliberately not proposed

- **A stamp on the in-memory stores.** They cannot hold a row across a generator change, so the
  stamp would be unfalsifiable there and would read as coverage.
- **Re-embedding on mismatch.** The store does not have the source text for a row it cannot rank,
  and inventing one is worse than refusing.
- **A registry of known-good stamp pairs.** Two stamps are either equal or they are not. A
  compatibility matrix is a thing to maintain and a thing to get wrong.
- **Removing the dimension guards.** They fail earlier and cheaper than the stamp, and section 2
  explains why the two are not redundant.
