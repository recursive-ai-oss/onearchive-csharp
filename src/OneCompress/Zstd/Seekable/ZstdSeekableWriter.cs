using System.Diagnostics.CodeAnalysis;
using System.Buffers;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// Writes a seekable Zstandard file: the content is cut into frames of a fixed size, each compressed on its own, and a
/// seek table is appended as a final skippable frame. The result is an ordinary Zstandard file that any decoder reads
/// start to end; <see cref="ZstdSeekableReader"/> uses the table to read ranges.
/// </summary>
/// <remarks>
/// The bytes depend only on the content, the options and any <see cref="EndFrame"/> calls, never on how the content is
/// split across <c>Write</c> calls. A writer owns one encoder and one frame buffer and can be reused for another file with
/// <see cref="Reset(Stream)"/>. Not thread-safe. Call <see cref="Complete"/> to finish the file; disposing a writer that
/// is not complete and not faulted completes it.
/// </remarks>
[Experimental("ONECOMPRESS001", UrlFormat = "https://github.com/recursive-ai-oss/onearchive-csharp#preview-codecs")]
public sealed class ZstdSeekableWriter : IDisposable, IAsyncDisposable
{
    private readonly int _frameSize;
    private readonly bool _checksums;
    private readonly bool _leaveOpen;
    private readonly ZstdEncoder _encoder;
    private readonly ZstdSeekTableBuilder _table;
    private Stream? _stream;
    private IBufferWriter<byte>? _sink;
    private byte[] _buffer = [];
    private int _fill;
    private byte[] _scratch = [];
    private bool _completed;
    private bool _faulted;
    private bool _disposed;

    /// <summary>Creates a writer that writes to a stream.</summary>
    /// <param name="destination">A writable stream; it need not seek.</param>
    /// <param name="options">Frame size, checksums and compression options; <see langword="null"/> for the defaults.</param>
    /// <param name="leaveOpen">True to leave the stream open when the writer is disposed.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable, or <see cref="ZstdCompressionOptions.ContentSize"/> is set.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame size is not from 1 to <see cref="ZstdSeekableOptions.MaxFrameSize"/>, or a compression option is out of range.</exception>
    public ZstdSeekableWriter(Stream destination, ZstdSeekableOptions? options = null, bool leaveOpen = false)
        : this(options)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(destination));
        }

        _stream = destination;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Creates a writer that writes to a buffer writer; frames are compressed straight into its buffers.</summary>
    /// <param name="destination">Receives the file.</param>
    /// <param name="options">Frame size, checksums and compression options; <see langword="null"/> for the defaults.</param>
    /// <exception cref="ArgumentException"><see cref="ZstdCompressionOptions.ContentSize"/> is set.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The frame size is not from 1 to <see cref="ZstdSeekableOptions.MaxFrameSize"/>, or a compression option is out of range.</exception>
    public ZstdSeekableWriter(IBufferWriter<byte> destination, ZstdSeekableOptions? options = null)
        : this(options)
    {
        ArgumentNullException.ThrowIfNull(destination);
        _sink = destination;
    }

    private ZstdSeekableWriter(ZstdSeekableOptions? options)
    {
        options ??= ZstdSeekableOptions.Default;
        if (options.FrameSize < 1 || options.FrameSize > ZstdSeekableOptions.MaxFrameSize)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "FrameSize must be from 1 to 1 GiB.");
        }

        ArgumentNullException.ThrowIfNull(options.Compression);
        if (options.Compression.ContentSize is not null)
        {
            throw new ArgumentException("ContentSize cannot be set for a seekable file: every frame declares its own size.", nameof(options));
        }

        _frameSize = options.FrameSize;
        _checksums = options.Checksums;
        _encoder = new ZstdEncoder(options.Compression);
        _table = new ZstdSeekTableBuilder(_checksums);
    }

    /// <summary>The number of frames written so far.</summary>
    public int FramesWritten => _table.Count;

    /// <summary>The number of content bytes accepted so far.</summary>
    public long BytesIn { get; private set; }

    /// <summary>The number of compressed bytes produced so far, including the seek table once complete.</summary>
    public long BytesOut { get; private set; }

    /// <summary>Points the writer at a new stream and forgets the previous file, keeping its buffers and encoder.</summary>
    /// <param name="destination">A writable stream.</param>
    /// <exception cref="ArgumentException"><paramref name="destination"/> is not writable.</exception>
    public void Reset(Stream destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite)
        {
            throw new ArgumentException("The stream must be writable.", nameof(destination));
        }

        _stream = destination;
        _sink = null;
        ResetState();
    }

    /// <summary>Points the writer at a new buffer writer and forgets the previous file, keeping its buffers and encoder.</summary>
    /// <param name="destination">Receives the file.</param>
    public void Reset(IBufferWriter<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(destination);
        _sink = destination;
        _stream = null;
        ResetState();
    }

    private void ResetState()
    {
        _table.Clear();
        _fill = 0;
        BytesIn = 0;
        BytesOut = 0;
        _completed = false;
        _faulted = false;
    }

    /// <summary>Adds content, writing every frame that fills up.</summary>
    /// <param name="data">The content.</param>
    /// <exception cref="InvalidOperationException">The file is already complete.</exception>
    public void Write(ReadOnlySpan<byte> data)
    {
        ThrowIfNotWritable();
        try
        {
            BytesIn += data.Length;
            while (!data.IsEmpty)
            {
                if (_fill == 0 && data.Length >= _frameSize)
                {
                    EmitFrame(data[.._frameSize]);
                    data = data[_frameSize..];
                    continue;
                }

                int take = Buffer(data);
                data = data[take..];
                if (_fill == _frameSize)
                {
                    EmitBuffered();
                }
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Adds content, writing every frame that fills up.</summary>
    /// <param name="data">The content.</param>
    /// <param name="cancellationToken">Cancels the writes to the stream.</param>
    /// <exception cref="InvalidOperationException">The file is already complete.</exception>
    /// <exception cref="OperationCanceledException">The token was cancelled; the writer is then unusable.</exception>
    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        ThrowIfNotWritable();
        try
        {
            BytesIn += data.Length;
            while (!data.IsEmpty)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_fill == 0 && data.Length >= _frameSize)
                {
                    await EmitFrameAsync(data[.._frameSize], cancellationToken).ConfigureAwait(false);
                    data = data[_frameSize..];
                    continue;
                }

                int take = Buffer(data.Span);
                data = data[take..];
                if (_fill == _frameSize)
                {
                    await EmitFrameAsync(_buffer.AsMemory(0, _fill), cancellationToken, fromBuffer: true).ConfigureAwait(false);
                }
            }
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>
    /// Ends the current frame now, even if it is shorter than the frame size; the next byte starts a new frame. A way to put a
    /// record boundary at a frame boundary. Does nothing when no content is waiting.
    /// </summary>
    /// <exception cref="InvalidOperationException">The file is already complete.</exception>
    public void EndFrame()
    {
        ThrowIfNotWritable();
        if (_fill > 0)
        {
            try
            {
                EmitBuffered();
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    /// <summary>Ends the current frame now; see <see cref="EndFrame"/>.</summary>
    /// <param name="cancellationToken">Cancels the write to the stream.</param>
    public async ValueTask EndFrameAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfNotWritable();
        if (_fill > 0)
        {
            try
            {
                await EmitFrameAsync(_buffer.AsMemory(0, _fill), cancellationToken, fromBuffer: true).ConfigureAwait(false);
            }
            catch
            {
                _faulted = true;
                throw;
            }
        }
    }

    /// <summary>Writes the last frame and the seek table. Calling it again does nothing.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        ThrowIfNotWritable();
        try
        {
            if (_fill > 0)
            {
                EmitBuffered();
            }

            int length = checked((int)_table.EncodedLength);
            if (_sink is not null)
            {
                _table.WriteTo(_sink);
            }
            else
            {
                EnsureScratch(length);
                _table.WriteTo(_scratch);
                _stream!.Write(_scratch, 0, length);
            }

            BytesOut += length;
            _completed = true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Writes the last frame and the seek table. Calling it again does nothing.</summary>
    /// <param name="cancellationToken">Cancels the writes to the stream.</param>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken = default)
    {
        if (_completed)
        {
            return;
        }

        ThrowIfNotWritable();
        try
        {
            if (_fill > 0)
            {
                await EmitFrameAsync(_buffer.AsMemory(0, _fill), cancellationToken, fromBuffer: true).ConfigureAwait(false);
            }

            int length = checked((int)_table.EncodedLength);
            if (_sink is not null)
            {
                _table.WriteTo(_sink);
            }
            else
            {
                EnsureScratch(length);
                _table.WriteTo(_scratch);
                await _stream!.WriteAsync(_scratch.AsMemory(0, length), cancellationToken).ConfigureAwait(false);
            }

            BytesOut += length;
            _completed = true;
        }
        catch
        {
            _faulted = true;
            throw;
        }
    }

    /// <summary>Completes the file if that has not happened, then closes the stream unless it was left open.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_completed && !_faulted)
            {
                Complete();
            }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen)
            {
                _stream?.Dispose();
            }
        }
    }

    /// <summary>Completes the file if that has not happened, then closes the stream unless it was left open.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            if (!_completed && !_faulted)
            {
                await CompleteAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _disposed = true;
            if (!_leaveOpen && _stream is not null)
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private void ThrowIfNotWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_completed)
        {
            throw new InvalidOperationException("The seekable file is complete; call Reset to start another.");
        }

        if (_faulted)
        {
            throw new InvalidOperationException("The writer failed earlier and cannot continue; call Reset to start another file.");
        }
    }

    // Copies as much of data as fits in the frame buffer; returns the number of bytes taken.
    private int Buffer(ReadOnlySpan<byte> data)
    {
        int take = Math.Min(_frameSize - _fill, data.Length);
        if (_buffer.Length < _fill + take)
        {
            // Grow geometrically but never past one frame, so a small file does not pay for a large frame size.
            Array.Resize(ref _buffer, (int)Math.Min(_frameSize, Math.Max((long)_fill + take, (long)_buffer.Length * 2)));
        }

        data[..take].CopyTo(_buffer.AsSpan(_fill));
        _fill += take;
        return take;
    }

    private void EmitBuffered()
    {
        EmitFrame(_buffer.AsSpan(0, _fill));
        _fill = 0;
    }

    private void EmitFrame(ReadOnlySpan<byte> frame)
    {
        int written = Encode(frame, out bool inScratch);
        if (inScratch)
        {
            _stream!.Write(_scratch, 0, written);
        }
    }

    private async ValueTask EmitFrameAsync(ReadOnlyMemory<byte> frame, CancellationToken cancellationToken, bool fromBuffer = false)
    {
        int written = Encode(frame.Span, out bool inScratch);
        if (fromBuffer)
        {
            _fill = 0;
        }

        if (inScratch)
        {
            await _stream!.WriteAsync(_scratch.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
        }
    }

    // Compresses one frame into the sink's buffer or the scratch array and records its seek table entry.
    private int Encode(ReadOnlySpan<byte> frame, out bool inScratch)
    {
        int max = ZstdEncoder.GetMaxCompressedLength(frame.Length);
        int written;
        if (_sink is not null)
        {
            Span<byte> span = _sink.GetSpan(max);
            written = _encoder.CompressFrame(frame, span);
            _sink.Advance(written);
            inScratch = false;
        }
        else
        {
            EnsureScratch(max);
            written = _encoder.CompressFrame(frame, _scratch);
            inScratch = true;
        }

        // A frame never exceeds 1 GiB of content, and the bound adds a few bytes per block, so both fit the table's 32-bit fields.
        _table.Add((uint)written, (uint)frame.Length, _checksums ? (uint)XxHash64.Compute(frame) : 0);
        BytesOut += written;
        return written;
    }

    private void EnsureScratch(int size)
    {
        if (_scratch.Length < size)
        {
            _scratch = new byte[size];
        }
    }
}
