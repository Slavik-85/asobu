using System.Security.Cryptography;

namespace Asobu.Core.Download;

/// <summary>One file to fetch. <paramref name="Sha1"/> is verified before the file is committed.</summary>
public sealed record DownloadTask(string Url, string Destination, string? Sha1 = null, long Size = 0);

public sealed record DownloadProgress(int Completed, int Total, long BytesCompleted, long BytesTotal, string Current)
{
    public double Fraction => BytesTotal > 0 ? (double)BytesCompleted / BytesTotal
                            : Total > 0 ? (double)Completed / Total
                            : 0;
}

public sealed class Downloader(HttpClient http, int parallelism = 8)
{
    private const int MaxAttempts = 3;

    /// <summary>Downloads everything not already cached. Verified, parallel, restartable.</summary>
    public async Task RunAsync(
        IReadOnlyList<DownloadTask> tasks,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var pending = tasks.Where(NeedsDownload).ToArray();
        var totalBytes = pending.Sum(t => t.Size);
        var completed = 0;
        var completedBytes = 0L;

        progress?.Report(new DownloadProgress(0, pending.Length, 0, totalBytes, ""));
        if (pending.Length == 0) return;

        await Parallel.ForEachAsync(
            pending,
            new ParallelOptions { MaxDegreeOfParallelism = parallelism, CancellationToken = cancellationToken },
            async (task, token) =>
            {
                await DownloadOneAsync(task, token).ConfigureAwait(false);

                var doneCount = Interlocked.Increment(ref completed);
                var doneBytes = Interlocked.Add(ref completedBytes, task.Size);
                progress?.Report(new DownloadProgress(
                    doneCount, pending.Length, doneBytes, totalBytes, Path.GetFileName(task.Destination)));
            }).ConfigureAwait(false);
    }

    private static bool NeedsDownload(DownloadTask task)
    {
        var file = new FileInfo(task.Destination);
        if (!file.Exists) return true;

        // ponytail: cached files are matched on size, not re-hashed. A full SHA1 sweep of the
        // ~4000 asset objects costs seconds on every single launch. Anything Asobu writes was
        // hash-verified at download time. Swap to a full verify behind a "repair instance" action.
        return task.Size > 0 && file.Length != task.Size;
    }

    private async Task DownloadOneAsync(DownloadTask task, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(task.Destination)!);
        var partial = task.Destination + ".part";

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using (var response = await http
                    .GetAsync(task.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();

                    await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    await using var destination = File.Create(partial);
                    await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                }

                if (task.Sha1 is { Length: > 0 } expected)
                {
                    var actual = await Sha1Async(partial, cancellationToken).ConfigureAwait(false);
                    if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(
                            $"Checksum mismatch for {task.Url} (expected {expected}, got {actual}).");
                }

                File.Move(partial, task.Destination, overwrite: true);
                return;
            }
            catch (Exception) when (attempt < MaxAttempts && !cancellationToken.IsCancellationRequested)
            {
                TryDelete(partial);
                await Task.Delay(300 * attempt, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                TryDelete(partial);
                throw;
            }
        }
    }

    public static async Task<string> Sha1Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
