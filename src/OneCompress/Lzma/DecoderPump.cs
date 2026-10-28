using System.Buffers;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// The read-side plumbing shared by the LZMA, xz and lzip streams: reads the inner stream into a pooled buffer and feeds a step decoder.
/// The end of the data is reported only at a unit boundary; an inner stream that ends earlier throws the truncated format error.
/// </summary>
internal sealed class DecoderPump<T> : IDisposable
    where T : class, IStepDecoder
{
    public const int InputBufferSize = 64 * 1024;

    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private readonly T _decoder;
    private readonly string _format;
    private readonly bool _singleStream;
    private readonly bool _allowTrailing;
    private byte[]? _input;
    private int _start;
    private int _end;
    private bool _innerEnded;
    private bool _sawData;
    private bool _streamDone;
    private bool _finished;
    private long _offset;

    public DecoderPump(Stream stream, T decoder, string format, bool singleStream, bool allowTrailing, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
        {
            throw new ArgumentException("The stream must be readable.", nameof(stream));
        }

        _inner = stream;
        _decoder = decoder;
        _format = format;
        _singleStream = singleStream;
        _allowTrailing = allowTrailing;
        _leaveOpen = leaveOpen;
        _input = ArrayPool<byte>.Shared.Rent(InputBufferSize);
    }

    public bool IsOpen => _input is not null;

    public int Read(Span<byte> buffer)
    {
        byte[] input = GetInput();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (TryDecode(input, buffer, out int written))
            {
                return written;
            }

            Refill(input, _inner.Read(input, 0, input.Length));
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        byte[] input = GetInput();
        cancellationToken.ThrowIfCancellationRequested();
        if (buffer.IsEmpty)
        {
            return 0;
        }

        while (true)
        {
            if (TryDecode(input, buffer.Span, out int written))
            {
                return written;
            }

            Refill(input, await _inner.ReadAsync(input.AsMemory(0, input.Length), cancellationToken).ConfigureAwait(false));
        }
    }

    private void Refill(byte[] input, int n)
    {
        _offset += _end - _start;
        _start = 0;
        _end = n;
        if (n == 0)
        {
            _innerEnded = true;
        }
    }

    // Decodes what the buffered input allows. Returns false when more input is needed from the inner stream; true with
    // written == 0 means the end of the data.
    private bool TryDecode(byte[] input, Span<byte> buffer, out int written)
    {
        written = 0;
        while (true)
        {
            if (_finished)
            {
                return true;
            }

            if (_streamDone)
            {
                // A single-stream format ended: whatever follows is trailing data.
                if (_start < _end)
                {
                    if (!_allowTrailing)
                    {
                        throw new CompressionFormatException(_format, _offset + _start, "the end of the data after the " + _format + " stream");
                    }

                    _finished = true;
                    return true;
                }

                if (_innerEnded)
                {
                    _finished = true;
                    return true;
                }

                return false;
            }

            if (_innerEnded && _start == _end && _decoder.IsAtFrameBoundary)
            {
                if (!_sawData)
                {
                    throw new CompressionFormatException(_format, 0, "a " + _format + " stream", isTruncated: true);
                }

                _finished = true;
                return true;
            }

            OperationStatus status = _decoder.Decompress(input.AsSpan(_start, _end - _start), buffer[written..], out int consumed, out int w);
            _start += consumed;
            written += w;
            if (consumed > 0 || w > 0)
            {
                _sawData = true;
            }

            if (status == OperationStatus.Done)
            {
                _sawData = true;
                if (!_singleStream && consumed == 0 && w == 0)
                {
                    // A multi-stream decoder that reports Done without using anything has found data it was told to leave alone.
                    _finished = true;
                    return true;
                }

                if (_singleStream)
                {
                    _streamDone = true;
                    if (written > 0)
                    {
                        return true;
                    }

                    continue;
                }
            }

            if (written > 0 && (status == OperationStatus.DestinationTooSmall || written == buffer.Length))
            {
                return true;
            }

            switch (status)
            {
                case OperationStatus.NeedMoreData:
                    if (_innerEnded)
                    {
                        _decoder.ThrowIfIncomplete();
                        if (written > 0)
                        {
                            return true;
                        }

                        continue;
                    }

                    return written > 0;
                case OperationStatus.DestinationTooSmall:
                    return true;
                default: // Done: the next stream, or the end
                    if (written > 0 && _start == _end)
                    {
                        return true;
                    }

                    break;
            }
        }
    }

    private byte[] GetInput()
    {
        ObjectDisposedException.ThrowIf(_input is null, this);
        return _input;
    }

    public void Dispose()
    {
        if (ReleaseBuffer() && !_leaveOpen)
        {
            _inner.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (ReleaseBuffer() && !_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool ReleaseBuffer()
    {
        byte[]? input = _input;
        _input = null;
        if (input is null)
        {
            return false;
        }

        ArrayPool<byte>.Shared.Return(input);
        return true;
    }
}
