# Port of EgressSubjectNestingTests.FlowModel (no-skip rule) to compute the literal restore-path pins.
class Node:
    def __init__(s, name, mark, prev, outer):
        s.name, s.mark, s.prev, s.outer, s.disposed = name, mark, prev, outer, False

class Model:
    def __init__(s): s.head = None
    def enter(s, name, mark):
        s.head = Node(name, set(mark), s.head, s.head); return s.head
    def detach(s):
        s.head = Node("D", None, None, s.head); return s.head
    def dispose(s, n):
        if n.disposed: return
        if n.mark is not None:
            a = n.prev
            while a is not None and a.mark is not None:
                a.mark |= n.mark; a = a.prev
        n.disposed = True
        if s.head is n: s.head = n.outer
    def length(s):
        k, a = 0, s.head
        while a is not None: k += 1; a = a.outer
        return k

def perms(n):
    if n == 1:
        yield [0]; return
    for shorter in perms(n - 1):
        for at in range(len(shorter) + 1):
            l = list(shorter); l.insert(at, n - 1); yield l

# three frames
m = Model()
for order in perms(3):
    e = m.enter("E", set()); ns = [m.enter(str(i), {i}) for i in range(3)]
    for k in order: m.dispose(ns[k])
    m.dispose(e)
    f = m.enter("fresh", set()); m.dispose(f)
print("three-frame:", m.length())

# detachment in the middle
m = Model()
for order in perms(3):
    e = m.enter("E", set()); ns = [m.enter("a", {"a"}), m.detach(), m.enter("c", {"c"})]
    for k in order: m.dispose(ns[k])
    m.dispose(e)
print("detach-middle:", m.length())

# four frames and detachments, 384 programs
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
