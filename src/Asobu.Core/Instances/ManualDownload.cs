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
                Path.Combine(home, "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            }
            .Where(folder => folder.Length > 0 && Directory.Exists(folder))
            .Distinct(StringComparer.OrdinalIgnoreCase)];
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
                if (FindArrival(waiting[i]) is not { } arrival) continue;
                if (!TryAccept(waiting[i], arrival)) continue;

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

    /// <summary>The finished download, or null while there is nothing to take yet.</summary>
    private string? FindArrival(BlockedDownload item)
    {
        foreach (var folder in _folders)
        {
            foreach (var candidate in CandidateNames(item.FileName))
            {
                var path = Path.Combine(folder, candidate);
                if (File.Exists(path)) return path;
            }
        }

        return null;
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

            if (item.Sha1 is { Length: > 0 } expected && !Matches(path, expected)) return false;

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
