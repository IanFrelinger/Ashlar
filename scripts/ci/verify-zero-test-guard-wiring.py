#!/usr/bin/env python3
"""Every pinned `dotnet test --filter` is followed by scripts/ci/zero-test-guard.sh over its own TRX.

WHY. VSTest exits 0 when a --filter selects nothing. scripts/ci/zero-test-guard.sh turns that into a
red lane, but only where it is called: a pinned filter added tomorrow without it is exactly as quiet
as the three that sat green on zero tests until this check existed (security-gate Tier B,
compat-gate Tier A step 1, application-gate Tier C). So the population is DERIVED, not listed:
every `dotnet test` in
  - .github/workflows/*.yml
  - scripts/**/*.sh and tests/**/*.sh (tests/uat/*.sh is run by uat-gate.yml)
  - the Makefile
  - deploy/compose/*.yml (compose-gate.yml brings up docker-compose.test.yml, whose service command
    runs a pinned filter; a folded `command: >` scalar is read as the one line the shell sees)
except the files in FIXTURE_TEXT, whose `dotnet test` lines are this check's own test inputs.

A filter is PINNED when it has at least one positive term (`=` or `~`, not `!=` / `!~`): it names
what it selects, so it can go stale. An exclusion-only filter (`FullyQualifiedName!~X`) selects "the
rest of the project" and is out of scope, as is a run with no filter at all. A filter this check
cannot resolve to text (a variable it cannot see) counts as pinned: the unknown direction is the one
that must not pass quietly.

For each pinned invocation it requires:
  1. a TRX of its own: `--logger "trx;LogFileName=<name>"`, the name unique in the file;
  2. `-f`/`--framework` when the project targets more than one framework - one invocation runs every
     target and each overwrites the previous TRX ("WARNING: Overwriting results file"), so a target
     that matched nothing leaves no trace in the file that survives (measured on the security-gate
     Tier B filter: the surviving net10.0 TRX said 44 executed while net8.0 had matched nothing);
  3. for a .sln/.slnf target, `LogFilePrefix=<p>` instead (one TRX per project and framework) and a
     guard call in `--solution` mode naming <p>;
  4. a LATER guard call naming that TRX, in the SAME unit that ran the test - the same Make target,
     the same workflow `run:` block, the same script. A guard in another Make target or another step
     runs in a different shell, possibly never. For a pinned run in a compose file, the unit is the
     workflow/script/Make `run` that brings the file up (`docker compose -f <file> ... up`): the guard
     must follow that line, because that is where the TRX lands on the host.
     "Naming" is read from the guard's ARGUMENTS, not from its line: the words after the guard's path,
     quotes removed, stopping at a word that starts with `#` (a trailing comment names nothing). A
     GitHub expression is compared as written on both sides (`${{ matrix.os }}` equals
     `${{matrix.os}}` and nothing else). One argument's basename must EQUAL the LogFileName - so
     `out/barfoo.trx` does not name foo.trx, and neither does `out/other.trx  # foo.trx`. In
     `--solution` mode one argument's basename must match `<p>_*` or `*__<p>_*` for LogFilePrefix <p>.
     Shell variables are not expanded: the TRX name must be written literally in the argument;
  5. that the guard call can fail its unit. It is refused when
     - it is not a command of its own: the line must start with `bash <path>/scripts/ci/zero-test-guard.sh`
       (so not `echo ...`, `if ...`, `! ...`). `scripts/cert-gate-zero-test-guard.sh` is not this guard;
     - it is text inside a heredoc body (`cat > x <<'EOF'` ... `EOF`): that is data for whatever reads
       the heredoc, not a command this unit runs;
     - it shares its line with a control operator - `||` (`|| true`, `|| echo ...`), `&&`, `;`, `|`
       or `&`. Each lets the line succeed while the guard fails: `x || y` runs y, `x && y` and `x; y`
       are exempt from `set -e` or report y's status, a pipeline reports its last command, and `&`
       reports nothing. A guard needs a line of its own;
     - a Make recipe line carries the `-` prefix (make prints "Error 1 (ignored)" and carries on);
     - `set -e` is not in force where it runs: in a script, the last `set -e`/`set +e` before it must
       be `-e`; in a workflow `run:` block (GitHub runs bash with -e) no `set +e` may precede it;
  6. that nothing between the test and its guard, in the same script or run block, ends the unit with
     success first: an `exit` or `return` in command position whose status is absent (`exit`: the
     last command's, which under set -e is the test's 0) or a literal 0 (`exit 0`, `exit "0"`). Lines
     inside heredoc bodies are not commands and are skipped. A Make recipe is exempt: each recipe
     line runs in a shell of its own, so an `exit 0` line ends only itself.

What it does NOT check - each of these would still let a guarded lane go quiet:
  - that the step or job is not `continue-on-error`, or that its `if:` ever lets it run, or that the
    lane runs on any trigger at all. It pins the wiring, not the reachability;
  - the step's shell. A `shell: pwsh` step (or a Windows runner's default) does not stop on a failing
    command mid-script. Every guarded step today declares `shell: bash` or runs on ubuntu;
  - control flow. A unit is read as a flat list of lines, not as a graph: a guard in a DIFFERENT
    branch of an if/else (or case arm) from its test is accepted, although it never runs after that
    test. So is a guard inside a shell function (never called, or called before the test), a subshell
    or a loop, and a `set +e` inside a function;
  - an `exit`/`return` between the test and its guard whose status is not a literal: `exit "$rc"`,
    `exit $?`. perf-certification.yml re-raises `exit "$rc"` inside `if [[ "$rc" -ne 0 ]]`, which is
    correct, and this check cannot tell it from `rc=0; exit "$rc"`. Nor does it see `exec`, `kill $$`,
    or an `exit 0` inside a function that is called between the two;
  - a heredoc it does not recognise as one: its delimiter must be a plain word (`EOF`, `'EOF'`,
    `"EOF"`, `\\EOF`, with or without `-`). A `dotnet test` inside a heredoc body IS still counted as a
    pinned run (the heredoc may be a script that is run), and a guard next to it inside the same body
    is refused - the fail-closed direction.

Callers it does NOT reach (a pinned filter there is invisible to it):
  - Dockerfile CMD lines (.docker/Dockerfile.test-*). Measured when this was written: no workflow runs
    one of those images with its default command - compose-gate.yml overrides it with the compose
    service's `command:`, test-trust-multi-env.yml and full-platform-readiness-gate.yml with
    `docker run <image> bash -c ...`;
  - C# that launches `dotnet test` (application/src/Ashlar.CLI/Commands/*, src/Ashlar.Infrastructure/**);
  - PowerShell (scripts/test-in-container.ps1, a developer harness) and python scripts;
  - a filter a workflow passes in through an input, an env var or a matrix value the scan cannot see
    (it counts as pinned and unresolvable, so it is refused rather than missed - but only if the
    `dotnet test` itself is in a scanned file).

Run:  python3 scripts/ci/verify-zero-test-guard-wiring.py            # the repository (all checks)
      python3 scripts/ci/verify-zero-test-guard-wiring.py --root DIR # a fixture tree (no floor,
                                                                     # no required lanes, no stale-
                                                                     # exemption check)
"""
import collections
import fnmatch
import os
import re
import sys

NL = chr(10)
CR = chr(13)
TAB = chr(9)
BS = chr(92)

GUARD_PATH = "scripts/ci/zero-test-guard.sh"

# Whole files or Make targets this check does not hold to the rule, each with its reason. Every entry
# must still match at least one pinned invocation on the real tree, so the list cannot rot quietly.
EXEMPT_FILES = [
    ("scripts/test-in-container.sh",
     "developer harness: forwards whatever filter the caller typed, runs no gate"),
    ("scripts/cert-gate-*.sh",
     "cert-gate's own scripts: cert-gate-config.sh derives the expected count with --list-tests "
     "(not a test run), and cert-gate has its own fail-closed guard, cert-gate-zero-test-guard.sh"),
    ("scripts/run-cert-gate.sh",
     "cert-gate's runner, guarded by scripts/cert-gate-zero-test-guard.sh"),
    ("scripts/validate-safe.sh",
     "local-only helper that no workflow reaches"),
    (".github/workflows/full-platform-readiness-gate.yml",
     "owned by the readiness gate's own change; its smoke lanes are not yet guarded"),
    ("tests/uat/tier0-2.sh",
     "uat-gate Tier 2 collects verdicts instead of exiting on the first failure, and it already FAILs "
     "unless the run log reports Total=16 (the count TesterQuickstart claims) and when no total can be "
     "read - an exact count, stronger than this guard, though Total also counts skipped tests. Its "
     "second pinned call is --list-tests, which runs nothing"),
]
EXEMPT_MAKE_TARGETS = [
    ("test-all-platforms", "local-only Docker matrix that no workflow reaches"),
    ("test-prime-time", "local-only solution-filter sweep that no workflow reaches"),
    ("test-mesh-lab", "local-only Docker mesh-lab run that no workflow reaches"),
]
# Files whose `dotnet test` lines are TEXT, not lanes. They are not counted as pinned invocations at
# all (so they cannot pad the floor below), and each must still match one, like an exemption.
FIXTURE_TEXT = [
    ("tests/scripts/zero-test-guard-wiring.test.sh",
     "this check's own tests: their heredocs are the shapes it must refuse or accept, written into "
     "throwaway trees"),
]

# The real tree must yield at least this many pinned invocations (fixture text excluded). A scanner
# that stops matching finds nothing and reports clean; this is the floor under that. It is a literal
# on purpose - lowering it is a visible diff, never a side effect of regenerating something. Set
# below the count at the time of writing: 106 (91 guarded, 15 exempt). Losing the workflows (37), the
# Makefile (34) or scripts/ (28) from the scan takes it below the floor.
MINIMUM_PINNED = 100

# Lanes this check must find wired on the real tree: the two this change repaired, and one per other
# kind of file it scans (a workflow, the Makefile, a compose file). Losing any from the scan is how
# the population rots back into a list. tests/**/*.sh is held by its exemption's stale check.
REQUIRED_WIRED = [
    ("scripts/security-gate-tier-b.sh", "api-security.trx"),
    ("scripts/compat-gate-tier-a.sh", "mesh-checkpoint-migration.trx"),
    (".github/workflows/mcp-a2a-gate.yml", "protocol-prodstyle.trx"),
    ("Makefile", "kernel-gate-hosting.trx"),
    ("deploy/compose/docker-compose.test.yml", "ubuntu-baseframework.trx"),
]

# The test images COPY the repository to /workspace (.docker/Dockerfile.test-caching*), so a project
# path under it is the same project in this tree.
CONTAINER_WORKSPACE = "/workspace/"

FILTER = re.compile(r"""--filter\s+(?:"([^"]*)"|'([^']*)'|(\S+))""")
FRAMEWORK = re.compile(r"""(?:^|\s)(?:-f|--framework)\s+["']?([^\s"']+)""")
# A GitHub expression inside a name (`smoke-${{ matrix.os }}.trx`) carries spaces, so it is one unit.
LOG_NAME = re.compile(r"LogFileName=((?:\$\{\{[^}]*\}\}|[^;\"'\s])+)")
LOG_PREFIX = re.compile(r"LogFilePrefix=((?:\$\{\{[^}]*\}\}|[^;\"'\s])+)")
SHELL_ASSIGN = re.compile(r"""^\s*(?:export\s+|readonly\s+)?([A-Za-z_][A-Za-z0-9_]*)=(?:"([^"]*)"|'([^']*)'|(\S*))""")
MAKE_ASSIGN = re.compile(r"^([A-Za-z_][A-Za-z0-9_]*)\s*(?::=|\?=|=)\s*(.*?)\s*$")
MAKE_TARGET = re.compile(r"^([A-Za-z0-9_.%-]+)\s*:(?!=)")
VAR_REF = re.compile(r"\$\{([A-Za-z_][A-Za-z0-9_]*)\}|\$\(([A-Za-z_][A-Za-z0-9_]*)\)|\$([A-Za-z_][A-Za-z0-9_]*)")
# `key: >` (a YAML folded scalar): its lines are one line to whatever reads the value.
FOLDED_KEY = re.compile(r"^(\s*(?:-\s+)?)[A-Za-z0-9_.-]+:\s*>[-+0-9]*\s*$")
RUN_KEY = re.compile(r"^(\s*(?:-\s+)?)run:\s*(.*)$")
BLOCK_SCALAR = re.compile(r"^[|>][-+0-9]*\s*(?:#.*)?$")
# The guard must be the command itself: `bash <anything>/scripts/ci/zero-test-guard.sh`, quoted or not.
GUARD_COMMAND = re.compile(r"""^bash\s+["']?(?:[^\s"']*/)?scripts/ci/zero-test-guard\.sh["']?(?:\s|$)""")
CONTROL = re.compile(r"\|\||&&|[|&;]")
SET_E = re.compile(r"(?:^|[\s;])set\s+(?:-[A-Za-z]*e[A-Za-z]*|-o\s+errexit)(?![A-Za-z])")
SET_NO_E = re.compile(r"(?:^|[\s;])set\s+(?:\+[A-Za-z]*e[A-Za-z]*|\+o\s+errexit)(?![A-Za-z])")
COMPOSE_CALL = re.compile(r"docker[ -]compose\s(.*)")
COMPOSE_FILE_ARG = re.compile(r"""(?:^|\s)(?:-f|--file)\s+["']?([^\s"']+)""")
COMPOSE_UP = re.compile(r"(?:^|\s)up(?:\s|$)")
# A heredoc operator (not `<<<`, a here-string) and the plain-word delimiter after it.
HEREDOC_OP = re.compile(r"(?<!<)<<(?!<)(-?)[ \t]*")
HEREDOC_WORD = re.compile(r"""(['"]?)\\?([A-Za-z_][A-Za-z0-9_.-]*)\1""")
# `exit` / `return` in command position: at the start of the line, or after an operator or keyword.
QUIT = re.compile(r"(?:^|[;&|({]|\bthen\b|\belse\b|\bdo\b)\s*(exit|return)(?=$|[\s;&|)}])")
QUIT_ARG = re.compile(r"""[ \t]*(?:"([^"]*)"|'([^']*)'|([^\s;&|)}#]+))?""")
GH_EXPR = re.compile(r"\$\{\{\s*(.*?)\s*\}\}")
# Unquoted, each of these ends a shell word and is a word of its own.
WORD_BREAK = ";&|<>"

Guard = collections.namedtuple("Guard", "idx text block defect args")


def rel(root, path):
    return os.path.relpath(path, root).replace(BS, "/")


def kind_of(name):
    if name == "Makefile":
        return "make"
    if name.startswith(".github/workflows/"):
        return "workflow"
    if name.startswith("deploy/compose/"):
        return "compose"
    return "shell"


def files_to_scan(root):
    out = []
    make = os.path.join(root, "Makefile")
    if os.path.isfile(make):
        out.append(make)
    for sub in ("scripts", "tests"):
        base = os.path.join(root, sub)
        for d, _, fs in os.walk(base):
            out += [os.path.join(d, f) for f in fs if f.endswith(".sh")]
    for sub, exts in ((os.path.join(".github", "workflows"), (".yml", ".yaml")),
                      (os.path.join("deploy", "compose"), (".yml", ".yaml"))):
        d = os.path.join(root, sub)
        if os.path.isdir(d):
            out += [os.path.join(d, f) for f in os.listdir(d) if f.endswith(exts)]
    return sorted(out)


def indent_of(line):
    return len(line) - len(line.lstrip(" "))


def logical_lines(text, folded=False):
    """Joins backslash-continued lines, and (folded=True) a YAML `key: >` scalar's lines.

    Returns [(first_line_number, text, raw_first_line)]."""
    lines = text.replace(CR + NL, NL).split(NL)
    out, buf, start, i = [], [], None, 0
    while i < len(lines):
        line = lines[i]
        if folded and start is None:
            m = FOLDED_KEY.match(line)
            if m:
                key_indent, j, body = len(m.group(1)), i + 1, []
                while j < len(lines) and (not lines[j].strip() or indent_of(lines[j]) > key_indent):
                    if lines[j].strip():
                        body.append(lines[j].strip())
                    j += 1
                out.append((i + 1, " ".join(body), line))
                i = j
                continue
        if start is None:
            start = i + 1
        stripped = line.rstrip()
        i += 1
        if stripped.endswith(BS):
            buf.append(stripped[:-1])
            continue
        buf.append(line)
        out.append((start, " ".join(s.strip() for s in buf), lines[start - 1]))
        buf, start = [], None
    if buf:
        out.append((start, " ".join(s.strip() for s in buf), lines[start - 1]))
    return out


def run_blocks(text):
    """Maps each physical line number of a workflow to the `run:` block it belongs to (or None)."""
    lines = text.replace(CR + NL, NL).split(NL)
    block, cur, cur_indent = {}, None, None
    for n, line in enumerate(lines, 1):
        if cur is not None:
            if not line.strip() or indent_of(line) > cur_indent:
                block[n] = cur
                continue
            cur = None
        m = RUN_KEY.match(line)
        if m:
            block[n] = n
            if BLOCK_SCALAR.match(m.group(2).strip()):
                cur, cur_indent = n, len(m.group(1))
    return block


def is_comment(raw):
    return raw.lstrip().startswith("#")


def variables(text, is_make):
    found = {}
    for line in text.replace(CR + NL, NL).split(NL):
        if is_make:
            if line.startswith(TAB):
                continue
            m = MAKE_ASSIGN.match(line)
            if m and m.group(1) not in found:
                found[m.group(1)] = m.group(2)
        else:
            m = SHELL_ASSIGN.match(line)
            if m and m.group(1) not in found:
                found[m.group(1)] = next(g for g in m.groups()[1:] if g is not None)
    return found


def resolve(token, found):
    for _ in range(4):
        new = VAR_REF.sub(lambda m: found.get(m.group(1) or m.group(2) or m.group(3), m.group(0)), token)
        if new == token:
            break
        token = new
    return token.strip("\"'")


def is_pinned(flt):
    if flt is None:
        return False
    if "$" in flt:
        return True  # unresolvable: fail closed
    return re.search(r"(?<![!])[=~]", flt) is not None


def target_of(segment, found):
    for tok in re.findall(r"""\"[^\"]*\"|'[^']*'|\S+""", segment):
        tok = tok.strip("\"'")
        if not tok or tok.startswith("-"):
            return None
        return resolve(tok, found)
    return None


def multi_target(root, project):
    if project.startswith(CONTAINER_WORKSPACE):
        project = project[len(CONTAINER_WORKSPACE):]
    path = os.path.join(root, project)
    if not os.path.isfile(path):
        return None
    with open(path, encoding="utf-8", errors="replace") as fh:
        text = fh.read()
    m = re.search(r"<TargetFrameworks>([^<]*)</TargetFrameworks>", text)
    return bool(m and ";" in m.group(1).strip().strip(";"))


def unquoted(cmd):
    """The command with quoted text, GitHub expressions and fd redirections blanked out."""
    cmd = re.sub(r"\$\{\{[^}]*\}\}", "X", cmd)
    cmd = re.sub(r'"(?:[^"\\]|\\.)*"', '""', cmd)
    cmd = re.sub(r"'[^']*'", "''", cmd)
    return re.sub(r"\d*[<>]&\d*-?|&>>?", " ", cmd)


def expr_norm(s):
    """GitHub expressions with their inner whitespace removed: `${{ matrix.os }}` becomes one word that
    equals itself however it was spaced, and nothing else (not `${{ matrix.label }}`)."""
    return GH_EXPR.sub(lambda m: "${{" + re.sub(r"\s+", "", m.group(1)) + "}}", s)


def masked(line):
    """The line with the inside of every quoted string replaced by `x` and a trailing comment cut off.
    Offsets are unchanged, so a match found in it is at the same place in the line, and nothing it
    finds was inside quotes."""
    out, i, n = list(line), 0, len(line)
    while i < n:
        c = line[i]
        if c == BS:
            i += 2
            continue
        if c == "'":
            j = line.find("'", i + 1)
        elif c == '"':
            j = i + 1
            while j < n and line[j] != '"':
                j += 2 if line[j] == BS else 1
        else:
            i += 1
            continue
        j = n if j < 0 or j > n else j
        for k in range(i + 1, j):
            out[k] = "x"
        i = j + 1
    text = "".join(out)
    m = re.search(r"(?:^|\s)#", text)
    return text[:m.start()] if m else text


def words(cmd):
    """The shell words of one command line: quotes removed; an unquoted `;`, `&`, `|`, `<` or `>` ends
    a word and is a word of its own; a `#` that starts a word starts a comment, which ends the line."""
    out, cur, inword, i, n = [], [], False, 0, len(cmd)
    while i < n:
        c = cmd[i]
        if c in (" ", TAB) or c in WORD_BREAK:
            if inword:
                out.append("".join(cur))
                cur, inword = [], False
            if c in WORD_BREAK:
                out.append(c)
            i += 1
            continue
        if c == "#" and not inword:
            break
        inword = True
        if c == "'":
            j = cmd.find("'", i + 1)
            j = n if j < 0 else j
            cur.append(cmd[i + 1:j])
            i = j + 1
        elif c == '"':
            j = i + 1
            while j < n and cmd[j] != '"':
                if cmd[j] == BS and j + 1 < n:
                    j += 1
                cur.append(cmd[j])
                j += 1
            i = j + 1
        elif c == BS and i + 1 < n:
            cur.append(cmd[i + 1])
            i += 2
        else:
            cur.append(c)
            i += 1
    if inword:
        out.append("".join(cur))
    return out


def command_of(kind, text, raw):
    """(command, make_prefix): the line without a workflow's `run:` key or a Make recipe's @+- prefix."""
    cmd = text.strip()
    if kind == "workflow":
        cmd = re.sub(r"^(?:-\s+)?run:\s*", "", cmd)
    prefix = ""
    if kind == "make" and raw.startswith(TAB):
        prefix = re.match(r"^[@+\-\s]*", cmd).group(0)
        cmd = cmd[len(prefix):]
    return cmd, prefix


def guard_args(kind, text, raw):
    """The words after the guard's path on its line; [] when no word is the guard's path."""
    toks = words(expr_norm(command_of(kind, text, raw)[0]))
    for _ in range(3):
        for i, t in enumerate(toks):
            if t == GUARD_PATH or t.endswith("/" + GUARD_PATH):
                return toks[i + 1:]
        # `echo "bash scripts/ci/zero-test-guard.sh out/x.trx"`: the call sits inside one word. Read
        # what it names there as well, so the refusal says this guard cannot fail, not that none exists.
        inner = next((t for t in toks if GUARD_PATH in t), None)
        if inner is None:
            return []
        toks = words(inner)
    return []


def names_trx(args, key, mode):
    """Whether one of a guard's arguments names this run's TRX: its basename EQUALS the LogFileName, or
    in --solution mode matches `<prefix>_*` or `*__<prefix>_*`. Never a substring of the line."""
    key = expr_norm(key)
    literal = re.sub(r"([*?\[])", r"[\1]", key)
    for a in args:
        if not a or a.startswith("-") or a in WORD_BREAK:
            continue
        base = a.rsplit("/", 1)[-1]
        if mode == "--solution":
            if fnmatch.fnmatchcase(base, literal + "_*") or fnmatch.fnmatchcase(base, "*__" + literal + "_*"):
                return True
        elif base == key:
            return True
    return False


def heredoc_bodies(text, kind, block_of):
    """{physical line number: the line its heredoc opened on}, for every line inside a heredoc body."""
    if kind not in ("shell", "workflow"):
        return {}  # a Make recipe line is a shell of its own; a compose command is folded into one line
    lines = text.replace(CR + NL, NL).split(NL)
    body, pending, cur = {}, [], None
    for n, line in enumerate(lines, 1):
        if cur is not None:
            delim, dash, opened, blk = cur
            if kind == "workflow" and block_of(n) != blk:
                cur, pending = None, []  # the run block ended first; this line is read normally below
            else:
                # A workflow's `run: |` block is de-indented by YAML before bash sees it.
                probe = line.strip() if kind == "workflow" else (line.lstrip(TAB) if dash else line)
                if probe == delim:
                    cur = pending.pop(0) if pending else None
                else:
                    body[n] = opened
                continue
        if (kind == "workflow" and block_of(n) is None) or is_comment(line):
            continue
        for m in HEREDOC_OP.finditer(masked(line)):
            w = HEREDOC_WORD.match(line, m.end())
            if w:
                pending.append((w.group(2), m.group(1) == "-", n, block_of(n)))
        if pending and not line.rstrip().endswith(BS):
            cur = pending.pop(0)
    return body


def quiet_quit(cmd):
    """The `exit`/`return` in this command that ends its shell with success (no status: the last
    command's, which under set -e is 0; or a literal 0), or None."""
    for m in QUIT.finditer(masked(cmd)):
        arg = QUIT_ARG.match(cmd, m.end())
        val = next((g for g in arg.groups() if g is not None), "")
        if re.fullmatch(r"0*", val):
            return (m.group(1) + " " + val).strip()
    return None


def guard_defect(kind, text, raw):
    """Why this guard line cannot fail its unit, or None when it can."""
    cmd, prefix = command_of(kind, text, raw)
    if "-" in prefix:
        return "make ignores its exit status (the `-` recipe prefix)"
    if not GUARD_COMMAND.match(cmd):
        return ("it is not a command of its own: the line must start with `bash .../" + GUARD_PATH + "` "
                "(under `echo`, `if`, `!` and the like its failure never fails the lane)")
    op = CONTROL.search(unquoted(cmd))
    if op:
        return (f"it shares its line with `{op.group(0)}`, which lets the line succeed while the guard "
                "fails; give the guard a line of its own")
    return None


def errexit_state(entries, upto, block_of, block, initial):
    """Whether `set -e` is in force at entry `upto`, reading the toggles before it in the same unit."""
    state = initial
    for i in range(upto):
        line_no, t, raw = entries[i]
        if is_comment(raw) or block_of(line_no) != block:
            continue
        marks = [(m.start(), True) for m in SET_E.finditer(t)] + [(m.start(), False) for m in SET_NO_E.finditer(t)]
        for _, on in sorted(marks):
            state = on
    return state


class Unit:
    """One scanned file: its logical lines, what unit each line runs in, and its guard calls."""

    def __init__(self, root, path):
        self.name = rel(root, path)
        self.kind = kind_of(self.name)
        with open(path, encoding="utf-8", errors="replace") as fh:
            self.text = fh.read()
        self.found = variables(self.text, self.kind == "make")
        self.entries = logical_lines(self.text, folded=self.kind == "compose")
        if self.kind == "workflow":
            blocks = run_blocks(self.text)
            self.block_of = lambda n: blocks.get(n)
        elif self.kind == "make":
            targets, target = {}, None
            for n, line in enumerate(self.text.replace(CR + NL, NL).split(NL), 1):
                m = MAKE_TARGET.match(line)
                if m and not line.startswith(TAB):
                    target = m.group(1)
                targets[n] = target
            self.block_of = lambda n: targets.get(n)
        else:
            self.block_of = lambda n: "file"
        self.heredoc = heredoc_bodies(self.text, self.kind, self.block_of)
        self.guards = []
        self.bringups = []
        for idx, (line_no, t, raw) in enumerate(self.entries):
            if is_comment(raw):
                continue
            if GUARD_PATH in t:
                self.guards.append(Guard(idx, t, self.block_of(line_no), self.defect(idx, t, raw),
                                         guard_args(self.kind, t, raw)))
            call = COMPOSE_CALL.search(t)
            if self.kind != "compose" and call and COMPOSE_UP.search(call.group(1)):
                for f in COMPOSE_FILE_ARG.findall(call.group(1)):
                    f = resolve(f, self.found)
                    self.bringups.append((f[2:] if f.startswith("./") else f, idx))

    def defect(self, idx, t, raw):
        line_no = self.entries[idx][0]
        if line_no in self.heredoc:
            return (f"it is text inside a heredoc body (opened at line {self.heredoc[line_no]}): data for "
                    "whatever reads the heredoc, not a command this unit runs")
        why = guard_defect(self.kind, t, raw)
        if why is not None:
            return why
        if self.kind == "shell" and not errexit_state(self.entries, idx, self.block_of, "file", False):
            return "`set -e` is not in force where it runs, so the script carries on when it fails"
        if self.kind == "workflow" and not errexit_state(
                self.entries, idx, self.block_of, self.block_of(line_no), True):
            return "a `set +e` earlier in its run block means the step carries on when it fails"
        return None

    def where(self, idx):
        return f"{self.name}:{self.entries[idx][0]}"

    def unit_label(self, block):
        if self.kind == "make":
            return f"Make target {block}"
        if self.kind == "workflow":
            return f"the run block at line {block}" if block is not None else "no run block"
        return "the script"

    def quit_between(self, after, until, block):
        """(entry, text) of an `exit`/`return` that ends the unit with success between two entries."""
        if self.kind == "make":
            return None  # each recipe line is a shell of its own: an `exit 0` line ends only itself
        for i in range(after + 1, until):
            line_no, t, raw = self.entries[i]
            if is_comment(raw) or line_no in self.heredoc or self.block_of(line_no) != block:
                continue
            quit_ = quiet_quit(command_of(self.kind, t, raw)[0])
            if quit_:
                return i, quit_
        return None

    def find_guard(self, after, key, mode):
        """None when a guard that can fail follows entry `after` in its unit; else why not."""
        block = self.block_of(self.entries[after][0])
        naming = [g for g in self.guards if g.idx > after and names_trx(g.args, key, mode)]
        same = [g for g in naming if block is not None and g.block == block]
        if not same:
            if naming:
                return (f"is guarded only in another unit ({self.where(naming[0].idx)}, "
                        f"{self.unit_label(naming[0].block)}); a guard counts only in the unit that ran the "
                        f"test ({self.unit_label(block)})")
            return f"is not followed by `bash {GUARD_PATH}` naming {key}"
        why = None
        for g in same:
            if g.defect is not None:
                why = why or f"is followed by a guard over {key} at {self.where(g.idx)} that cannot fail: {g.defect}"
                continue
            if mode and mode not in g.args:
                why = why or f"needs its guard over {key} in {mode} mode ({self.where(g.idx)})"
                continue
            quit_ = self.quit_between(after, g.idx, block)
            if quit_:
                why = why or (f"can end before its guard over {key} at {self.where(g.idx)} runs: `{quit_[1]}` at "
                              f"{self.where(quit_[0])} ends {self.unit_label(block)} with success first")
                continue
            return None
        return why


def scan(root, strict):
    problems, pinned_total, wired, exempt_hits, fixture_hits = [], 0, set(), {}, {}
    units = [Unit(root, p) for p in files_to_scan(root)]
    bringups = [(u, f, idx) for u in units for f, idx in u.bringups]
    for u in units:
        file_key = next((p for p, _ in EXEMPT_FILES if fnmatch.fnmatch(u.name, p)), None)
        fixture_key = next((p for p, _ in FIXTURE_TEXT if fnmatch.fnmatch(u.name, p)), None)
        names_seen = {}
        for idx, (line_no, t, raw) in enumerate(u.entries):
            if is_comment(raw) or "dotnet test" not in t:
                continue
            for seg in t.split("dotnet test")[1:]:
                fm = FILTER.search(seg)
                if not fm:
                    continue
                flt = resolve(next(g for g in fm.groups() if g is not None), u.found)
                if not is_pinned(flt):
                    continue
                if fixture_key is not None:
                    fixture_hits[fixture_key] = fixture_hits.get(fixture_key, 0) + 1
                    continue
                pinned_total += 1
                where = f"{u.name}:{line_no}"
                if file_key is not None:
                    exempt_hits[("file", file_key)] = exempt_hits.get(("file", file_key), 0) + 1
                    continue
                if u.kind == "make":
                    target = u.block_of(line_no)
                    if any(tg == target for tg, _ in EXEMPT_MAKE_TARGETS):
                        exempt_hits[("make", target)] = exempt_hits.get(("make", target), 0) + 1
                        continue

                proj = target_of(seg, u.found)
                prefix = LOG_PREFIX.search(seg)
                logname = LOG_NAME.search(seg)
                if proj is None or "$" in proj:
                    problems.append(f"{where}: cannot resolve which project this pinned run targets ({proj!r}); "
                                    "the unknown direction is not allowed to pass")
                    continue
                if proj.endswith((".sln", ".slnf")):
                    if not prefix:
                        problems.append(f"{where}: solution-wide pinned run needs LogFilePrefix=<p> so each project "
                                        "writes its own TRX (one LogFileName is overwritten project by project)")
                        continue
                    key, mode = prefix.group(1), "--solution"
                else:
                    if not logname:
                        problems.append(f"{where}: pinned filter {flt!r} writes no TRX of its own; add "
                                        '--logger "trx;LogFileName=<unique>.trx" and guard it')
                        continue
                    key, mode = logname.group(1), None
                    if key in names_seen:
                        problems.append(f"{where}: LogFileName={key} is also used at line {names_seen[key]}; "
                                        "a shared name silently overwrites the earlier run's results")
                    names_seen[key] = line_no
                    if multi_target(root, proj) and not FRAMEWORK.search(seg):
                        problems.append(f"{where}: {proj} targets several frameworks and this run pins none; each "
                                        "target overwrites the previous one's TRX. Pin -f, one invocation per framework")

                if u.kind == "compose":
                    # The TRX lands on the host of whoever brought the service up; the guard goes there.
                    sites = [(b, i) for b, f, i in bringups if f == u.name]
                    if not sites:
                        problems.append(f"{where}: pinned filter {flt!r} runs in a compose service that no "
                                        f"workflow, script or Make target brings up with `docker compose -f "
                                        f"{u.name} ... up`, so there is no place its guard could run")
                        continue
                    faults = [f"{where}: pinned filter {flt!r}, brought up at {b.where(i)}, {why}"
                              for b, i in sites for why in [b.find_guard(i, key, mode)] if why]
                    problems += faults
                    if not faults:
                        wired.add((u.name, key))
                    continue
                why = u.find_guard(idx, key, mode)
                if why:
                    problems.append(f"{where}: pinned filter {flt!r} {why}")
                    continue
                wired.add((u.name, key))

    if strict:
        if pinned_total < MINIMUM_PINNED:
            problems.append(f"found {pinned_total} pinned invocations, expected at least {MINIMUM_PINNED}: the scan "
                            "has stopped matching, so a clean result would mean nothing. Fix the scan; do not "
                            "lower the floor")
        for f, key in REQUIRED_WIRED:
            if (f, key) not in wired:
                problems.append(f"required lane {f} ({key}) was not found wired; the scan no longer reaches it")
        for p, reason in EXEMPT_FILES:
            if not exempt_hits.get(("file", p)):
                problems.append(f"stale exemption {p} ({reason}): it matches no pinned invocation any more; remove it")
        for tg, reason in EXEMPT_MAKE_TARGETS:
            if not exempt_hits.get(("make", tg)):
                problems.append(f"stale exemption Makefile:{tg} ({reason}): it matches no pinned invocation any more; "
                                "remove it")
        for p, reason in FIXTURE_TEXT:
            if not fixture_hits.get(p):
                problems.append(f"stale fixture-text entry {p} ({reason}): it matches no `dotnet test` any more; "
                                "remove it")
    return problems, pinned_total, len(wired), sum(exempt_hits.values()), sum(fixture_hits.values())


def main(argv):
    root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
    strict = True
    if len(argv) == 3 and argv[1] == "--root":
        root, strict = os.path.abspath(argv[2]), False
    elif len(argv) != 1:
        print("usage: verify-zero-test-guard-wiring.py [--root DIR]", file=sys.stderr)
        return 2
    problems, pinned, wired, exempt, fixture = scan(root, strict)
    for p in problems:
        print(f"zero-test-guard-wiring: ERROR: {p}")
    print(f"zero-test-guard-wiring: {pinned} pinned invocation(s): {wired} guarded, {exempt} exempt, "
          f"{len(problems)} problem(s); {fixture} more in fixture text, not lanes")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
