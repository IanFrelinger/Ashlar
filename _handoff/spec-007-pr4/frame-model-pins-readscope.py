# frame-model-pins.py (the port of EgressSubjectNestingTests.FlowModel, no-skip rule) extended with the 4.5 READ FRAME:
#   begin_read()            pushes a read frame (mark pinned at SystemHigh = TOP) as the flow's head;
#   end_read(n, result, on_flow)  ReadScope.Dispose: observes `result` into the frames the read was begun inside, marks
#                           the read frame disposed, and restores the flow's head to the frame's prev ONLY IF the read
#                           frame is this flow's undisposed head and the dispose runs on this flow (no-skip). on_flow=False
#                           models a dispose from another flow or inside an awaited async helper (the restore is lost).
# Part 1 re-computes the three pins.py numbers with no reads (they must not change: the rule adds frames only when
# BeginRead is called), plus the stay-outer twin's lengths. Part 2 computes candidate pins for 4.5 twins: exact restore-path lengths with read frames.
TOP = "TOP"

class Node:
    # `outer` is the restore target (prev for a subject or read frame). The pins.py detachment is the OLD disposable one
    # (prev None, outer = the caller's head): kept here only so Part 1 reproduces pins.py; 4.4 deleted the two twins that
    # pin it (detach-middle, four-frame shapes) in favour of the 2,092-program twin, and RunDetached cannot be disposed.
    def __init__(s, name, mark, prev, kind="S", outer=None):
        s.name, s.mark, s.prev, s.disposed, s.kind = name, mark, prev, False, kind
        s.outer = prev if kind != "D" else outer

class Model:
    def __init__(s): s.head = None
    def enter(s, name, mark):
        s.head = Node(name, set(mark), s.head); return s.head
    def detach(s):
        s.head = Node("D", None, None, "D", outer=s.head); return s.head
    def begin_read(s):
        s.head = Node("read", TOP, s.head, "R"); return s.head
    def observe_into(s, a, label):
        while a is not None and a.kind != "D":
            if a.kind == "S" and label is not TOP: a.mark |= label
            elif a.kind == "S": a.mark = TOP_SET
            a = a.prev
    def dispose(s, n, on_flow=True):
        # a subject frame: Frame.Dispose
        if n.disposed: return
        if n.mark is not None and n.mark is not TOP:
            a = n.prev
            while a is not None and a.kind != "D":
                if a.kind == "S": a.mark |= n.mark
                a = a.prev
        n.disposed = True
        if on_flow and s.head is n: s.head = n.outer
    def end_read(s, n, result, on_flow=True):
        if n.disposed: return
        s.observe_into(n.prev, result)
        n.disposed = True
        if on_flow and s.head is n: s.head = n.prev
    def length(s):
        k, a = 0, s.head
        while a is not None: k += 1; a = a.outer
        return k

TOP_SET = set(["TOP"])

def perms(n):
    if n == 1:
        yield [0]; return
    for shorter in perms(n - 1):
        for at in range(len(shorter) + 1):
            l = list(shorter); l.insert(at, n - 1); yield l

print("== Part 1: the 4.4 pins, no reads (must equal frame-model-pins.py: 13, 13, 1248) ==")
m = Model()
for order in perms(3):
    e = m.enter("E", set()); ns = [m.enter(str(i), {i}) for i in range(3)]
    for k in order: m.dispose(ns[k])
    m.dispose(e)
    f = m.enter("fresh", set()); m.dispose(f)
print("three-frame:", m.length())

m = Model()
for order in perms(3):
    e = m.enter("E", set()); ns = [m.enter("a", {"a"}), m.detach(), m.enter("c", {"c"})]
    for k in order: m.dispose(ns[k])
    m.dispose(e)
print("detach-middle:", m.length())

m = Model()
for shape in range(16):
    for order in perms(4):
        ns = []
        for i in range(5):
            d = i > 0 and ((shape >> (i - 1)) & 1) == 1
            ns.append(m.detach() if d else m.enter(str(i), {i}))
        for k in [o + 1 for o in order]: m.dispose(ns[k])
        m.dispose(ns[0])
        f = m.enter("fresh", set()); m.dispose(f)
print("four-frame shapes:", m.length())

# The stay-outer twin's lengths (1, i+2 over 50 iterations, 53): no reads, unchanged.
m = Model()
o = m.enter("stay-outer", {"S"}); i_ = m.enter("stay-inner", set()); m.dispose(o); m.dispose(i_)
lengths = [m.length()]
for i in range(50):
    lo = m.enter("loop-outer", {"L%d" % i}); li = m.enter("loop-inner", set()); m.dispose(lo); m.dispose(li)
    lengths.append(m.length())
enc = m.enter("enclosing", set()); no = m.enter("nested-outer", {"S"}); ni = m.enter("nested-inner", set()); m.dispose(no); m.dispose(ni)
lengths.append(m.length()); m.dispose(enc); lengths.append(m.length())
print("stay-outer twin lengths: first %d, loop i+2 holds: %s, then %d, after enclosing dispose %d" % (
    lengths[0], all(lengths[1 + i] == i + 2 for i in range(50)), lengths[51], lengths[52]))

print()
print("== Part 2: candidate pins for 4.5 twins (read frames) ==")
# (a) three-frame orders, each frame's work under a read scope that ends IN ORDER on the flow before any dispose:
#     the read frames come and go, and the chain left is the same 13.
m = Model()
for order in perms(3):
    e = m.enter("E", set()); ns = []
    for i in range(3):
        ns.append(m.enter(str(i), {i}))
        r = m.begin_read(); m.end_read(r, {"r%d" % i})          # in order: begun and ended on the flow as its head
    for k in order: m.dispose(ns[k])
    m.dispose(e)
    f = m.enter("fresh", set()); m.dispose(f)
print("(a) three-frame orders with an in-order read inside each frame:", m.length(), "(reads in order add nothing)")

# (b) a read ended on another flow, or in an awaited helper, in a loop: the begin flow stays inside each read frame,
#     so its chain grows by exactly one frame per iteration (4.4 limit (a) for read frames).
m = Model()
enc = m.enter("session", set())
lens = []
for i in range(50):
    r = m.begin_read(); m.end_read(r, {"hit%d" % i}, on_flow=False)
    lens.append(m.length())
print("(b) read ended off-flow per iteration: lengths start %d, i+2 holds for 50 iterations: %s, final %d" % (
    lens[0], all(lens[i] == i + 2 for i in range(50)), lens[-1]))

# (c) a read ended in order per iteration leaves nothing: length stays 1.
m = Model()
enc = m.enter("session", set())
ok = True
for i in range(50):
    r = m.begin_read(); m.end_read(r, {"hit%d" % i}); ok = ok and m.length() == 1
print("(c) read ended in order per iteration: length stays 1 for 50 iterations:", ok)

# (d) a frame entered inside an open read and left undisposed when the read ends: the read frame is not the head, so
#     the flow stays inside both (+2); disposing the frame afterwards leaves the flow on the disposed read frame (+1).
m = Model()
enc = m.enter("session", set())
r = m.begin_read(); inner = m.enter("tool-frame", {"t"}); m.end_read(r, TOP)
a = m.length(); m.dispose(inner); b = m.length()
print("(d) read ended under an undisposed frame entered inside it: %d, then %d after that frame is disposed" % (a, b))

# (e) every program of an enclosing frame, up to two frames and up to two reads on one flow, each read ended by the
#     flow (in order or not) and every frame disposed in any order: programs and frames left (a program-twin candidate).
def programs(frames, reads):
    out = []
    def extend(prog, entered, live, begun, open_reads):
        if (live & ~1) == 0 and open_reads == 0 and prog:
            out.append(list(prog))
        if entered <= frames: extend(prog + ["E"], entered + 1, live | (1 << entered), begun, open_reads)
        for i in range(entered):
            if live & (1 << i): extend(prog + ["X%d" % i], entered, live & ~(1 << i), begun, open_reads)
        if begun < reads: extend(prog + ["R"], entered, live, begun + 1, open_reads | (1 << begun))
        for j in range(begun):
            if open_reads & (1 << j): extend(prog + ["Z%d" % j], entered, live, begun, open_reads & ~(1 << j))
    extend([], 1, 1, 0, 0)
    return out
progs = programs(2, 2)
left = 0
for prog in progs:
    m = Model(); fr = [m.enter("0", {0})]; rs = []
    for op in prog:
        if op == "E": fr.append(m.enter(str(len(fr)), {len(fr)}))
        elif op == "R": rs.append(m.begin_read())
        elif op[0] == "X": m.dispose(fr[int(op[1:])])
        else: m.end_read(rs[int(op[1:])], {"r"})
    left += m.length()
print("(e) programs of 1+2 frames and 2 reads on one flow, every frame disposed and every read ended: %d programs, %d frames left" % (len(progs), left))
