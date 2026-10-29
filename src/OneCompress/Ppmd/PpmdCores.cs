using System.Buffers;

namespace RecursiveAI.OneCompress.Ppmd;

/// <summary>The span-driven encoder loop shared by both variants.</summary>
internal sealed class PpmdEncoderCore
{
    private readonly IPpmdModel _model;
    private readonly bool _endMarker;
    private bool _started;
    private bool _finished;

    internal PpmdEncoderCore(IPpmdModel model, bool endMarker)
    {
        _model = model;
        _endMarker = endMarker;
    }

    /// <summary>Gets a value indicating whether the stream has been finished (the final block was fully written).</summary>
    internal bool IsFinished => _finished;

    private bool Drain(Span<byte> destination, ref int written)
    {
        ReadOnlySpan<byte> pending = _model.PendingOutput;
        int n = Math.Min(pending.Length, destination.Length - written);
        pending[..n].CopyTo(destination[written..]);
        written += n;
        _model.ConsumeOutput(n);
        return n == pending.Length;
    }

    internal OperationStatus Compress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock)
    {
        consumed = 0;
        written = 0;
        if (_finished)
        {
            return Drain(destination, ref written) ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
        }

        if (!_started)
        {
            _model.StartEncoding();
            _started = true;
        }

        while (true)
        {
            if (!Drain(destination, ref written))
            {
                return OperationStatus.DestinationTooSmall;
            }

            if (consumed == source.Length)
            {
                break;
            }

            _model.EncodeSymbol(source[consumed++]);
        }

        if (!isFinalBlock)
        {
            return OperationStatus.Done;
        }

        if (_endMarker)
        {
            _model.EncodeSymbol(-1);
        }

        _model.FinishEncoding();
        _finished = true;
        return Drain(destination, ref written) ? OperationStatus.Done : OperationStatus.DestinationTooSmall;
    }
}

/// <summary>The span-driven decoder loop shared by both variants.</summary>
internal sealed class PpmdDecoderCore
{
    private readonly IPpmdModel _model;
    private readonly string _format;
    private readonly int _headerLength;
    private readonly long _outputSize;
    private readonly long _maxOutputSize;
    private bool _started;
    private bool _done;
    private bool _markerRead;
    private long _inputOffset;
    private long _produced;

    internal PpmdDecoderCore(IPpmdModel model, string format, int headerLength, long outputSize, long maxOutputSize)
    {
        _model = model;
        _format = format;
        _headerLength = headerLength;
        _outputSize = outputSize;
        _maxOutputSize = maxOutputSize;
    }

    internal long TotalProduced => _produced;

    /// <summary>Reads the end marker that follows the last byte of a stream whose size was given. Call it after <see cref="Decompress"/> reported Done.</summary>
    internal OperationStatus ReadEndMarker(ReadOnlySpan<byte> source, out int consumed)
    {
        consumed = 0;
        if (!_done)
        {
            throw new InvalidOperationException("The end marker follows the last byte of output.");
        }

        if (_markerRead)
        {
            return OperationStatus.Done;
        }

        int pos = 0;
        int sym = _model.DecodeSymbol(source, ref pos);
        if (sym == -3)
        {
            return OperationStatus.NeedMoreData;
        }

        if (sym != -1)
        {
            return OperationStatus.InvalidData;
        }

        _markerRead = true;
        consumed = pos;
        _inputOffset += pos;
        return OperationStatus.Done;
    }

    internal bool IsDone => _done;

    internal OperationStatus Decompress(ReadOnlySpan<byte> source, Span<byte> destination, out int consumed, out int written, bool isFinalBlock)
    {
        consumed = 0;
        written = 0;
        if (_done)
        {
            return OperationStatus.Done;
        }

        int pos = 0;
        if (!_started)
        {
            if (source.Length < _headerLength)
            {
                if (isFinalBlock)
                {
                    throw new CompressionFormatException(_format, _inputOffset + source.Length, "the range coder header", isTruncated: true);
                }

                return OperationStatus.NeedMoreData;
            }

            if (!_model.StartDecoding(source, ref pos))
            {
                throw new CompressionFormatException(_format, _inputOffset, "a valid range coder header");
            }

            _started = true;
        }

        OperationStatus status;
        while (true)
        {
            if (_outputSize >= 0 && _produced >= _outputSize)
            {
                _done = true;
                status = OperationStatus.Done;
                break;
            }

            if (written == destination.Length)
            {
                status = OperationStatus.DestinationTooSmall;
                break;
            }

            if (_produced >= _maxOutputSize)
            {
                throw new CompressionLimitException(_format, "MaxOutputSize", _maxOutputSize, _produced + 1);
            }

            int sym = _model.DecodeSymbol(source, ref pos);
            if (sym == -3)
            {
                // The model is unchanged and pos is where the symbol began: the caller presents these bytes again with more after them.
                if (isFinalBlock)
                {
                    throw new CompressionFormatException(_format, _inputOffset + source.Length, "more compressed data", isTruncated: true);
                }

                status = OperationStatus.NeedMoreData;
                break;
            }

            if (sym < 0)
            {
                long at = _inputOffset + pos;
                if (sym == -2)
                {
                    throw new CompressionFormatException(_format, at, "a symbol that fits the model's frequency table");
                }

                if (_outputSize >= 0)
                {
                    throw new CompressionFormatException(_format, at, $"{_outputSize} decoded bytes but the end marker came after {_produced}");
                }

                _done = true;
                status = OperationStatus.Done;
                break;
            }

            destination[written++] = (byte)sym;
            _produced++;
        }

        consumed = pos;
        _inputOffset += pos;
        return status;
    }
}
