using Asobu.Core;

namespace Asobu.Core.Tests;

/// <summary>
/// The switch that takes Asobu's stand-in out from between the game and Mojang.
///
/// Letting a friend without a Microsoft account into a world means Asobu has to be able to vouch
/// for them, and that means pointing the game's four Mojang addresses at a stand-in of Asobu's
/// own. Those addresses can only be set as the game starts, and nobody knows then whether anyone
/// will be invited — so today every session routes its sign-ins, skins and server joins through
/// that stand-in, whether or not a guest ever appears. No other launcher has anything in that
/// path, which makes it the first thing to remove when connecting is unreliable.
///
/// What is tested here is only that removing it is a choice and never a default. Everything that
/// works today has to keep working for somebody who never opens Settings.
/// </summary>
public class OfflineFriendsSettingTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-settings-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private AsobuPaths Paths => new(_root);

    [Fact]
    public void It_is_on_unless_somebody_turns_it_off()
    {
        Assert.True(new LauncherSettings().LetOfflineFriendsJoin);
    }

    /// <summary>
    /// And on for everybody who already has a settings file, which was written before this
    /// existed. A missing field must read as the old behaviour, not as off.
    /// </summary>
    [Fact]
    public void A_settings_file_from_before_it_existed_still_has_it_on()
    {
        Directory.CreateDirectory(Paths.Root);
        File.WriteAllText(Path.Combine(Paths.Root, "settings.json"), """{"maxMemoryMb": 6144}""");

        var settings = LauncherSettings.Load(Paths);

        Assert.True(settings.LetOfflineFriendsJoin);
        Assert.Equal(6144, settings.MaxMemoryMb);
    }

    [Fact]
    public void Turning_it_off_is_remembered()
    {
        var settings = LauncherSettings.Load(Paths);
        settings.LetOfflineFriendsJoin = false;
        settings.Save(Paths);

        Assert.False(LauncherSettings.Load(Paths).LetOfflineFriendsJoin);
    }

    /// <summary>
    /// An instance's own settings inherit it. The stand-in is started once per launch from the
    /// merged settings, so a per-instance override must not lose the launcher's answer.
    /// </summary>
    [Fact]
    public void An_instance_inherits_whatever_the_launcher_says()
    {
        var settings = LauncherSettings.Load(Paths);
        settings.LetOfflineFriendsJoin = false;
        settings.Save(Paths);

        var instance = new Instances.Instance { Id = "x", Name = "Test", MinecraftVersion = "1.20.1", Folder = "test" };

        Assert.False(LauncherSettings.Load(Paths).ForInstance(instance, Paths).LetOfflineFriendsJoin);
    }
}
