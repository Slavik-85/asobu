using Asobu.Core;
using Asobu.Core.Instances;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>How unfinished a build each installed mod may be offered as its update.</summary>
public class UpdateChannelTests
{
    private static ModEntry Mod(string fileName) => new($"C:/mods/{fileName}", fileName, fileName, "", null, 1, true, null);

    private static Instance Pack(params string[] shipped) =>
        new() { Id = "x", Name = "Pack", MinecraftVersion = "1.21.1", PackMods = [.. shipped] };

    [Fact]
    public void OffersAPacksOwnModsReleasesOnlyEvenWhenItShippedABeta()
    {
        var instance = Pack("sodium-0.6.0-beta.2.jar");

        Assert.Equal(ModChannel.Release, AsobuLauncher.Allowed(instance, Mod("sodium-0.6.0-beta.2.jar"), ModChannel.Beta));
    }

    [Fact]
    public void OffersBetasAfterABetaSomebodyPutInThemselves()
    {
        var instance = Pack("sodium-0.6.0.jar");

        Assert.Equal(ModChannel.Beta, AsobuLauncher.Allowed(instance, Mod("iris-1.8.0-beta.1.jar"), ModChannel.Beta));
    }

    [Fact]
    public void KeepsAnInstalledReleaseOnReleases()
    {
        var instance = new Instance { Id = "y", Name = "Mine", MinecraftVersion = "1.21.1" };

        Assert.Equal(ModChannel.Release, AsobuLauncher.Allowed(instance, Mod("lithium-0.13.0.jar"), ModChannel.Release));
    }
}
