# frame-model-readscope.py: the 4.4 no-skip frame rule (frame-model-wd-callback.py, "r7", the model whose counts PR
# 4.4's body quotes) extended with the 4.5 READ FRAME (owner decision 2026-10-06).
#
# THE RULE MODELLED (EgressSubject.cs / ReadScope.cs on master de41a8ac, plus the 4.5 read frame):
#   * E  Enter: push a subject frame (prev = the flow's head).  Resolve = join of the marks of every subject or read
#        frame from the head down to a detachment, read live; no live frame or a detachment innermost -> SystemHigh.
#   * X  Dispose a subject frame (from any flow): observe its mark into the frames it was entered inside (up to a
#        detachment), mark it disposed, and ONLY IF it is the disposing flow's own undisposed head restore exactly its
#        prev (the 4.4 no-skip rule). Anywhere else it moves no flow.
#   * O  Observe a fresh atom: join it into every subject/read frame from the head down to a detachment.
#   * T  Fork (Task / continuation / thread started inside): the child starts with the parent's head and lexical stack.
#   * C/U RunDetached brackets: C pushes a detachment (chain end), U restores exactly the caller's head and drops the
#        frames and reads entered inside from the lexical stack (4.4 limit (c)). A detachment is never disposed.
#   * A  Dispose a subject frame inside an AWAITED ASYNC HELPER: Dispose runs on a COPY of the flow's context, so the
#        head restore is lost; lexically the flow left the frame (r7's A op).
#   * R  BeginRead: push a READ FRAME (prev = head) whose mark is PINNED AT SystemHigh while the read is open.
#        The ReadScope holds that frame.
#   * P  Report a fresh atom on read r (from any flow; ReadScope.Report may be called from any thread). After the read
#        ended, it observes straight into the read frame and the chain it was begun on (ReadScope.cs:63-65).
#   * K  Complete read r (from any flow).
#   * Z  EndRead = ReadScope.Dispose (from any flow): result = join of the reports if Complete was called and at least
#        one report was made, else SystemHigh (ReadScope.cs:83-84, unchanged). Observe the result into the chain the
#        read was begun on; mark the read frame disposed; ONLY IF the read frame is the disposing flow's own undisposed
#        head restore its prev (no-skip). Variant "pinned" (the rule as given): the read frame's mark stays SystemHigh for
#        life, so a flow that did not leave it decides SystemHigh for life. Variant "settled": once ended, the read frame
#        counts at join(result, everything observed into it while open), so a flow stuck inside it decides as the chain
#        it was begun on does. Both are checked; the REPORT says what each costs.
#   * Y  EndRead inside an awaited async helper (the A op for reads): Dispose on a copy, lexically left.
#   * L  The flow's own `using` of subject frame k ends after another flow (or a helper) already disposed it: the code is
#        a no-op (the CAS returns), lexically the flow has left k (r7's L op).
#   * W  The same for a read: the flow's `using` of read r ends after another flow already ended it: code no-op
#        (ReadScope.Dispose's Exchange returns), lexically the flow has left the read.
#
# REQUIRED LABEL (the spec the code is checked against; lexical in-order semantics, as in r7):
#   A flow's lexical stack holds the subject frames, detachments and read frames it is inside in program order (a fork
#   copies it; X/A/Y/Z on the flow remove the item; U drops everything entered inside the callback). Its scope is the
#   items after the innermost detachment (with r7's barrier refinement). required(flow) =
#     SystemHigh                                            if the scope is empty (no subject);
#     join over the scope of ( subject frame -> its ideal, or its live mark; ENDED read -> its result; an open read
#     contributes nothing here, P2 covers it )              otherwise.
#   The "ideal" of a subject frame is what in-order semantics say it has read: its own atoms, plus the ideal of every
#   frame disposed inside it, plus the result of every read ENDED that was begun inside it (joined when the read ends).
#
# PROPERTIES, checked after every op, for every flow:
#   P1 (no write-down; the 4.4 property restated): resolve(flow) >= required(flow), for both the ideal and the live-mark
#      reading of required. I.e. a decision never resolves below the join of every label that reached its flow through a
#      read that has ended, nor below the marks of the frames it is inside.
#   P2 (open read = SystemHigh): if the flow's lexical scope holds a read that is not yet ended (begun on this flow, or on
#      an ancestor before the fork), resolve(flow) == SystemHigh. (An open read counts as unreported until it ends: a
#      Report before Dispose does not lower it.)
#   P3 (no-skip, with read frames present): after any X/Z/A/Y, every flow's head is exactly: the disposed frame's prev if
#      the disposing flow's head was that frame and it was undisposed; otherwise unchanged. Nothing else ever moves a
#      flow (E/R push; C/U are the exact bracket).
#   P4 (fail closed on a mis-disposed read): a Z/Y that is not an in-order end (the read frame is not the disposing
#      flow's own undisposed head: out of order, on another flow, or in an awaited helper) moves no flow's head and lowers
#      no mark: every subject frame's mark is >= before, and the read frame's mark is SystemHigh (pinned) or >= the
#      result (settled). Independently, every subject-frame mark is monotone under every op.
#
# BROKEN VARIANTS (controls; each must be CAUGHT, with the shortest counterexample):
#   B1  the open scope is not counted (master today): BeginRead pushes no frame; the scope only observes on Dispose.
#   B2  Dispose pops the read frame even when it is not the head: the flow's head jumps to the read frame's prev (skip).
#   B3  work forked inside the scope does not inherit the read frame: the child starts below the read frame(s).
#   B4  Report before Complete satisfies the scope even when the read ends without Complete (throws).
#
# Usage: frame-model-readscope.py <variant> <N> <MAXF> <ops> [nobarrier]
#   variant: correct | settled | B1 | B2 | B3 | B4      ops: letters from E O C U A L R P K Z Y W (X, and T up to MAXF, are always on)
#   With no read ops, "ACELO" N=7 MAXF=2 and "ACELOQR"->"ACELO" N=6 reproduce r7's quoted 1,900,357 and (with R K P Z for
#   Q/R) the read-scope counts differ only by the richer read ops.
import sys, time

TOP = "TOP"
def join(a, b):
    if a is TOP or b is TOP: return TOP
    return a | b
def geq(a, b):
    if a is TOP: return True
    if b is TOP: return False
    return b <= a

S, D, RD = "S", "D", "R"   # subject frame, detachment, read frame

class F:
    __slots__ = ("id", "kind", "prev", "disposed", "mark", "ideal", "barrier", "owner", "read", "observed")
    def __init__(s, i, kind, prev):
        s.id, s.kind, s.prev = i, kind, prev
        s.disposed = False
        s.mark = frozenset({"m%d" % i}) if kind == S else (TOP if kind == RD else None)
        s.ideal = s.mark if kind == S else None
        s.barrier = -1
        s.owner = -1
        s.read = None          # read index for a read frame
        s.observed = frozenset()  # labels observed into an open read frame (settled variant)

VARIANT = "correct"

def live(f):
    while f is not None and f.disposed: f = f.prev
    return f

def resolve(chain):
    inner = live(chain)
    if inner is None or inner.kind == D: return TOP
    cur = frozenset()
    f = chain
    while f is not None and f.kind != D:
        cur = join(cur, f.mark); f = f.prev
    return cur

def observe_into(chain, label):
    f = chain
    while f is not None and f.kind != D:
        if f.kind == RD:
            f.observed = join(f.observed, label)
            if f.mark is not TOP: f.mark = join(f.mark, label)   # settled, after the end
        else:
            f.mark = join(f.mark, label)
        f = f.prev

def dispose_frame(ctx, f):
    # Frame.Dispose for a subject frame (EgressSubject.cs:263-280).
    if f.disposed: return
    f.disposed = True
    observe_into(f.prev, f.mark)
    g = f.prev
    while g is not None and g.kind != D:
        if g.kind == S: g.ideal = join(g.ideal, f.ideal)
        g = g.prev
    if ctx["act"] is f: ctx["act"] = f.prev

class Read:
    """One ReadScope: the read frame it holds (the lexical item), the chain its result is observed into (the read
    frame's prev; for B1, the begin head), the reports, Complete, whether it ended, and the result the CODE computed
    (res) beside the result the SPEC requires (spec: reports iff Complete and a report, else SystemHigh)."""
    __slots__ = ("frame", "chain", "reported", "completed", "ended", "res", "spec")
    def __init__(s, frame, chain):
        s.frame, s.chain = frame, chain
        s.reported, s.completed, s.ended, s.res, s.spec = None, False, False, None, None

def end_read(ctx, rd):
    # ReadScope.Dispose with the read frame.
    rd.spec = rd.reported if rd.completed and rd.reported is not None else TOP
    rd.res = rd.reported if (rd.completed or VARIANT == "B4") and rd.reported is not None else TOP
    rd.ended = True
    frame = rd.frame
    if VARIANT == "B1":
        observe_into(rd.chain, rd.res)     # no read frame on any chain; the scope only observes
        return
    if VARIANT == "settled":
        frame.mark = join(rd.res, frame.observed)
    frame.disposed = True
    observe_into(frame.prev, rd.res)
    if VARIANT == "B2":
        f = ctx["act"]
        while f is not None and f.kind != D and f is not frame: f = f.prev
        if f is frame: ctx["act"] = frame.prev
    elif ctx["act"] is frame:
        ctx["act"] = frame.prev

BARRIER = True
def scope_of(stack):
    j = -1
    for i, f in enumerate(stack):
        if f.kind == D: j = i
    scope = stack[j + 1:]
    if BARRIER and scope:
        floor = scope[-1].barrier
        scope = [f for f in scope if f.id > floor]
    return scope

def required(stack, live_marks, reads):
    scope = scope_of(stack)
    if not scope: return TOP
    cur = frozenset()
    for f in scope:
        if f.kind == RD:
            rd = reads[f.read]
            if not rd.ended: continue          # an open read contributes nothing to P1 (P2 requires SystemHigh for it)
            cur = join(cur, rd.spec)           # an ended read: its result, as the spec defines it
        else:
            cur = join(cur, f.mark if live_marks else f.ideal)
    return cur

def open_read_in_scope(stack, reads):
    return any(f.kind == RD and not reads[f.read].ended for f in scope_of(stack))

def lex_scope_of_read(stacks, rd):
    # The subject frames the read was begun inside, lexically: the scope items before the read frame on every stack
    # that still holds it. If none holds it any more, the subject frames of the chain the code observes into.
    out = []
    for st in stacks:
        if rd.frame in st:
            idx = st.index(rd.frame)
            for f in scope_of(st[:idx]):
                if f.kind == S and f not in out: out.append(f)
    if not out:
        f = rd.chain
        while f is not None and f.kind != D:
            if f.kind == S and f not in out: out.append(f)
            f = f.prev
    return out

def heads(flows): return [c["act"] for c in flows]
def marks(frames): return [f.mark for f in frames]

def new_read_frame(frames, st, head, fl, nreads):
    f = F(len(frames), RD, head); f.owner = fl; f.read = nreads
    top = st[-1] if st else None
    f.barrier = -1 if top is None else (top.id if top.kind == D else top.barrier)
    frames.append(f); st.append(f)
    return f

def run(ops, MAXF, report):
    frames, flows, stacks, reads = [], [{"act": None, "cb": ()}], [[]], []
    atom = 0
    for step, (kind, fl, arg) in enumerate(ops):
        if fl >= len(flows): return False
        ctx, st = flows[fl], stacks[fl]
        before_heads, before_marks = heads(flows), marks(frames)
        p3_expect = None   # (disposing flow, its expected head)
        p4_check = None    # (read frame, spec result) for a mis-disposed read
        if kind == "E":
            f = F(len(frames), S, ctx["act"])
            top = st[-1] if st else None
            f.barrier = -1 if top is None else (top.id if top.kind == D else top.barrier)
            f.owner = fl
            frames.append(f); ctx["act"] = f; st.append(f)
        elif kind == "T":
            if len(flows) >= MAXF: return False
            child = dict(ctx); child["cb"] = ()
            if VARIANT == "B3":
                h = child["act"]
                while h is not None and h.kind == RD: h = h.prev
                child["act"] = h
            flows.append(child); stacks.append(list(st))
        elif kind == "C":
            d = F(len(frames), D, None)
            top = st[-1] if st else None
            d.barrier = -1 if top is None else (top.id if top.kind == D else top.barrier)
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
            for f in scope_of(st):
                if f.kind == S: f.ideal = join(f.ideal, x)
        elif kind == "R":
            head = ctx["act"]
            f = new_read_frame(frames, st, head, fl, len(reads))
            if VARIANT == "B1":
                f.disposed = True                  # lexically a read; the code pushes no frame (master today)
                reads.append(Read(f, head))
            else:
                ctx["act"] = f
                reads.append(Read(f, head))        # chain = the read frame's prev
        elif kind == "P":
            if arg >= len(reads): return False
            rd = reads[arg]
            atom += 1; x = frozenset({"o%d" % atom})
            rd.reported = x if rd.reported is None else join(rd.reported, x)
            if rd.ended:
                # A late report goes to the frames itself (ReadScope.cs:63-65): the read frame and the chain it was begun on.
                observe_into(rd.chain if VARIANT == "B1" else rd.frame, x)
                for f in lex_scope_of_read(stacks, rd): f.ideal = join(f.ideal, x)
        elif kind == "K":
            if arg >= len(reads): return False
            rd = reads[arg]
            if rd.completed or rd.ended: return False
            rd.completed = True
        elif kind in ("Z", "Y"):
            if arg >= len(reads): return False
            rd = reads[arg]
            if rd.ended: return False
            frame = rd.frame
            in_order = VARIANT != "B1" and ctx["act"] is frame and not frame.disposed and kind == "Z"
            if kind == "Y":
                if frame not in st or frame.owner != fl: return False
                end_read(dict(ctx), rd)            # the helper's copy of the flow; discarded on return
            else:
                end_read(ctx, rd)
            for f in lex_scope_of_read(stacks, rd): f.ideal = join(f.ideal, rd.spec)
            if frame in st: st.remove(frame)
            if VARIANT != "B1":
                p3_expect = (fl, frame.prev if in_order else before_heads[fl])
                if not in_order: p4_check = (frame, rd.spec)
        elif kind == "L":
            if arg >= len(frames) or frames[arg].kind != S or not frames[arg].disposed or frames[arg] not in st or frames[arg].owner != fl: return False
            st.remove(frames[arg])
        elif kind == "W":
            if arg >= len(reads) or not reads[arg].ended or reads[arg].frame not in st or reads[arg].frame.owner != fl: return False
            st.remove(reads[arg].frame)
        elif kind in ("X", "A"):
            if arg >= len(frames) or frames[arg].disposed or frames[arg].kind != S: return False
            f = frames[arg]
            in_order = ctx["act"] is f and kind == "X"
            if kind == "A":
                if f not in st or f.owner != fl: return False
                dispose_frame(dict(ctx), f)
            else:
                dispose_frame(ctx, f)
            if f in st: st.remove(f)
            p3_expect = (fl, f.prev if in_order else before_heads[fl])
        else:
            return False

        # ---- properties (every violated one is reported; the sequence then stops) ----
        bad = False
        for i, f in enumerate(frames[:len(before_marks)]):
            if f.kind == S and not geq(f.mark, before_marks[i]):
                report(ops, "P4-mark-lowered", fl); bad = True; break
        if p3_expect is not None:
            dfl, exp = p3_expect
            for g in range(len(flows)):
                want = exp if g == dfl else before_heads[g]
                if flows[g]["act"] is not want:
                    report(ops, "P3-head", g); bad = True; break
        if p4_check is not None:
            frame, spec = p4_check
            ok = frame.mark is TOP if VARIANT != "settled" else geq(frame.mark, spec)
            if not ok or heads(flows) != before_heads:
                report(ops, "P4-misdisposed-read", fl); bad = True
        for g in range(len(flows)):
            got = resolve(flows[g]["act"])
            if open_read_in_scope(stacks[g], reads) and got is not TOP:
                report(ops, "P2-open-read", g); bad = True
            for lm in (False, True):
                want = required(stacks[g], lm, reads)
                if not geq(got, want):
                    report(ops, "P1-write-down" + ("-live" if lm else "-ideal"), g); bad = True
        if bad: return False
    return True

def search(N, MAXF, opsset, quiet=False):
    found = {}
    count = [0]
    def report(ops, prop, g):
        key = (prop, "main" if g == 0 else "t%d" % g)
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
                gen(ops + [("R", fl, None)], nf + 1, nflows, nreads + 1)
                for r in range(nreads):
                    if "P" in opsset: gen(ops + [("P", fl, r)], nf, nflows, nreads)
                    if "K" in opsset: gen(ops + [("K", fl, r)], nf, nflows, nreads)
                    gen(ops + [("Z", fl, r)], nf, nflows, nreads)
                    if "Y" in opsset: gen(ops + [("Y", fl, r)], nf, nflows, nreads)
                    if "W" in opsset: gen(ops + [("W", fl, r)], nf, nflows, nreads)
    t0, c0 = time.time(), time.process_time()
    gen([], 0, 1, 0)
    dt, cpu = time.time() - t0, time.process_time() - c0
    fmt = lambda v: " ; ".join("%s%s@%s" % (o[0], "" if o[2] is None else o[2], "main" if o[1] == 0 else "t%d" % o[1]) for o in v)
    print("variant=%s N=%d flows<=%d ops=%s: sequences %d (wall %.1fs, cpu %.1fs)" % (VARIANT, N, MAXF, "".join(sorted(opsset)), count[0], dt, cpu))
    for k in sorted(found, key=lambda k: (len(found[k]), k)):
        print("   CAUGHT %s on %s (%d ops): %s" % (k[0], k[1], len(found[k]), fmt(found[k])))
    if not found: print("   no violation of P1-P4")
    sys.stdout.flush()
    return found, count[0]

if __name__ == "__main__":
    VARIANT = sys.argv[1]; N = int(sys.argv[2]); MAXF = int(sys.argv[3]); opsset = set(sys.argv[4])
    if "nobarrier" in sys.argv[5:]: BARRIER = False
    search(N, MAXF, opsset)
