using Asobu.Core.Minecraft;

namespace Asobu.Core.Tests;

/// <summary>
/// Merging the library lists when a loader's version is flattened onto the vanilla one.
///
/// The library list is the part that bites. A loader and the game routinely want different builds
/// of the same library, and putting both on the classpath is not a preference the JVM resolves —
/// Fabric checks for it by hand and refuses to start.
/// </summary>
public class LibraryMergeTests
{
    private static Library Lib(string name) => new() { Name = name };

    private static VersionJson Version(string id, string? inherits, params string[] libraries) => new()
    {
        Id = id,
        InheritsFrom = inherits,
        MainClass = inherits is null ? "net.minecraft.client.main.Main" : null,
        Libraries = [.. libraries.Select(Lib)],
    };

    private static VersionJson Resolve(params VersionJson[] chain)
    {
        var byId = chain.ToDictionary(version => version.Id, StringComparer.Ordinal);

        return VersionResolver
            .ResolveAsync(chain[0].Id, (id, _) => Task.FromResult(byId[id]))
            .GetAwaiter().GetResult();
    }

    [Fact]
    public void KeepsOneBuildOfALibraryTheChainDisagreesAbout()
    {
        // The crash this exists for, in its own words:
        //   duplicate ASM classes found on classpath: .../asm-9.10.1.jar, .../asm-9.6.jar
        var merged = Resolve(
            Version("fabric-1.21.11", "1.21.11", "org.ow2.asm:asm:9.10.1"),
            Version("1.21.11", null, "org.ow2.asm:asm:9.6"));

        var asm = merged.Libraries.Where(l => l.Name.StartsWith("org.ow2.asm:asm:")).ToList();

        Assert.Single(asm);
        Assert.Equal("org.ow2.asm:asm:9.10.1", asm[0].Name);
    }

    [Fact]
    public void TakesTheNewerBuildEvenWhenTheGameIsTheOneAskingForIt()
    {
        // The disagreement runs both ways: a loader can be older than the game it is run against.
        var merged = Resolve(
            Version("fabric-1.21.11", "1.21.11", "org.ow2.asm:asm:9.6"),
            Version("1.21.11", null, "org.ow2.asm:asm:9.10.1"));

        Assert.Equal("org.ow2.asm:asm:9.10.1", Assert.Single(merged.Libraries).Name);
    }

    [Fact]
    public void LeavesTheLoaderAheadOfVanillaOnTheClasspath()
    {
        // Order still decides whose patched classes win, so the loader's entries have to keep
        // their place even when a version was taken from the other end of the chain.
        var merged = Resolve(
            Version("fabric-1.21.11", "1.21.11", "net.fabricmc:fabric-loader:0.17.4", "org.ow2.asm:asm:9.6"),
            Version("1.21.11", null, "com.mojang:logging:1.2.7", "org.ow2.asm:asm:9.10.1"));

        Assert.Equal(
            ["net.fabricmc:fabric-loader:0.17.4", "org.ow2.asm:asm:9.10.1", "com.mojang:logging:1.2.7"],
            merged.Libraries.Select(l => l.Name));
    }

    [Fact]
    public void KeepsNativesForDifferentPlatformsApart()
    {
        // Same coordinate, different classifier. These are different files, not two builds of
        // one — folding them together would strip the natives for every platform but one.
        var merged = Resolve(
            Version("fabric-1.21.11", "1.21.11"),
            Version("1.21.11", null,
                "org.lwjgl:lwjgl:3.3.3",
                "org.lwjgl:lwjgl:3.3.3:natives-windows",
                "org.lwjgl:lwjgl:3.3.3:natives-linux"));

        Assert.Equal(3, merged.Libraries.Count);
    }

    [Fact]
    public void LeavesAVanillaVersionAlone()
    {
        var merged = Resolve(Version("1.21.11", null, "com.mojang:logging:1.2.7"));

        Assert.Equal("1.21.11", merged.Id);
        Assert.Equal("com.mojang:logging:1.2.7", Assert.Single(merged.Libraries).Name);
    }

    [Fact]
    public void DoesNotFoldTwoDifferentArtifactsTogether()
    {
        var merged = Resolve(
            Version("fabric-1.21.11", "1.21.11", "org.ow2.asm:asm:9.10.1"),
            Version("1.21.11", null, "org.ow2.asm:asm-tree:9.10.1", "org.ow2.asm:asm-analysis:9.10.1"));

        Assert.Equal(3, merged.Libraries.Count);
    }

    [Fact]
    public void KeepsGoingWhenAVersionCannotBeRead()
    {
        // A coordinate with no version is nothing to compare, and must not throw or win.
        var merged = Resolve(
            Version("fabric-1.21.11", "1.21.11", "org.ow2.asm:asm"),
            Version("1.21.11", null, "org.ow2.asm:asm:9.6"));

        Assert.Equal("org.ow2.asm:asm:9.6", Assert.Single(merged.Libraries).Name);
    }
}
