using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asobu.Core.Instances;
using Asobu.Core.Mods;

namespace Asobu.Core.Online;

/// <summary>One file in a shared instance: where it goes, and what it is.</summary>
public sealed record SharedFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha1")] string Sha1,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("fingerprint")] uint Fingerprint);

/// <summary>An instance described well enough to rebuild, without any of its files.</summary>
public sealed record SharedInstance(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("gameVersion")] string GameVersion,
    [property: JsonPropertyName("loader")] string Loader,
    [property: JsonPropertyName("loaderVersion")] string LoaderVersion,
    [property: JsonPropertyName("files")] IReadOnlyList<SharedFile> Files);

/// <summary>A code, and when it stops working.</summary>
public sealed record ShareCode(string Code, DateTimeOffset Expires, bool Reused)
{
    /// <summary>"6 days" or "today", for the sheet that shows the code.</summary>
    public string ExpiryLabel
    {
        get
        {
            var left = Expires - DateTimeOffset.UtcNow;

            return left <= TimeSpan.Zero ? "expired"
                : left.TotalDays >= 1.5 ? $"{(int)Math.Round(left.TotalDays)} days"
                : left.TotalHours >= 1.5 ? $"{(int)Math.Round(left.TotalHours)} hours"
                : "less than an hour";
        }
    }
}

/// <summary>
/// Sharing an instance as a code, and rebuilding one from a code.
///
/// The code stands for a list of files identified by hash. It carries no download addresses at
/// all, which is the whole safety of the thing: importing a code can only ever fetch files that
/// Modrinth or CurseForge already serve under that hash, so the worst a hostile code can name is
/// something that does not exist.
///
/// Only mods, resource packs, shaders and data packs travel. Worlds and configuration stay put:
/// a world is nobody else's to receive, and a config folder is where people keep server
/// addresses and, occasionally, things they would not choose to publish.
/// </summary>
public sealed class ShareClient(HttpClient http, AsobuPaths paths, FriendsClient friends)
{
    private const string Api = "https://api.asobu.cc/v1/";

    /// <summary>The folders a share is allowed to describe. Matches the server's own list.</summary>
    private static readonly string[] Shareable = ["mods", "resourcepacks", "shaderpacks", "datapacks"];

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Reads an instance into something shareable: every file in the four content folders, by
    /// hash. Disabled files are included as they are, since a pack someone shares with a mod
    /// switched off is a pack that arrives with it switched off.
    /// </summary>
    public async Task<SharedInstance> DescribeAsync(
        Instance instance, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        var gameDir = paths.InstanceGameDir(instance.Folder);
        var files = new List<SharedFile>();

        foreach (var folder in Shareable)
        {
            var directory = Path.Combine(gameDir, folder);
            if (!Directory.Exists(directory)) continue;

            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var info = new FileInfo(file);
                if (info.Length == 0) continue;

                progress?.Report($"Reading {info.Name}");

                files.Add(new SharedFile(
                    folder + "/" + info.Name,
                    await Sha1Async(file, cancellationToken).ConfigureAwait(false),
                    info.Length,
                    await CurseForgeFingerprint.OfFileAsync(file, cancellationToken).ConfigureAwait(false)));
            }
        }

        return new SharedInstance(
            instance.Name,
            instance.MinecraftVersion,
            string.IsNullOrWhiteSpace(instance.Loader) ? "vanilla" : instance.Loader.ToLowerInvariant(),
            instance.LoaderVersion ?? "",
            files);
    }

    /// <summary>
    /// Asks for a code. The server decides whether this is new: the same contents always come
    /// back as the same code, however the instance is named and whoever asks.
    /// </summary>
    public async Task<ShareCode> PublishAsync(
        SharedInstance instance, CancellationToken cancellationToken = default)
    {
        var reply = await friends
            .SendAsync<CodeReply>(HttpMethod.Post, "shares", instance, cancellationToken)
            .ConfigureAwait(false);

        return new ShareCode(reply.Code, reply.Expires, reply.Reused);
    }

    /// <summary>Withdraws a code before its week is up.</summary>
    public Task WithdrawAsync(string code, CancellationToken cancellationToken = default) =>
        friends.SendAsync<OkReply>(HttpMethod.Delete, "shares/" + Uri.EscapeDataString(code), null, cancellationToken);

    /// <summary>
    /// Looks a code up. Needs no account: being sent a pack should not require making one.
    /// Returns null when the code is unknown or its week is up, which the server does not tell
    /// apart on purpose.
    /// </summary>
    public async Task<SharedInstance?> FetchAsync(string code, CancellationToken cancellationToken = default)
    {
        using var response = await http
            .GetAsync(Api + "shares/" + Uri.EscapeDataString(code.Trim().ToUpperInvariant()), cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode) throw new FriendsException("Asobu couldn't be reached to look that code up.");

        var reply = await response.Content
            .ReadFromJsonAsync<ManifestReply>(Json, cancellationToken)
            .ConfigureAwait(false);

        return Safe(reply?.Manifest);
    }

    /// <summary>
    /// Everything the launcher will not act on, thrown away before anything downloads.
    ///
    /// The server checks all of this when a code is made, and it is checked again here because
    /// the two are not the same trust: this runs on someone's own machine and is what actually
    /// creates files. A path that escapes an instance is the difference between a shared pack
    /// and a way to write anywhere on a stranger's disk.
    /// </summary>
    private static SharedInstance? Safe(SharedInstance? manifest)
    {
        if (manifest is null) return null;
        if (manifest.Files.Count > 500) return null;

        var files = new List<SharedFile>();

        foreach (var file in manifest.Files)
        {
            var path = file.Path.Replace('\\', '/').Trim();

            if (path.Length is 0 or > 200) continue;
            if (Path.IsPathRooted(path) || path.Contains(':')) continue;

            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length != 2) continue;
            if (parts.Any(p => p is "." or "..")) continue;
            if (!Shareable.Contains(parts[0], StringComparer.OrdinalIgnoreCase)) continue;

            // A name the filesystem would argue with, or one that hides where it really goes.
            if (parts[1].IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) continue;
            if (file.Sha1.Length != 40 || !file.Sha1.All(Uri.IsHexDigit)) continue;
            if (file.Size <= 0 || file.Size > 512L * 1024 * 1024) continue;

            files.Add(file with { Path = parts[0].ToLowerInvariant() + "/" + parts[1] });
        }

        return manifest with { Files = files };
    }

    private static async Task<string> Sha1Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);

        return Convert.ToHexStringLower(hash);
    }

    private sealed record CodeReply(string Code, DateTimeOffset Expires, bool Reused);
    private sealed record ManifestReply(SharedInstance? Manifest, DateTimeOffset Expires);
    private sealed record OkReply(bool Ok);
}
