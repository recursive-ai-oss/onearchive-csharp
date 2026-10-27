using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// The training samples laid end to end, with the start of each. Empty samples are dropped. If the samples hold more than the
/// byte limit, every n-th sample is kept (n depends only on the sizes), and what still does not fit is cut off at the limit.
/// </summary>
internal sealed class ZstdTrainingSet
{
    // Reads of up to 8 bytes at any position inside the data stay inside the array.
    private const int Slack = 16;

    private ZstdTrainingSet(byte[] data, int length, int[] starts, int count)
    {
        Data = data;
        Length = length;
        Starts = starts;
        Count = count;
    }

    public byte[] Data { get; }

    public int Length { get; }

    /// <summary>Gets the start of each sample, then the end of the last (<see cref="Count"/> + 1 entries).</summary>
    public int[] Starts { get; }

    public int Count { get; }

    public ReadOnlyMemory<byte> Sample(int index) => Data.AsMemory(Starts[index], Starts[index + 1] - Starts[index]);

    public static ZstdTrainingSet Create(IReadOnlyList<ReadOnlyMemory<byte>> samples, long maxBytes)
    {
        long total = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            total += samples[i].Length;
        }

        long stride = 1;
        if (total > maxBytes)
        {
            stride = (total + maxBytes - 1) / maxBytes;
        }

        long kept = 0;
        int count = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (i % stride == 0 && samples[i].Length > 0 && kept < maxBytes)
            {
                kept += Math.Min(samples[i].Length, maxBytes - kept);
                count++;
            }
        }

        byte[] data = new byte[kept + Slack];
        int[] starts = new int[count + 1];
        int n = 0;
        int p = 0;
        for (int i = 0; i < samples.Count && n < count; i++)
        {
            ReadOnlyMemory<byte> s = samples[i];
            if (i % stride != 0 || s.Length == 0)
            {
                continue;
            }

            int take = (int)Math.Min(s.Length, kept - p);
            if (take <= 0)
            {
                break;
            }

            starts[n++] = p;
            s.Span[..take].CopyTo(data.AsSpan(p));
            p += take;
        }

        starts[n] = p;
        return new ZstdTrainingSet(data, p, starts, n);
    }

    /// <summary>Gets a view of samples first (inclusive) to last (exclusive), sharing no storage.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Slice(int first, int last, int maxSamples, long maxBytes)
    {
        int available = Math.Max(last - first, 0);
        long bytes = Starts[Math.Max(last, first)] - Starts[first];
        long stride = 1;
        if (available > maxSamples)
        {
            stride = Math.Max(stride, (available + maxSamples - 1) / maxSamples);
        }

        if (bytes > maxBytes)
        {
            stride = Math.Max(stride, (bytes + maxBytes - 1) / maxBytes);
        }

        var list = new List<ReadOnlyMemory<byte>>();
        for (int i = first; i < last; i++)
        {
            if ((i - first) % stride == 0)
            {
                list.Add(Sample(i));
            }
        }

        return list;
    }
}

/// <summary>
/// d-mer statistics over a <see cref="ZstdTrainingSet"/> for one d-mer size: which d-mer each position starts, and how many samples
/// each d-mer occurs in. A position whose d bytes would cross a sample's end starts no d-mer.
/// </summary>
internal sealed class ZstdDmerIndex
{
    private ZstdDmerIndex(int dmerSize, int[] ids, uint[] frequencies)
    {
        DmerSize = dmerSize;
        Ids = ids;
        Frequencies = frequencies;
    }

    public int DmerSize { get; }

    /// <summary>Gets the d-mer id at each position, or -1 for none.</summary>
    public int[] Ids { get; }

    /// <summary>Gets the number of samples each d-mer id occurs in.</summary>
    public uint[] Frequencies { get; }

    public static ZstdDmerIndex Build(ZstdTrainingSet set, int d, ZstdDictionaryTrainingOptions options, CancellationToken cancellationToken)
    {
        byte[] data = set.Data;
        int[] ids = new int[set.Length];
        bool exact = options.Algorithm == ZstdTrainingAlgorithm.Cover;
        int accel = exact ? 1 : options.Acceleration;
        int hashBits = options.HashBits;
        ExactIds? table = exact ? new ExactIds() : null;
        int idCount = exact ? 0 : 1 << hashBits;
        for (int s = 0; s < set.Count; s++)
        {
            if ((s & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            int start = set.Starts[s];
            int end = set.Starts[s + 1];
            for (int p = start; p < end; p++)
            {
                if (p + d > end || (p - start) % accel != 0)
                {
                    ids[p] = -1;
                    continue;
                }

                ulong key = Key(data, p, d);
                ids[p] = exact ? table!.GetOrAdd(key) : (int)((key * 0x9E3779B97F4A7C15UL) >> (64 - hashBits));
            }
        }

        if (exact)
        {
            idCount = table!.Count;
        }

        // The frequency of a d-mer is the number of samples it occurs in; one sample repeating a d-mer does not make it more
        // useful to the others.
        uint[] freq = new uint[idCount];
        int[] seenIn = new int[idCount];
        for (int s = 0; s < set.Count; s++)
        {
            int stamp = s + 1;
            for (int p = set.Starts[s]; p < set.Starts[s + 1]; p++)
            {
                int id = ids[p];
                if (id >= 0 && seenIn[id] != stamp)
                {
                    seenIn[id] = stamp;
                    freq[id]++;
                }
            }
        }

        return new ZstdDmerIndex(d, ids, freq);
    }

    private static ulong Key(byte[] data, int p, int d)
    {
        if (d <= 8)
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p));
            return d == 8 ? v : v & ((1UL << (8 * d)) - 1);
        }

        // 9 to 16 bytes: the first and last eight, which overlap when d < 16, mixed into one 64-bit key.
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p));
        ulong b = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(p + d - 8));
        return (a * 0xC2B2AE3D27D4EB4FUL) ^ BitOperations.RotateLeft(b * 0x165667B19E3779F9UL, 31);
    }

    /// <summary>The weight a d-mer of the given frequency adds to a segment's score; a d-mer found in one sample only adds nothing, as it cannot help any other.</summary>
    public static uint Weight(uint frequency, ZstdSegmentScore score) =>
        frequency < 2 ? 0 : score == ZstdSegmentScore.SquareRoot ? ISqrt((ulong)frequency << 16) : frequency;

    internal static uint ISqrt(ulong x)
    {
        if (x == 0)
        {
            return 0;
        }

        ulong r = 1UL << ((BitOperations.Log2(x) >> 1) + 1);
        while (true)
        {
            ulong n = (r + (x / r)) >> 1;
            if (n >= r)
            {
                return (uint)r;
            }

            r = n;
        }
    }

    /// <summary>Open-addressing table from 64-bit keys to dense ids; keys are compared in full, so ids are exact.</summary>
    private sealed class ExactIds
    {
        private ulong[] _keys = new ulong[1 << 12];
        private int[] _values = new int[1 << 12];
        private int _count;

        public int Count => _count;

        public int GetOrAdd(ulong key)
        {
            if (_count * 2 >= _keys.Length)
            {
                Grow();
            }

            int mask = _keys.Length - 1;
            int slot = (int)((key * 0x9E3779B97F4A7C15UL) >> 32) & mask;
            while (true)
            {
                int v = _values[slot];
                if (v == 0)
                {
                    _keys[slot] = key;
                    _values[slot] = ++_count;
                    return _count - 1;
                }

                if (_keys[slot] == key)
                {
                    return v - 1;
                }

                slot = (slot + 1) & mask;
            }
        }

        private void Grow()
        {
            ulong[] oldKeys = _keys;
            int[] oldValues = _values;
            _keys = new ulong[oldKeys.Length * 2];
            _values = new int[oldKeys.Length * 2];
            int mask = _keys.Length - 1;
            for (int i = 0; i < oldKeys.Length; i++)
            {
                if (oldValues[i] == 0)
                {
                    continue;
                }

                int slot = (int)((oldKeys[i] * 0x9E3779B97F4A7C15UL) >> 32) & mask;
                while (_values[slot] != 0)
                {
                    slot = (slot + 1) & mask;
                }

                _keys[slot] = oldKeys[i];
                _values[slot] = oldValues[i];
            }
        }
    }
}
