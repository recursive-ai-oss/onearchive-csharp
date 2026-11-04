using System.Runtime.Versioning;
using System.Text;
using RecursiveAI.OneArchive.Zip;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>
/// Raw copy of archives made by the reference tools (tools/zip-raw/make-set.sh) and by our own writer: every entry's raw bytes survive
/// unchanged, and the copies extract with our reader, Python's zipfile and 7-Zip. Runs only with ONEARCHIVE_INTEROP=1 and
/// ONEARCHIVE_RAWCOPY_DIR naming the directory make-set.sh filled; the test adds its own archives there, writes the copies below it
/// (copies/, copies-forward/) and a report.tsv with the counts per tool and method. ONEARCHIVE_ZIPCHECK names tools/zip-raw/check.py, and ONEARCHIVE_PYTHON and ONEARCHIVE_7Z the
/// executables (defaults python3, 7zz).
/// </summary>
[Trait("Category", "Interop")]
[UnsupportedOSPlatform("windows")]
public class ZipRawCopyInteropTests
{
    private const string Password = "pw-123";

    private static string Python => Environment.GetEnvironmentVariable("ONEARCHIVE_PYTHON") ?? "python3";

    private static string SevenZip => Environment.GetEnvironmentVariable("ONEARCHIVE_7Z") ?? "7zz";

    private static void WriteOurs(string dir)
    {
        byte[] text = ZipTests.Text(300_000, 21);
        byte[] noise = new Random(3).GetBytes(30_000);
        void Write(string file, ZipWriterOptions options, bool seekable = true)
        {
            var ms = new MemoryStream();
            using (var w = new ZipWriter(seekable ? ms : new ZipRawCopyTests.NoSeekWrite(ms), options, leaveOpen: true))
            {
                w.WriteEntry(new ZipEntry { Name = "dir/", Kind = ArchiveEntryKind.Directory });
                w.WriteEntry(new ZipEntry { Name = "empty", Kind = ArchiveEntryKind.File }, Stream.Null);
                w.WriteEntry(new ZipEntry { Name = "text.txt", Kind = ArchiveEntryKind.File, Size = text.Length, ModifiedTime = new DateTimeOffset(2025, 1, 2, 3, 4, 6, TimeSpan.Zero) }, new MemoryStream(text));
                w.WriteEntry(new ZipEntry { Name = "dir/noise.bin", Kind = ArchiveEntryKind.File, Size = noise.Length }, new MemoryStream(noise));
                w.WriteEntry(new ZipEntry { Name = "ünï-日本.txt", Kind = ArchiveEntryKind.File, Size = 5 }, new MemoryStream("hello"u8.ToArray()));
                w.WriteEntry(new ZipEntry { Name = "link", Kind = ArchiveEntryKind.Symlink, LinkTarget = "text.txt" });
            }

            File.WriteAllBytes(Path.Combine(dir, file), ms.ToArray());
        }

        foreach (ZipMethod m in new[] { ZipMethod.Store, ZipMethod.Deflate, ZipMethod.Deflate64, ZipMethod.BZip2, ZipMethod.Zstd, ZipMethod.Ppmd })
        {
            Write($"ours-{m.ToString().ToLowerInvariant()}.zip", new ZipWriterOptions { Method = m });
            Write($"ours-{m.ToString().ToLowerInvariant()}-descriptors.zip", new ZipWriterOptions { Method = m }, seekable: false);
        }

        Write("ours-zip64.zip", new ZipWriterOptions { Method = ZipMethod.Deflate, Zip64 = ZipZip64Mode.Always });
        Write("ours-zip64-descriptors.zip", new ZipWriterOptions { Method = ZipMethod.Zstd, Zip64 = ZipZip64Mode.Always, DataDescriptors = ZipDataDescriptorMode.Always });
        Write("ours-aes256.zip", new ZipWriterOptions { Method = ZipMethod.Deflate, Password = Password });
        Write("ours-aes128-ae1.zip", new ZipWriterOptions { Method = ZipMethod.Zstd, Password = Password, AesStrength = ZipAesStrength.Aes128, AesVersion = ZipAesVersion.Ae1 });

        // Document packages: the content-types part first, stored; the rest deflated.
        foreach ((string file, string main) in new[] { ("ours-ooxml-doc.zip", "word/document.xml"), ("ours-ooxml-sheet.zip", "xl/worksheets/sheet1.xml") })
        {
            var ms = new MemoryStream();
            using (var w = new ZipWriter(ms, new ZipWriterOptions { Method = ZipMethod.Deflate }, leaveOpen: true))
            {
                byte[] types = Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>");
                w.WriteEntry(new ZipEntry { Name = "[Content_Types].xml", Kind = ArchiveEntryKind.File, Size = types.Length }, types);
                w.WriteEntry(new ZipEntry { Name = "_rels/.rels", Kind = ArchiveEntryKind.File, Size = 20 }, new byte[20]);
                w.WriteEntry(new ZipEntry { Name = main, Kind = ArchiveEntryKind.File, Size = text.Length }, text);
            }

            File.WriteAllBytes(Path.Combine(dir, file), ms.ToArray());
        }
    }

    [Fact]
    public void ToolAndOurArchivesCopyRaw()
    {
        Assert.SkipUnless(Tools.InteropEnabled, "set ONEARCHIVE_INTEROP=1 to run interop tests");
        string dir = Environment.GetEnvironmentVariable("ONEARCHIVE_RAWCOPY_DIR") ?? throw new InvalidOperationException("set ONEARCHIVE_RAWCOPY_DIR to the directory tools/zip-raw/make-set.sh filled");
        WriteOurs(dir);
        string copies = Path.Combine(dir, "copies"), forward = Path.Combine(dir, "copies-forward");
        Directory.CreateDirectory(copies);
        Directory.CreateDirectory(forward);
        string[] sources = Directory.GetFiles(dir, "*.zip").Order(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(sources);

        var report = new List<string> { "file\ttool\tentries\tmethods\trawIdentical\tzipfile\t7zz" };
        var perTool = new SortedDictionary<string, int>();
        var perMethod = new SortedDictionary<string, int>();
        foreach (string file in sources)
        {
            string name = Path.GetFileName(file);
            string tool = name[..name.IndexOf('-', StringComparison.Ordinal)];
            byte[] src = File.ReadAllBytes(file);

            // Our reader's checks: raw bytes, headers and content survive; the copy extracts with CRC checks (AssertCopy reads every entry).
            ZipRawCopyTests.AssertCopy(src, Password);
            ZipRawCopyTests.AssertCopy(src, Password, seekableOutput: false, lazy: true);
            File.WriteAllBytes(Path.Combine(copies, name), ZipRawCopyTests.Copy(src, new ZipReaderOptions { Password = Password }, null, true));
            File.WriteAllBytes(Path.Combine(forward, name), ZipRawCopyTests.Copy(src, new ZipReaderOptions { Password = Password, LazyDirectory = true }, new ZipWriterOptions { Zip64 = ZipZip64Mode.Always }, false));

            var entries = ZipRawCopyTests.ReadAll(src, new ZipReaderOptions { Password = Password });
            var methods = entries.GroupBy(e => $"{(int)e.Entry.Method}" + (e.Entry.Method == ZipMethod.Aes ? $"/{(int)e.Entry.CompressionMethod}" : string.Empty)).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
            perTool[tool] = perTool.GetValueOrDefault(tool) + 1;
            foreach (var g in methods)
            {
                perMethod[$"{tool}:{g.Key}"] = perMethod.GetValueOrDefault($"{tool}:{g.Key}") + g.Count();
            }

            report.Add($"{name}\t{tool}\t{entries.Count}\t{string.Join(",", methods.Select(g => g.Key + "x" + g.Count()))}\tyes\t\t");
        }

        // Python's zipfile and 7-Zip on both sets of copies.
        string repoScript = Environment.GetEnvironmentVariable("ONEARCHIVE_ZIPCHECK") ?? throw new InvalidOperationException("set ONEARCHIVE_ZIPCHECK to tools/zip-raw/check.py");
        var pyResults = new Dictionary<string, string>();
        var sevenResults = new Dictionary<string, string>();
        foreach (string set in new[] { copies, forward })
        {
            string[] files = Directory.GetFiles(set, "*.zip").Order(StringComparer.Ordinal).ToArray();
            string output = Tools.Run(Python, [repoScript, Password, .. files], failOnError: false);
            foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] cols = line.Split('\t');
                pyResults[$"{Path.GetFileName(set)}/{Path.GetFileName(cols[0])}"] = $"{cols[3]} read={cols[1]} skipped={cols[2]}";
            }

            foreach (string f in files)
            {
                string o = Tools.Run(SevenZip, ["t", "-p" + Password, "-bd", f], failOnError: false);
                sevenResults[$"{Path.GetFileName(set)}/{Path.GetFileName(f)}"] = o.Contains("Everything is Ok", StringComparison.Ordinal) ? "OK" : "FAIL: " + o.Replace('\n', ' ');
            }
        }

        var failures = new List<string>();
        foreach (string set in new[] { "copies", "copies-forward" })
        {
            foreach (string file in sources)
            {
                string key = $"{set}/{Path.GetFileName(file)}";
                string p = pyResults.GetValueOrDefault(key, "missing"), s = sevenResults.GetValueOrDefault(key, "missing");
                report.Add($"{key}\t\t\t\t\t{p}\t{s}");
                if (!p.StartsWith("OK", StringComparison.Ordinal) || s != "OK")
                {
                    failures.Add($"{key}: zipfile {p}; 7zz {s}");
                }
            }
        }

        report.Add(string.Empty);
        report.AddRange(perTool.Select(kv => $"archives\t{kv.Key}\t{kv.Value}"));
        report.AddRange(perMethod.Select(kv => $"entries\t{kv.Key}\t{kv.Value}"));
        File.WriteAllLines(Path.Combine(dir, "report.tsv"), report);
        Assert.Empty(failures);
    }
}
