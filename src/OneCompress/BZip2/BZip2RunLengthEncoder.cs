
namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// The first stage of bzip2: runs of four to 255 equal bytes become the four bytes followed by a count of the further copies
/// (0 to 251), and the result fills a block. It also keeps the block's CRC, which covers the bytes as the caller gave them.
/// </summary>
/// <remarks>
/// A block holds at most <see cref="GetBlockCapacity"/> bytes (the level times 100 000, less 19). A run is never kept open across
/// blocks: when the block is full the run so far is written with its count, and the next block starts a new run (derivation note,
/// facts F2 and F3). A unit (a literal, or a run with its count byte) that would not fit is not started, so a block never passes the capacity.
/// </remarks>
public sealed class BZip2RunLengthEncoder
{
    /// <summary>The longest run written as one unit (four bytes and a count of 251).</summary>
    public const int MaxRun = 255;

    private readonly byte[] _block;
    private readonly int _capacity;
    private int _length;
    private byte _runByte;
    private int _runLength;
    private uint _crc = BZip2Crc.Initial;

    /// <summary>Creates a first-stage encoder for blocks of a level.</summary>
    /// <param name="level">The block size level, 1 to 9.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 1 to 9.</exception>
    public BZip2RunLengthEncoder(int level)
    {
        _capacity = GetBlockCapacity(level);
        _block = new byte[_capacity];
    }

    /// <summary>Gets the most first-stage bytes a block of a level holds: the level times 100 000, less 19.</summary>
    /// <param name="level">The block size level, 1 to 9.</param>
    /// <returns>The capacity in bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="level"/> is outside 1 to 9.</exception>
    public static int GetBlockCapacity(int level)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(level, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(level, 9);
        return (level * 100_000) - 19;
    }

    /// <summary>Gets a value indicating whether no byte has been added since the last <see cref="Reset"/> or <see cref="Finish"/>.</summary>
    public bool IsEmpty => _length == 0 && _runLength == 0;

    private static int Cost(int run) => run < 4 ? run : 5;

    /// <summary>Adds bytes until the block is full or the data runs out.</summary>
    /// <param name="data">The bytes.</param>
    /// <returns>The number of bytes taken. Less than <c>data.Length</c> means the block is full: call <see cref="Finish"/>.</returns>
    public int Add(ReadOnlySpan<byte> data)
    {
        byte[] block = _block;
        int capacity = _capacity;
        int length = _length;
        int run = _runLength;
        byte runByte = _runByte;
        int i = 0;
        for (; i < data.Length; i++)
        {
            byte b = data[i];
            if (run > 0 && b == runByte && run < MaxRun)
            {
                if (length + Cost(run + 1) > capacity)
                {
                    break;
                }

                run++;
                continue;
            }

            // Close the run in progress (if any) and start a new one; the pending run's cost is already reserved.
            if (length + Cost(run) + 1 > capacity)
            {
                break;
            }

            length = Emit(block, length, runByte, run);
            runByte = b;
            run = 1;
        }

        _length = length;
        _runLength = run;
        _runByte = runByte;
        if (i > 0)
        {
            _crc = BZip2Crc.Update(_crc, data[..i]);
        }

        return i;
    }

    private static int Emit(byte[] block, int length, byte value, int run)
    {
        if (run == 0)
        {
            return length;
        }

        if (run < 4)
        {
            for (int k = 0; k < run; k++)
            {
                block[length++] = value;
            }

            return length;
        }

        block[length] = value;
        block[length + 1] = value;
        block[length + 2] = value;
        block[length + 3] = value;
        block[length + 4] = (byte)(run - 4);
        return length + 5;
    }

    /// <summary>Writes out the run in progress and hands over the block. The encoder is empty again after <see cref="Reset"/>.</summary>
    /// <param name="crc">The block's CRC, over the bytes added (before this stage).</param>
    /// <returns>The block's first-stage bytes; valid until the next <see cref="Add"/> or <see cref="Reset"/>.</returns>
    public ReadOnlySpan<byte> Finish(out uint crc)
    {
        _length = Emit(_block, _length, _runByte, _runLength);
        _runLength = 0;
        crc = ~_crc;
        return _block.AsSpan(0, _length);
    }

    /// <summary>Empties the block and starts a new CRC.</summary>
    public void Reset()
    {
        _length = 0;
        _runLength = 0;
        _crc = BZip2Crc.Initial;
    }
}
