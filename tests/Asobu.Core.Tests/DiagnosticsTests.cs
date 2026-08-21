using Asobu.Core.Diagnostics;

namespace Asobu.Core.Tests;

/// <summary>
/// What the launcher reads out of a launch log, and — just as much — what it then says about it.
///
/// The wording is asserted alongside the parse on purpose. Every one of these findings exists to
/// be shown to somebody, and a row that parsed perfectly and then renders as "wants a different
/// version" has failed at the only thing it was for.
/// </summary>
public class DiagnosticsTests
{
    // Fabric's own words, from a real refusal to start: the suggested fix, then the detail
    // underneath explaining which mod objected and to what.
    private const string FabricIncompatible = """
        Incompatible mod set!
        net.fabricmc.loader.impl.FormattedException: Some of your mods are incompatible with the game or each other!

        A potential solution has been determined, this may resolve your problem:
            - Replace mod 'Sodium' (sodium) 0.9.2-alpha.4 with any 0.9.x version that is compatible with:
                - iris 1.11.2

        More details:
            - Mod 'Iris' (iris) 1.11.2 is incompatible with version 0.9.2-alpha.4 or earlier of mod 'Sodium' (sodium), yet a conflicting version is present: 0.9.2-alpha.4!
        """;

    // A breakage the loader had no suggestion for, which is the shape that has to be described
    // from the incompatibility line alone.
    private const string FabricBreakageOnly = """
        Incompatible mod set!
            - Mod 'Iris' (iris) 1.11.2 is incompatible with version 0.6.0 or earlier of mod 'Indium' (indium), yet a conflicting version is present: 0.6.0!
        """;

    private const string FabricWrongVersion = """
        Mod 'Create' (create) 6.0.4 requires version 0.16.0 or later of fabric-api, but only the wrong version is present: fabric-api 0.15.11!
        """;

    private const string FabricMissing = """
        Mod 'Create' (create) 6.0.4 requires any version of mod 'Fabric API' (fabric-api), which is missing!
        """;

    private const string ForgeTable = """
        Missing or unsupported mandatory dependencies:
            Mod ID: 'jei', Requested by: 'ars_nouveau', Expected range: '[15.2.0,)', Actual version: '15.0.0'
            Mod ID: 'curios', Requested by: 'ars_nouveau', Expected range: '[5.1.0,)', Actual version: '[MISSING]'
        """;

    [Fact]
    public void Conflict_DescribesAnExclusiveFloorRatherThanShrugging()
    {
        var conflict = Assert.Single(ModConflicts.Find(FabricBreakageOnly));

        // The bound the loader stated is "past 0.6.0". Saying so is the whole value of the row:
        // "a different version" is what it looked like before Above was a case in WantedLabel,
        // and it is indistinguishable from every other row on the screen.
        Assert.Equal("later than 0.6.0", conflict.WantedLabel);
        Assert.DoesNotContain("a different version", conflict.Detail);
    }

    [Fact]
    public void Conflict_ReadsFabricsOwnSuggestedFix()
    {
        var conflict = ModConflicts.Find(FabricIncompatible)
            .Single(c => c.ModId.Equals("sodium", StringComparison.OrdinalIgnoreCase));

        Assert.Equal("0.9.2-alpha.4", conflict.Present);
        Assert.Equal("0.9", conflict.Wanted.AtLeast);
        Assert.Equal("0.10", conflict.Wanted.Below);
        Assert.Equal("0.9 up to 0.10", conflict.WantedLabel);
    }

    [Fact]
    public void Conflict_CarriesTheOtherEndOfTheDisagreement()
    {
        var conflict = ModConflicts.Find(FabricIncompatible)
            .Single(c => c.ModId.Equals("sodium", StringComparison.OrdinalIgnoreCase));

        // Fabric only ever proposes moving one of the two. When no build of Sodium fits, moving
        // Iris instead is the remaining answer — so the row has to know Iris is the other end.
        Assert.NotNull(conflict.Alternative);
        Assert.Equal("iris", conflict.Alternative!.ModId);
    }

    [Fact]
    public void Conflict_DoesNotOfferTheSameDisagreementTwice()
    {
        // One problem, one row. The suggested fix and the incompatibility line underneath it
        // describe the same pair of mods, and offering both invites fixing it from both ends.
        Assert.Single(ModConflicts.Find(FabricIncompatible));
    }

    [Fact]
    public void Conflict_ReadsAWrongVersionThatIsPresent()
    {
        var conflict = Assert.Single(ModConflicts.Find(FabricWrongVersion));

        Assert.Equal("fabric-api", conflict.ModId);
        Assert.Equal("0.15.11", conflict.Present);
        Assert.Equal("0.16.0 or later", conflict.WantedLabel);
    }

    [Fact]
    public void Conflict_LeavesForgesMissingRowToTheDependencyReader()
    {
        var found = ModConflicts.Find(ForgeTable);

        // '[MISSING]' means absent, not out of date. A swap for it could only ever fail.
        Assert.Equal(["jei"], found.Select(c => c.ModId));
    }

    [Fact]
    public void Dependency_ReadsWhatFabricSaysIsMissing()
    {
        var missing = Assert.Single(MissingDependencies.Find(FabricMissing));

        Assert.Equal("fabric-api", missing.Id);
        Assert.Equal("Fabric API", missing.Name);
        Assert.Equal("Create", missing.RequiredBy);
    }

    [Fact]
    public void Dependency_ReadsForgesMissingRow()
    {
        var missing = Assert.Single(MissingDependencies.Find(ForgeTable));

        Assert.Equal("curios", missing.Id);
        Assert.Equal("ars_nouveau", missing.RequiredBy);
    }

    /// <summary>The crash this was reported for, copied out of the launcher's own log.</summary>
    private const string DuplicateAsm = """
        Exception in thread "main" java.lang.ExceptionInInitializerError
            at net.fabricmc.loader.impl.launch.knot.KnotClient.main(KnotClient.java:23)
        Caused by: java.lang.IllegalStateException: duplicate ASM classes found on classpath: jar:file:/C:/Users/x/AppData/Local/Asobu/data/cache/libraries/org/ow2/asm/asm/9.10.1/asm-9.10.1.jar!/org/objectweb/asm/ClassReader.class, jar:file:/C:/Users/x/AppData/Local/Asobu/data/cache/libraries/org/ow2/asm/asm/9.6/asm-9.6.jar!/org/objectweb/asm/ClassReader.class
            at net.fabricmc.loader.impl.util.LoaderUtil.verifyClasspath(LoaderUtil.java:83)
        """;

    [Fact]
    public void Crash_NamesADuplicateLibraryRatherThanBlamingAMod()
    {
        var analysis = CrashAnalyzer.Analyze(DuplicateAsm, []);

        Assert.Equal(CrashCause.DuplicateLibrary, analysis.Cause);
        Assert.Contains("ASM", analysis.Headline);

        // The stack trace is Fabric's, so the mod heuristics would happily have found somebody
        // to accuse. Nobody's mods did this.
        Assert.Empty(analysis.Suspects);
    }

    [Fact]
    public void Crash_SaysWhoseFaultTheDuplicateWas()
    {
        var analysis = CrashAnalyzer.Analyze(DuplicateAsm, []);

        // Sending somebody off to uninstall mods over a launcher bug is the failure worth
        // avoiding here, so the advice has to say plainly that it is not their mods.
        Assert.Contains("None of your mods", analysis.Advice);
    }

    /// <summary>Word for word from the report, for a mod built for 1.21.1 run on 1.21.8.</summary>
    private const string WrongBuildCrash = """
        ---- Minecraft Crash Report ----
        Description: Initializing game

        java.lang.RuntimeException: Could not execute entrypoint stage 'main' due to errors, provided by 'corner-entity' at 'com.corner.entity.CornerEntity'!
        	at net.fabricmc.loader.impl.FabricLoaderImpl.lambda$invokeEntrypoints$0(FabricLoaderImpl.java:413)
        Caused by: java.lang.NoSuchMethodError: 'net.minecraft.class_1299 net.fabricmc.fabric.api.object.builder.v1.entity.FabricEntityTypeBuilder.build()'
        	at knot//com.corner.entity.CornerEntity.<clinit>(CornerEntity.java:50)
        """;

    private static readonly Asobu.Core.Mods.ModEntry CornerEntity = new(
        "C:/mods/corner-entity-2.0.0+1.21.1.jar", "corner-entity-2.0.0+1.21.1.jar",
        "Corner Entity", "someone", "corner-entity", 4096, true, null);

    [Fact]
    public void Crash_NamesTheModThatWasBuiltForAnotherVersion()
    {
        var analysis = CrashAnalyzer.Analyze(WrongBuildCrash, [CornerEntity]);

        Assert.Equal(CrashCause.WrongBuild, analysis.Cause);
        Assert.Contains("Corner Entity", analysis.Headline);

        // The suspect carries the file name so the sheet can find the jar and replace it.
        var suspect = Assert.Single(analysis.Suspects);
        Assert.Equal("corner-entity-2.0.0+1.21.1.jar", suspect.FileName);
        Assert.True(suspect.NamedDirectly);
    }

    [Fact]
    public void Crash_SaysTheFixIsAnotherBuildRatherThanRemoval()
    {
        var analysis = CrashAnalyzer.Analyze(WrongBuildCrash, [CornerEntity]);

        // The mod is wanted; the wrong build of it is the problem. Advice that says "turn it off"
        // would throw away a mod whose author has probably shipped one that works.
        Assert.Contains("build made for this instance", analysis.Advice);
    }

    [Fact]
    public void Crash_LeavesTheGuessingToTheSuspectHuntWhenTheModIsNotInstalled()
    {
        // Named a mod this instance does not have — claiming to know which jar to replace would
        // be a lie, so it falls through rather than inventing one.
        var analysis = CrashAnalyzer.Analyze(WrongBuildCrash, []);

        Assert.NotEqual(CrashCause.WrongBuild, analysis.Cause);
    }

    [Fact]
    public void Crash_DoesNotCallAnOrdinaryModCrashAWrongBuild()
    {
        // An entrypoint that threw something ordinary is a bug in the mod, not a version
        // mismatch, and the fix for it is not "fetch another build".
        const string ordinary = """
            java.lang.RuntimeException: Could not execute entrypoint stage 'main' due to errors, provided by 'corner-entity' at 'com.corner.entity.CornerEntity'!
            Caused by: java.lang.NullPointerException: Cannot invoke "java.lang.String.length()" because "s" is null
            	at knot//com.corner.entity.CornerEntity.<clinit>(CornerEntity.java:50)
            """;

        Assert.NotEqual(CrashCause.WrongBuild, CrashAnalyzer.Analyze(ordinary, [CornerEntity]).Cause);
    }

    [Fact]
    public void Log_UnwrapsSavedLog4jXmlTheWayTheLiveViewDoes()
    {
        // Straight out of a saved launch log: three lines of markup around one sentence.
        const string raw = """
            <log4j:Event logger="FabricLoader/GameProvider" timestamp="1787323148641" level="INFO" thread="main">
              <log4j:Message><![CDATA[Loading Minecraft 1.21.8 with Fabric Loader 0.19.3]]></log4j:Message>
            </log4j:Event>
            """;

        var line = Assert.Single(Formatted(raw));

        Assert.Contains("Loading Minecraft 1.21.8 with Fabric Loader 0.19.3", line.Text);
        Assert.Contains("FabricLoader/GameProvider", line.Text);
        Assert.DoesNotContain("log4j", line.Text);
        Assert.DoesNotContain("CDATA", line.Text);
        Assert.Equal(GameLogLevel.Info, line.Level);
    }

    [Fact]
    public void Log_ColoursAWarningAsAWarning()
    {
        const string raw = """
            <log4j:Event logger="Sodium-Workarounds" timestamp="1787323152213" level="WARN" thread="main">
              <log4j:Message><![CDATA[Sodium has applied one or more workarounds]]></log4j:Message>
            </log4j:Event>
            """;

        Assert.Equal(GameLogLevel.Warn, Assert.Single(Formatted(raw)).Level);
    }

    [Fact]
    public void Log_KeepsAStackTraceTogetherUnderItsError()
    {
        const string raw = """
            <log4j:Event logger="FabricLoader" timestamp="1787323132553" level="ERROR" thread="Essential Thread 3">
              <log4j:Message><![CDATA[Uncaught exception in thread "Essential Thread 3"]]></log4j:Message>
              <log4j:Throwable><![CDATA[gg.essential.minecraftauth.exception.AuthenticationException: expired
            	at knot//gg.essential.MicrosoftAuthenticationService.requestAccessToken(x.kt:146)
            ]]></log4j:Throwable>
            </log4j:Event>
            """;

        var lines = Formatted(raw);

        // The message and every frame beneath it stay one error rather than the frames going
        // quiet and separating from what they explain.
        Assert.True(lines.Count >= 2);
        Assert.All(lines, line => Assert.Equal(GameLogLevel.Error, line.Level));
        Assert.Contains(lines, line => line.Text.Contains("requestAccessToken"));
    }

    [Fact]
    public void Log_LeavesPlainTextAloneButStillReadsItsLevel()
    {
        // Crash reports are not XML at all, and plenty of mods print straight to stdout. None of
        // that should be mangled — but a stack trace is still an error.
        var lines = Formatted("java.lang.NoSuchMethodError: FabricEntityTypeBuilder.build()");

        Assert.Equal(GameLogLevel.Error, Assert.Single(lines).Level);
    }

    private static IReadOnlyList<GameLogLine> Formatted(string text)
    {
        var formatter = new GameLogFormatter();
        var lines = new List<GameLogLine>();

        foreach (var line in text.Split('\n')) lines.AddRange(formatter.Feed(line.TrimEnd('\r')));

        lines.AddRange(formatter.Drain());

        return lines;
    }

    /// <summary>
    /// The shape of the log a native fault leaves: a perfectly ordinary launch that simply
    /// stops. Every mod loaded, the game started, somebody played for six minutes — and then
    /// nothing. Lines taken from the report this was written for.
    /// </summary>
    private const string NativeFaultLog = """
        [10:53:28] [Render thread/INFO] [net.minecraft.client.Minecraft]: Using graphics device: Intel(R) UHD Graphics (Intel)
        [10:53:33] [Render thread/INFO] [Sodium-GlSurface]: OpenGL Vendor: Intel
        [10:53:12] [main/INFO] [Sodium-GraphicsAdapterProbe]: Found graphics adapter: AdapterInfo{vendor=INTEL, description='Intel(R) UHD Graphics', openglIcdVersion=31.0.101.4255}
        [10:54:21] [Server thread/INFO] [net.minecraft.server.players.PlayerList]: Bellixix logged in with entity id 4
        [11:00:40] [Server thread/WARN] [net.minecraft.server.MinecraftServer]: Can't keep up! Is the server overloaded?
        """;

    /// <summary>0xC0000005, which is how Windows says a process touched memory that was not its own.</summary>
    private const int AccessViolation = -1073741819;

    [Fact]
    public void Crash_ExplainsANativeFaultInsteadOfCallingTheLogClean()
    {
        // Without the exit code this log is a clean one: nothing in it went wrong, because the
        // process died before it could write anything down. "No crash in this log" was the old
        // answer, followed by an invitation to read crash reports that were never written.
        var analysis = CrashAnalyzer.Analyze(NativeFaultLog, [], AccessViolation);

        Assert.NotEqual(CrashCause.Clean, analysis.Cause);
        Assert.True(analysis.HasVerdict);
    }

    [Fact]
    public void Crash_NamesTheGraphicsCardAndItsDriver()
    {
        var analysis = CrashAnalyzer.Analyze(NativeFaultLog, [], AccessViolation);

        Assert.Equal(CrashCause.Graphics, analysis.Cause);
        Assert.Contains("Intel(R) UHD Graphics", analysis.Headline);

        // The driver version is the one number that makes "update your driver" actionable.
        Assert.Contains("31.0.101.4255", analysis.Advice);
    }

    [Fact]
    public void Crash_SaysWhyThereIsNoCrashReport()
    {
        // The actual question somebody has when the launcher points them at a folder that is
        // empty.
        Assert.Contains("no crash report", CrashAnalyzer.Analyze(NativeFaultLog, [], AccessViolation).Advice);
    }

    [Fact]
    public void Crash_StillManagesSomethingWithNoAdapterInTheLog()
    {
        var analysis = CrashAnalyzer.Analyze("nothing useful here at all", [], AccessViolation);

        Assert.True(analysis.HasVerdict);
        Assert.Contains("no crash report", analysis.Advice);
    }

    [Fact]
    public void Crash_LeavesAnOrdinaryExitCodeToTheLog()
    {
        // An exit code that is not a native fault must not hijack the analysis — a mod crash
        // exits non-zero too, and the log is far better evidence than the number.
        var analysis = CrashAnalyzer.Analyze(WrongBuildCrash, [CornerEntity], exitCode: 1);

        Assert.Equal(CrashCause.WrongBuild, analysis.Cause);
    }

    [Theory]
    // A game version in front of the mod's own is not part of it.
    [InlineData("mc26.2-0.9.1-fabric", "0.9.0", true)]
    // Build metadata is not four more components.
    [InlineData("1.11.2+26.2", "1.11.2", true)]
    [InlineData("0.9", "0.10", false)]
    public void Bound_ComparesTheModsVersionAndNotTheGames(string version, string floor, bool accepted)
    {
        Assert.Equal(accepted, new VersionBound(floor, null, null).Accepts(version));
    }

    [Fact]
    public void Bound_TreatsAFamilyAsAClosedRange()
    {
        var bound = ModConflicts.Bound("0.9.x");

        Assert.True(bound.Accepts("0.9.4"));
        Assert.False(bound.Accepts("0.10.0"));
    }

    [Fact]
    public void Bound_ReadsAnExclusiveFloorAsStrictlyAbove()
    {
        var bound = new VersionBound(null, null, null) { Above = "0.6.0" };

        Assert.False(bound.Accepts("0.6.0"));
        Assert.True(bound.Accepts("0.6.1"));
    }
}
