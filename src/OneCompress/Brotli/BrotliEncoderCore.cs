namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// The encoder's engine: it buffers input, parses each block into commands with the plan's match finder, and hands the commands to
/// <see cref="BrotliMetaBlockWriter"/>.
/// </summary>
/// <remarks>
/// Input collects in a buffer that holds the window (the data a copy may refer to) followed by the block being gathered. A block is
/// parsed when it is full, on a flush, or at the end of the stream, so the meta-blocks, and with them the output bytes, depend only on the
/// input and on where flushes were asked for, never on how the input was cut into calls. When the buffer is full its oldest bytes are dropped
/// and the positions in the match finder tables move down with the data.
/// </remarks>
internal sealed partial class BrotliEncoderCore
{
    private const int Margin = 16;

    private readonly BrotliQualityPlan _plan;
    private readonly int _windowBits;
    private readonly int _window; // the largest distance of a copy: 2^bits - 16 (RFC 7932 section 9.1)
    private readonly BrotliBitWriter _bits = new();
    private readonly BrotliMetaBlockWriter _writer;
    private readonly BrotliCommandList _commands = new();
    private readonly int[] _ring = [4, 11, 15, 16];

    private byte[] _buffer = [];
    private int _length;       // bytes held in the buffer
    private int _blockStart;   // where the block being gathered starts
    private long _streamBase;  // the stream position of buffer[0]
    private bool _headerWritten;
    private bool _finished;
    private bool _dirty;       // data since the last flush marker

    internal BrotliEncoderCore(int quality, int windowBits)
    {
        _plan = BrotliQualityPlan.For(quality);
        _windowBits = windowBits;
        _window = (1 << windowBits) - 16;
        _writer = new BrotliMetaBlockWriter(_plan);
        InitMatchFinder();
    }

    internal BrotliBitWriter Output => _bits;

    internal bool IsFinished => _finished;

    internal void Reset()
    {
        _bits.Reset();
        _length = 0;
        _blockStart = 0;
        _streamBase = 0;
        _headerWritten = false;
        _finished = false;
        _dirty = false;
        _ring[0] = 4;
        _ring[1] = 11;
        _ring[2] = 15;
        _ring[3] = 16;
        ResetMatchFinder();
    }

    /// <summary>Takes input, coding every block that fills up.</summary>
    internal void Append(ReadOnlySpan<byte> source)
    {
        while (!source.IsEmpty)
        {
            int pending = _length - _blockStart;
            int room = _plan.BlockSize - pending;
            if (_length == _buffer.Length && !MakeRoom())
            {
                // Cannot happen: the buffer is sized for the window and one block.
                throw new InvalidOperationException("Unreachable: no room in the encoder buffer.");
            }

            int n = Math.Min(Math.Min(source.Length, room), _buffer.Length - _length);
            source.Slice(0, n).CopyTo(_buffer.AsSpan(_length));
            _length += n;
            source = source.Slice(n);
            _dirty = true;
            if (_length - _blockStart == _plan.BlockSize)
            {
                EncodeBlock(false);
            }
        }
    }

    /// <summary>Codes the data gathered so far as a meta-block and ends the stream.</summary>
    internal void Finish()
    {
        EnsureHeader();
        int pending = _length - _blockStart;
        if (pending > 0)
        {
            EncodeBlock(true);
        }
        else
        {
            BrotliMetaBlockWriter.WriteEmptyLast(_bits);
        }

        _finished = true;
    }

    /// <summary>Codes the data gathered so far and ends at a byte boundary, so that a reader can decode everything written.</summary>
    internal void Flush()
    {
        EnsureHeader();
        if (_length - _blockStart > 0)
        {
            EncodeBlock(false);
        }

        if (_dirty)
        {
            BrotliMetaBlockWriter.WriteFlushMarker(_bits);
            _dirty = false;
        }
    }

    private void EnsureHeader()
    {
        if (_headerWritten)
        {
            return;
        }

        _headerWritten = true;
        // RFC 7932 section 9.1: WBITS.
        if (_windowBits == 16)
        {
            _bits.Write(0, 1);
        }
        else if (_windowBits == 17)
        {
            _bits.Write(1, 1);
            _bits.Write(0, 3);
            _bits.Write(0, 3);
        }
        else if (_windowBits > 17)
        {
            _bits.Write(1, 1);
            _bits.Write((uint)(_windowBits - 17), 3);
        }
        else
        {
            _bits.Write(1, 1);
            _bits.Write(0, 3);
            _bits.Write((uint)(_windowBits - 8), 3);
        }
    }

    private void EncodeBlock(bool isLast)
    {
        EnsureHeader();
        int start = _blockStart;
        int length = _length - start;
        _commands.Clear();
        Parse(start, start + length);
        _writer.Write(_bits, _buffer, start, length, _commands, _ring, isLast);
        _blockStart = start + length;
    }

    // Frees room in a full buffer: grows it up to the size needed for a window and a block, then drops the oldest bytes.
    private bool MakeRoom()
    {
        // Dropping the oldest bytes moves everything that is kept, so the buffer holds room for at least 4 MiB of new data: with small blocks
        // the move then happens once per 4 MiB instead of once per block. Which bytes are in the buffer never changes what is found, because a
        // candidate beyond the window is refused either way.
        long wanted = (long)_window + (_plan.BinaryTree ? _plan.BlockSize : Math.Max(_plan.BlockSize, 4 << 20)) + Margin;
        if (_buffer.Length < wanted)
        {
            int size = (int)Math.Min(wanted, Math.Max(64 * 1024, (long)_buffer.Length * 2));
            Array.Resize(ref _buffer, size);
            ResizeChain(size);
            return true;
        }

        int keep = Math.Min(_window, _blockStart);
        int shift = _blockStart - keep;
        if (shift <= 0)
        {
            return false;
        }

        Buffer.BlockCopy(_buffer, shift, _buffer, 0, _length - shift);
        _length -= shift;
        _blockStart -= shift;
        _streamBase += shift;
        Rebase(shift);
        return true;
    }
}
