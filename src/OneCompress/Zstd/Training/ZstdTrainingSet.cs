
namespace RecursiveAI.OneCompress.Zstd;

/// <summary>
/// The training samples laid end to end, with the start of each. Empty samples are dropped. If the samples hold more than the
/// byte limit, every n-th sample is kept (n depends only on the sizes), and what still does not fit is cut off at the limit.
/// </summary>
internal sealed class ZstdTrainingSet
{
    // Reads of up to 8 bytes at any position inside the data stay inside the array.
    private const int Slack = 16;

    private ZstdTrainingSet(byte[] data, int length, int[] starts, int count)
    {
        Data = data;
        Length = length;
        Starts = starts;
        Count = count;
    }

    public byte[] Data { get; }

    public int Length { get; }

    /// <summary>Gets the start of each sample, then the end of the last (<see cref="Count"/> + 1 entries).</summary>
    public int[] Starts { get; }

    public int Count { get; }

    public ReadOnlyMemory<byte> Sample(int index) => Data.AsMemory(Starts[index], Starts[index + 1] - Starts[index]);

    public static ZstdTrainingSet Create(IReadOnlyList<ReadOnlyMemory<byte>> samples, long maxBytes)
    {
        long total = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            total += samples[i].Length;
        }

        long stride = 1;
        if (total > maxBytes)
        {
            stride = (total + maxBytes - 1) / maxBytes;
        }

        long kept = 0;
        int count = 0;
        for (int i = 0; i < samples.Count; i++)
        {
            if (i % stride == 0 && samples[i].Length > 0 && kept < maxBytes)
            {
                kept += Math.Min(samples[i].Length, maxBytes - kept);
                count++;
            }
        }

        byte[] data = new byte[kept + Slack];
        int[] starts = new int[count + 1];
        int n = 0;
        int p = 0;
        for (int i = 0; i < samples.Count && n < count; i++)
        {
            ReadOnlyMemory<byte> s = samples[i];
            if (i % stride != 0 || s.Length == 0)
            {
                continue;
            }

            int take = (int)Math.Min(s.Length, kept - p);
            if (take <= 0)
            {
                break;
            }

            starts[n++] = p;
            s.Span[..take].CopyTo(data.AsSpan(p));
            p += take;
        }

        starts[n] = p;
        return new ZstdTrainingSet(data, p, starts, n);
    }

    /// <summary>Gets a view of samples first (inclusive) to last (exclusive), sharing no storage.</summary>
    public IReadOnlyList<ReadOnlyMemory<byte>> Slice(int first, int last, int maxSamples, long maxBytes)
    {
        int available = Math.Max(last - first, 0);
        long bytes = Starts[Math.Max(last, first)] - Starts[first];
        long stride = 1;
        if (available > maxSamples)
        {
            stride = Math.Max(stride, (available + maxSamples - 1) / maxSamples);
        }

        if (bytes > maxBytes)
        {
            stride = Math.Max(stride, (bytes + maxBytes - 1) / maxBytes);
        }

        var list = new List<ReadOnlyMemory<byte>>();
        for (int i = first; i < last; i++)
        {
            if ((i - first) % stride == 0)
            {
                list.Add(Sample(i));
            }
        }

        return list;
    }
}
