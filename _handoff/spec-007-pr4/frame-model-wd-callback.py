# r7: the final rule, no-skip subject frames + callback-shaped RunDetached. Ops C (open a callback run detached on the
# flow: push a detachment, remember the caller's head and lexical stack) and U (return from the flow's innermost open
# callback: head restored exactly, frames entered inside dropped from the lexical stack). A detachment can never be
# disposed (X/A/L on it are refused). A task forked inside a callback keeps the detachment on its stack for life.
# Write-down hunt model for the no-skip rule (EgressSubject.cs at e761b070) and, for comparison, the flow-local rule
# (e648ac4d). Extends scratchpad/model-noskip/noskip.py with:
#   L k : the flow's own `using` of frame k ends after another flow (or a helper) already disposed it: code no-op
#         (Dispose returns on the CAS), lexically the flow has left k.
#   A k : the flow disposes frame k inside an awaited async helper: the code runs Dispose on a COPY of the flow's
#         context (the async method builder restores the caller's context on return), lexically the flow left k.
#   up to MAXF flows (T forks), R/Q read scopes (R begins on the flow's chain, Q k ends read scope k with a fresh atom,
#   on any flow).
# Required (lexical in-order semantics, barrier refinement as in noskip.py): the subject frames inside the innermost
# detachment on the flow's lexical stack; frames entered under a detachment stay detached work. Two required values
# are checked: the in-order "ideal" and the code's live marks of the same frames.
import sys, itertools

TOP = "TOP"
def join(a, b):
    if a is TOP or b is TOP: return TOP
    return a | b
def geq(a, b):
    if a is TOP: return True
    if b is TOP: return False
    return b <= a

class F:
    __slots__ = ("id", "subject", "prev", "restore", "disposed", "mark", "ideal", "barrier", "owner")
    def __init__(s, i, subject, prev, restore):
        s.id, s.subject, s.prev, s.restore = i, subject, prev, restore
        s.disposed = False
        s.mark = frozenset({"m%d" % i}) if subject else None
        s.ideal = s.mark
        s.barrier = -1
    @property
    def outer(s): return s.prev if s.subject else s.restore

def live(f):
    while f is not None and f.disposed: f = f.prev
    return f

def resolve(chain):
    inner = live(chain)
    if inner is None or not inner.subject: return TOP
    cur = frozenset()
    f = chain
    while f is not None and f.subject:
        cur = join(cur, f.mark); f = f.prev
    return cur

def observe_into(chain, label):
    f = chain
    while f is not None and f.subject:
        f.mark = join(f.mark, label); f = f.prev

RULE = "noskip"

def unwind(frame, skips):
    outer = frame.outer
    if RULE == "noskip": return outer, skips
    left = list(skips)
    while outer is not None and (frame, outer) in left:
        left.remove((frame, outer)); frame = outer; outer = frame.outer
    return outer, tuple(left)

def dispose(ctx, f):
    # ctx: dict with 'act' and 'skips' (the flow's AsyncLocals)
    if f.disposed: return
    f.disposed = True
    if f.subject:
        observe_into(f.prev, f.mark)
        g = f.prev
        while g is not None and g.subject:
            g.ideal = join(g.ideal, f.ideal); g = g.prev
    if ctx["act"] is f:
        ctx["act"], ctx["skips"] = unwind(f, ctx["skips"]); return
    if RULE == "flowlocal":
        inside = ctx["act"]
        while inside is not None:
            if inside.outer is f:
                ctx["skips"] = ((inside, f),) + tuple(ctx["skips"]); break
            inside = inside.outer

BARRIER = True
def scope_of(stack):
    j = -1
    for i, f in enumerate(stack):
        if not f.subject: j = i
    scope = stack[j + 1:]
    if BARRIER and scope:
        floor = scope[-1].barrier
        scope = [f for f in scope if f.id > floor]
    return scope

def required(stack, live_marks):
    scope = scope_of(stack)
    if not scope: return TOP
    cur = frozenset()
    for f in scope: cur = join(cur, f.mark if live_marks else f.ideal)
    return cur

def classify(flows, stacks):
    # D1-class: some flow's chain segment ends at a disposed detachment that the flow has lexically left
    for i, ctx in enumerate(flows):
        f = ctx["act"]
        while f is not None and f.subject: f = f.prev
        if f is not None and f.disposed and f not in stacks[i]:
            return "D1-class(stuck-on-disposed-detachment)"
    return "OTHER"

def run(ops, MAXF, report):
    frames, flows, stacks, reads = [], [{"act": None, "skips": (), "cb": ()}], [[]], []
    atom = 0
    for kind, fl, arg in ops:
        if fl >= len(flows): return False
        ctx, st = flows[fl], stacks[fl]
        if kind in ("E", "D"):
            f = F(len(frames), kind == "E", ctx["act"] if kind == "E" else None, None if kind == "E" else ctx["act"])
            top = st[-1] if st else None
            f.barrier = -1 if top is None else (top.id if not top.subject else top.barrier)
            f.owner = fl
            frames.append(f); ctx["act"] = f; st.append(f)
        elif kind == "T":
            if len(flows) >= MAXF: return False
            child = dict(ctx); child["cb"] = ()
            flows.append(child); stacks.append(list(st))
        elif kind == "C":
            d = F(len(frames), False, None, None)
            top = st[-1] if st else None
            d.barrier = -1 if top is None else (top.id if not top.subject else top.barrier)
            d.owner = fl
            frames.append(d)
            ctx["cb"] = ctx["cb"] + ((ctx["act"], list(st)),)
            ctx["act"] = d; st.append(d)
        elif kind == "U":
            if not ctx["cb"]: return False
            saved_act, saved_st = ctx["cb"][-1]; ctx["cb"] = ctx["cb"][:-1]
            ctx["act"] = saved_act
            st[:] = [f for f in saved_st if f in st]
        elif kind == "O":
            atom += 1; x = frozenset({"o%d" % atom})
            observe_into(ctx["act"], x)
            for f in scope_of(st): f.ideal = join(f.ideal, x)
        elif kind == "R":
            reads.append((ctx["act"], list(scope_of(st)), False))
        elif kind == "Q":
            if arg >= len(reads) or reads[arg][2]: return False
            chain, lscope, _ = reads[arg]; reads[arg] = (chain, lscope, True)
            atom += 1; x = frozenset({"o%d" % atom})
            observe_into(chain, x)
            for f in lscope: f.ideal = join(f.ideal, x)
        elif kind in ("X", "A"):
            if arg >= len(frames) or frames[arg].disposed or not frames[arg].subject: return False
            f = frames[arg]
            if kind == "A":
                if f not in st or f.owner != fl: return False
                tmp = dict(ctx); dispose(tmp, f)   # the helper's copy; discarded on return
            else:
                dispose(ctx, f)
            if f in st: st.remove(f)
        elif kind == "L":
            if arg >= len(frames) or not frames[arg].subject or not frames[arg].disposed or frames[arg] not in st or frames[arg].owner != fl: return False
            st.remove(frames[arg])
        for g in range(len(flows)):
            got = resolve(flows[g]["act"])
            for lm in (False, True):
                want = required(stacks[g], lm)
                if not geq(got, want):
                    report(ops, g, lm, classify(flows, stacks))
                    return False
    return True

def search(N, MAXF, opsset):
    found = {}
    count = [0]
    def report(ops, g, lm, cls):
        kinds = frozenset(o[0] for o in ops)
        key = (cls, "main" if g == 0 else "task%d" % g, "".join(sorted(kinds & set("LAXRQT"))))
        if key not in found or len(found[key]) > len(ops): found[key] = list(ops)
    def gen(ops, nf, nflows, nreads):
        count[0] += 1
        if not run(ops, MAXF, report) or len(ops) == N: return
        for fl in range(nflows):
            gen(ops + [("E", fl, None)], nf + 1, nflows, nreads)
            if "C" in opsset:
                gen(ops + [("C", fl, None)], nf + 1, nflows, nreads)
                gen(ops + [("U", fl, None)], nf, nflows, nreads)
            if "O" in opsset: gen(ops + [("O", fl, None)], nf, nflows, nreads)
            if nflows < MAXF: gen(ops + [("T", fl, None)], nf, nflows + 1, nreads)
            for k in range(nf):
                gen(ops + [("X", fl, k)], nf, nflows, nreads)
                if "L" in opsset: gen(ops + [("L", fl, k)], nf, nflows, nreads)
                if "A" in opsset: gen(ops + [("A", fl, k)], nf, nflows, nreads)
            if "R" in opsset:
                gen(ops + [("R", fl, None)], nf, nflows, nreads + 1)
                for r in range(nreads): gen(ops + [("Q", fl, r)], nf, nflows, nreads)
    gen([], 0, 1, 0)
    fmt = lambda v: " ; ".join("%s%s@%s" % (o[0], "" if o[2] is None else o[2], "main" if o[1] == 0 else "t%d" % o[1]) for o in v)
    print("rule=%s N=%d flows<=%d ops=%s: sequences %d" % (RULE, N, MAXF, "".join(sorted(opsset)), count[0]))
    for k in sorted(found, key=lambda k: (len(found[k]), k)):
        print("   write-down", k, ":", fmt(found[k]))
    if not found: print("   no write-down")
    sys.stdout.flush()

if __name__ == "__main__":
    RULE = sys.argv[1]; N = int(sys.argv[2]); MAXF = int(sys.argv[3]); opsset = set(sys.argv[4])
    if "nobarrier" in sys.argv[5:]: BARRIER = False
    search(N, MAXF, opsset)
