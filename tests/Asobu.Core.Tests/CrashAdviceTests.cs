using Asobu.Core.Diagnostics;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// The gap between what Asobu said and what actually fixed it.
///
/// Every case here is a real crash report off a real machine, run through the analyser as it was.
/// Two of the three mod-loading crashes came back pointing at the wrong thing, and both in the
/// same way: Forge writes a block saying in plain words which dependency is absent, the analyser
/// did not read it, and the stack trace it read instead is entirely Forge's own code — so the
/// hunt for a mod to blame landed on the mod the block was about, and offered to turn off the
/// very mod that was waiting for something else.
/// </summary>
public class CrashAdviceTests
{
    private static ModEntry Mod(string file, string name, string? id = null) =>
        new($"C:/mods/{file}", file, name, "", id, 1024, true, null);

    private static readonly IReadOnlyList<ModEntry> Installed =
    [
        Mod("MoreChestVariants-1.5.6+1.20.2-Forge.jar", "More Chest Variants", "lolmcv"),
        Mod("immersive-portals-3.0.7-all.jar", "Immersive Portals", "imm_ptl_core"),
    ];

    /// <summary>
    /// Cut from crash-2026-08-31_02.57.08-fml.txt, keeping its shape exactly: a stack trace that
    /// is Forge's from top to bottom, and one block at the end that actually says something.
    /// </summary>
    private const string MissingQuad = """
        ---- Minecraft Crash Report ----
        Description: Mod loading error has occurred

        java.lang.Exception: Mod Loading has failed
        	at net.minecraftforge.logging.CrashReportExtender.dumpModLoadingCrashReport(CrashReportExtender.java:60) ~[forge-1.20.1-47.4.10-universal.jar%23244!/:?] {re:classloading}
        	at net.minecraftforge.client.loading.ClientModLoader.completeModLoading(ClientModLoader.java:135) ~[forge-1.20.1-47.4.10-universal.jar%23244!/:?] {re:classloading}

        -- Head --
        Thread: Render thread
        Suspected Mods: NONE

        -- MOD lolmcv --
        Details:
        	Mod File: /C:/Users/slavi/AppData/Local/Asobu/data/instances/KISSES/minecraft/mods/MoreChestVariants-1.5.6+1.20.2-Forge.jar
        	Failure message: Mod lolmcv requires quad 1.2.3 or above
        		Currently, quad is not installed
        	Mod Version: 1.5.6
        	Mod Issue URL: NOT PROVIDED
        	Exception message: MISSING EXCEPTION MESSAGE

        -- System Details --
        Details:
        	Minecraft Version: 1.20.1
        """;

    // ---- what the loader said outright ----

    [Fact]
    public void A_dependency_the_loader_said_was_absent_is_the_verdict()
    {
        var analysis = CrashAnalyzer.Analyze(MissingQuad, Installed);

        Assert.Equal(CrashCause.MissingDependency, analysis.Cause);
        Assert.Contains("quad", analysis.Headline);
    }

    /// <summary>
    /// The bug, and the reason this matters more than a wrong label. Asobu said "More Chest
    /// Variants looks responsible — turn it off and launch again", which would have worked, in
    /// the sense that removing the mod removes the crash. It is the wrong end of the fix: the
    /// mod is wanted and works, and what is missing is the one nobody knew to install.
    /// </summary>
    [Fact]
    public void The_mod_that_was_waiting_for_it_is_not_the_one_accused()
    {
        var analysis = CrashAnalyzer.Analyze(MissingQuad, Installed);

        Assert.Empty(analysis.Suspects);
        Assert.DoesNotContain("More Chest Variants", analysis.Headline);
    }

    /// <summary>
    /// And it is carried, not just described. This is what the button on the screen fetches, so
    /// without it the verdict is a sentence telling somebody to go and do it themselves.
    /// </summary>
    [Fact]
    public void What_is_missing_is_handed_over_ready_to_fetch()
    {
        var missing = CrashAnalyzer.Analyze(MissingQuad, Installed).Missing;

        Assert.NotNull(missing);
        Assert.Equal("quad", missing.Id);
        Assert.Equal("lolmcv", missing.RequiredBy);
    }

    /// <summary>The jar rather than the mod id, since one of those means something to a person.</summary>
    [Fact]
    public void The_advice_names_the_file_it_came_out_of()
    {
        Assert.Contains("MoreChestVariants-1.5.6+1.20.2-Forge.jar", CrashAnalyzer.Analyze(MissingQuad, Installed).Advice);
    }

    /// <summary>
    /// From crash-2026-08-27_20.20.14-fml.txt, which the analyser answered with "Nothing obvious"
    /// while the report said, four lines down, exactly what was wrong.
    /// </summary>
    [Fact]
    public void The_report_that_came_back_as_nothing_obvious_now_names_it()
    {
        var crash = """
            ---- Minecraft Crash Report ----
            Description: Mod loading error has occurred

            java.lang.Exception: Mod Loading has failed
            	at net.minecraftforge.logging.CrashReportExtender.dumpModLoadingCrashReport(CrashReportExtender.java:60) ~[forge-1.20.1-47.4.10-universal.jar%23202!/:?] {re:classloading}

            -- MOD q_misc_util --
            Details:
            	Mod File: /C:/Users/slavi/AppData/Local/Asobu/data/instances/1.20.1/minecraft/mods/immersive-portals-3.0.7-all.jar
            	Failure message: Mod q_misc_util requires cloth_config 11.1 or above
            		Currently, cloth_config is not installed
            	Mod Version: 3.0.7
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.MissingDependency, analysis.Cause);
        Assert.Equal("cloth_config", analysis.Missing?.Id);
    }

    /// <summary>
    /// Installed, but not at a version that will do. The same block says so — the line under it
    /// reads a version instead of "not installed" — and the answer is a different one: there is
    /// nothing to fetch, so nothing is offered to be fetched.
    /// </summary>
    [Fact]
    public void A_dependency_that_is_merely_too_old_is_a_different_answer()
    {
        var crash = """
            ---- Minecraft Crash Report ----
            Description: Mod loading error has occurred

            -- MOD lolmcv --
            Details:
            	Mod File: /C:/mods/MoreChestVariants-1.5.6+1.20.2-Forge.jar
            	Failure message: Mod lolmcv requires quad 1.2.3 or above
            		Currently, quad is 1.0.1
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.MissingDependency, analysis.Cause);
        Assert.Contains("wrong version", analysis.Headline);
        Assert.Contains("1.0.1", analysis.Advice);
        Assert.Null(analysis.Missing);
    }

    /// <summary>
    /// Forge's other template, for a dependency a mod merely prefers. It says "only supports"
    /// rather than "requires" and the game starts regardless, so it is not a crash and must not
    /// be read as one.
    /// </summary>
    [Fact]
    public void A_dependency_a_mod_only_prefers_is_not_a_failure()
    {
        var crash = """
            ---- Minecraft Crash Report ----
            Description: Rendering overlay

            -- MOD lolmcv --
            Details:
            	Failure message: Mod lolmcv only supports quad 1.2.3 or above
            		Currently, quad is 1.0.1
            """;

        Assert.NotEqual(CrashCause.MissingDependency, CrashAnalyzer.Analyze(crash, Installed).Cause);
    }

    /// <summary>
    /// The one the reviewer caught. A mod built for another Minecraft says it in exactly the same
    /// words as any other dependency — "requires minecraft between 1.19.2 and 1.20, currently
    /// 1.20.1" — and answering it the same way tells somebody to update the game to suit a mod.
    /// Nobody does that. They get the build of the mod made for the game they have.
    /// </summary>
    [Fact]
    public void A_mod_built_for_another_minecraft_is_not_answered_with_update_minecraft()
    {
        var crash = """
            ---- Minecraft Crash Report ----
            Description: Mod loading error has occurred

            -- MOD jei --
            Details:
            	Mod File: /C:/mods/jei-1.19.2-11.6.0.1015.jar
            	Failure message: Mod jei requires minecraft between 1.19.2 and 1.20
            		Currently, minecraft is 1.20.1
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.WrongBuild, analysis.Cause);
        Assert.DoesNotContain("Updating minecraft", analysis.Advice);
        Assert.Contains("jei-1.19.2-11.6.0.1015.jar", analysis.Headline);
        Assert.Null(analysis.Missing);
    }

    /// <summary>The loader counts as the platform too: nobody downgrades Forge for one mod.</summary>
    [Fact]
    public void The_same_goes_for_the_loader()
    {
        var crash = """
            ---- Minecraft Crash Report ----
            Description: Mod loading error has occurred

            -- MOD jei --
            Details:
            	Mod File: /C:/mods/jei-1.19.2.jar
            	Failure message: Mod jei requires forge between 43.0 and 44.0
            		Currently, forge is 47.4.10
            """;

        Assert.Equal(CrashCause.WrongBuild, CrashAnalyzer.Analyze(crash, Installed).Cause);
    }

    /// <summary>
    /// A Mod File line ending in a separator, which leaves nothing for the file name to be. The
    /// sentence has to open with something, and the mod id is what there is.
    /// </summary>
    [Fact]
    public void A_path_with_no_file_name_on_the_end_still_names_something()
    {
        var crash = """
            ---- Minecraft Crash Report ----
            Description: Mod loading error has occurred

            -- MOD lolmcv --
            Details:
            	Mod File: /C:/mods/exploded/
            	Failure message: Mod lolmcv requires quad 1.2.3 or above
            		Currently, quad is not installed
            """;

        Assert.StartsWith("lolmcv needs quad", CrashAnalyzer.Analyze(crash, Installed).Advice);
    }

    // ---- the memory that ran out was not the game's ----

    /// <summary>
    /// Cut from hs_err_pid37044.log. All five of these on this machine say the same thing: the
    /// page file is empty, the RAM is not, and the game was never the greedy one.
    /// </summary>
    private const string PageFileFull = """
        #
        # There is insufficient memory for the Java Runtime Environment to continue.
        # Native memory allocation (mmap) failed to map 2097152 bytes. Error detail: G1 virtual space
        # Possible reasons:
        #   The system is out of physical RAM or swap space

        Command Line: -Xms1024M -Xmx4096M -Djava.library.path=natives net.minecraft.client.main.Main

        Memory: 4k page, system-wide physical 32703M (3674M free)
        TotalPageFile size 40703M (AvailPageFile size 3M)
        current process WorkingSet (physical memory assigned to process): 2752M, peak: 2757M
        """;

    /// <summary>
    /// The old advice was "lower the instance's memory", which on these numbers is nonsense: the
    /// game had 4 GB of a 32 GB machine, and taking a gigabyte off it would not have moved the
    /// number that actually ran out.
    /// </summary>
    [Fact]
    public void A_full_page_file_is_told_apart_from_a_full_machine()
    {
        var analysis = CrashAnalyzer.Analyze(PageFileFull, Installed);

        Assert.Equal(CrashCause.MachineOutOfMemory, analysis.Cause);
        Assert.Contains("page file", analysis.Headline, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("3 MB", analysis.Advice);
        Assert.Contains("39.7 GB", analysis.Advice);
    }

    [Fact]
    public void An_instance_that_was_not_the_greedy_one_is_not_told_to_take_less()
    {
        var advice = CrashAnalyzer.Analyze(PageFileFull, Installed).Advice;

        Assert.Contains("would not help much", advice);
        Assert.Contains("4 GB", advice);
        Assert.Contains("31.9 GB", advice);
    }

    /// <summary>
    /// And when the instance really is most of the machine, the old advice was right and is kept.
    /// 12 GB of a 16 GB machine is a setting somebody typed, and it is the thing to change.
    /// </summary>
    [Fact]
    public void An_instance_that_is_most_of_the_machine_is_told_to_take_less()
    {
        var crash = """
            # There is insufficient memory for the Java Runtime Environment to continue.
            # Native memory allocation (mmap) failed to map 2097152 bytes. Error detail: G1 virtual space

            Command Line: -Xms1024M -Xmx12288M net.minecraft.client.main.Main

            Memory: 4k page, system-wide physical 16000M (200M free)
            TotalPageFile size 20000M (AvailPageFile size 60M)
            """;

        Assert.Contains("would help", CrashAnalyzer.Analyze(crash, Installed).Advice);
    }

    /// <summary>RAM gone with page file to spare, which is the other way round.</summary>
    [Fact]
    public void A_machine_out_of_ram_is_said_to_be_out_of_ram()
    {
        var crash = """
            # There is insufficient memory for the Java Runtime Environment to continue.
            # Native memory allocation (malloc) failed to allocate 958736 bytes. Error detail: Chunk::new

            Command Line: -Xmx4096M net.minecraft.client.main.Main

            Memory: 4k page, system-wide physical 16000M (120M free)
            TotalPageFile size 20000M (AvailPageFile size 9000M)
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Installed);

        Assert.Equal(CrashCause.MachineOutOfMemory, analysis.Cause);
        Assert.DoesNotContain("page file", analysis.Advice, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Closing whatever else is running", analysis.Advice);
    }

    /// <summary>
    /// The button that was pointing the wrong way.
    ///
    /// Both of these used to come back as CrashCause.OutOfMemory, which the crash sheet answers
    /// with "give it more" — on a crash caused by the machine having nothing left to give. The
    /// cause is its own now, so that row cannot fire, and the only memory button offered here is
    /// the one that takes memory away, and only when the instance is most of the problem.
    /// </summary>
    [Fact]
    public void A_machine_that_ran_out_is_never_answered_with_give_the_game_more()
    {
        Assert.Equal(CrashCause.MachineOutOfMemory, CrashAnalyzer.Analyze(PageFileFull, Installed).Cause);
    }

    /// <summary>4 GB of a 32 GB machine is not what filled it, so there is nothing to take off.</summary>
    [Fact]
    public void Nothing_is_offered_to_lower_when_the_instance_is_not_the_problem()
    {
        Assert.Null(CrashAnalyzer.Analyze(PageFileFull, Installed).LowerMemoryToMb);
    }

    /// <summary>
    /// And 12 GB of a 16 GB machine is. Half the machine is the figure, because the other half
    /// pays for the JVM itself, the driver, the textures and the world being read off disk.
    /// </summary>
    [Fact]
    public void A_heap_too_big_for_the_machine_is_offered_a_figure_that_fits()
    {
        var crash = """
            # There is insufficient memory for the Java Runtime Environment to continue.
            # Native memory allocation (mmap) failed to map 2097152 bytes. Error detail: G1 virtual space

            Command Line: -Xms1024M -Xmx12288M net.minecraft.client.main.Main

            Memory: 4k page, system-wide physical 16000M (200M free)
            TotalPageFile size 20000M (AvailPageFile size 60M)
            """;

        // Half of 16000 MB is 8000, which lands on 7680 once it is put on the planner's own
        // half-gigabyte step — downwards, since the point of the figure is to fit.
        Assert.Equal(7680, CrashAnalyzer.Analyze(crash, Installed).LowerMemoryToMb);
    }

    /// <summary>
    /// An error file with no figures in it at all — truncated, or from a platform that words them
    /// differently. The verdict still stands; it just cannot be specific.
    /// </summary>
    [Fact]
    public void A_file_with_no_figures_still_reaches_a_verdict()
    {
        var crash = """
            # There is insufficient memory for the Java Runtime Environment to continue.
            # Native memory allocation (mmap) failed to map 2097152 bytes.
            """;

        Assert.Equal(CrashCause.MachineOutOfMemory, CrashAnalyzer.Analyze(crash, Installed).Cause);
    }
}
