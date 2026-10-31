using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.OneArchive.Zip;

/// <summary>How a zip entry's data is encrypted.</summary>
public enum ZipEncryption
{
    /// <summary>Not encrypted.</summary>
    None = 0,

    /// <summary>Traditional PKWARE encryption (APPNOTE 6.1). Read only: it is weak and is never written.</summary>
    ZipCrypto = 1,

    /// <summary>WinZip AES with a 128-bit key.</summary>
    Aes128 = 2,

    /// <summary>WinZip AES with a 192-bit key.</summary>
    Aes192 = 3,

    /// <summary>WinZip AES with a 256-bit key.</summary>
    Aes256 = 4,

    /// <summary>Encrypted in a way this library does not read (PKWARE strong encryption) or with an AES field that is malformed.</summary>
    Other = 5,
}

/// <summary>The key size of WinZip AES encryption.</summary>
public enum ZipAesStrength
{
    /// <summary>256-bit key (the default).</summary>
    Aes256 = 3,

    /// <summary>192-bit key.</summary>
    Aes192 = 2,

    /// <summary>128-bit key.</summary>
    Aes128 = 1,
}

/// <summary>The WinZip AES format version, which differs only in how the CRC-32 is stored.</summary>
public enum ZipAesVersion
{
    /// <summary>AE-2 (the default): the header CRC-32 is zero and the authentication code alone detects damage.</summary>
    Ae2 = 2,

    /// <summary>AE-1: the CRC-32 of the plain data is stored in the headers as well.</summary>
    Ae1 = 1,
}

/// <summary>What the headers of one entry say about its encryption (APPNOTE 4.4.4 bits 0 and 6, WinZip AES extra field 0x9901).</summary>
internal readonly struct ZipEncryptionInfo
{
    public const ushort ExtraAes = 0x9901;

    public ZipEncryption Kind { get; init; }

    /// <summary>1 for AE-1, 2 for AE-2, 0 otherwise.</summary>
    public int AesVersion { get; init; }

    /// <summary>The compression method of the data once decrypted (the real method for AES entries).</summary>
    public ushort ActualMethod { get; init; }

    /// <summary>Set when the entry cannot be read: what was expected.</summary>
    public string? Problem { get; init; }

    /// <summary>True when <see cref="Problem"/> is a feature this library lacks rather than a malformed header.</summary>
    public bool Unsupported { get; init; }

    public int AesKeyBytes => Kind switch { ZipEncryption.Aes128 => 16, ZipEncryption.Aes192 => 24, _ => 32 };

    public static ZipEncryptionInfo Inspect(ushort flags, ushort method, IReadOnlyList<ZipExtraField> extra)
    {
        bool flagged = (flags & 1) != 0;
        if (!flagged && method != (ushort)ZipMethod.Aes)
        {
            return new ZipEncryptionInfo { Kind = ZipEncryption.None, ActualMethod = method };
        }

        if ((flags & 0x40) != 0)
        {
            return new ZipEncryptionInfo { Kind = ZipEncryption.Other, ActualMethod = method, Problem = "PKWARE strong encryption", Unsupported = true };
        }

        if (method != (ushort)ZipMethod.Aes)
        {
            return new ZipEncryptionInfo { Kind = ZipEncryption.ZipCrypto, ActualMethod = method };
        }

        // WinZip AES document, "AES extra data field": version, vendor "AE", strength, real method.
        ReadOnlyMemory<byte> data = default;
        bool found = false;
        foreach (ZipExtraField f in extra)
        {
            if (f.Id == ExtraAes)
            {
                data = f.Data;
                found = true;
                break;
            }
        }

        ReadOnlySpan<byte> d = data.Span;
        if (!found || d.Length < 7)
        {
            return Bad("a WinZip AES extra field (0x9901) of at least 7 bytes for compression method 99");
        }

        ushort version = BinaryPrimitives.ReadUInt16LittleEndian(d);
        if (version is not (1 or 2))
        {
            return Bad($"WinZip AES vendor version 1 or 2, found {version}");
        }

        if (d[2] != (byte)'A' || d[3] != (byte)'E')
        {
            return Bad("the WinZip AES vendor id \"AE\"");
        }

        ZipEncryption kind = d[4] switch { 1 => ZipEncryption.Aes128, 2 => ZipEncryption.Aes192, 3 => ZipEncryption.Aes256, _ => ZipEncryption.Other };
        if (kind == ZipEncryption.Other)
        {
            return Bad($"a WinZip AES strength of 1, 2 or 3, found {d[4]}");
        }

        ushort actual = BinaryPrimitives.ReadUInt16LittleEndian(d[5..]);
        if (actual == (ushort)ZipMethod.Aes)
        {
            return Bad("a compression method other than 99 inside the WinZip AES field");
        }

        if (!flagged)
        {
            return Bad("bit 0 of the general purpose flags set on a WinZip AES entry");
        }

        return new ZipEncryptionInfo { Kind = kind, AesVersion = version, ActualMethod = actual };

        static ZipEncryptionInfo Bad(string expected) => new() { Kind = ZipEncryption.Other, Problem = expected };
    }
}

/// <summary>Turns a password into the bytes the zip encryption schemes use: UTF-8.</summary>
internal static class ZipPassword
{
    public static byte[] ToBytes(ReadOnlyMemory<char> password)
    {
        byte[] bytes = new byte[Encoding.UTF8.GetByteCount(password.Span)];
        Encoding.UTF8.GetBytes(password.Span, bytes);
        return bytes;
    }
}

/// <summary>Decrypts the data of one entry as it is read: a header before the data, an optional authentication code after it.</summary>
internal abstract class ZipDecryptor : IDisposable
{
    /// <summary>Gets the bytes stored before the data (salt and verifier, or the 12-byte header).</summary>
    public abstract int HeaderLength { get; }

    /// <summary>Gets the bytes stored after the data (the authentication code).</summary>
    public abstract int TrailerLength { get; }

    /// <summary>Checks the password against the header. Throws <see cref="ArchivePasswordException"/> when it does not match.</summary>
    public abstract void Start(ReadOnlySpan<byte> header);

    /// <summary>Decrypts <paramref name="data"/> in place. Data must be passed in order, in pieces of any size.</summary>
    public abstract void Decrypt(Span<byte> data);

    /// <summary>Returns whether the stored authentication code matches the data seen so far.</summary>
    public virtual bool Verify(ReadOnlySpan<byte> trailer) => true;

    public abstract void Dispose();
}

/// <summary>Traditional PKWARE encryption (APPNOTE section 6.1), read only.</summary>
internal sealed class ZipCryptoDecryptor : ZipDecryptor
{
    private static readonly uint[] s_crcTable = BuildTable();

    private readonly string _entryName;
    private readonly byte _crcCheck;
    private readonly byte _timeCheck;
    private readonly bool _acceptTime;
    private byte[]? _password;
    private uint _k0, _k1, _k2;

    // The check byte is the high byte of the CRC-32; when bit 3 is set the CRC is not known yet and the high byte of
    // the DOS time is used instead (APPNOTE 6.1.6). Writers differ, so with bit 3 either is accepted.
    public ZipCryptoDecryptor(ReadOnlyMemory<char> password, string entryName, uint crc, ushort dosTime, bool hasDescriptor)
    {
        _password = ZipPassword.ToBytes(password);
        _entryName = entryName;
        _crcCheck = (byte)(crc >> 24);
        _timeCheck = (byte)(dosTime >> 8);
        _acceptTime = hasDescriptor;
    }

    public override int HeaderLength => 12;

    public override int TrailerLength => 0;

    public override void Start(ReadOnlySpan<byte> header)
    {
        _k0 = 0x12345678;
        _k1 = 0x23456789;
        _k2 = 0x34567890;
        foreach (byte b in _password!)
        {
            Update(b);
        }

        byte last = 0;
        for (int i = 0; i < 12; i++)
        {
            last = DecryptByte(header[i]);
        }

        CryptographicOperations.ZeroMemory(_password);
        _password = null;
        bool ok = last == _crcCheck || (_acceptTime && last == _timeCheck);
        if (!ok)
        {
            throw new ArchivePasswordException(ZipFormat.Name, _entryName, ArchivePasswordProblem.Incorrect);
        }
    }

    public override void Decrypt(Span<byte> data)
    {
        for (int i = 0; i < data.Length; i++)
        {
            data[i] = DecryptByte(data[i]);
        }
    }

    public override void Dispose()
    {
        if (_password is { } p)
        {
            CryptographicOperations.ZeroMemory(p);
            _password = null;
        }

        _k0 = _k1 = _k2 = 0;
    }

    private byte DecryptByte(byte c)
    {
        uint t = _k2 | 2;
        c ^= (byte)((t * (t ^ 1)) >> 8);
        Update(c);
        return c;
    }

    private void Update(byte c)
    {
        _k0 = (_k0 >> 8) ^ s_crcTable[(byte)(_k0 ^ c)];
        _k1 = (_k1 + (_k0 & 0xFF)) * 134775813 + 1;
        _k2 = (_k2 >> 8) ^ s_crcTable[(byte)(_k2 ^ (_k1 >> 24))];
    }

    // The CRC-32 table of the zip polynomial, used here one byte at a time without the usual pre- and post-inversion.
    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            }

            t[i] = c;
        }

        return t;
    }
}

/// <summary>
/// WinZip AES key schedule and keystream (WinZip AES document, "Key generation" and "Encrypted file data"): PBKDF2 with
/// HMAC-SHA1 and 1000 iterations yields the encryption key, the authentication key and a 2-byte verifier; the data is
/// encrypted with AES in counter mode whose 16-byte little-endian counter starts at 1, and authenticated with HMAC-SHA1
/// over the ciphertext, truncated to 10 bytes.
/// </summary>
internal sealed class ZipAesCore : IDisposable
{
    public const int TagLength = 10;
    private const int Blocks = 64; // keystream blocks produced per AES call

    private readonly Aes _aes;
    private readonly IncrementalHash _mac;
    private readonly byte[] _counters = new byte[Blocks * 16];
    private readonly byte[] _stream = new byte[Blocks * 16];
    private ulong _counterLow = 1;
    private ulong _counterHigh;
    private int _streamPos = Blocks * 16;

    public ZipAesCore(ReadOnlySpan<byte> password, int keyBytes, ReadOnlySpan<byte> salt, Span<byte> verifier)
    {
        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(password, salt, 1000, HashAlgorithmName.SHA1, (2 * keyBytes) + 2);
        try
        {
            _aes = Aes.Create();
            _aes.SetKey(derived.AsSpan(0, keyBytes));
            _mac = IncrementalHash.CreateHMAC(HashAlgorithmName.SHA1, derived.AsSpan(keyBytes, keyBytes));
            derived.AsSpan((2 * keyBytes), 2).CopyTo(verifier);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(derived);
        }
    }

    public static int SaltLength(int keyBytes) => keyBytes / 2;

    /// <summary>Feeds ciphertext to the authentication code.</summary>
    public void Authenticate(ReadOnlySpan<byte> ciphertext) => _mac.AppendData(ciphertext);

    /// <summary>Gets the 10-byte authentication code of the ciphertext seen so far.</summary>
    public void GetTag(Span<byte> tag)
    {
        Span<byte> full = stackalloc byte[20];
        _mac.GetCurrentHash(full);
        full[..TagLength].CopyTo(tag);
    }

    /// <summary>XORs the keystream into <paramref name="data"/> (encrypts or decrypts; counter mode is symmetric).</summary>
    public void Crypt(Span<byte> data)
    {
        while (!data.IsEmpty)
        {
            if (_streamPos == _stream.Length)
            {
                for (int i = 0; i < Blocks; i++)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(_counters.AsSpan(i * 16), _counterLow);
                    BinaryPrimitives.WriteUInt64LittleEndian(_counters.AsSpan((i * 16) + 8), _counterHigh);
                    if (++_counterLow == 0)
                    {
                        _counterHigh++;
                    }
                }

                _aes.EncryptEcb(_counters, _stream, PaddingMode.None);
                _streamPos = 0;
            }

            int n = Math.Min(data.Length, _stream.Length - _streamPos);
            for (int i = 0; i < n; i++)
            {
                data[i] ^= _stream[_streamPos + i];
            }

            _streamPos += n;
            data = data[n..];
        }
    }

    public void Dispose()
    {
        _aes.Dispose();
        _mac.Dispose();
        CryptographicOperations.ZeroMemory(_stream);
        CryptographicOperations.ZeroMemory(_counters);
    }
}

internal sealed class ZipAesDecryptor : ZipDecryptor
{
    private readonly string _entryName;
    private readonly int _keyBytes;
    private byte[]? _password;
    private ZipAesCore? _core;

    public ZipAesDecryptor(ReadOnlyMemory<char> password, int keyBytes, string entryName)
    {
        _password = ZipPassword.ToBytes(password);
        _keyBytes = keyBytes;
        _entryName = entryName;
    }

    public override int HeaderLength => ZipAesCore.SaltLength(_keyBytes) + 2;

    public override int TrailerLength => ZipAesCore.TagLength;

    public override void Start(ReadOnlySpan<byte> header)
    {
        int saltLength = ZipAesCore.SaltLength(_keyBytes);
        Span<byte> verifier = stackalloc byte[2];
        var core = new ZipAesCore(_password!, _keyBytes, header[..saltLength], verifier);
        CryptographicOperations.ZeroMemory(_password);
        _password = null;
        if (!CryptographicOperations.FixedTimeEquals(verifier, header.Slice(saltLength, 2)))
        {
            core.Dispose();
            throw new ArchivePasswordException(ZipFormat.Name, _entryName, ArchivePasswordProblem.Incorrect);
        }

        _core = core;
    }

    public override void Decrypt(Span<byte> data)
    {
        _core!.Authenticate(data);
        _core.Crypt(data);
    }

    public override bool Verify(ReadOnlySpan<byte> trailer)
    {
        Span<byte> tag = stackalloc byte[ZipAesCore.TagLength];
        _core!.GetTag(tag);
        return CryptographicOperations.FixedTimeEquals(tag, trailer);
    }

    public override void Dispose()
    {
        if (_password is { } p)
        {
            CryptographicOperations.ZeroMemory(p);
            _password = null;
        }

        _core?.Dispose();
    }
}

/// <summary>Encrypts what is written to it with WinZip AES: salt and verifier first, then the ciphertext, then <see cref="Complete"/> adds the authentication code.</summary>
internal sealed class ZipAesEncryptStream : Stream
{
    private readonly Stream _inner;
    private readonly ZipAesCore _core;
    private byte[]? _pendingHeader;
    private byte[]? _work = ArrayPool<byte>.Shared.Rent(32 * 1024);

    public ZipAesEncryptStream(Stream inner, ReadOnlyMemory<char> password, int keyBytes, Action<Span<byte>>? randomBytes)
    {
        _inner = inner;
        int saltLength = ZipAesCore.SaltLength(keyBytes);
        byte[] header = new byte[saltLength + 2];
        Span<byte> salt = header.AsSpan(0, saltLength);
        if (randomBytes is not null)
        {
            randomBytes(salt);
        }
        else
        {
            RandomNumberGenerator.Fill(salt);
        }

        byte[] pw = ZipPassword.ToBytes(password);
        try
        {
            _core = new ZipAesCore(pw, keyBytes, salt, header.AsSpan(saltLength, 2));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pw);
        }

        _pendingHeader = header;
    }

    /// <summary>Gets the bytes this stream adds to the data: salt, verifier and authentication code.</summary>
    public static int Overhead(int keyBytes) => ZipAesCore.SaltLength(keyBytes) + 2 + ZipAesCore.TagLength;

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
        if (TakeHeader() is { } h)
        {
            _inner.Write(h);
        }

        while (!buffer.IsEmpty)
        {
            int n = Encrypt(ref buffer);
            _inner.Write(_work.AsSpan(0, n));
        }
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (TakeHeader() is { } h)
        {
            await _inner.WriteAsync(h, cancellationToken).ConfigureAwait(false);
        }

        while (!buffer.IsEmpty)
        {
            ReadOnlySpan<byte> span = buffer.Span;
            int n = Encrypt(ref span);
            buffer = buffer[(buffer.Length - span.Length)..];
            await _inner.WriteAsync(_work.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes the salt (if nothing was written yet) and the authentication code.</summary>
    public void Complete()
    {
        if (TakeHeader() is { } h)
        {
            _inner.Write(h);
        }

        _inner.Write(Tag());
    }

    /// <inheritdoc cref="Complete"/>
    public async ValueTask CompleteAsync(CancellationToken cancellationToken)
    {
        if (TakeHeader() is { } h)
        {
            await _inner.WriteAsync(h, cancellationToken).ConfigureAwait(false);
        }

        await _inner.WriteAsync(Tag(), cancellationToken).ConfigureAwait(false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _core.Dispose();
            if (_work is { } w)
            {
                CryptographicOperations.ZeroMemory(w);
                ArrayPool<byte>.Shared.Return(w);
                _work = null;
            }
        }

        base.Dispose(disposing);
    }

    private byte[]? TakeHeader()
    {
        byte[]? h = _pendingHeader;
        _pendingHeader = null;
        return h;
    }

    private byte[] Tag()
    {
        byte[] tag = new byte[ZipAesCore.TagLength];
        _core.GetTag(tag);
        return tag;
    }

    // Encrypts the front of the input into the work buffer and advances the input past it.
    private int Encrypt(ref ReadOnlySpan<byte> input)
    {
        byte[] work = _work ?? throw new ObjectDisposedException(nameof(ZipAesEncryptStream));
        int n = Math.Min(input.Length, work.Length);
        input[..n].CopyTo(work);
        Span<byte> w = work.AsSpan(0, n);
        _core.Crypt(w);
        _core.Authenticate(w);
        input = input[n..];
        return n;
    }
}
