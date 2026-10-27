namespace RecursiveAI.Bar;

/// <summary>A write-only stream over a caller's buffer.</summary>
internal sealed class MemorySink(Memory<byte> target) : Stream
{
    public int Written { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => Written;
    public override long Position { get => Written; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        buffer.CopyTo(target.Span[Written..]);
        Written += buffer.Length;
    }
}

/// <summary>A read-only stream over a caller's buffer.</summary>
internal sealed class ReadOnlyMemoryStream(ReadOnlyMemory<byte> source) : Stream
{
    private int _pos;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => source.Length;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = Math.Min(buffer.Length, source.Length - _pos);
        source.Span.Slice(_pos, n).CopyTo(buffer);
        _pos += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

internal static class StreamUtil
{
    public static int ReadFully(Stream s, Span<byte> dst)
    {
        int total = 0;
        while (total < dst.Length)
        {
            int n = s.Read(dst[total..]);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }
}
