using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.SevenZip;

/// <summary>
/// The 7z AES-256 coder (method ID 06 F1 07 01). 7zFormat.txt and Methods.txt name the method but not its properties or key
/// derivation; those were pinned by experiment and are written down in <c>docs/7z-aes-derivation.md</c>.
/// </summary>
internal static class SevenZipAes
{
    public const int KeyLength = 32;
    public const int BlockLength = 16;

    /// <summary>The key-derivation cost 7-Zip writes: 2^19 rounds.</summary>
    public const int DefaultPower = 19;

    /// <summary>The parsed coder properties.</summary>
    internal readonly record struct Parameters(int Power, byte[] Salt, byte[] Iv);

    /// <summary>Parses the property bytes: two flag bytes, then the salt, then the IV (zero-padded to 16 bytes by the caller of the cipher).</summary>
    public static bool TryParse(ReadOnlySpan<byte> props, out Parameters parameters, out string? problem)
    {
        parameters = default;
        problem = null;
        if (props.Length < 2)
        {
            problem = "at least two property bytes";
            return false;
        }

        // Experiment 1 and 2: bits 0-5 of the first byte are the power; bit 7 and bit 6 are the extra byte of the salt and
        // IV sizes whose remaining four bits are the high and low nibbles of the second byte.
        int power = props[0] & 0x3F;
        int saltLength = ((props[0] >> 7) & 1) + (props[1] >> 4);
        int ivLength = ((props[0] >> 6) & 1) + (props[1] & 0xF);
        if (props.Length != 2 + saltLength + ivLength)
        {
            problem = $"{2 + saltLength + ivLength} property bytes for a {saltLength}-byte salt and a {ivLength}-byte IV, found {props.Length}";
            return false;
        }

        if (ivLength != BlockLength)
        {
            // Experiment 3: 7-Zip itself rejects other IV sizes.
            problem = $"a 16-byte IV, found {ivLength} bytes";
            return false;
        }

        parameters = new Parameters(power, props.Slice(2, saltLength).ToArray(), props.Slice(2 + saltLength, ivLength).ToArray());
        return true;
    }

    /// <summary>Builds the property bytes for a new coder: no salt, a 16-byte IV (the layout 7-Zip writes).</summary>
    public static byte[] BuildProperties(int power, ReadOnlySpan<byte> iv)
    {
        byte[] props = new byte[2 + iv.Length];
        props[0] = (byte)(0x40 | power); // bit 6: the IV size 16 is one more than the nibble below
        props[1] = (byte)(iv.Length - 1);
        iv.CopyTo(props.AsSpan(2));
        return props;
    }

    /// <summary>
    /// Derives the key: SHA-256 over 2^power repetitions of salt, the password as UTF-16LE and an 8-byte little-endian
    /// round counter (experiment 1).
    /// </summary>
    public static byte[] DeriveKey(ReadOnlyMemory<char> password, ReadOnlySpan<byte> salt, int power)
    {
        byte[] pw = new byte[Encoding.Unicode.GetByteCount(password.Span)];
        Encoding.Unicode.GetBytes(password.Span, pw);
        if (!BitConverter.IsLittleEndian)
        {
            for (int i = 0; i + 1 < pw.Length; i += 2)
            {
                (pw[i], pw[i + 1]) = (pw[i + 1], pw[i]);
            }
        }

        int round = salt.Length + pw.Length + 8;
        int perChunk = Math.Max(1, (64 * 1024) / round);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(perChunk * round);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            ulong total = 1UL << power;
            ulong counter = 0;
            while (counter < total)
            {
                int n = (int)Math.Min((ulong)perChunk, total - counter);
                Span<byte> w = buffer;
                for (int i = 0; i < n; i++)
                {
                    salt.CopyTo(w);
                    pw.CopyTo(w[salt.Length..]);
                    BinaryPrimitives.WriteUInt64LittleEndian(w[(salt.Length + pw.Length)..], counter++);
                    w = w[round..];
                }

                hash.AppendData(buffer.AsSpan(0, n * round));
            }

            return hash.GetHashAndReset();
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

/// <summary>Remembers the last derived key so that the folders of one archive, which share a password and usually a salt, derive it once.</summary>
internal sealed class SevenZipKeyCache : IDisposable
{
    private byte[]? _key;
    private byte[] _salt = [];
    private int _power = -1;

    public byte[] Get(ReadOnlyMemory<char> password, SevenZipAes.Parameters p)
    {
        if (_key is null || _power != p.Power || !_salt.AsSpan().SequenceEqual(p.Salt))
        {
            Clear();
            _key = SevenZipAes.DeriveKey(password, p.Salt, p.Power);
            _salt = p.Salt;
            _power = p.Power;
        }

        return _key;
    }

    public void Clear()
    {
        if (_key is not null)
        {
            CryptographicOperations.ZeroMemory(_key);
            _key = null;
        }
    }

    public void Dispose() => Clear();
}

/// <summary>Decrypts AES-256 in CBC mode without padding; the output is cut to the coder's unpack size by the caller.</summary>
internal sealed class SevenZipAesDecryptStream : Stream
{
    private readonly Stream _input;
    private readonly Aes _aes;
    private readonly byte[] _iv;
    private readonly long _errorOffset;
    private byte[]? _buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _pos;
    private int _end;
    private bool _eof;

    public SevenZipAesDecryptStream(Stream input, byte[] key, byte[] iv, long errorOffset)
    {
        _input = input;
        _errorOffset = errorOffset;
        _iv = (byte[])iv.Clone();
        _aes = Aes.Create();
        _aes.SetKey(key);
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        byte[] buf = _buf ?? throw new ObjectDisposedException(nameof(SevenZipAesDecryptStream));
        if (buffer.IsEmpty)
        {
            return 0;
        }

        Span<byte> lastBlock = stackalloc byte[SevenZipAes.BlockLength];
        while (_pos == _end)
        {
            if (_eof)
            {
                return 0;
            }

            // Read whole blocks only: a partial block at the end is a damaged packed stream.
            int filled = 0;
            while (filled < buf.Length)
            {
                int n = _input.Read(buf, filled, buf.Length - filled);
                if (n == 0)
                {
                    _eof = true;
                    break;
                }

                filled += n;
            }

            int whole = filled & ~(SevenZipAes.BlockLength - 1);
            if (_eof && whole != filled)
            {
                throw new CompressionFormatException(SevenZipFormat.Name, _errorOffset, "an encrypted stream whose size is a multiple of 16 bytes", true);
            }

            if (whole == 0)
            {
                return 0;
            }

            // The next chunk's IV is the last ciphertext block of this one.
            buf.AsSpan(whole - SevenZipAes.BlockLength, SevenZipAes.BlockLength).CopyTo(lastBlock);
            _aes.DecryptCbc(buf.AsSpan(0, whole), _iv, buf.AsSpan(0, whole), PaddingMode.None);
            lastBlock.CopyTo(_iv);
            _pos = 0;
            _end = whole;
        }

        int take = Math.Min(buffer.Length, _end - _pos);
        buf.AsSpan(_pos, take).CopyTo(buffer);
        _pos += take;
        return take;
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_buf is { } b)
            {
                CryptographicOperations.ZeroMemory(b);
                ArrayPool<byte>.Shared.Return(b);
                _buf = null;
            }

            _aes.Dispose();
            _input.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Encrypts what is written with AES-256 in CBC mode; <see cref="Complete"/> zero-pads the last block (experiment 4).</summary>
internal sealed class SevenZipAesEncryptStream : Stream
{
    private readonly Stream _sink;
    private readonly Aes _aes;
    private readonly byte[] _iv;
    private byte[]? _buf = ArrayPool<byte>.Shared.Rent(64 * 1024);
    private int _fill;
    private bool _completed;

    public SevenZipAesEncryptStream(Stream sink, byte[] key, byte[] iv)
    {
        _sink = sink;
        _iv = (byte[])iv.Clone();
        _aes = Aes.Create();
        _aes.SetKey(key);
    }

    /// <summary>Gets the number of bytes written to this stream, which is the coder's unpack size.</summary>
    public long InputCount { get; private set; }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        byte[] buf = _buf ?? throw new ObjectDisposedException(nameof(SevenZipAesEncryptStream));
        InputCount += buffer.Length;
        while (!buffer.IsEmpty)
        {
            int n = Math.Min(buffer.Length, buf.Length - _fill);
            buffer[..n].CopyTo(buf.AsSpan(_fill));
            _fill += n;
            buffer = buffer[n..];
            if (_fill == buf.Length)
            {
                EncryptAndWrite(_fill);
                _fill = 0;
            }
        }
    }

    /// <summary>Pads the last block with zeros and writes everything out. The sink stays open.</summary>
    public void Complete()
    {
        if (_completed)
        {
            return;
        }

        byte[] buf = _buf ?? throw new ObjectDisposedException(nameof(SevenZipAesEncryptStream));
        _completed = true;
        int padded = (_fill + SevenZipAes.BlockLength - 1) & ~(SevenZipAes.BlockLength - 1);
        buf.AsSpan(_fill, padded - _fill).Clear();
        if (padded > 0)
        {
            EncryptAndWrite(padded);
        }

        _fill = 0;
    }

    // Disposing finishes the stream like any buffered stream; the chain of encoders above may dispose it before the writer asks.
    protected override void Dispose(bool disposing)
    {
        if (disposing && _buf is not null)
        {
            try
            {
                Complete();
            }
            finally
            {
                if (_buf is { } b)
                {
                    CryptographicOperations.ZeroMemory(b);
                    ArrayPool<byte>.Shared.Return(b);
                    _buf = null;
                }

                _aes.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private void EncryptAndWrite(int length)
    {
        byte[] buf = _buf!;
        _aes.EncryptCbc(buf.AsSpan(0, length), _iv, buf.AsSpan(0, length), PaddingMode.None);
        buf.AsSpan(length - SevenZipAes.BlockLength, SevenZipAes.BlockLength).CopyTo(_iv);
        _sink.Write(buf.AsSpan(0, length));
    }
}
