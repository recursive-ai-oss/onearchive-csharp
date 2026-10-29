using System.Runtime.CompilerServices;
using RecursiveAI.OneCompress.Ppmd;

namespace RecursiveAI.HashMatrix;

/// <summary>PPMd H and I compression over several orders, model sizes and restore methods: the bytes must not depend on the platform.</summary>
internal static class PpmdEntries
{
    [ModuleInitializer]
    internal static void Register()
    {
        foreach ((int order, int mem) in new[] { (2, 1 << 16), (6, 1 << 24), (16, 1 << 20), (32, 1 << 22), (64, 1 << 20) })
        {
            int o = order;
            int m = mem;
            Matrix.Add("ppmd-h", $"o{o}-m{m >> 10}k", (input, _) => PpmdH.Compress(input, new PpmdHOptions { Order = o, MemorySize = m }));
        }

        Matrix.Add("ppmd-h", "o8-endmarker", (input, _) => PpmdH.Compress(input, new PpmdHOptions { Order = 8, MemorySize = 1 << 22, WriteEndMarker = true }));

        foreach ((int order, int mem) in new[] { (2, 1 << 20), (6, 1 << 24), (16, 1 << 21) })
        {
            foreach (PpmdRestoreMethod restore in new[] { PpmdRestoreMethod.Restart, PpmdRestoreMethod.CutOff })
            {
                int o = order;
                int m = mem;
                PpmdRestoreMethod r = restore;
                Matrix.Add("ppmd-i", $"o{o}-m{m >> 10}k-{r}", (input, _) => PpmdI.Compress(input, new PpmdIOptions { Order = o, MemorySize = m, RestoreMethod = r }));
            }
        }

        // A small model on large inputs forces the restart and cut-off paths.
        Matrix.Add("ppmd-i", "o8-tiny-cutoff", (input, _) => PpmdI.Compress(input, new PpmdIOptions { Order = 8, MemorySize = PpmdIModel.MinMemorySize, RestoreMethod = PpmdRestoreMethod.CutOff }));
        Matrix.Add("ppmd-h", "o8-tiny", (input, _) => PpmdH.Compress(input, new PpmdHOptions { Order = 8, MemorySize = 1 << 12 }));
    }
}
