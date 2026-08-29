using Asobu.Core.Instances;

namespace Asobu.Core.Tests;

/// <summary>
/// Finding a mod the launcher was not allowed to fetch.
///
/// The name is the weakest thing known about it: browsers add "(1)", people rename things, and
/// CurseForge serves a file under a name the pack's manifest never used. The size and hash are
/// exact, so they are what the search falls back on.
/// </summary>
public class ManualDownloadTests : IDisposable
{
    private readonly string _downloads = Directory.CreateTempSubdirectory("asobu-dl-").FullName;
    private readonly string _instance = Directory.CreateTempSubdirectory("asobu-inst-").FullName;

    public void Dispose()
    {
        Directory.Delete(_downloads, recursive: true);
        Directory.Delete(_instance, recursive: true);
    }

    private static readonly byte[] Jar = [.. "pretend this is a mod"u8.ToArray()];

    /// <summary>SHA-1 of <see cref="Jar"/>, so the watcher is given the truth about it.</summary>
    private static string Sha1 =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(Jar));

    private BlockedDownload Wanting(string fileName) =>
        new("Shelf Backport", fileName, Jar.Length, Sha1, "https://example.invalid",
            Path.Combine(_instance, "mods", fileName));

    private string Drop(string name)
    {
        var path = Path.Combine(_downloads, name);
        File.WriteAllBytes(path, Jar);
        return path;
    }

    private async Task<bool> Landed(BlockedDownload item)
    {
        var landed = false;
        using var giveUp = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        await new ManualDownloadWatcher([_downloads])
            .RunAsync([item], _ => landed = true, giveUp.Token);

        return landed;
    }

    [Fact]
    public async Task A_file_already_sitting_in_downloads_is_taken()
    {
        Drop("shelfbackport-1.0.0.jar");

        Assert.True(await Landed(Wanting("shelfbackport-1.0.0.jar")));
        Assert.True(File.Exists(Path.Combine(_instance, "mods", "shelfbackport-1.0.0.jar")));
    }

    /// <summary>The browser's own name for a second copy.</summary>
    [Fact]
    public async Task A_numbered_copy_is_taken()
    {
        Drop("shelfbackport-1.0.0 (2).jar");

        Assert.True(await Landed(Wanting("shelfbackport-1.0.0.jar")));
    }

    /// <summary>
    /// The one that used to be missed. Nothing about the name matches, but the size and hash say
    /// it is the file, and it is sitting right there in Downloads.
    /// </summary>
    [Fact]
    public async Task A_renamed_file_is_found_by_its_contents()
    {
        Drop("ShelfBackport-FABRIC-1.20.1.jar");

        Assert.True(await Landed(Wanting("shelfbackport-1.0.0.jar")));
        Assert.True(File.Exists(Path.Combine(_instance, "mods", "shelfbackport-1.0.0.jar")));
    }

    /// <summary>Same size, different mod. Taking it would install the wrong jar silently.</summary>
    [Fact]
    public async Task Something_else_of_the_same_size_is_left_alone()
    {
        File.WriteAllBytes(Path.Combine(_downloads, "somethingelse.jar"), new byte[Jar.Length]);

        Assert.False(await Landed(Wanting("shelfbackport-1.0.0.jar")));
    }

    /// <summary>The original is theirs to keep — Asobu copies rather than moves.</summary>
    [Fact]
    public async Task The_download_stays_where_they_put_it()
    {
        var dropped = Drop("shelfbackport-1.0.0.jar");

        Assert.True(await Landed(Wanting("shelfbackport-1.0.0.jar")));
        Assert.True(File.Exists(dropped));
    }
}
