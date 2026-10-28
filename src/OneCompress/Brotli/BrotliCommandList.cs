namespace RecursiveAI.OneCompress.Brotli;

/// <summary>
/// The commands of one meta-block as parsing produced them (RFC 7932 section 9.3): each has a number of literals to insert, a copy
/// length and a distance. The literals themselves are the input bytes between the copies.
/// </summary>
/// <remarks>
/// A command whose <see cref="Copy"/> is 0 is the last command of a meta-block that ends in literals. A copy that refers to the static
/// dictionary has a distance beyond the window (RFC 7932 section 8); <see cref="Covered"/> then differs from the copy length because the
/// word is transformed.
/// </remarks>
internal sealed class BrotliCommandList
{
    internal int[] Insert = new int[1024];
    internal int[] Copy = new int[1024];
    internal int[] Distance = new int[1024];
    internal int[] Covered = new int[1024];
    internal bool[] IsDictionary = new bool[1024];
    internal int Count;

    internal void Clear() => Count = 0;

    internal void Add(int insert, int copy, int distance, int covered, bool dictionary)
    {
        if (Count == Insert.Length)
        {
            int size = Count * 2;
            Array.Resize(ref Insert, size);
            Array.Resize(ref Copy, size);
            Array.Resize(ref Distance, size);
            Array.Resize(ref Covered, size);
            Array.Resize(ref IsDictionary, size);
        }

        Insert[Count] = insert;
        Copy[Count] = copy;
        Distance[Count] = distance;
        Covered[Count] = covered;
        IsDictionary[Count] = dictionary;
        Count++;
    }
}
