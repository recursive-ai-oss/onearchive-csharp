using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Stand-in entries that exercise the runner before any codec exists: a scalar hash, a run-length
/// encoder, a vector sum with a scalar fallback, and a multithreaded job split. Each must produce the
/// same bytes in every column. Codec units add their own file beside this one.
/// </summary>
internal static class PlaceholderEntries
{
    private const int JobSize = 64 * 1024;

    [ModuleInitializer]
    internal static void Register()
    {
        Matrix.Add("placeholder", "fnv1a64", (input, _) => Fnv(input, 0xCBF29CE484222325UL));
        Matrix.Add("placeholder", "rle", (input, _) => RunLength(input));
        Matrix.Add("placeholder", "vector-sum", (input, _) => VectorSum(input));
        Matrix.Add("placeholder", "jobs", (input, ctx) => Jobs(input, ctx.Threads));
    }

    private static byte[] Fnv(ReadOnlySpan<byte> input, ulong seed)
    {
        ulong h = seed;
        foreach (byte b in input)
        {
            h = (h ^ b) * 0x100000001B3UL;
        }

        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(result, h);
        return result;
    }

    // Output: for each run, the byte then the run length as a varint.
    private static byte[] RunLength(ReadOnlySpan<byte> input)
    {
        List<byte> output = [];
        for (int i = 0; i < input.Length;)
        {
            int j = i + 1;
            while (j < input.Length && input[j] == input[i])
            {
                j++;
            }

            output.Add(input[i]);
            for (uint n = (uint)(j - i); ; n >>= 7)
            {
                output.Add((byte)((n & 0x7F) | (n > 0x7F ? 0x80u : 0)));
                if (n <= 0x7F)
                {
                    break;
                }
            }

            i = j;
        }

        return [.. output];
    }

    // Sum of all bytes. Takes the Vector256 path where it is hardware accelerated and a scalar loop
    // otherwise; both compute the same function, which is the property the columns check.
    private static byte[] VectorSum(ReadOnlySpan<byte> input)
    {
        ulong sum = 0;
        int i = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<uint> acc = Vector256<uint>.Zero;
            for (; i + Vector256<byte>.Count <= input.Length; i += Vector256<byte>.Count)
            {
                (Vector256<ushort> lo, Vector256<ushort> hi) = Vector256.Widen(Vector256.Create(input.Slice(i, Vector256<byte>.Count)));
                (Vector256<uint> a, Vector256<uint> b) = Vector256.Widen(lo);
                (Vector256<uint> c, Vector256<uint> d) = Vector256.Widen(hi);
                acc += a + b + c + d;
            }

            for (int lane = 0; lane < Vector256<uint>.Count; lane++)
            {
                sum += acc.GetElement(lane);
            }
        }

        for (; i < input.Length; i++)
        {
            sum += input[i];
        }

        byte[] result = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(result, sum);
        return result;
    }

    // The input is cut into fixed 64 KiB jobs, whatever the thread count, and the job results are
    // joined in order: the shape every multithreaded codec entry has to follow.
    private static byte[] Jobs(ReadOnlySpan<byte> input, int threads)
    {
        byte[] data = input.ToArray();
        int jobs = (data.Length + JobSize - 1) / JobSize;
        byte[][] results = new byte[jobs][];
        Parallel.For(0, jobs, new ParallelOptions { MaxDegreeOfParallelism = threads }, j =>
        {
            int start = j * JobSize;
            results[j] = Fnv(data.AsSpan(start, Math.Min(JobSize, data.Length - start)), 0xCBF29CE484222325UL + (ulong)j);
        });

        return [.. results.SelectMany(r => r)];
    }
}

/// <summary>
/// Entries that deliberately differ between columns (they read the process architecture and the vector
/// width). They run only with <c>--include-opt-in</c> and exist to prove the comparison reports a mismatch.
/// </summary>
internal static class DivergentEntries
{
    [ModuleInitializer]
    internal static void Register() =>
        Matrix.AddOptIn("divergent", "hardware", (_, _) =>
            System.Text.Encoding.ASCII.GetBytes(
                $"{RuntimeInformation.ProcessArchitecture} vector256={Vector256.IsHardwareAccelerated}"),
            "text-small");
}
