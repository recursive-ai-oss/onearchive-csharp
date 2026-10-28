# Annotates an xctrace Time Profiler export with JIT disassembly: where the samples fall inside the hottest compiled methods.
# Used by profile-mac.sh; needs the perf map (DOTNET_PerfMapEnabled=1) and a JitDisasm listing of the same run.
import sys, re, bisect, collections
import xml.etree.ElementTree as ET
# usage: prof2.py trace.xml perf.map code.asm [topfuncs] [lines]
xmlp, mapp, asmp = sys.argv[1], sys.argv[2], sys.argv[3]
topf = int(sys.argv[4]) if len(sys.argv) > 4 else 3
ctx = int(sys.argv[5]) if len(sys.argv) > 5 else 4
ranges = []
for line in open(mapp, errors='replace'):
    p = line.rstrip('\n').split(' ', 2)
    if len(p) == 3:
        try:
            ranges.append((int(p[0], 16), int(p[1], 16), p[2]))
        except ValueError:
            pass
ranges.sort()
starts = [r[0] for r in ranges]
def jit(addr):
    i = bisect.bisect_right(starts, addr) - 1
    if i >= 0 and addr < ranges[i][0] + ranges[i][1]:
        return i, addr - ranges[i][0]
    return None, 0
frames = {}; bts = {}
counts = collections.Counter(); offs = collections.defaultdict(collections.Counter); total = 0
for ev, el in ET.iterparse(xmlp, events=('end',)):
    if el.tag == 'frame':
        fid = el.get('id')
        if fid is not None:
            a = el.get('addr'); frames[fid] = (el.get('name'), int(a, 16) if a else 0)
    elif el.tag == 'tagged-backtrace':
        bid = el.get('id')
        if bid is not None:
            ch = list(el.iter('frame'))
            if ch: bts[bid] = ch[0].get('id') or ch[0].get('ref')
    elif el.tag == 'row':
        tb = el.find('tagged-backtrace')
        if tb is None: continue
        key = bts.get(tb.get('id') or tb.get('ref'))
        if key is None: continue
        name, addr = frames.get(key, (None, 0))
        total += 1
        i, off = jit(addr)
        if i is not None:
            counts[i] += 1; offs[i][off] += 1
        else:
            counts[name or hex(addr)] += 1
        el.clear()
print('samples', total)
for k, c in counts.most_common(14):
    n = ranges[k][2] if isinstance(k, int) else k
    print('%6d %5.1f%%  %s' % (c, 100.0 * c / total, re.sub(r'\[RecursiveAI.OneCompress\] RecursiveAI.OneCompress.', '', n)[:120]))
# asm listings
lists = []; cur = None
for line in open(asmp, errors='replace'):
    if line.startswith('; Assembly listing for method'):
        cur = {'hdr': line.strip(), 'ins': [], 'size': 0}; lists.append(cur)
    elif cur is not None:
        m = re.match(r'^\s+([0-9A-Fa-f]{8,})\s{2,}(.*)$', line.rstrip('\n'))
        if m and not line.lstrip().startswith(';'):
            cur['ins'].append((m.group(1), m.group(2)))
        m2 = re.match(r'^; Total bytes of code (\d+)', line)
        if m2: cur['size'] = int(m2.group(1))
shown = 0
for k, c in counts.most_common(14):
    if not isinstance(k, int): continue
    name = ranges[k][2]; size = ranges[k][1]
    # pick listing whose code size is within 64 bytes of the map size and whose method name matches
    meth = re.search(r'::(\w+)\(', name)
    cands = [l for l in lists if meth and (':' + meth.group(1) + '(') in l['hdr'] and abs(l['size'] - size) <= 96 and l['ins']]
    if not cands: continue
    L = cands[-1]
    # offsets
    pos = []; o = 0
    for b, t in L['ins']:
        pos.append(o); o += len(b) // 2
    hist = collections.Counter()
    for off, n in offs[k].items():
        hist[off] += n
    print('=== %s  (%d samples, listing size %d)' % (name[-70:], c, L['size']))
    top = hist.most_common(6)
    for off, n in top:
        idx = bisect.bisect_right(pos, off) - 1
        print('  +0x%x  %d samples' % (off, n))
        for j in range(max(0, idx - ctx), min(len(L['ins']), idx + ctx + 1)):
            mark = '>>' if j == idx else '  '
            print('   %s %4x %s' % (mark, pos[j], L['ins'][j][1][:90]))
    shown += 1
    if shown >= topf: break
