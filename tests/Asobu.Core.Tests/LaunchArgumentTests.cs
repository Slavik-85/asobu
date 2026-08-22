using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Instances;
using Asobu.Core.Launch;
using Asobu.Core.Minecraft;

namespace Asobu.Core.Tests;

/// <summary>
/// What ends up on the java command line.
///
/// The case that matters here is Forge's ignore list, which is matched against file names on the
/// class path and decides what stays off the module path. Getting it wrong does not degrade
/// anything — the JVM refuses to start at all, with a message about duplicate packages that says
/// nothing about launcher arguments.
/// </summary>
public class LaunchArgumentTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-launch-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    /// <summary>Forge's own, copied from the 1.19.2 profile.</summary>
    private const string IgnoreList =
        "-DignoreList=bootstraplauncher,securejarhandler,asm-commons,asm-util,asm-analysis,asm-tree," +
        "asm,JarJarFileSystems,client-extra,fmlcore,javafmllanguage,lowcodelanguage,mclanguage,forge-," +
        "${version_name}.jar";

    private LaunchPlan Build(string id, string? clientJarVersionId)
    {
        var paths = new AsobuPaths(_root);
        var version = new VersionJson
        {
            Id = id,
            ClientJarVersionId = clientJarVersionId,
            MainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
            Arguments = new Arguments
            {
                Jvm = [new ConditionalArgument { Values = [IgnoreList] }],
                Game = [new ConditionalArgument { Values = ["--version", "${version_name}"] }],
            },
        };

        var instance = new Instance { Id = "x", Name = "Test", MinecraftVersion = "1.19.2" };
        instance.Folder = "test";

        using var http = new HttpClient();
        var builder = new LaunchBuilder(paths, new MinecraftInstaller(http, paths, new MojangMeta(http)));

        return builder.Build(
            version, instance, new LauncherSettings(),
            new MinecraftSession("Slavky", "abc", "token", "msa", null), "java");
    }

    /// <summary>
    /// The entry exists to keep the vanilla client jar off the module path. Naming the profile
    /// instead names a file that does not exist, nothing is excluded, and the JVM dies with
    /// "Module minecraft contains package com.mojang.blaze3d.system".
    /// </summary>
    [Fact]
    public void Forges_ignore_list_names_the_client_jar_not_the_profile()
    {
        var plan = Build("1.19.2-forge-43.5.0", clientJarVersionId: "1.19.2");

        var ignore = plan.Arguments.Single(a => a.StartsWith("-DignoreList=", StringComparison.Ordinal));

        Assert.Contains("1.19.2.jar", ignore);
        Assert.DoesNotContain("1.19.2-forge-43.5.0.jar", ignore);
    }

    /// <summary>
    /// F3 shows this, and it should say which profile is running rather than which jar it was
    /// built on — so the game arguments keep naming the profile.
    /// </summary>
    [Fact]
    public void The_game_is_still_told_which_profile_it_is()
    {
        var plan = Build("1.19.2-forge-43.5.0", clientJarVersionId: "1.19.2");

        Assert.Equal("1.19.2-forge-43.5.0", After(plan, "--version"));
    }

    /// <summary>The argument after a flag, which is where its value lives.</summary>
    private static string After(LaunchPlan plan, string flag)
    {
        var arguments = plan.Arguments.ToList();
        return arguments[arguments.IndexOf(flag) + 1];
    }

    [Fact]
    public void A_vanilla_version_names_itself_in_both()
    {
        var plan = Build("1.19.2", clientJarVersionId: null);

        Assert.Contains("1.19.2.jar",
            plan.Arguments.Single(a => a.StartsWith("-DignoreList=", StringComparison.Ordinal)));
        Assert.Equal("1.19.2", After(plan, "--version"));
    }
}
