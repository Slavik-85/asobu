namespace Asobu.Core;

public static class DirectorySize
{
    /// <summary>
    /// Recursive byte total. Skips files it can't read rather than failing outright — a running
    /// Minecraft instance can hold locks on its own log or world files while this scans.
    /// </summary>
    public static long Compute(string path)
    {
        if (!Directory.Exists(path)) return 0;

        long total = 0;
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            try { total += new FileInfo(file).Length; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }

        return total;
    }
}
