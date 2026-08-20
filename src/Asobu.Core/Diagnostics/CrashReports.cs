using System.Globalization;
using Asobu.Core.Instances;

namespace Asobu.Core.Diagnostics;

public sealed record CrashReportEntry(string Path, string Name, DateTimeOffset Modified, string Kind)
{
    // Invariant on purpose: the UI is English, so a locale month name next to it just looks broken.
    public string ModifiedLabel => Modified.ToString("MMM d, HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>
/// Surfaces what went wrong with a launch. Minecraft only writes a crash-reports/*.txt on some
/// failure modes (a Mixin or native crash often doesn't), so Asobu's own captured stdout/stderr
/// per launch is listed alongside it — that one always exists.
/// </summary>
public static class CrashReports
{
    private const int TailBytes = 300_000;

    public static IReadOnlyList<CrashReportEntry> List(AsobuPaths paths, Instance instance)
    {
        var entries = new List<CrashReportEntry>();

        var crashDir = Path.Combine(paths.InstanceGameDir(instance.Folder), "crash-reports");
        if (Directory.Exists(crashDir))
            foreach (var file in Directory.EnumerateFiles(crashDir, "*.txt"))
                entries.Add(new CrashReportEntry(file, Path.GetFileName(file), File.GetLastWriteTimeUtc(file), "Crash report"));

        if (Directory.Exists(paths.Logs))
            foreach (var file in Directory.EnumerateFiles(paths.Logs, $"{instance.Id}-*.log"))
                entries.Add(new CrashReportEntry(file, Path.GetFileName(file), File.GetLastWriteTimeUtc(file), "Launch log"));

        return [.. entries.OrderByDescending(e => e.Modified)];
    }

    /// <summary>Reads a report's text, tailing very large launch logs instead of loading them whole.</summary>
    public static async Task<string> ReadAsync(string path, CancellationToken cancellationToken = default)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return "(file no longer exists)";

        if (info.Length <= TailBytes)
            return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);

        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Seek(-TailBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        var tail = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return $"(showing the last {TailBytes / 1000} KB of a larger file)\n\n{tail}";
    }
}
