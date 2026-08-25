using Asobu.Core.Minecraft;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Mods;

/// <summary>
/// CurseForge's catalogue, which only opens with an API key.
///
/// The key comes from one of two places: compiled into this build (see BuildConfig), or pasted
/// into Settings, which wins. CurseForge issue a key per application to that application's owner,
/// so it is never committed to the repository — which is why this source can report itself
/// unavailable rather than simply working.
///
/// The other constraint worth knowing: a CurseForge author can forbid third-party downloads, and
/// the API then returns a file with no URL. Asobu sends people to the project page in that case.
/// Reconstructing the CDN address from the file id would work and is exactly the thing the flag
/// exists to prevent.
/// </summary>
public sealed class CurseForge(HttpClient http, Func<string?> apiKey) : IModSource
{
    private const string ApiRoot = "https://api.curseforge.com/v1/";

    private const int MinecraftGameId = 432;

    public ModProvider Provider => ModProvider.CurseForge;

    public bool IsAvailable => apiKey() is { Length: > 0 };

    /// <summary>
    /// CurseForge filters by numeric category id, so the browser's names have to be turned back
    /// into ids. Fetched per class and kept, rather than written out here: a table of ids in the
    /// source would start going stale the day it was written.
    /// </summary>
    private readonly Dictionary<int, CategoryList> _categories = [];

    /// <summary>
    /// A class's categories: the names as CurseForge writes them, and the ids to filter by,
    /// looked up by a key that ignores case and punctuation so a table spelling "Armor Tools and
    /// Weapons" finds "Armor, Tools, and Weapons".
    /// </summary>
    private sealed record CategoryList(IReadOnlyList<string> Names, Dictionary<string, int> ByKey);

    /// <summary>
    /// True once CurseForge has turned a key down. Kept apart from "no results" because the two
    /// look identical on screen and mean completely different things: one is a typo in a key, the
    /// other is a search worth rewording.
    /// </summary>
    public bool KeyRejected { get; private set; }

    public async Task<IReadOnlyList<ModListing>> SearchAsync(
        ModQuery query, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];

        // CurseForge has no relevance sort at all — leaving sortField out returns near-arbitrary
        // results — so relevance falls back to popularity and the ranking is fixed up below.
        // CurseForge names its pagination offset "index", which is not the sort index.
        var url = $"{ApiRoot}mods/search?gameId={MinecraftGameId}" +
                  $"&pageSize={query.Limit}&index={query.Offset}" +
                  $"&sortField={SortFieldFor(query.Sort)}&sortOrder=desc" +
                  $"&searchFilter={Uri.EscapeDataString(query.Text)}";

        if (ClassId(query.Kind) is { } classId) url += $"&classId={classId}";

        if (query.GameVersion is { Length: > 0 } version)
            url += $"&gameVersion={Uri.EscapeDataString(version)}";

        if (query.LoaderApplies && LoaderType(query.Loader) is { } loaderType)
            url += $"&modLoaderType={loaderType}";

        if (query.Categories is { Count: > 0 })
        {
            var ids = await CategoryIdsAsync(query, cancellationToken).ConfigureAwait(false);

            // The two catalogues file things differently, and a name CurseForge has never heard
            // of is Modrinth's. Bowing out is the honest answer: answering the search without the
            // filter would quietly mix unfiltered results in among filtered ones.
            if (ids.Count == 0) return [];

            url += $"&categoryIds={Uri.EscapeDataString("[" + string.Join(",", ids) + "]")}";
        }

        var response = await GetAsync<SearchResponse>(url, cancellationToken).ConfigureAwait(false);

        return response?.Data is null
            ? []
            : [.. response.Data
                .Where(mod => FitsLoader(mod, query))
                .OrderByDescending(mod => NameScore(mod.Name, query.Text))
                .Select(Listing)];
    }

    /// <summary>
    /// One project as a listing. Shared with the search rather than written twice, because a page
    /// opened from an installed jar and the same page opened from a search should not be able to
    /// disagree about what the mod is called.
    /// </summary>
    private static ModListing Listing(Mod mod) =>
        new(ModProvider.CurseForge,
            mod.Id.ToString(),
            mod.Name ?? "",
            mod.Authors?.FirstOrDefault()?.Name ?? "Unknown",
            mod.Summary ?? "",
            mod.Logo?.Url,
            mod.DownloadCount,
            mod.Links?.WebsiteUrl ?? "https://www.curseforge.com/minecraft/mc-mods",
            mod.Screenshots?.FirstOrDefault() is { } shot ? shot.Url ?? shot.ThumbnailUrl : null)
        {
            Kind = mod.ClassId is { } classId ? ModContent.KindForClass(classId) : ModKind.Any,
        };

    /// <summary>
    /// The listing for one project id, for a mod already on disk rather than one searched for.
    /// Null when CurseForge has no key here, or does not know the id.
    /// </summary>
    public async Task<ModListing?> GetListingAsync(int modId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}mods");
            request.Headers.Add("x-api-key", apiKey());
            request.Content = JsonContent.Create(new { modIds = new[] { modId } });

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var parsed = await JsonSerializer
                .DeserializeAsync<SearchResponse>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return parsed?.Data?.FirstOrDefault() is { } mod ? Listing(mod) : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    public async Task<ModDetails?> GetDetailsAsync(
        string modId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;

        var id = Uri.EscapeDataString(modId);

        // Two calls: CurseForge keeps the long description behind its own endpoint, and it is
        // HTML rather than part of the mod record.
        var mod = await GetAsync<ModResponse>($"{ApiRoot}mods/{id}", cancellationToken)
            .ConfigureAwait(false);

        var description = await GetAsync<StringResponse>(
            $"{ApiRoot}mods/{id}/description", cancellationToken).ConfigureAwait(false);

        if (mod?.Data is not { } data) return null;

        IReadOnlyList<ModImage> gallery =
        [
            .. (data.Screenshots ?? [])
                .Select(shot => new ModImage(
                    shot.Url ?? shot.ThumbnailUrl ?? "",
                    shot.ThumbnailUrl ?? shot.Url ?? "",
                    shot.Title))
                .Where(image => image.Url.Length > 0),
        ];

        return new ModDetails(Prose.FromHtml(description?.Data ?? data.Summary), gallery);
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string modId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return [];

        var response = await GetAsync<FilesResponse>(
            $"{ApiRoot}mods/{Uri.EscapeDataString(modId)}/files?pageSize=50",
            cancellationToken).ConfigureAwait(false);

        return
        [
            .. (response?.Data ?? [])
                .OrderByDescending(file => file.FileDate)
                .Select(file => new ModVersion(
                    ModProvider.CurseForge,
                    file.DisplayName ?? file.FileName ?? "",
                    file.DisplayName ?? file.FileName ?? "",
                    // CurseForge mixes loader names into the same list as game versions, which
                    // is why they are told apart by name rather than by field.
                    [.. (file.GameVersions ?? []).Where(v => !IsLoaderName(v))],
                    [.. (file.GameVersions ?? []).Where(IsLoaderName)],
                    file.FileDate,
                    file.DownloadCount,
                    file.DownloadUrl,
                    file.FileName ?? "mod.jar",
                    file.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value,
                    file.FileLength,
                    // 1 release, 2 beta, 3 alpha.
                    file.ReleaseType switch
                    {
                        3 => ModChannel.Alpha,
                        2 => ModChannel.Beta,
                        _ => ModChannel.Release,
                    },
                    Required(file.Dependencies))),
        ];
    }

    /// <summary>
    /// The mods a file will not run without. relationType 3 is a required dependency; 1 is
    /// embedded in the jar already, 2 optional, 4 a tool, 5 incompatible, 6 an include.
    /// </summary>
    private static IReadOnlyList<string> Required(List<FileDependency>? dependencies) =>
    [
        .. (dependencies ?? [])
            .Where(dependency => dependency.RelationType == 3)
            .Select(dependency => dependency.ModId.ToString(CultureInfo.InvariantCulture))
            .Distinct(StringComparer.Ordinal),
    ];

    private static bool IsLoaderName(string value) => Loaders.IsLoaderName(value);

    public async Task<ModDownload?> GetDownloadAsync(
        string modId, string gameVersion, string loader,
        ModKind kind = ModKind.Mod, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;

        var url = $"{ApiRoot}mods/{Uri.EscapeDataString(modId)}/files?pageSize=50" +
                  $"&gameVersion={Uri.EscapeDataString(gameVersion)}";

        // Left off for content no loader applies to: CurseForge answers a resource-pack query
        // filtered by Fabric with nothing at all.
        if (ModContent.NeedsLoader(kind) && LoaderType(loader) is { } loaderType)
            url += $"&modLoaderType={loaderType}";

        var response = await GetAsync<FilesResponse>(url, cancellationToken).ConfigureAwait(false);
        if (response?.Data is not { Count: > 0 } files) return null;

        // releaseType 1 is a release; 2 beta, 3 alpha. Prefer stability, but take what exists.
        var chosen = files.FirstOrDefault(f => f.ReleaseType == 1) ?? files[0];

        return new ModDownload(
            chosen.DownloadUrl,
            chosen.FileName ?? "mod.jar",
            // algo 1 is SHA-1; 2 is MD5, which the downloader does not verify against.
            chosen.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value,
            chosen.FileLength,
            Required(chosen.Dependencies));
    }

    /// <summary>
    /// Which mod a file on disk belongs to, by CurseForge's own fingerprint. Their answer to
    /// Modrinth's hash lookup, and the only one they offer — there is no SHA-1 endpoint.
    /// </summary>
    public async Task<string?> GetModIdByFingerprintAsync(
        uint fingerprint, CancellationToken cancellationToken = default)
    {
        var found = await GetModIdsByFingerprintAsync([fingerprint], cancellationToken).ConfigureAwait(false);

        return found.Values.FirstOrDefault();
    }

    /// <summary>
    /// Which mods a set of files belong to, keyed by the fingerprint that was asked about. The
    /// bulk form of the lookup above, so a mods folder costs one request rather than one each.
    /// </summary>
    public async Task<IReadOnlyDictionary<uint, string>> GetModIdsByFingerprintAsync(
        IReadOnlyList<uint> fingerprints, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<uint, string>();
        if (!IsAvailable || fingerprints.Count == 0) return found;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}fingerprints");
            request.Headers.Add("x-api-key", apiKey());
            request.Content = JsonContent.Create(new { fingerprints });

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return found;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var matches = await JsonSerializer
                .DeserializeAsync<FingerprintResponse>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            foreach (var match in matches?.Data?.ExactMatches ?? [])
            {
                if (match.File is not { ModId: > 0, FileFingerprint: > 0 } file) continue;

                found[(uint)file.FileFingerprint.Value] =
                    file.ModId.Value.ToString(CultureInfo.InvariantCulture);
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
        }

        return found;
    }

    /// <summary>
    /// The files a set of fingerprints belong to, with enough to download each one.
    ///
    /// The same request as GetModIdsByFingerprintAsync, kept apart because they want different
    /// halves of the answer: that one identifies which mod a jar is, this one wants the jar
    /// back. A file whose author has opted out of third-party downloads comes back with a null
    /// address rather than being dropped, so the caller can say so instead of silently missing it.
    /// </summary>
    public async Task<IReadOnlyDictionary<uint, PackFile>> GetFilesByFingerprintAsync(
        IReadOnlyList<uint> fingerprints, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<uint, PackFile>();
        if (!IsAvailable || fingerprints.Count == 0) return found;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}fingerprints");
            request.Headers.Add("x-api-key", apiKey());
            request.Content = JsonContent.Create(new { fingerprints });

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return found;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var matches = await JsonSerializer
                .DeserializeAsync<FingerprintResponse>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            foreach (var match in matches?.Data?.ExactMatches ?? [])
            {
                if (match.File is not { FileFingerprint: > 0 } file) continue;

                found[(uint)file.FileFingerprint.Value] = new PackFile(
                    file.Id,
                    file.ModId ?? 0,
                    file.FileName ?? "",
                    file.DownloadUrl,
                    file.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value,
                    file.FileLength);
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
        }

        return found;
    }

    /// <summary>One entry of a modpack manifest, resolved to something downloadable.</summary>
    public sealed record PackFile(int FileId, int ModId, string FileName, string? DownloadUrl, string? Sha1, long Size);

    /// <summary>
    /// The files a modpack manifest lists, all in one request. A manifest names files by id
    /// alone, so everything else about them — name, size, address — has to be asked for.
    /// </summary>
    public async Task<IReadOnlyList<PackFile>> GetFilesByIdAsync(
        IReadOnlyList<int> fileIds, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || fileIds.Count == 0) return [];

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}mods/files");
            request.Headers.Add("x-api-key", apiKey());
            request.Content = JsonContent.Create(new { fileIds });

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return [];

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var parsed = await JsonSerializer
                .DeserializeAsync<FilesResponse>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return [.. (parsed?.Data ?? [])
                .Where(file => file is { Id: > 0, ModId: > 0 })
                .Select(file => new PackFile(
                    file.Id,
                    file.ModId!.Value,
                    file.FileName ?? file.Id.ToString(CultureInfo.InvariantCulture) + ".jar",
                    file.DownloadUrl,
                    file.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value,
                    file.FileLength))];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
            return [];
        }
    }

    /// <summary>
    /// The little a modpack import needs to know about a project: where its files go, and enough
    /// to find the same project somewhere else when CurseForge will not serve it.
    /// </summary>
    public sealed record CurseProject(int Id, string? Slug, string Name, int ClassId);

    /// <summary>
    /// The projects behind a set of ids, keyed by id. A modpack manifest names files and nothing
    /// else, so where each one goes — and what it actually is — has to be asked for.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, CurseProject>> GetProjectsByIdAsync(
        IReadOnlyList<int> modIds, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<int, CurseProject>();
        if (!IsAvailable || modIds.Count == 0) return found;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiRoot}mods");
            request.Headers.Add("x-api-key", apiKey());
            request.Content = JsonContent.Create(new { modIds });

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return found;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var parsed = await JsonSerializer
                .DeserializeAsync<SearchResponse>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            foreach (var mod in parsed?.Data ?? [])
                found[mod.Id] = new CurseProject(mod.Id, mod.Slug, mod.Name ?? "", mod.ClassId ?? 6);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
        }

        return found;
    }

    /// <summary>
    /// Which project a slug names, or null. CurseForge's own links carry the slug and nothing
    /// else identifying, so a pasted link cannot be resolved without asking.
    /// </summary>
    public async Task<int?> GetModIdBySlugAsync(
        string slug, ModKind kind, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || slug.Length == 0) return null;

        var url = $"{ApiRoot}mods/search?gameId={MinecraftGameId}&slug={Uri.EscapeDataString(slug)}";
        if (ClassId(kind) is { } classId) url += $"&classId={classId}";

        var response = await GetAsync<SearchResponse>(url, cancellationToken).ConfigureAwait(false);

        return response?.Data is [{ Id: > 0 } mod, ..] ? mod.Id : null;
    }

    /// <summary>
    /// A project's newest file, preferring a release to a beta the way the mod download already
    /// does. For a link that names no file of its own, this is what it means.
    /// </summary>
    public async Task<PackFile?> GetNewestFileAsync(int modId, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable) return null;

        var response = await GetAsync<FilesResponse>(
            $"{ApiRoot}mods/{modId}/files?pageSize=50", cancellationToken).ConfigureAwait(false);

        if (response?.Data is not { Count: > 0 } files) return null;

        var chosen = files.FirstOrDefault(f => f.ReleaseType == 1) ?? files[0];

        return new PackFile(
            chosen.Id,
            chosen.ModId ?? modId,
            chosen.FileName ?? "pack.zip",
            chosen.DownloadUrl,
            chosen.Hashes?.FirstOrDefault(h => h.Algo == 1)?.Value,
            chosen.FileLength);
    }

    /// <summary>2 is popularity, 3 last updated, 6 total downloads, 11 release date.</summary>
    private static int SortFieldFor(ModSort sort) => sort switch
    {
        ModSort.Downloads => 6,
        ModSort.Updated => 3,
        ModSort.Newest => 11,
        _ => 2,
    };

    /// <summary>
    /// CurseForge files its catalogue by numeric class. Null for <see cref="ModKind.Any"/>, which
    /// means searching across all of them.
    /// </summary>
    private static int? ClassId(ModKind kind) => kind switch
    {
        ModKind.Mod => 6,
        ModKind.Modpack => 4471,
        ModKind.ResourcePack => 12,
        ModKind.Shader => 6552,
        ModKind.DataPack => 6945,
        ModKind.World => 17,
        _ => null,
    };

    /// <summary>
    /// Turns the browser's category names back into the ids CurseForge filters by. Names it does
    /// not know are dropped rather than guessed at — they will be Modrinth's, and Modrinth is
    /// answering the same search alongside.
    /// </summary>
    private async Task<List<int>> CategoryIdsAsync(ModQuery query, CancellationToken cancellationToken)
    {
        if (query.Categories is not { Count: > 0 } wanted) return [];
        if (ClassId(query.Kind) is not { } classId) return [];

        var categories = await LoadCategoriesAsync(classId, cancellationToken).ConfigureAwait(false);

        return [.. wanted
            .SelectMany(label => CategoryMap.CurseForgeNames(query.Kind, label))
            .Select(name => categories.ByKey.TryGetValue(CategoryKey(name), out var id) ? id : 0)
            .Where(id => id != 0)
            .Distinct()];
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(
        ModKind kind, CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || ClassId(kind) is not { } classId) return [];

        var categories = await LoadCategoriesAsync(classId, cancellationToken).ConfigureAwait(false);

        return categories.Names;
    }

    private async Task<CategoryList> LoadCategoriesAsync(int classId, CancellationToken cancellationToken)
    {
        if (_categories.TryGetValue(classId, out var cached)) return cached;

        var response = await GetAsync<CategoryResponse>(
            $"{ApiRoot}categories?gameId={MinecraftGameId}&classId={classId}",
            cancellationToken).ConfigureAwait(false);

        var names = new List<string>();
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var category in response?.Data ?? [])
        {
            if (category.Name is not { Length: > 0 } name) continue;
            if (!byKey.TryAdd(CategoryKey(name), category.Id)) continue;

            names.Add(name);
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);

        var loaded = new CategoryList(names, byKey);

        // Only kept once it arrived: an outage should not cache an empty catalogue for the
        // lifetime of the application.
        if (names.Count > 0) _categories[classId] = loaded;

        return loaded;
    }

    /// <summary>
    /// How often each category turns up among the most popular projects of a kind. CurseForge
    /// publishes no ranking of its categories either, but every search result lists its own, so
    /// one page over the top of the catalogue is enough to count them.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetCategoryWeightsAsync(
        ModKind kind, CancellationToken cancellationToken = default)
    {
        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        if (!IsAvailable || ClassId(kind) is not { } classId) return weights;

        var response = await GetAsync<SearchResponse>(
            $"{ApiRoot}mods/search?gameId={MinecraftGameId}&classId={classId}" +
            $"&pageSize={SampleSize}&sortField=2&sortOrder=desc",
            cancellationToken).ConfigureAwait(false);

        foreach (var mod in response?.Data ?? [])
            foreach (var category in mod.Categories ?? [])
                if (category.Name is { Length: > 0 } name)
                    weights[name] = weights.GetValueOrDefault(name) + 1;

        return weights;
    }

    /// <summary>CurseForge caps a page at 50, and that is a wide enough sample to rank by.</summary>
    private const int SampleSize = 50;

    /// <summary>Letters and digits only, lowercased — the same shape CategoryMap keys on.</summary>
    private static string CategoryKey(string name) =>
        new([.. name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);

    /// <summary>
    /// Pulls name matches above mods that merely mention the word. CurseForge searches summaries
    /// as well as names and then sorts by popularity, so asking for "create" put Xaero's Minimap
    /// first — it is the more popular mod and its description happens to contain the word.
    ///
    /// A stable sort keeps CurseForge's popularity order within each tier, so this only rescues
    /// the exact match rather than reordering everything.
    /// </summary>
    private static int NameScore(string? name, string search)
    {
        if (search.Length == 0 || name is not { Length: > 0 }) return 0;

        if (name.Equals(search, StringComparison.OrdinalIgnoreCase)) return 3;
        if (name.StartsWith(search, StringComparison.OrdinalIgnoreCase)) return 2;
        return name.Contains(search, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>CurseForge identifies loaders by number: 1 Forge, 4 Fabric, 5 Quilt, 6 NeoForge.</summary>
    /// <summary>
    /// Whether one result belongs in an "Everything" search made on an instance's behalf.
    ///
    /// A mixed search cannot carry modLoaderType: CurseForge answers a resource-pack query
    /// filtered by Fabric with nothing at all, so asking for the loader across every class would
    /// empty the list rather than narrow the mods in it. That is why the filter was dropped for
    /// mixed searches — and why Fabric mods turned up while browsing for Forge.
    ///
    /// Done here instead, on what came back. Each result lists the loaders of its own newest files,
    /// so this costs no extra request.
    ///
    /// Only mods are held to it, and only where the result actually says. A mod naming no loader
    /// is not evidence of anything, and being wrong that way shows a row too many rather than
    /// hiding one that would have worked.
    /// </summary>
    private static bool FitsLoader(Mod mod, ModQuery query)
    {
        if (query.Kind != ModKind.Any) return true;      // a single-kind search was filtered already
        if (LoaderType(query.Loader) is not { } wanted) return true;

        var kind = mod.ClassId is { } classId ? ModContent.KindForClass(classId) : ModKind.Any;
        if (kind != ModKind.Mod) return true;

        var named = mod.LatestFilesIndexes?
            .Select(index => index.ModLoader)
            .Where(loader => loader is > 0)
            .Distinct()
            .ToList();

        return named is not { Count: > 0 } || named.Contains(wanted);
    }

    private static int? LoaderType(string? loader) => loader?.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => null,
    };

    private async Task<T?> GetAsync<T>(string url, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("x-api-key", apiKey());

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);

            KeyRejected = response.StatusCode is System.Net.HttpStatusCode.Unauthorized
                                              or System.Net.HttpStatusCode.Forbidden;

            if (!response.IsSuccessStatusCode) return default;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            return await JsonSerializer.DeserializeAsync<T>(stream, Options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
            // A bad key or an outage means an empty catalogue, not a broken page.
            return default;
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private sealed class SearchResponse
    {
        [JsonPropertyName("data")] public List<Mod>? Data { get; init; }
    }

    /// <summary>
    /// One of a project's newest files, of which only the loader is wanted here. CurseForge
    /// returns these on every search result, which is what makes filtering a mixed search free.
    /// </summary>
    private sealed class FileIndex
    {
        [JsonPropertyName("modLoader")] public int? ModLoader { get; init; }
    }

    private sealed class FingerprintResponse
    {
        [JsonPropertyName("data")] public FingerprintData? Data { get; init; }
    }

    private sealed class FingerprintData
    {
        [JsonPropertyName("exactMatches")] public List<FingerprintMatch>? ExactMatches { get; init; }
    }

    private sealed class FingerprintMatch
    {
        [JsonPropertyName("file")] public ModFile? File { get; init; }
    }

    private sealed class CategoryResponse
    {
        [JsonPropertyName("data")] public List<Category>? Data { get; init; }
    }

    private sealed class Category
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed class FilesResponse
    {
        [JsonPropertyName("data")] public List<ModFile>? Data { get; init; }
    }

    private sealed class ModResponse
    {
        [JsonPropertyName("data")] public Mod? Data { get; init; }
    }

    /// <summary>The description endpoint answers with the HTML in a "data" string.</summary>
    private sealed class StringResponse
    {
        [JsonPropertyName("data")] public string? Data { get; init; }
    }

    private sealed class Mod
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("summary")] public string? Summary { get; init; }
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; init; }
        [JsonPropertyName("logo")] public Logo? Logo { get; init; }
        [JsonPropertyName("links")] public Links? Links { get; init; }
        [JsonPropertyName("authors")] public List<Author>? Authors { get; init; }

        /// <summary>The project's own gallery, for showing a mod off at banner size.</summary>
        [JsonPropertyName("screenshots")] public List<Screenshot>? Screenshots { get; init; }

        /// <summary>
        /// The newest file per loader and game version. Read only for the loaders it names, which
        /// is how a mixed search can drop the mods that would not run here without asking again.
        /// </summary>
        [JsonPropertyName("latestFilesIndexes")] public List<FileIndex>? LatestFilesIndexes { get; init; }

        [JsonPropertyName("categories")] public List<Category>? Categories { get; init; }

        /// <summary>6 is a mod, 12 a resource pack, 6552 a shader, 4471 a whole modpack.</summary>
        [JsonPropertyName("classId")] public int? ClassId { get; init; }

        /// <summary>The name in the project's own URL, which both catalogues tend to agree on.</summary>
        [JsonPropertyName("slug")] public string? Slug { get; init; }
    }

    private sealed class Screenshot
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("thumbnailUrl")] public string? ThumbnailUrl { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
    }

    private sealed class Logo
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
    }

    private sealed class Links
    {
        [JsonPropertyName("websiteUrl")] public string? WebsiteUrl { get; init; }
    }

    private sealed class Author
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed class ModFile
    {
        [JsonPropertyName("id")] public int Id { get; init; }
        [JsonPropertyName("fileName")] public string? FileName { get; init; }
        [JsonPropertyName("fileLength")] public long FileLength { get; init; }
        [JsonPropertyName("releaseType")] public int ReleaseType { get; init; }

        /// <summary>Null when the author has opted out of third-party downloads.</summary>
        [JsonPropertyName("downloadUrl")] public string? DownloadUrl { get; init; }

        [JsonPropertyName("hashes")] public List<FileHash>? Hashes { get; init; }
        [JsonPropertyName("dependencies")] public List<FileDependency>? Dependencies { get; init; }
        [JsonPropertyName("modId")] public int? ModId { get; init; }

        /// <summary>Echoed back by the fingerprint lookup, so a reply can be matched to a file.</summary>
        [JsonPropertyName("fileFingerprint")] public long? FileFingerprint { get; init; }
        [JsonPropertyName("displayName")] public string? DisplayName { get; init; }
        [JsonPropertyName("fileDate")] public DateTimeOffset? FileDate { get; init; }
        [JsonPropertyName("downloadCount")] public long DownloadCount { get; init; }

        /// <summary>Holds loader names alongside Minecraft versions, mixed together.</summary>
        [JsonPropertyName("gameVersions")] public List<string>? GameVersions { get; init; }
    }

    private sealed class FileDependency
    {
        [JsonPropertyName("modId")] public int ModId { get; init; }
        [JsonPropertyName("relationType")] public int RelationType { get; init; }
    }

    private sealed class FileHash
    {
        [JsonPropertyName("value")] public string? Value { get; init; }
        [JsonPropertyName("algo")] public int Algo { get; init; }
    }
}
