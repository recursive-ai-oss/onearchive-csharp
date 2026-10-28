using System.Buffers.Binary;
using System.Diagnostics;
using RecursiveAI.OneCompress.Checksums;

namespace RecursiveAI.OneCompress.Tests.Checksums;

/// <summary>
/// Cross-checks against the checksums that reference command-line tools write into their own output. The tools are
/// run as black boxes. A missing tool skips the test, unless ONEARCHIVE_INTEROP=1 asks for interop, in which case it
/// fails loudly.
/// </summary>
[Trait("Category", "Interop")]
public class ToolOracleTests
{
    private static byte[] Run(string tool, string[] args, byte[] input)
    {
        var psi = new ProcessStartInfo(tool)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string a in args)
        {
            psi.ArgumentList.Add(a);
        }

        Process? started;
        try
        {
            started = Process.Start(psi);
        }
        catch (System.ComponentModel.Win32Exception) when (Environment.GetEnvironmentVariable("ONEARCHIVE_INTEROP") != "1")
        {
            Assert.Skip($"{tool} is not installed; set ONEARCHIVE_INTEROP=1 to require it");
            throw;
        }

        using Process p = started ?? throw new InvalidOperationException($"cannot start {tool}");
        var output = new MemoryStream();
        Task copy = p.StandardOutput.BaseStream.CopyToAsync(output);
        Task err = p.StandardError.ReadToEndAsync();
        // Feed stdin from another task so a full output pipe cannot deadlock the tool.
        Task feed = Task.Run(() =>
        {
            p.StandardInput.BaseStream.Write(input);
            p.StandardInput.Close();
        });
        feed.Wait();
        copy.Wait();
        err.Wait();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"{tool} exited with {p.ExitCode}");
        return output.ToArray();
    }

    private static IEnumerable<byte[]> Inputs()
    {
        foreach (int len in new[] { 1, 9, 100, 4097, 300_000 })
        {
            // Mixed content so that the tools take their compressed paths.
            byte[] d = ChecksumTests.RandomBytes(len, len);
            for (int i = 0; i < len; i += 3)
            {
                d[i] = (byte)(i & 0x1F);
            }

            yield return d;
        }
    }

    [Fact]
    public void Gzip_Trailer_Crc32()
    {
        foreach (byte[] d in Inputs())
        {
            byte[] gz = Run("gzip", ["-c", "-n"], d);
            Assert.Equal(Crc32.Compute(d), BinaryPrimitives.ReadUInt32LittleEndian(gz.AsSpan(gz.Length - 8)));
        }
    }

    [Fact]
    public void Xz_Check_Crc32_Crc64()
    {
        foreach (byte[] d in Inputs())
        {
            byte[] x64 = Run("xz", ["-c", "-0", "--check=crc64"], d);
            Assert.Equal(Crc64.Compute(d), BinaryPrimitives.ReadUInt64LittleEndian(CheckField(x64, 8)));
            byte[] x32 = Run("xz", ["-c", "-0", "--check=crc32"], d);
            Assert.Equal(Crc32.Compute(d), BinaryPrimitives.ReadUInt32LittleEndian(CheckField(x32, 4)));
        }
    }

    // xz file format 2.1.2: the footer's backward size gives the index length; the check field sits just before the index.
    private static ReadOnlySpan<byte> CheckField(byte[] xz, int size)
    {
        int indexSize = (int)((BinaryPrimitives.ReadUInt32LittleEndian(xz.AsSpan(xz.Length - 8)) + 1) * 4);
        int indexStart = xz.Length - 12 - indexSize;
        return xz.AsSpan(indexStart - size, size);
    }

    [Fact]
    public void Zstd_FrameChecksum_XxHash64()
    {
        foreach (byte[] d in Inputs())
        {
            byte[] z = Run("zstd", ["-c", "-q", "--check"], d);
            Assert.Equal((uint)XxHash64.Compute(d), BinaryPrimitives.ReadUInt32LittleEndian(z.AsSpan(z.Length - 4)));
        }
    }

    [Fact]
    public void Lz4_ContentChecksum_XxHash32()
    {
        foreach (byte[] d in Inputs())
        {
            byte[] l = Run("lz4", ["-c", "-q"], d);
            Assert.Equal(XxHash32.Compute(d), BinaryPrimitives.ReadUInt32LittleEndian(l.AsSpan(l.Length - 4)));
        }
    }
}
