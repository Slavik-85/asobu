using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Asobu.Core.Download;

namespace Asobu.Core.Minecraft;

/// <summary>
/// Installs Forge and NeoForge, which do not simply hand over a version document the way Fabric
/// does. Their installer jar carries a build recipe: a set of tool libraries, a table of named
/// data files, and a list of processors to run in order. Those processors split the vanilla jar,
/// merge two sets of mappings, remap the result and finally apply a binary patch — producing the
/// patched client the game actually runs against.
///
/// Both loaders use the same "spec 1" format, NeoForge having forked from Forge, so one
/// implementation covers both and only the coordinates differ.
/// </summary>
public sealed class ForgeInstaller(AsobuPaths paths, Downloader downloader)
{
    /// <summary>Written once the processors have all succeeded, so they run exactly once.</summary>
    private const string MarkerFile = ".loader-installed";

    /// <summary>
    /// Makes sure the loader is fully built on disk, and returns its version document. The
    /// vanilla client jar must already exist — the processors take it as their input.
    /// </summary>
    public async Task<VersionJson> EnsureAsync(
        string installerUrl,
        string javaExecutable,
        string minecraftJar,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var installerPath = await DownloadInstallerAsync(installerUrl, progress, cancellationToken).ConfigureAwait(false);

        using var archive = ZipFile.OpenRead(installerPath);

        var profile = Read<InstallProfile>(archive, "install_profile.json")
            ?? throw new InvalidDataException("The loader installer carries no install_profile.json.");

        // Forge changed shape at 1.13. Newer installers ship the version document as its own file
        // and a list of processors to run; older ones — every 1.12.2 and before — keep it inside
        // the profile as "versionInfo" and have no processors at all, because the whole install
        // was ever only "put this jar where the game will find it".
        //
        // Looked for in both places rather than one, which is what left 1.8.9 reporting that the
        // installer carried no version.json. It carried no such file and never had.
        var version = Read<VersionJson>(archive, "version.json") ?? profile.VersionInfo
            ?? throw new InvalidDataException(
                "The loader installer carries no version document, in either of the two places Forge has kept one.");

        var marker = Path.Combine(paths.VersionDir(version.Id), MarkerFile);
        if (File.Exists(marker)) return version;

        // The old way: one jar out of the installer and into the libraries folder, and nothing to
        // run. Done before the processors below, which such an installer has none of.
        if (profile.Install is { FilePath.Length: > 0, Path.Length: > 0 } legacy)
        {
            progress?.Report(new InstallProgress("Installing the loader", 0));
            ExtractLegacyJar(archive, legacy);
        }

        progress?.Report(new InstallProgress("Fetching loader tools", 0));
        await DownloadToolsAsync(profile, cancellationToken).ConfigureAwait(false);

        var staging = Path.Combine(Path.GetTempPath(), "asobu-loader-" + Guid.NewGuid().ToString("n")[..8]);
        Directory.CreateDirectory(staging);

        try
        {
            var tokens = BuildTokens(profile, archive, staging, installerPath, minecraftJar);
            await RunProcessorsAsync(profile, tokens, javaExecutable, progress, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(staging);
        }

        Directory.CreateDirectory(paths.VersionDir(version.Id));
        await File.WriteAllTextAsync(marker, DateTimeOffset.UtcNow.ToString("o"), cancellationToken).ConfigureAwait(false);

        return version;
    }

    private async Task<string> DownloadInstallerAsync(
        string url, IProgress<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(paths.Cache, "installers");
        Directory.CreateDirectory(directory);

        var destination = Path.Combine(directory, Path.GetFileName(new Uri(url).LocalPath));
        if (File.Exists(destination)) return destination;

        progress?.Report(new InstallProgress("Fetching the loader installer", 0));
        await downloader.RunAsync([new DownloadTask(url, destination)], cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return destination;
    }

    /// <summary>
    /// The installer's own tool libraries. Entries with no URL are outputs the processors will
    /// create themselves, so an absent download here is expected rather than an error.
    /// </summary>
    private async Task DownloadToolsAsync(InstallProfile profile, CancellationToken cancellationToken)
    {
        var tasks = profile.Libraries
            .Select(library => library.Downloads?.Artifact)
            .Where(artifact => artifact is { Url.Length: > 0, Path.Length: > 0 })
            .Select(artifact => new DownloadTask(
                artifact!.Url, System.IO.Path.Combine(paths.Libraries, artifact.Path!), artifact.Sha1, artifact.Size))
            .ToList();

        if (tasks.Count > 0) await downloader.RunAsync(tasks, cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The substitutions every processor argument is written against. Three shapes appear:
    /// "[maven:coords]" is a file in the library tree, "/path" is a file inside the installer jar
    /// that has to be unpacked first, and "'quoted'" is a literal such as a checksum.
    /// </summary>
    private Dictionary<string, string> BuildTokens(
        InstallProfile profile, ZipArchive archive, string staging, string installerPath, string minecraftJar)
    {
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["MINECRAFT_JAR"] = minecraftJar,
            ["SIDE"] = "client",
            ["ROOT"] = paths.Root,
            ["INSTALLER"] = installerPath,
            ["LIBRARY_DIR"] = paths.Libraries,
        };

        foreach (var (key, entry) in profile.Data)
        {
            if (entry.Client is not { Length: > 0 } value) continue;

            tokens[key] = value[0] switch
            {
                '[' => Path.Combine(paths.Libraries, Maven.PathFor(value[1..^1])),
                '/' => Extract(archive, value, staging),
                '\'' => value.Trim('\''),
                _ => value,
            };
        }

        return tokens;
    }

    private static string Extract(ZipArchive archive, string entryPath, string staging)
    {
        var name = entryPath.TrimStart('/');
        var entry = archive.GetEntry(name)
            ?? throw new InvalidDataException($"The loader installer is missing '{name}'.");

        var destination = Path.Combine(staging, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        entry.ExtractToFile(destination, overwrite: true);

        return destination;
    }

    private async Task RunProcessorsAsync(
        InstallProfile profile,
        Dictionary<string, string> tokens,
        string javaExecutable,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Server-only steps build artifacts a client never loads; running them would just be
        // several minutes of work thrown away.
        var applicable = profile.Processors
            .Where(p => p.Sides is null || p.Sides.Contains("client", StringComparer.OrdinalIgnoreCase))
            .ToList();

        for (var index = 0; index < applicable.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var processor = applicable[index];
            var outputs = processor.Outputs.ToDictionary(
                kv => Substitute(kv.Key, tokens), kv => Substitute(kv.Value, tokens));

            if (outputs.Count > 0 && outputs.All(IsUpToDate)) continue;

            progress?.Report(new InstallProgress(
                $"Building the loader ({index + 1} of {applicable.Count})",
                (double)index / applicable.Count));

            var jar = Path.Combine(paths.Libraries, Maven.PathFor(processor.Jar));
            if (!File.Exists(jar))
                throw new InvalidDataException($"The loader tool '{processor.Jar}' was not downloaded.");

            var classpath = processor.Classpath
                .Select(coords => Path.Combine(paths.Libraries, Maven.PathFor(coords)))
                .Append(jar)
                .ToList();

            await RunAsync(
                javaExecutable,
                classpath,
                MainClassOf(jar),
                [.. processor.Args.Select(a => Substitute(a, tokens))],
                cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>A processor can be skipped when everything it would produce is already correct.</summary>
    private static bool IsUpToDate(KeyValuePair<string, string> output)
    {
        if (!File.Exists(output.Key)) return false;
        if (output.Value.Length == 0) return true;

        return Downloader.Sha1Async(output.Key).GetAwaiter().GetResult()
            .Equals(output.Value, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task RunAsync(
        string javaExecutable,
        IReadOnlyList<string> classpath,
        string mainClass,
        IReadOnlyList<string> args,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(javaExecutable)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        startInfo.ArgumentList.Add("-cp");
        startInfo.ArgumentList.Add(string.Join(Path.PathSeparator, classpath));
        startInfo.ArgumentList.Add(mainClass);
        foreach (var argument in args) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Could not start {mainClass}.");

        // Read both streams while waiting: a processor that fills a pipe buffer with nobody
        // draining it would block forever instead of finishing.
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode == 0) return;

        var detail = (await error.ConfigureAwait(false)) is { Length: > 0 } text
            ? text
            : await output.ConfigureAwait(false);

        throw new InvalidOperationException(
            $"The loader build step {mainClass} failed with exit code {process.ExitCode}." +
            (detail is { Length: > 0 } ? " " + Tail(detail) : ""));
    }

    private static string Tail(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length <= 400 ? trimmed : "…" + trimmed[^400..];
    }

    /// <summary>
    /// Each tool declares its entry point in its own manifest, so nothing here has to carry a
    /// hardcoded list of class names that would rot the next time a tool is renamed.
    /// </summary>
    private static string MainClassOf(string jarPath)
    {
        using var archive = ZipFile.OpenRead(jarPath);

        var manifest = archive.GetEntry("META-INF/MANIFEST.MF")
            ?? throw new InvalidDataException($"'{Path.GetFileName(jarPath)}' has no manifest.");

        using var reader = new StreamReader(manifest.Open());

        while (reader.ReadLine() is { } line)
            if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase))
                return line["Main-Class:".Length..].Trim();

        throw new InvalidDataException($"'{Path.GetFileName(jarPath)}' declares no Main-Class.");
    }

    /// <summary>
    /// Replaces every {TOKEN} and resolves a bare [maven] coordinate. Tokens can appear inside a
    /// longer string, so this is a scan rather than a whole-value match.
    /// </summary>
    private string Substitute(string value, Dictionary<string, string> tokens)
    {
        if (value.Length > 1 && value[0] == '[' && value[^1] == ']')
            return Path.Combine(paths.Libraries, Maven.PathFor(value[1..^1]));

        if (!value.Contains('{')) return value;

        foreach (var (key, replacement) in tokens)
            value = value.Replace("{" + key + "}", replacement, StringComparison.Ordinal);

        return value;
    }

    private static T? Read<T>(ZipArchive archive, string entryName)
    {
        if (archive.GetEntry(entryName) is not { } entry) return default;

        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, MojangJson.Options);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (IOException)
        {
            // Staging lives in the temp folder; the OS will get to it.
        }
    }

    /// <summary>
    /// Puts the loader jar an old installer carries into the libraries folder, under the Maven
    /// coordinate the profile names.
    ///
    /// Nothing downloads it: the jar is inside the installer already, which is why an old Forge
    /// install works offline once the installer is in hand.
    /// </summary>
    private void ExtractLegacyJar(ZipArchive archive, LegacyInstall install)
    {
        if (archive.GetEntry(install.FilePath) is not { } entry)
            throw new InvalidDataException(
                $"The loader installer does not contain {install.FilePath}, which its profile says it does.");

        var destination = Path.Combine(paths.Libraries, Maven.PathFor(install.Path));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        // Written even where one is already there: a half-extracted jar from an interrupted
        // install would otherwise be kept forever, and the file is a few megabytes.
        entry.ExtractToFile(destination, overwrite: true);
    }

    private sealed class InstallProfile
    {
        public List<Library> Libraries { get; init; } = [];
        public List<Processor> Processors { get; init; } = [];
        public Dictionary<string, DataEntry> Data { get; init; } = [];

        /// <summary>
        /// The version document itself, for installers old enough to keep it here.
        ///
        /// Forge changed shape at 1.13. Before that there was no version.json in the jar at all
        /// and no processors either: the whole install was "put this jar in libraries", and the
        /// version document sat inside the profile under this name.
        /// </summary>
        public VersionJson? VersionInfo { get; init; }

        /// <summary>What the old installers had instead of processors.</summary>
        public LegacyInstall? Install { get; init; }
    }

    /// <summary>
    /// The pre-1.13 install step: one jar, carried inside the installer, that belongs in the
    /// libraries folder under a Maven coordinate.
    /// </summary>
    private sealed class LegacyInstall
    {
        /// <summary>Where it goes: "net.minecraftforge:forge:1.8.9-11.15.1.2318-1.8.9".</summary>
        public string Path { get; init; } = "";

        /// <summary>What it is called inside the installer.</summary>
        public string FilePath { get; init; } = "";
    }

    private sealed class DataEntry
    {
        public string? Client { get; init; }
        public string? Server { get; init; }
    }

    private sealed class Processor
    {
        public string Jar { get; init; } = "";
        public List<string> Classpath { get; init; } = [];
        public List<string> Args { get; init; } = [];
        public List<string>? Sides { get; init; }

        [JsonPropertyName("outputs")]
        public Dictionary<string, string> Outputs { get; init; } = [];
    }
}
