# Model of the integrator's third-round rule for EgressSubject.Frame.Unwind:
#   go past a disposed outer frame only when it is a SUBJECT frame whose _previous is a SUBJECT frame and its
#   _unwoundBy record names the frame being unwound (cleared when used). Never past an outermost subject frame, never
#   past a detachment.
# Everything else mirrors EgressSubject.cs at ef4da122 (Resolve, ObserveInto, Dispose's out-of-order record).
#
# Usage: rule3.py oneflow N        -> one flow, ops E (enter) / D (detach) / Xk (dispose frame k); reports the largest
#                                     number of DISPOSED frames on the flow's Outer path after any sequence of N ops.
#        rule3.py multiflow N      -> two flows (main + one forked task), ops E/D/T/Xk/O (observe a fresh atom) on
#                                     either flow; fail-closed check against in-order lexical semantics (below).
#        rule3.py loop K           -> the one-flow loop {Detach; Enter; detachment.Dispose(); frame.Dispose()} K times,
#                                     printing the Outer path length after each iteration.
import sys

TOP = "TOP"


def join(a, b):
    if a is TOP or b is TOP:
        return TOP
    return a | b


def geq(a, b):
    if a is TOP:
        return True
    if b is TOP:
        return False
    return b <= a


class F:
    __slots__ = ("id", "subject", "prev", "restore", "unwound", "disposed", "mark", "ideal", "barrier")

    def __init__(s, i, subject, prev, restore):
        s.id = i
        s.subject = subject
        s.prev = prev
        s.restore = restore
        s.unwound = None
        s.disposed = False
        s.mark = frozenset({"m%d" % i}) if subject else None
        s.ideal = s.mark
        s.barrier = -1

    @property
    def outer(s):
        return s.prev if s.subject else s.restore


def live(f):
    while f is not None and f.disposed:
        f = f.prev
    return f


RESOLVE = "innermost"


def resolve(chain):
    inner = live(chain)
    if inner is None or not inner.subject:
        return TOP
    cur = inner.mark
    f = chain if RESOLVE == "full" else inner.prev
    while f is not None and f.subject:
        cur = join(cur, f.mark)
        f = f.prev
    return cur


def observe_into(chain, label):
    f = chain
    while f is not None and f.subject:
        f.mark = join(f.mark, label)
        f = f.prev


UNWIND = "rule3"
SKIPS = {}


def unwind(frame, fl=0):
    outer = frame.outer
    if UNWIND == "phaseb":
        return outer
    if UNWIND == "flowlocal":
        left = list(SKIPS.get(fl, ()))
        while outer is not None and (frame, outer) in left:
            left.remove((frame, outer))
            frame = outer
            outer = frame.outer
        SKIPS[fl] = tuple(left)
        return outer
    while (outer is not None and outer.unwound is frame and (UNWIND == "ef4da122" or (
           outer.subject and outer.prev is not None and outer.prev.subject))):
        outer.unwound = None
        frame = outer
        outer = frame.outer
    return outer


def dispose(act, fl, f):
    if f.disposed:
        return
    f.disposed = True
    if f.subject:
        observe_into(f.prev, f.mark)
        # ideal: in-order semantics carry the mark outward along prev too
        g = f.prev
        while g is not None and g.subject:
            g.ideal = join(g.ideal, f.ideal)
            g = g.prev
    if act[fl] is f:
        act[fl] = unwind(f, fl)
        return
    inside = act[fl]
    while inside is not None:
        if inside.outer is f:
            if UNWIND == "flowlocal":
                SKIPS[fl] = ((inside, f),) + tuple(SKIPS.get(fl, ()))
            else:
                f.unwound = inside
            break
        inside = inside.outer


def path_disposed(a):
    n = 0
    seen = set()
    while a is not None and id(a) not in seen:
        seen.add(id(a))
        if a.disposed:
            n += 1
        a = a.outer
    return n


def stuck(a):
    n = 0
    while a is not None and a.disposed:
        n += 1
        a = a.outer
    return n


def path_len(a):
    n = 0
    while a is not None:
        n += 1
        a = a.outer
    return n


BARRIER = False


def scope_of(stack):
    j = -1
    for i, f in enumerate(stack):
        if not f.subject:
            j = i
    scope = stack[j + 1:]
    if BARRIER and scope:
        floor = scope[-1].barrier
        scope = [f for f in scope if f.id > floor]
    return scope


def required(stack):
    # in-order lexical semantics: the subject frames inside the innermost detachment; none -> no subject (TOP)
    scope = scope_of(stack)
    if not scope:
        return TOP
    cur = frozenset()
    for f in scope:
        cur = join(cur, f.ideal)
    return cur


OPS = ("E", "D")
INORDER_DETACH = False


def oneflow(N):
    worst = [0, None]

    def run(ops):
        frames = []
        act = [None]
        for op in ops:
            if op == "E":
                f = F(len(frames), True, act[0], None)
                frames.append(f)
                act[0] = f
            elif op == "D":
                f = F(len(frames), False, None, act[0])
                frames.append(f)
                act[0] = f
            else:
                k = int(op[1:])
                dispose(act, 0, frames[k])
        d = stuck(act[0])
        if d > worst[0]:
            worst[0] = d
            worst[1] = list(ops)

    def gen(ops, nf, livef):
        run(ops)
        if len(ops) == N:
            return
        for op in OPS:
            gen(ops + [op], nf + 1, livef | {nf})
        for k in sorted(livef):
            gen(ops + ["X%d" % k], nf, livef - {k})

    gen([], 0, frozenset())
    print("oneflow N=%d: most disposed frames between the active frame and the first live one (stuck): %d  e.g. %s" % (N, worst[0], " ".join(worst[1] or [])))


def multiflow(N):
    found = {}
    count = [0]

    def run(ops):
        frames = []
        act = [None]
        stacks = [[]]
        atom = 0
        SKIPS.clear()
        for kind, fl, arg in ops:
            if fl >= len(act):
                return False
            if kind in ("E", "D"):
                f = F(len(frames), kind == "E", act[fl] if kind == "E" else None, None if kind == "E" else act[fl])
                # The detachment this frame is entered under: the innermost lexical item if it is one, otherwise
                # the one the frame it is entered inside was entered under (a chain of its own stays one).
                top = stacks[fl][-1] if stacks[fl] else None
                f.barrier = -1 if top is None else (top.id if not top.subject else top.barrier)
                frames.append(f)
                act[fl] = f
                stacks[fl].append(f)
            elif kind == "T":
                act.append(act[fl])
                stacks.append(list(stacks[fl]))
                SKIPS[len(act) - 1] = SKIPS.get(fl, ())
            elif kind == "O":
                atom += 1
                x = frozenset({"o%d" % atom})
                observe_into(act[fl], x)
                for f in scope_of(stacks[fl]):
                    f.ideal = join(f.ideal, x)
            else:
                if arg >= len(frames) or frames[arg].disposed:
                    return False
                f = frames[arg]
                if INORDER_DETACH and not f.subject and act[fl] is not f:
                    return False
                dispose(act, fl, f)
                if f in stacks[fl]:
                    stacks[fl].remove(f)
            for g in range(len(act)):
                want = required(stacks[g])
                got = resolve(act[g])
                if not geq(got, want):
                    key = "write-down on " + ("main" if g == 0 else "task")
                    if key not in found or len(found[key]) > len(ops):
                        found[key] = list(ops)
                    return False
        return True

    def gen(ops, nf, nflows, livef):
        count[0] += 1
        if not run(ops) or len(ops) == N:
            return
        for fl in range(nflows):
            gen(ops + [("E", fl, None)], nf + 1, nflows, livef | {nf})
            if "D" in OPS:
                gen(ops + [("D", fl, None)], nf + 1, nflows, livef | {nf})
            gen(ops + [("O", fl, None)], nf, nflows, livef)
            if nflows < 2:
                gen(ops + [("T", fl, None)], nf, nflows + 1, livef)
            for k in sorted(livef):
                gen(ops + [("X", fl, k)], nf, nflows, livef - {k})

    gen([], 0, 1, frozenset())
    print("multiflow N=%d: sequences %d" % (N, count[0]))
    fmt = lambda v: " ; ".join(
        "%s%s@%s" % (o[0], "" if o[2] is None else o[2], "main" if o[1] == 0 else "task") for o in v)
    for k, v in found.items():
        print("  ", k, ":", fmt(v))
    if not found:
        print("   no write-down")


def loop(K):
    frames = []
    act = [None]
    caller = F(0, True, None, None)
    frames.append(caller)
    act[0] = caller
    for i in range(K):
        d = F(len(frames), False, None, act[0])
        frames.append(d)
        act[0] = d
        s = F(len(frames), True, act[0], None)
        frames.append(s)
        act[0] = s
        dispose(act, 0, d)
        dispose(act, 0, s)
        print("iteration %d: Outer path length %d, disposed on it %d, resolve %s" % (
            i + 1, path_len(act[0]), path_disposed(act[0]), resolve(act[0])))


if __name__ == "__main__":
    mode, n = sys.argv[1], int(sys.argv[2])
    for a in sys.argv[3:]:
        if a.startswith("unwind="):
            UNWIND = a.split("=", 1)[1]
    if "resolve=full" in sys.argv[3:]:
        RESOLVE = "full"
    if "barrier" in sys.argv[3:]:
        BARRIER = True
    if "inorder-detach" in sys.argv[3:]:
        INORDER_DETACH = True
    if "subjects-only" in sys.argv[3:]:
        OPS = ("E",)
    {"oneflow": oneflow, "multiflow": multiflow, "loop": loop}[mode](n)
