using System.Text.Json;

namespace Asobu.Core.Minecraft;

/// <summary>
/// Reads Minecraft metadata straight from Mojang. No file ever passes through Asobu infrastructure.
/// </summary>
public sealed class MojangMeta(HttpClient http, AsobuPaths? paths = null)
{
    public const string VersionManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    // Process-lifetime cache. A duplicate fetch under a race is harmless; on-disk caching
    // with SHA1 validation arrives with the download manager.
    private VersionManifest? _manifest;

    public async Task<VersionManifest> GetManifestAsync(CancellationToken cancellationToken = default) =>
        _manifest ??= await GetJsonAsync<VersionManifest>(VersionManifestUrl, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Fetches one vanilla version descriptor, unflattened — or reads the copy already on disk.
    ///
    /// The installer writes every version document it installs, so by the second launch the
    /// answer is sitting in the versions folder. Asking Mojang for it again cost 380 ms of every
    /// single launch, and it also made starting an installed game depend on Mojang being up,
    /// which for something already fully downloaded is a strange thing to require.
    ///
    /// Safe to keep forever because these are immutable in practice: a version's document lives
    /// at a content-addressed URL, so a changed document is a changed address and Mojang has to
    /// publish a new id to change anything. A file that will not parse is ignored rather than
    /// repaired — the network is right there, and one slow launch beats a wrong one.
    /// </summary>
    public async Task<VersionJson> GetVersionAsync(string id, CancellationToken cancellationToken = default)
    {
        if (paths is not null && ReadLocal(paths.VersionJsonFile(id)) is { } local) return local;

        var manifest = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var summary = manifest.Find(id)
            ?? throw new KeyNotFoundException($"Unknown Minecraft version '{id}'.");

        return await GetJsonAsync<VersionJson>(summary.Url, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The document a previous install left behind, or null. Never asks anybody anything, so a
    /// loader's own id — which Mojang has never heard of — is a miss rather than an error.
    /// </summary>
    public VersionJson? TryReadInstalled(string id) =>
        paths is null ? null : ReadLocal(paths.VersionJsonFile(id));

    private static VersionJson? ReadLocal(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;

            // Read whole rather than streamed: these are tens of kilobytes and the point of
            // being here at all is to be quick.
            var document = JsonSerializer.Deserialize<VersionJson>(File.ReadAllBytes(file), MojangJson.Options);

            // A document with no id is a half-written one, which is what a launch killed
            // mid-install leaves behind.
            return document is { Id.Length: > 0 } ? document : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
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
