# Assembles REPORT.md from the run logs in logs/ (the counts table and the quotable sentence are generated from them, so
# nothing in the report is typed by hand). Run: python3 gen-report.py
import glob, re, os
here = os.path.dirname(os.path.abspath(__file__))
rows = []
for log in sorted(glob.glob(os.path.join(here, "logs", "*.log"))):
    text = open(log).read()
    for m in re.finditer(r"variant=(\w+) N=(\d+) flows<=(\d+) ops=(\w+): sequences (\d+) \(wall ([\d.]+)s, cpu ([\d.]+)s\)\n((?:   .*\n)*)", text):
        variant, n, flows, ops, seqs, wall, cpu, tail = m.groups()
        verdict = "no violation of P1-P4" if "no violation" in tail else "CAUGHT: " + "; ".join(l.strip() for l in tail.strip().splitlines())
        rows.append((variant, int(n), int(flows), ops, int(seqs), float(wall), float(cpu), verdict, os.path.basename(log)))

def fmt_ops(ops):
    names = {"A": "A", "C": "C/U", "E": "E", "K": "K", "L": "L", "O": "O", "P": "P", "R": "R", "U": "", "W": "W", "Y": "Y", "Z": "Z"}
    return " ".join(sorted({names[c] for c in ops if names.get(c)})) + " (+X, +T)"

main_rows = [r for r in rows if r[0] in ("correct", "settled")]
ctrl_rows = [r for r in rows if r[0].startswith("B")]
table = ["| Variant | Ops | Flows | N | Sequences | CPU s | Result | Log |", "|---|---|---|---|---|---|---|---|"]
total_correct = 0
for r in sorted(main_rows, key=lambda r: (r[0] != "correct", r[3], r[2], r[1])):
    table.append("| `%s` | %s | <= %d | %d | %s | %.1f | %s | `%s` |" % (r[0], fmt_ops(r[3]), r[2], r[1], format(r[4], ","), r[6], r[7], r[8]))
    if r[0] == "correct" and "R" in r[3]: total_correct += r[4]
ctable = ["| Control | Ops | N | Sequences | Caught (shortest per property) |", "|---|---|---|---|---|"]
for r in sorted(ctrl_rows, key=lambda r: (r[0], r[1], r[3])):
    ctable.append("| `%s` | %s | %d | %s | %s |" % (r[0], fmt_ops(r[3]), r[1], format(r[4], ","), r[7].replace("CAUGHT: ", "")))
largest = max((r for r in main_rows if r[0] == "correct" and "R" in r[3]), key=lambda r: r[4])
counts_sentence = "%s sequences in all (the largest single bound: %s sequences at N=%d, up to %d flows, ops %s, %.0f s of CPU)" % (
    format(total_correct, ","), format(largest[4], ","), largest[1], largest[2], fmt_ops(largest[3]), largest[6])

report = open(os.path.join(here, "REPORT.template.md")).read()
report = report.replace("COUNTS_TABLE_PLACEHOLDER", "\n".join(table) + "\n\nControls (every op kind on):\n\n" + "\n".join(ctable))
report = report.replace("COUNTS_SENTENCE_PLACEHOLDER", counts_sentence)
report = report.replace("TOTAL_CORRECT_PLACEHOLDER", format(total_correct, ","))
open(os.path.join(here, "REPORT.md"), "w").write(report)
print("REPORT.md written; correct-variant read-scope sequences in all:", format(total_correct, ","))
