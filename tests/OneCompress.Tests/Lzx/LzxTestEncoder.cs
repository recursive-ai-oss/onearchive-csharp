#pragma warning disable CS1591 // Test helper shared with the hash-matrix host; not part of the library API.

using RecursiveAI.OneCompress.Lzx;

namespace RecursiveAI.OneCompress.Tests.Lzx;

/// <summary>
/// A small LZX compressor for tests, written from the format description: greedy matching, canonical Huffman codes, and every block
/// type. Its output is judged by the decoder under test and by an independent extractor, so it only has to be valid, not good.
/// </summary>
public sealed class LzxTestEncoder
{
    public enum Kind
    {
        Verbatim,
        Aligned,
        Uncompressed,
    }

    public sealed class Settings
    {
        /// <summary>The window exponent (15 to 21).</summary>
        public int WindowBits { get; init; } = 16;

        /// <summary>Uncompressed bytes per block. Blocks cycle through <see cref="Kinds"/>.</summary>
        public int BlockSize { get; init; } = 50000;

        public Kind[] Kinds { get; init; } = [Kind.Verbatim, Kind.Aligned];

        /// <summary>When set, x86 call translation is enabled with this translation size.</summary>
        public uint? TranslationSize { get; init; }

        /// <summary>Longest match; at most 257 unless <see cref="ExtraLengths"/> is set.</summary>
        public int MaxMatch { get; init; } = 257;

        /// <summary>Write the extra-length field after a match token of 257 (the form [MS-PATCH] describes).</summary>
        public bool ExtraLengths { get; init; }

        /// <summary>Repeat codes (19) repeat a length difference rather than a length.</summary>
        public bool RunsAreDeltas { get; init; }

        /// <summary>Skip the four operand bytes of a call only when it was translated.</summary>
        public bool SkipOnlyWhenTranslated { get; init; }

        /// <summary>Never use the repeated-offset slots.</summary>
        public bool NoRepeats { get; init; }

        /// <summary>Insert a whole zero word before uncompressed data even when the header ends on a word boundary.</summary>
        public bool FullWordPadding { get; init; } = true;
    }

    private sealed class BitWriter
    {
        private readonly List<byte> _bytes = [];
        private uint _acc;
        private int _n;

        public int ByteCount => _bytes.Count;

        public void Write(uint value, int bits)
        {
            for (int i = bits - 1; i >= 0; i--)
            {
                _acc = (_acc << 1) | ((value >> i) & 1);
                if (++_n == 16)
                {
                    _bytes.Add((byte)(_acc & 0xFF));
                    _bytes.Add((byte)(_acc >> 8));
                    _acc = 0;
                    _n = 0;
                }
            }
        }

        public int BitsInWord => _n;

        public void PadToWord()
        {
            while (_n != 0)
            {
                Write(0, 1);
            }
        }

        public void AppendBytes(ReadOnlySpan<byte> data) => _bytes.AddRange(data.ToArray());

        public byte[] TakeBytes()
        {
            byte[] r = [.. _bytes];
            _bytes.Clear();
            return r;
        }
    }

    private readonly struct Token(int literalOrLength, int offset)
    {
        public readonly int Value = literalOrLength; // literal byte when Offset == 0, else match length
        public readonly int Offset = offset;
    }

    /// <summary>Compresses <paramref name="data"/> and returns the compressed bytes of each frame.</summary>
    public static List<byte[]> Encode(byte[] data, Settings settings)
    {
        var enc = new LzxTestEncoder(data, settings);
        return enc.Run();
    }

    private readonly byte[] _original;
    private readonly byte[] _data;
    private readonly Settings _s;
    private readonly int _slots;
    private readonly byte[] _mainLens;
    private readonly byte[] _lenLens = new byte[249];
    private readonly BitWriter _w = new();
    private readonly List<byte[]> _frames = [];
    private int _r0 = 1;
    private int _r1 = 1;
    private int _r2 = 1;
    private int _frameBytesDone;

    private LzxTestEncoder(byte[] data, Settings s)
    {
        _original = data;
        _s = s;
        _slots = LzxPositionSlots.GetSlotCount(s.WindowBits);
        _mainLens = new byte[256 + (8 * _slots)];
        _data = s.TranslationSize is { } size ? Translate(data, size, s.SkipOnlyWhenTranslated) : data;
    }

    // [MS-PATCH] 2.2.2 forward translation, frame by frame.
    private static byte[] Translate(byte[] data, uint fileSize, bool skipOnlyWhenTranslated)
    {
        byte[] d = (byte[])data.Clone();
        for (int frame = 0; frame * 32768L < d.Length; frame++)
        {
            long chunkOffset = frame * 32768L;
            int size = (int)Math.Min(32768, d.Length - chunkOffset);
            if (chunkOffset >= 0x40000000 || size <= 10)
            {
                continue;
            }

            int baseIndex = (int)chunkOffset;
            for (int i = 0; i < size - 10; i++)
            {
                if (d[baseIndex + i] != 0xE8)
                {
                    continue;
                }

                long current = chunkOffset + i;
                long disp = BitConverter.ToInt32(d, baseIndex + i + 1);
                long target = current + disp;
                if (target >= 0 && target < fileSize + current)
                {
                    if (target >= fileSize)
                    {
                        target = disp - fileSize;
                    }

                    BitConverter.TryWriteBytes(d.AsSpan(baseIndex + i + 1), (int)target);
                    i += 4;
                }
                else if (!skipOnlyWhenTranslated)
                {
                    i += 4;
                }
            }
        }

        return d;
    }

    private List<byte[]> Run()
    {
        if (_s.TranslationSize is { } size)
        {
            _w.Write(1, 1);
            _w.Write(size >> 16, 16);
            _w.Write(size & 0xFFFF, 16);
        }
        else
        {
            _w.Write(0, 1);
        }

        Token[] tokens = Parse(out int[] tokenStart);
        int block = 0;
        int pos = 0;
        int ti = 0;
        while (pos < _data.Length)
        {
            int blockLength = Math.Min(_s.BlockSize, _data.Length - pos);
            Kind kind = _s.Kinds[block % _s.Kinds.Length];
            int tEnd = ti;
            while (tEnd < tokens.Length && tokenStart[tEnd] < pos + blockLength)
            {
                tEnd++;
            }

            EmitBlock(kind, pos, blockLength, tokens.AsSpan(ti, tEnd - ti));
            ti = tEnd;
            pos += blockLength;
            block++;
        }

        FinishFrame();
        return _frames;
    }

    // Greedy hash-chain parse; no token crosses a block or frame boundary.
    private Token[] Parse(out int[] starts)
    {
        var tokens = new List<Token>();
        var startList = new List<int>();
        int n = _data.Length;
        int maxOffset = (1 << _s.WindowBits) - 3;
        var head = new int[1 << 16];
        Array.Fill(head, -1);
        var prev = new int[n];
        int maxMatch = _s.ExtraLengths ? _s.MaxMatch : Math.Min(_s.MaxMatch, 257);
        int pos = 0;
        while (pos < n)
        {
            int blockEnd = Math.Min(n, ((pos / _s.BlockSize) + 1) * _s.BlockSize);
            int frameEnd = Math.Min(n, ((pos / 32768) + 1) * 32768);
            int limit = Math.Min(Math.Min(blockEnd, frameEnd) - pos, maxMatch);
            int bestLen = 0;
            int bestOff = 0;
            if (limit >= 3 && pos + 2 < n)
            {
                int h = Hash(pos);
                int cand = head[h];
                for (int tries = 0; cand >= 0 && tries < 32 && pos - cand <= maxOffset; tries++)
                {
                    int len = 0;
                    while (len < limit && _data[cand + len] == _data[pos + len])
                    {
                        len++;
                    }

                    if (len > bestLen)
                    {
                        bestLen = len;
                        bestOff = pos - cand;
                    }

                    cand = prev[cand];
                }
            }

            int step;
            startList.Add(pos);
            if (bestLen >= 3)
            {
                tokens.Add(new Token(bestLen, bestOff));
                step = bestLen;
            }
            else
            {
                tokens.Add(new Token(_data[pos], 0));
                step = 1;
            }

            for (int k = 0; k < step; k++)
            {
                int p = pos + k;
                if (p + 2 < n)
                {
                    int h = Hash(p);
                    prev[p] = head[h];
                    head[h] = p;
                }
            }

            pos += step;
        }

        starts = [.. startList];
        return [.. tokens];
    }

    private int Hash(int p) => ((_data[p] << 8) ^ (_data[p + 1] << 4) ^ _data[p + 2] ^ (_data[p + 2] << 11)) & 0xFFFF;

    private void EmitBlock(Kind kind, int pos, int size, ReadOnlySpan<Token> tokens)
    {
        _w.Write(kind == Kind.Verbatim ? 1u : kind == Kind.Aligned ? 2u : 3u, 3);
        _w.Write((uint)size, 24);
        if (kind == Kind.Uncompressed)
        {
            if (_w.BitsInWord == 0 && _s.FullWordPadding)
            {
                _w.Write(0, 16);
            }

            _w.PadToWord();
            Span<byte> r = stackalloc byte[12];
            BitConverter.TryWriteBytes(r, (uint)_r0);
            BitConverter.TryWriteBytes(r[4..], (uint)_r1);
            BitConverter.TryWriteBytes(r[8..], (uint)_r2);
            _w.AppendBytes(r);
            int done = 0;
            while (done < size)
            {
                int n = Math.Min(32768 - _frameBytesDone, size - done);
                _w.AppendBytes(_data.AsSpan(pos + done, n));
                done += n;
                _frameBytesDone += n;
                if (done == size && (size & 1) != 0)
                {
                    _w.AppendBytes([0]);
                }

                if (_frameBytesDone == 32768)
                {
                    EndFrame(false);
                }
            }

            return;
        }

        bool aligned = kind == Kind.Aligned;
        var mainFreq = new int[_mainLens.Length];
        var lenFreq = new int[249];
        var alignedFreq = new int[8];
        var coded = new List<(int Main, int LenFooter, int Extra, int ExtraBits, int Aligned, int ExtraLen)>();
        int r0 = _r0, r1 = _r1, r2 = _r2;
        foreach (Token t in tokens)
        {
            if (t.Offset == 0)
            {
                mainFreq[t.Value]++;
                coded.Add((t.Value, -1, 0, 0, -1, -1));
                continue;
            }

            int len = t.Value;
            int slot;
            int footerValue = 0;
            int footerBits = 0;
            int alignedBits = -1;
            if (!_s.NoRepeats && t.Offset == r0)
            {
                slot = 0;
            }
            else if (!_s.NoRepeats && t.Offset == r1)
            {
                slot = 1;
                (r0, r1) = (r1, r0);
            }
            else if (!_s.NoRepeats && t.Offset == r2)
            {
                slot = 2;
                (r0, r2) = (r2, r0);
            }
            else
            {
                int formatted = t.Offset + 2;
                slot = 3;
                while (slot + 1 < _slots && LzxPositionSlots.GetBasePosition(slot + 1) <= formatted)
                {
                    slot++;
                }

                footerBits = LzxPositionSlots.GetFooterBits(slot);
                footerValue = formatted - LzxPositionSlots.GetBasePosition(slot);
                if (aligned && footerBits >= 3)
                {
                    alignedBits = footerValue & 7;
                    footerValue >>= 3;
                    footerBits -= 3;
                    alignedFreq[alignedBits]++;
                }

                r2 = r1;
                r1 = r0;
                r0 = t.Offset;
            }

            int lenHeader = len <= 8 ? len - 2 : 7;
            int lenFooter = len <= 8 ? -1 : Math.Min(len, 257) - 9;
            int main = 256 + (slot << 3) + lenHeader;
            mainFreq[main]++;
            if (lenFooter >= 0)
            {
                lenFreq[lenFooter]++;
            }

            int extraLen = len >= 257 && _s.ExtraLengths ? len - 257 : -1;
            coded.Add((main, lenFooter, footerValue, footerBits, alignedBits, extraLen));
        }

        byte[] alignedLens = new byte[8];
        if (aligned)
        {
            // An aligned-offset tree is always sent complete: a reference extractor refuses an empty one.
            alignedLens = alignedFreq.All(f => f == 0) ? [3, 3, 3, 3, 3, 3, 3, 3] : BuildLengths(alignedFreq, 7);
            for (int i = 0; i < 8; i++)
            {
                _w.Write(alignedLens[i], 3);
            }
        }

        byte[] newMain = BuildLengths(mainFreq, 16);
        byte[] newLen = BuildLengths(lenFreq, 16);
        WriteLengths(_mainLens.AsSpan(0, 256), newMain.AsSpan(0, 256));
        WriteLengths(_mainLens.AsSpan(256), newMain.AsSpan(256));
        WriteLengths(_lenLens, newLen);
        newMain.CopyTo(_mainLens, 0);
        newLen.CopyTo(_lenLens, 0);

        uint[] mainCodes = Canonical(_mainLens);
        uint[] lenCodes = Canonical(_lenLens);
        uint[] alignedCodes = Canonical(alignedLens);

        int tokenPos = pos;
        foreach (var c in coded)
        {
            _w.Write(mainCodes[c.Main], _mainLens[c.Main]);
            int produced;
            if (c.Main < 256)
            {
                produced = 1;
            }
            else
            {
                int lenHeader = (c.Main - 256) & 7;
                produced = lenHeader == 7 ? 0 : lenHeader + 2;
                if (c.LenFooter >= 0)
                {
                    _w.Write(lenCodes[c.LenFooter], _lenLens[c.LenFooter]);
                    produced = c.LenFooter + 9;
                }

                if (c.ExtraBits > 0)
                {
                    _w.Write((uint)c.Extra, c.ExtraBits);
                }

                if (c.Aligned >= 0)
                {
                    _w.Write(alignedCodes[c.Aligned], alignedLens[c.Aligned]);
                }

                if (c.ExtraLen >= 0)
                {
                    WriteExtraLength(c.ExtraLen);
                    produced += c.ExtraLen;
                }
            }

            tokenPos += produced;
            AdvanceFrame(produced);
        }

        _ = tokenPos;
        (_r0, _r1, _r2) = (r0, r1, r2);
    }

    private void WriteExtraLength(int v)
    {
        // [MS-PATCH] 2.6.6.
        if (v < 256)
        {
            _w.Write(0, 1);
            _w.Write((uint)v, 8);
        }
        else if (v < 256 + 1024)
        {
            _w.Write(2, 2);
            _w.Write((uint)(v - 256), 10);
        }
        else if (v < 256 + 1024 + 4096)
        {
            _w.Write(6, 3);
            _w.Write((uint)(v - 256 - 1024), 12);
        }
        else
        {
            _w.Write(7, 3);
            _w.Write((uint)v, 15);
        }
    }

    private void AdvanceFrame(int produced)
    {
        _frameBytesDone += produced;
        if (_frameBytesDone == 32768)
        {
            EndFrame(false);
        }
    }

    private void EndFrame(bool rawOddPending)
    {
        _ = rawOddPending;
        _w.PadToWord();
        _frames.Add(_w.TakeBytes());
        _frameBytesDone = 0;
    }

    private void FinishFrame()
    {
        if (_frameBytesDone > 0)
        {
            _w.PadToWord();
            _frames.Add(_w.TakeBytes());
            _frameBytesDone = 0;
        }
    }

    // [MS-PATCH] 2.5.
    private void WriteLengths(Span<byte> previous, ReadOnlySpan<byte> next)
    {
        var syms = new List<(int Sym, int Extra, int ExtraBits)>();
        int x = 0;
        while (x < next.Length)
        {
            int run = 1;
            if (_s.RunsAreDeltas && next[x] != 0)
            {
                int delta = (previous[x] - next[x] + 17) % 17;
                while (x + run < next.Length && next[x + run] != 0 && (previous[x + run] - next[x + run] + 17) % 17 == delta)
                {
                    run++;
                }
            }
            else
            {
                while (x + run < next.Length && next[x + run] == next[x])
                {
                    run++;
                }
            }

            if (next[x] == 0 && run >= 4)
            {
                int take = Math.Min(run, 51);
                if (take >= 20)
                {
                    syms.Add((18, take - 20, 5));
                }
                else
                {
                    syms.Add((17, take - 4, 4));
                }

                x += take;
            }
            else if (next[x] != 0 && run >= 4)
            {
                int take = Math.Min(run, 5);
                int code = (previous[x] - next[x] + 17) % 17;
                syms.Add((19, take - 4, 1));
                syms.Add((code, 0, 0));
                x += take;
            }
            else
            {
                syms.Add(((previous[x] - next[x] + 17) % 17, 0, 0));
                x++;
            }
        }

        var freq = new int[20];
        // The code after a repeat marker is a plain difference code; both count toward the pretree.
        foreach (var s in syms)
        {
            freq[s.Sym]++;
        }

        byte[] preLens = BuildLengths(freq, 15);
        for (int i = 0; i < 20; i++)
        {
            _w.Write(preLens[i], 4);
        }

        uint[] codes = Canonical(preLens);
        foreach (var s in syms)
        {
            _w.Write(codes[s.Sym], preLens[s.Sym]);
            if (s.ExtraBits > 0)
            {
                _w.Write((uint)s.Extra, s.ExtraBits);
            }
        }
    }

    private static byte[] BuildLengths(int[] freq, int limit)
    {
        var result = new byte[freq.Length];
        int used = 0;
        for (int i = 0; i < freq.Length; i++)
        {
            if (freq[i] > 0)
            {
                used++;
            }
        }

        if (used == 0)
        {
            return result;
        }

        int[] f = (int[])freq.Clone();
        if (used == 1)
        {
            // A tree needs at least two codes.
            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] == 0)
                {
                    f[i] = 1;
                    break;
                }
            }
        }

        while (true)
        {
            int[] depth = HuffmanDepths(f);
            if (depth.Max() <= limit)
            {
                for (int i = 0; i < depth.Length; i++)
                {
                    result[i] = (byte)depth[i];
                }

                return result;
            }

            for (int i = 0; i < f.Length; i++)
            {
                if (f[i] > 0)
                {
                    f[i] = (f[i] + 1) / 2;
                }
            }
        }
    }

    private static int[] HuffmanDepths(int[] freq)
    {
        var depth = new int[freq.Length];
        var nodes = new List<(long Weight, int Id, List<int> Leaves)>();
        for (int i = 0; i < freq.Length; i++)
        {
            if (freq[i] > 0)
            {
                nodes.Add((freq[i], i, [i]));
            }
        }

        if (nodes.Count == 1)
        {
            depth[nodes[0].Leaves[0]] = 1;
            return depth;
        }

        int nextId = freq.Length;
        while (nodes.Count > 1)
        {
            nodes.Sort((a, b) => a.Weight != b.Weight ? a.Weight.CompareTo(b.Weight) : a.Id.CompareTo(b.Id));
            var a0 = nodes[0];
            var b0 = nodes[1];
            nodes.RemoveRange(0, 2);
            foreach (int l in a0.Leaves)
            {
                depth[l]++;
            }

            foreach (int l in b0.Leaves)
            {
                depth[l]++;
            }

            var merged = new List<int>(a0.Leaves);
            merged.AddRange(b0.Leaves);
            nodes.Add((a0.Weight + b0.Weight, nextId++, merged));
        }

        return depth;
    }

    private static uint[] Canonical(byte[] lens)
    {
        var codes = new uint[lens.Length];
        uint code = 0;
        for (int len = 1; len <= 16; len++)
        {
            for (int s = 0; s < lens.Length; s++)
            {
                if (lens[s] == len)
                {
                    codes[s] = code++;
                }
            }

            code <<= 1;
        }

        return codes;
    }
}
