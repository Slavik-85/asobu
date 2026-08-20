using System.Security.Cryptography;
using System.Text;

namespace Asobu.Core.Download;

/// <summary>
/// Keeps pictures fetched from the web around long enough to be worth not fetching twice.
///
/// Every mod card has a logo, every mod page a banner and a gallery, and the same handful come
/// back on every search — but a bitmap cache only lives as long as the process, so closing Asobu
/// meant fetching every one of them again on the next launch.
///
/// This is a cache and behaves like one. It lives in the system temp folder rather than in
/// Asobu's own data, so the OS is free to reclaim it and nobody has to know it exists; it is
/// capped by both age and total size; and losing any of it costs one re-download and nothing
/// else. Nothing here is ever the only copy of anything.
///
/// Deliberately not a general HTTP cache. No revalidation, no headers, no expiry negotiation —
/// this is for content-addressed assets where the URL is the identity and a new picture means a
/// new address. Anything else should not use it.
/// </summary>
public sealed class WebCache(HttpClient http)
{
    /// <summary>
    /// The ceiling on one file. Icons and banners are tens to hundreds of kilobytes; a
    /// multi-megabyte animated gallery entry is better re-fetched than kept.
    /// </summary>
    private const int LargestKept = 4 * 1024 * 1024;

    /// <summary>How much of the disk this may occupy in total before the oldest starts going.</summary>
    private const long TotalBudget = 150L * 1024 * 1024;

    /// <summary>And how stale a picture may get before it is dropped regardless of size.</summary>
    private static readonly TimeSpan KeepFor = TimeSpan.FromDays(14);

    /// <summary>
    /// Under the system temp folder on purpose. Windows Storage Sense and every other cleaner
    /// know to reclaim it, which is exactly the right relationship to have with a pile of mod
    /// logos: useful until something needs the space more.
    /// </summary>
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "Asobu", "images");

    private int _pruned;

    /// <summary>
    /// The bytes at a URL, from disk where they have been seen before.
    ///
    /// A failure to read or write the cache is never a failure to fetch: the download is the
    /// point and the cache is only a shortcut, so every filesystem problem here falls through to
    /// the network rather than surfacing.
    /// </summary>
    public async Task<byte[]> GetAsync(string url, CancellationToken cancellationToken = default)
    {
        var file = PathFor(url);

        try
        {
            if (File.Exists(file)) return await File.ReadAllBytesAsync(file, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }

        var bytes = await http.GetByteArrayAsync(url, cancellationToken).ConfigureAwait(false);

        if (bytes.Length is > 0 and <= LargestKept) _ = Task.Run(() => Write(file, bytes), CancellationToken.None);

        return bytes;
    }

    /// <summary>
    /// Throws away what has gone stale or spilled over the budget. Run once in the background
    /// after startup: doing it on every write would mean listing the whole folder to save one
    /// picture, and doing it never is how a cache turns into a leak.
    /// </summary>
    public void Prune()
    {
        if (Interlocked.Exchange(ref _pruned, 1) == 1) return;

        try
        {
            if (!Directory.Exists(Root)) return;

            var files = new DirectoryInfo(Root)
                .EnumerateFiles("*", SearchOption.AllDirectories)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();

            var cutoff = DateTime.UtcNow - KeepFor;
            var kept = 0L;

            foreach (var file in files)
            {
                kept += file.Length;

                // Newest first, so the moment the running total passes the budget everything
                // from here down is both older and surplus.
                if (file.LastWriteTimeUtc >= cutoff && kept <= TotalBudget) continue;

                try
                {
                    file.Delete();
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // In use, or not ours to delete. It will come up again next time.
                }
            }

            // The two-character fan-out folders left behind by all that.
            foreach (var directory in Directory.EnumerateDirectories(Root))
            {
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static void Write(string file, byte[] bytes)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);

            // Through a temporary name, so a half-written file is never read back as a picture.
            var partial = file + ".part";
            File.WriteAllBytes(partial, bytes);
            File.Move(partial, file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// A file name from the URL. Hashed rather than sanitised: addresses are far longer than a
    /// path may be, and two that differ only in a query string must not collide. Fanned into
    /// sub-folders by the first byte, since a single directory of many thousands of files is
    /// slow to enumerate on Windows.
    /// </summary>
    private static string PathFor(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url)));

        return Path.Combine(Root, hash[..2], hash[2..]);
    }
}
