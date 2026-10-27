using System.Diagnostics;
using System.Security.Cryptography;
using RecursiveAI.OneCompress.Zstd;

namespace RecursiveAI.OneCompress.Tests;

/// <summary>
/// Seekable-format interop. Our files must decode with the zstd tool (they are ordinary frames plus a skippable frame) and be
/// readable by a seekable reader from outside this library; files from an outside seekable producer must open in our reader.
/// The outside programs are the seekable-format examples built by tools/build-zstd-seekable-examples.sh, driven as black
/// boxes. These tests run only when ONEARCHIVE_INTEROP=1 and fail loudly then when a tool or corpus is missing.
/// Environment: ONEARCHIVE_ZSTD, ONEARCHIVE_SEEKABLE_COMPRESS, ONEARCHIVE_SEEKABLE_DECOMPRESS (tool paths), ONEARCHIVE_CORPORA,
/// ONEARCHIVE_INTEROP_TMP.
/// </summary>
[Trait("Category", "Interop")]
public class ZstdSeekableInteropTests
{
    private static string Home { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "onearchive");

    private static string ZstdTool => Environment.GetEnvironmentVariable("ONEARCHIVE_ZSTD") ?? Path.Combine(Home, "tools", "bin", "zstd");

    private static string SeekableCompress => Environment.GetEnvironmentVariable("ONEARCHIVE_SEEKABLE_COMPRESS") ?? Path.Combine(Home, "tools", "bin", "seekable-compress");

    private static string SeekableDecompress => Environment.GetEnvironmentVariable("ONEARCHIVE_SEEKABLE_DECOMPRESS") ?? Path.Combine(Home, "tools", "bin", "seekable-decompress");

    private static string Corpora => Environment.GetEnvironmentVariable("ONEARCHIVE_CORPORA") ?? Path.Combine(Home, "corpora");

    private static string Scratch { get; } = MakeScratch();

    private static string MakeScratch()
    {
        string root = Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP_TMP") ?? Path.GetTempPath();
        string dir = Path.Combine(root, "zstd-seekable-interop-" + Environment.ProcessId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static TheoryData<string, int, int> OurOutputCases()
    {
        var data = new TheoryData<string, int, int>();
        foreach (string file in new[] { "silesia.tar", "enwik8" })
        {
            foreach (int level in new[] { 1, 3, 9, 15 })
            {
                foreach (int frame in new[] { 64 << 10, 1 << 20, 8 << 20 })
                {
                    data.Add(file, level, frame);
                }
            }
        }

        return data;
    }

    private static void RequireEnabled()
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") == "1", "set ONEARCHIVE_INTEROP=1 to run interop tests");
    }

    private static string Find(string name)
    {
        foreach (string candidate in new[] { Path.Combine(Corpora, name), Path.Combine(Corpora, "silesia", name), Path.Combine(Corpora, "enwik8", name) })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException($"corpus file '{name}' not found under {Corpora}");
    }

    // Runs a program and returns its standard output; fails with the error text on a non-zero exit.
    private static byte[] Run(string program, params string[] args)
    {
        var psi = new ProcessStartInfo(program, args) { RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Scratch };
        using Process p = Process.Start(psi)!;
        Task<string> error = p.StandardError.ReadToEndAsync();
        using var output = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(output);
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"{program} {string.Join(' ', args)} failed ({p.ExitCode}): {error.Result}");
        }

        return output.ToArray();
    }

    [Theory]
    [MemberData(nameof(OurOutputCases))]
    public void OurFilesDecodeWithTheZstdToolAndReadWithTheReferenceReader(string corpus, int level, int frameSize)
    {
        RequireEnabled();
        string path = Find(corpus);
        byte[] original = File.ReadAllBytes(path);
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(original));
        foreach (bool checksums in new[] { true, false })
        {
            var options = new ZstdSeekableOptions { FrameSize = frameSize, Checksums = checksums, Compression = new ZstdCompressionOptions { Level = level } };
            string file = Path.Combine(Scratch, $"{corpus}.l{level}.f{frameSize}.c{(checksums ? 1 : 0)}.zst");
            File.WriteAllBytes(file, ZstdSeekable.Compress(original, options));
            try
            {
                // zstd -d reads the skippable seek table frame as nothing.
                byte[] decoded = Run(ZstdTool, "-d", "-q", "-c", file);
                Assert.Equal(expectedHash, Convert.ToHexStringLower(SHA256.HashData(decoded)));

                // The outside seekable reader finds the table and reads ranges (FILE START END, end exclusive).
                var rng = new Random(level + frameSize);
                for (int i = 0; i < 4; i++)
                {
                    int start = rng.Next(0, original.Length - 1);
                    int end = Math.Min(original.Length, start + rng.Next(1, 3 * frameSize));
                    byte[] range = Run(SeekableDecompress, file, start.ToString(System.Globalization.CultureInfo.InvariantCulture), end.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    Assert.True(range.AsSpan().SequenceEqual(original.AsSpan(start, end - start)), $"range {start}..{end} differs");
                }
            }
            finally
            {
                File.Delete(file);
            }
        }
    }

    public static TheoryData<string, int, int> ReferenceProducerCases()
    {
        var data = new TheoryData<string, int, int>();
        foreach (string file in new[] { "dickens", "x-ray", "xml" })
        {
            foreach (int frame in new[] { 4096, 64 << 10, 1 << 20 })
            {
                data.Add(file, frame, 3);
            }
        }

        data.Add("dickens", 64 << 10, 19);
        return data;
    }

    [Theory]
    [MemberData(nameof(ReferenceProducerCases))]
    public void FilesFromTheReferenceProducerOpenAndReadInOurReader(string corpus, int frameSize, int level)
    {
        RequireEnabled();
        byte[] original = File.ReadAllBytes(Find(corpus));
        string input = Path.Combine(Scratch, $"{corpus}.{frameSize}.{level}");
        File.WriteAllBytes(input, original);
        try
        {
            Run(SeekableCompress, input, frameSize.ToString(System.Globalization.CultureInfo.InvariantCulture), level.ToString(System.Globalization.CultureInfo.InvariantCulture));
            byte[] file = File.ReadAllBytes(input + ".zst");
            using var reader = new ZstdSeekableReader(new MemoryStream(file));
            Assert.Equal(original.Length, reader.Length);
            // The producer ends an input that is an exact multiple of the frame size with one more, empty, frame.
            int expectedFrames = (original.Length + frameSize - 1) / frameSize;
            Assert.InRange(reader.Table.Count, expectedFrames, expectedFrames + 1);
            if (reader.Table.Count > expectedFrames)
            {
                Assert.Equal(0, reader.Table.GetFrame(expectedFrames).DecompressedSize);
            }

            reader.Verify(TestContext.Current.CancellationToken);
            Assert.Equal(original, ZstdSeekable.Decompress(file));
            var rng = new Random(frameSize);
            for (int i = 0; i < 200; i++)
            {
                int start = rng.Next(0, original.Length);
                int length = rng.Next(0, 2 * frameSize);
                byte[] got = new byte[length];
                int n = reader.Read(start, got);
                Assert.True(got.AsSpan(0, n).SequenceEqual(original.AsSpan(start, n)));
            }

            // Their table, our table builder: re-encoding the entries gives the same bytes.
            var builder = new ZstdSeekTableBuilder(reader.Table.HasChecksums);
            for (int i = 0; i < reader.Table.Count; i++)
            {
                ZstdSeekFrame f = reader.Table.GetFrame(i);
                builder.Add((uint)f.CompressedSize, (uint)f.DecompressedSize, f.Checksum);
            }

            Assert.Equal(file.AsSpan(file.Length - (int)reader.Table.EncodedLength).ToArray(), builder.ToArray());
        }
        finally
        {
            File.Delete(input);
            File.Delete(input + ".zst");
        }
    }
}
