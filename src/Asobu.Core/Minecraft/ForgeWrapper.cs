using Asobu.Core.Download;

namespace Asobu.Core.Minecraft;

/// <summary>
/// The other way to build Forge: inside the game, at launch, instead of out here beforehand.
///
/// Asobu's own way is to read the installer's recipe and run its processors as Java subprocesses
/// while installing — see <see cref="ForgeInstaller"/>. That works, and when it doesn't, it
/// doesn't in a way nobody can act on: the instance simply never launches again. Several things
/// can do it. Security software objects to a launcher spawning java.exe with a long generated
/// command line. A processor shape Forge introduced after this code was written goes unhandled.
/// The temp folder the staging directory lives in is redirected or locked down.
///
/// ForgeWrapper is the approach Prism Launcher uses instead, and is why instances that will not
/// start elsewhere start there. It is a small jar that becomes the main class: at launch it does
/// the same patching using the installer's own classes, in the game's own JVM, and then hands
/// over to Forge. No subprocess, no staging directory, no command line, and the handling of each
/// Forge generation is maintained by people who follow Forge.
///
/// Used here only after the ordinary way has already failed. That is the whole of its safety: an
/// instance that installs normally never touches any of this, so nothing that works today can be
/// broken by it, and the only instances that reach it are ones that were not going to run at all.
/// </summary>
public static class ForgeWrapper
{
    /// <summary>
    /// The build, pinned. Prism's fork rather than the original, because the original stopped at
    /// Forge's 2022 shape and this one follows NeoForge and current Forge — and because pinning
    /// one known file, with its hash, is the only honest way to depend on somebody else's jar.
    /// </summary>
    private const string Build = "prism-2026-08-01";

    public const string Coordinates = $"io.github.zekerzhayard:ForgeWrapper:{Build}";

    private const string Url =
        $"https://files.prismlauncher.org/maven/io/github/zekerzhayard/ForgeWrapper/{Build}/ForgeWrapper-{Build}.jar";

    /// <summary>Verified on download, so a wrong or tampered file is refused rather than launched.</summary>
    private const string Sha1 = "852b7e59748da1512d40e38407eadb1f0031a996";

    private const long Size = 29800;

    public const string MainClass = "io.github.zekerzhayard.forgewrapper.installer.Main";

    /// <summary>
    /// Fetches the jar, or says it could not. Never throws: this is the second attempt at
    /// something that has already failed once, and a failure here must leave the first failure
    /// as the one reported — that is the one that explains the instance.
    /// </summary>
    public static async Task<bool> TryFetchAsync(
        AsobuPaths paths, Downloader downloader, CancellationToken cancellationToken = default)
    {
        try
        {
            await downloader.RunAsync(
                [new DownloadTask(Url, JarPath(paths), Sha1, Size)],
                cancellationToken: cancellationToken).ConfigureAwait(false);

            return File.Exists(JarPath(paths));
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            return false;
        }
    }

    public static string JarPath(AsobuPaths paths) =>
        Path.Combine(paths.Libraries, Maven.PathFor(Coordinates));

    /// <summary>
    /// Whether a document launches through the wrapper — which is to say, whether it is one the
    /// ordinary loader build failed to produce.
    ///
    /// Asked in two places that must agree: wrapping refuses to wrap one twice, and the launcher
    /// refuses to remember one as an instance's settled version. The second is the one that
    /// matters, because remembering it would make a failure that might have been a passing thing
    /// into the way that instance works from now on.
    /// </summary>
    public static bool IsWrapped(VersionJson version) => version.MainClass == MainClass;

    /// <summary>
    /// The same version document, launched through the wrapper.
    ///
    /// Three properties and a main class, which is the whole of the wrapper's interface. The
    /// arguments the document already carries are kept exactly as they are — Forge's module path,
    /// its ignore list, its --fml switches — because the wrapper reads those same switches to
    /// work out which Forge it is building, and then hands them on to Forge itself.
    ///
    /// Pure: it builds a new document and changes nothing on disk, so what it produces can be
    /// looked at and compared before anything is launched with it.
    ///
    /// Null for a document the wrapper cannot do anything with — see below.
    /// </summary>
    public static VersionJson? Wrap(VersionJson version, AsobuPaths paths, string installerPath, string minecraftJar)
    {
        // Nothing to wrap. Forge before 1.13 has no structured arguments and no processors — the
        // whole install was ever only unpacking one jar — and the wrapper does not support those
        // versions. Refused here rather than at the caller so it cannot be got wrong elsewhere:
        // wrapping one anyway would manufacture an arguments block holding nothing but these
        // three properties, and a document with arguments takes a different branch in the launch
        // builder, which would then emit no classpath and none of the game's own arguments.
        if (version.Arguments is null) return null;

        // Already wrapped. Wrapping twice would put the properties on twice and leave the main
        // class pointing at the wrapper's own main, which is where it already points.
        if (IsWrapped(version)) return version;

        var properties = new ConditionalArgument
        {
            Values =
            [
                $"-Dforgewrapper.librariesDir={paths.Libraries}",
                $"-Dforgewrapper.installer={installerPath}",
                $"-Dforgewrapper.minecraft={minecraftJar}",
            ],
        };

        var arguments = new Arguments
        {
            Game = version.Arguments?.Game ?? [],

            // Appended, so anything the document says about the module path is already in place
            // by the time these are read.
            Jvm = [.. version.Arguments?.Jvm ?? [], properties],
        };

        return new VersionJson
        {
            Id = version.Id,
            InheritsFrom = version.InheritsFrom,
            ClientJarVersionId = version.ClientJarVersionId,
            Type = version.Type,
            MainClass = MainClass,
            Assets = version.Assets,
            AssetIndex = version.AssetIndex,
            JavaVersion = version.JavaVersion,
            Downloads = version.Downloads,

            // The wrapper has to be on the classpath to be the main class. With its real download
            // block, so the installer fetches and verifies it like any other library rather than
            // guessing at a URL for it.
            Libraries =
            [
                .. version.Libraries,
                new Library
                {
                    Name = Coordinates,
                    Downloads = new LibraryDownloads
                    {
                        Artifact = new DownloadRef
                        {
                            Url = Url,
                            Path = Maven.PathFor(Coordinates).Replace('\\', '/'),
                            Sha1 = Sha1,
                            Size = Size,
                        },
                    },
                },
            ],
            Arguments = arguments,
            MinecraftArguments = version.MinecraftArguments,
            Logging = version.Logging,
            ComplianceLevel = version.ComplianceLevel,
            ReleaseTime = version.ReleaseTime,
            MinimumLauncherVersion = version.MinimumLauncherVersion,
        };
    }
}
