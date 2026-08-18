using System.IO.Compression;
using System.Text.Json;
using Asobu.Core.Download;

namespace Asobu.Core.Minecraft;

public sealed record InstallProgress(string Stage, double Fraction);

/// <summary>
/// Turns a version id into a complete, verified set of files on disk. Everything comes
/// straight from Mojang's CDN; nothing is proxied.
/// </summary>
public sealed class MinecraftInstaller(HttpClient http, AsobuPaths paths, MojangMeta meta)
{
    private const string LibraryFallbackRepository = "https://libraries.minecraft.net/";

    private readonly Downloader _downloader = new(http);

    public async Task<VersionJson> InstallAsync(
        string versionId,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        paths.EnsureCreated();
        progress?.Report(new InstallProgress("Reading version metadata", 0));

        var version = await meta.GetResolvedVersionAsync(versionId, cancellationToken).ConfigureAwait(false);
        var platform = RuleContext.Current;

        Directory.CreateDirectory(paths.VersionDir(version.Id));
        await File.WriteAllTextAsync(
            paths.VersionJsonFile(version.Id),
            JsonSerializer.Serialize(version, MojangJson.Options),
            cancellationToken).ConfigureAwait(false);

        var downloads = new List<DownloadTask>();

        if (version.ClientJar is { } client)
            downloads.Add(new DownloadTask(client.Url, paths.VersionJarFile(version.Id), client.Sha1, client.Size));

        foreach (var library in version.Libraries.Where(l => RuleEvaluator.Allows(l, platform)))
        {
            if (LibraryDownload(library) is { } artifact) downloads.Add(artifact);
            if (NativeDownload(library, platform) is { } native) downloads.Add(native);
        }

        if (version.Logging?.Client?.File is { } logConfig)
            downloads.Add(new DownloadTask(
                logConfig.Url,
                Path.Combine(paths.LogConfigs, logConfig.Id ?? "log4j2.xml"),
                logConfig.Sha1,
                logConfig.Size));

        progress?.Report(new InstallProgress("Reading asset index", 0));
        var assetIndex = await LoadAssetIndexAsync(version, cancellationToken).ConfigureAwait(false);
        if (assetIndex is not null)
            downloads.AddRange(assetIndex.Objects.Values
                .DistinctBy(o => o.Hash)
                .Select(o => new DownloadTask(o.Url, Path.Combine(paths.AssetObjects, o.RelativePath), o.Hash, o.Size)));

        var downloadProgress = new Progress<DownloadProgress>(p =>
            progress?.Report(new InstallProgress(
                p.Total == 0 ? "Already installed" : $"Downloading {p.Completed} of {p.Total}", p.Fraction)));

        await _downloader.RunAsync(downloads, downloadProgress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new InstallProgress("Unpacking native libraries", 1));
        ExtractNatives(version, platform);

        if (assetIndex is { Virtual: true } or { MapToResources: true })
        {
            progress?.Report(new InstallProgress("Preparing legacy assets", 1));
            BuildVirtualAssets(version, assetIndex);
        }

        progress?.Report(new InstallProgress("Ready", 1));
        return version;
    }

    /// <summary>Everything the JVM needs on -cp, in version-JSON order.</summary>
    public IReadOnlyList<string> BuildClasspath(VersionJson version, RuleContext platform) =>
    [
        .. version.Libraries
            .Where(l => RuleEvaluator.Allows(l, platform))
            .Where(l => l.Natives is null || l.Downloads?.Artifact is not null)
            .Select(LibraryFile),
        paths.VersionJarFile(version.Id),
    ];

    private string LibraryFile(Library library) =>
        Path.Combine(paths.Libraries, library.Downloads?.Artifact?.Path ?? Maven.PathFor(library.Name));

    private DownloadTask? LibraryDownload(Library library)
    {
        if (library.Downloads?.Artifact is { } artifact)
            return new DownloadTask(artifact.Url, LibraryFile(library), artifact.Sha1, artifact.Size);

        // Natives-only libraries (pre-1.19 LWJGL, jinput) publish classifier payloads and no plain
        // jar at all. Asking Mojang's repository for one returns 404 and fails the whole install.
        if (library.Natives is not null) return null;

        // Loaders publish bare Maven coordinates plus a repository root instead of a downloads block.
        if (library.Url is { Length: > 0 } repository)
        {
            var relative = Maven.PathFor(library.Name).Replace('\\', '/');
            return new DownloadTask(repository.TrimEnd('/') + "/" + relative, LibraryFile(library));
        }

        return new DownloadTask(
            LibraryFallbackRepository + Maven.PathFor(library.Name).Replace('\\', '/'),
            LibraryFile(library));
    }

    private DownloadTask? NativeDownload(Library library, RuleContext platform)
    {
        if (NativeClassifier(library, platform) is not { } classifier) return null;
        if (library.Downloads?.Classifiers?.TryGetValue(classifier, out var native) is not true) return null;

        return new DownloadTask(
            native.Url,
            Path.Combine(paths.Libraries, native.Path ?? Maven.PathFor($"{library.Name}:{classifier}")),
            native.Sha1,
            native.Size);
    }

    /// <summary>Legacy natives mapping, e.g. "natives-windows-${arch}" on a 64-bit PC.</summary>
    private static string? NativeClassifier(Library library, RuleContext platform)
    {
        if (library.Natives?.TryGetValue(platform.OsName, out var template) is not true) return null;
        return template.Replace("${arch}", platform.OsArch == "x86" ? "32" : "64");
    }

    private void ExtractNatives(VersionJson version, RuleContext platform)
    {
        var target = paths.NativesDir(version.Id);
        Directory.CreateDirectory(target);

        // Modern versions ship no native payloads at all — LWJGL unpacks itself at runtime into
        // the subdirectories named by the JVM arguments. Creating the folder is the whole job.
        foreach (var library in version.Libraries.Where(l => RuleEvaluator.Allows(l, platform)))
        {
            if (NativeDownload(library, platform) is not { } native) continue;
            if (!File.Exists(native.Destination)) continue;

            using var archive = ZipFile.OpenRead(native.Destination);
            foreach (var entry in archive.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.Length == 0) continue;
                if (library.Extract?.Exclude.Any(prefix => entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) == true)
                    continue;

                // Native archives are flat by convention; flatten defensively so a crafted
                // entry name cannot escape the natives directory.
                var destination = Path.Combine(target, Path.GetFileName(entry.FullName));
                if (File.Exists(destination)) continue;

                entry.ExtractToFile(destination, overwrite: true);
            }
        }
    }

    private async Task<AssetIndexFile?> LoadAssetIndexAsync(VersionJson version, CancellationToken cancellationToken)
    {
        if (version.AssetIndex is not { } index) return null;

        var file = Path.Combine(paths.AssetIndexes, index.Id + ".json");
        await _downloader
            .RunAsync([new DownloadTask(index.Url, file, index.Sha1, index.Size)], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        await using var stream = File.OpenRead(file);
        return await JsonSerializer.DeserializeAsync<AssetIndexFile>(stream, MojangJson.Options, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Pre-1.7.3 expects a real folder tree instead of the hashed object store.</summary>
    private void BuildVirtualAssets(VersionJson version, AssetIndexFile index)
    {
        var root = Path.Combine(paths.AssetsVirtual, version.AssetIndex!.Id);

        foreach (var (name, asset) in index.Objects)
        {
            var destination = Path.Combine(root, name.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(destination)) continue;

            var source = Path.Combine(paths.AssetObjects, asset.RelativePath);
            if (!File.Exists(source)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
    }

    public string VirtualAssetsDir(VersionJson version) =>
        Path.Combine(paths.AssetsVirtual, version.AssetIndex?.Id ?? "legacy");
}
