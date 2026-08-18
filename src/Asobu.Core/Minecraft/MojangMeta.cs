using System.Text.Json;

namespace Asobu.Core.Minecraft;

/// <summary>
/// Reads Minecraft metadata straight from Mojang. No file ever passes through Asobu infrastructure.
/// </summary>
public sealed class MojangMeta(HttpClient http)
{
    public const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    // Process-lifetime cache. A duplicate fetch under a race is harmless; on-disk caching
    // with SHA1 validation arrives with the download manager.
    private VersionManifest? _manifest;

    public async Task<VersionManifest> GetManifestAsync(CancellationToken cancellationToken = default) =>
        _manifest ??= await GetJsonAsync<VersionManifest>(VersionManifestUrl, cancellationToken).ConfigureAwait(false);

    /// <summary>Fetches one vanilla version descriptor, unflattened.</summary>
    public async Task<VersionJson> GetVersionAsync(string id, CancellationToken cancellationToken = default)
    {
        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var summary = manifest.Find(id)
            ?? throw new KeyNotFoundException($"Unknown Minecraft version '{id}'.");

        return await GetJsonAsync<VersionJson>(summary.Url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Fetches a version and flattens any inheritsFrom chain.</summary>
    public Task<VersionJson> GetResolvedVersionAsync(string id, CancellationToken cancellationToken = default) =>
        VersionResolver.ResolveAsync(id, GetVersionAsync, cancellationToken);

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, MojangJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Empty document at {url}.");
    }
}
