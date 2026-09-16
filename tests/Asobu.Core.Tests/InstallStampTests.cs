using Asobu.Core.Instances;

namespace Asobu.Core.Tests;

/// <summary>
/// The stamp that lets a launch skip working out which version document to install.
///
/// Deriving that document was most of what a launch did: ask Mojang for the vanilla document,
/// ask Forge's repository which installer answers to this build, download it, open it, read the
/// profile inside — 1.8 seconds of it, every time, to arrive at a document already sitting in
/// the versions folder from the last launch. It is skipped when the stamp still matches.
///
/// Which makes the stamp the whole safety of it. It has to change whenever anything about what
/// would be built changes, because nothing else clears it — there is no code anywhere that
/// remembers to, on purpose, since that code is exactly what would be forgotten.
/// </summary>
public class InstallStampTests
{
    private static Instance Forge() => new()
    {
        Id = "x",
        Name = "Test",
        MinecraftVersion = "1.19.2",
        Loader = "forge",
        LoaderVersion = "43.5.0",
    };

    [Fact]
    public void The_same_instance_stamps_the_same_every_time()
    {
        Assert.Equal(Forge().InstallStamp, Forge().InstallStamp);
    }

    [Theory]
    [InlineData("MinecraftVersion")]
    [InlineData("Loader")]
    [InlineData("LoaderVersion")]
    public void Changing_any_of_the_three_things_it_is_built_from_changes_it(string property)
    {
        var instance = Forge();
        var before = instance.InstallStamp;

        switch (property)
        {
            case "MinecraftVersion": instance.MinecraftVersion = "1.20.1"; break;
            case "Loader": instance.Loader = "fabric"; break;
            case "LoaderVersion": instance.LoaderVersion = "43.5.1"; break;
        }

        Assert.NotEqual(before, instance.InstallStamp);
    }

    /// <summary>
    /// Dropping the loader entirely, which is a real thing somebody does to an instance and the
    /// case where reusing the last document would launch a modded profile with no mods loader.
    /// </summary>
    [Fact]
    public void Taking_the_loader_off_changes_it_too()
    {
        var instance = Forge();
        var before = instance.InstallStamp;

        instance.Loader = "";
        instance.LoaderVersion = null;

        Assert.NotEqual(before, instance.InstallStamp);
    }

    /// <summary>
    /// The three fields cannot run together into one another — "1.19.2|forge|43.5.0" must not be
    /// reachable from a different three. A separator that appears in no version string is what
    /// stops that, so this fails if somebody swaps it for a dash or a dot.
    /// </summary>
    [Fact]
    public void Two_different_instances_cannot_share_a_stamp_by_running_their_fields_together()
    {
        var one = Forge();

        var other = Forge();
        other.MinecraftVersion = "1.19.2|forge";
        other.Loader = "";

        Assert.NotEqual(one.InstallStamp, other.InstallStamp);
    }

    /// <summary>
    /// And it is not saved as its own field, which would be one more thing able to go stale.
    /// It is worked out from the instance every time it is asked for.
    /// </summary>
    [Fact]
    public void It_is_computed_rather_than_stored()
    {
        var instance = Forge();
        instance.InstalledFrom = instance.InstallStamp;

        instance.LoaderVersion = "43.5.1";

        Assert.NotEqual(instance.InstalledFrom, instance.InstallStamp);
    }
}
