namespace RecursiveAI.HashMatrix;

/// <summary>Per-run settings the host passes to every entry.</summary>
internal sealed class MatrixContext
{
    /// <summary>Worker-thread count for entries that compress with several threads. Output must not depend on it.</summary>
    public int Threads { get; init; } = 1;
}

/// <summary>Computes one entry's output from its fixed input.</summary>
internal delegate byte[] EntryFunc(ReadOnlySpan<byte> input, MatrixContext context);

/// <summary>One row of the matrix: a codec, an option set and a fixed input, producing bytes.</summary>
internal sealed record MatrixEntry(string Name, string InputName, EntryFunc Run, bool OptIn);

/// <summary>
/// The registry every column runs. A codec adds its entries from a file of its own under Entries/,
/// using a module initializer, so the runner never needs editing:
/// <code>
/// internal static class FooEntries
/// {
///     [ModuleInitializer]
///     internal static void Register()
///     {
///         for (int level = 1; level &lt;= 9; level++)
///         {
///             int l = level;
///             Matrix.Add("foo", $"level{l}", (input, ctx) =&gt; Foo.Compress(input, l, ctx.Threads));
///         }
///     }
/// }
/// </code>
/// An entry is named <c>codec/options/input</c>. Entries are listed in ordinal name order, not
/// registration order, so the manifest does not depend on which initializer ran first.
/// </summary>
internal static class Matrix
{
    private static readonly List<MatrixEntry> s_entries = [];

    /// <summary>
    /// Registers <paramref name="run"/> for each named input (every standard input when none are given).
    /// </summary>
    public static void Add(string codec, string options, EntryFunc run, params string[] inputs) =>
        Register(codec, options, run, optIn: false, inputs);

    /// <summary>
    /// Registers an entry that runs only when the host is given <c>--include-opt-in</c>. Used for
    /// entries that are meant to differ between columns, to prove the comparison catches a difference.
    /// </summary>
    public static void AddOptIn(string codec, string options, EntryFunc run, params string[] inputs) =>
        Register(codec, options, run, optIn: true, inputs);

    private static void Register(string codec, string options, EntryFunc run, bool optIn, string[] inputs)
    {
        foreach (string input in inputs.Length == 0 ? Inputs.Standard : inputs)
        {
            if (!Inputs.Standard.Contains(input))
            {
                throw new ArgumentException($"unknown matrix input '{input}'", nameof(inputs));
            }

            lock (s_entries)
            {
                s_entries.Add(new MatrixEntry($"{codec}/{options}/{input}", input, run, optIn));
            }
        }
    }

    /// <summary>All registered entries in ordinal name order. Duplicate names are a registration bug.</summary>
    public static IReadOnlyList<MatrixEntry> Entries(bool includeOptIn)
    {
        lock (s_entries)
        {
            List<MatrixEntry> list = s_entries.Where(e => includeOptIn || !e.OptIn).ToList();
            list.Sort((a, b) => string.CompareOrdinal(a.Name, b.Name));
            for (int i = 1; i < list.Count; i++)
            {
                if (list[i].Name == list[i - 1].Name)
                {
                    throw new InvalidOperationException($"matrix entry registered twice: {list[i].Name}");
                }
            }

            return list;
        }
    }
}
