using Asobu.Core.Diagnostics;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// Which mod a crash report is actually about.
///
/// From a real one that got it wrong. A Forge crash whose every fatal frame belonged to
/// WebDisplays came back accusing ImmediatelyFast, which had nothing to do with it: Forge stamps
/// the whole list of loaded mixin configs onto the end of every frame it prints, and counting
/// those counted how long the trace was rather than what was in it.
/// </summary>
public class CrashAttributionTests
{
    private static ModEntry Mod(string file, string name, string? id = null) =>
        new($"C:/mods/{file}", file, name, "", id, 1024, true, null);

    private static readonly IReadOnlyList<ModEntry> Installed =
    [
        Mod("webdisplays-1.3.3.jar", "Web Displays", "webdisplays"),
        Mod("mcef-1.2.5.jar", "MCEF", "forgecef"),
        Mod("ImmediatelyFast-Forge-1.4.0+1.19.2.jar", "ImmediatelyFast", "immediatelyfast"),
        Mod("entityculling-forge-1.6.1-mc1.19.2.jar", "EntityCulling", "entityculling"),
        Mod("embeddium-0.3.18.1+mc1.19.2.jar", "Embeddium", "embeddium"),
        Mod("geckolib-forge-1.19-3.1.40.jar", "GeckoLib", "geckolib3"),
        Mod("Essential (forge_1.19.2).jar", "Essential", "essential"),
    ];

    /// <summary>
    /// The shape that broke it, cut down but not changed: a package name that looks nothing like
    /// its mod, and the mixin list Forge tacks onto every frame.
    /// </summary>
    private const string WebDisplaysCrash = """
        ---- Minecraft Crash Report ----
        Description: Rendering Block Entity

        java.lang.NullPointerException: Cannot invoke "net.montoyo.mcef.api.API.createBrowser(String)" because the return value of "net.montoyo.wd.client.ClientProxy.getMCEF()" is null
        	at net.montoyo.wd.entity.TileEntityScreen$Screen.createBrowser(TileEntityScreen.java:225) ~[webdisplays-1.3.3.jar%23265!/:1.3.3] {re:classloading}
        	at net.montoyo.wd.client.renderers.ScreenRenderer.render(ScreenRenderer.java:48) ~[webdisplays-1.3.3.jar%23265!/:1.3.3] {re:classloading}
        	at net.minecraft.client.renderer.blockentity.BlockEntityRenderDispatcher.m_112284_(BlockEntityRenderDispatcher.java:90) ~[client-1.19.2.jar%23267!/:?] {re:mixin,pl:mixin:APP:entityculling.mixins.json:BlockEntityRenderDispatcherMixin,pl:mixin:APP:immediatelyfast-common.mixins.json:core.MixinMinecraftClient,pl:mixin:A}
        	at net.minecraft.client.Minecraft.m_91383_(Minecraft.java:1115) ~[client-1.19.2.jar%23267!/:?] {re:mixin,pl:mixin:APP:immediatelyfast-common.mixins.json:core.MixinMinecraftClient,pl:mixin:APP:entityculling.mixins.json:ClientWorldMixin,pl:mixin:APP:immediatelyfast-common.mixins.json:hud_batching.MixinMinecraftClient,pl:mixin:A}
        	at net.minecraft.client.main.Main.main(Main.java:51) ~[1.19.2.jar:?] {re:classloading}

        -- Head --
        Thread: Render thread
        Suspected Mods:
        	WebDisplays (webdisplays), Version: 1.3.3
        		Issue tracker URL:
        		at TRANSFORMER/webdisplays@1.3.3/net.montoyo.wd.entity.TileEntityScreen$Screen.createBrowser(TileEntityScreen.java:225)

        	Embeddium (embeddium), Version: 0.3.18.1+mc1.19.2
        		at TRANSFORMER/embeddium@0.3.18.1/me.jellysquid.mods.sodium.client.render.SodiumWorldRenderer.renderTileEntities(SodiumWorldRenderer.java:329)
        Stacktrace:
        	at net.minecraft.client.renderer.blockentity.BlockEntityRenderDispatcher.m_112278_(BlockEntityRenderDispatcher.java:104) ~[client-1.19.2.jar%23267!/:?] {re:mixin}

        -- System Details --
        Details:
        	Minecraft Version: 1.19.2
        	Memory: 361640112 bytes (344 MiB) / 1962934272 bytes (1872 MiB) up to 4294967296 bytes (4096 MiB)
        """;

    // ---- the mod it is actually about ----

    [Fact]
    public void The_mod_whose_frames_killed_the_game_is_the_one_named()
    {
        var analysis = CrashAnalyzer.Analyze(WebDisplaysCrash, Installed);

        Assert.Equal(CrashCause.Mod, analysis.Cause);
        Assert.Equal("Web Displays", analysis.Suspects[0].Name);
    }

    /// <summary>
    /// The bug. These two are only mentioned because Forge lists every loaded mixin config on
    /// every frame, including frames belonging to somebody else's classes.
    /// </summary>
    [Fact]
    public void A_mod_named_only_by_the_loaders_boilerplate_is_not_accused()
    {
        var accused = CrashAnalyzer.Analyze(WebDisplaysCrash, Installed).Suspects.Select(s => s.Name).ToList();

        Assert.DoesNotContain("ImmediatelyFast", accused);
        Assert.DoesNotContain("EntityCulling", accused);
    }

    /// <summary>
    /// WebDisplays ships as net.montoyo.wd. Nothing in that says "webdisplays", so matching the
    /// package against mod names found it nowhere — but Forge writes the jar on the frame.
    /// </summary>
    [Fact]
    public void A_mod_whose_package_looks_nothing_like_its_name_is_still_found()
    {
        const string report = """
            ---- Minecraft Crash Report ----
            java.lang.NullPointerException
            	at net.montoyo.wd.entity.TileEntityScreen.createBrowser(TileEntityScreen.java:225) ~[webdisplays-1.3.3.jar%23265!/:1.3.3] {re:classloading}
            """;

        Assert.Equal("Web Displays", CrashAnalyzer.Analyze(report, Installed).Suspects[0].Name);
    }

    /// <summary>The other way Forge writes it, in front of the class rather than after it.</summary>
    [Fact]
    public void The_transformer_prefix_names_a_mod_too()
    {
        const string report = """
            ---- Minecraft Crash Report ----
            java.lang.IndexOutOfBoundsException: Index -1 out of bounds for length 0
            	at TRANSFORMER/geckolib3@3.1.40/software.bernie.geckolib3.file.AnimationFileLoader.loadAllAnimations(AnimationFileLoader.java:30)
            """;

        Assert.Equal("GeckoLib", CrashAnalyzer.Analyze(report, Installed).Suspects[0].Name);
    }

    /// <summary>A jar whose name has a space arrives escaped, and has to be unescaped to match.</summary>
    [Fact]
    public void An_escaped_jar_name_still_matches_the_file_on_disk()
    {
        const string report = """
            ---- Minecraft Crash Report ----
            java.lang.RuntimeException: boom
            	at gg.essential.ice.stun.StunSocket.send(StunSocket.kt:116) ~[Essential%20(forge_1.19.2).jar%23296!/:?] {re:classloading}
            """;

        Assert.Equal("Essential", CrashAnalyzer.Analyze(report, Installed).Suspects[0].Name);
    }

    /// <summary>Forge lists more than one, and the second used to be thrown away.</summary>
    [Fact]
    public void Every_mod_Forge_suspected_is_carried_through()
    {
        var accused = CrashAnalyzer.Analyze(WebDisplaysCrash, Installed).Suspects.Select(s => s.Name).ToList();

        Assert.Contains("Web Displays", accused);
        Assert.Contains("Embeddium", accused);
    }

    // ---- and when memory is the answer ----

    /// <summary>
    /// A heap against its ceiling with nothing in the log saying so. The JVM does not always get
    /// to write an OutOfMemoryError, and the report records where the heap stood regardless.
    /// </summary>
    [Fact]
    public void A_heap_at_its_ceiling_is_called_out_of_memory()
    {
        const string report = """
            ---- Minecraft Crash Report ----
            Description: Ticking entity
            java.lang.NullPointerException
            	at net.minecraft.client.Minecraft.m_91383_(Minecraft.java:1115)
            -- System Details --
            	Memory: 104857600 bytes (100 MiB) / 4194304000 bytes (4000 MiB) up to 4294967296 bytes (4096 MiB)
            """;

        var analysis = CrashAnalyzer.Analyze(report, Installed);

        Assert.Equal(CrashCause.OutOfMemory, analysis.Cause);
        Assert.Contains("3900 MB", analysis.Advice);
        Assert.Contains("4096 MB", analysis.Advice);
    }

    /// <summary>
    /// And the case that matters more: this crash had plenty of memory left, so saying otherwise
    /// would send somebody raising a limit that was never the problem.
    /// </summary>
    [Fact]
    public void A_heap_with_room_left_is_not_blamed_for_the_crash()
    {
        Assert.NotEqual(CrashCause.OutOfMemory, CrashAnalyzer.Analyze(WebDisplaysCrash, Installed).Cause);
    }

    /// <summary>When the game did say so, the numbers go in the message rather than being reread.</summary>
    [Fact]
    public void A_reported_out_of_memory_says_how_much_was_in_use()
    {
        const string report = """
            ---- Minecraft Crash Report ----
            java.lang.OutOfMemoryError: Java heap space
            	at net.minecraft.client.Minecraft.m_91383_(Minecraft.java:1115)
            -- System Details --
            	Memory: 20971520 bytes (20 MiB) / 2147483648 bytes (2048 MiB) up to 2147483648 bytes (2048 MiB)
            """;

        var analysis = CrashAnalyzer.Analyze(report, Installed);

        Assert.Equal(CrashCause.OutOfMemory, analysis.Cause);
        Assert.Contains("2028 MB", analysis.Advice);
    }
}
