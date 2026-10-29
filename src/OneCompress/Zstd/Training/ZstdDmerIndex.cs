using System.Buffers.Binary;
using System.Numerics;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Reusable work arrays for training. One instance belongs to one thread at a time; arrays only grow, so a search that scores many
/// candidates allocates them once instead of once per candidate.
/// </summary>
internal sealed class ZstdTrainingScratch
{
    /// <summary>Gets or sets the last sample that counted each d-mer slot while frequencies are counted.</summary>
    public uint[] Stamps = [];

    /// <summary>Gets or sets the weight (high 16 bits) and window count (low 16 bits) of each d-mer slot, for windows up to 65535 positions.</summary>
    public uint[] Cells = [];

    /// <summary>Gets or sets the same with 32 bits each, for longer windows.</summary>
    public ulong[] WideCells = [];

    /// <summary>Gets or sets the d-mer slot of each position of the current window, so that removing a position needs no second hash.</summary>
    public int[] Ring = [];

    /// <summary>Gets or sets the buffer a candidate's held-out samples are compressed into.</summary>
    public byte[] Output = [];

    /// <summary>
    /// Makes <paramref name="array"/> at least <paramref name="length"/> long. Contents are not cleared. The arrays are the scratch set's
    /// own and stay with it for the whole training, so they are allocated at the size asked for: a pooled array would be rounded up
    /// to a power of two, and a table of 2^20 slots and one more would take twice the memory.
    /// </summary>
    public static void Fit<T>(ref T[] array, int length)
    {
        if (array.Length < length)
        {
            array = new T[length];
        }
    }

    public void Release()
    {
        Stamps = [];
        Cells = [];
        WideCells = [];
        Ring = [];
        Output = [];
    }
}

/// <summary>
/// Large temporary arrays come from the shared pool and go back to it, so that a training that scores many candidates holds the memory
/// of one, not of all: arrays of a megabyte and more live on the large object heap, which is collected rarely. A rented array can be
/// longer than asked for and holds whatever its last user left; every user states the length it means and writes before it reads.
/// </summary>
internal static class ZstdTrainingBuffers
{
    public static T[] Rent<T>(int length) => length == 0 ? [] : System.Buffers.ArrayPool<T>.Shared.Rent(length);

    public static void Return<T>(T[]? array)
    {
        if (array is { Length: > 0 })
        {
            System.Buffers.ArrayPool<T>.Shared.Return(array);
        }
    }

    public static T[] RentCleared<T>(int length)
    {
        T[] array = Rent<T>(length);
        Array.Clear(array, 0, length);
        return array;
    }
}

/// <summary>
/// d-mer statistics over a <see cref="ZstdTrainingSet"/> for one d-mer size and one set of samples: which d-mer each position
/// starts, and the weight of each d-mer, which segment selection adds up. A position whose d bytes would cross a sample's end
/// starts no d-mer.
/// </summary>
/// <remarks>
/// With the FastCover algorithm a d-mer's slot is a multiplicative hash of its bytes (so nothing is stored per position and two
/// d-mers can share a slot); with Cover each distinct d-mer has its own slot, numbered in order of first appearance.
/// </remarks>
internal sealed class ZstdDmerIndex
{
    private ushort[]? _squareRootWeights;
    private ushort[]? _frequencyWeights;

    internal ZstdDmerIndex(ZstdTrainingSet set, int dmerSize, int hashBits, int slots, int[]? exactIds, ulong[]? valid, int[] runs, int trainLength)
    {
        Set = set;
        DmerSize = dmerSize;
        HashBits = hashBits;
        Slots = slots;
        ExactIds = exactIds;
        Valid = valid;
        Runs = runs;
        TrainLength = trainLength;
    }

    /// <summary>Gets the samples the d-mers were counted over; training uses all of them or the runs listed in <see cref="Runs"/>.</summary>
    public ZstdTrainingSet Set { get; }

    /// <summary>Gets the stretches of <see cref="Set"/> trained on, as (compact start, start in the set, length) triples in order.</summary>
    public int[] Runs { get; }

    /// <summary>Gets the number of bytes trained on (the lengths of <see cref="Runs"/> added up).</summary>
    public int TrainLength { get; }

    public int DmerSize { get; }

    /// <summary>Gets the log2 of the number of hash slots, or 0 when slots are exact.</summary>
    public int HashBits { get; }

    /// <summary>Gets the number of slots (ids) a position can map to.</summary>
    public int Slots { get; }

    /// <summary>Gets the exact d-mer id at each position, or -1 for none; null with the hashed algorithm.</summary>
    public int[]? ExactIds { get; }

    /// <summary>Gets one bit per position, set where a d-mer starts; null with the exact algorithm.</summary>
    public ulong[]? Valid { get; }

    /// <summary>Gives the index's weights back to the pool; the index is not used afterwards. (The position tables belong to the counts.)</summary>
    public void Release()
    {
        ZstdTrainingBuffers.Return(_squareRootWeights);
        ZstdTrainingBuffers.Return(_frequencyWeights);
        _squareRootWeights = null;
        _frequencyWeights = null;
    }

    public ushort[] Weights(ZstdSegmentScore score) =>
        (score == ZstdSegmentScore.SquareRoot ? _squareRootWeights : _frequencyWeights) ?? throw new InvalidOperationException("The weights for this score were not prepared.");

    internal void SetWeights(ZstdSegmentScore score, ushort[] weights)
    {
        if (score == ZstdSegmentScore.SquareRoot)
        {
            _squareRootWeights = weights;
        }
        else
        {
            _frequencyWeights = weights;
        }
    }
}

/// <summary>
/// How many samples each d-mer slot occurs in, counted once over all the samples and kept apart for each group of samples. The
/// frequencies for any part of the samples that is made of whole groups are then sums of group tables, so a search that holds out one
/// group at a time (and the training on all of them) needs one counting pass, not one per held-out part.
/// </summary>
internal sealed class ZstdDmerCounts
{
    internal const ulong Multiplier = 0x9E3779B97F4A7C15UL;

    // A frequency is kept in 16 bits and a weight in 16: a d-mer found in more samples than this counts as if it were found in this
    // many. The sum of saturated group counts saturates exactly when the true sum would.
    private const int MaxFrequency = ushort.MaxValue;

    private readonly ZstdTrainingSet _set;
    private readonly int _d;
    private readonly int _bits;
    private readonly int _slots;
    private readonly int _accel;
    private readonly ushort[][] _groupFrequencies;
    private readonly int[]? _exactIds;
    private readonly ulong[]? _valid;

    private ZstdDmerCounts(ZstdTrainingSet set, int d, int bits, int slots, int accel, ushort[][] groupFrequencies, int[]? exactIds)
    {
        _set = set;
        _d = d;
        _bits = bits;
        _slots = slots;
        _accel = accel;
        _groupFrequencies = groupFrequencies;
        _exactIds = exactIds;
        _valid = exactIds is null ? MakeValid(set, d, accel) : null;
    }

    /// <summary>Gives the group tables back to the pool.</summary>
    public void Release()
    {
        foreach (ushort[] table in _groupFrequencies)
        {
            ZstdTrainingBuffers.Return(table);
        }

        ZstdTrainingBuffers.Return(_valid);
    }

    /// <summary>The number of hash bits used for a set of this many bytes: at least eight slots per byte (never fewer than 2^12 unless the option allows fewer), and no more than the option allows.</summary>
    internal static int HashBitsFor(int length, int maxBits) =>
        Math.Clamp(BitOperations.Log2((uint)Math.Max(length, 1)) + 3, Math.Min(12, maxBits), maxBits);

    /// <summary>
    /// Counts the d-mers of <paramref name="set"/>. Sample i belongs to group (i / blockSize) % groupCount.
    /// </summary>
    public static ZstdDmerCounts Build(ZstdTrainingSet set, int d, int groupCount, int blockSize, ZstdDictionaryTrainingOptions options, ZstdTrainingScratch scratch, CancellationToken cancellationToken)
    {
        bool exact = options.Algorithm == ZstdTrainingAlgorithm.Cover;
        int accel = exact ? 1 : options.Acceleration;
        int[]? ids = null;
        int bits = 0;
        int slots;
        if (exact)
        {
            ids = AssignExactIds(set, d, out slots, cancellationToken);
        }
        else
        {
            bits = HashBitsFor(set.Length, options.HashBits);
            slots = 1 << bits;
        }

        ushort[][] frequencies = new ushort[groupCount][];
        for (int g = 0; g < groupCount; g++)
        {
            frequencies[g] = ZstdTrainingBuffers.RentCleared<ushort>(slots);
        }

        ZstdTrainingScratch.Fit(ref scratch.Stamps, slots);
        Array.Clear(scratch.Stamps, 0, slots);
        Count(set, d, accel, bits, ids, groupCount, blockSize, frequencies, scratch.Stamps, cancellationToken);

        // The stamps are of no use once the counting is done.
        scratch.Stamps = [];
        return new ZstdDmerCounts(set, d, bits, slots, accel, frequencies, ids);
    }

    private static void Count(ZstdTrainingSet set, int d, int accel, int bits, int[]? ids, int groupCount, int blockSize, ushort[][] frequencies, uint[] stamps, CancellationToken cancellationToken)
    {
        byte[] data = set.Data;
        int shift = 64 - bits;
        for (int s = 0; s < set.Count; s++)
        {
            if ((s & 1023) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            // The frequency of a d-mer is the number of samples it occurs in; one sample repeating a d-mer does not make it more
            // useful to the others.
            uint stamp = (uint)s + 1;
            ushort[] frequency = frequencies[groupCount == 1 ? 0 : (s / blockSize) % groupCount];
            int start = set.Starts[s];
            int end = set.Starts[s + 1];
            if (ids is not null)
            {
                for (int p = start; p < end; p++)
                {
                    int id = ids[p];
                    if (id >= 0 && stamps[id] != stamp)
                    {
                        stamps[id] = stamp;
                        if (frequency[id] != MaxFrequency)
                        {
                            frequency[id]++;
                        }
                    }
                }

                continue;
            }

            int last = end - d;
            for (int p = start; p <= last; p += accel)
            {
                // Branch-free: whether a d-mer is new to this sample is not predictable, and a missed branch costs more than the
                // store it would save.
                int id = (int)((Key(data, p, d) * Multiplier) >> shift);
                uint fresh = stamps[id] != stamp ? 1u : 0u;
                stamps[id] = stamp;
                uint count = frequency[id];
                frequency[id] = (ushort)(count + (fresh & (count == MaxFrequency ? 0u : 1u)));
            }
        }
    }

    private static int[] AssignExactIds(ZstdTrainingSet set, int d, out int slots, CancellationToken cancellationToken)
    {
        byte[] data = set.Data;
        int[] ids = new int[set.Length];
        var table = new ExactIdTable();
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
                ids[p] = p + d > end ? -1 : table.GetOrAdd(Key(data, p, d));
            }
        }

        slots = table.Count;
        return ids;
    }

    /// <summary>
    /// Makes the index of part of the samples: the training runs <paramref name="runs"/> (see <see cref="ZstdDmerIndex.Runs"/>) of
    /// <paramref name="trainCount"/> samples, made of every group but <paramref name="excludedGroup"/> (-1 for all of them).
    /// </summary>
    public ZstdDmerIndex IndexFor(int[] runs, int trainLength, int trainCount, int excludedGroup, IReadOnlyCollection<ZstdSegmentScore> scores)
    {
        var index = new ZstdDmerIndex(_set, _d, _bits, _slots, _exactIds, _valid, runs, trainLength);

        // A frequency is at most the number of samples, so one lookup table of that size (at most 64 Ki entries) turns the counts into
        // weights without a square root per slot.
        int top = Math.Min(trainCount, MaxFrequency);
        ushort[][] groups = new ushort[_groupFrequencies.Length - (excludedGroup >= 0 ? 1 : 0)][];
        for (int g = 0, n = 0; g < _groupFrequencies.Length; g++)
        {
            if (g != excludedGroup)
            {
                groups[n++] = _groupFrequencies[g];
            }
        }

        foreach (ZstdSegmentScore score in scores)
        {
            ushort[] table = new ushort[top + 1];
            for (int f = 2; f <= top; f++)
            {
                table[f] = (ushort)(score == ZstdSegmentScore.SquareRoot ? Math.Min(ISqrt((ulong)f << 16), (uint)MaxFrequency) : (uint)f);
            }

            ushort[] weights = ZstdTrainingBuffers.Rent<ushort>(_slots);
            if (groups.Length == 4)
            {
                // The usual case, three groups when one of four is held out: one pass, no intermediate table.
                ushort[] a = groups[0], b = groups[1], c = groups[2], d = groups[3];
                for (int i = 0; i < _slots; i++)
                {
                    weights[i] = table[(int)Math.Min((uint)(a[i] + b[i] + c[i] + d[i]), (uint)top)];
                }
            }
            else if (groups.Length == 3)
            {
                ushort[] a = groups[0], b = groups[1], c = groups[2];
                for (int i = 0; i < _slots; i++)
                {
                    weights[i] = table[(int)Math.Min((uint)(a[i] + b[i] + c[i]), (uint)top)];
                }
            }
            else
            {
                for (int i = 0; i < _slots; i++)
                {
                    uint sum = 0;
                    foreach (ushort[] g in groups)
                    {
                        sum += g[i];
                    }

                    weights[i] = table[(int)Math.Min(sum, (uint)top)];
                }
            }

            index.SetWeights(score, weights);
        }

        return index;
    }

    // One bit per position, set where a d-mer starts: the first d - 1 positions before a sample's end start none, and with sampling
    // only every accel-th position of a sample starts one.
    private static ulong[] MakeValid(ZstdTrainingSet set, int d, int accel)
    {
        ulong[] valid = ZstdTrainingBuffers.RentCleared<ulong>((set.Length + 63) >> 6);
        for (int s = 0; s < set.Count; s++)
        {
            int last = set.Starts[s + 1] - d;
            for (int p = set.Starts[s]; p <= last; p += accel)
            {
                valid[p >> 6] |= 1UL << (p & 63);
            }
        }

        return valid;
    }

    /// <summary>Gets the 64-bit key of the d-mer (2 to 16 bytes) that starts at <paramref name="p"/>.</summary>
    internal static ulong Key(byte[] data, int p, int d)
    {
        // The set keeps 16 spare bytes after its data, so an 8-byte read at any position that starts a d-mer stays inside the array.
        ReadOnlySpan<byte> span = data.AsSpan(p);
        if (d <= 8)
        {
            ulong v = BinaryPrimitives.ReadUInt64LittleEndian(span);
            return d == 8 ? v : v & ((1UL << (8 * d)) - 1);
        }

        // 9 to 16 bytes: the first and last eight, which overlap when d < 16, mixed into one 64-bit key.
        ulong a = BinaryPrimitives.ReadUInt64LittleEndian(span);
        ulong b = BinaryPrimitives.ReadUInt64LittleEndian(span[(d - 8)..]);
        return (a * 0xC2B2AE3D27D4EB4FUL) ^ BitOperations.RotateLeft(b * 0x165667B19E3779F9UL, 31);
    }

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
    private sealed class ExactIdTable
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
            int slot = (int)((key * Multiplier) >> 32) & mask;
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

                int slot = (int)((oldKeys[i] * Multiplier) >> 32) & mask;
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
