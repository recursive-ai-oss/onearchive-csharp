using RecursiveAI.OneCompress;

namespace RecursiveAI.OneArchive.Tests;

/// <summary>Runs every archive of the generated hostile zip set through extraction, seekable and forward-only.</summary>
public class ZipHostileSetTests
{
    private static string Script() => Path.Combine(AppContext.BaseDirectory, "testdata", "make-hostile.py");

    private static string? FindPython()
    {
        foreach (string candidate in s_pythons)
        {
            try
            {
                Tools.Run(candidate, ["--version"]);
                return candidate;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }
        }

        return null;
    }

    private static readonly string[] s_pythons = ["python3", "python"];

    [Fact]
    public void EveryHostileArchiveEndsInATypedErrorOrExtractsInsideTheTarget()
    {
        string? python = FindPython();
        if (python is null)
        {
            Assert.SkipUnless(!Tools.InteropEnabled, "python3 is needed to generate the hostile set");
            return;
        }

        using var temp = new TempDir();
        string set = temp.Combine("set");
        Tools.Run(python, [Script(), set]);
        string[] files = Directory.GetFiles(set, "*.zip");
        Assert.True(files.Length > 50, "the generator wrote " + files.Length + " archives");
        int typed = 0, ok = 0;
        foreach (string file in files)
        {
            foreach (bool forward in new[] { false, true })
            {
                string workDir = temp.Combine("w" + Path.GetFileNameWithoutExtension(file) + forward);
                string target = Path.Combine(workDir, "out");
                Directory.CreateDirectory(workDir);
                try
                {
                    if (forward)
                    {
                        using var s = new ForwardOnly(File.OpenRead(file), 4000);
                        Archive.Extract(s, target);
                    }
                    else
                    {
                        Archive.Extract(file, target);
                    }

                    ok++;
                }
                catch (CompressionException)
                {
                    typed++;
                }

                string[] outside = Directory.GetFileSystemEntries(workDir).Where(p => p != target).ToArray();
                Assert.True(outside.Length == 0, $"{Path.GetFileName(file)} (forward={forward}) wrote outside the target: {string.Join(", ", outside)}");
                Assert.False(File.Exists(Path.Combine(temp.Root, "evil.txt")));
            }
        }

        Assert.True(typed > 60, $"typed={typed} ok={ok}");
        Assert.False(File.Exists("/tmp/onearchive-absolute-evil.txt"));
    }
}
