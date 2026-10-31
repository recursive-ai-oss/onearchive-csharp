using System.Globalization;

namespace RecursiveAI.OneArchive.Rar;

/// <summary>Finds the later volumes of a RAR set on disk from the name of the first one.</summary>
/// <remarks>
/// Two naming schemes exist. New-style sets are <c>name.part1.rar</c>, <c>name.part2.rar</c>, ... (the digits keep the width of the
/// first name, so <c>part01</c> is followed by <c>part02</c>). Old-style sets are <c>name.rar</c>, <c>name.r00</c>, <c>name.r01</c>, ...
/// <c>name.r99</c>, <c>name.s00</c>, ... The scheme is read from the first file's name.
/// </remarks>
public static class RarVolumes
{
    /// <summary>Gets the path of volume <paramref name="volumeNumber"/> of the set whose first volume is <paramref name="firstVolumePath"/>.</summary>
    /// <param name="firstVolumePath">The path of the first volume.</param>
    /// <param name="volumeNumber">The zero-based volume number.</param>
    /// <returns>The path the volume would have, or <see langword="null"/> if the name follows neither scheme or the number is out of range. The file may not exist.</returns>
    public static string? GetVolumePath(string firstVolumePath, int volumeNumber)
    {
        ArgumentNullException.ThrowIfNull(firstVolumePath);
        if (volumeNumber < 0)
        {
            return null;
        }

        if (volumeNumber == 0)
        {
            return firstVolumePath;
        }

        string dir = Path.GetDirectoryName(firstVolumePath) ?? string.Empty;
        string file = Path.GetFileName(firstVolumePath);
        if (TrySplitNewStyle(file, out string stem, out string digits, out string newExt))
        {
            if (!long.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out long first))
            {
                return null;
            }

            string next = (first + volumeNumber).ToString(CultureInfo.InvariantCulture).PadLeft(digits.Length, '0');
            return Path.Combine(dir, stem + next + newExt);
        }

        if (file.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            int index = volumeNumber - 1;
            int letterStep = index / 100;
            if (letterStep > 'z' - 'r')
            {
                return null;
            }

            bool upper = char.IsUpper(file[^1]);
            char letter = (char)('r' + letterStep);
            string ext = (upper ? char.ToUpperInvariant(letter) : letter) + (index % 100).ToString("D2", CultureInfo.InvariantCulture);
            return Path.Combine(dir, file[..^3] + ext);
        }

        return null;
    }

    /// <summary>Creates a provider that opens the later volumes from disk, named after <paramref name="firstVolumePath"/>.</summary>
    /// <param name="firstVolumePath">The path of the first volume.</param>
    /// <returns>A provider that returns <see langword="null"/> for a volume whose file does not exist.</returns>
    public static RarVolumeProvider ForFile(string firstVolumePath)
    {
        ArgumentNullException.ThrowIfNull(firstVolumePath);
        return number =>
        {
            string? path = GetVolumePath(firstVolumePath, number);
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            return new FileStream(path, new FileStreamOptions { Mode = FileMode.Open, Access = FileAccess.Read, Share = FileShare.Read, BufferSize = 64 * 1024, Options = FileOptions.SequentialScan });
        };
    }

    // name.partN.rar, in any letter case: the stem keeps its ".part", the digits keep their width, the extension its case.
    private static bool TrySplitNewStyle(string file, out string stem, out string digits, out string ext)
    {
        stem = digits = ext = string.Empty;
        if (!file.EndsWith(".rar", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        int end = file.Length - 4;
        int start = end;
        while (start > 0 && char.IsAsciiDigit(file[start - 1]))
        {
            start--;
        }

        if (start == end || start < 5 || !file.AsSpan(start - 5, 5).Equals(".part", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        stem = file[..start];
        digits = file[start..end];
        ext = file[end..];
        return true;
    }
}
