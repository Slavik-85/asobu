using Asobu.Core;
using Asobu.Core.Accounts;
using Asobu.Core.Instances;
using Asobu.Core.Java;
using Asobu.Core.Launch;
using Asobu.Core.Minecraft;

namespace Asobu.Core.Tests;

/// <summary>
/// Two things Prism Launcher does before it hands anything to the JVM, and Asobu did not.
///
/// Both are the same shape of problem: a setting somebody can reach produces a command line the
/// JVM rejects outright, and the rejection names the argument rather than the setting — or names
/// nothing at all. No crash report is written for either, because the game never starts.
/// </summary>
public class LaunchGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("asobu-guard-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private LaunchPlan Build(int minMemoryMb, int maxMemoryMb)
    {
        var paths = new AsobuPaths(_root);
        var version = new VersionJson { Id = "1.20.1", MainClass = "net.minecraft.client.main.Main" };

        var instance = new Instance { Id = "x", Name = "Test", MinecraftVersion = "1.20.1", Folder = "test" };

        using var http = new HttpClient();
        var builder = new LaunchBuilder(paths, new MinecraftInstaller(http, paths, new MojangMeta(http)));

        return builder.Build(
            version, instance,
            new LauncherSettings { MinMemoryMb = minMemoryMb, MaxMemoryMb = maxMemoryMb },
            new MinecraftSession("Slavky", "abc", "token", "msa", null), "java");
    }

    // ---- memory the JVM will accept ----

    [Fact]
    public void The_usual_way_round_is_passed_through_untouched()
    {
        var plan = Build(minMemoryMb: 1024, maxMemoryMb: 4096);

        Assert.Contains("-Xms1024M", plan.Arguments);
        Assert.Contains("-Xmx4096M", plan.Arguments);
    }

    /// <summary>
    /// The floor above the ceiling. Java refuses to start at all on this — "Initial heap size set
    /// to a larger value than the maximum heap size", then nothing: no window, no log, no crash
    /// report, and a launcher that reported "the game closed immediately".
    ///
    /// Swapped rather than refused, which is what Prism does and is plainly what was meant.
    /// </summary>
    [Fact]
    public void A_floor_above_the_ceiling_is_turned_the_right_way_round()
    {
        var plan = Build(minMemoryMb: 8192, maxMemoryMb: 4096);

        Assert.Contains("-Xms4096M", plan.Arguments);
        Assert.Contains("-Xmx8192M", plan.Arguments);

        Assert.DoesNotContain("-Xms8192M", plan.Arguments);
    }

    [Fact]
    public void Two_of_the_same_figure_stay_as_they_are()
    {
        var plan = Build(minMemoryMb: 2048, maxMemoryMb: 2048);

        Assert.Contains("-Xms2048M", plan.Arguments);
        Assert.Contains("-Xmx2048M", plan.Arguments);
    }

    // ---- the Java a version can actually run on ----

    /// <summary>
    /// Read from the "release" file every distribution ships, two folders up from the binary.
    /// Asking the binary means starting a JVM to find out whether to start a JVM.
    /// </summary>
    [Fact]
    public void The_java_version_is_read_from_the_runtime_on_disk()
    {
        var home = Path.Combine(_root, "jdk-17");
        Directory.CreateDirectory(Path.Combine(home, "bin"));

        File.WriteAllText(Path.Combine(home, "release"),
            "IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"17.0.9\"\nOS_ARCH=\"x86_64\"\n");

        Assert.Equal(17, JavaManager.MajorOf(Path.Combine(home, "bin", "java.exe")));
    }

    /// <summary>Java 8 names itself 1.8.0, which is 8 and not 1.</summary>
    [Fact]
    public void The_old_numbering_is_read_as_the_release_it_means()
    {
        var home = Path.Combine(_root, "jre8");
        Directory.CreateDirectory(Path.Combine(home, "bin"));

        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=\"1.8.0_402\"\n");

        Assert.Equal(8, JavaManager.MajorOf(Path.Combine(home, "bin", "java.exe")));
    }

    /// <summary>
    /// A runtime that will not say what it is. The guard has to let it through — refusing to
    /// launch because a file could not be read would be worse than the crash it prevents.
    /// </summary>
    [Fact]
    public void A_runtime_with_no_release_file_is_not_second_guessed()
    {
        var home = Path.Combine(_root, "mystery");
        Directory.CreateDirectory(Path.Combine(home, "bin"));

        Assert.Null(JavaManager.MajorOf(Path.Combine(home, "bin", "java.exe")));
    }

    [Fact]
    public void A_path_that_is_not_a_runtime_at_all_is_answered_rather_than_thrown()
    {
        Assert.Null(JavaManager.MajorOf("java"));
        Assert.Null(JavaManager.MajorOf(""));
    }
}
