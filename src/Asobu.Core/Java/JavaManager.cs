using System.Runtime.InteropServices;
using System.Text.Json;
using Asobu.Core.Download;
using Asobu.Core.Minecraft;

namespace Asobu.Core.Java;

/// <summary>A Java runtime Asobu can launch with, whether it downloaded it or merely found it.</summary>
public sealed record JavaInstallation(string ExecutablePath, string Version, int Major, string Source)
{
    public string Label => $"Java {Major}  ·  {Source}";
}

internal sealed class JavaRuntimeEntry
{
    public DownloadRef? Manifest { get; init; }
    public JavaRuntimeVersion? Version { get; init; }
}

internal sealed class JavaRuntimeVersion
{
    public string? Name { get; init; }
}

internal sealed class JavaRuntimeManifest
{
    public Dictionary<string, JavaRuntimeFile> Files { get; init; } = [];
}

internal sealed class JavaRuntimeFile
{
    /// <summary>file | directory | link</summary>
    public string? Type { get; init; }
    public string? Target { get; init; }
    public bool Executable { get; init; }

    /// <summary>Keyed "raw" and "lzma". Asobu takes "raw" and skips the compressed variant.</summary>
    public Dictionary<string, DownloadRef>? Downloads { get; init; }
}

/// <summary>
/// Java, handled for the user. Mojang publishes the exact runtimes the vanilla launcher uses,
/// so Asobu installs those rather than guessing at whatever is on the machine.
/// </summary>
public sealed class JavaManager(HttpClient http, AsobuPaths paths)
{
    private const string AllRuntimesUrl =
        "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private readonly Downloader _downloader = new(http);

    /// <summary>Downloads the runtime a version asks for, unless it is already installed.</summary>
    public async Task<string> EnsureRuntimeAsync(
        JavaVersionRef? required,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        // Versions before 1.17 carry no javaVersion block; they all want the legacy Java 8 runtime.
        var component = required?.Component is { Length: > 0 } named ? named : "jre-legacy";
        var home = Path.Combine(paths.Java, component);

        if (FindExecutable(home) is { } installed) return installed;

        progress?.Report(new InstallProgress($"Fetching {component}", 0));

        var catalogue = await GetJsonAsync<Dictionary<string, Dictionary<string, List<JavaRuntimeEntry>>>>(
            AllRuntimesUrl, cancellationToken).ConfigureAwait(false);

        if (!catalogue.TryGetValue(PlatformKey(), out var components)
            || !components.TryGetValue(component, out var entries)
            || entries.FirstOrDefault()?.Manifest is not { } manifestRef)
        {
            throw new InvalidOperationException(
                $"Mojang publishes no '{component}' runtime for {PlatformKey()}. " +
                "Pick a Java installation manually in Settings.");
        }

        var manifest = await GetJsonAsync<JavaRuntimeManifest>(manifestRef.Url, cancellationToken).ConfigureAwait(false);

        var downloads = new List<DownloadTask>();
        foreach (var (relative, file) in manifest.Files)
        {
            if (file.Type != "file" || file.Downloads?.GetValueOrDefault("raw") is not { } raw) continue;
            downloads.Add(new DownloadTask(
                raw.Url,
                Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar)),
                raw.Sha1,
                raw.Size));
        }

        var reporter = new Progress<DownloadProgress>(p =>
            progress?.Report(new InstallProgress($"Installing Java ({p.Completed} of {p.Total})", p.Fraction)));

        await _downloader.RunAsync(downloads, reporter, cancellationToken).ConfigureAwait(false);

        ApplyFileModes(home, manifest);

        return FindExecutable(home)
            ?? throw new InvalidOperationException($"Installed '{component}' but found no java executable under {home}.");
    }

    /// <summary>Java runtimes already on this machine, for the manual override in Settings.</summary>
    public static IReadOnlyList<JavaInstallation> DetectSystemJava()
    {
        var found = new List<JavaInstallation>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var home in CandidateHomes())
        {
            if (FindExecutable(home) is not { } executable) continue;
            if (!seen.Add(executable)) continue;
            if (ReadRelease(home) is not { } release) continue;

            found.Add(new JavaInstallation(executable, release.Version, release.Major, Path.GetFileName(home)));
        }

        return [.. found.OrderByDescending(j => j.Major)];
    }

    private static IEnumerable<string> CandidateHomes()
    {
        if (Environment.GetEnvironmentVariable("JAVA_HOME") is { Length: > 0 } javaHome)
            yield return javaHome;

        var roots = OperatingSystem.IsWindows()
            ?
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Java"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Eclipse Adoptium"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Microsoft"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Zulu"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Amazon Corretto"),
            ]
            : new[] { "/usr/lib/jvm", "/Library/Java/JavaVirtualMachines" };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;

            string[] children;
            try { children = Directory.GetDirectories(root); }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var child in children) yield return child;
        }
    }

    private static (string Version, int Major)? ReadRelease(string home)
    {
        var release = Path.Combine(home, "release");
        if (!File.Exists(release)) return null;

        foreach (var line in File.ReadLines(release))
        {
            if (!line.StartsWith("JAVA_VERSION=", StringComparison.Ordinal)) continue;

            var value = line["JAVA_VERSION=".Length..].Trim('"', ' ');
            // "1.8.0_402" means 8; "21.0.3" means 21.
            var head = value.Split('.', '_')[0];
            var major = head == "1" ? int.Parse(value.Split('.')[1]) : int.Parse(head);
            return (value, major);
        }

        return null;
    }

    /// <summary>
    /// javaw has no console window, which is what a launcher wants; redirected output still
    /// reaches us. Mojang's macOS runtimes bury the binary inside a bundle, so search as a fallback.
    /// </summary>
    private static string? FindExecutable(string home)
    {
        if (!Directory.Exists(home)) return null;

        var names = OperatingSystem.IsWindows() ? new[] { "javaw.exe", "java.exe" } : ["java"];

        foreach (var name in names)
        {
            var direct = Path.Combine(home, "bin", name);
            if (File.Exists(direct)) return direct;
        }

        foreach (var name in names)
        {
            var match = Directory.EnumerateFiles(home, name, SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null) return match;
        }

        return null;
    }

    private static void ApplyFileModes(string home, JavaRuntimeManifest manifest)
    {
        if (OperatingSystem.IsWindows()) return;

        foreach (var (relative, file) in manifest.Files)
        {
            var path = Path.Combine(home, relative);

            if (file.Type == "link" && file.Target is { } target && !File.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    File.CreateSymbolicLink(path, target);
                }
                catch (IOException) { }
                continue;
            }

            if (file.Executable && File.Exists(path))
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute);
        }
    }

    private static string PlatformKey()
    {
        var arch = RuntimeInformation.OSArchitecture;

        if (OperatingSystem.IsWindows())
            return arch switch
            {
                Architecture.X86 => "windows-x86",
                Architecture.Arm64 => "windows-arm64",
                _ => "windows-x64",
            };

        if (OperatingSystem.IsMacOS())
            return arch == Architecture.Arm64 ? "mac-os-arm64" : "mac-os";

        return arch == Architecture.X86 ? "linux-i386" : "linux";
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        await using var stream = await http.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, MojangJson.Options, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException($"Empty document at {url}.");
    }
}
