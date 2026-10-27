#!/usr/bin/env python3
"""Writes N small game-state style payloads (150-300 bytes each) as files p00000..: payloads.py DIR N [SEED]."""
import os
import sys

MASK = (1 << 64) - 1
WORDS = ("sword shield potion key gem arrow torch map scroll bread rope lamp coin ring bow").split()


class SplitMix:
    def __init__(self, seed):
        self.s = seed & MASK

    def next(self):
        self.s = (self.s + 0x9E3779B97F4A7C15) & MASK
        z = self.s
        z = ((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9) & MASK
        z = ((z ^ (z >> 27)) * 0x94D049BB133111EB) & MASK
        return z ^ (z >> 31)


def payload(seed, i):
    r = SplitMix(seed * 1000003 + i)
    out = '{"tick":%d,"entity":%d,"pos":[%d,%d,%d],"health":%d,"state":"%s","inventory":[' % (
        1000 + i, r.next() % 64, r.next() % 512, r.next() % 64, r.next() % 512, r.next() % 100,
        ["idle", "walking", "attacking", "falling"][r.next() % 4])
    for _ in range(3 + r.next() % 4):
        out += '{"item":"%s","count":%d},' % (WORDS[r.next() % len(WORDS)], r.next() % 20)
    return (out + '],"chunk":[%d,%d]}' % (r.next() % 32, r.next() % 32)).encode()


directory, count = sys.argv[1], int(sys.argv[2])
seed = int(sys.argv[3]) if len(sys.argv) > 3 else 7
os.makedirs(directory, exist_ok=True)
for i in range(count):
    with open(os.path.join(directory, "p%05d" % i), "wb") as f:
        f.write(payload(seed, i))
