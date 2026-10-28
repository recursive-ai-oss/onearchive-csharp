using System.Buffers;

namespace RecursiveAI.OneCompress.Deflate;

// The shared body of Deflate.Compress, ZLib.Compress and GZip.Compress. Built on DeflateEncoder only.
internal static class DeflateCompressOneShot
{
    public static byte[] Compress(DeflateFormat format, ReadOnlySpan<byte> source, DeflateOptions? options)
    {
        var encoder = new DeflateEncoder(format, options, source.Length);
        long bound = DeflateEncoder.GetMaxCompressedLength(format, source.Length, options);
        byte[] buffer = GC.AllocateUninitializedArray<byte>(checked((int)bound));
        OperationStatus status = encoder.Compress(source, buffer, out _, out int written, DeflateFlush.Finish);
        if (status != OperationStatus.Done)
        {
            throw new InvalidOperationException("The compressed size passed its bound.");
        }

        return buffer.AsSpan(0, written).ToArray();
    }

    public static bool TryCompress(DeflateFormat format, ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten, DeflateOptions? options)
    {
        var encoder = new DeflateEncoder(format, options, source.Length);
        OperationStatus status = encoder.Compress(source, destination, out _, out int written, DeflateFlush.Finish);
        bytesWritten = status == OperationStatus.Done ? written : 0;
        return status == OperationStatus.Done;
    }
}
