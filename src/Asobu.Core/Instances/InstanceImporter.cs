using System.IO.Compression;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asobu.Core.Download;
using Asobu.Core.Minecraft;
using Asobu.Core.Mods;

namespace Asobu.Core.Instances;

/// <summary>
/// What an import came to. <paramref name="Reason"/> is why it did not happen, written for the
/// person who pasted the code; <paramref name="Notes"/> are things worth knowing about an import
/// that did — files skipped, guesses made — so a quiet success never hides them.
/// </summary>
public sealed record ImportOutcome(Instance? Instance, string? Reason, IReadOnlyList<string> Notes)
{
    /// <summary>
    /// Files whose authors allow downloads only from their own page. The import is finished and
    /// the instance is real; these are what the person now has to fetch themselves, and the
    /// launcher waits for each one and files it away.
    /// </summary>
    public IReadOnlyList<BlockedDownload> Blocked { get; init; } = [];

    public bool Succeeded => Instance is not null;

    public static ImportOutcome Failed(string reason) => new(null, reason, []);
}

/// <summary>
/// Builds an instance out of something that already exists somewhere else: a modpack file, a
/// folder another launcher made, or a code a friend sent.
///
/// What it accepts, by container:
///  - a Modrinth .mrpack (or a zip holding modrinth.index.json);
///  - a CurseForge pack zip — manifest.json plus overrides — which is also exactly what their
///    app's "share profile" codes and profile exports produce;
///  - an instance folder: Asobu's own (instance.json), the CurseForge app's
///    (minecraftinstance.json), MultiMC or Prism's (mmc-pack.json), or a bare game folder,
///    zipped or not;
///  - a code: a CurseForge profile code, or a Modrinth modpack link or slug. CurseForge is asked
///    first — their codes are opaque, so the only way to know is to ask — and Modrinth second,
///    mirroring how the download side of the catalogue already leans.
///
/// Everything lands in a freshly created instance, and a failure part-way tears that instance
/// down again: half an import surviving as a library card would launch into a broken game.
/// </summary>
public sealed class InstanceImporter(
    HttpClient http,
    AsobuPaths paths,
    InstanceStore instances,
    Modrinth modrinth,
    CurseForge curseForge,
    ModCatalogue catalogue)
{
    /// <summary>
    /// Resolves a profile code to the pack zip behind it. Not in the published API; it is what
    /// the CurseForge app itself calls, answering 404 "code does not exist" for anything it
    /// does not know and redirecting to the zip on their CDN for anything it does. No key.
    /// </summary>
    private const string SharedProfileUrl = "https://api.curseforge.com/v1/shared-profile/";

    /// <summary>
    /// CurseForge's own public download routes, keyless. Only used when this build has no API
    /// key at all: with one, the proper API answers, including the author's own say over
    /// third-party downloads — which is honoured, not routed around.
    /// </summary>
    private const string WebApiRoot = "https://www.curseforge.com/api/v1/";

    private readonly Downloader _downloader = new(http);

    /// <summary>
    /// Filled in by the CurseForge resolver while one import runs, and handed back with its
    /// outcome. Held here rather than threaded through every signature between the two: the
    /// importer runs one import at a time, which is what the modal in front of it allows.
    /// </summary>
    private readonly List<BlockedDownload> _blocked = [];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ---- The three doors in. ----

    /// <summary>A pack file: .mrpack, or any zip holding a manifest this class knows.</summary>
    public async Task<ImportOutcome> ImportFileAsync(
        string path, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return ImportOutcome.Failed("That file doesn't exist any more.");

        progress?.Report(new InstallProgress("Reading the pack", 0));

        ZipArchive archive;
        try
        {
            archive = ZipFile.OpenRead(path);
        }
        catch (Exception e) when (e is InvalidDataException or IOException)
        {
            return ImportOutcome.Failed("That file isn't a zip Asobu can read.");
        }

        using (archive)
        {
            if (Find(archive, "modrinth.index.json") is { } index)
                return await ImportModrinthPackAsync(archive, index, progress, cancellationToken)
                    .ConfigureAwait(false);

            // The folder formats, arrived zipped, are recognised before a pack manifest on
            // purpose: the CurseForge app's instance folders can hold a manifest.json of their
            // own, and a zip of one is the whole instance, not the pack it began as. Unpacked
            // and handed to the folder door — the detection is the same, only the container
            // differs.
            if (Find(archive, "instance.json") is not null
                || Find(archive, "minecraftinstance.json") is not null
                || Find(archive, "mmc-pack.json") is not null)
            {
                var unpacked = TempPath("unzipped-" + Guid.NewGuid().ToString("n")[..8]);
                try
                {
                    progress?.Report(new InstallProgress("Unpacking", 0));
                    ExtractUnder(archive, RootPrefix(archive), unpacked, []);

                    return await ImportFolderAsync(unpacked, progress, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    TryDelete(unpacked);
                }
            }

            if (Find(archive, "manifest.json") is { } manifest)
                return await ImportCurseForgePackAsync(archive, manifest, progress, cancellationToken)
                    .ConfigureAwait(false);
        }

        return ImportOutcome.Failed(
            "No pack manifest inside — expected a Modrinth .mrpack, a CurseForge pack zip, or a zipped instance folder.");
    }

    /// <summary>An instance folder, from Asobu or any launcher whose layout is recognised.</summary>
    public async Task<ImportOutcome> ImportFolderAsync(
        string path, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(path)) return ImportOutcome.Failed("That folder doesn't exist any more.");

        progress?.Report(new InstallProgress("Reading the folder", 0));

        if (File.Exists(Path.Combine(path, "instance.json")))
            return await Task.Run(() => ImportAsobuFolder(path), cancellationToken).ConfigureAwait(false);

        if (File.Exists(Path.Combine(path, "minecraftinstance.json")))
            return await Task.Run(() => ImportCurseAppFolder(path), cancellationToken).ConfigureAwait(false);

        if (File.Exists(Path.Combine(path, "mmc-pack.json")))
            return await Task.Run(() => ImportMultiMcFolder(path), cancellationToken).ConfigureAwait(false);

        if (LooksLikeGameFolder(path))
            return await Task.Run(() => ImportBareGameFolder(path), cancellationToken).ConfigureAwait(false);

        return ImportOutcome.Failed(
            "Nothing recognisable in that folder — it has none of the files Asobu, the CurseForge app, MultiMC or Prism leave behind, and it doesn't look like a game folder either.");
    }

    /// <summary>
    /// A code or a link someone shared: CurseForge profile codes, CurseForge modpack links
    /// (including the app's own curseforge:// install links), and Modrinth links or slugs.
    /// </summary>
    public async Task<ImportOutcome> ImportCodeAsync(
        string code, IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        code = code.Trim();
        if (code.Length == 0) return ImportOutcome.Failed("Paste a code first.");

        // A Modrinth address says outright what it is; no need to ask CurseForge about it.
        if (code.Contains("modrinth.com/", StringComparison.OrdinalIgnoreCase))
        {
            var (slug, versionHint) = ParseModrinthUrl(code);

            return slug is null
                ? ImportOutcome.Failed("That Modrinth link doesn't point at a project.")
                : await ImportModrinthProjectAsync(slug, versionHint, progress, cancellationToken)
                    .ConfigureAwait(false);
        }

        if (code.StartsWith("curseforge://", StringComparison.OrdinalIgnoreCase))
            return await ImportCurseForgeProtocolAsync(code, progress, cancellationToken).ConfigureAwait(false);

        if (code.Contains("curseforge.com/", StringComparison.OrdinalIgnoreCase))
            return await ImportCurseForgeLinkAsync(code, progress, cancellationToken).ConfigureAwait(false);

        if (code.Contains('/') || code.Contains(' '))
            return ImportOutcome.Failed("That doesn't look like a profile code or a modpack link.");

        progress?.Report(new InstallProgress("Asking CurseForge about the code", 0));

        string? sharedZip;
        try
        {
            sharedZip = await FetchSharedProfileAsync(code, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return ImportOutcome.Failed("CurseForge couldn't be reached to look that code up.");
        }

        if (sharedZip is not null)
        {
            try
            {
                return await ImportFileAsync(sharedZip, progress, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                TryDelete(sharedZip);
            }
        }

        // Not a CurseForge code, so the same string is tried as a Modrinth slug or id.
        var outcome = await ImportModrinthProjectAsync(code, null, progress, cancellationToken)
            .ConfigureAwait(false);

        return outcome.Succeeded
            ? outcome
            : ImportOutcome.Failed(
                $"Neither CurseForge nor Modrinth recognise “{code}”. CurseForge codes do expire after 7 days — asking for a fresh one may be all it takes.");
    }

    // ---- Packs picked out of the catalogue, which is the browser's way in. ----

    /// <summary>
    /// Turns a modpack from the catalogue into an instance, taking its newest published build.
    /// The name is the one the person chose; without one the pack's own is used.
    /// </summary>
    public async Task<ImportOutcome> ImportPackAsync(
        CatalogueMod pack, string? name = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        progress?.Report(new InstallProgress($"Looking up {pack.Title}", 0));

        var versions = await catalogue.GetVersionsAsync(pack, cancellationToken).ConfigureAwait(false);

        // Newest first is how both providers answer, so the first with a file is the current one.
        var newest = versions.FirstOrDefault(version => version.Url is { Length: > 0 });

        if (newest is null)
            return ImportOutcome.Failed(versions.Count == 0
                ? $"No published builds came back for {pack.Title}."
                : $"{pack.Title} can only be downloaded from its own page. Download it there, then import the file.");

        return await ImportPackVersionAsync(newest, name, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The same for one particular build, which is what picking a row out of the versions table
    /// means. Both providers hand over a pack file here — a .mrpack or a CurseForge pack zip —
    /// so this comes down to fetching it and going in the front door.
    /// </summary>
    public async Task<ImportOutcome> ImportPackVersionAsync(
        ModVersion version, string? name = null,
        IProgress<InstallProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (version.Url is not { Length: > 0 } url)
            return ImportOutcome.Failed(
                "This build can only be downloaded from its own page. Download it there, then import the file.");

        var extension = Path.GetExtension(version.FileName) is { Length: > 0 } given ? given : ".zip";
        var path = TempPath("pack-" + Guid.NewGuid().ToString("n")[..8] + extension);

        try
        {
            await _downloader.RunAsync(
                [new DownloadTask(url, path, version.Sha1, version.Size)],
                Stage(progress, "Downloading the pack"),
                cancellationToken).ConfigureAwait(false);

            var outcome = await ImportFileAsync(path, progress, cancellationToken).ConfigureAwait(false);

            return outcome.Succeeded ? Rename(outcome, name) : outcome;
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>
    /// Gives the new instance the name that was asked for. Applied after the import rather than
    /// handed to it: what the pack calls itself is the right default, and only a person who
    /// typed something else has said otherwise.
    ///
    /// Through the store, so the folder moves too — an instance the person named themselves
    /// sitting in a folder named after the pack is exactly the mismatch folder naming exists to
    /// avoid.
    /// </summary>
    private ImportOutcome Rename(ImportOutcome outcome, string? name)
    {
        if (outcome.Instance is not { } instance) return outcome;
        if (name is not { Length: > 0 } wanted || wanted == instance.Name) return outcome;

        instances.Rename(instance, wanted);

        return outcome;
    }

    // ---- Modrinth packs. ----

    private async Task<ImportOutcome> ImportModrinthProjectAsync(
        string slug, string? versionHint, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress("Asking Modrinth about the project", 0));

        var versions = await modrinth.GetVersionsAsync(slug, cancellationToken).ConfigureAwait(false);
        if (versions.Count == 0)
            return ImportOutcome.Failed($"Modrinth doesn't know a project called “{slug}”.");

        var packs = versions
            .Where(v => v.FileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) && v.Url is { Length: > 0 })
            .ToList();

        if (packs.Count == 0)
            return ImportOutcome.Failed(
                $"“{slug}” exists on Modrinth, but it isn't a modpack — only whole packs can become instances.");

        var chosen = versionHint is null
            ? packs[0]
            : packs.FirstOrDefault(v => v.VersionNumber.Equals(versionHint, StringComparison.OrdinalIgnoreCase));

        var missedHint = chosen is null;
        chosen ??= packs[0];

        var file = TempPath("modrinth-" + Guid.NewGuid().ToString("n")[..8] + ".mrpack");
        try
        {
            await _downloader.RunAsync(
                [new DownloadTask(chosen.Url!, file, chosen.Sha1, chosen.Size)],
                Stage(progress, "Downloading the pack"),
                cancellationToken).ConfigureAwait(false);

            var outcome = await ImportFileAsync(file, progress, cancellationToken).ConfigureAwait(false);

            return missedHint && outcome.Succeeded
                ? outcome with
                {
                    Notes = [.. outcome.Notes,
                        $"Version “{versionHint}” wasn't found, so the newest one was taken instead."],
                }
                : outcome;
        }
        finally
        {
            TryDelete(file);
        }
    }

    private async Task<ImportOutcome> ImportModrinthPackAsync(
        ZipArchive archive, ZipArchiveEntry indexEntry,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        MrIndex? index;
        using (var stream = indexEntry.Open())
            index = await JsonSerializer.DeserializeAsync<MrIndex>(stream, Json, cancellationToken)
                .ConfigureAwait(false);

        if (index?.Dependencies is null || !index.Dependencies.TryGetValue("minecraft", out var gameVersion))
            return ImportOutcome.Failed("The pack's index doesn't say which Minecraft version it is for.");

        var (loader, loaderVersion, problem) = LoaderFromDependencies(index.Dependencies);
        if (problem is not null) return ImportOutcome.Failed(problem);

        var name = index.Name is { Length: > 0 } given ? given : "Imported pack";
        var notes = new List<string>();
        var instance = instances.Create(name, gameVersion, loader, loaderVersion);

        try
        {
            var gameDir = paths.InstanceGameDir(instance.Folder);
            var tasks = new List<DownloadTask>();
            var skipped = 0;

            foreach (var entry in index.Files ?? [])
            {
                if (entry.Env?.Client?.Equals("unsupported", StringComparison.OrdinalIgnoreCase) == true)
                {
                    skipped++;
                    continue;
                }

                if (entry.Downloads is not [{ Length: > 0 } url, ..]) continue;
                if (SafeRelativePath(gameDir, entry.Path) is not { } destination) continue;

                string? sha1 = null;
                entry.Hashes?.TryGetValue("sha1", out sha1);

                tasks.Add(new DownloadTask(url, destination, sha1, entry.FileSize));
            }

            await _downloader.RunAsync(tasks, Stage(progress, "Downloading files"), cancellationToken)
                .ConfigureAwait(false);

            progress?.Report(new InstallProgress("Copying the pack's own files", 1));

            var prefix = PrefixOf(indexEntry, "modrinth.index.json");
            ExtractUnder(archive, prefix + "overrides/", gameDir, notes);
            // Client overrides land second on purpose: where both name a file, the client one wins.
            ExtractUnder(archive, prefix + "client-overrides/", gameDir, notes);

            if (skipped > 0)
                notes.Add(skipped == 1
                    ? "One server-only file was left out."
                    : $"{skipped} server-only files were left out.");

            instances.Save(instance);
            return new ImportOutcome(instance, null, notes);
        }
        catch
        {
            instances.Delete(instance);
            throw;
        }
    }

    // ---- CurseForge packs — from a zip in hand, an exported profile, or a shared code. ----

    private async Task<ImportOutcome> ImportCurseForgePackAsync(
        ZipArchive archive, ZipArchiveEntry manifestEntry,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        CfManifest? manifest;
        using (var stream = manifestEntry.Open())
            manifest = await JsonSerializer.DeserializeAsync<CfManifest>(stream, Json, cancellationToken)
                .ConfigureAwait(false);

        _blocked.Clear();

        if (manifest?.Minecraft?.Version is not { Length: > 0 } gameVersion)
            return ImportOutcome.Failed("The pack's manifest doesn't say which Minecraft version it is for.");

        var primary = manifest.Minecraft.ModLoaders?.FirstOrDefault(l => l.Primary)
                      ?? manifest.Minecraft.ModLoaders?.FirstOrDefault();

        var (loader, loaderVersion, problem) = ParseLoaderId(primary?.Id);
        if (problem is not null) return ImportOutcome.Failed(problem);

        var name = manifest.Name is { Length: > 0 } given ? given : "Imported pack";
        var notes = new List<string>();
        var instance = instances.Create(name, gameVersion, loader, loaderVersion);

        try
        {
            var gameDir = paths.InstanceGameDir(instance.Folder);

            progress?.Report(new InstallProgress("Copying the pack's own files", 0));

            var overridesName = manifest.Overrides is { Length: > 0 } o ? o : "overrides";
            ExtractUnder(archive, PrefixOf(manifestEntry, "manifest.json") + overridesName + "/", gameDir, notes);

            var wanted = (manifest.Files ?? []).Where(f => f.Required).ToList();
            var optional = (manifest.Files?.Count ?? 0) - wanted.Count;
            if (optional > 0)
                notes.Add(optional == 1
                    ? "One optional file was left out; it can be added from Browse."
                    : $"{optional} optional files were left out; they can be added from Browse.");

            var tasks = curseForge.IsAvailable
                ? await ResolveWithApiAsync(
                    wanted, gameDir, gameVersion, loader, notes, progress, cancellationToken).ConfigureAwait(false)
                : await ResolveKeylessAsync(wanted, gameDir, notes, progress, cancellationToken).ConfigureAwait(false);

            await _downloader.RunAsync(tasks, Stage(progress, "Downloading mods"), cancellationToken)
                .ConfigureAwait(false);

            instances.Save(instance);
            return new ImportOutcome(instance, null, notes) { Blocked = [.. _blocked] };
        }
        catch
        {
            instances.Delete(instance);
            throw;
        }
    }

    /// <summary>
    /// The proper resolution: every file in one request, every project in another, and an
    /// author's opt-out of third-party downloads respected — CurseForge is never asked for a
    /// file it has been told not to serve. Where the same mod is published on Modrinth, it is
    /// taken from there instead, which is the mod's own second front door rather than a way
    /// around the first; anything left is named in the notes rather than skipped silently.
    /// </summary>
    private async Task<List<DownloadTask>> ResolveWithApiAsync(
        List<CfManifestFile> wanted, string gameDir, string gameVersion, string loader,
        List<string> notes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress("Looking the pack's files up", 0));

        var files = await curseForge.GetFilesByIdAsync(
            [.. wanted.Select(f => f.FileId)], cancellationToken).ConfigureAwait(false);

        if (files.Count == 0 && wanted.Count > 0)
            throw new InvalidOperationException("CurseForge did not answer the file lookup.");

        var projects = await curseForge.GetProjectsByIdAsync(
            [.. files.Select(f => f.ModId).Distinct()], cancellationToken).ConfigureAwait(false);

        var tasks = new List<DownloadTask>();
        var withheld = new List<CurseForge.PackFile>();

        foreach (var file in files)
        {
            if (file.DownloadUrl is not { Length: > 0 } url)
            {
                withheld.Add(file);
                continue;
            }

            if (Destination(gameDir, projects, file.ModId, file.FileName) is { } destination)
                tasks.Add(new DownloadTask(url, destination, file.Sha1, file.Size));
        }

        var missing = wanted.Count - files.Count;
        if (missing > 0)
            notes.Add(missing == 1
                ? "One file in the pack no longer exists on CurseForge."
                : $"{missing} files in the pack no longer exist on CurseForge.");

        if (withheld.Count > 0)
            await RescueFromModrinthAsync(
                withheld, projects, tasks, gameDir, gameVersion, loader, notes, progress, cancellationToken)
                .ConfigureAwait(false);

        return tasks;
    }

    /// <summary>
    /// Looks for the mods CurseForge would not serve on Modrinth, and takes them from there.
    /// Matched by slug — the two catalogues nearly always agree on it — and then checked by
    /// name, because a slug two different mods happen to share would otherwise install the
    /// wrong one, which is worse than installing nothing.
    /// </summary>
    private async Task RescueFromModrinthAsync(
        List<CurseForge.PackFile> withheld,
        IReadOnlyDictionary<int, CurseForge.CurseProject> projects,
        List<DownloadTask> tasks,
        string gameDir, string gameVersion, string loader,
        List<string> notes, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress("Looking for the rest on Modrinth", 0));

        var bySlug = withheld
            .Select(file => projects.GetValueOrDefault(file.ModId))
            .Where(project => project is { Slug.Length: > 0 })
            .GroupBy(project => project!.Slug!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First()!, StringComparer.OrdinalIgnoreCase);

        var found = bySlug.Count == 0
            ? []
            : await modrinth.GetProjectsAsync([.. bySlug.Keys], cancellationToken).ConfigureAwait(false);

        var rescued = new List<string>();
        var stillMissing = new List<string>();
        var matched = new HashSet<int>();

        foreach (var listing in found)
        {
            // GetProjectsAsync answers in the order asked, so the slug comes back from the URL.
            var slug = listing.PageUrl[(listing.PageUrl.LastIndexOf('/') + 1)..];

            if (!bySlug.TryGetValue(slug, out var project)) continue;
            if (!SameMod(project.Name, listing.Title)) continue;

            var download = await modrinth
                .GetDownloadAsync(listing.Id, gameVersion, loader, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (download?.Url is not { Length: > 0 } url) continue;
            if (Destination(gameDir, projects, project.Id, download.FileName) is not { } destination) continue;

            tasks.Add(new DownloadTask(url, destination, download.Sha1, download.Size));
            matched.Add(project.Id);
            rescued.Add(listing.Title);
        }

        foreach (var file in withheld)
        {
            if (matched.Contains(file.ModId)) continue;

            var project = projects.GetValueOrDefault(file.ModId);
            var name = project?.Name is { Length: > 0 } given ? given : file.FileName;

            stillMissing.Add(name);

            // Only worth offering when there is a page to send them to and somewhere to put it.
            if (project?.Slug is not { Length: > 0 } slug) continue;
            if (Destination(gameDir, projects, file.ModId, file.FileName) is not { } destination) continue;

            _blocked.Add(new BlockedDownload(
                name,
                file.FileName,
                file.Size,
                file.Sha1,
                $"https://www.curseforge.com/minecraft/{SectionForClass(project.ClassId)}/{slug}/download/{file.FileId}",
                destination));
        }

        if (rescued.Count > 0)
            notes.Add($"{Listed(rescued)} {(rescued.Count == 1 ? "isn't" : "aren't")} downloadable from "
                      + "CurseForge by the author's choice, so " + (rescued.Count == 1 ? "it was" : "they were")
                      + " taken from Modrinth instead.");

        if (stillMissing.Count > 0 && _blocked.Count == 0)
            notes.Add($"{Listed(stillMissing)} can only be downloaded from "
                      + (stillMissing.Count == 1 ? "its" : "their")
                      + " own CurseForge page, and " + (stillMissing.Count == 1 ? "was" : "were")
                      + " left out — the pack will ask for "
                      + (stillMissing.Count == 1 ? "it" : "them") + " on first launch.");
    }

    /// <summary>Where a project's file belongs in the game folder, or null to leave it out.</summary>
    private static string? Destination(
        string gameDir, IReadOnlyDictionary<int, CurseForge.CurseProject> projects, int modId, string fileName) =>
        FolderForClass(projects.TryGetValue(modId, out var project) ? project.ClassId : 6) is { } folder
            ? SafeRelativePath(gameDir, folder + "/" + fileName)
            : null;

    /// <summary>
    /// Whether two catalogue entries are the same mod. Compared on letters and digits alone, so
    /// "Just Enough Items" still matches "Just Enough Items (JEI)" and "Mod: Reborn" matches
    /// "Mod Reborn", while two unrelated mods sharing a slug do not.
    /// </summary>
    private static bool SameMod(string left, string right)
    {
        var a = Simplify(left);
        var b = Simplify(right);

        if (a.Length == 0 || b.Length == 0) return false;
        if (!a.Contains(b) && !b.Contains(a)) return false;

        // One name containing the other is only worth something when the two are close in
        // length: "Create" sits inside "Create: Steam 'n' Rails" and is a different mod.
        return Math.Min(a.Length, b.Length) * 2 >= Math.Max(a.Length, b.Length);

        static string Simplify(string text) =>
            new([.. text.ToLowerInvariant().Where(char.IsLetterOrDigit)]);
    }

    /// <summary>"A", "A and B", "A, B and 3 more" — a list short enough to read in a note.</summary>
    private static string Listed(List<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        <= 4 => string.Join(", ", names[..^1]) + " and " + names[^1],
        _ => string.Join(", ", names.Take(3)) + $" and {names.Count - 3} more",
    };

    /// <summary>
    /// The fallback for builds with no API key: CurseForge's own public per-file routes, the
    /// same ones the download buttons on their site sit on. One request per file, so slower —
    /// and no class information, so everything is assumed to be a mod unless its name says zip.
    /// </summary>
    private async Task<List<DownloadTask>> ResolveKeylessAsync(
        List<CfManifestFile> wanted, string gameDir, List<string> notes,
        IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var tasks = new List<DownloadTask>();
        var missing = 0;

        for (var i = 0; i < wanted.Count; i++)
        {
            progress?.Report(new InstallProgress(
                $"Looking the pack's files up ({i + 1} of {wanted.Count})", (double)i / wanted.Count));

            var file = wanted[i];
            var url = $"{WebApiRoot}mods/{file.ProjectId}/files/{file.FileId}";

            WebFileResponse? info = null;
            try
            {
                await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
                info = await JsonSerializer.DeserializeAsync<WebFileResponse>(stream, Json, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or JsonException)
            {
            }

            if (info?.Data?.FileName is not { Length: > 0 } fileName)
            {
                missing++;
                continue;
            }

            var folder = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? "resourcepacks" : "mods";
            if (SafeRelativePath(gameDir, folder + "/" + fileName) is { } destination)
                tasks.Add(new DownloadTask($"{url}/download", destination, null, info.Data.FileLength));
        }

        if (missing > 0)
            notes.Add(missing == 1
                ? "One file in the pack couldn't be looked up on CurseForge."
                : $"{missing} files in the pack couldn't be looked up on CurseForge.");

        return tasks;
    }

    /// <summary>
    /// A link off a CurseForge project page. Only modpacks can become an instance, so a link to
    /// anything else is turned down by name rather than imported as an empty instance — the
    /// mod itself belongs in Browse, added to an instance that already exists.
    /// </summary>
    private async Task<ImportOutcome> ImportCurseForgeLinkAsync(
        string link, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var marker = link.IndexOf("curseforge.com/", StringComparison.OrdinalIgnoreCase);
        var segments = link[(marker + "curseforge.com/".Length)..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // minecraft/<section>/<slug>, then optionally files/<id> or download/<id>.
        if (segments.Length < 3)
            return ImportOutcome.Failed("That CurseForge link doesn't point at a project.");

        if (!segments[1].Equals("modpacks", StringComparison.OrdinalIgnoreCase))
            return ImportOutcome.Failed(
                $"That link is to a {SectionName(segments[1])}, not a modpack — only a whole pack can become an instance. Add it to an instance from Browse instead.");

        var slug = segments[2].Split('?', 2)[0];

        int? fileId = segments.Length >= 5
                      && segments[3] is "files" or "download"
                      && int.TryParse(segments[4].Split('?', 2)[0], out var parsed)
            ? parsed
            : null;

        if (!curseForge.IsAvailable)
            return ImportOutcome.Failed(
                "CurseForge links need an API key to look up, and this build has none. Settings takes one, or the pack's own zip can be imported from a file.");

        progress?.Report(new InstallProgress("Asking CurseForge about the pack", 0));

        var modId = await curseForge.GetModIdBySlugAsync(slug, ModKind.Modpack, cancellationToken)
            .ConfigureAwait(false);

        return modId is { } id
            ? await ImportCurseForgeFileAsync(id, fileId, progress, cancellationToken).ConfigureAwait(false)
            : ImportOutcome.Failed($"CurseForge doesn't know a modpack called “{slug}”.");
    }

    /// <summary>
    /// curseforge://install?addonId=&lt;project&gt;&amp;fileId=&lt;file&gt; — what their site's
    /// "Install with the app" button hands over, and what people paste when they have it.
    /// </summary>
    private async Task<ImportOutcome> ImportCurseForgeProtocolAsync(
        string link, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var query = link.IndexOf('?') is > 0 and var mark ? link[(mark + 1)..] : "";

        int? addonId = null, fileId = null;

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=');
            if (equals <= 0 || !int.TryParse(pair[(equals + 1)..], out var value)) continue;

            switch (pair[..equals].ToLowerInvariant())
            {
                case "addonid": addonId = value; break;
                case "fileid": fileId = value; break;
            }
        }

        if (addonId is not { } project)
            return ImportOutcome.Failed("That CurseForge link doesn't name a project.");

        if (!curseForge.IsAvailable)
            return ImportOutcome.Failed(
                "CurseForge links need an API key to look up, and this build has none. Settings takes one, or the pack's own zip can be imported from a file.");

        return await ImportCurseForgeFileAsync(project, fileId, progress, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Fetches one CurseForge pack file and imports it. A modpack's file is a pack zip, so this
    /// hands off to the same door a downloaded zip goes through.
    /// </summary>
    private async Task<ImportOutcome> ImportCurseForgeFileAsync(
        int modId, int? fileId, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        progress?.Report(new InstallProgress("Looking the pack up", 0));

        var file = fileId is { } wanted
            ? (await curseForge.GetFilesByIdAsync([wanted], cancellationToken).ConfigureAwait(false))
                .FirstOrDefault()
            : await curseForge.GetNewestFileAsync(modId, cancellationToken).ConfigureAwait(false);

        if (file is null)
            return ImportOutcome.Failed("CurseForge has no downloadable file for that pack.");

        if (file.DownloadUrl is not { Length: > 0 } url)
            return ImportOutcome.Failed(
                "This pack's author only allows downloads from their own page. Download it there, then import the file.");

        var path = TempPath("curseforge-" + Guid.NewGuid().ToString("n")[..8] + ".zip");
        try
        {
            await _downloader.RunAsync(
                [new DownloadTask(url, path, file.Sha1, file.Size)],
                Stage(progress, "Downloading the pack"),
                cancellationToken).ConfigureAwait(false);

            return await ImportFileAsync(path, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDelete(path);
        }
    }

    /// <summary>What a CurseForge URL section is, for saying so in a refusal.</summary>
    private static string SectionName(string section) => section.ToLowerInvariant() switch
    {
        "mc-mods" => "mod",
        "texture-packs" => "resource pack",
        "shaders" => "shader pack",
        "worlds" => "world",
        "data-packs" => "data pack",
        "customization" => "customisation",
        _ => "project",
    };

    /// <summary>The zip a profile code stands for, or null when CurseForge says no such code.</summary>
    private async Task<string?> FetchSharedProfileAsync(string code, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(
                SharedProfileUrl + Uri.EscapeDataString(code),
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var file = TempPath("shared-" + Guid.NewGuid().ToString("n")[..8] + ".zip");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = File.Create(file);
        await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);

        return file;
    }

    // ---- Folders other launchers left behind. ----

    private ImportOutcome ImportAsobuFolder(string path)
    {
        Instance? source;
        try
        {
            source = JsonSerializer.Deserialize<Instance>(
                File.ReadAllText(Path.Combine(path, "instance.json")), Json);
        }
        catch (JsonException)
        {
            return ImportOutcome.Failed("The folder's instance.json is damaged.");
        }

        if (source is null || source.MinecraftVersion.Length == 0)
            return ImportOutcome.Failed("The folder's instance.json is damaged.");

        var instance = instances.Create(
            source.Name.Length > 0 ? source.Name : "Imported instance",
            source.MinecraftVersion, source.Loader, source.LoaderVersion, source.PerformanceMod);

        try
        {
            var game = Path.Combine(path, "minecraft");
            if (Directory.Exists(game)) CopyContents(game, paths.InstanceGameDir(instance.Folder), _ => false);

            instances.Save(instance);
            return new ImportOutcome(instance, null, []);
        }
        catch
        {
            instances.Delete(instance);
            throw;
        }
    }

    private ImportOutcome ImportCurseAppFolder(string path)
    {
        CurseAppInstance? source;
        try
        {
            source = JsonSerializer.Deserialize<CurseAppInstance>(
                File.ReadAllText(Path.Combine(path, "minecraftinstance.json")), Json);
        }
        catch (JsonException)
        {
            return ImportOutcome.Failed("The folder's minecraftinstance.json is damaged.");
        }

        if (source?.GameVersion is not { Length: > 0 } gameVersion)
            return ImportOutcome.Failed("The folder's minecraftinstance.json doesn't name a Minecraft version.");

        var (loader, loaderVersion, problem) = ParseLoaderId(source.BaseModLoader?.Name);
        if (problem is not null) return ImportOutcome.Failed(problem);

        var name = source.Name is { Length: > 0 } given ? given : Path.GetFileName(path.TrimEnd('\\', '/'));
        var instance = instances.Create(name, gameVersion, loader, loaderVersion);

        try
        {
            // The CurseForge app's instance folder is the game folder, with its bookkeeping
            // dropped straight in. Everything comes along except that bookkeeping.
            CopyContents(path, paths.InstanceGameDir(instance.Folder),
                top => top is "minecraftinstance.json" or "manifest.json" or "modelist.html"
                       or ".curseclient" or "profileImage.png");

            instances.Save(instance);
            return new ImportOutcome(instance, null, []);
        }
        catch
        {
            instances.Delete(instance);
            throw;
        }
    }

    private ImportOutcome ImportMultiMcFolder(string path)
    {
        MmcPack? pack;
        try
        {
            pack = JsonSerializer.Deserialize<MmcPack>(
                File.ReadAllText(Path.Combine(path, "mmc-pack.json")), Json);
        }
        catch (JsonException)
        {
            return ImportOutcome.Failed("The folder's mmc-pack.json is damaged.");
        }

        string? gameVersion = null;
        var loader = Loaders.Vanilla;
        string? loaderVersion = null;

        foreach (var component in pack?.Components ?? [])
        {
            switch (component.Uid)
            {
                case "net.minecraft": gameVersion = component.Version; break;
                case "net.fabricmc.fabric-loader": (loader, loaderVersion) = (Loaders.Fabric, component.Version); break;
                case "net.minecraftforge": (loader, loaderVersion) = (Loaders.Forge, component.Version); break;
                case "net.neoforged": (loader, loaderVersion) = (Loaders.NeoForge, component.Version); break;
                case "org.quiltmc.quilt-loader": (loader, loaderVersion) = (Loaders.Quilt, component.Version); break;
            }
        }

        if (gameVersion is not { Length: > 0 })
            return ImportOutcome.Failed("The folder's mmc-pack.json doesn't name a Minecraft version.");

        var name = NameFromInstanceCfg(Path.Combine(path, "instance.cfg"))
                   ?? Path.GetFileName(path.TrimEnd('\\', '/'));

        var instance = instances.Create(name, gameVersion, loader, loaderVersion);

        try
        {
            // MultiMC keeps the game folder one level down; either spelling of it appears.
            var game = Directory.Exists(Path.Combine(path, ".minecraft"))
                ? Path.Combine(path, ".minecraft")
                : Path.Combine(path, "minecraft");

            if (Directory.Exists(game)) CopyContents(game, paths.InstanceGameDir(instance.Folder), _ => false);

            instances.Save(instance);
            return new ImportOutcome(instance, null, []);
        }
        catch
        {
            instances.Delete(instance);
            throw;
        }
    }

    /// <summary>
    /// A .minecraft someone copied out of the vanilla launcher. The version has to be read off
    /// the versions folder, because nothing else in there says — and when even that is missing,
    /// refusing with the reason beats inventing a version that silently loads nothing.
    /// </summary>
    private ImportOutcome ImportBareGameFolder(string path)
    {
        var (gameVersion, loader, loaderVersion) = SniffVersionsFolder(Path.Combine(path, "versions"));

        if (gameVersion is null)
            return ImportOutcome.Failed(
                "This looks like a game folder, but nothing in it says which Minecraft version it is for. Make an instance on the right version instead, then copy the saves and mods in.");

        var notes = new List<string>();
        if (loader == Loaders.Vanilla && Directory.Exists(Path.Combine(path, "mods")))
            notes.Add("A mods folder came along, but no mod loader could be recognised — the instance is plain " + gameVersion + ".");

        var instance = instances.Create(
            Path.GetFileName(path.TrimEnd('\\', '/')), gameVersion, loader, loaderVersion);

        try
        {
            CopyContents(path, paths.InstanceGameDir(instance.Folder), top => top is "versions");

            instances.Save(instance);
            return new ImportOutcome(instance, null, notes);
        }
        catch
        {
            instances.Delete(instance);
            throw;
        }
    }

    private static bool LooksLikeGameFolder(string path) =>
        Directory.Exists(Path.Combine(path, "saves"))
        || Directory.Exists(Path.Combine(path, "mods"))
        || Directory.Exists(Path.Combine(path, "versions"))
        || File.Exists(Path.Combine(path, "options.txt"));

    /// <summary>
    /// What the vanilla launcher's versions folder can tell: "1.21.4" is a plain version,
    /// "fabric-loader-0.16.9-1.21.4" and "1.20.1-forge-47.3.0" and "neoforge-21.4.33" are the
    /// loaders' own naming habits. A modded id outranks a plain one; ties go to the newest.
    /// </summary>
    private static (string? GameVersion, string Loader, string? LoaderVersion) SniffVersionsFolder(string versionsDir)
    {
        if (!Directory.Exists(versionsDir)) return (null, Loaders.Vanilla, null);

        (string Game, string Loader, string? Version)? modded = null;
        string? plain = null;
        DateTime moddedTime = DateTime.MinValue, plainTime = DateTime.MinValue;

        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            var id = Path.GetFileName(dir);
            var time = Directory.GetLastWriteTimeUtc(dir);

            if (id.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase)
                || id.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase))
            {
                // <loader>-loader-<loader version>-<minecraft>, which both write the same way.
                var quilt = id.StartsWith("quilt-loader-", StringComparison.OrdinalIgnoreCase);
                var rest = id[(quilt ? "quilt-loader-" : "fabric-loader-").Length..].Split('-', 2);

                if (rest.Length == 2 && time > moddedTime)
                    (modded, moddedTime) = ((rest[1], quilt ? Loaders.Quilt : Loaders.Fabric, rest[0]), time);
            }
            else if (id.Contains("-forge-", StringComparison.OrdinalIgnoreCase))
            {
                // <minecraft>-forge-<loader>
                var parts = id.Split("-forge-", 2, StringSplitOptions.None);
                if (parts.Length == 2 && time > moddedTime)
                    (modded, moddedTime) = ((parts[0], Loaders.Forge, parts[1]), time);
            }
            else if (id.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
            {
                // neoforge-<loader>; the Minecraft version is implied, so a plain id must fill it in.
                if (time > moddedTime)
                    (modded, moddedTime) = (("", Loaders.NeoForge, id["neoforge-".Length..]), time);
            }
            else if (id.Length > 0 && char.IsDigit(id[0]) && time > plainTime)
            {
                (plain, plainTime) = (id, time);
            }
        }

        if (modded is { } found)
        {
            var game = found.Game.Length == 0 ? plain : found.Game;
            if (game is { Length: > 0 }) return (game, found.Loader, found.Version);
        }

        return (plain, Loaders.Vanilla, null);
    }

    private static string? NameFromInstanceCfg(string path)
    {
        if (!File.Exists(path)) return null;

        foreach (var line in File.ReadLines(path))
            if (line.StartsWith("name=", StringComparison.OrdinalIgnoreCase))
                return line[5..].Trim() is { Length: > 0 } name ? name : null;

        return null;
    }

    // ---- The shared plumbing. ----

    /// <summary>fabric-0.16.9, forge-47.3.0, neoforge-21.4.33 — how CurseForge writes loaders.</summary>
    private static (string Loader, string? LoaderVersion, string? Problem) ParseLoaderId(string? id)
    {
        if (id is not { Length: > 0 }) return (Loaders.Vanilla, null, null);

        var dash = id.IndexOf('-');
        var (family, version) = dash > 0 ? (id[..dash], id[(dash + 1)..]) : (id, (string?)null);

        return family.ToLowerInvariant() switch
        {
            "fabric" => (Loaders.Fabric, version, null),
            "forge" => (Loaders.Forge, version, null),
            "neoforge" => (Loaders.NeoForge, version, null),
            "quilt" => (Loaders.Quilt, version, null),
            _ => (Loaders.Vanilla, null,
                $"This pack needs a loader called “{family}”, which Asobu doesn't know."),
        };
    }

    /// <summary>How a .mrpack writes the same thing: loaders are keys of the dependency table.</summary>
    private static (string Loader, string? LoaderVersion, string? Problem) LoaderFromDependencies(
        Dictionary<string, string> dependencies)
    {
        if (dependencies.TryGetValue("fabric-loader", out var fabric)) return (Loaders.Fabric, fabric, null);
        if (dependencies.TryGetValue("forge", out var forge)) return (Loaders.Forge, forge, null);
        if (dependencies.TryGetValue("neoforge", out var neo)) return (Loaders.NeoForge, neo, null);
        if (dependencies.TryGetValue("quilt-loader", out var quilt)) return (Loaders.Quilt, quilt, null);

        return (Loaders.Vanilla, null, null);
    }

    /// <summary>modrinth.com/modpack/&lt;slug&gt;, with an optional /version/&lt;number&gt; after.</summary>
    private static (string? Slug, string? VersionHint) ParseModrinthUrl(string url)
    {
        var marker = url.IndexOf("modrinth.com/", StringComparison.OrdinalIgnoreCase);
        var segments = url[(marker + "modrinth.com/".Length)..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // The first segment is the project type — modpack, mod, datapack — and the second the slug.
        if (segments.Length < 2) return (null, null);

        var slug = Uri.UnescapeDataString(segments[1].Split('?', 2)[0]);
        var version = segments.Length >= 4 && segments[2].Equals("version", StringComparison.OrdinalIgnoreCase)
            ? Uri.UnescapeDataString(segments[3].Split('?', 2)[0])
            : null;

        return (slug.Length > 0 ? slug : null, version);
    }

    /// <summary>The part of a CurseForge address naming what kind of project it is.</summary>
    private static string SectionForClass(int classId) => classId switch
    {
        12 => "texture-packs",
        6552 => "shaders",
        6945 => "data-packs",
        17 => "worlds",
        4471 => "modpacks",
        _ => "mc-mods",
    };

    /// <summary>Where a class of CurseForge project lives in a game folder; null to leave out.</summary>
    private static string? FolderForClass(int classId) =>
        ModContent.FolderFor(ModContent.KindForClass(classId));

    /// <summary>
    /// Joins a zip- or manifest-supplied relative path onto the game folder, refusing anything
    /// that tries to climb out of it. Null means "do not write this file anywhere".
    /// </summary>
    private static string? SafeRelativePath(string root, string relative)
    {
        if (relative.Length == 0) return null;

        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('\\', '/')));

        return full.StartsWith(
            Path.GetFullPath(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? full
            : null;
    }

    /// <summary>
    /// The named entry, tolerating the single wrapping folder some exporters put around
    /// everything — a pack is a pack whether or not it travels inside its own name.
    /// </summary>
    private static ZipArchiveEntry? Find(ZipArchive archive, string name)
    {
        ZipArchiveEntry? wrapped = null;

        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (path.Equals(name, StringComparison.OrdinalIgnoreCase)) return entry;

            var slash = path.IndexOf('/');
            if (slash > 0 && path[(slash + 1)..].Equals(name, StringComparison.OrdinalIgnoreCase))
                wrapped ??= entry;
        }

        return wrapped;
    }

    /// <summary>The wrapper folder in front of a found entry — "" or "MyPack/".</summary>
    private static string PrefixOf(ZipArchiveEntry entry, string name) =>
        entry.FullName.Replace('\\', '/')[..^name.Length];

    /// <summary>The wrapping folder shared by every entry of the archive, if there is one.</summary>
    private static string RootPrefix(ZipArchive archive)
    {
        string? root = null;

        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            var slash = path.IndexOf('/');
            if (slash <= 0) return "";

            var top = path[..(slash + 1)];
            if (root is null) root = top;
            else if (!root.Equals(top, StringComparison.OrdinalIgnoreCase)) return "";
        }

        return root ?? "";
    }

    /// <summary>Extracts everything under a prefix, minus anything that tries to escape.</summary>
    private static void ExtractUnder(ZipArchive archive, string prefix, string destination, List<string> notes)
    {
        var escaped = 0;

        foreach (var entry in archive.Entries)
        {
            var path = entry.FullName.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.EndsWith('/') || entry.Name.Length == 0) continue;

            var relative = path[prefix.Length..];

            if (SafeRelativePath(destination, relative) is not { } target)
            {
                escaped++;
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }

        if (escaped > 0)
            notes.Add($"{escaped} file{(escaped == 1 ? "" : "s")} in the pack pointed outside the instance "
                      + $"and {(escaped == 1 ? "was" : "were")} refused.");
    }

    /// <summary>Copies a folder's contents, with a veto over the top-level names that come.</summary>
    private static void CopyContents(string source, string destination, Func<string, bool> skipTopLevel)
    {
        Directory.CreateDirectory(destination);

        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
        {
            var name = Path.GetFileName(entry);
            if (skipTopLevel(name)) continue;

            var target = Path.Combine(destination, name);

            if (Directory.Exists(entry)) CopyTree(entry, target);
            else File.Copy(entry, target, overwrite: true);
        }
    }

    private static void CopyTree(string source, string destination)
    {
        Directory.CreateDirectory(destination);

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(destination, Path.GetRelativePath(source, file)), overwrite: true);
    }

    private string TempPath(string name) => Path.Combine(paths.Cache, "imports", name);

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
            else if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
            // A leftover in the cache is not worth failing an import over.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Turns the downloader's counting into the one status line the modal shows.</summary>
    private static IProgress<DownloadProgress> Stage(IProgress<InstallProgress>? progress, string what) =>
        new RelayProgress(p => progress?.Report(new InstallProgress(
            p.Total > 0 ? $"{what} ({p.Completed} of {p.Total})" : what, p.Fraction)));

    private sealed class RelayProgress(Action<DownloadProgress> apply) : IProgress<DownloadProgress>
    {
        public void Report(DownloadProgress value) => apply(value);
    }

    // ---- The shapes of everyone's manifests. ----

    private sealed class MrIndex
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; init; }
        [JsonPropertyName("files")] public List<MrFile>? Files { get; init; }
    }

    private sealed class MrFile
    {
        [JsonPropertyName("path")] public string Path { get; init; } = "";
        [JsonPropertyName("hashes")] public Dictionary<string, string>? Hashes { get; init; }
        [JsonPropertyName("env")] public MrEnv? Env { get; init; }
        [JsonPropertyName("downloads")] public List<string>? Downloads { get; init; }
        [JsonPropertyName("fileSize")] public long FileSize { get; init; }
    }

    private sealed class MrEnv
    {
        [JsonPropertyName("client")] public string? Client { get; init; }
    }

    private sealed class CfManifest
    {
        [JsonPropertyName("minecraft")] public CfMinecraft? Minecraft { get; init; }
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("overrides")] public string? Overrides { get; init; }
        [JsonPropertyName("files")] public List<CfManifestFile>? Files { get; init; }
    }

    private sealed class CfMinecraft
    {
        [JsonPropertyName("version")] public string? Version { get; init; }
        [JsonPropertyName("modLoaders")] public List<CfLoader>? ModLoaders { get; init; }
    }

    private sealed class CfLoader
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("primary")] public bool Primary { get; init; }
    }

    private sealed class CfManifestFile
    {
        [JsonPropertyName("projectID")] public int ProjectId { get; init; }
        [JsonPropertyName("fileID")] public int FileId { get; init; }
        [JsonPropertyName("required")] public bool Required { get; init; } = true;
    }

    /// <summary>The CurseForge app's own bookkeeping file, read for just these three lines.</summary>
    private sealed class CurseAppInstance
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
        [JsonPropertyName("gameVersion")] public string? GameVersion { get; init; }
        [JsonPropertyName("baseModLoader")] public CurseAppLoader? BaseModLoader { get; init; }
    }

    private sealed class CurseAppLoader
    {
        [JsonPropertyName("name")] public string? Name { get; init; }
    }

    private sealed class MmcPack
    {
        [JsonPropertyName("components")] public List<MmcComponent>? Components { get; init; }
    }

    private sealed class MmcComponent
    {
        [JsonPropertyName("uid")] public string? Uid { get; init; }
        [JsonPropertyName("version")] public string? Version { get; init; }
    }

    private sealed class WebFileResponse
    {
        [JsonPropertyName("data")] public WebFile? Data { get; init; }
    }

    private sealed class WebFile
    {
        [JsonPropertyName("fileName")] public string? FileName { get; init; }
        [JsonPropertyName("fileLength")] public long FileLength { get; init; }
    }
}
