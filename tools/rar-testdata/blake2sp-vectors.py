#!/usr/bin/env python3
"""Prints BLAKE2sp digests of a fixed set of inputs, computed with Python's hashlib (BLAKE2s with tree parameters) as a black box.

The tree is the one the BLAKE2 paper defines for BLAKE2sp: eight leaves (fanout 8, depth 2, inner length 32), leaf i hashing the
64-byte blocks i, i + 8, i + 16, ..., the last leaf marked as the last node, and a root over the eight leaf digests.
The input of length n is bytes (i % 251) for i in 0 .. n - 1. The output lines are copied into the unit tests.
"""
import hashlib


def blake2sp(data: bytes) -> str:
    leaves = [hashlib.blake2s(fanout=8, depth=2, leaf_size=0, node_offset=i, node_depth=0, inner_size=32, last_node=(i == 7)) for i in range(8)]
    for block in range(0, len(data), 64):
        leaves[(block // 64) % 8].update(data[block:block + 64])
    root = hashlib.blake2s(fanout=8, depth=2, leaf_size=0, node_offset=0, node_depth=1, inner_size=32, last_node=True)
    for leaf in leaves:
        root.update(leaf.digest())
    return root.hexdigest()


for n in (0, 1, 63, 64, 65, 127, 128, 255, 256, 511, 512, 513, 1000, 1024, 4096, 4097, 100000):
    print(n, blake2sp(bytes(i % 251 for i in range(n))))
