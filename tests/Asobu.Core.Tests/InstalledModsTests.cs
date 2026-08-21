using System.Reflection;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Whether a search result is recognised as something the instance already has.
///
/// The matching itself is exercised rather than the folder scan, because the interesting part is
/// the naming: shops and jars disagree about punctuation and about how much of a name to use, and
/// every miss here is an offer to install a second copy of something.
/// </summary>
public class InstalledModsTests
{
    /// <summary>
    /// Builds the index straight from entries, which is what For() does once it has scanned. The
    /// constructor is private because scanning is the only sane way in from application code;
    /// reaching past it here keeps the test off the filesystem.
    /// </summary>
    private static InstalledMods Index(params ModEntry[] entries) =>
        (InstalledMods)Activator.CreateInstance(
            typeof(InstalledMods),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [entries.AsEnumerable()],
            culture: null)!;

    private static ModEntry Jar(string modId, string name) =>
        new($"C:/mods/{name}.jar", $"{name}.jar", name, "someone", modId, 1024, true, null);

    private static CatalogueMod Listed(string title, string modrinthSlug, string? curseSlug = null) =>
        new(
            new ModListing(ModProvider.Modrinth, "abc123", title, "someone", "", null, 10,
                $"https://modrinth.com/mod/{modrinthSlug}"),
            curseSlug is null
                ? null
                : new ModListing(ModProvider.CurseForge, "999", title, "someone", "", null, 10,
                    $"https://www.curseforge.com/minecraft/mc-mods/{curseSlug}"));

    [Fact]
    public void MatchesTheSlugAgainstTheModIdTheJarDeclares()
    {
        var installed = Index(Jar("sodium", "sodium-fabric-0.9.1"));

        Assert.True(installed.Has(Listed("Sodium", "sodium")));
    }

    [Fact]
    public void IgnoresThePunctuationTheTwoDisagreeAbout()
    {
        // Cloth Config's jar says cloth_config; its page says cloth-config. Same mod.
        var installed = Index(Jar("cloth_config", "cloth-config-11.1.118-fabric"));

        Assert.True(installed.Has(Listed("Cloth Config API", "cloth-config")));
    }

    [Fact]
    public void MatchesWhenOnlyTheCurseForgeSlugAgrees()
    {
        var installed = Index(Jar("jei", "jei-1.21-19.21.0.247"));

        // A mod whose Modrinth slug and CurseForge slug differ; either is enough.
        Assert.True(installed.Has(Listed("Just Enough Items", "just-enough-items", "jei")));
    }

    [Fact]
    public void MatchesOnTheDisplayNameWhenNothingElseDoes()
    {
        // Resource packs and shaders declare no mod id, so the file's own name is all there is.
        var installed = Index(new ModEntry(
            "C:/resourcepacks/Faithful.zip", "Faithful.zip", "Faithful", "", null, 2048, true, null));

        Assert.True(installed.Has(Listed("Faithful", "faithful-32x")));
    }

    [Fact]
    public void DoesNotClaimAModTheInstanceHasNot()
    {
        var installed = Index(Jar("sodium", "sodium-fabric-0.9.1"));

        Assert.False(installed.Has(Listed("Iris Shaders", "iris")));
    }

    [Fact]
    public void DoesNotMatchOnAShredOfANameTwoModsShare()
    {
        // "Create" and "Create: Steam 'n' Rails" are different mods and must stay so — a
        // substring match here would fold half a catalogue into one entry.
        var installed = Index(Jar("create", "create-1.21.1-6.0.4"));

        Assert.False(installed.Has(Listed("Create: Steam 'n' Rails", "create-steam-n-rails")));
    }

    [Fact]
    public void FindsTheFileSoItCanBeReplaced()
    {
        var installed = Index(Jar("sodium", "sodium-fabric-0.9.1"));

        var found = installed.Find(Listed("Sodium", "sodium"));

        // Installing another build has to know which file to take out afterwards.
        Assert.Equal("sodium-fabric-0.9.1.jar", found?.FileName);
    }

    [Fact]
    public void RecognisesAModThatWasTurnedOff()
    {
        // A disabled jar is still installed. Offering to add it again would leave two copies,
        // one of them .disabled, which is how a mods folder becomes a junk drawer.
        var installed = Index(new ModEntry(
            "C:/mods/sodium.jar.disabled", "sodium.jar", "Sodium", "", "sodium", 1024, false, null));

        Assert.True(installed.Has(Listed("Sodium", "sodium")));
    }

    [Fact]
    public void AnEmptyInstanceHasNothing()
    {
        Assert.False(InstalledMods.Empty.Has(Listed("Sodium", "sodium")));
    }

    [Theory]
    [InlineData("fabric-api-0.115.0+1.21.1.jar", "fabric-api")]
    [InlineData("sodium-fabric-0.9.1.jar", "sodium-fabric")]
    [InlineData("cloth-config-11.1.118-fabric.jar", "cloth-config")]
    public void ReadsTheProjectOutOfAFileName(string fileName, string expected)
    {
        Assert.Equal(expected, InstalledMods.ProjectStem(fileName));
    }

    [Fact]
    public void FindsTheOlderBuildOfADependency()
    {
        var installed = new[] { Jar("fabric-api", "fabric-api-0.110.0+1.21") };

        var older = InstalledMods.OlderBuildOf("fabric-api-0.115.0+1.21.1.jar", installed);

        Assert.Equal("fabric-api-0.110.0+1.21.jar", older?.FileName);
    }

    [Fact]
    public void DoesNotTreatTheSameBuildAsAnOlderOne()
    {
        var installed = new[] { Jar("fabric-api", "fabric-api-0.115.0+1.21.1") };

        // Downloading over itself. Calling this an older build would have the caller delete
        // the file that was just fetched.
        Assert.Null(InstalledMods.OlderBuildOf("fabric-api-0.115.0+1.21.1.jar", installed));
    }

    [Fact]
    public void KeepsTwoProjectsWithASharedPrefixApart()
    {
        var installed = new[] { Jar("sodium", "sodium-fabric-0.9.1") };

        // Sodium Extra is a different mod, and installing it must not remove Sodium.
        Assert.Null(InstalledMods.OlderBuildOf("sodium-extra-0.6.0.jar", installed));
    }

    [Fact]
    public void GivesUpRatherThanGuessBetweenTwoCandidates()
    {
        // Both share a stem, so which one is the older build of the incoming file is anyone's
        // guess — and a wrong guess deletes a mod somebody wanted.
        var installed = new[]
        {
            Jar("create", "create-0.5.1"),
            Jar("create", "create-0.5.0"),
        };

        Assert.Null(InstalledMods.OlderBuildOf("create-0.6.0.jar", installed));
    }
}
