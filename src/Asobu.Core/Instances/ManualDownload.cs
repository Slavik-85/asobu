using System.Security.Cryptography;

namespace Asobu.Core.Instances;

/// <summary>
/// A file the launcher is not allowed to fetch, and where it needs to end up.
///
/// CurseForge lets an author say that only their own page may serve a file. Asobu honours that:
/// nobody but the person at the keyboard can start this download, and they do it from the
/// author's own page, which is the arrangement the flag exists to protect. What the launcher can
/// still do is know exactly what it is waiting for and put it where it belongs.
/// </summary>
public sealed record BlockedDownload(
    string ModName,
    string FileName,
    long Size,
    string? Sha1,
    string PageUrl,
    string Destination);

/// <summary>
/// Watches for files the user is downloading by hand and files them into their instance.
///
/// Polled rather than driven by FileSystemWatcher: a browser writes a download under a temporary
/// name and renames it at the end, so the interesting moment is a rename into a name we already
/// know, and a poll that checks for exactly that is both simpler and immune to the event storms
/// a part-written file produces. A second's delay in noticing costs nothing here.
/// </summary>
public sealed class ManualDownloadWatcher(IReadOnlyList<string>? folders = null)
{
    private const int PollMilliseconds = 700;

    private readonly IReadOnlyList<string> _folders = folders is { Count: > 0 } ? folders : DefaultFolders();

    /// <summary>Where a browser puts things unless it was told otherwise.</summary>
    public static IReadOnlyList<string> DefaultFolders()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return [.. new[]
            {
                OperatingSystem.IsWindows() ? WindowsDownloads() : XdgDownloads(home),
                Path.Combine(home, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            }
            .OfType<string>()
            .Where(folder => folder.Length > 0 && Directory.Exists(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// The Linux equivalent: the XDG user-dirs entry, which is where a desktop in another language
    /// puts downloads — ~/Загрузки, ~/Téléchargements — and so where every browser saves them.
    /// The file reads XDG_DOWNLOAD_DIR="$HOME/Загрузки".
    /// </summary>
    private static string? XdgDownloads(string home)
    {
        try
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } set
                ? set
                : Path.Combine(home, ".config");
            var file = Path.Combine(config, "user-dirs.dirs");
            if (!File.Exists(file)) return null;

            return File.ReadLines(file)
                .Select(line => line.Trim())
                .Where(line => line.StartsWith("XDG_DOWNLOAD_DIR=", StringComparison.Ordinal))
                .Select(line => line["XDG_DOWNLOAD_DIR=".Length..].Trim('"').Replace("$HOME", home, StringComparison.Ordinal))
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The Downloads folder Windows actually uses, which every browser defaults to. Anyone who
    /// moved it off C: — Properties, Location — has it somewhere %USERPROFILE%\Downloads isn't,
    /// and a watcher looking only there never saw a single file land.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? WindowsDownloads()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders");

            return key?.GetValue("{374DE290-123F-4565-9164-39C4925E467B}") is string path
                ? Environment.ExpandEnvironmentVariables(path)
                : null;
        }
        catch (Exception e) when (e is System.Security.SecurityException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for each file to appear and files it away, reporting them one at a time. Returns
    /// when everything has landed or the wait is called off; whatever did land stays landed.
    /// </summary>
    public async Task RunAsync(
        IReadOnlyList<BlockedDownload> pending,
        Action<BlockedDownload> onLanded,
        CancellationToken cancellationToken = default)
    {
        var waiting = pending.ToList();

        while (waiting.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            for (var i = waiting.Count - 1; i >= 0; i--)
            {
                if (!TryLand(waiting[i])) continue;

                onLanded(waiting[i]);
                waiting.RemoveAt(i);
            }

            if (waiting.Count == 0) break;

            try
            {
                await Task.Delay(PollMilliseconds, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Files a download the user pointed at themselves, for when it was saved somewhere nothing
    /// is watching. The name is not checked — they said which file this is.
    /// </summary>
    public static bool TryAcceptChosen(BlockedDownload item, string sourcePath) =>
        File.Exists(sourcePath) && Place(item, sourcePath);

    /// <summary>
    /// Files the download if it has arrived under any name it might have. Every candidate is
    /// tried, not just the first that exists: an older file of the same name — or a browser's
    /// empty placeholder — used to be found, refused, and found again on every poll, while the
    /// real download sat beside it as "name (1).jar" until the wait ran out.
    /// </summary>
    private bool TryLand(BlockedDownload item) => _folders.Any(folder =>
        CandidateNames(item.FileName)
            .Select(candidate => Path.Combine(folder, candidate))
            .Where(File.Exists)
            .Any(path => TryAccept(item, path))
        || (FindByContent(item, folder) is { } renamed && TryAccept(item, renamed)));

    /// <summary>
    /// The file under a name nobody predicted: saved by hand, renamed, or served under a name the
    /// pack's manifest never used. The exact size is known, so it is used to pick out the few
    /// files worth hashing rather than hashing the folder.
    /// </summary>
    private static string? FindByContent(BlockedDownload item, string folder)
    {
        if (item.Size <= 0 || item.Sha1 is not { Length: > 0 } expected) return null;

        var extension = Path.GetExtension(item.FileName);

        try
        {
            return Directory
                .EnumerateFiles(folder, extension.Length > 0 ? "*" + extension : "*")
                .FirstOrDefault(path => IsSize(path, item.Size) && Matches(path, expected));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Half-written, or not ours to read. The next poll asks again.
            return null;
        }
    }

    private static bool IsSize(string path, long size)
    {
        try
        {
            return new FileInfo(path).Length == size;
        }
        catch (IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// The names a browser might have given it: the file's own, and the "(1)" forms browsers
    /// use when something of that name is already sitting in the folder.
    /// </summary>
    private static IEnumerable<string> CandidateNames(string fileName)
    {
        yield return fileName;

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);

        for (var n = 1; n <= 5; n++) yield return $"{stem} ({n}){extension}";
    }

    /// <summary>
    /// Takes the file if it is finished and is the one expected. A download still being written
    /// is simply not ready yet, so this says no and the next poll asks again.
    /// </summary>
    private static bool TryAccept(BlockedDownload item, string path)
    {
        // Nothing to check it against, so nothing is taken on trust from a Downloads folder: a
        // page left open could drop a jar of the right name there while Asobu is watching, and it
        // would run with the next launch. Pointing at the file by hand still works.
        if (item.Sha1 is not { Length: > 0 } expected) return false;

        try
        {
            var file = new FileInfo(path);

            // The size CurseForge published is the surest sign a download has finished.
            if (item.Size > 0 && file.Length != item.Size) return false;

            // Without one, settle for the length holding still between two looks.
            if (item.Size == 0)
            {
                var length = file.Length;
                Thread.Sleep(400);
                file.Refresh();
                if (length != file.Length || length == 0) return false;
            }

            if (!Matches(path, expected)) return false;

            return Place(item, path);
        }
        catch (IOException)
        {
            // Still being written to. Not an error — just not yet.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Place(BlockedDownload item, string sourcePath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);

            // Copied, not moved: what is in someone's Downloads folder is theirs to keep.
            File.Copy(sourcePath, item.Destination, overwrite: true);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Matches(string path, string sha1)
    {
        using var stream = File.OpenRead(path);

        return Convert.ToHexString(SHA1.HashData(stream))
            .Equals(sha1, StringComparison.OrdinalIgnoreCase);
    }
}
