using System.Buffers;

namespace RecursiveAI.OneCompress.Lzma;

/// <summary>
/// A first-in first-out byte buffer the encoders write finished output into and callers drain into their destination spans.
/// It grows when written to and is reused, so steady-state streaming allocates nothing.
/// </summary>
internal sealed class OutputQueue : IBufferWriter<byte>
{
    private byte[] _data = new byte[1 << 12];
    private int _head;
    private int _tail;

    public int Count => _tail - _head;

    public void Clear()
    {
        _head = 0;
        _tail = 0;
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(GetSpan(bytes.Length));
        Advance(bytes.Length);
    }

    public void WriteByte(byte value)
    {
        GetSpan(1)[0] = value;
        Advance(1);
    }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _tail += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _data.AsMemory(_tail);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Reserve(sizeHint);
        return _data.AsSpan(_tail);
    }

    // Copies as much as fits into destination and removes it from the queue.
    public int Drain(Span<byte> destination)
    {
        int n = Math.Min(destination.Length, Count);
        _data.AsSpan(_head, n).CopyTo(destination);
        _head += n;
        if (_head == _tail)
        {
            _head = 0;
            _tail = 0;
        }

        return n;
    }

    private void Reserve(int size)
    {
        size = Math.Max(size, 1);
        if (_data.Length - _tail >= size)
        {
            return;
        }

        int live = _tail - _head;
        if (_head > 0 && _data.Length - live >= size)
        {
            Buffer.BlockCopy(_data, _head, _data, 0, live);
        }
        else
        {
            byte[] bigger = new byte[Math.Max(_data.Length * 2, live + size)];
            Buffer.BlockCopy(_data, _head, bigger, 0, live);
            _data = bigger;
        }

        _head = 0;
        _tail = live;
    }
}
