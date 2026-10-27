using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Seekable Zstandard files (frames plus the seek table) at levels 3 and 15, with a 64 KiB and a 100000-byte frame size (the
/// second does not divide the inputs, so the last frame is short), with and without seek-table checksums. A frame is compressed
/// on its own, so the bytes may not depend on the thread count or on the vector paths.
/// </summary>
internal static class ZstdSeekableEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach (int level in new[] { 3, 15 })
        {
            foreach (int frameSize in new[] { 64 * 1024, 100_000 })
            {
                foreach (bool checksums in new[] { true, false })
                {
                    int l = level;
                    int f = frameSize;
                    bool c = checksums;
                    Matrix.Add("zstd-seekable", $"level{l}-frame{f}-{(c ? "checksums" : "nochecksums")}", (input, _) => ZstdSeekable.Compress(input, new ZstdSeekableOptions
                    {
                        FrameSize = f,
                        Checksums = c,
                        Compression = new ZstdCompressionOptions { Level = l },
                    }), "text-3m", "binary-1m", "text-small", "empty");
                }
            }
        }
    }
}
