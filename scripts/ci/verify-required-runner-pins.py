#!/usr/bin/env python3
"""Every job behind a REQUIRED status check runs on a pinned runner image, never a floating label.

WHY. A `-latest` runner label is not a version; it is a pointer GitHub moves. From 2026-10-19 to
2026-11-19 `ubuntu-latest` moves from Ubuntu 24.04 to Ubuntu 26.04 (actions/runner-images#14748),
and with it the tools under this repository's required checks: shellcheck 0.9.0 -> 0.11.0 (the
linter shell-lint runs), Python 3.12.3 -> 3.14.4 (every repo gate), Docker Compose 2.38.2 -> 5.1.3.
None of that is a commit here, so none of it is reviewed, and for the month the rollout lasts two
runs of the SAME commit can land on different images. A required check that goes red that way
blocks every pull request in the repository (enforce_admins=true, no bypass) with a diff that
explains nothing. Pinning makes an image upgrade a pull request someone reads.

SCOPE. The five workflows whose jobs sit behind master's required status checks (REQUIRED below,
checked against `gh api repos/IanFrelinger/Ashlar/branches/master/protection/required_status_checks`
on 2026-09-30). EVERY job in those files is judged, not only the one that owns the required context:
"Readiness summary" `needs:` every readiness lane, so a lane on a moving image moves the required
verdict with it. Other workflows are advisory and out of scope here.

WHAT IS JUDGED. Each job's `runs-on`. A `${{ matrix.<key> }}` reference is resolved to every value
the job's `strategy.matrix` gives that key, at matrix level and in each `include:` entry, and each
value is judged where it is written. A list or a `labels:` mapping is judged label by label.

PINNED means a label that names one image family, in one of these shapes:
    ubuntu-NN.NN[-arm]                     e.g. ubuntu-24.04
    macos-NN[-intel|-large|-xlarge]        e.g. macos-26
    windows-NNNN-vsNNNN                    e.g. windows-2025-vs2026
Everything else is refused, and the refusal says why:
  - anything containing `latest` (any case; runner labels are case-insensitive);
  - `windows-NNNN` alone. It names the Windows Server release but not the Visual Studio image, and
    GitHub has moved it between them: `windows-2025` was the VS2022 image until June 2026 and is the
    VS2026 one since (actions/runner-images#14017, rolled out 2026-06-08 to 2026-06-15; #14004 saw
    some jobs moved from 2026-05-05; the README only caught up on 2026-07-06, 69bb04343d98);
  - an expression this check cannot evaluate (`${{ inputs.x }}`, `${{ fromJSON(...) }}`, a matrix
    key the matrix does not define, a matrix built from an expression);
  - any other label (self-hosted, a larger-runner group, `ubuntu-slim`): unknown is not pinned.
A pinned label still receives the weekly image rebuild within its family (runs of 2026-09-30 on
`ubuntu-latest` reported both ubuntu-24.04 20260920.314.1 and 20260927.320.1). GitHub-hosted runners
offer no way to pin an image build; the family is what can be pinned, and it is what moves under -latest.

THE ONLY EXCEPTION is a YAML comment on the line of the value itself, in the workflow file:
    runs-on: ubuntu-slim  # runner-pin: allow <why this job may float, in words>
    - os: macos-latest    # runner-pin: allow <why>
The reason is required. There is no central baseline: an exception lives, and is reviewed, next to
the label it excuses. An exception on a line whose labels are all pinned is refused as stale, and a
`runner-pin:` comment on any other line of these files is refused as stray, because it excuses nothing
while looking as though it does. On a flow list (`os: [a, b]`) one comment covers the whole line. For
`runs-on: ${{ matrix.os }}` the values are judged, so the comment goes on the matrix value's line.
It must be a COMMENT: the same text inside a value (a quoted string such as `label: "x # runner-pin:
allow ..."`, or a `run: |` script) is data, excuses nothing, and is refused wherever it appears.
PyYAML keeps no comments, so a comment is found by elimination (comment_starts below): the first `#`
that follows whitespace or starts the line and lies outside every scalar the YAML scanner reads.

NON-VACUITY. A scanner that stops matching finds nothing and reports clean. So: each of the five
files must exist and parse, and must still contain the job whose name is the required context; the
real tree must yield at least MINIMUM_LABELS judged labels and at least one of each OS family in
REQUIRED_FAMILIES (the readiness matrix is the only place macOS and Windows appear, so losing the
matrix resolution fails here rather than passing having judged two fewer labels).

What it does NOT check:
  - that a pin equals what its -latest label resolved to. That is a fact about GitHub on a date, not
    about this tree; it was read from the "Set up job" step of real runs when the pins were set;
  - tools a job installs itself (setup-dotnet `10.0.x`, setup-python, `apt-get install`), or
    container images a lane pulls or builds (`mcr.microsoft.com/dotnet/sdk:10.0` is also a tag that
    moves). A pinned runner does not pin those;
  - the runner of a reusable workflow a job calls (such a job is refused, since its runner is not
    visible here), or of anything outside the five files;
  - any workflow outside REQUIRED.

Run:  python3 scripts/ci/verify-required-runner-pins.py             # the repository (all checks)
      python3 scripts/ci/verify-required-runner-pins.py --root DIR  # a fixture tree (no floor, no
                                                                    # required OS families)
"""
import os
import re
import sys

try:
    import yaml
except ImportError:  # pragma: no cover - shell-lint installs PyYAML before the repo gates run
    print("required-runner-pins: ERROR: PyYAML is not installed (pip install pyyaml); refusing to "
          "report clean without having read a workflow", file=sys.stderr)
    raise SystemExit(2)

PREFIX = "required-runner-pins"

# Workflow file -> the required status-check context its jobs report. Branch protection requires
# exactly these five (2026-09-30). The context must still be a job name in the file, or the scan has
# lost what it is for.
REQUIRED = [
    (".github/workflows/cert-gate.yml", "cert-gate"),
    (".github/workflows/build-gate.yml", "build-core"),
    (".github/workflows/shell-lint.yml", "shell-lint"),
    (".github/workflows/docs-link-check.yml", "lychee (README + docs)"),
    (".github/workflows/full-platform-readiness-gate.yml", "Readiness summary"),
]

# Judged labels on the real tree when this was written: 12 (one job each in four files; in the
# readiness gate `changes`, three native lanes, the container, docker-cli, docker-all-images and
# summary jobs). A literal on purpose, and set under that count so removing one lane is not a red
# build - but losing a file or the matrix resolution is.
MINIMUM_LABELS = 10
REQUIRED_FAMILIES = ("ubuntu", "macos", "windows")

PINNED = [
    re.compile(r"ubuntu-\d{2}\.\d{2}(-arm)?"),
    re.compile(r"macos-\d{2}(-intel|-large|-xlarge)?"),
    re.compile(r"windows-\d{4}-vs\d{4}"),
]
WINDOWS_OS_ONLY = re.compile(r"windows-\d{4}")
MATRIX_REF = re.compile(r"\$\{\{\s*matrix\.([A-Za-z_][A-Za-z0-9_-]*)\s*\}\}")
MARKER = re.compile(r"#\s*runner-pin:")
EXCEPTION = re.compile(r"#\s*runner-pin:\s*allow\b(.*)$")


def classify(label):
    """(verdict, reason) for one literal label. verdict is 'pinned' or 'refused'."""
    low = label.strip().lower()
    if "${{" in low:
        return "refused", "is an expression this check cannot evaluate statically"
    if "latest" in low:
        return "refused", ("is a floating -latest label: GitHub moves it to a newer image without a commit "
                           "here (ubuntu-latest, for one, moves to ubuntu-26.04 from 2026-10-19)")
    if WINDOWS_OS_ONLY.fullmatch(low):
        return "refused", ("names the Windows Server release but not the Visual Studio image, and GitHub has "
                           "moved it between images (windows-2025: VS2022 until June 2026, VS2026 since, "
                           "actions/runner-images#14017); pin "
                           "windows-NNNN-vsNNNN")
    if any(rx.fullmatch(low) for rx in PINNED):
        return "pinned", ""
    return "refused", ("is not a label this check knows to be pinned (ubuntu-NN.NN[-arm], "
                       "macos-NN[-intel|-large|-xlarge], windows-NNNN-vsNNNN)")


def comment_starts(text, lines):
    """{line1: column0} where each line's YAML comment starts, for the lines that have one.

    PyYAML drops comments, so they are found by elimination. A `#` starts a comment only when it
    follows whitespace or starts the line AND lies outside every scalar the YAML scanner reads: a
    quoted string (`label: "a # b"`), a plain value (`C#`), or a block scalar's content (a `run: |`
    script). A block scalar's header line holds only its indicator, so a comment after `|` counts.
    """
    spans = {}  # line0 -> [(start, end)], end None = to the end of the line
    for tok in yaml.scan(text, Loader=yaml.SafeLoader):
        if not isinstance(tok, yaml.ScalarToken):
            continue
        s, e = tok.start_mark, tok.end_mark
        first = s.line + 1 if tok.style in ("|", ">") else s.line
        for ln in range(first, e.line + 1):
            spans.setdefault(ln, []).append((s.column if ln == s.line else 0, e.column if ln == e.line else None))
    starts = {}
    for i, raw in enumerate(lines):
        for c, ch in enumerate(raw):
            if ch != "#" or (c and raw[c - 1] not in " \t"):
                continue
            if any(a <= c and (b is None or c < b) for a, b in spans.get(i, ())):
                continue
            starts[i + 1] = c
            break
    return starts


def mapping_get(node, key):
    if isinstance(node, yaml.MappingNode):
        for k, v in node.value:
            if isinstance(k, yaml.ScalarNode) and k.value == key:
                return v
    return None


class Scan:
    def __init__(self, root):
        self.root = root
        self.problems = []
        self.sites = []    # (rel, line1, job, label, verdict) after exceptions are applied
        self.pending = []  # sites of the file being scanned, before its exceptions are applied
        self.consumed = set()  # (rel, line1) whose runner-pin comment sits on a judged value
        self.jobs = 0
        self.files = 0

    def problem(self, msg):
        self.problems.append(msg)

    def judge_label(self, job, node, label):
        """Record one literal label where it is written. Exceptions are settled per line in settle()."""
        verdict, reason = classify(label)
        self.pending.append({"line": node.start_mark.line + 1, "job": job, "label": label,
                             "verdict": verdict, "reason": reason})

    def judge(self, job, node, matrix):
        """Judge a runs-on value, or one part of one."""
        if isinstance(node, yaml.SequenceNode):
            if not node.value:
                self.judge_label(job, node, "<empty runner list>")
            for item in node.value:
                self.judge(job, item, matrix)
            return
        if isinstance(node, yaml.MappingNode):
            labels = mapping_get(node, "labels")
            if labels is None:
                self.judge_label(job, node, "<runner group without labels>")
                return
            self.judge(job, labels, matrix)
            return
        value = node.value if isinstance(node, yaml.ScalarNode) else ""
        m = MATRIX_REF.fullmatch(value.strip())
        if not m:
            self.judge_label(job, node, value)
            return
        key = m.group(1)
        if matrix is None:
            self.judge_label(job, node, value + " (the job has no strategy.matrix)")
            return
        if not isinstance(matrix, yaml.MappingNode):
            self.judge_label(job, node, value + " (strategy.matrix is an expression)")
            return
        values = []
        direct = mapping_get(matrix, key)
        if direct is not None:
            values.append(direct)
        include = mapping_get(matrix, "include")
        if isinstance(include, yaml.SequenceNode):
            for entry in include.value:
                v = mapping_get(entry, key)
                if v is not None:
                    values.append(v)
        elif include is not None:
            self.judge_label(job, include, "${{ ... }} (strategy.matrix.include is an expression)")
        if not values:
            self.judge_label(job, node, value + f" (the matrix defines no '{key}')")
            return
        for v in values:
            # A matrix value is a label or a list of them; a nested matrix reference cannot be resolved.
            self.judge(job, v, None)

    def settle(self, rel, lines, comments):
        """Apply `# runner-pin: allow` comments line by line, then record every site of this file."""
        by_line = {}
        for site in self.pending:
            by_line.setdefault(site["line"], []).append(site)
        self.pending = []
        for line1 in sorted(by_line):
            group = by_line[line1]
            raw = lines[line1 - 1] if line1 - 1 < len(lines) else ""
            # Only the line's YAML comment can grant an exception, never text inside a value on it.
            exc = EXCEPTION.search(raw[comments[line1]:]) if line1 in comments else None
            where = f"{rel}:{line1}"
            if exc:
                self.consumed.add(line1)
                why = exc.group(1).strip()
                if all(g["verdict"] == "pinned" for g in group):
                    shown = ", ".join(f"'{g['label']}'" for g in group)
                    self.problem(f"{where}: job '{group[0]['job']}': stale exception - {shown} is already "
                                 "pinned, so `# runner-pin: allow` excuses nothing; remove the comment")
                elif not re.search(r"\w{3,}", why):
                    for g in group:
                        if g["verdict"] == "refused":
                            self.problem(f"{where}: job '{g['job']}' runs on '{g['label']}', which {g['reason']}; "
                                         "its `# runner-pin: allow` comment gives no reason, and an exception "
                                         "without one is refused")
                else:
                    for g in group:
                        if g["verdict"] == "refused":
                            g["verdict"] = "excepted"
            else:
                for g in group:
                    if g["verdict"] == "refused":
                        self.problem(f"{where}: job '{g['job']}' runs on '{g['label']}', which {g['reason']}. Pin "
                                     "it, or put `# runner-pin: allow <reason>` on this line")
            for g in group:
                self.sites.append((rel, line1, g["job"], g["label"], g["verdict"]))

    def scan_file(self, rel, context):
        path = os.path.join(self.root, rel)
        if not os.path.isfile(path):
            self.problem(f"{rel}: missing. It carries the required check '{context}'; if it moved, move this "
                         "check's REQUIRED entry with it")
            return
        with open(path, encoding="utf-8") as fh:
            text = fh.read()
        lines = text.split("\n")
        try:
            doc = yaml.compose(text, Loader=yaml.SafeLoader)
        except yaml.YAMLError as e:
            self.problem(f"{rel}: does not parse as YAML: {e}")
            return
        jobs = mapping_get(doc, "jobs")
        if not isinstance(jobs, yaml.MappingNode) or not jobs.value:
            self.problem(f"{rel}: no jobs found, so nothing here was judged")
            return
        self.files += 1
        self.consumed = set()
        names = []
        for k, job in jobs.value:
            jid = k.value
            self.jobs += 1
            name_node = mapping_get(job, "name")
            names.append(name_node.value if isinstance(name_node, yaml.ScalarNode) else jid)
            runs_on = mapping_get(job, "runs-on")
            if runs_on is None:
                uses = mapping_get(job, "uses")
                if uses is not None:
                    self.judge_label(jid, uses, "<calls a reusable workflow; its runner is not visible here>")
                else:
                    self.judge_label(jid, k, "<no runs-on>")
                continue
            strategy = mapping_get(job, "strategy")
            matrix = mapping_get(strategy, "matrix") if strategy is not None else None
            self.judge(jid, runs_on, matrix)
        comments = comment_starts(text, lines)
        self.settle(rel, lines, comments)
        if context not in names:
            self.problem(f"{rel}: no job is named '{context}' (found: {', '.join(names)}). That is the required "
                         "check this file is in scope for; if it was renamed, update REQUIRED here and branch "
                         "protection together")
        # runner-pin text inside a value, or a runner-pin comment that sits on no judged value, excuses
        # nothing; say so rather than ignore it.
        for i, raw in enumerate(lines, start=1):
            col = comments.get(i)
            if any(col is None or m.start() < col for m in MARKER.finditer(raw)):
                self.problem(f"{rel}:{i}: `runner-pin:` text inside a YAML value (a quoted string, or a block "
                             "scalar such as a run: script) is not a comment, so it excuses nothing; the only "
                             "exception is a `# runner-pin: allow <reason>` comment after the label on its own line")
            comment = raw[col:] if col is not None else ""
            if MARKER.search(comment) and i not in self.consumed:
                if EXCEPTION.search(comment):
                    self.problem(f"{rel}:{i}: stray `# runner-pin: allow` comment - it is not on a runs-on or "
                                 "matrix value this check judges, so it excuses nothing; move it onto the label's "
                                 "own line or remove it")
                else:
                    self.problem(f"{rel}:{i}: malformed runner-pin comment; the only form is "
                                 "`# runner-pin: allow <reason>` on the label's own line")


def main(argv):
    root = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
    strict = True
    if len(argv) == 3 and argv[1] == "--root":
        root, strict = os.path.abspath(argv[2]), False
    elif len(argv) != 1:
        print("usage: verify-required-runner-pins.py [--root DIR]", file=sys.stderr)
        return 2

    s = Scan(root)
    for rel, context in REQUIRED:
        s.scan_file(rel, context)

    if strict:
        if len(s.sites) < MINIMUM_LABELS:
            s.problem(f"judged {len(s.sites)} runner label(s), expected at least {MINIMUM_LABELS}: the scan has "
                      "stopped reaching jobs it used to reach, so a clean result would mean nothing. Fix the "
                      "scan; do not lower the floor")
        for fam in REQUIRED_FAMILIES:
            if not any(label.strip().lower().startswith(fam + "-") for _, _, _, label, _ in s.sites):
                s.problem(f"no {fam} runner was judged. The readiness matrix is where macOS and Windows run; "
                          "if its lanes are no longer resolved, this check has gone blind to them")

    for rel, line1, job, label, verdict in s.sites:
        print(f"{PREFIX}: {rel}:{line1} {job} -> {label} [{verdict}]")
    for p in s.problems:
        print(f"{PREFIX}: ERROR: {p}")
    counts = {v: sum(1 for x in s.sites if x[4] == v) for v in ("pinned", "excepted", "refused")}
    print(f"{PREFIX}: {s.files} workflow(s), {s.jobs} job(s), {len(s.sites)} runner label(s): "
          f"{counts['pinned']} pinned, {counts['excepted']} excepted, {counts['refused']} refused; "
          f"{len(s.problems)} problem(s)")
    return 1 if s.problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
