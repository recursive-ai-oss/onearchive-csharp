"""A small compound file model used to generate damaged vectors and to say what a recovering reader should still find.

It reads a good file, so every offset the damage generator needs (FAT slots, directory entries, chains) is looked up here
rather than guessed. It is not a reader to trust with hostile input.
"""
import gzip
import struct

END, FREE, FATSECT, DIFSECT, NOSTREAM = 0xFFFFFFFE, 0xFFFFFFFF, 0xFFFFFFFD, 0xFFFFFFFC, 0xFFFFFFFF


def load(path):
    raw = open(path, "rb").read()
    return gzip.decompress(raw) if path.endswith(".gz") else raw


class Cfb:
    def __init__(self, data):
        self.data = bytearray(data)
        d = self.data
        self.major = struct.unpack_from("<H", d, 26)[0]
        self.shift = struct.unpack_from("<H", d, 30)[0]
        self.size = 1 << self.shift
        self.per = self.size // 4
        (self.num_fat, self.first_dir, _, self.cutoff, self.first_mini, self.num_mini, self.first_dif, self.num_dif) = struct.unpack_from("<IIIIIIII", d, 44)
        self.difat_slots = []  # (offset of the 4-byte slot, sector or FREE)
        for i in range(109):
            self.difat_slots.append((76 + 4 * i, struct.unpack_from("<I", d, 76 + 4 * i)[0]))
        link = self.first_dif
        self.difat_sectors = []
        while link != END and link != FREE and len(self.difat_sectors) < self.num_dif:
            self.difat_sectors.append(link)
            base = self.off(link)
            for i in range(self.per - 1):
                self.difat_slots.append((base + 4 * i, struct.unpack_from("<I", d, base + 4 * i)[0]))
            link = struct.unpack_from("<I", d, base + self.size - 4)[0]
        self.fat_sectors = [s for _, s in self.difat_slots if s != FREE]
        self.fat = []
        for s in self.fat_sectors:
            self.fat += struct.unpack_from("<%dI" % self.per, d, self.off(s))
        self.dir_sectors = self.chain(self.first_dir)
        self.entries = []
        for si, s in enumerate(self.dir_sectors):
            for e in range(self.size // 128):
                o = self.off(s) + 128 * e
                cb = struct.unpack_from("<H", d, o + 64)[0]
                name = bytes(d[o:o + max(0, cb - 2)]).decode("utf-16-le", "replace") if cb >= 2 else ""
                typ = d[o + 66]
                left, right, child = struct.unpack_from("<III", d, o + 68)
                start = struct.unpack_from("<I", d, o + 116)[0]
                sz = struct.unpack_from("<Q", d, o + 120)[0]
                if self.major == 3:
                    sz &= 0xFFFFFFFF
                self.entries.append(dict(sid=si * (self.size // 128) + e, off=o, name=name, type=typ, left=left, right=right, child=child, start=start, size=sz))
        self.mini_chain = self.chain(self.first_mini) if self.num_mini else []
        self.mini_fat = []
        for s in self.mini_chain:
            self.mini_fat += struct.unpack_from("<%dI" % self.per, d, self.off(s))
        self.root = self.entries[0]
        self.mini_stream = self.chain(self.root["start"]) if self.root["size"] else []

    def off(self, sector):
        return (sector + 1) << self.shift

    def chain(self, start):
        out, cur = [], start
        while cur != END and cur < len(self.fat) and cur not in out:
            out.append(cur)
            cur = self.fat[cur]
        return out

    def streams(self):
        """Allocated stream entries with their tree paths (tuples of names), found by walking the tree."""
        res = []

        def walk(sid, path):
            def inorder(n):
                if n == NOSTREAM:
                    return
                e = self.entries[n]
                yield from inorder(e["left"])
                yield n
                yield from inorder(e["right"])
            for n in inorder(self.entries[sid]["child"]):
                e = self.entries[n]
                p = path + (e["name"],)
                res.append((p, e))
                if e["type"] == 1:
                    walk(n, p)
        walk(0, ())
        return res

    def is_mini(self, e):
        return 0 < e["size"] < self.cutoff

    # --- what a reader should be able to read after damage; the model arguments describe the damaged file ---
    def readable(self, e, fat, mini_fat, mini_stream_bytes, file_len):
        """Bytes of a stream that can be followed in the given (damaged) tables: the prefix before the first break."""
        size = e["size"]
        if size == 0:
            return 0
        if self.is_mini(e):
            n = -(-size // 64)
            got, cur, seen = 0, e["start"], set()
            while len(seen) < n:
                if cur >= -(-mini_stream_bytes // 64) or cur in seen:
                    break
                seen.add(cur)
                got += min(64, mini_stream_bytes - cur * 64)
                cur = mini_fat[cur] if cur < len(mini_fat) else FREE
            return min(size, got)
        n = -(-size // self.size)
        sectors = -(-(file_len) // self.size) - 1
        got, cur, seen = 0, e["start"], set()
        while len(seen) < n:
            if cur >= sectors or cur in seen:
                break
            seen.add(cur)
            present = min(self.size, file_len - self.off(cur))
            got += present
            if present < self.size:
                break
            cur = fat[cur] if cur < len(fat) else FREE
        return min(size, got)

    def mini_bytes(self, fat, file_len):
        """How many bytes of the mini stream its (possibly damaged) sector chain still holds."""
        got, cur, seen = 0, self.root["start"], set()
        sectors = -(-(file_len) // self.size) - 1
        while cur != END and len(seen) < sectors:
            if cur >= sectors or cur in seen:
                break
            seen.add(cur)
            present = min(self.size, file_len - self.off(cur))
            got += present
            if present < self.size:
                break
            cur = fat[cur] if cur < len(fat) else FREE
        declared = self.root["size"]
        if declared > got or declared < got - (self.size - 1):
            return got
        return declared
