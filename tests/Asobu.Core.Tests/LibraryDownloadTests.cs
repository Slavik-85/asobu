using Asobu.Core;
using Asobu.Core.Minecraft;

namespace Asobu.Core.Tests;

/// <summary>
/// Which of a version's libraries are actually things to go and fetch.
///
/// From a real one, caught in review before it shipped. Forge from 1.21 on lists its own patched
/// client among the libraries — with a path, a hash, a size, and an empty url — because the file
/// at that path is what its own build produces rather than something anybody publishes:
///
///     "name": "net.minecraftforge:forge:26.2-65.1.0:client",
///     "downloads": { "artifact": { "path": "net/minecraftforge/forge/…-client.jar",
///                                  "url": "", "sha1": "f9ef709f…", "size": 79191195 } }
///
/// That never mattered while the loader build could only succeed: the processors wrote the file,
/// and the "is it already here?" check skipped it on size. It matters now that a failed build
/// carries on to a fallback, because then the file genuinely is not there, an empty url is handed
/// to the http client, and the install dies on "An invalid request URI was provided" — burying
/// the loader failure that was the actual problem under an unrelated network error.
/// </summary>
public class LibraryDownloadTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-libs-").FullName;
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        Directory.Delete(_root, recursive: true);
    }

    private MinecraftInstaller Installer =>
        new(_http, new AsobuPaths(_root), new MojangMeta(_http));

    private static Library Published(string name, string url) => new()
    {
        Name = name,
        Downloads = new LibraryDownloads
        {
            Artifact = new DownloadRef { Url = url, Path = Maven.PathFor(name).Replace('\\', '/') },
        },
    };

    [Fact]
    public void A_library_that_is_published_somewhere_is_fetched()
    {
        const string url = "https://libraries.minecraft.net/org/ow2/asm/asm/9.7.1/asm-9.7.1.jar";

        Assert.Equal(url, Installer.LibraryDownload(Published("org.ow2.asm:asm:9.7.1", url))?.Url);
    }

    /// <summary>
    /// The one that bit. There is nowhere to fetch it from, so it is not a download — it is an
    /// output, and the same rule the loader installer has always applied to its own tool
    /// libraries applies here.
    /// </summary>
    [Fact]
    public void A_library_with_no_url_is_an_output_and_is_never_fetched()
    {
        Assert.Null(Installer.LibraryDownload(Published("net.minecraftforge:forge:26.2-65.1.0:client", "")));
    }

    /// <summary>
    /// A library with no downloads block at all is a different thing: loaders publish bare Maven
    /// coordinates and a repository to find them in, and those are still fetched.
    /// </summary>
    [Fact]
    public void A_library_that_names_only_a_repository_is_still_fetched()
    {
        var task = Installer.LibraryDownload(
            new Library { Name = "net.fabricmc:tiny-mappings-parser:0.3.0", Url = "https://maven.fabricmc.net/" });

        Assert.StartsWith("https://maven.fabricmc.net/", task?.Url);
    }

    /// <summary>And one with neither is looked for where Mojang keeps everything else.</summary>
    [Fact]
    public void A_library_with_nothing_said_about_it_falls_back_to_mojangs_repository()
    {
        var task = Installer.LibraryDownload(new Library { Name = "com.mojang:patchy:2.2.10" });

        Assert.StartsWith("https://libraries.minecraft.net/", task?.Url);
    }

    /// <summary>
    /// A natives-only library, which publishes classifier payloads and no plain jar. Asking
    /// Mojang for one returns a 404 and fails the whole install.
    /// </summary>
    [Fact]
    public void A_natives_only_library_has_no_plain_jar_to_fetch()
    {
        var task = Installer.LibraryDownload(new Library
        {
            Name = "org.lwjgl.lwjgl:lwjgl-platform:2.9.4",
            Natives = new Dictionary<string, string> { ["windows"] = "natives-windows" },
        });

        Assert.Null(task);
    }
}
