using System.Reflection;
using Asobu.Core;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// The rule the Add button follows: offer it when the instance has not got the mod, and when it
/// has got it but has fallen behind. Withhold it only when there is genuinely nothing left to do.
/// </summary>
public class InstanceContentsTests
{
    private static InstalledMods Index(params ModEntry[] entries) =>
        (InstalledMods)Activator.CreateInstance(
            typeof(InstalledMods),
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            args: [entries.AsEnumerable()],
            culture: null)!;

    private static readonly ModEntry Sodium =
        new("C:/mods/sodium-0.9.1.jar", "sodium-0.9.1.jar", "Sodium", "", "sodium", 1024, true, null);

    private static readonly CatalogueMod Listed =
        new(new ModListing(ModProvider.Modrinth, "AANobbMI", "Sodium", "jellysquid3", "", null, 10,
            "https://modrinth.com/mod/sodium"), null);

    [Fact]
    public void OffersTheButtonWhenTheModIsNotInstalled()
    {
        var contents = new AsobuLauncher.InstanceContents(Index(), []);

        Assert.False(contents.HasNewestOf(Listed));
    }

    [Fact]
    public void KeepsTheButtonWhenTheInstalledBuildHasFallenBehind()
    {
        // Installed, but a newer build exists. This is the one time the button is most worth
        // pressing, and treating "installed" as "done" would hide it.
        var contents = new AsobuLauncher.InstanceContents(Index(Sodium), [Sodium.Path]);

        Assert.False(contents.HasNewestOf(Listed));
        Assert.True(contents.Has(Listed));
    }

    [Fact]
    public void WithholdsTheButtonOnlyWhenThereIsNothingLeftToDo()
    {
        var contents = new AsobuLauncher.InstanceContents(Index(Sodium), []);

        Assert.True(contents.HasNewestOf(Listed));
    }

    [Fact]
    public void CarriesWhatIsBehindAcrossARescan()
    {
        // After an add, the folder is reread but the update lookup is not. A mod that had fallen
        // behind a moment ago still has, and must keep its button.
        var contents = new AsobuLauncher.InstanceContents(Index(Sodium), [Sodium.Path]);

        var after = contents.WithInstalled(Index(Sodium));

        Assert.False(after.HasNewestOf(Listed));
    }

    [Fact]
    public void AnEmptyInstanceOffersEverything()
    {
        Assert.False(AsobuLauncher.InstanceContents.Empty.HasNewestOf(Listed));
    }
}
