using RecursiveAI.OneArchive;
using RecursiveAI.OneCompress;

// Calls one public API from each package so the trimmer and the AOT compiler analyse both.
string compress = OneCompressInfo.Name;
string archive = OneArchiveInfo.Name;
if (compress != "OneCompress" || !archive.Contains(compress, StringComparison.Ordinal))
{
    Console.Error.WriteLine("smoke failed");
    return 1;
}

Console.WriteLine("ok: " + archive);
return 0;
