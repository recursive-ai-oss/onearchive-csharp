using System.Buffers;
using RecursiveAI.OneCompress.Deflate;

namespace RecursiveAI.OneCompress.Tests;

// Run alone: the per-thread allocation counter is only exact while no other thread is triggering collections.
[Collection("Lz4 allocation")]
public class DeflateEncoderAllocationTests
{
    [Fact]
    public void ReusedEncoderAllocatesNothingInSteadyState()
    {
        byte[] data = DeflateTestData.Text(14, 400_000);
        var output = new byte[(int)DeflateEncoder.GetMaxCompressedLength(DeflateFormat.GZip, data.Length)];
        foreach (int level in new[] { 0, 1, 3, 6, 9, 12 })
        {
            foreach (DeflateFormat format in new[] { DeflateFormat.GZip, DeflateFormat.ZLib, DeflateFormat.Raw })
            {
                var encoder = new DeflateEncoder(format, new DeflateOptions { Level = level });
                var seen = new List<long>();
                for (int call = 0; call < 12; call++)
                {
                    long before = GC.GetAllocatedBytesForCurrentThread();
                    encoder.Reset();
                    OperationStatus status = encoder.Compress(data, output, out _, out int written, DeflateFlush.Finish);
                    seen.Add(GC.GetAllocatedBytesForCurrentThread() - before);
                    Assert.Equal(OperationStatus.Done, status);
                    Assert.True(written > 0);
                }

                // The first calls grow the window and warm the JIT (tiering can allocate on the calling thread); a real per-call allocation shows in every later call, so the smallest of them is the steady state.
                Assert.True(seen.Skip(3).Min() <= 0, $"level {level} {format}: {string.Join(",", seen)}");
            }
        }
    }
}
