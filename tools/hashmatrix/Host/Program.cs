using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;

namespace RecursiveAI.HashMatrix;

/// <summary>
/// Command line: <c>run</c> executes the registry and writes a manifest, <c>list</c> prints entry names,
/// <c>compare</c> diffs manifests. See docs/hash-matrix.md.
/// </summary>
internal static class Program
{
    private const string BeginMarker = "#BEGIN-MANIFEST";
    private const string EndMarker = "#END-MANIFEST";

    private static int Main(string[] args)
    {
        try
        {
            return args.Length == 0 ? Usage() : args[0] switch
            {
                "run" => Run(args[1..]),
                "list" => List(args[1..]),
                "compare" => Compare(args[1..]),
                _ => Usage(),
            };
        }
        catch (ArgumentException e)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            usage: HashMatrix run [--out FILE|-] [--column NAME] [--threads N] [--filter TEXT] [--include-opt-in]
                   HashMatrix list [--filter TEXT] [--include-opt-in]
                   HashMatrix compare MANIFEST MANIFEST...   (the first is the reference)
            """);
        return 2;
    }

    private sealed record Options(string? Out, string Column, int Threads, string? Filter, bool IncludeOptIn);

    private static Options ParseOptions(string[] args)
    {
        string? output = null;
        string column = "unnamed";
        int threads = 1;
        string? filter = null;
        bool optIn = false;
        for (int i = 0; i < args.Length; i++)
        {
            string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
            switch (args[i])
            {
                case "--out": output = Value(); break;
                case "--column": column = Value(); break;
                case "--threads":
                    threads = int.Parse(Value(), CultureInfo.InvariantCulture);
                    if (threads < 1)
                    {
                        throw new ArgumentException("--threads must be at least 1");
                    }

                    break;
                case "--filter": filter = Value(); break;
                case "--include-opt-in": optIn = true; break;
                default: throw new ArgumentException("unknown option " + args[i]);
            }
        }

        return new Options(output, column, threads, filter, optIn);
    }

    private static IEnumerable<MatrixEntry> Select(Options o) =>
        Matrix.Entries(o.IncludeOptIn).Where(e => o.Filter is null || e.Name.Contains(o.Filter, StringComparison.Ordinal));

    private static int List(string[] args)
    {
        foreach (MatrixEntry e in Select(ParseOptions(args)))
        {
            Console.WriteLine(e.Name);
        }

        return 0;
    }

    private static int Run(string[] args)
    {
        Options o = ParseOptions(args);
        MatrixContext context = new() { Threads = o.Threads };
        StringBuilder text = new();

        // Header: what this column ran on. The comparison ignores it; people read it.
        text.Append("# column: ").Append(o.Column).Append('\n');
        text.Append("# framework: ").Append(RuntimeInformation.FrameworkDescription).Append('\n');
        text.Append("# os: ").Append(RuntimeInformation.OSDescription).Append('\n');
        text.Append("# arch: ").Append(RuntimeInformation.ProcessArchitecture).Append('\n');
        text.Append("# threads: ").Append(o.Threads.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("# vector128/256/512: ")
            .Append(Vector128.IsHardwareAccelerated).Append('/')
            .Append(Vector256.IsHardwareAccelerated).Append('/')
            .Append(Vector512.IsHardwareAccelerated).Append('\n');

        int failures = 0;
        int count = 0;
        StringBuilder rows = new();
        foreach (MatrixEntry entry in Select(o))
        {
            string hash;
            long length = -1;
            try
            {
                byte[] output = entry.Run(Inputs.Get(entry.InputName), context);
                length = output.Length;
                hash = Convert.ToHexStringLower(SHA256.HashData(output));
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A failing entry is a manifest row, so it shows up as a mismatch against the other columns.
                hash = "ERROR:" + e.GetType().Name;
                failures++;
            }

            rows.Append(entry.Name).Append('\t').Append(hash).Append('\t').Append(length.ToString(CultureInfo.InvariantCulture)).Append('\n');
            count++;
        }

        text.Append("# entries: ").Append(count.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append(rows);

        if (o.Out is null or "-")
        {
            // Browser-wasm has no host file system: the manifest goes to stdout between markers.
            Console.Out.Write(BeginMarker + "\n" + text + EndMarker + "\n");
            Console.Out.Flush();
        }
        else
        {
            File.WriteAllText(o.Out, text.ToString());
        }

        if (failures > 0)
        {
            Console.Error.WriteLine($"{failures} entries threw");
        }

        return failures > 0 ? 1 : 0;
    }

    private static Dictionary<string, string> ReadManifest(string path)
    {
        Dictionary<string, string> map = new(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] parts = line.Split('\t');
            if (parts.Length != 3)
            {
                throw new ArgumentException($"{path}: malformed manifest row '{line}'");
            }

            map[parts[0]] = parts[1] + "\t" + parts[2];
        }

        return map;
    }

    private static int Compare(string[] paths)
    {
        if (paths.Length < 2)
        {
            return Usage();
        }

        Dictionary<string, string> reference = ReadManifest(paths[0]);
        if (reference.Count == 0)
        {
            Console.Error.WriteLine($"{paths[0]}: no entries");
            return 2;
        }

        Console.WriteLine($"reference {Path.GetFileName(paths[0])}: {reference.Count} entries");
        int bad = 0;
        foreach (string path in paths.Skip(1))
        {
            Dictionary<string, string> other = ReadManifest(path);
            List<string> names = reference.Keys.Union(other.Keys).OrderBy(n => n, StringComparer.Ordinal).ToList();
            string? first = null;
            int differing = 0;
            foreach (string name in names)
            {
                reference.TryGetValue(name, out string? a);
                other.TryGetValue(name, out string? b);
                if (a != b)
                {
                    differing++;
                    first ??= $"{name}\n    reference: {a?.Replace('\t', ' ') ?? "(missing)"}\n    this:      {b?.Replace('\t', ' ') ?? "(missing)"}";
                }
            }

            string label = Path.GetFileName(path);
            if (differing == 0)
            {
                Console.WriteLine($"{label}: equal ({other.Count} entries)");
            }
            else
            {
                bad++;
                Console.WriteLine($"{label}: MISMATCH, {differing} of {names.Count} entries differ; first: {first}");
            }
        }

        return bad == 0 ? 0 : 1;
    }
}
