using Asobu.Core;
using Asobu.Core.Diagnostics;
using Asobu.Core.Instances;
using Asobu.Core.Accounts;
using Asobu.Core.Launch;
using Asobu.Core.Minecraft;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Crashes the game had nothing to do with.
///
/// From a real one. A tester's log ended with the block Java prints when it dies of a fatal
/// error — an access violation inside jvm.dll, on a thread that was compiling code — and Asobu
/// answered "Essential is the most likely cause", having found a handled connection error in
/// Essential's networking four minutes earlier and nothing better since.
///
/// Nothing above that block is evidence. The process stopped mid-instruction, and everything
/// before it belongs to a session that was running fine: a mixin's startup warning, a mod's
/// failed socket, an advancement that would not parse. The block itself says what was actually
/// executing, so it answers first and the rest of the log is not consulted at all.
/// </summary>
public class JvmFatalTests
{
    private static ModEntry Mod(string file, string name, string? id = null) =>
        new($"C:/mods/{file}", file, name, "", id, 1024, true, null);

    private static readonly IReadOnlyList<ModEntry> Installed =
    [
        Mod("Essential (forge_1.19.2).jar", "Essential", "essential"),
        Mod("mcef-1.19.2-1.2.5.jar", "MCEF", "mcef"),
        Mod("webdisplays-1.3.3.jar", "Web Displays", "webdisplays"),
        Mod("embeddium-0.3.18.1+mc1.19.2.jar", "Embeddium", "embeddium"),
        Mod("quark-3.4-431.jar", "Quark", "quark"),
    ];

    /// <summary>
    /// The noise the real log ended on: a handled exception from a mod, minutes before the crash,
    /// which is what the analyser used to convict.
    /// </summary>
    private const string Noise = """
        [12:02:14] [Render thread/INFO] [minecraft/Minecraft]: Using graphics device: NVIDIA GeForce RTX 3060 (NVIDIA)
        [12:07:11] [DefaultDispatcher-worker-6/ERROR] [essential/ice/]: [2] Failed to send java.net.DatagramPacket@4a2f6de0
        java.net.BindException: Cannot assign requested address: sendto
        	at java.base/sun.nio.ch.DatagramChannelImpl.send0(Native Method)
        	at gg.essential.ice.stun.StunSocket$2.invokeSuspend(StunSocket.kt:116) ~[Essential%20(forge_1.19.2).jar%23296!/:?] {re:classloading}
        [12:23:13] [Render thread/ERROR] [mojang/YggdrasilMinecraftSessionService]: Signature is missing from textures payload
        """;

    /// <summary>
    /// The block itself, exactly as the runtime writes it. The replay-data line is the important
    /// one: Java writes it only when the thread that died was a JIT compiler thread, so its
    /// presence is a statement about which thread died rather than an inference from the frame.
    /// </summary>
    private const string CompilerCrash = Noise + """

        #
        # A fatal error has been detected by the Java Runtime Environment:
        #
        #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffa67f622b3, pid=25308, tid=18924
        #
        # JRE version: OpenJDK Runtime Environment Microsoft-11369865 (17.0.15+6) (build 17.0.15+6-LTS)
        # Java VM: OpenJDK 64-Bit Server VM Microsoft-11369865 (17.0.15+6-LTS, mixed mode, tiered, g1 gc, windows-amd64)
        # Problematic frame:
        # V  [jvm.dll+0x2222b3]
        #
        # No core dump will be written. Minidumps are not enabled by default on client versions of Windows
        #
        # An error report file with more information is saved as:
        # C:\Users\lunav\AppData\Local\Asobu\data\instances\One Block\minecraft\hs_err_pid25308.log
        #
        # Compiler replay data is saved as:
        # C:\Users\lunav\AppData\Local\Asobu\data\instances\One Block\minecraft\replay_pid25308.log
        #
        """;

    /// <summary>The same block with the frame in a driver instead, which is the common one.</summary>
    private const string DriverCrash = Noise + """

        #
        # A fatal error has been detected by the Java Runtime Environment:
        #
        #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffa1044c1e, pid=9001, tid=4242
        #
        # JRE version: OpenJDK Runtime Environment (17.0.15+6) (build 17.0.15+6-LTS)
        # Problematic frame:
        # C  [nvoglv64.dll+0x1044c1e]
        #
        # An error report file with more information is saved as:
        # C:\games\instance\minecraft\hs_err_pid9001.log
        #
        """;

    // ---- what actually died ----

    [Fact]
    public void A_crash_inside_the_compiler_is_reported_as_one()
    {
        var analysis = CrashAnalyzer.Analyze(CompilerCrash, Installed);

        Assert.Equal(CrashCause.JvmFatal, analysis.Cause);
        Assert.Contains("compiling", analysis.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The bug this is all about. Essential is in the log because a socket call failed and it
    /// carried on; the game died four minutes later somewhere Essential had no part in.
    /// </summary>
    [Fact]
    public void No_mod_is_accused_for_a_crash_no_mod_was_running_in()
    {
        var analysis = CrashAnalyzer.Analyze(CompilerCrash, Installed);

        Assert.Empty(analysis.Suspects);
        Assert.DoesNotContain("Essential", analysis.Headline, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Turning mods off is the one thing that cannot work here, and the one thing anybody would
    /// try. Saying so is most of the value of recognising this at all.
    /// </summary>
    [Fact]
    public void The_advice_says_outright_that_turning_mods_off_will_not_help()
    {
        var analysis = CrashAnalyzer.Analyze(CompilerCrash, Installed);

        Assert.Contains("turning mods off will not", analysis.Advice, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The version is worth quoting: the fix on offer is a different runtime.</summary>
    [Fact]
    public void The_runtime_that_crashed_is_named()
    {
        Assert.Contains("17.0.15+6", CrashAnalyzer.Analyze(CompilerCrash, Installed).Advice);
    }

    /// <summary>
    /// The file with everything the printed block leaves out. Naming it is what turns "send me
    /// your logs" into something somebody can do.
    /// </summary>
    [Fact]
    public void The_error_file_is_named_by_its_own_file_name_rather_than_its_path()
    {
        var advice = CrashAnalyzer.Analyze(CompilerCrash, Installed).Advice;

        Assert.Contains("hs_err_pid25308.log", advice);
        Assert.DoesNotContain(@"C:\Users\lunav", advice);
    }

    // ---- and when it is somebody's fault ----

    [Fact]
    public void A_frame_in_the_graphics_driver_is_the_graphics_driver()
    {
        var analysis = CrashAnalyzer.Analyze(DriverCrash, Installed);

        Assert.Equal(CrashCause.Graphics, analysis.Cause);
        Assert.Contains("RTX 3060", analysis.Headline);
        Assert.Contains("nvoglv64.dll", analysis.Advice);
    }

    /// <summary>
    /// A library whose name begins the way a driver's does and is nothing of the sort.
    ///
    /// GLib, which half the native code in existence is linked against and which has no
    /// connection to a graphics card. The driver names are matched as prefixes because they
    /// carry a bitness in the middle — nvoglv64 — and the ordinary words among them have to be
    /// bounded or they take libglib, libGLU and anything else that starts with those letters
    /// with them, and answer a crash in one of them with "update your graphics driver".
    /// </summary>
    [Theory]
    [InlineData("libglib-2.0.so.0")]
    [InlineData("libGLU.so.1")]
    [InlineData("libgladevalidator.so")]
    public void A_library_that_only_starts_like_a_driver_is_not_one(string library)
    {
        var crash = Noise + $"""

            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  SIGSEGV (0xb) at pc=0x00007ff8, pid=1, tid=2
            # Problematic frame:
            # C  [{library}+0x1a2b3c]
            #
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.NotEqual(CrashCause.Graphics, analysis.Cause);
        Assert.Contains(library, analysis.Advice);
    }

    /// <summary>The real ones, which still have to match.</summary>
    [Theory]
    [InlineData("nvoglv64.dll")]
    [InlineData("atio6axx.dll")]
    [InlineData("ig9icd64.dll")]
    [InlineData("vulkan-1.dll")]
    [InlineData("OPENGL32.dll")]
    [InlineData("libGL.so.1")]
    [InlineData("libGLX_nvidia.so.0")]
    [InlineData("libnvidia-glcore.so.550.54")]
    public void The_drivers_themselves_still_are(string library)
    {
        var crash = Noise + $"""

            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  SIGSEGV (0xb) at pc=0x00007ff8, pid=1, tid=2
            # Problematic frame:
            # C  [{library}+0x1a2b3c]
            #
            """;

        Assert.Equal(CrashCause.Graphics, CrashAnalyzer.Analyze(crash, Installed).Cause);
    }

    /// <summary>
    /// A heap that ran out and a process that then died somewhere unremarkable. The fatal block
    /// is answered first everywhere else on this page, and this is the one case where it should
    /// not be: there is nothing in an unrecognised frame worth knowing, and the log has already
    /// said what the actual trouble was.
    /// </summary>
    [Fact]
    public void A_crash_with_nothing_in_the_frame_defers_to_a_heap_that_had_already_run_out()
    {
        var crash = """
            [12:04:02] [Render thread/ERROR] [minecraft/Minecraft]: java.lang.OutOfMemoryError: Java heap space
            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  SIGSEGV (0xb) at pc=0x00007ff8, pid=1, tid=2
            # Problematic frame:
            # C  [somethingelse64.dll+0x1234]
            #
            """;

        Assert.Equal(CrashCause.OutOfMemory, CrashAnalyzer.Analyze(crash, Installed).Cause);
    }

    /// <summary>
    /// And not when the frame said something. A driver that fell over in a session that had also
    /// been short of memory is still a driver that fell over, and handing that to the memory
    /// check would throw away the one piece of direct evidence there is.
    /// </summary>
    [Fact]
    public void A_frame_that_names_something_keeps_its_verdict_whatever_else_the_log_says()
    {
        var crash = """
            [12:04:02] [Render thread/ERROR] [minecraft/Minecraft]: java.lang.OutOfMemoryError: Java heap space
            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ff8, pid=1, tid=2
            # Problematic frame:
            # C  [nvoglv64.dll+0x1044c1e]
            #
            """;

        Assert.Equal(CrashCause.Graphics, CrashAnalyzer.Analyze(crash, Installed).Cause);
    }

    /// <summary>
    /// A whole copy of Chromium, running in the game's process. Nothing about this looks like a
    /// mod crash — there is no Java in the trace at all — but a mod is exactly what put it there,
    /// and turning that mod off is the fix.
    /// </summary>
    [Fact]
    public void A_frame_in_the_embedded_browser_names_the_mod_that_ships_it()
    {
        var crash = Noise + """

            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ff8, pid=1, tid=2
            # Problematic frame:
            # C  [libcef.dll+0x2f8a1c6]
            #
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.Mod, analysis.Cause);
        Assert.Equal("MCEF", analysis.Suspects.Single().Name);
        Assert.True(analysis.Suspects.Single().NamedDirectly);
    }

    /// <summary>Nothing installed to blame it on, so the finding stands on its own.</summary>
    [Fact]
    public void An_embedded_browser_crash_is_still_explained_when_no_mod_matches()
    {
        var crash = Noise + """

            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ff8, pid=1, tid=2
            # Problematic frame:
            # C  [libcef.dll+0x2f8a1c6]
            #
            """;

        var analysis = CrashAnalyzer.Analyze(crash, [Mod("quark-3.4-431.jar", "Quark", "quark")]);

        Assert.Equal(CrashCause.JvmFatal, analysis.Cause);
        Assert.Empty(analysis.Suspects);
    }

    /// <summary>
    /// A Java frame, which is a mod's own code — and unlike a mod merely appearing in a stack
    /// trace, this one was executing at the instant the process stopped.
    /// </summary>
    [Fact]
    public void A_java_frame_belongs_to_whichever_mod_owns_the_class()
    {
        var crash = Noise + """

            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ff8, pid=1, tid=2
            # Problematic frame:
            # J 24263 c2 vazkii.quark.content.tools.module.PickarangModule.tick()V (52 bytes)
            #
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.Mod, analysis.Cause);
        Assert.Equal("Quark", analysis.Suspects.Single().Name);
    }

    /// <summary>
    /// jvm.dll again, but on an ordinary thread. Without the replay file there is nothing saying
    /// a compiler was involved, and saying so anyway would be inventing the one detail that makes
    /// the rest of the advice make sense.
    /// </summary>
    [Fact]
    public void A_crash_in_java_itself_is_only_called_a_compiler_crash_when_it_was_one()
    {
        var crash = Noise + """

            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ff8, pid=1, tid=2
            # JRE version: OpenJDK Runtime Environment (21.0.4+7) (build 21.0.4+7-LTS)
            # Problematic frame:
            # V  [jvm.dll+0x6f1a2c]
            #
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.JvmFatal, analysis.Cause);
        Assert.DoesNotContain("compil", analysis.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("21.0.4+7", analysis.Advice);
    }

    /// <summary>A frame in nothing recognisable still beats a hunt through the log above it.</summary>
    [Fact]
    public void An_unrecognised_frame_is_answered_without_reaching_for_a_mod()
    {
        var crash = Noise + """

            #
            # A fatal error has been detected by the Java Runtime Environment:
            #
            #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ff8, pid=1, tid=2
            # Problematic frame:
            # C  [somethingelse64.dll+0x1234]
            #
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.JvmFatal, analysis.Cause);
        Assert.Empty(analysis.Suspects);
        Assert.Contains("somethingelse64.dll", analysis.Advice);
    }

    // ---- the machine's memory, which is not the game's ----

    /// <summary>
    /// Written instead of the usual block rather than alongside it: no signal, no frame, nothing
    /// crashed. Java asked Windows for memory and Windows said no.
    ///
    /// The advice has to be the opposite of the one for a heap that filled up, which is why this
    /// is worth telling apart at all — more memory for the instance brings this on sooner.
    /// </summary>
    [Fact]
    public void Being_refused_memory_by_the_machine_is_not_the_game_running_out_of_heap()
    {
        var crash = """
            #
            # There is insufficient memory for the Java Runtime Environment to continue.
            # Native memory allocation (mmap) failed to map 1073741824 bytes for G1 virtual space
            # An error report file with more information is saved as:
            # C:\games\instance\minecraft\hs_err_pid7.log
            #
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.OutOfMemory, analysis.Cause);
        Assert.Contains("Lower it", analysis.Advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("G1 virtual space", analysis.Advice);
    }

    // ---- what Asobu can do about it ----

    /// <summary>
    /// The error file's own account of what was being compiled. This is the only place it is
    /// written — the summary in the log says a compiler thread died but not what it was working
    /// on — and it is the one thing on this whole page that can be acted on.
    /// </summary>
    private const string ErrorFile = """
        #
        # A fatal error has been detected by the Java Runtime Environment:
        #
        #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffa67f622b3, pid=25308, tid=18924
        #
        # JRE version: OpenJDK Runtime Environment Microsoft-11369865 (17.0.15+6)
        # Problematic frame:
        # V  [jvm.dll+0x2222b3]

        ---------------  S U M M A R Y ------------

        Command Line: -Xmx4096M net.minecraft.client.main.Main

        Current thread (0x0000021e4b0d5340):  JavaThread "C2 CompilerThread0" daemon [_thread_in_native, id=18924]

        Current CompileTask:
        C2:  67525 12345       4       net.minecraft.client.renderer.LevelRenderer::renderLevel (1247 bytes)

        Stack: [0x000000d4c1f00000,0x000000d4c2000000]
        """;

    [Fact]
    public void The_method_java_died_compiling_is_read_out_of_the_error_file()
    {
        Assert.Equal("net.minecraft.client.renderer.LevelRenderer::renderLevel",
            CrashAnalyzer.CompilingMethodIn(ErrorFile));
    }

    /// <summary>
    /// The error file carries no replay line — that one is printed to the log instead — so the
    /// compile task has to be enough on its own. Java writes it only for a thread that was
    /// compiling, exactly as it writes the replay line only for one, so the verdict does not
    /// depend on which of the two files somebody opened.
    /// </summary>
    [Fact]
    public void The_error_file_reaches_the_same_verdict_as_the_log_did()
    {
        var analysis = CrashAnalyzer.Analyze(ErrorFile, Installed);

        Assert.Equal(CrashCause.JvmFatal, analysis.Cause);
        Assert.Contains("compiling", analysis.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("net.minecraft.client.renderer.LevelRenderer::renderLevel", analysis.CompilingMethod);
    }

    /// <summary>
    /// And from the log, which has no compile task in it, the file to go and read for one. This
    /// is the hand-off: the log gets the verdict, the file it names gets the fix.
    /// </summary>
    [Fact]
    public void A_log_carries_the_path_of_the_file_that_holds_the_rest()
    {
        var analysis = CrashAnalyzer.Analyze(CompilerCrash, Installed);

        Assert.Null(analysis.CompilingMethod);
        Assert.Equal(@"C:\Users\lunav\AppData\Local\Asobu\data\instances\One Block\minecraft\hs_err_pid25308.log",
            analysis.ErrorFile);
    }

    /// <summary>A crash that was not the compiler's has nothing to exclude.</summary>
    [Fact]
    public void Nothing_is_offered_for_a_crash_that_was_not_a_compile()
    {
        Assert.Null(CrashAnalyzer.Analyze(DriverCrash, Installed).CompilingMethod);
        Assert.Null(CrashAnalyzer.CompilingMethodIn(DriverCrash));
    }

    /// <summary>
    /// Constructors are compiled like anything else, and Java names them in a shape that would
    /// fall out of any pattern written for ordinary method names.
    /// </summary>
    [Fact]
    public void A_constructor_is_a_method_like_any_other()
    {
        var file = """
            Current CompileTask:
            C2:  1024 512       4       vazkii.quark.base.module.QuarkModule::<init> (18 bytes)
            """;

        Assert.Equal("vazkii.quark.base.module.QuarkModule::<init>", CrashAnalyzer.CompilingMethodIn(file));
    }

    // ---- and the file it leaves behind ----

    /// <summary>
    /// The one crash that writes no crash report. Listing the file Java left in the game's folder
    /// is the only way the screen shows anything at all for a session that ended this way.
    /// </summary>
    [Fact]
    public void The_runtimes_own_error_file_is_listed_with_the_rest()
    {
        var root = Directory.CreateTempSubdirectory("asobu-hs-err-").FullName;

        try
        {
            var paths = new AsobuPaths(root);
            var instance = new Instance { Id = "x", Name = "One Block", Folder = "one-block", MinecraftVersion = "1.19.2" };

            var gameDir = paths.InstanceGameDir(instance.Folder);
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "hs_err_pid25308.log"), "# A fatal error has been detected");

            // Compiler replay data sits beside it and is of no use to anybody reading this screen.
            File.WriteAllText(Path.Combine(gameDir, "replay_pid25308.log"), "ciMethod ...");

            var listed = CrashReports.List(paths, instance);

            Assert.Equal("hs_err_pid25308.log", listed.Single().Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// The whole way through, from the file Java left on disk to the flag on the next command
    /// line — every step of which is somewhere else, and none of which is worth anything alone.
    ///
    /// The claim being tested is the one the button makes: press it and the thing that crashed
    /// does not get compiled next time.
    /// </summary>
    [Fact]
    public async Task The_crash_on_disk_becomes_a_flag_on_the_next_launch()
    {
        var root = Directory.CreateTempSubdirectory("asobu-jit-").FullName;

        try
        {
            var paths = new AsobuPaths(root);
            var instance = new Instance { Id = "x", Name = "One Block", Folder = "one-block", MinecraftVersion = "1.19.2" };

            var gameDir = paths.InstanceGameDir(instance.Folder);
            Directory.CreateDirectory(gameDir);
            File.WriteAllText(Path.Combine(gameDir, "hs_err_pid25308.log"), ErrorFile);

            // What the screen does: find the file Java left, read what it was compiling, and
            // write that down against the instance.
            var listed = CrashReports.List(paths, instance).Single(entry => entry.Kind == "Java error");
            var method = CrashAnalyzer.CompilingMethodIn(await CrashReports.ReadAsync(listed.Path));

            Assert.NotNull(method);
            instance.SkipCompiling.Add(method);

            using var http = new HttpClient();
            var plan = new LaunchBuilder(paths, new MinecraftInstaller(http, paths, new MojangMeta(http))).Build(
                new VersionJson { Id = "1.19.2", MainClass = "net.minecraft.client.main.Main" },
                instance,
                new LauncherSettings(),
                new MinecraftSession("Slavky", "abc", "token", "msa", null),
                "java");

            Assert.Contains(
                "-XX:CompileCommand=exclude,net.minecraft.client.renderer.LevelRenderer::renderLevel",
                plan.Arguments);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Read from the front, unlike every other file on that screen. A Java error file opens with
    /// the crash and closes with a map of the process's memory, so tailing a large one shows the
    /// map and hides the only part anybody wants.
    /// </summary>
    [Fact]
    public async Task A_large_error_file_is_read_from_its_beginning()
    {
        var file = Path.Combine(Directory.CreateTempSubdirectory("asobu-hs-err-").FullName, "hs_err_pid1.log");

        try
        {
            File.WriteAllText(file,
                "# A fatal error has been detected by the Java Runtime Environment:\n"
                + new string('x', 400_000)
                + "\nDynamic libraries: 0x00007ff...");

            var text = await CrashReports.ReadAsync(file);

            Assert.Contains("A fatal error has been detected", text);
            Assert.DoesNotContain("Dynamic libraries", text);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(file)!, recursive: true);
        }
    }
}
