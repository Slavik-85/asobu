using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Asobu.Core.Minecraft;

/// <summary>Which loader an instance runs on. The string values are what instance.json stores.</summary>
public static class Loaders
{
    public const string Vanilla = "vanilla";
    public const string Fabric = "fabric";
    public const string Forge = "forge";
    public const string NeoForge = "neoforge";
    public const string Quilt = "quilt";

    /// <summary>
    /// Every name that means "this build needs that mod loader", the two catalogues' spellings
    /// together. Worth having in one place because the interesting use is the negative one:
    /// anything a build declares that is NOT on this list does not constrain it to a loader.
    /// Modrinth files resource packs under "minecraft" and shaders under "iris" and "optifine",
    /// and reading those as loaders hides every one of them from an instance that runs Fabric.
    /// </summary>
    public static bool IsLoaderName(string value) =>
        value.ToLowerInvariant()
            is Fabric or Forge or NeoForge or "quilt" or "rift" or "liteloader" or "modloader";
}

/// <summary>
/// Finds which Forge and NeoForge builds exist for a Minecraft version, and where their installer
/// jars live. Kept apart from <see cref="ForgeInstaller"/>, which does not care where the URL it
/// is handed came from.
/// </summary>
public sealed partial class LoaderCatalog(HttpClient http)
{
    private const string ForgePromotionsUrl = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    private const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge/";

    private const string NeoForgeVersions = "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge";
    private const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge/";

    private Dictionary<string, string>? _forgePromos;
    private HashSet<string>? _forgeArtifacts;
    private IReadOnlyList<string>? _neoForgeVersions;

    /// <summary>
    /// Forge's recommended build for a Minecraft version, falling back to the latest. Recommended
    /// is what Forge itself points people at, and is usually the one mods are built against.
    /// </summary>
    public async Task<string?> GetForgeVersionAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        var promos = await GetForgePromosAsync(cancellationToken).ConfigureAwait(false);

        return promos.GetValueOrDefault($"{gameVersion}-recommended")
               ?? promos.GetValueOrDefault($"{gameVersion}-latest");
    }

    /// <summary>
    /// Where Forge's installer for a build actually is.
    ///
    /// Not composable from the two version numbers, which is what this used to do. Most builds are
    /// published under "1.19.2-43.5.0", but 378 of the five thousand carry a branch on the end as
    /// well — 1.8.9's recommended build lives at "1.8.9-11.15.1.2318-1.8.9", and asking for it
    /// without that tail is a 404 during install, before any log exists to explain it.
    ///
    /// So the published list is asked rather than guessed at, and only fallen back to a guess
    /// where the list cannot be reached — in which case the plain form is right nine times in ten
    /// and the download says so either way.
    /// </summary>
    public async Task<string> ForgeInstallerUrlAsync(
        string gameVersion, string forgeVersion, CancellationToken cancellationToken = default)
    {
        var plain = $"{gameVersion}-{forgeVersion}";
        var published = await GetForgeArtifactsAsync(cancellationToken).ConfigureAwait(false);

        // The exact name first: a build whose branch happens to match another's prefix must not
        // be answered with the other one.
        var artifact = published.Contains(plain)
            ? plain
            : published.FirstOrDefault(v => v.StartsWith(plain + "-", StringComparison.Ordinal)) ?? plain;

        return $"{ForgeMaven}{artifact}/forge-{artifact}-installer.jar";
    }

    /// <summary>Every build name Forge has published, read once.</summary>
    private async Task<HashSet<string>> GetForgeArtifactsAsync(CancellationToken cancellationToken)
    {
        if (_forgeArtifacts is { } remembered) return remembered;

        try
        {
            var xml = await http.GetStringAsync(ForgeMaven + "maven-metadata.xml", cancellationToken)
                .ConfigureAwait(false);

            return _forgeArtifacts = [.. ForgeVersionPattern().Matches(xml).Select(m => m.Groups[1].Value)];
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // Unreachable. The plain form is what nine builds in ten use, and a wrong guess fails
            // as a download rather than as something worse.
            return _forgeArtifacts = [];
        }
    }

    /// <summary>
    /// NeoForge's newest build for a Minecraft version. Its version numbers drop the leading "1."
    /// and track the game: Minecraft 1.21.1 is served by NeoForge 21.1.x. That scheme only starts
    /// at 1.20.2, so anything older simply has no NeoForge.
    /// </summary>
    public async Task<string?> GetNeoForgeVersionAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        if (NeoForgePrefix(gameVersion) is not { } prefix) return null;

        var versions = await GetNeoForgeVersionsAsync(cancellationToken).ConfigureAwait(false);

        // Stable builds only: a -beta suffix is not what someone clicking "NeoForge" is asking for.
        return versions
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal) && !v.Contains('-'))
            .LastOrDefault();
    }

    /// <summary>One <version> entry out of Forge's maven metadata.</summary>
    [GeneratedRegex(@"<version>([^<]+)</version>")]
    private static partial Regex ForgeVersionPattern();

    public static string NeoForgeInstallerUrl(string neoForgeVersion) =>
        $"{NeoForgeMaven}{neoForgeVersion}/neoforge-{neoForgeVersion}-installer.jar";

    /// <summary>"1.21.1" becomes "21.1.", "1.21" becomes "21.0.". Anything else has no mapping.</summary>
    private static string? NeoForgePrefix(string gameVersion)
    {
        var parts = gameVersion.Split('.');
        if (parts.Length is < 2 or > 3 || parts[0] != "1") return null;
        if (!int.TryParse(parts[1], out var minor)) return null;

        var patch = parts.Length == 3 ? parts[2] : "0";
        return int.TryParse(patch, out _) ? $"{minor}.{patch}." : null;
    }

    private async Task<Dictionary<string, string>> GetForgePromosAsync(CancellationToken cancellationToken)
    {
        if (_forgePromos is not null) return _forgePromos;

        try
        {
            await using var stream = await http.GetStreamAsync(ForgePromotionsUrl, cancellationToken).ConfigureAwait(false);
            var document = await JsonSerializer
                .DeserializeAsync<ForgePromotions>(stream, MojangJson.Options, cancellationToken)
                .ConfigureAwait(false);

            return _forgePromos = document?.Promos ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            // Forge being unreachable means "no Forge offered", not a broken version picker.
            return _forgePromos = [];
        }
    }

    private async Task<IReadOnlyList<string>> GetNeoForgeVersionsAsync(CancellationToken cancellationToken)
    {
        if (_neoForgeVersions is not null) return _neoForgeVersions;

        try
        {
            await using var stream = await http.GetStreamAsync(NeoForgeVersions, cancellationToken).ConfigureAwait(false);
            var document = await JsonSerializer
                .DeserializeAsync<NeoForgeIndex>(stream, MojangJson.Options, cancellationToken)
                .ConfigureAwait(false);

            return _neoForgeVersions = document?.Versions ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return _neoForgeVersions = [];
        }
    }

    private sealed class ForgePromotions
    {
        [JsonPropertyName("promos")] public Dictionary<string, string>? Promos { get; init; }
    }

    private sealed class NeoForgeIndex
    {
        [JsonPropertyName("versions")] public List<string>? Versions { get; init; }
    }
}
