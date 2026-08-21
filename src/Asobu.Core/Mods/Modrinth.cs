using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Asobu.Core.Mods;

/// <summary>A single downloadable jar, already matched to a Minecraft version and loader.</summary>
public sealed record ModrinthFile(
    string Url,
    string FileName,
    string? Sha1,
    long Size,
    IReadOnlyList<string>? Requires = null)
{
    public IReadOnlyList<string> Requires { get; init; } = Requires ?? [];
}

/// <summary>
/// Just enough of Modrinth to fetch one known mod. Deliberately not a browser: this exists so the
/// new-instance page can offer a performance mod, and it should stay this small until there is a
/// real content browser to build properly.
/// </summary>
public sealed class Modrinth(HttpClient http) : IModSource
{
    public ModProvider Provider => ModProvider.Modrinth;

    /// <summary>Modrinth needs no key, so it is always there.</summary>
    public bool IsAvailable => true;

    /// <summary>Category names per project type, fetched once and kept.</summary>
    private readonly Dictionary<ModKind, IReadOnlyList<string>> _categories = [];

    private const string ApiRoot = "https://api.modrinth.com/v2/";

    public const string Sodium = "sodium";
    public const string Embeddium = "embeddium";

    /// <summary>
    /// Which performance mod suits a loader. Sodium never supported plain Forge, which is the
    /// entire reason Embeddium exists — Embeddium is a port of Sodium's work, not an improvement
    /// on it, so Sodium is the right answer wherever it runs.
    /// </summary>
    /// <summary>
    /// Forge gets Embeddium, the fork built for it. Fabric and Quilt both get Sodium — Quilt
    /// loads Fabric mods, and Sodium publishes for it directly.
    /// </summary>
    public static string PerformanceModFor(string loader) =>
        loader.Equals("forge", StringComparison.OrdinalIgnoreCase) ? Embeddium : Sodium;

    /// <summary>
    /// Searches the mod catalogue. Facets are Modrinth's filter language: each inner array is
    /// ORed and the outer ones ANDed, so this asks for mods that are for this loader and this
    /// Minecraft version.
    /// </summary>
    public async Task<IReadOnlyList<ModListing>> SearchAsync(
        ModQuery query, CancellationToken cancellationToken = default)
    {
        // Modrinth has no world project type at all, so there is nothing here to ask.
        if (query.Kind == ModKind.World) return [];

        var facets = new List<string>();

        if (ProjectType(query.Kind) is { } projectType)
            facets.Add($"[\"project_type:{projectType}\"]");

        if (query.LoaderApplies &&
            query.Loader is { Length: > 0 } loader &&
            !loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
            facets.Add($"[\"categories:{loader.ToLowerInvariant()}\"]");

        if (query.GameVersion is { Length: > 0 } version)
            facets.Add($"[\"versions:{version}\"]");

        // Each label in its own array, which is how Modrinth reads an AND: picking two
        // categories narrows to what is in both, the way the checkboxes look like they should
        // behave. Within an array it is an OR, which is where a label that Modrinth splits
        // across several of its own slugs goes.
        foreach (var label in query.Categories ?? [])
        {
            var slugs = CategoryMap.ModrinthNames(query.Kind, label)
                .Select(slug => $"\"categories:{slug.ToLowerInvariant()}\"")
                .ToList();

            // Nothing on Modrinth answers to this one — it is a category only CurseForge files
            // under, and answering the search without the filter would be worse than not
            // answering it at all.
            if (slugs.Count == 0) return [];

            facets.Add("[" + string.Join(",", slugs) + "]");
        }

        var url = $"{ApiRoot}search?limit={query.Limit}&offset={query.Offset}&index={IndexFor(query.Sort)}" +
                  $"&query={Uri.EscapeDataString(query.Text)}";

        if (facets.Count > 0)
            url += $"&facets={Uri.EscapeDataString("[" + string.Join(",", facets) + "]")}";

        try
        {
            await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);

            var results = await JsonSerializer
                .DeserializeAsync<SearchResults>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return results?.Hits is null
                ? []
                : [.. results.Hits.Select(hit => new ModListing(
                    ModProvider.Modrinth,
                    hit.ProjectId ?? hit.Slug ?? "",
                    hit.Title ?? hit.Slug ?? "",
                    hit.Author ?? "Unknown",
                    hit.Description ?? "",
                    hit.IconUrl,
                    hit.Downloads,
                    $"https://modrinth.com/mod/{hit.Slug}",
                    hit.FeaturedGallery ?? hit.Gallery?.FirstOrDefault())
                {
                    Kind = ModContent.KindForProjectType(hit.ProjectType),
                })];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return [];
        }
    }

    /// <summary>Modrinth calls this the search index. "follows" is its notion of popularity.</summary>
    private static string IndexFor(ModSort sort) => sort switch
    {
        ModSort.Popular => "follows",
        ModSort.Downloads => "downloads",
        ModSort.Updated => "updated",
        ModSort.Newest => "newest",
        _ => "relevance",
    };

    /// <summary>
    /// How often each category turns up among the most-downloaded projects of a kind — which is
    /// as close to "popular category" as either provider will answer. Modrinth publishes no such
    /// ranking, but every search hit lists its own categories, so one request over the top of the
    /// catalogue is enough to count them.
    ///
    /// The same field also carries loader tags, so only names on the real category list are
    /// counted; otherwise fabric and forge would lead every list.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetCategoryWeightsAsync(
        ModKind kind, CancellationToken cancellationToken = default)
    {
        var known = (await GetCategoriesAsync(kind, cancellationToken).ConfigureAwait(false))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (known.Count == 0) return new Dictionary<string, int>();

        var facets = ProjectType(kind) is { } projectType
            ? "&facets=" + Uri.EscapeDataString($"[[{Quote}project_type:{projectType}{Quote}]]")
            : "";

        var weights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        try
        {
            await using var stream = await http
                .GetStreamAsync($"{ApiRoot}search?limit={SampleSize}&index=downloads&query=" + facets,
                    cancellationToken)
                .ConfigureAwait(false);

            var results = await JsonSerializer
                .DeserializeAsync<SearchResults>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            foreach (var hit in results?.Hits ?? [])
                foreach (var category in hit.Categories ?? [])
                    if (known.Contains(category))
                        weights[category] = weights.GetValueOrDefault(category) + 1;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
        }

        return weights;
    }

    /// <summary>Modrinth's ceiling for one page, and a wide enough sample to rank by.</summary>
    private const int SampleSize = 100;

    /// <summary>Facets are JSON inside a query string; this keeps the escaping readable.</summary>
    private const string Quote = "\"";

    /// <summary>One of Modrinth's version records as the rest of the launcher understands it.</summary>
    private static ModVersion? ToVersion(ProjectVersion version)
    {
        var file = version.Files.FirstOrDefault(f => f.Primary) ?? version.Files.FirstOrDefault();

        return new ModVersion(
            ModProvider.Modrinth,
            version.Name ?? version.VersionNumber ?? "",
            version.VersionNumber ?? "",
            version.GameVersions ?? [],
            version.Loaders ?? [],
            version.Published,
            version.Downloads,
            file?.Url,
            file?.FileName ?? "mod.jar",
            file?.Hashes?.GetValueOrDefault("sha1"),
            file?.Size ?? 0,
            version.VersionType switch
            {
                "alpha" => ModChannel.Alpha,
                "beta" => ModChannel.Beta,
                _ => ModChannel.Release,
            },
            Required(version.Dependencies));
    }

    /// <summary>
    /// The projects a build will not run without. Optional and incompatible ones are left out on
    /// purpose: the first is a suggestion, and the second is the opposite of something to fetch.
    /// Embedded ones are already inside the jar.
    /// </summary>
    private static IReadOnlyList<string> Required(List<VersionDependency>? dependencies) =>
    [
        .. (dependencies ?? [])
            .Where(dependency => dependency.DependencyType == "required")
            .Select(dependency => dependency.ProjectId)
            .Where(id => id is { Length: > 0 })
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal),
    ];

    /// <summary>Null for <see cref="ModKind.Any"/>, which means asking for no type at all.</summary>
    private static string? ProjectType(ModKind kind) => kind switch
    {
        ModKind.Mod => "mod",
        ModKind.Modpack => "modpack",
        ModKind.ResourcePack => "resourcepack",
        ModKind.Shader => "shader",
        ModKind.DataPack => "datapack",
        _ => null,
    };

    /// <summary>
    /// Modrinth publishes its category list, tagged by which project type each belongs to, so
    /// the browser can offer exactly the ones that mean something for what is being looked at.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetCategoriesAsync(
        ModKind kind, CancellationToken cancellationToken = default)
    {
        if (kind == ModKind.World) return [];
        if (_categories.TryGetValue(kind, out var cached)) return cached;

        try
        {
            await using var stream = await http
                .GetStreamAsync(ApiRoot + "tag/category", cancellationToken)
                .ConfigureAwait(false);

            var tags = await JsonSerializer
                .DeserializeAsync<List<CategoryTag>>(stream, Options, cancellationToken)
                .ConfigureAwait(false) ?? [];

            var wanted = ProjectType(kind);

            IReadOnlyList<string> names =
            [
                .. tags
                    .Where(tag => wanted is null || tag.ProjectType == wanted)
                    .Select(tag => tag.Name)
                    .Where(name => name is { Length: > 0 })
                    .Select(name => name!)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(name => name, StringComparer.OrdinalIgnoreCase),
            ];

            _categories[kind] = names;

            return names;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Looks up specific projects by slug, for a hand-picked shelf rather than a search. One
    /// request covers the whole set, so a curated row costs no more than a search does.
    /// </summary>
    public async Task<IReadOnlyList<ModListing>> GetProjectsAsync(
        IReadOnlyList<string> slugs, CancellationToken cancellationToken = default)
    {
        if (slugs.Count == 0) return [];

        var ids = string.Join(",", slugs.Select(slug => $"\"{slug}\""));
        var url = $"{ApiRoot}projects?ids={Uri.EscapeDataString("[" + ids + "]")}";

        try
        {
            await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);

            var projects = await JsonSerializer
                .DeserializeAsync<List<CuratedProject>>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            if (projects is null) return [];

            var bySlug = projects
                .Where(p => p.Slug is { Length: > 0 })
                .ToDictionary(p => p.Slug!, StringComparer.OrdinalIgnoreCase);

            // Kept in the order they were asked for: a curated shelf has an intended running order.
            return [.. slugs
                .Select(bySlug.GetValueOrDefault)
                .Where(p => p is not null)
                .Select(p => new ModListing(
                    ModProvider.Modrinth,
                    p!.Id ?? p.Slug ?? "",
                    p.Title ?? p.Slug ?? "",
                    "",
                    p.Description ?? "",
                    p.IconUrl,
                    p.Downloads,
                    $"https://modrinth.com/mod/{p.Slug}",
                    Featured(p.Gallery))
                {
                    Kind = ModContent.KindForProjectType(p.ProjectType),
                })];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The picture a project would lead with. Takes the raw file rather than the thumbnail the
    /// list endpoints hand out: this one is going behind a banner, and a 350px wide source
    /// stretched across it looks exactly like a 350px wide source stretched across it.
    /// </summary>
    private static string? Featured(List<GalleryImage>? gallery)
    {
        if (gallery is not { Count: > 0 }) return null;

        var chosen = gallery.FirstOrDefault(image => image.Featured) ?? gallery[0];

        return chosen.RawUrl ?? chosen.Url;
    }

    /// <summary>
    /// A project's slug and title, looked up by either. Hash identification hands back an opaque
    /// id, and the readable slug is what a table of known ports can be keyed by — nobody is
    /// writing "AANobbMI" in a lookup table and knowing they meant Sodium.
    /// </summary>
    public async Task<(string Slug, string Title)?> GetIdentityAsync(
        string idOrSlug, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await http
                .GetStreamAsync(ApiRoot + "project/" + Uri.EscapeDataString(idOrSlug), cancellationToken)
                .ConfigureAwait(false);

            var project = await JsonSerializer
                .DeserializeAsync<CuratedProject>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return project?.Slug is { Length: > 0 } slug
                ? (slug, project.Title is { Length: > 0 } title ? title : slug)
                : null;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    public async Task<ModDetails?> GetDetailsAsync(
        string modId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await http
                .GetStreamAsync(ApiRoot + "project/" + Uri.EscapeDataString(modId), cancellationToken)
                .ConfigureAwait(false);

            var project = await JsonSerializer
                .DeserializeAsync<Project>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            if (project is null) return null;

            // Featured first, then the author's own ordering: a gallery has a running order and
            // the first picture is the one they chose to lead with.
            IReadOnlyList<ModImage> gallery =
            [
                .. (project.Gallery ?? [])
                    .OrderByDescending(image => image.Featured)
                    .ThenBy(image => image.Ordering)
                    .Select(image => new ModImage(
                        image.RawUrl ?? image.Url ?? "",
                        image.Url ?? image.RawUrl ?? "",
                        image.Title))
                    .Where(image => image.Url.Length > 0),
            ];

            return new ModDetails(Prose.FromMarkdown(project.Body), gallery);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    public async Task<IReadOnlyList<ModVersion>> GetVersionsAsync(
        string modId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await http
                .GetStreamAsync(ApiRoot + "project/" + Uri.EscapeDataString(modId) + "/version",
                    cancellationToken)
                .ConfigureAwait(false);

            var versions = await JsonSerializer
                .DeserializeAsync<List<ProjectVersion>>(stream, Options, cancellationToken)
                .ConfigureAwait(false) ?? [];

            return
            [
                .. versions
                    .OrderByDescending(version => version.Published)
                    .Select(ToVersion)
                    .Where(version => version is not null)
                    .Select(version => version!),
            ];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return [];
        }
    }

    public async Task<ModDownload?> GetDownloadAsync(
        string modId, string gameVersion, string loader,
        ModKind kind = ModKind.Mod, CancellationToken cancellationToken = default)
    {
        var file = await GetLatestAsync(
            modId, gameVersion, ModContent.NeedsLoader(kind) ? loader : null, cancellationToken)
            .ConfigureAwait(false);

        return file is null
            ? null
            : new ModDownload(file.Url, file.FileName, file.Sha1, file.Size, file.Requires);
    }

    /// <summary>
    /// The files a set of SHA-1s belong to, in one request, with an address for each.
    ///
    /// Modrinth's bulk form of the lookup below. A shared instance is a list of hashes, and
    /// asking about a hundred jars one at a time would be a hundred round trips before the
    /// first byte of anything downloads.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModDownload>> GetDownloadsByHashAsync(
        IReadOnlyList<string> sha1s, CancellationToken cancellationToken = default)
    {
        var found = new Dictionary<string, ModDownload>(StringComparer.OrdinalIgnoreCase);
        if (sha1s.Count == 0) return found;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoot + "version_files");
            request.Content = JsonContent.Create(new { hashes = sha1s, algorithm = "sha1" });

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return found;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            // Keyed by the hash that was asked about, so the answer maps back to the file.
            var versions = await JsonSerializer
                .DeserializeAsync<Dictionary<string, ProjectVersion>>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            foreach (var (hash, version) in versions ?? [])
            {
                // The file that actually matches, not merely the first one this version ships.
                var file = version.Files.FirstOrDefault(f =>
                    string.Equals(f.Hashes?.GetValueOrDefault("sha1"), hash, StringComparison.OrdinalIgnoreCase));

                if (file is not { Url.Length: > 0 }) continue;

                found[hash] = new ModDownload(
                    file.Url, file.FileName, file.Hashes?.GetValueOrDefault("sha1"), file.Size, []);
            }
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
        }

        return found;
    }

    /// <summary>
    /// Which project a file on disk belongs to, by its SHA-1. The only reliable way to identify
    /// an installed jar: a mod's own id is the loader's, not the catalogue's, and the two agree
    /// only by luck.
    /// </summary>
    public async Task<string?> GetProjectIdByHashAsync(
        string sha1, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await http
                .GetStreamAsync($"{ApiRoot}version_file/{Uri.EscapeDataString(sha1)}?algorithm=sha1",
                    cancellationToken)
                .ConfigureAwait(false);

            var version = await JsonSerializer
                .DeserializeAsync<HashedVersion>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return version?.ProjectId;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// The newest build of every installed file at once, keyed by the SHA-1 that was asked about.
    /// One request for a whole mods folder — asking per mod would be forty round trips to answer
    /// a question nobody explicitly asked.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, ModVersion>> GetUpdatesAsync(
        IReadOnlyList<string> hashes,
        string gameVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        var updates = new Dictionary<string, ModVersion>(StringComparer.OrdinalIgnoreCase);
        if (hashes.Count == 0) return updates;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiRoot + "version_files/update")
            {
                Content = JsonContent.Create(new
                {
                    hashes,
                    algorithm = "sha1",
                    loaders = new[] { loader.ToLowerInvariant() },
                    game_versions = new[] { gameVersion },
                }),
            };

            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return updates;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var found = await JsonSerializer
                .DeserializeAsync<Dictionary<string, ProjectVersion>>(stream, Options, cancellationToken)
                .ConfigureAwait(false) ?? [];

            foreach (var (hash, version) in found)
                if (ToVersion(version) is { } converted)
                    updates[hash] = converted;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or InvalidOperationException)
        {
        }

        return updates;
    }

    /// <summary>Every Minecraft version a project publishes for, so the UI can offer it honestly.</summary>
    public async Task<IReadOnlyList<string>> GetGameVersionsAsync(
        string projectId, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var stream = await http
                .GetStreamAsync(ApiRoot + "project/" + projectId, cancellationToken)
                .ConfigureAwait(false);

            var project = await JsonSerializer
                .DeserializeAsync<Project>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            return project?.GameVersions ?? [];
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The newest release build for a version and loader. A prerelease is only offered when
    /// nothing stable exists, so a brand-new Minecraft version isn't left with no option at all.
    /// </summary>
    /// <summary>
    /// A null <paramref name="loader"/> means the content is not built against one — a resource
    /// pack, a shader — and the filter is left off entirely rather than sent as a loader name
    /// that would match none of its builds.
    /// </summary>
    public async Task<ModrinthFile?> GetLatestAsync(
        string projectId, string gameVersion, string? loader, CancellationToken cancellationToken = default)
    {
        var url = $"{ApiRoot}project/{projectId}/version" +
                  $"?game_versions=%5B%22{Uri.EscapeDataString(gameVersion)}%22%5D";

        if (loader is { Length: > 0 } wanted)
            url += $"&loaders=%5B%22{Uri.EscapeDataString(wanted.ToLowerInvariant())}%22%5D";

        try
        {
            await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);

            var versions = await JsonSerializer
                .DeserializeAsync<List<ProjectVersion>>(stream, Options, cancellationToken)
                .ConfigureAwait(false);

            if (versions is null or { Count: 0 }) return null;

            var chosen = versions.FirstOrDefault(v => v.VersionType == "release") ?? versions[0];
            var file = chosen.Files.FirstOrDefault(f => f.Primary) ?? chosen.Files.FirstOrDefault();

            return file is null
                ? null
                : new ModrinthFile(
                    file.Url,
                    file.FileName,
                    file.Hashes?.GetValueOrDefault("sha1"),
                    file.Size,
                    Required(chosen.Dependencies));
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private sealed class SearchResults
    {
        [JsonPropertyName("hits")] public List<SearchHit>? Hits { get; init; }
    }

    private sealed class SearchHit
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
        [JsonPropertyName("slug")] public string? Slug { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("author")] public string? Author { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }
        [JsonPropertyName("downloads")] public long Downloads { get; init; }

        /// <summary>Search returns plain URLs here, and only the 350px thumbnails.</summary>
        [JsonPropertyName("gallery")] public List<string>? Gallery { get; init; }

        [JsonPropertyName("featured_gallery")] public string? FeaturedGallery { get; init; }

        /// <summary>Carries loader tags alongside real categories, so it needs sifting.</summary>
        [JsonPropertyName("categories")] public List<string>? Categories { get; init; }

        /// <summary>"mod", "resourcepack", "shader", "datapack", "modpack".</summary>
        [JsonPropertyName("project_type")] public string? ProjectType { get; init; }
    }

    private sealed class CategoryTag
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("project_type")] public string? ProjectType { get; init; }
    }

    private sealed class GalleryImage
    {
        [JsonPropertyName("url")] public string? Url { get; init; }
        [JsonPropertyName("raw_url")] public string? RawUrl { get; init; }
        [JsonPropertyName("featured")] public bool Featured { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("ordering")] public int Ordering { get; init; }
    }

    private sealed class CuratedProject
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("slug")] public string? Slug { get; init; }
        [JsonPropertyName("title")] public string? Title { get; init; }
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("icon_url")] public string? IconUrl { get; init; }
        [JsonPropertyName("downloads")] public long Downloads { get; init; }

        /// <summary>The project endpoint returns objects, with the full-size file alongside.</summary>
        [JsonPropertyName("gallery")] public List<GalleryImage>? Gallery { get; init; }

        /// <summary>"mod", "resourcepack", "shader", "datapack", "modpack".</summary>
        [JsonPropertyName("project_type")] public string? ProjectType { get; init; }
    }

    private sealed class HashedVersion
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; init; }
    }

    private sealed class Project
    {
        [JsonPropertyName("game_versions")] public List<string>? GameVersions { get; init; }
        [JsonPropertyName("body")] public string? Body { get; init; }
        [JsonPropertyName("gallery")] public List<GalleryImage>? Gallery { get; init; }
    }

    private sealed class VersionDependency
    {
        [JsonPropertyName("project_id")] public string? ProjectId { get; init; }

        /// <summary>required | optional | incompatible | embedded</summary>
        [JsonPropertyName("dependency_type")] public string? DependencyType { get; init; }
    }

    private sealed class ProjectVersion
    {
        [JsonPropertyName("version_type")] public string? VersionType { get; init; }
        [JsonPropertyName("dependencies")] public List<VersionDependency>? Dependencies { get; init; }
        [JsonPropertyName("files")] public List<VersionFile> Files { get; init; } = [];
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("version_number")] public string? VersionNumber { get; init; }
        [JsonPropertyName("game_versions")] public List<string>? GameVersions { get; init; }
        [JsonPropertyName("loaders")] public List<string>? Loaders { get; init; }
        [JsonPropertyName("downloads")] public long Downloads { get; init; }
        [JsonPropertyName("date_published")] public DateTimeOffset? Published { get; init; }
    }

    private sealed class VersionFile
    {
        [JsonPropertyName("url")] public string Url { get; init; } = "";
        [JsonPropertyName("filename")] public string FileName { get; init; } = "";
        [JsonPropertyName("primary")] public bool Primary { get; init; }
        [JsonPropertyName("size")] public long Size { get; init; }
        [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; init; }
    }
}
