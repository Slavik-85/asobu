using Asobu.Core.Diagnostics;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Crashes from one real modpack that the analyser got wrong, cut down to the lines that decide
/// them. Each was checked against what actually fixed it: Hyperslide or Vivecraft off, JEI rolled
/// back, lrtactical rolled back, the CurseForge app's overlay gone.
/// </summary>
public class RealCrashTests
{
    private static ModEntry Mod(string file, string name, string? id) =>
        new($"C:/mods/{file}", file, name, "", id, 1024, true, null);

    private static readonly IReadOnlyList<ModEntry> Ham =
    [
        Mod("Essential_1-5-0-1_forge_1-20-1.jar", "Essential", "essential"),
        Mod("hyperslide-3.2-frog-1.20.1.jar", "Hyperslide", "hyperslide"),
        Mod("vivecraft-1.20.1-1.3.15-forge.jar", "Vivecraft", "vivecraft"),
        Mod("jei-1.20.1-forge-15.70.0.221.jar", "Just Enough Items", "jei"),
        Mod("mezz_config-1.20.1-forge-0.6.8.jar", "MezzConfig", "mezz_config"),
        Mod("buters_knives-1.1.0-forge-1.20.1.jar", "buter's knives", "buters_knives"),
        Mod("Knifes pack from cs2-0.2.1.jar", "Shidzuko Melee Pack", "cs2_wt"),
        Mod("lrtactical-1.20.1-0.4.3.jar", "LesRaisins Tactical Equipments", "lrtactical"),
        Mod("ferritecore-6.0.1-forge.jar", "Ferrite Core", "ferritecore"),
    ];

    private const string MixinCrash = """
        ---- Minecraft Crash Report ----
        Description: Unexpected error

        java.lang.RuntimeException: org.spongepowered.asm.mixin.transformer.throwables.MixinTransformerError: An unexpected critical error was encountered
        	at gg.essential.gui.common.EssentialTooltip.render(EssentialTooltip.kt:40) ~[Essential%20(forge_1.20.1).jar%23412!/:?] {re:classloading}
        Caused by: org.spongepowered.asm.mixin.throwables.MixinApplyError: Mixin [vivecraft.mixins.json:client_vr.KeyboardInputVRMixin] from phase [DEFAULT] in config [vivecraft.mixins.json] FAILED during APPLY
        	at org.spongepowered.asm.mixin.transformer.MixinProcessor.handleMixinError(MixinProcessor.java:636) ~[mixin-0.8.5.jar:0.8.5] {}
        Caused by: org.spongepowered.asm.mixin.injection.throwables.InvalidInjectionException: @At("INVOKE") on net/minecraft/client/player/KeyboardInput::vivecraft$noMovementWhenClimbing with priority 1000 cannot inject into net/minecraft/client/player/KeyboardInput::m_214106_(ZF)V merged by hyperslide.mixins.DisableMovementMovementarrowsMixin with priority 1000 [PREINJECT Applicator Phase -> vivecraft.mixins.json:client_vr.KeyboardInputVRMixin]
        	at org.spongepowered.asm.mixin.injection.code.Injector.findTargetNodes(Injector.java:305) ~[mixin-0.8.5.jar%23100!/:0.8.5] {}

        -- Head --
        Thread: Render thread
        Suspected Mod:
        	Essential (essential), Version: 1.5.0.1
        		at TRANSFORMER/essential@1.5.0.1/gg.essential.gui.common.EssentialTooltip.render(EssentialTooltip.kt:40)
        Stacktrace:
        	at gg.essential.gui.common.EssentialTooltip.render(EssentialTooltip.kt:40) ~[Essential%20(forge_1.20.1).jar%23412!/:?] {re:classloading}
        """;

    [Fact]
    public void A_mixin_clash_names_both_mods_and_not_the_one_that_loaded_the_class()
    {
        var analysis = CrashAnalyzer.Analyze(MixinCrash, Ham);

        Assert.Equal(CrashCause.Mod, analysis.Cause);
        Assert.Equal(["Hyperslide", "Vivecraft"], analysis.Suspects.Select(s => s.Name));
        Assert.All(analysis.Suspects, s => Assert.True(s.NamedDirectly));
    }

    private const string Overwolf = """
        # A fatal error has been detected by the Java Runtime Environment:
        #
        #  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffb6f4ff7c0, pid=19776, tid=33136
        #
        # Problematic frame:
        # C  [owe-client-x64.dll+0x43f7c0]
        #
        # An error report file with more information is saved as:
        # C:\Users\slavi\AppData\Local\Asobu\data\instances\Ham Modpack\minecraft\hs_err_pid19776.log
        """;

    [Fact]
    public void Java_dying_after_the_game_printed_its_own_crash_answers_with_that_crash()
    {
        var log = "[20:46:01] [Render thread/INFO] [minecraft/Minecraft]: Loaded\n" + MixinCrash
            + "\n[20:46:02] [Render thread/INFO] [STDOUT/]: #@!@# Game crashed! Crash report saved to: #@!@# crash.txt\n"
            + "activate hook: GetRawInputBuffer#\n" + Overwolf;

        // The same answer whether or not the exit code survived: Java's own access violation
        // is not the graphics driver's.
        foreach (var exit in new[] { -1, unchecked((int)0xC0000005) })
        {
            var analysis = CrashAnalyzer.Analyze(log, Ham, exit);

            Assert.Equal(CrashCause.Mod, analysis.Cause);
            Assert.Equal("Hyperslide", analysis.Suspects[0].Name);
            Assert.Contains("closing", analysis.Advice);
        }
    }

    [Fact]
    public void An_overlay_crashing_a_normal_quit_is_the_overlay_and_nothing_lost()
    {
        var log = "[23:30:01] [Render thread/INFO] [minecraft/Minecraft]: Stopping!\nactivate hook: GetRawInputBuffer#\n" + Overwolf;

        var analysis = CrashAnalyzer.Analyze(log, Ham, unchecked((int)0xC0000005));

        Assert.Equal(CrashCause.Overlay, analysis.Cause);
        Assert.Contains("Overwolf", analysis.Headline);
        Assert.Empty(analysis.Suspects);
    }

    [Fact]
    public void A_handled_exception_before_a_normal_quit_is_not_a_crash()
    {
        const string log = """
            [21:51:02] [Essential STUN/ERROR] [gg.essential/]: Failed to send
            java.net.SocketException: Network is unreachable
            	at java.net.DatagramSocket.send(DatagramSocket.java:664) ~[?:?] {}
            	at malte0811.ferritecore.mixin.SomeHandler.run(SomeHandler.java:116) ~[ferritecore-6.0.1-forge.jar%23300!/:?] {}
            [21:51:03] [CullThread/INFO] [STDOUT/]: Shutting down culling task!
            [21:51:03] [Render thread/INFO] [minecraft/Minecraft]: Stopping!
            """;

        Assert.Equal(CrashCause.Clean, CrashAnalyzer.Analyze(log, Ham).Cause);
    }

    [Fact]
    public void A_broken_JEI_build_is_the_wrong_build_of_JEI_not_MezzConfig()
    {
        const string crash = """
            ---- Minecraft Crash Report ----
            Description: Rendering screen

            java.lang.AbstractMethodError: Receiver class mezz.jei.gui.input.handlers.SearchInputLayer does not define or inherit an implementation of the resolved method 'boolean isMouseOver(double, double)' of interface mezz.jei.common.input.IGuiInputLayer.
            	at mezz.jei.gui.events.GuiEventHandler.lambda$drawOverlays$3(GuiEventHandler.java:156) ~[jei-1.20.1-forge-15.70.0.221.jar%23307!/:15.70.0.221] {re:classloading}
            	at java.util.stream.MatchOps$1MatchSink.accept(MatchOps.java:90) ~[?:?] {}

            -- Head --
            Thread: Render thread
            Suspected Mod:
            	Just Enough Items (jei), Version: 15.70.0.221
            		at TRANSFORMER/jei@15.70.0.221/mezz.jei.gui.events.GuiEventHandler.lambda$drawOverlays$3(GuiEventHandler.java:156)
            Stacktrace:
            	at mezz.jei.gui.events.GuiEventHandler.lambda$drawOverlays$3(GuiEventHandler.java:156) ~[jei-1.20.1-forge-15.70.0.221.jar%23307!/:15.70.0.221] {re:classloading}
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Ham);

        Assert.Equal(CrashCause.WrongBuild, analysis.Cause);
        Assert.Equal("Just Enough Items", Assert.Single(analysis.Suspects).Name);
    }

    private const string ForgeTable = """
        [20:26:16] [main/ERROR] [ne.mi.fm.lo.ModSorter/LOADING]: Missing or unsupported mandatory dependencies:
        	Mod ID: 'lrtactical', Requested by: 'cs2_wt', Expected range: '[0.2.4,0.3.0]', Actual version: '0.4.3'
        	Mod ID: 'mcreator_link', Requested by: 'buters_knives', Expected range: '[0,)', Actual version: '[MISSING]'
        [20:26:17] [main/INFO] [mixin/]: Compatibility level set to JAVA_17
        """;

    [Fact]
    public void Forges_table_names_the_missing_mod_rather_than_its_column_heading()
    {
        var analysis = CrashAnalyzer.Analyze(ForgeTable + "Exception in thread \"main\" java.lang.RuntimeException\n", Ham);

        Assert.Equal(CrashCause.MissingDependency, analysis.Cause);
        Assert.Equal("mcreator_link", analysis.Missing?.Id);
        Assert.Equal("buter's knives", Assert.Single(analysis.Suspects).Name);
    }

    [Fact]
    public void A_dependency_that_is_too_new_says_to_go_back_and_still_fetches_the_missing_one()
    {
        var log = ForgeTable + """
            net.minecraftforge.fml.ModLoadingException: Mod §ecs2_wt§r requires §6lrtactical§r §obetween 0.2.4 and 0.3.0 (inclusive)§r
            §7Currently, §6lrtactical§r§7 is §o0.4.3
            	at net.minecraftforge.fml.ModLoader.lambda$gatherAndInitializeMods$1(ModLoader.java:177) ~[fmlcore-1.20.1-47.4.0.jar%23251!/:?] {}
            """;

        var analysis = CrashAnalyzer.Analyze(log, Ham);

        Assert.Contains("too new", analysis.Headline);
        Assert.Contains("0.3.0 or earlier", analysis.Advice);
        Assert.Equal("mcreator_link", analysis.Missing?.Id);
    }

    /// <summary>Forge's commonest range, [a,b), in its own words. The ceiling is excluded, so b itself is too new.</summary>
    [Fact]
    public void A_dependency_at_an_excluded_ceiling_is_too_new()
    {
        const string crash = """
            ---- Minecraft Crash Report ----
            	Mod File: /C:/mods/Knifes pack from cs2-0.2.1.jar
            	Failure message: Mod cs2_wt requires lrtactical 0.2.4 or above, and below 0.3.0
            		Currently, lrtactical is 0.3.0
            """;

        var analysis = CrashAnalyzer.Analyze(crash, Ham);

        Assert.Contains("too new", analysis.Headline);
        Assert.Contains("below 0.3.0", analysis.Advice);
    }

    [Fact]
    public void A_maven_range_closed_with_a_square_bracket_takes_its_ceiling_in()
    {
        var bound = ModConflicts.Bound("[0.2.4,0.3.0]");

        Assert.True(bound.Accepts("0.3.0"));
        Assert.False(bound.Accepts("0.4.3"));
        Assert.False(ModConflicts.Bound("[0.2.4,0.3.0)").Accepts("0.3.0"));
    }

    [Fact]
    public void Essentials_routine_checksum_note_does_not_make_every_crash_a_damaged_download()
    {
        var log = "[12:00:00] [Essential/WARN]: Checksum mismatch for cosmetic-cache entry 1f3a, refetching\n" + MixinCrash;

        Assert.NotEqual(CrashCause.CorruptFiles, CrashAnalyzer.Analyze(log, Ham).Cause);
    }

    [Fact]
    public void Forges_early_window_names_the_card_and_driver()
    {
        const string log = """
            <log4j:Event logger="EARLYDISPLAY" level="INFO"><log4j:Message><![CDATA[GL info: NVIDIA GeForce RTX 4070/PCIe/SSE2 GL version 4.6.0 NVIDIA 617.14, NVIDIA Corporation]]></log4j:Message></log4j:Event>
            [12:00:00] [Render thread/INFO]: Loaded
            """;

        var analysis = CrashAnalyzer.Analyze(log, Ham, unchecked((int)0xC0000005));

        Assert.Equal(CrashCause.Graphics, analysis.Cause);
        Assert.Equal("NVIDIA GeForce RTX 4070 stopped the game", analysis.Headline);
        Assert.Contains("617.14", analysis.Advice);
    }
}
