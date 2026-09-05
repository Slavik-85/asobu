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

    private LaunchPlan Build(string id, string? clientJarVersionId, Action<Instance>? adjust = null)
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
        adjust?.Invoke(instance);

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
    /// A method Java crashed compiling, which it is now told to leave alone.
    ///
    /// The spelling is the whole of it and is not the one anybody would guess. Checked against
    /// the runtime itself rather than from the documentation: java 17.0.15 accepts
    /// "package.Class::method" and refuses both "package/Class::method" ("Method pattern uses
    /// '/' together with '::'") and "package.Class.method" ("multiple '.' in pattern"), and it
    /// refuses them by printing a parse error and carrying on — so a wrong one is not an error
    /// anybody sees, it is a fix that silently does nothing.
    /// </summary>
    [Fact]
    public void A_method_java_crashed_compiling_is_excluded_in_the_spelling_java_accepts()
    {
        var plan = Build("1.19.2", clientJarVersionId: null,
            instance => instance.SkipCompiling.Add("net.minecraft.client.Minecraft::runTick"));

        Assert.Contains("-XX:CompileCommand=exclude,net.minecraft.client.Minecraft::runTick", plan.Arguments);
    }

    /// <summary>
    /// Nothing writes one of these by hand, but the instance file is a text file and people edit
    /// them. A pattern the JVM cannot parse is worse than none: it complains once at startup and
    /// then runs exactly as it did before, which looks from the outside like the fix not working.
    /// </summary>
    [Fact]
    public void A_method_name_java_would_refuse_is_never_put_on_the_command_line()
    {
        var plan = Build("1.19.2", clientJarVersionId: null, instance =>
        {
            instance.SkipCompiling.Add("net/minecraft/client/Minecraft::runTick");
            instance.SkipCompiling.Add("net.minecraft.client.Minecraft.runTick");
            instance.SkipCompiling.Add("-XX:+UnlockDiagnosticVMOptions");
        });

        Assert.DoesNotContain(plan.Arguments, a => a.StartsWith("-XX:CompileCommand", StringComparison.Ordinal));
        Assert.DoesNotContain("-XX:+UnlockDiagnosticVMOptions", plan.Arguments);
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
