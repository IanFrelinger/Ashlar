#!/usr/bin/env python
"""ctx: answer "where is X, what governs it, how do I test it" in one bounded call.

WHY. An agent working in this repository (Codex, Claude, Grok, Cursor) otherwise rebuilds its map of the
code every session by grepping, opening files and reading whole specs. On a tree of ~3,300 C# files
(16 MB) and 735-line specs, that orientation costs more than the change itself. ctx answers the common
orientation questions with a few dozen lines of output each, so an agent reads only the files it then
needs.

WHAT IT READS. `git ls-files` (plus untracked, not-ignored files, so a file an agent just created is
visible), every *.csproj and *.sln/*.slnf, `docs/knowledge-graph.json` (generated, see CLAUDE.md), and
the C# sources. The C# symbol index is cached under the git directory (`<git-common-dir>/ctx/`), keyed
per file on size and mtime, so only changed files are re-parsed. Nothing in the working tree is written.

HOW IT PARSES. Line-oriented regular expressions, not Roslyn: type declarations (class, record, struct,
interface, enum) and members declared with an explicit access modifier. It is an index for finding
things, not a compiler; when it matters, open the file at the line it prints.

Commands (each prints at most --limit rows per section; pass --limit 0 for all):
  sym NAME [NAME ...]    definitions, owning project, solution filters, referencing files (prod/test),
                         docs that mention it, and the AGENTS.md / CLAUDE.md that govern its folder
  file PATH              project, namespace, the types it declares, tests that name them
  project NAME           path, layer, frameworks, references in and out, test projects, solution filters
  doc PATH [HEADING]     outline of a markdown file with line numbers, or one section's text
  decisions [TERM ...]   rows of every "Decisions log" table in the docs, filtered by all terms
  env VAR                a configuration variable from the knowledge graph, and where it is read
  find TERM              bounded word search across tracked text files

Python 3.8+, standard library only. Run as `python scripts/ctx.py ...` (on Windows `python3` may be the
Store stub; use `python`).
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
from pathlib import Path, PurePosixPath

INDEX_VERSION = 3
DEFAULT_LIMIT = 12

TYPE_RE = re.compile(
    r"^\s*(?:\[[^\]]*\]\s*)*"
    r"(?P<mods>(?:(?:public|internal|private|protected|file|static|sealed|abstract|partial|readonly|ref|unsafe|new)\s+)*)"
    r"(?P<kind>record\s+struct|record\s+class|record|class|struct|interface|enum)\s+"
    r"(?P<name>[A-Za-z_]\w*)"
)
MEMBER_RE = re.compile(
    r"^\s*(?P<vis>public|internal|protected|private)\s+"
    r"(?:(?:static|virtual|override|abstract|sealed|async|extern|new|readonly|partial|unsafe|required|const|event|implicit|explicit|operator)\s+)*"
    r"(?P<type>(?:\([^()]*\)|[\w.]+(?:<[^;{}()]*>)?)(?:\[\])*\??)\s+"
    r"(?P<name>[A-Za-z_]\w*)\s*(?:<[^>()]*>)?\s*(?P<tail>[({=;]|=>)"
)
NS_RE = re.compile(r"^\s*namespace\s+([\w.]+)")
VIS_WORDS = ("public", "internal", "private", "protected", "file")
NOT_MEMBER_TYPES = {"return", "new", "throw", "await", "using", "if", "while", "for", "foreach", "switch", "case", "else", "var"}


# ----------------------------------------------------------------------------------------------- git

def git(root: Path, *args: str) -> str:
    out = subprocess.run(["git", "-C", str(root), *args], stdout=subprocess.PIPE, stderr=subprocess.DEVNULL)
    return out.stdout.decode("utf-8", errors="replace")


def repo_root() -> Path:
    here = Path(__file__).resolve().parent
    out = subprocess.run(["git", "-C", str(here), "rev-parse", "--show-toplevel"], stdout=subprocess.PIPE,
                         stderr=subprocess.DEVNULL)
    top = out.stdout.decode().strip()
    if not top:
        sys.exit("ctx: not inside a git work tree")
    return Path(top)


def list_files(root: Path) -> list[str]:
    tracked = git(root, "ls-files", "-z").split("\0")
    untracked = git(root, "ls-files", "-z", "--others", "--exclude-standard").split("\0")
    seen, files = set(), []
    for f in tracked + untracked:
        if f and f not in seen and (root / f).is_file():
            seen.add(f)
            files.append(f)
    return files


def git_grep(root: Path, term: str, pathspecs: list[str], word: bool = True, files_only: bool = False) -> list[str]:
    args = ["grep", "--untracked", "-I", "-F"]
    if word:
        args.append("-w")
    args.append("-l" if files_only else "-n")
    args += ["-e", term, "--", *pathspecs]
    return [l for l in git(root, *args).splitlines() if l]


def cache_dir(root: Path) -> Path:
    common = git(root, "rev-parse", "--git-common-dir").strip()
    d = Path(common)
    if not d.is_absolute():
        d = root / d
    d = d / "ctx"
    d.mkdir(parents=True, exist_ok=True)
    return d


# ----------------------------------------------------------------------------------------------- index

def read_text(path: Path) -> str:
    data = path.read_bytes()
    if data.startswith(b"\xef\xbb\xbf"):
        data = data[3:]
    return data.decode("utf-8", errors="replace")


def parse_cs(text: str) -> dict:
    ns, types, members = "", [], []
    in_block_comment = False
    for i, raw in enumerate(text.splitlines(), 1):
        line = raw
        if in_block_comment:
            if "*/" in line:
                in_block_comment = False
                line = line.split("*/", 1)[1]
            else:
                continue
        stripped = line.lstrip()
        if stripped.startswith("//") or not stripped:
            continue
        if stripped.startswith("/*") and "*/" not in stripped:
            in_block_comment = True
            continue
        if not ns:
            m = NS_RE.match(line)
            if m:
                ns = m.group(1)
                continue
        m = TYPE_RE.match(line)
        if m:
            mods = m.group("mods").split()
            vis = " ".join(w for w in mods if w in VIS_WORDS) or "default"
            kind = " ".join(m.group("kind").split())
            types.append([m.group("name"), kind, i, vis, "partial" in mods])
            continue
        m = MEMBER_RE.match(line)
        if m and m.group("type").strip().split()[-1] not in NOT_MEMBER_TYPES:
            members.append([m.group("name"), i, m.group("vis")])
    return {"ns": ns, "types": types, "members": members}


def load_index(root: Path, files: list[str]) -> dict:
    path = cache_dir(root) / "index.json"
    old = {}
    try:
        data = json.loads(path.read_text(encoding="utf-8"))
        if data.get("version") == INDEX_VERSION:
            old = data.get("files", {})
    except (OSError, ValueError):
        pass
    new, changed = {}, False
    for f in files:
        if not f.endswith(".cs"):
            continue
        try:
            st = (root / f).stat()
        except OSError:
            continue
        key = [st.st_size, int(st.st_mtime)]
        prev = old.get(f)
        if prev and prev.get("k") == key:
            new[f] = prev
            continue
        entry = parse_cs(read_text(root / f))
        entry["k"] = key
        new[f] = entry
        changed = True
    if changed or len(new) != len(old):
        tmp = path.with_suffix(".tmp")
        tmp.write_text(json.dumps({"version": INDEX_VERSION, "files": new}, separators=(",", ":")), encoding="utf-8")
        os.replace(tmp, path)
    return new


# -------------------------------------------------------------------------------------------- projects

PROJREF_RE = re.compile(r'<ProjectReference\s+Include="([^"]+)"', re.I)
TFM_RE = re.compile(r"<TargetFrameworks?>([^<]+)</TargetFrameworks?>", re.I)
COMPILE_RE = re.compile(r'<Compile\s+Include="([^"*]+\.cs)"', re.I)
SLN_PROJ_RE = re.compile(r'^Project\("[^"]*"\)\s*=\s*"[^"]*",\s*"([^"]+\.csproj)"', re.M)


def norm(p: str) -> str:
    parts = []
    for seg in p.replace("\\", "/").split("/"):
        if seg in ("", "."):
            continue
        if seg == ".." and parts:
            parts.pop()
        else:
            parts.append(seg)
    return "/".join(parts)


class Repo:
    def __init__(self, root: Path):
        self.root = root
        self.files = list_files(root)
        self.fileset = set(self.files)
        self._index = None
        self._kg = None
        self._projects = None
        self._solutions = None

    @property
    def index(self) -> dict:
        if self._index is None:
            self._index = load_index(self.root, self.files)
        return self._index

    @property
    def kg(self) -> dict:
        if self._kg is None:
            try:
                self._kg = json.loads(read_text(self.root / "docs/knowledge-graph.json"))
            except (OSError, ValueError):
                self._kg = {}
        return self._kg

    @property
    def projects(self) -> dict:
        """name -> {path, dir, refs:[name], tfms, kg:{...}|None}"""
        if self._projects is None:
            kg_by_name = {p["name"]: p for p in self.kg.get("projects", [])}
            projects, by_path = {}, {}
            for f in self.files:
                if not f.endswith(".csproj"):
                    continue
                name = PurePosixPath(f).stem
                text = read_text(self.root / f)
                d = str(PurePosixPath(f).parent)
                refs = [norm(d + "/" + r) for r in PROJREF_RE.findall(text)]
                tfm = TFM_RE.search(text)
                linked = [norm(d + "/" + c) for c in COMPILE_RE.findall(text)]
                linked = [c for c in linked if not c.startswith(d + "/")]
                projects[name] = {"name": name, "path": f, "dir": d, "ref_paths": refs, "linked": linked,
                                  "tfms": tfm.group(1).strip() if tfm else None, "kg": kg_by_name.get(name)}
                by_path[f] = name
            for p in projects.values():
                p["refs"] = [by_path.get(r, PurePosixPath(r).stem) for r in p["ref_paths"]]
            for p in projects.values():
                p["referenced_by"] = sorted(q["name"] for q in projects.values() if p["name"] in q["refs"])
            self._projects = projects
        return self._projects

    @property
    def solutions(self) -> dict:
        """solution file -> set of csproj paths"""
        if self._solutions is None:
            sols = {}
            for f in self.files:
                if f.endswith(".slnf"):
                    try:
                        data = json.loads(read_text(self.root / f))
                        sols[f] = {norm(p) for p in data.get("solution", {}).get("projects", [])}
                    except ValueError:
                        pass
                elif f.endswith(".sln"):
                    d = str(PurePosixPath(f).parent)
                    sols[f] = {norm(d + "/" + p) for p in SLN_PROJ_RE.findall(read_text(self.root / f))}
            self._solutions = sols
        return self._solutions

    def projects_of(self, path: str) -> list:
        """The project whose folder holds the file, else every project that links it with <Compile Include>."""
        best = None
        for p in self.projects.values():
            d = p["dir"]
            if d in ("", ".") or path.startswith(d + "/"):
                if best is None or len(d) > len(best["dir"]):
                    best = p
        if best:
            return [best]
        return [p for p in self.projects.values() if path in p["linked"]]

    def solutions_of(self, csproj: str) -> list[str]:
        sols = [s for s, ps in self.solutions.items() if csproj in ps]
        return sorted(sols, key=lambda s: (not s.endswith(".slnf"), len(self.solutions[s]), s))

    def governing_docs(self, path: str) -> list[str]:
        out, parts = [], PurePosixPath(path).parts[:-1]
        for i in range(len(parts) + 1):
            d = "/".join(parts[:i])
            for name in ("AGENTS.md", "CLAUDE.md"):
                cand = f"{d}/{name}" if d else name
                if cand in self.fileset:
                    out.append(cand)
        return out


def is_test_path(path: str) -> bool:
    low = path.lower()
    return "/tests/" in "/" + low or ".tests" in low or low.startswith("tests/") or low.endswith("tests.cs")


# ----------------------------------------------------------------------------------------------- output

class Out:
    def __init__(self, limit: int):
        self.limit = limit

    def section(self, title: str, rows: list[str], empty: str = "none"):
        print(f"{title}:")
        if not rows:
            print(f"  ({empty})")
            return
        shown = rows if self.limit == 0 else rows[: self.limit]
        for r in shown:
            print(f"  {r}")
        if len(shown) < len(rows):
            print(f"  ... {len(rows) - len(shown)} more (--limit 0 for all)")


def clip(s: str, n: int = 150) -> str:
    s = " ".join(s.split())
    return s if len(s) <= n else s[: n - 1] + "…"


# ----------------------------------------------------------------------------------------------- commands

def cmd_sym(repo: Repo, out: Out, names: list[str]):
    for qi, name in enumerate(names):
        if qi:
            print()
        defs, members = [], []
        for f, e in repo.index.items():
            for t in e["types"]:
                if t[0] == name:
                    defs.append((f, t, e["ns"]))
            for m in e["members"]:
                if m[0] == name:
                    members.append((f, m))
        print(f"== {name}")
        if not defs and not members:
            near = sorted({t[0] for e in repo.index.values() for t in e["types"] if name.lower() in t[0].lower()})
            out.section("no definition found; similar type names", near, "no similar names")
            continue
        rows = [f"{t[1]} {e_ns + '.' if e_ns else ''}{t[0]}  [{t[3]}{', partial' if t[4] else ''}]  {f}:{t[2]}"
                for f, t, e_ns in defs]
        rows += [f"member {name}  [{m[2]}]  {f}:{m[1]}" for f, m in members]
        out.section("defined", rows)
        owners, linked_note = [], False
        for f in sorted({f for f, *_ in defs} | {f for f, _ in members}):
            ps = repo.projects_of(f)
            if ps and not f.startswith(ps[0]["dir"] + "/"):
                linked_note = True
            for p in ps:
                if p not in owners:
                    owners.append(p)
        prows = []
        for p in owners:
            kg = p["kg"] or {}
            tfms = ",".join(kg.get("target_frameworks", [])) or p["tfms"] or "inherited"
            sols = repo.solutions_of(p["path"])
            prows.append(f"{p['name']}  layer={kg.get('layer', p['path'].split('/')[0])}  tfm={tfms}"
                         f"  nuget={'yes' if kg.get('ships_to_nuget') else 'no'}  build={sols[0] if sols else 'not in any .sln/.slnf'}")
        out.section("project (source linked into each, via <Compile Include>)" if linked_note else "project", prows)
        def_files = {f for f, *_ in defs} | {f for f, _ in members}
        refs = [f for f in git_grep(repo.root, name, ["*.cs"], files_only=True) if f not in def_files]
        prod = [f for f in refs if not is_test_path(f)]
        tests = [f for f in refs if is_test_path(f)]
        out.section(f"referenced in production code ({len(prod)} files)", prod)
        out.section(f"referenced in tests ({len(tests)} files)", tests)
        docs = [clip(l) for l in git_grep(repo.root, name, ["*.md", ":!docs/knowledge-graph.md", ":!CHANGELOG.md"])]
        out.section(f"mentioned in docs ({len(docs)} lines; CHANGELOG excluded)", docs)
        gov = []
        for f in sorted(def_files):
            for g in repo.governing_docs(f):
                if g not in gov:
                    gov.append(g)
        out.section("governing instructions", gov)


def cmd_file(repo: Repo, out: Out, path: str):
    path = norm(path)
    if path not in repo.fileset:
        sys.exit(f"ctx: {path} is not a tracked or untracked-but-not-ignored file")
    ps = repo.projects_of(path)
    print(f"== {path}")
    linked = ps and not path.startswith(ps[0]["dir"] + "/")
    print(f"project{' (linked into)' if linked else ''}: {', '.join(p['name'] for p in ps) or '(none)'}")
    e = repo.index.get(path)
    if e:
        print(f"namespace: {e['ns'] or '(none)'}")
        out.section("types", [f"{t[1]} {t[0]} [{t[3]}] :{t[2]}" for t in e["types"]])
        out.section("members with an access modifier", [f"{m[0]} [{m[2]}] :{m[1]}" for m in e["members"]])
        tests = set()
        for t in e["types"]:
            tests.update(f for f in git_grep(repo.root, t[0], ["*.cs"], files_only=True) if is_test_path(f) and f != path)
        out.section("test files naming its types", sorted(tests))
    out.section("governing instructions", repo.governing_docs(path))


def cmd_project(repo: Repo, out: Out, name: str):
    p = repo.projects.get(name)
    if not p:
        near = sorted(n for n in repo.projects if name.lower() in n.lower())
        out.section(f"no project named {name}; similar", near)
        return
    kg = p["kg"] or {}
    print(f"== {name}")
    print(f"path: {p['path']}")
    print(f"layer: {kg.get('layer', p['path'].split('/')[0])}   tfm: {','.join(kg.get('target_frameworks', [])) or p['tfms'] or 'inherited'}"
          f"   nuget: {'yes' if kg.get('ships_to_nuget') else 'no'}   in knowledge graph: {'yes' if kg else 'no'}")
    out.section("references", p["refs"])
    prod_in = [n for n in p["referenced_by"] if not is_test_path(repo.projects[n]["path"])]
    test_in = [n for n in p["referenced_by"] if is_test_path(repo.projects[n]["path"])]
    out.section("referenced by (production)", prod_in)
    out.section("referenced by (tests)", test_in)
    out.section("solutions and filters (smallest filter first)", repo.solutions_of(p["path"]))
    cs = [f for f in repo.index if f.startswith(p["dir"] + "/")]
    print(f"files: {len(cs)} .cs")
    out.section("governing instructions", repo.governing_docs(p["path"]))


HEAD_RE = re.compile(r"^(#{1,6})\s+(.*\S)\s*$")


def md_outline(lines: list[str]):
    heads, fence = [], False
    for i, l in enumerate(lines, 1):
        if l.lstrip().startswith("```"):
            fence = not fence
            continue
        if not fence:
            m = HEAD_RE.match(l)
            if m:
                heads.append((i, len(m.group(1)), m.group(2)))
    return heads


def cmd_doc(repo: Repo, out: Out, path: str, heading: str | None):
    path = norm(path)
    if path not in repo.fileset:
        sys.exit(f"ctx: {path} is not in the repository")
    lines = read_text(repo.root / path).splitlines()
    heads = md_outline(lines)
    if not heading:
        print(f"== {path} ({len(lines)} lines)")
        for i, lvl, text in heads:
            end = next((j for j, l2, _ in heads if j > i and l2 <= lvl), len(lines) + 1)
            print(f"  {i:>5}  {'  ' * (lvl - 1)}{text}  ({end - i} lines)")
        return
    q = heading.lower()
    match = [h for h in heads if q in h[2].lower()]
    if not match:
        sys.exit(f"ctx: no heading in {path} contains '{heading}'. Run without a heading for the outline.")
    i, lvl, text = match[0]
    end = next((j for j, l2, _ in heads if j > i and l2 <= lvl), len(lines) + 1)
    if len(match) > 1:
        print(f"(first of {len(match)} matching headings: " + "; ".join(f"{h[2]} :{h[0]}" for h in match[1:4]) + ")")
    print(f"== {path}:{i}-{end - 1}")
    body = lines[i - 1:end - 1]
    cap = 0 if out.limit == 0 else max(out.limit * 10, 120)
    for l in body if cap == 0 else body[:cap]:
        print(l)
    if cap and len(body) > cap:
        print(f"... {len(body) - cap} more lines (--limit 0 for the whole section)")


def cmd_decisions(repo: Repo, out: Out, terms: list[str]):
    rows = []
    for f in repo.files:
        if not f.endswith(".md"):
            continue
        try:
            lines = read_text(repo.root / f).splitlines()
        except OSError:
            continue
        heads = md_outline(lines)
        for i, lvl, text in heads:
            if "decisions log" not in text.lower():
                continue
            end = next((j for j, l2, _ in heads if j > i and l2 <= lvl), len(lines) + 1)
            for n in range(i, end - 1):
                l = lines[n]
                if not l.startswith("|") or set(l.replace("|", "").strip()) <= set("-: ") or l.lower().startswith("| date"):
                    continue
                if all(t.lower() in l.lower() for t in terms):
                    rows.append((f, n + 1, l))
    print(f"== decisions log rows matching {' '.join(terms) or '(all)'}: {len(rows)}")
    shown = rows if out.limit == 0 else rows[: out.limit]
    for f, n, l in shown:
        cells = [c.strip() for c in l.strip().strip("|").split("|")]
        width = 0 if out.limit == 0 else 400
        print(f"- {f}:{n}  {' | '.join(cells[:2])}")
        print(f"  {clip(' | '.join(cells[2:]), width) if width else ' | '.join(cells[2:])}")
    if len(shown) < len(rows):
        print(f"... {len(rows) - len(shown)} more (add terms, or --limit 0)")


def cmd_env(repo: Repo, out: Out, var: str):
    entry = next((v for v in repo.kg.get("config_variables", []) if v["name"] == var), None)
    print(f"== {var}")
    if entry:
        print(f"declared in: {', '.join(entry.get('declared_in', []))}   test only: {entry.get('test_only')}"
              f"   in configuration reference: {entry.get('in_configuration_reference')}")
    else:
        print("not in docs/knowledge-graph.json")
    hits = git_grep(repo.root, var, ["*.cs", "*.sh", "*.ps1", "*.yml", "*.yaml", "*.json", ":!docs/knowledge-graph.json"])
    out.section(f"read or set at ({len(hits)} lines)", [clip(h) for h in hits])
    docs = git_grep(repo.root, var, ["*.md", ":!docs/knowledge-graph.md"])
    out.section(f"documented at ({len(docs)} lines)", [clip(h) for h in docs])


def cmd_find(repo: Repo, out: Out, term: str):
    hits = git_grep(repo.root, term, ["."], word=False)
    hits = [h for h in hits if not h.startswith("docs/knowledge-graph.")]
    by_file = {}
    for h in hits:
        by_file.setdefault(h.split(":", 1)[0], []).append(h)
    print(f"== '{term}': {len(hits)} lines in {len(by_file)} files")
    rows = [f"{f} ({len(v)})  {clip(v[0].split(':', 2)[-1], 110)}" for f, v in sorted(by_file.items(), key=lambda kv: -len(kv[1]))]
    out.section("files, most hits first", rows)


def main(argv=None):
    common = argparse.ArgumentParser(add_help=False)
    common.add_argument("--limit", type=int, default=argparse.SUPPRESS, help="rows per section; 0 for all")
    ap = argparse.ArgumentParser(prog="ctx", description=__doc__.split("\n\n")[0], parents=[common])
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("sym", parents=[common]); s.add_argument("names", nargs="+")
    s = sub.add_parser("file", parents=[common]); s.add_argument("path")
    s = sub.add_parser("project", parents=[common]); s.add_argument("name")
    s = sub.add_parser("doc", parents=[common]); s.add_argument("path"); s.add_argument("heading", nargs="?")
    s = sub.add_parser("decisions", parents=[common]); s.add_argument("terms", nargs="*")
    s = sub.add_parser("env", parents=[common]); s.add_argument("var")
    s = sub.add_parser("find", parents=[common]); s.add_argument("term")
    s = sub.add_parser("reindex", parents=[common])
    a = ap.parse_args(argv)
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    repo, out = Repo(repo_root()), Out(getattr(a, "limit", DEFAULT_LIMIT))
    if a.cmd == "sym":
        cmd_sym(repo, out, a.names)
    elif a.cmd == "file":
        cmd_file(repo, out, a.path)
    elif a.cmd == "project":
        cmd_project(repo, out, a.name)
    elif a.cmd == "doc":
        cmd_doc(repo, out, a.path, a.heading)
    elif a.cmd == "decisions":
        cmd_decisions(repo, out, a.terms)
    elif a.cmd == "env":
        cmd_env(repo, out, a.var)
    elif a.cmd == "find":
        cmd_find(repo, out, a.term)
    elif a.cmd == "reindex":
        print(f"indexed {len(repo.index)} .cs files")


if __name__ == "__main__":
    try:
        main()
    except BrokenPipeError:  # `ctx ... | head` closes the pipe early; that is not an error
        os.dup2(os.open(os.devnull, os.O_WRONLY), sys.stdout.fileno())
        sys.exit(0)
