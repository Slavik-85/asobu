using Asobu.Core;
using Asobu.Core.Minecraft;

namespace Asobu.Core.Tests;

/// <summary>
/// The second way to build Forge, used only when the first has already failed.
///
/// Asobu builds Forge by running the installer's processors as Java subprocesses while
/// installing. When that fails the instance is finished — every launch after it runs the same
/// processors and fails the same way — so there is a fallback: ForgeWrapper becomes the main
/// class and does the same build inside the game's own JVM, which is how Prism Launcher starts
/// instances that will not start elsewhere.
///
/// What is tested here is the document it produces, because that is the part that is a decision
/// rather than a download. The transformation is pure on purpose: it can be looked at and
/// compared before anything is launched with it.
/// </summary>
public class ForgeWrapperTests
{
    private static readonly AsobuPaths Paths = new(@"C:\asobu");

    private const string Installer = @"C:\asobu\cache\installers\forge-1.19.2-43.5.0-installer.jar";
    private const string MinecraftJar = @"C:\asobu\cache\versions\1.19.2\1.19.2.jar";

    /// <summary>Forge's own document, cut to the parts the wrapping touches or must not touch.</summary>
    private static VersionJson Forge() => new()
    {
        Id = "1.19.2-forge-43.5.0",
        InheritsFrom = "1.19.2",
        MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
        Libraries = [new Library { Name = "net.minecraftforge:fmlloader:1.19.2-43.5.0" }],
        Arguments = new Arguments
        {
            Game = [new ConditionalArgument { Values = ["--fml.forgeVersion", "43.5.0"] }],
            Jvm = [new ConditionalArgument { Values = ["-DignoreList=asm,forge-"] }],
        },
    };

    private static IReadOnlyList<string> JvmValues(VersionJson version) =>
        [.. version.Arguments!.Jvm.SelectMany(a => a.Values)];

    // ---- what the wrapper needs ----

    [Fact]
    public void The_wrapper_becomes_the_main_class()
    {
        var wrapped = ForgeWrapper.Wrap(Forge(), Paths, Installer, MinecraftJar)!;

        Assert.Equal("io.github.zekerzhayard.forgewrapper.installer.Main", wrapped.MainClass);
    }

    /// <summary>
    /// The three properties are its whole interface: without them it cannot find the installer
    /// to build from, and reports that it cannot detect one.
    /// </summary>
    [Fact]
    public void It_is_told_where_the_installer_the_jar_and_the_libraries_are()
    {
        var jvm = JvmValues(ForgeWrapper.Wrap(Forge(), Paths, Installer, MinecraftJar)!);

        Assert.Contains($"-Dforgewrapper.installer={Installer}", jvm);
        Assert.Contains($"-Dforgewrapper.minecraft={MinecraftJar}", jvm);
        Assert.Contains($"-Dforgewrapper.librariesDir={Paths.Libraries}", jvm);
    }

    /// <summary>It has to be on the classpath to be the main class.</summary>
    [Fact]
    public void It_is_added_to_the_libraries()
    {
        var wrapped = ForgeWrapper.Wrap(Forge(), Paths, Installer, MinecraftJar)!;

        var wrapper = wrapped.Libraries.Single(l => l.Name == ForgeWrapper.Coordinates);

        // With a real download block, so the installer fetches and verifies it like any other
        // library. Without one it would be guessed at against Mojang's repository, which has
        // never heard of it, and the install would fail on a 404.
        Assert.NotNull(wrapper.Downloads?.Artifact);
        Assert.StartsWith("https://", wrapper.Downloads.Artifact.Url);
        Assert.False(string.IsNullOrEmpty(wrapper.Downloads.Artifact.Sha1));
        Assert.True(wrapper.Downloads.Artifact.Size > 0);
    }

    // ---- and what it must leave alone ----

    /// <summary>
    /// The wrapper reads --fml.forgeVersion and --fml.mcVersion out of the game arguments to
    /// work out which Forge it is building, then hands them on to Forge itself. Dropping them
    /// would break both halves at once.
    /// </summary>
    [Fact]
    public void Forges_own_arguments_are_carried_through_untouched()
    {
        var wrapped = ForgeWrapper.Wrap(Forge(), Paths, Installer, MinecraftJar)!;

        Assert.Contains("--fml.forgeVersion", wrapped.Arguments!.Game.SelectMany(a => a.Values));
        Assert.Contains("-DignoreList=asm,forge-", JvmValues(wrapped));
    }

    /// <summary>
    /// The module path and the ignore list have to be in place before the properties are read,
    /// so the additions go on the end rather than the front.
    /// </summary>
    [Fact]
    public void The_properties_are_added_after_what_was_already_there()
    {
        var jvm = JvmValues(ForgeWrapper.Wrap(Forge(), Paths, Installer, MinecraftJar)!);

        Assert.Equal("-DignoreList=asm,forge-", jvm[0]);
        Assert.All(jvm.Skip(1), value => Assert.StartsWith("-Dforgewrapper.", value));
    }

    [Fact]
    public void Everything_else_about_the_version_is_the_same_version()
    {
        var before = Forge();
        var after = ForgeWrapper.Wrap(before, Paths, Installer, MinecraftJar)!;

        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.InheritsFrom, after.InheritsFrom);
        Assert.Contains(after.Libraries, l => l.Name == "net.minecraftforge:fmlloader:1.19.2-43.5.0");
    }

    /// <summary>
    /// Wrapping something already wrapped. It cannot happen through the fallback, which runs
    /// once, but the document it produces is written to disk and read back on later launches —
    /// and a second pass would put the properties on twice and point the main class at itself.
    /// </summary>
    [Fact]
    public void Wrapping_twice_changes_nothing_the_second_time()
    {
        var once = ForgeWrapper.Wrap(Forge(), Paths, Installer, MinecraftJar)!;
        var twice = ForgeWrapper.Wrap(once, Paths, Installer, MinecraftJar)!;

        Assert.Same(once, twice);
        Assert.Equal(3, JvmValues(twice).Count(v => v.StartsWith("-Dforgewrapper.")));
    }

    /// <summary>
    /// Forge before 1.13, which has no structured arguments and no processors. The wrapper does
    /// not support those, and wrapping one anyway would build an arguments block holding nothing
    /// but the three properties — which the launch builder reads as "this version uses structured
    /// arguments", and then emits no classpath and none of the game's own arguments.
    ///
    /// Refused by Wrap itself rather than by its caller, so there is no way to reach it.
    /// </summary>
    [Fact]
    public void A_version_from_before_the_wrapper_existed_is_refused()
    {
        var old = new VersionJson
        {
            Id = "1.8.9-forge",
            MainClass = "net.minecraft.launchwrapper.Launch",
            MinecraftArguments = "--username ${auth_player_name}",
        };

        Assert.Null(ForgeWrapper.Wrap(old, Paths, Installer, MinecraftJar));
    }

    // ---- and what must never be settled on ----

    /// <summary>
    /// A wrapped document is recognisable as one, which is what keeps an instance from being
    /// committed to it.
    ///
    /// The launcher remembers the version document an install produced and skips working it out
    /// again on later launches. Remembering a wrapped one would settle a failure permanently: the
    /// ordinary build would never be tried again, so whatever broke it is never found to be
    /// fixed, and the instance runs through a third-party jar on the strength of an install that
    /// never saw it launch. So a wrapped document is not remembered, and every launch tries the
    /// ordinary way first — which is also how an instance gets itself back to normal.
    /// </summary>
    [Fact]
    public void A_wrapped_document_is_known_to_be_one()
    {
        Assert.True(ForgeWrapper.IsWrapped(ForgeWrapper.Wrap(Forge(), Paths, Installer, MinecraftJar)!));
    }

    [Fact]
    public void An_ordinary_forge_document_is_not()
    {
        Assert.False(ForgeWrapper.IsWrapped(Forge()));
    }

    /// <summary>A document with no main class at all is not one either, and does not throw.</summary>
    [Fact]
    public void Neither_is_one_that_names_no_main_class()
    {
        Assert.False(ForgeWrapper.IsWrapped(new VersionJson { Id = "1.20.1" }));
    }

    /// <summary>
    /// The original is not modified. It is the document the ordinary path would have returned,
    /// and the caller still has it.
    /// </summary>
    [Fact]
    public void The_document_it_was_given_is_left_as_it_was()
    {
        var original = Forge();

        ForgeWrapper.Wrap(original, Paths, Installer, MinecraftJar);

        Assert.Equal("cpw.mods.bootstraplauncher.BootstrapLauncher", original.MainClass);
        Assert.Single(original.Libraries);
        Assert.Single(original.Arguments!.Jvm);
    }
}
