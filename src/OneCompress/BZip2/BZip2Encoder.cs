using System.Buffers;

namespace RecursiveAI.OneCompress.BZip2;

/// <summary>
/// A bzip2 compressor over spans. It writes one stream: the <c>BZh</c> header, one block for every <c>level × 100 000 − 19</c> bytes of
/// first-stage output, and the end-of-stream marker with the combined CRC when the final call is made. Several streams are made by
/// concatenating the output of several encoders (or of one encoder after <see cref="Reset"/>).
/// </summary>
/// <remarks>
/// <para>Input is collected into the current block; a block is sorted and coded only when it is full or the stream ends, so a call may
/// consume a lot of input and produce nothing, and one call may produce a whole block's output (up to about a megabyte); what the
/// destination cannot take stays inside the encoder until the next call. The output depends only on the input bytes and the level,
/// never on how the input is cut into calls. Memory: the block (up to 900 000 bytes), the sorter (about 16 bytes per block byte at
/// level 9) and the symbols; all are allocated when first needed and kept for the next block.</para>
/// <para>Format choices the specification leaves open are written down in docs/bzip2-encoder.md.</para>
/// </remarks>
public sealed class BZip2Encoder
{
    private const long EndMagic = 0x177245385090;

    private readonly int _level;
    private readonly int _maxCodeLength;
    private readonly BZip2RunLengthEncoder _runs;
    private readonly BZip2BlockSorter _sorter = new();
    private readonly BZip2BlockWriter _blockWriter = new();
    private readonly BZip2BitWriter _writer = new();
    private byte[] _lastColumn = [];
    private int _drained;
    private bool _started;
    private bool _finished;
    private uint _combinedCrc;

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The level; <see langword="null"/> for <see cref="BZip2Options.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 1 to 9.</exception>
    public BZip2Encoder(BZip2Options? options = null)
    {
        options ??= BZip2Options.Default;
        _level = options.Level;
        _maxCodeLength = options.MaxCodeLength;
        _runs = new BZip2RunLengthEncoder(_level);
    }

    /// <summary>Gets the block size level, 1 to 9.</summary>
    public int Level => _level;

    /// <summary>Starts a new stream, dropping anything not yet written.</summary>
    public void Reset()
    {
        _runs.Reset();
        _writer.Reset();
        _drained = 0;
        _started = false;
        _finished = false;
        _combinedCrc = 0;
    }

    /// <summary>Compresses a part of the data.</summary>
    /// <param name="source">The next input bytes.</param>
    /// <param name="destination">Receives compressed bytes.</param>
    /// <param name="consumed">The number of input bytes taken.</param>
    /// <param name="written">The number of bytes written to <paramref name="destination"/>.</param>
    /// <param name="isFinalBlock">True when <paramref name="source"/> is the last input: the stream is then ended once the input is taken.</param>
    /// <returns>
    /// <see cref="OperationStatus.Done"/> when the stream has ended and all of it has been written (only after a call with <paramref name="isFinalBlock"/>);
    /// <see cref="OperationStatus.DestinationTooSmall"/> when output is waiting: call again with more room (and the rest of the input);
    /// <see cref="OperationStatus.NeedMoreData"/> when all input was taken and the stream is not final.
    /// </returns>
    /// <exception cref="InvalidOperationException">The stream has ended (call <see cref="Reset"/>) and <paramref name="source"/> is not empty.</exception>
    public OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock = false)
    {
        consumed = 0;
        written = 0;
        if (_finished && !source.IsEmpty)
        {
            throw new InvalidOperationException("The stream has ended; call Reset() to start another.");
        }

        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (_finished)
            {
                return OperationStatus.Done;
            }

            if (!_started)
            {
                _started = true;
                _writer.Write(0x425A68, 24); // "BZh"
                _writer.Write((uint)('0' + _level), 8);
            }

            ReadOnlySpan<byte> rest = source[consumed..];
            int taken = _runs.Add(rest);
            consumed += taken;
            if (taken < rest.Length)
            {
                EncodeBlock();
                continue;
            }

            if (!isFinalBlock)
            {
                return OperationStatus.NeedMoreData;
            }

            if (!_runs.IsEmpty)
            {
                EncodeBlock();
            }

            _writer.Write((uint)(EndMagic >> 24), 24);
            _writer.Write((uint)(EndMagic & 0xFFFFFF), 24);
            _writer.Write(_combinedCrc, 32);
            _writer.AlignToByte();
            _finished = true;
        }
    }

    /// <summary>Writes out the bytes already coded and still waiting. It does not end the block in progress: a block is coded only when full or at the end of the stream.</summary>
    /// <param name="destination">Receives the waiting bytes.</param>
    /// <param name="written">The number of bytes written.</param>
    /// <returns><see cref="OperationStatus.Done"/> when nothing is waiting any more, otherwise <see cref="OperationStatus.DestinationTooSmall"/>.</returns>
    public OperationStatus Flush(Span<byte> destination, out int written)
    {
        written = 0;
        return Drain(destination, ref written) ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
    }

    private bool Drain(Span<byte> destination, ref int written)
    {
        ReadOnlySpan<byte> pending = _writer.Bytes[_drained..];
        int n = Math.Min(pending.Length, destination.Length - written);
        pending[..n].CopyTo(destination[written..]);
        written += n;
        _drained += n;
        if (_drained == _writer.Bytes.Length)
        {
            _writer.Discard(_drained);
            _drained = 0;
            return true;
        }

        return false;
    }

    private void EncodeBlock()
    {
        ReadOnlySpan<byte> block = _runs.Finish(out uint crc);
        if (_lastColumn.Length < block.Length)
        {
            _lastColumn = new byte[block.Length];
        }

        int row = _sorter.Sort(block, _lastColumn);
        _blockWriter.Write(_writer, _lastColumn.AsSpan(0, block.Length), row, crc, _maxCodeLength);
        _combinedCrc = BZip2Crc.Combine(_combinedCrc, crc);
        _runs.Reset();
    }
}
