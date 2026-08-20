using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Minecraft;

/// <summary>One loader build available for a given Minecraft version.</summary>
public sealed record LoaderBuild(string Version, bool Stable);

/// <summary>
/// A Fabric-style metadata service. Both Fabric and Quilt run one, at the same routes and with
/// the same answers, because Quilt is a fork of Fabric and kept the API — so one client serves
/// both and the differences fit in the record below.
///
/// What such a service hands back is a ready-made launcher profile whose inheritsFrom points at
/// the vanilla version — exactly the shape <see cref="VersionResolver"/> already flattens, with
/// libraries carrying bare Maven coordinates plus a repository root, which
/// <see cref="MinecraftInstaller"/> already knows how to fetch. So neither loader needs any new
/// install machinery: each is one more version document.
/// </summary>
/// <param name="Name">For error messages, so a failure says which service did not answer.</param>
/// <param name="Root">Where its loader list and profiles live.</param>
/// <param name="ProfilePrefix">The stem of the profile id it publishes.</param>
/// <param name="MarksStable">
/// Whether the service says outright which builds are stable. Fabric does, on every entry; Quilt
/// does not send the field at all, so stability has to be read off the version string instead.
/// </param>
public sealed record LoaderFlavour(string Name, string Root, string ProfilePrefix, bool MarksStable)
{
    public static readonly LoaderFlavour Fabric = new(
        "Fabric", "https://meta.fabricmc.net/v2/versions/loader/", "fabric-loader", MarksStable: true);

    public static readonly LoaderFlavour Quilt = new(
        "Quilt", "https://meta.quiltmc.org/v3/versions/loader/", "quilt-loader", MarksStable: false);
}

public sealed class FabricStyleMeta(HttpClient http, LoaderFlavour flavour)
{
    public string ProfileId(string loaderVersion, string gameVersion) =>
        $"{flavour.ProfilePrefix}-{loaderVersion}-{gameVersion}";

    /// <summary>
    /// Loader builds for a Minecraft version, newest first. Empty when the loader has none.
    ///
    /// Sorted here rather than trusted: Fabric answers newest-first, but Quilt's list arrives in
    /// no order at all — 0.20.0-beta.9 ahead of 0.24.0 — so taking the first entry would offer a
    /// year-old prerelease as the current build.
    /// </summary>
    public async Task<IReadOnlyList<LoaderBuild>> GetLoadersAsync(
        string gameVersion, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await http
                .GetAsync(flavour.Root + Uri.EscapeDataString(gameVersion), cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode) return [];

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var entries = await JsonSerializer
                .DeserializeAsync<List<LoaderEntry>>(stream, MojangJson.Options, cancellationToken)
                .ConfigureAwait(false);

            if (entries is null) return [];

            return [.. entries
                .Where(e => e.Loader is { Version.Length: > 0 })
                .Select(e => new LoaderBuild(
                    e.Loader!.Version,
                    flavour.MarksStable ? e.Loader.Stable : IsStableVersion(e.Loader.Version)))
                .OrderByDescending(build => build, Newest)];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            // The service being unreachable means "this loader is not on offer", not a broken
            // version picker.
            return [];
        }
    }

    /// <summary>The newest stable build, falling back to the newest of any kind.</summary>
    public async Task<string?> GetLatestLoaderAsync(string gameVersion, CancellationToken cancellationToken = default)
    {
        var loaders = await GetLoadersAsync(gameVersion, cancellationToken).ConfigureAwait(false);

        return loaders.FirstOrDefault(l => l.Stable)?.Version ?? loaders.FirstOrDefault()?.Version;
    }

    public async Task<VersionJson> GetProfileAsync(
        string gameVersion, string loaderVersion, CancellationToken cancellationToken = default)
    {
        var url = $"{flavour.Root}{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json";

        await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);

        return await JsonSerializer
            .DeserializeAsync<VersionJson>(stream, MojangJson.Options, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException($"{flavour.Name} returned no profile for {gameVersion}.");
    }

    /// <summary>
    /// Anything carrying a prerelease tag is not stable. Only consulted for a service that does
    /// not say — Quilt marks nothing stable, and every one of its builds would otherwise look
    /// like a beta, leaving the picker offering a beta to everybody.
    /// </summary>
    private static bool IsStableVersion(string version) =>
        !version.Contains("beta", StringComparison.OrdinalIgnoreCase)
        && !version.Contains("alpha", StringComparison.OrdinalIgnoreCase)
        && !version.Contains("pre", StringComparison.OrdinalIgnoreCase)
        && !version.Contains("rc", StringComparison.OrdinalIgnoreCase);

    private static readonly IComparer<LoaderBuild> Newest = Comparer<LoaderBuild>.Create(Compare);

    /// <summary>
    /// Orders two builds oldest to newest: the numbers left to right, and where those tie, a
    /// release ahead of the prerelease that led to it — 0.24.0 beats 0.24.0-beta.3.
    /// </summary>
    private static int Compare(LoaderBuild? left, LoaderBuild? right)
    {
        var a = Numbers(left?.Version);
        var b = Numbers(right?.Version);

        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var difference = (i < a.Count ? a[i] : 0).CompareTo(i < b.Count ? b[i] : 0);
            if (difference != 0) return difference;
        }

        // Same numbers: whichever is not a prerelease is the later one.
        return IsStableVersion(left?.Version ?? "").CompareTo(IsStableVersion(right?.Version ?? ""));
    }

    /// <summary>
    /// The numeric run at the front — "0.24.0" is 0, 24, 0 — stopping at the first prerelease
    /// tag, so the "9" of "-beta.9" cannot outrank a patch number.
    /// </summary>
    private static List<int> Numbers(string? version)
    {
        var numbers = new List<int>();
        if (version is null) return numbers;

        foreach (var part in version.Split(['.', '-', '+'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!int.TryParse(part, out var value)) break;
            numbers.Add(value);
        }

        return numbers;
    }

    private sealed class LoaderEntry
    {
        [JsonPropertyName("loader")] public LoaderInfo? Loader { get; init; }
    }

    private sealed class LoaderInfo
    {
        [JsonPropertyName("version")] public string Version { get; init; } = "";

        /// <summary>Fabric sends this; Quilt does not, and defaults it to false.</summary>
        [JsonPropertyName("stable")] public bool Stable { get; init; }
    }
}
