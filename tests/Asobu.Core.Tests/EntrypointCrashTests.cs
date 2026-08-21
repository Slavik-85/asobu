using Asobu.Core.Diagnostics;
using Asobu.Core.Mods;

namespace Asobu.Core.Tests;

/// <summary>
/// A Fabric startup crash, taken from a real one.
///
/// This is the case that showed the analyser blaming bystanders. Fabric refused to start because
/// essential-loader's preLaunch entrypoint could not be cast — and the log, further up, carried
/// four unrelated mentions of mixin config files: one a missing-refmap warning that says in the
/// same sentence that it can be ignored, and three companion plugins that failed for the same
/// underlying reason as the crash rather than causing it.
///
/// Every one of those names was being read as the loader naming a culprit. The mod that actually
/// stopped the game was named by a line nothing matched, so it was never accused at all: four
/// healthy mods on screen as "named in the crash", and the real one absent.
/// </summary>
public class EntrypointCrashTests
{
    private const string Report = """
        [00:15:29] [main/WARN]: Reference map 'clumps.refmap.json' for clumps.mixins.json could not be read. If this is a development environment you can ignore this message
        [00:15:30] [main/ERROR]: Error loading companion plugin class [net.raphimc.immediatelyfast.injection.ImmediatelyFastMixinPlugin] for mixin config [immediatelyfast-common.mixins.json]. The plugin may be out of date: ClassCastException
        [00:15:30] [main/ERROR]: Error loading companion plugin class [ru.vidtu.ksyxis.platform.KPlugin] for mixin config [ksyxis.mixins.json]. The plugin may be out of date: ClassCastException
        [00:15:30] [main/ERROR]: Error loading companion plugin class [com.memorysettings.MixinConfig] for mixin config [memorysettings.mixins.json]. The plugin may be out of date: ClassCastException
        [00:15:31] [main/ERROR]: Exception
        net.fabricmc.loader.impl.FormattedException: java.lang.RuntimeException: Could not execute entrypoint stage 'preLaunch' due to errors, provided by 'essential-loader' at 'gg.essential.loader.stage0.EssentialSetupPreLaunch'!
        	at net.fabricmc.loader.impl.FabricLoaderImpl.lambda$invokeEntrypoints$0(FabricLoaderImpl.java:413)
        	at net.fabricmc.loader.impl.util.ExceptionUtil.gatherExceptions(ExceptionUtil.java:33)
        Caused by: java.lang.RuntimeException: Could not execute entrypoint stage 'preLaunch' due to errors, provided by 'essential-loader' at 'gg.essential.loader.stage0.EssentialSetupPreLaunch'!
        	Suppressed: net.fabricmc.loader.api.EntrypointException: Exception while loading entries for entrypoint 'preLaunch' provided by 'sodium'
        Caused by: net.fabricmc.loader.api.EntrypointException: Exception while loading entries for entrypoint 'preLaunch' provided by 'essential-loader'
        Caused by: net.fabricmc.loader.api.LanguageAdapterException: Class gg.essential.loader.stage0.EssentialSetupPreLaunch cannot be cast to net.fabricmc.loader.api.entrypoint.PreLaunchEntrypoint!
        """;

    private static ModEntry Mod(string id, string file) =>
        new(System.IO.Path.Combine("mods", file), file, id, "Someone", id, 1024, true, null);

    private static readonly List<ModEntry> Installed =
    [
        Mod("essential-loader", "essential-fabric_1.21.4-1.3.9.jar"),
        Mod("sodium", "sodium-fabric-0.6.0.jar"),
        Mod("clumps", "Clumps-fabric-1.21.4-21.0.0.5.jar"),
        Mod("immediatelyfast", "ImmediatelyFast-1.16.3+1.21.4.jar"),
        Mod("ksyxis", "Ksyxis-1.4.3.jar"),
        Mod("memorysettings", "memorysettings-1.21.4-6.0.jar"),
    ];

    /// <summary>
    /// The cause is deliberately not pinned to one value. A report like this can be read as "a mod
    /// did it" or, more usefully, as "that mod's build does not fit" — and the second is an
    /// improvement on the first. What must not drift is which mod is named.
    /// </summary>
    [Fact]
    public void Blames_the_mod_whose_entrypoint_threw()
    {
        var analysis = CrashAnalyzer.Analyze(Report, Installed);

        Assert.True(analysis.HasVerdict, "no verdict at all for a crash that names its own cause");
        Assert.Contains("essential", analysis.Suspects[0].Name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The heart of it: a mod mentioned in passing must not be presented as the culprit.</summary>
    [Fact]
    public void Does_not_put_bystanders_at_the_top()
    {
        var analysis = CrashAnalyzer.Analyze(Report, Installed);
        var top = analysis.Suspects[0].Name;

        foreach (var innocent in (string[])["Clumps", "ImmediatelyFast", "Ksyxis", "memorysettings"])
            Assert.DoesNotContain(innocent, top, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A warning that says it can be ignored is not evidence of anything, and Clumps appears in
    /// this log exactly once, in one of those.
    /// </summary>
    [Fact]
    public void Ignores_a_line_that_says_to_ignore_it()
    {
        var analysis = CrashAnalyzer.Analyze(Report, Installed);

        Assert.DoesNotContain(analysis.Suspects, s =>
            s.Name.Contains("Clumps", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// "Named in the crash" is a claim about how sure we are, and it has to be earned. A mixin
    /// config filename turning up in a log does not earn it.
    /// </summary>
    [Fact]
    public void Only_claims_a_mod_was_named_when_it_was()
    {
        var analysis = CrashAnalyzer.Analyze(Report, Installed);

        foreach (var suspect in analysis.Suspects.Where(s => s.NamedDirectly))
        {
            Assert.True(
                suspect.Name.Contains("essential", StringComparison.OrdinalIgnoreCase)
                || suspect.Name.Contains("sodium", StringComparison.OrdinalIgnoreCase),
                $"{suspect.Name} was reported as named in the crash, but the loader never named it");
        }
    }

    /// <summary>Sodium's entrypoint threw too, so it belongs in the list — under the one that killed it.</summary>
    [Fact]
    public void Keeps_the_suppressed_failure_as_a_lesser_suspect()
    {
        var analysis = CrashAnalyzer.Analyze(Report, Installed);

        var essential = analysis.Suspects.FirstOrDefault(s =>
            s.Name.Contains("essential", StringComparison.OrdinalIgnoreCase));
        var sodium = analysis.Suspects.FirstOrDefault(s =>
            s.Name.Contains("sodium", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(essential);
        if (sodium is not null) Assert.True(essential!.Score >= sodium.Score);
    }
}
