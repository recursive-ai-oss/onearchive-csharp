using System.Buffers;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>Writes what an LZMA family encoder produces to a stream: the shared half of the encoder stream classes.</summary>
internal sealed class EncoderPump<T> : IDisposable
    where T : class, IQueuedEncoder
{
    private const int OutputBufferSize = 1 << 16;

    private readonly T _encoder;
    private readonly Stream _inner;
    private readonly bool _leaveOpen;
    private byte[]? _output;
    private bool _finished;

    public EncoderPump(Stream inner, T encoder, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(inner);
        if (!inner.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(inner));
        }

        _inner = inner;
        _encoder = encoder;
        _leaveOpen = leaveOpen;
        _output = ArrayPool<byte>.Shared.Rent(OutputBufferSize);
    }

    public bool IsOpen => _output is not null;

    private byte[] Output => _output ?? throw new ObjectDisposedException(GetType().Name);

    public void Write(ReadOnlySpan<byte> data)
    {
        byte[] output = Output;
        OperationStatus status;
        do
        {
            status = EncodeLoop.Compress(_encoder, data, output, out int consumed, out int written, false);
            if (written > 0)
            {
                _inner.Write(output, 0, written);
            }

            data = data[consumed..];
        }
        while (status == OperationStatus.DestinationTooSmall || data.Length > 0);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] output = Output;
        OperationStatus status;
        do
        {
            status = EncodeLoop.Compress(_encoder, data.Span, output, out int consumed, out int written, false);
            if (written > 0)
            {
                await _inner.WriteAsync(output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }

            data = data[consumed..];
        }
        while (status == OperationStatus.DestinationTooSmall || data.Length > 0);
    }

    public void Finish()
    {
        if (_finished || _output is null)
        {
            return;
        }

        _finished = true;
        OperationStatus status;
        do
        {
            status = EncodeLoop.Compress(_encoder, [], _output, out _, out int written, true);
            if (written > 0)
            {
                _inner.Write(_output, 0, written);
            }
        }
        while (status != OperationStatus.Done);
    }

    public async ValueTask FinishAsync(CancellationToken cancellationToken)
    {
        if (_finished || _output is null)
        {
            return;
        }

        _finished = true;
        OperationStatus status;
        do
        {
            status = EncodeLoop.Compress(_encoder, [], _output, out _, out int written, true);
            if (written > 0)
            {
                await _inner.WriteAsync(_output.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            }
        }
        while (status != OperationStatus.Done);
    }

    public void FlushInner() => _inner.Flush();

    public Task FlushInnerAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public void Dispose()
    {
        if (_output is null)
        {
            return;
        }

        try
        {
            Finish();
        }
        finally
        {
            Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_output is null)
        {
            return;
        }

        try
        {
            await FinishAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }

    private void Release()
    {
        ArrayPool<byte>.Shared.Return(_output!);
        _output = null;
        if (!_leaveOpen)
        {
            _inner.Dispose();
        }
    }

    private async ValueTask ReleaseAsync()
    {
        ArrayPool<byte>.Shared.Return(_output!);
        _output = null;
        if (!_leaveOpen)
        {
            await _inner.DisposeAsync().ConfigureAwait(false);
        }
    }
}
