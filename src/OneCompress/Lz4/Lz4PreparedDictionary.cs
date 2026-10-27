using System.Runtime.CompilerServices;

namespace RecursiveAI.OneCompress.Lz4;

/// <summary>
/// A dictionary's bytes with the match-finder tables built over them, shared by every compressor that uses the dictionary.
/// The tables are built on first use and never change afterwards, so many threads may read one instance.
/// </summary>
internal sealed class Lz4PreparedDictionary
{
    private static readonly ConditionalWeakTable<Lz4Dictionary, Lz4PreparedDictionary> s_cache = new();

    private readonly Lock _lock = new();
    private readonly uint[]?[] _fastTables = new uint[]?[Lz4FastBlockCompressor.MaxHashLog + 1];
    private HighIndex? _highSnapshot;

    private Lz4PreparedDictionary(Lz4Dictionary dictionary)
    {
        Content = dictionary.Content.ToArray();
    }

    public byte[] Content { get; }

    public static Lz4PreparedDictionary For(Lz4Dictionary dictionary) =>
        s_cache.GetValue(dictionary, static d => new Lz4PreparedDictionary(d));

    /// <summary>
    /// The fast compressor's hash table over the dictionary for a table of 2^<paramref name="hashLog"/> entries: each entry is a
    /// position plus one, or zero for none. Later positions replace earlier ones, as they do in a table built while compressing.
    /// </summary>
    public uint[] FastTable(int hashLog)
    {
        if (Volatile.Read(ref _fastTables[hashLog]) is { } table)
        {
            return table;
        }

        lock (_lock)
        {
            if (_fastTables[hashLog] is null)
            {
                var built = new uint[1 << hashLog];
                ReadOnlySpan<byte> c = Content;
                int shift = 64 - hashLog;
                for (int i = 0; i + 8 <= c.Length; i++)
                {
                    built[Lz4FastBlockCompressor.Hash(Lz4EncRaw.Read64(c, i), shift)] = (uint)i + 1;
                }

                Volatile.Write(ref _fastTables[hashLog], built);
            }

            return _fastTables[hashLog]!;
        }
    }

    /// <summary>
    /// The high-compression compressor's index over the dictionary, as if the dictionary were the first bytes of a window
    /// whose origin is a multiple of 64 KiB: head entries are a position plus one (zero for none), and the chain holds the
    /// distance to the previous position with the same hash, indexed by position.
    /// </summary>
    public HighIndex HighSnapshot
    {
        get
        {
            HighIndex? snapshot = Volatile.Read(ref _highSnapshot);
            if (snapshot is not null)
            {
                return snapshot;
            }

            lock (_lock)
            {
                if (_highSnapshot is null)
                {
                    var head = new uint[1 << Lz4HighBlockCompressor.HashLog];
                    var chain = new ushort[Content.Length];
                    ReadOnlySpan<byte> c = Content;
                    for (int p = 0; p + 4 <= c.Length; p++)
                    {
                        int h = Lz4HighBlockCompressor.Hash(Lz4EncRaw.Read32(c, p));
                        uint previous = head[h];
                        chain[p] = previous == 0 ? (ushort)0 : (ushort)(p - (previous - 1));
                        head[h] = (uint)p + 1;
                    }

                    Volatile.Write(ref _highSnapshot, new HighIndex(head, chain));
                }

                return _highSnapshot;
            }
        }
    }

    internal sealed record HighIndex(uint[] Head, ushort[] Chain);
}
