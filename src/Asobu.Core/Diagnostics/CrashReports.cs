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
/// per launch is listed alongside it — that one always exists. So does anything the Java runtime
/// left behind when it died below the game entirely, which is the one case where no crash report
/// is written at all.
/// </summary>
public static class CrashReports
{
    private const int TailBytes = 300_000;

    public static IReadOnlyList<CrashReportEntry> List(AsobuPaths paths, Instance instance)
    {
        var entries = new List<CrashReportEntry>();

        var gameDir = paths.InstanceGameDir(instance.Folder);

        var crashDir = Path.Combine(gameDir, "crash-reports");
        if (Directory.Exists(crashDir))
            foreach (var file in Directory.EnumerateFiles(crashDir, "*.txt"))
                entries.Add(new CrashReportEntry(file, Path.GetFileName(file), File.GetLastWriteTimeUtc(file), "Crash report"));

        // Java's own fatal-error files, which it writes into the game's folder rather than into
        // crash-reports — by the time one exists the game is no longer running to write anything.
        // Worth listing beside the rest precisely because there is no crash report when this
        // happens: the runtime died below the level the game could report from, and this file is
        // the only account of it there will ever be.
        if (Directory.Exists(gameDir))
            foreach (var file in Directory.EnumerateFiles(gameDir, "hs_err_pid*.log"))
                entries.Add(new CrashReportEntry(file, Path.GetFileName(file), File.GetLastWriteTimeUtc(file), "Java error"));

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

        // A Java error file is read from the front instead. Everything that says what happened —
        // the signal, the frame it died in, the thread that died — is on its first page, and the
        // pages after it are the process's whole memory map. Tailing one of those shows the map
        // and hides the crash.
        if (Path.GetFileName(path).StartsWith("hs_err_pid", StringComparison.OrdinalIgnoreCase))
        {
            using var opening = new StreamReader(stream);
            var buffer = new char[TailBytes];
            var read = await opening.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);

            return $"(showing the first {TailBytes / 1000} KB of a larger file)" + "\n\n"
                   + new string(buffer, 0, read);
        }

        stream.Seek(-TailBytes, SeekOrigin.End);
        using var reader = new StreamReader(stream);
        var tail = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return $"(showing the last {TailBytes / 1000} KB of a larger file)\n\n{tail}";
    }
}
