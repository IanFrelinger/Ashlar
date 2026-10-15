---
name: ashlar-ctx
description: Find where a type, member, file, project, config variable or owner decision lives in Ashlar, and read one doc section instead of a whole spec. Use before grepping, before opening files to orient, and before reading any doc over ~150 lines.
---

# ashlar-ctx

`scripts/ctx.py` answers orientation questions in one bounded call. Use it instead of `grep -r`, `find`,
or reading whole files and specs. It reads `git ls-files` (plus untracked, not-ignored files), the csproj
graph, the solution filters and `docs/knowledge-graph.json`. Its C# index is cached under `.git/ctx/` and
re-parses only changed files. It writes nothing to the work tree.

Run it with `python scripts/ctx.py ...`. On Windows, `python3` may be the Store stub.

| Question | Command |
|---|---|
| Where is `X` defined, who uses it, which tests, which project and filter builds it? | `ctx sym X [Y ...]` |
| What is in this file, and which tests name its types? | `ctx file path/to/File.cs` |
| What does this project reference, what references it, which `.slnf` builds it? | `ctx project Ashlar.Infrastructure` |
| What sections does a long doc have, and how long is each? | `ctx doc docs/specs/SPEC-007-security-labels-and-reference-monitor.md` |
| Read one section only | `ctx doc <path> "Decisions log"` (heading substring) |
| What did the owner decide about Z? | `ctx decisions loopback` (all terms must match) |
| Where is an `ASHLAR_*` variable read and documented? | `ctx env ASHLAR_EGRESS_MODE` |
| Which files mention a phrase, most hits first? | `ctx find "exit 77"` |

Each section prints at most 12 rows. Pass `--limit 0` only when you need everything.

## Rules

1. **Orient with ctx, then open only what it points to.** Open files at the line it prints, and read
   about 60 lines around it rather than the whole file.
2. **Never read a spec, runbook or handoff end to end.** Run `ctx doc <path>` for the outline, then read
   only the sections your task names.
3. **Build the smallest thing.** The `build=` field of `ctx sym` and the filter list of `ctx project`
   name the smallest `.slnf` that contains the project. Pass that to the container test script instead
   of `Ashlar.sln`. `build=not in any .sln/.slnf` means the project needs its own csproj path.
4. **Owner decisions come from `ctx decisions`, quoted with their file and line.** If no row matches,
   the owner has not decided. Ask; do not infer one.
5. **ctx is an index, not a compiler.** It parses declarations with regular expressions. When the
   answer matters (overloads, nested types, partial classes split across files), confirm in the file it
   names.

## Writing a brief for another agent

Run `ctx sym` on the types the task touches and paste only the `defined`, `project` and
`referenced in tests` lines into the brief, with the files to change and the test filter. A brief that
names its files saves the receiving agent from exploring.
