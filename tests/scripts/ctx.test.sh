#!/usr/bin/env bash
# Tests scripts/ctx.py, the context query tool agents use instead of grepping and reading whole files.
#
# WHY. ctx is only worth running if what it prints is right: an agent that is told the wrong file, the
# wrong project or "no tests" goes on to build against it. So each answer an agent relies on is
# asserted against a small fixture repository whose truth is known: a definition and its line, the
# owning project and its solution filter, a source linked into two projects with <Compile Include>,
# production and test references kept apart, the AGENTS.md chain, a decisions-log row, a doc section,
# a knowledge-graph variable, and an index that picks up an edited file.
#
# THE FLOOR. The last check runs ctx on this repository itself and requires it to index at least
# MINIMUM_CS_FILES C# files, so a parser or file-listing regression that silently indexes nothing
# fails here instead of answering "no definition found" to every agent.
#
# Run:  bash tests/scripts/ctx.test.sh
# Python 3.8+ and git only: no network, no dotnet, no container.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
MINIMUM_CS_FILES=2000

# Probe by RUNNING the interpreter, not `command -v`: on Windows an App Execution Alias stub answers
# `command -v python3` and exits 49 without running anything.
PY_BIN=""
for cand in python3 python py; do
  if "${cand}" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 8) else 1)' >/dev/null 2>&1; then PY_BIN="${cand}"; break; fi
done
if [[ -z "${PY_BIN}" ]]; then
  echo "ctx.test: ERROR: no working python 3.8+ interpreter (tried python3, python, py)." >&2
  exit 1
fi

FIX="$(mktemp -d)"
trap 'rm -rf "${FIX}"' EXIT
FAILS=0
PASSES=0

expect() {  # expect <description> <needle> <haystack>
  if [[ "$3" == *"$2"* ]]; then PASSES=$((PASSES + 1)); else
    FAILS=$((FAILS + 1)); echo "FAIL: $1"; echo "  expected to contain: $2"; echo "  got:"; printf '%s\n' "$3" | sed 's/^/    /'; fi
}
reject() {  # reject <description> <needle> <haystack>
  if [[ "$3" != *"$2"* ]]; then PASSES=$((PASSES + 1)); else
    FAILS=$((FAILS + 1)); echo "FAIL: $1"; echo "  expected NOT to contain: $2"; fi
}

# ---------------------------------------------------------------------------------------- fixture
mkdir -p "${FIX}/scripts" "${FIX}/src/Lib" "${FIX}/src/Shared" "${FIX}/src/HostA" "${FIX}/src/HostB" \
         "${FIX}/tests/Lib.Tests" "${FIX}/docs/specs"
cp "${ROOT}/scripts/ctx.py" "${FIX}/scripts/ctx.py"

cat >"${FIX}/src/Lib/Lib.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net8.0;net10.0</TargetFrameworks></PropertyGroup></Project>
EOF
cat >"${FIX}/src/HostA/HostA.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><ProjectReference Include="..\Lib\Lib.csproj" /></ItemGroup>
  <ItemGroup><Compile Include="../Shared/Verifier.cs" Link="Security/Verifier.cs" /></ItemGroup>
</Project>
EOF
cat >"${FIX}/src/HostB/HostB.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk">
  <ItemGroup><Compile Include="..\Shared\Verifier.cs" /></ItemGroup>
</Project>
EOF
cat >"${FIX}/tests/Lib.Tests/Lib.Tests.csproj" <<'EOF'
<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><ProjectReference Include="../../src/Lib/Lib.csproj" /></ItemGroup></Project>
EOF
cat >"${FIX}/Core.slnf" <<'EOF'
{ "solution": { "path": "All.sln", "projects": [ "src/Lib/Lib.csproj" ] } }
EOF
cat >"${FIX}/src/Lib/Policy.cs" <<'EOF'
namespace Fixture.Lib;

// public sealed class CommentedOut { }
/*
public class AlsoCommentedOut { }
*/
[Obsolete("x")]
public sealed partial class ListenerPolicy
{
    private readonly int _count;
    public static bool IsLoopback(string host) => host == "127.0.0.1";
    public ListenerPolicy(int count, string name) { _count = count; }
    internal (int, string) Pair() => (1, "a");
}

internal record struct Endpoint(string Host, int Port);
EOF
cat >"${FIX}/src/Shared/Verifier.cs" <<'EOF'
namespace Fixture.Shared
{
    internal sealed class LoopbackVerifier
    {
        public void Verify() { var ok = ListenerPolicy.IsLoopback("x"); }
    }
}
EOF
cat >"${FIX}/src/HostA/Program.cs" <<'EOF'
namespace Fixture.HostA;
public static class Program { public static void Main() { var v = new LoopbackVerifier(); } }
EOF
cat >"${FIX}/tests/Lib.Tests/ListenerPolicyTests.cs" <<'EOF'
namespace Fixture.Tests;
public class ListenerPolicyTests { public void Loopback() => ListenerPolicy.IsLoopback("127.0.0.1"); }
EOF
cat >"${FIX}/src/AGENTS.md" <<'EOF'
# src rules
EOF
cat >"${FIX}/docs/specs/SPEC-X.md" <<'EOF'
# SPEC-X

## 1. Why

Because ListenerPolicy matters.

```
## not a heading inside a fence
```

## Decisions log

| Date | Applies to | Decision |
|---|---|---|
| 2026-10-05 | PR 4 (Q6) | **Inbound surfaces stay on loopback.** Boot fails otherwise. |
| 2026-10-06 | PR 5 | **Responses are mediated.** |

## 2. After
Tail.
EOF
cat >"${FIX}/docs/knowledge-graph.json" <<'EOF'
{"projects":[{"name":"Lib","path":"src/Lib/Lib.csproj","layer":"src","ships_to_nuget":true,"target_frameworks":["net10.0","net8.0"],"references":[]}],
 "config_variables":[{"name":"FIX_MODE","declared_in":["Lib"],"test_only":false,"in_configuration_reference":true,"in_any_document":true}]}
EOF
echo 'var m = Environment.GetEnvironmentVariable("FIX_MODE");' >"${FIX}/src/Lib/Env.cs"

git -C "${FIX}" init -q
git -C "${FIX}" add -A
git -C "${FIX}" -c user.name=t -c user.email=t@t commit -qm fixture

ctx() { "${PY_BIN}" "${FIX}/scripts/ctx.py" "$@" 2>&1; }

# ---------------------------------------------------------------------------------------- sym
out="$(ctx sym ListenerPolicy)"
expect "sym: definition with namespace, visibility, partial, file and line" \
  "class Fixture.Lib.ListenerPolicy  [public, partial]  src/Lib/Policy.cs:8" "${out}"
expect "sym: owning project with KG layer, frameworks and smallest solution filter" \
  "Lib  layer=src  tfm=net10.0,net8.0  nuget=yes  build=Core.slnf" "${out}"
expect "sym: production reference counted" "referenced in production code (1 files)" "${out}"
expect "sym: production reference names the linked file" "src/Shared/Verifier.cs" "${out}"
expect "sym: test reference kept apart" "tests/Lib.Tests/ListenerPolicyTests.cs" "${out}"
expect "sym: docs mention with line" "docs/specs/SPEC-X.md:5:" "${out}"
expect "sym: governing AGENTS.md of the defining folder" "src/AGENTS.md" "${out}"

out="$(ctx sym CommentedOut)"
expect "sym: a type inside a // comment is not a definition" "no definition found" "${out}"
out="$(ctx sym AlsoCommentedOut)"
expect "sym: a type inside a /* */ block is not a definition" "no definition found" "${out}"

out="$(ctx sym Endpoint)"
expect "sym: record struct parsed as one kind" "record struct Fixture.Lib.Endpoint  [internal]" "${out}"

out="$(ctx sym IsLoopback)"
expect "sym: member with an access modifier is found" "member IsLoopback  [public]  src/Lib/Policy.cs:11" "${out}"
out="$(ctx sym Pair)"
expect "sym: member returning a tuple is found" "member Pair  [internal]  src/Lib/Policy.cs:13" "${out}"
out="$(ctx sym name)"
reject "sym: a constructor parameter is not a member" "member name" "${out}"

out="$(ctx sym LoopbackVerifier)"
expect "sym: linked source is attributed to every project that links it" \
  "project (source linked into each, via <Compile Include>)" "${out}"
expect "sym: linking project A (forward slashes)" "HostA  layer=src" "${out}"
expect "sym: linking project B (backslashes)" "HostB  layer=src" "${out}"
expect "sym: project in no solution says so" "build=not in any .sln/.slnf" "${out}"

out="$(ctx sym Polic)"
expect "sym: a miss suggests similar type names" "ListenerPolicy" "${out}"

# ---------------------------------------------------------------------------------------- file
out="$(ctx file src/Lib/Policy.cs)"
expect "file: project" "project: Lib" "${out}"
expect "file: namespace (file-scoped)" "namespace: Fixture.Lib" "${out}"
expect "file: type with line" "class ListenerPolicy [public] :8" "${out}"
expect "file: tests that name its types" "tests/Lib.Tests/ListenerPolicyTests.cs" "${out}"
out="$(ctx file src/Shared/Verifier.cs)"
expect "file: block namespace" "namespace: Fixture.Shared" "${out}"
expect "file: linked source lists its projects" "project (linked into): HostA, HostB" "${out}"

# ---------------------------------------------------------------------------------------- project
out="$(ctx project Lib)"
expect "project: referenced by production project" "HostA" "${out}"
expect "project: referenced by test project" "Lib.Tests" "${out}"
expect "project: solution filter" "Core.slnf" "${out}"
out="$(ctx project Li)"
expect "project: a miss suggests similar names" "no project named Li" "${out}"

# ---------------------------------------------------------------------------------------- doc
out="$(ctx doc docs/specs/SPEC-X.md)"
expect "doc: outline with line numbers and section sizes" "    3    1. Why  (" "${out}"
reject "doc: a fenced line is not a heading" "not a heading inside a fence" "${out}"
out="$(ctx doc docs/specs/SPEC-X.md "decisions")"
expect "doc: section by heading substring, with its line range" "== docs/specs/SPEC-X.md:11-" "${out}"
expect "doc: section body" "Responses are mediated" "${out}"
reject "doc: section stops at the next heading of its level" "Tail." "${out}"

# ---------------------------------------------------------------------------------------- decisions
out="$(ctx decisions loopback)"
expect "decisions: matching row count" "matching loopback: 1" "${out}"
expect "decisions: row with file, line, date and scope" "docs/specs/SPEC-X.md:15  2026-10-05 | PR 4 (Q6)" "${out}"
reject "decisions: non-matching row excluded" "mediated" "${out}"
out="$(ctx decisions)"
expect "decisions: no terms lists every row, header and rule excluded" "rows matching (all): 2" "${out}"

# ---------------------------------------------------------------------------------------- env / find
out="$(ctx env FIX_MODE)"
expect "env: knowledge-graph entry" "declared in: Lib" "${out}"
expect "env: where it is read" "src/Lib/Env.cs:1:" "${out}"
out="$(ctx find "stay on loopback")"
expect "find: phrase search with per-file counts" "docs/specs/SPEC-X.md (1)" "${out}"

# ---------------------------------------------------------------------------------------- limits
out="$(ctx sym ListenerPolicy --limit 0)"
expect "--limit accepted after the subcommand" "== ListenerPolicy" "${out}"
out="$(ctx --limit 1 decisions)"
expect "--limit before the subcommand caps rows" "... 1 more" "${out}"

# ---------------------------------------------------------------------------------------- index freshness
cat >>"${FIX}/src/Lib/Policy.cs" <<'EOF'

public interface IFreshlyAdded { }
EOF
out="$(ctx sym IFreshlyAdded)"
expect "index: an edited file is re-parsed" "interface Fixture.Lib.IFreshlyAdded" "${out}"
printf 'namespace N;\npublic enum BrandNew { A }\n' >"${FIX}/src/Lib/New.cs"
out="$(ctx sym BrandNew)"
expect "index: an untracked new file is visible" "enum N.BrandNew  [public]  src/Lib/New.cs:2" "${out}"
reject "ctx writes nothing into the work tree" "ctx" "$(git -C "${FIX}" status --porcelain --untracked-files=all | grep -v 'src/Lib/')"

# ---------------------------------------------------------------------------------------- floor (this repo)
out="$("${PY_BIN}" "${ROOT}/scripts/ctx.py" reindex 2>&1)"
n="$(printf '%s' "${out}" | sed -n 's/^indexed \([0-9]*\) .*/\1/p')"
if [[ -n "${n}" && "${n}" -ge "${MINIMUM_CS_FILES}" ]]; then PASSES=$((PASSES + 1)); else
  FAILS=$((FAILS + 1)); echo "FAIL: ctx indexed '${n:-?}' C# files in this repository; floor is ${MINIMUM_CS_FILES}"; echo "${out}"; fi

echo "ctx.test: ${PASSES} passed, ${FAILS} failed"
[[ "${FAILS}" -eq 0 ]]
