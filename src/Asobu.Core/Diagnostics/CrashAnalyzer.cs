using System.Text.RegularExpressions;
using Asobu.Core.Launch;
using Asobu.Core.Mods;

namespace Asobu.Core.Diagnostics;

public enum CrashCause
{
    /// <summary>Nothing recognisable. The report is shown as-is and nothing is accused.</summary>
    Unknown,
    Mod,
    MissingDependency,

    /// <summary>Two mods that will not sit together, which the loader refuses to start with.</summary>
    IncompatibleMods,

    /// <summary>
    /// Two builds of one library on the classpath, which the loader checks for by hand and
    /// refuses to start with. Asobu's fault rather than anybody's mods: it is the launcher that
    /// decides what goes on the classpath.
    /// </summary>
    DuplicateLibrary,

    /// <summary>
    /// A mod reaching for something that is not there — a method or class that existed in the
    /// version it was built against and does not exist here. Not a broken mod: the wrong build
    /// of a working one, which is why the fix is to get the right build rather than to remove it.
    /// </summary>
    WrongBuild,
    OutOfMemory,
    Graphics,

    /// <summary>
    /// The operating system stopped the process. No crash report exists for these — the JVM never
    /// got to write one — so the exit code is the whole of the evidence.
    /// </summary>
    NativeFault,

    /// <summary>
    /// The JVM died of a fatal error rather than of an exception: an access violation, a signal,
    /// one of its own assertions. Unlike a kill from outside there is evidence — the runtime
    /// prints a block saying what was executing, and saves an hs_err file with the rest.
    /// </summary>
    JvmFatal,
    Java,
    CorruptFiles,
    /// <summary>The game shut down normally. Not every log in the list is a crash.</summary>
    Clean,
}

/// <summary>
/// One mod the report points at, with the lines that implicated it. The evidence is carried
/// through to the screen deliberately: a launcher that says "it's this one" without showing
/// its working is asking to be trusted about something it only guessed at.
/// </summary>
/// <param name="NamedDirectly">
/// The loader named this mod outright, rather than it merely turning up in a stack trace. Carried
/// as its own flag rather than read back off the score: enough stack frames add up past the score
/// a direct accusation carries, and claiming the crash named a mod when it didn't is a lie about
/// how sure we are.
/// </param>
public sealed record CrashSuspect(
    string Name,
    string FileName,
    int Score,
    bool NamedDirectly,
    IReadOnlyList<string> Evidence)
{
    public string ConfidenceLabel => NamedDirectly ? "Named in the crash" : "Appears in the stack trace";
}

public sealed record CrashAnalysis(
    CrashCause Cause,
    string Headline,
    string Advice,
    IReadOnlyList<CrashSuspect> Suspects)
{
    public bool HasSuspects => Suspects.Count > 0;
    public bool HasVerdict => Cause != CrashCause.Unknown || Suspects.Count > 0;

    /// <summary>
    /// The error file this crash named, when it named one.
    ///
    /// A launch log carries only the summary Java prints on its way out; everything else it knew
    /// went into the file that summary points at. Carried here so a caller can go and read it —
    /// which for one kind of crash is the difference between explaining it and fixing it.
    /// </summary>
    public string? ErrorFile { get; init; }

    /// <summary>
    /// The method Java was compiling when it died, when what was read was the error file itself.
    /// In Java's own "package.Class::method" form, which is also the form the flag that excludes
    /// it takes — see <see cref="CompilerExclusion"/>.
    /// </summary>
    public string? CompilingMethod { get; init; }

    public static readonly CrashAnalysis None =
        new(CrashCause.Unknown, "Nothing obvious", "Asobu couldn't pin this one down. The full text is below.", []);
}

/// <summary>
/// Reads a crash report and tries to name what broke.
///
/// This is a heuristic and says so on screen. The order below matters: the environmental causes
/// are checked first because a mod is always somewhere in a modded stack trace, and blaming one
/// for what is really an out-of-memory kill or a graphics driver fault sends people uninstalling
/// things at random. A mod is only accused when nothing else explains the crash.
/// </summary>
public static partial class CrashAnalyzer
{
    private const int DirectAccusationScore = 100;
    private const int FirstStackFrameScore = 40;

    /// <summary>
    /// A mixin config named somewhere in the log. Below a stack frame in the fatal trace on
    /// purpose: being mentioned is not being blamed.
    /// </summary>
    private const int MixinConfigScore = 20;

    private const int LaterStackFrameScore = 12;
    private const int ReportThreshold = 12;
    private const int MaxSuspects = 4;

    /// <summary>
    /// Tokens that appear in half of all mod file names and would otherwise match everything.
    /// Anything here is never used on its own to accuse a mod.
    /// </summary>
    private static readonly HashSet<string> Noise = new(StringComparer.OrdinalIgnoreCase)
    {
        "fabric", "forge", "neoforge", "quilt", "fabricapi", "api", "lib", "libs", "library",
        "core", "common", "util", "utils", "mod", "mods", "minecraft", "client", "server",
        "loader", "mixin", "mixins", "java", "kotlin", "scala", "release", "beta", "alpha",
    };

    /// <param name="exitCode">
    /// What the process died with. Worth having because the most violent crashes leave the least
    /// evidence: a native fault kills the JVM outright, so no crash report is written, the log
    /// simply stops, and the number is the only thing that survives.
    /// </param>
    public static CrashAnalysis Analyze(string? report, IReadOnlyList<ModEntry> installedMods, int exitCode = 0)
    {
        // Before the log is even looked at. A process killed by the operating system left a log
        // that ends mid-sentence and looks perfectly healthy, so every check below would call it
        // clean and send somebody to read crash reports that were never written.
        if (NativeFault(exitCode) is { } fault) return Killed(fault, report);

        if (string.IsNullOrWhiteSpace(report)) return CrashAnalysis.None;

        // A launch log for a session that quit normally sits in the same list as real crashes,
        // and mods log handled stack traces all the time during a session that ends fine. Without
        // this gate every one of those would get a mod accused over a crash that never happened.
        if (!LooksLikeCrash(report)) return new CrashAnalysis(
            CrashCause.Clean,
            "No crash in this log",
            "The game ran to the end of this log without dying. Any errors in it were handled.", []);

        // Before every check that reads the log's ordinary errors. A fatal JVM error stops the
        // process mid-instruction, and the handled exceptions above it belong to a session that
        // was running fine minutes earlier — a mod's failed connection, a mixin's startup warning.
        // The block the runtime prints on its way out is the only part of the log written by the
        // crash, so it answers first or a mod gets blamed for being the noisiest thing in a file
        // it had nothing to do with.
        if (JvmFatal(report, installedMods) is { } fatal) return fatal;

        if (Environmental(report) is { } environmental) return environmental;

        // Before everything that looks at mods. When the loader's own classes exist twice, every
        // mod that touches them fails at once — and each of those failures names a mod, so a hunt
        // through them finds whichever unlucky one is mentioned first and sends somebody to
        // uninstall a mod that was never at fault.
        if (LoaderSplit(report) is { } split) return split;

        if (MissingDependency(report) is { } missing) return missing;

        // Before the suspect hunt: the loader has already said exactly which two mods disagree,
        // and guessing from stack frames after that would be picking through a bin next to a
        // signed confession. Fabric's stack here is all loader frames anyway, so the hunt finds
        // nothing and the whole crash comes back as "nothing obvious".
        if (Incompatible(report) is { } incompatible) return incompatible;

        // Also before the suspect hunt. The loader names the mod outright here too, and the fix
        // is a different one — the mod is fine, it is the wrong build of it.
        if (WrongBuild(report, installedMods) is { } wrong) return wrong;

        // Ahead of the hunt for the same reason, and after WrongBuild because a mod that called a
        // method the game no longer has has said more about itself than one that merely would not
        // cast — where both are true, the missing method is the more useful thing to be told.
        if (AdapterMismatch(report, installedMods) is { } mismatch) return mismatch;

        var suspects = FindSuspects(report, installedMods);
        if (suspects.Count == 0)
        {
            return installedMods.Count == 0
                ? new CrashAnalysis(CrashCause.Unknown, "Vanilla crash",
                    "No mods are installed, so this is the game or its libraries. The full text is below.", [])
                : CrashAnalysis.None;
        }

        var top = suspects[0];
        return new CrashAnalysis(
            CrashCause.Mod,
            top.NamedDirectly
                ? $"{top.Name} looks responsible"
                : $"{top.Name} is the most likely cause",
            "Turn it off and launch again. If the crash goes away you've found it; if not, turn it back on and try the next one.",
            suspects);
    }

    /// <summary>
    /// What the operating system killed the process for, or null for an ordinary exit.
    ///
    /// Only the codes worth telling somebody apart. Everything else is left to the log, which
    /// for a normal crash has far more to say than a number does.
    /// </summary>
    private static string? NativeFault(int exitCode) => (uint)exitCode switch
    {
        0xC0000005 => "tried to read memory that wasn't its own",
        0xC00000FD => "ran out of stack",
        0xC000001D => "was handed an instruction its processor doesn't have",
        0xC0000409 => "overran a buffer and was stopped",
        0xC0000374 => "corrupted its own memory",
        _ => null,
    };

    /// <summary>
    /// The verdict for a process the operating system stopped.
    ///
    /// Nearly always the graphics driver, and the log says which one even though it says nothing
    /// about the crash — the game prints its adapter at startup. Naming it turns "exited with
    /// code -1073741819" into something somebody can act on.
    /// </summary>
    private static CrashAnalysis Killed(string fault, string? report)
    {
        if (report is not null && GraphicsDevice(report) is { } device)
        {
            return new CrashAnalysis(CrashCause.Graphics, $"{device.Name} stopped the game",
                $"The game {fault} and Windows ended it. That is a graphics driver fault rather than anything "
                + "in the game, which is why no crash report was written."
                + (device.Driver is { Length: > 0 } driver ? $" Yours is {driver}. " : " ")
                + "Update it from the manufacturer's own site rather than Windows Update, which is often "
                + "years behind. Turning off shaders and any performance mods is worth trying meanwhile.",
                []);
        }

        // No adapter in the log, so the advice cannot name one. Still worth saying why there is
        // no crash report to read, which is the question somebody actually has.
        return new CrashAnalysis(CrashCause.NativeFault, "The game was stopped by Windows",
            $"It {fault}, which ends the game outright — no crash report is written, which is why "
            + "there isn't one. Almost always a graphics driver. Updating yours is the first thing to try.",
            []);
    }

    // ---- The JVM's own fatal error ----

    /// <summary>
    /// What the runtime wrote on its way out.
    /// </summary>
    /// <param name="Signal">How it died: an access violation, a signal, one of its own assertions.</param>
    /// <param name="Kind">V for the JVM's own code, C for a native library, J or j for Java's.</param>
    /// <param name="Frame">The frame it died in, kept whole because it is the evidence for all of this.</param>
    /// <param name="Library">The binary that frame is in, when the frame names one.</param>
    /// <param name="InCompiler">
    /// The thread that died was a JIT compiler thread. Known rather than guessed: the runtime
    /// writes compiler replay data only when the crashing thread was compiling, so the presence
    /// of that line is a statement about which thread died.
    /// </param>
    /// <param name="ErrorFile">The hs_err file saved beside the game, which holds everything this block leaves out.</param>
    /// <param name="Java">The runtime's own version, worth quoting when the runtime is the accused.</param>
    private sealed record FatalError(
        string Signal,
        char Kind,
        string Frame,
        string? Library,
        bool InCompiler,
        string? ErrorFile,
        string? Java)
    {
        /// <summary>What the signal means, said the way somebody would say it out loud.</summary>
        public string InPlainWords => Signal switch
        {
            "EXCEPTION_ACCESS_VIOLATION" or "SIGSEGV" or "SIGBUS" => "read memory that wasn't its own",
            "EXCEPTION_STACK_OVERFLOW" or "SIGSTKFLT" => "ran out of stack",
            "EXCEPTION_ILLEGAL_INSTRUCTION" or "SIGILL" => "ran an instruction this processor doesn't have",
            "EXCEPTION_INT_DIVIDE_BY_ZERO" or "SIGFPE" => "divided by zero",
            "Internal Error" => "failed one of its own internal checks",
            _ => "hit a fault it could not recover from",
        };

        /// <summary>The hs_err file's own name, which is the part somebody has to go and find.</summary>
        public string? ErrorFileName =>
            ErrorFile is { Length: > 0 } file ? Path.GetFileName(file.Trim()) : null;

        /// <summary>
        /// Where the rest of it is. Said once, at the end of whatever verdict is reached — the
        /// printed block is a summary, and the file it names has the thread that died, every
        /// library that was loaded, and the state of the machine.
        /// </summary>
        public string Detail => ErrorFileName is { } name
            ? $" Java saved the full detail as {name} beside the instance's game files."
            : "";
    }

    /// <summary>
    /// Reads the fatal block, or null when the log has none.
    ///
    /// Every field is optional on purpose. The block is written by a runtime that has already
    /// lost its footing, and a truncated one still says more than the rest of the log does.
    /// </summary>
    private static FatalError? ReadFatalError(string report)
    {
        if (!FatalBannerPattern().IsMatch(report)) return null;

        var frame = FatalFramePattern().Match(report);
        var body = frame.Success ? frame.Groups["frame"].Value.Trim() : "";

        return new FatalError(
            FatalSignalPattern().Match(report) is { Success: true } signal ? signal.Groups["signal"].Value : "",
            frame.Success ? frame.Groups["kind"].Value[0] : '?',
            body,
            FrameLibraryPattern().Match(body) is { Success: true } library ? library.Groups["lib"].Value : null,
            // Either account of the same fact. The log gets the replay line and the file gets the
            // compile task, and each is written only for a thread that was compiling — so a
            // verdict does not depend on which of the two somebody happens to be looking at.
            CompilerReplayPattern().IsMatch(report) || CompileTaskPattern().IsMatch(report),
            ErrorFilePattern().Match(report) is { Success: true } file ? file.Groups["path"].Value : null,
            JreBuildPattern().Match(report) is { Success: true } jre ? jre.Groups["version"].Value : null);
    }

    /// <summary>
    /// The verdict for a runtime that died of a fatal error.
    ///
    /// The frame decides it, because the frame is the one thing in the log that was executing
    /// when the process stopped. A driver's own file means the driver; the embedded browser some
    /// mods carry means that mod; jvm.dll means Java itself and no mod at all — which is worth
    /// saying outright, since the answer otherwise offered is a list of mods to turn off one at a
    /// time, and none of them would ever fix it.
    /// </summary>
    private static CrashAnalysis? JvmFatal(string report, IReadOnlyList<ModEntry> installedMods)
    {
        // Its own case, and first, because it is written instead of the usual block rather than
        // alongside it: no signal, no frame, nothing was executing. Java asked the machine for
        // memory and the machine said no.
        if (NativeMemoryPattern().Match(report) is { Success: true } starved)
            return new CrashAnalysis(CrashCause.OutOfMemory, "The computer ran out of memory",
                "Java asked Windows for memory and was refused, which stops it outright. This is the machine's "
                + "memory rather than the game's own limit, so giving the instance more would bring it on sooner "
                + "rather than later — lower it instead, and close whatever else is running. "
                + "32-bit Java does this at around 1.5 GB however much the machine has, so it is worth checking "
                + "the instance is on a 64-bit runtime."
                + (starved.Groups["what"].Success ? $" Java's own words: {starved.Groups["what"].Value.Trim()}" : ""),
                []);

        if (ReadFatalError(report) is not { } fatal) return null;

        var library = fatal.Library;

        // The driver's own file. As direct as this gets: the game handed it work and it fell over
        // holding it, which is neither the game's doing nor any mod's.
        if (library is not null && GraphicsLibraryPattern().IsMatch(library))
            return new CrashAnalysis(CrashCause.Graphics,
                GraphicsDevice(report) is { } card ? $"{card.Name}'s driver crashed" : "The graphics driver crashed",
                $"The game {fatal.InPlainWords} inside {library}, which belongs to the graphics driver rather than "
                + "to the game or to any mod. Update it from the manufacturer's own site rather than Windows "
                + "Update, which is often years behind. Turning off shaders and any performance mods is worth "
                + "trying meanwhile." + fatal.Detail, []);

        // A whole copy of Chromium, running inside the game's process. When it falls over it takes
        // the game with it, and no setting in the game changes that.
        if (library is not null && EmbeddedBrowserPattern().IsMatch(library))
        {
            if (OwnerOfLibrary(["mcef", "webdisplays", "cefbrowser", "jcef"], installedMods) is { } mod)
                return new CrashAnalysis(CrashCause.Mod, $"{mod.Name}'s embedded browser crashed the game",
                    $"The game {fatal.InPlainWords} inside {library} — the copy of Chromium {mod.Name} runs inside "
                    + "the game to draw web pages on screens. A browser that crashes takes the game down with it, "
                    + $"so there is nothing in the game's settings to change: turning {mod.Name} off is the fix."
                    + fatal.Detail,
                    [new CrashSuspect(mod.Name, mod.FileName, DirectAccusationScore, true, [fatal.Frame])]);

            return new CrashAnalysis(CrashCause.JvmFatal, "An embedded browser crashed the game",
                $"The game {fatal.InPlainWords} inside {library}, which is a copy of Chromium running inside the "
                + "game — MCEF and the mods built on it, such as WebDisplays, are what put it there. Turning those "
                + "off is the fix; nothing in the game's own settings touches it." + fatal.Detail, []);
        }

        if (library is not null && JvmLibraryPattern().IsMatch(library))
        {
            // The compiler, not the game. Java compiles the code it is running as it runs it, on
            // its own threads, and this crash was on one of those — so nothing a mod did was
            // executing at the time and turning mods off is a week spent proving that.
            if (fatal.InCompiler)
                return WithFix(fatal, report, new CrashAnalysis(CrashCause.JvmFatal, "Java itself crashed while compiling code",
                    "Java compiles the game as it runs, on threads of its own, and this crash was on one of those. "
                    + "Nothing the game or a mod was doing was executing at the time, so turning mods off will not "
                    + "change it and there is nothing in the instance to fix. The runtime is the thing to change, "
                    + "and Asobu's Automatic setting installs the exact Java the game asks for"
                    + (fatal.Java is { } version ? $" — {version} here" : "")
                    + ", so changing it means pointing this instance at another install of the same Java version in "
                    + "its settings. Worth doing only if it happens again: a compiler fault that repeats in the "
                    + "same place is a bug in that build of Java, and one that lands somewhere different every "
                    + "time is usually the machine's memory failing."
                    + fatal.Detail, []));

            return new CrashAnalysis(CrashCause.JvmFatal, "Java itself crashed",
                $"The crash was inside {library} — Java's own engine, rather than the game or any mod running on "
                + "it. If this instance has been pointed at a Java of your own, put it back on Automatic and Asobu "
                + "will install the one the game asks for; if it is already on Automatic, another install of that "
                + "same version is the thing to try"
                + (fatal.Java is { } built ? $" — it crashed on {built}." : ".")
                + fatal.Detail, []);
        }

        // A Java frame: compiled or interpreted, but somebody's actual code. The class names in it
        // say whose, and a frame that was executing at the instant the process died is a long way
        // better evidence than a mod merely turning up in the log.
        if (fatal.Kind is 'J' or 'j'
            && OwnerOf(fatal.Frame, BuildTokens(installedMods)) is { } file
            && installedMods.FirstOrDefault(m => m.FileName.Equals(file, StringComparison.OrdinalIgnoreCase)) is { } named)
        {
            return new CrashAnalysis(CrashCause.Mod, $"{named.Name} was running when Java died",
                $"Java {fatal.InPlainWords} while running {named.Name}'s code, which ends the game outright rather "
                + "than raising an error it could report. Turn it off and launch again." + fatal.Detail,
                [new CrashSuspect(named.Name, named.FileName, DirectAccusationScore, true, [fatal.Frame])]);
        }

        // Nothing recognisable in the frame, and the log says the heap ran out. A process that
        // spent its last minutes short of memory and then died somewhere unremarkable is more
        // usefully answered as the memory problem it was — so the ordinary checks get it back.
        // Only from here: a frame that named a driver, a browser or a mod named it whatever else
        // the log says, and handing those to the memory check would be losing real evidence.
        if (OutOfMemoryPattern().IsMatch(report)) return null;

        return new CrashAnalysis(CrashCause.JvmFatal, "Java stopped the game",
            $"Java {fatal.InPlainWords} and ended the game where it stood. That is why there is no crash report to "
            + "read: a fault this low stops the runtime before it can write one."
            + (library is { Length: > 0 } unknown ? $" It was inside {unknown} at the time." : "")
            + fatal.Detail, []);
    }

    /// <summary>
    /// Hangs the two things a caller can act on off a verdict: the file to go and read, and — if
    /// what was read was that file already — the method to keep Java's hands off.
    /// </summary>
    private static CrashAnalysis WithFix(FatalError fatal, string report, CrashAnalysis analysis) =>
        analysis with
        {
            ErrorFile = fatal.ErrorFile?.Trim(),
            CompilingMethod = CompilingMethodIn(report),
        };

    /// <summary>
    /// The method Java was compiling when it died, from the error file's own account of it:
    ///
    ///     Current CompileTask:
    ///     C2:  67525 12345       4       net.minecraft.client.Minecraft::runTick (1234 bytes)
    ///
    /// Only ever in the file. The summary printed to the log says a compiler thread died and
    /// where the fault was, but not what it was working on, so this returns nothing for a launch
    /// log and the file has to be read for it.
    ///
    /// Returned exactly as Java prints it, which is not a coincidence worth undoing: the dotted
    /// class and the two colons are the one spelling the flag that excludes it will accept.
    /// </summary>
    public static string? CompilingMethodIn(string errorFile)
    {
        if (CompileTaskPattern().Match(errorFile) is not { Success: true } task) return null;

        var method = task.Groups["method"].Value;

        return CompilerExclusion.IsMethodName(method) ? method : null;
    }

    /// <summary>
    /// Which installed mod owns a native library, by the ids that ship it.
    ///
    /// Native file names carry nothing to match a jar against — libcef.dll is libcef.dll whoever
    /// bundled it — so the mapping is stated here and the mod is looked up by the ids that are
    /// known to carry it.
    /// </summary>
    private static ModEntry? OwnerOfLibrary(string[] ids, IReadOnlyList<ModEntry> installedMods)
    {
        if (installedMods.Count == 0) return null;

        var tokens = BuildTokens(installedMods);

        foreach (var id in ids)
        {
            if (Resolve(id, tokens) is not { } file) continue;

            if (installedMods.FirstOrDefault(m => m.FileName.Equals(file, StringComparison.OrdinalIgnoreCase)) is { } mod)
                return mod;
        }

        return null;
    }

    /// <summary>The adapter the game reported at startup, which is in every log whether it crashed or not.</summary>
    private static (string Name, string? Driver)? GraphicsDevice(string report)
    {
        if (GraphicsDevicePattern().Match(report) is not { Success: true } device) return null;

        var driver = GraphicsDriverPattern().Match(report);

        var full = device.Groups["name"].Value.Trim();
        var name = TrailingVendorPattern().Replace(full, "");

        return (name.Length > 0 ? name : full, driver.Success ? driver.Groups["v"].Value : null);
    }

    /// <summary>
    /// "Using graphics device: Intel(R) UHD Graphics (Intel)" — vanilla writes this every launch.
    ///
    /// Read to the end of the line rather than to the first bracket. The first bracket in that
    /// example sits inside "Intel(R)", so stopping there names the card "Intel"; the trailing
    /// vendor in brackets is taken off afterwards instead.
    /// </summary>
    [GeneratedRegex(@"Using graphics device: (?<name>[^\r\n]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GraphicsDevicePattern();

    /// <summary>The " (Intel)" a card's name ends with, which only repeats what the name says.</summary>
    [GeneratedRegex(@"\s*\([^()]*\)\s*$")]
    private static partial Regex TrailingVendorPattern();

    /// <summary>Sodium's probe, which is the only line carrying the driver's own version.</summary>
    [GeneratedRegex(@"openglIcdVersion=(?<v>[\d.]+)", RegexOptions.IgnoreCase)]
    private static partial Regex GraphicsDriverPattern();

    /// <summary>
    /// A mod calling something that is not there.
    ///
    /// The loader names the mod whose entrypoint blew up, and the exception underneath says what
    /// went missing — a method, a field, a whole class. Together those mean one thing: the mod
    /// was compiled against a different version of the game, or of a library, than the one it is
    /// running with. Its own file name usually admits it, carrying the Minecraft version it was
    /// built for while the instance runs another.
    ///
    /// Worth telling apart from an ordinary mod crash because the fix is different. The mod is
    /// not broken and does not want removing; the right build of it wants installing.
    /// </summary>
    /// <summary>
    /// The same class existing twice, once in Fabric's classloader and once in the JVM's.
    ///
    /// Java says this in a mouthful — "X is in unnamed module of loader 'knot', Y is in unnamed
    /// module of loader 'app'" — and what it means is that two copies of one interface are loaded,
    /// so nothing on one side can be cast to the other. Every mod holding a mixin plugin or an
    /// entrypoint fails at the same instant, each failure naming its own mod.
    ///
    /// Which is exactly why this is checked first and answered as one finding. Read mod by mod it
    /// looks like twenty broken mods; it is one broken classpath, and the classpath is the
    /// launcher's to build. Telling somebody to uninstall Sodium here would be wrong twice over:
    /// it would not work, and it would blame them for something Asobu did.
    /// </summary>
    private static CrashAnalysis? LoaderSplit(string report)
    {
        var casts = LoaderSplitPattern().Matches(report);
        if (casts.Count == 0) return null;

        var loaders = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match cast in casts) loaders.Add(cast.Groups["loader"].Value);

        // One loader named over and over is ordinary. Two is the split.
        if (loaders.Count < 2) return null;

        var howMany = ClassCastPattern().Matches(report).Count;
        var scale = howMany > 1 ? $"{howMany} of them failed this way, " : "";

        return new CrashAnalysis(
            CrashCause.DuplicateLibrary,
            "Two copies of the loader's own classes were loaded",
            $"{scale}which means one library was loaded twice and nothing can be cast between the copies. "
            + "None of your mods caused it and turning them off will not help. The usual reason is that the "
            + "launcher's files are reachable by two different paths — running Asobu from a sandboxed, "
            + "portable or redirected folder does this, because the loader decides what to share by comparing "
            + "paths and two spellings of one folder do not match. Running the installed copy normally is the "
            + "first thing to try; after that, reinstalling the instance's loader.",
            []);
    }

    /// <summary>
    /// Fabric refusing to load a mod's entrypoint because its class will not cast.
    ///
    /// Its own verdict rather than part of the wrong-build one, because the evidence is different
    /// and so is the certainty. There is no missing method to quote here — the loader is saying
    /// outright that this build of this mod does not fit this loader, before a single line of the
    /// mod has run.
    /// </summary>
    private static CrashAnalysis? AdapterMismatch(string report, IReadOnlyList<ModEntry> installedMods)
    {
        if (AdapterMismatchPattern().Match(report) is not { Success: true } mismatch) return null;

        var tokens = BuildTokens(installedMods);

        // Whose class it is, from the class itself. The entrypoint line usually names the same
        // mod, but the class is the more direct statement of the two.
        var file = OwnerOf(mismatch.Groups["class"].Value, tokens)
                   ?? (EntrypointFailurePattern().Match(report) is { Success: true } entry
                       ? Resolve(entry.Groups["id"].Value, tokens)
                       : null);

        if (file is null) return null;

        var mod = installedMods.First(m => m.FileName.Equals(file, StringComparison.OrdinalIgnoreCase));

        return new CrashAnalysis(
            CrashCause.WrongBuild,
            $"{mod.Name} does not fit this version of Fabric",
            "Fabric could not load its entrypoint: the class it declared would not cast to the interface the "
            + "loader expects. That means this build of the mod was made for a different Fabric or Minecraft "
            + "version, not that the mod is broken. Getting the build made for this instance is the fix; "
            + "turning it off gets you playing in the meantime.",
            [new CrashSuspect(mod.Name, mod.FileName, DirectAccusationScore, NamedDirectly: true,
                [Shorten(mismatch.Value)])]);
    }

    private static CrashAnalysis? WrongBuild(string report, IReadOnlyList<ModEntry> installedMods)
    {
        if (EntrypointFailurePattern().Match(report) is not { Success: true } entrypoint) return null;
        if (LinkagePattern().Match(report) is not { Success: true } linkage) return null;

        var tokens = BuildTokens(installedMods);

        var id = entrypoint.Groups["id"].Value;
        var file = Resolve(id, tokens);

        // Named a mod this instance does not have. Nothing to act on, so leave it to the suspect
        // hunt rather than claiming to know.
        if (file is null) return null;

        // These two were found independently — the first entrypoint failure and the first linkage
        // error anywhere in the log — and pairing them on nothing but that produced a sentence
        // where the mod and the evidence came from different mods entirely: Essential named, and
        // FerriteCore's missing class quoted as the thing Essential had called. A log carries
        // plenty of linkage errors that never killed anything, so the pair has to be shown to
        // belong together before it is presented as one finding.
        var owner = OwnerOf(linkage.Value, tokens);
        if (owner is not null && !owner.Equals(file, StringComparison.OrdinalIgnoreCase)) return null;

        var mod = installedMods.First(m => m.FileName.Equals(file, StringComparison.OrdinalIgnoreCase));

        var missing = Shorten(linkage.Value);

        return new CrashAnalysis(
            CrashCause.WrongBuild,
            $"{mod.Name} was built for a different version",
            $"It called something the game no longer has, so the loader stopped: {missing}. That happens when a mod "
            + "is built for one Minecraft version and run on another. Asobu can fetch the build made for this "
            + "instance, and turn the mod off if its author has not published one.",
            [new CrashSuspect(mod.Name, mod.FileName, DirectAccusationScore, NamedDirectly: true,
                [Shorten(entrypoint.Value), missing])]);
    }

    /// <summary>
    /// Two mods that refuse to load together. The loader states this plainly and even suggests
    /// the fix, so the analysis quotes it rather than inventing wording of its own.
    /// </summary>
    private static CrashAnalysis? Incompatible(string report)
    {
        if (!IncompatiblePattern().IsMatch(report)) return null;

        // Fabric's own recommendation, when it worked one out: "Replace mod 'X' (x) 1.2 with any
        // 0.9.x version that is compatible with: - y 1.0". That names the mod to change.
        if (ReplaceSuggestionPattern().Match(report) is { Success: true } fix)
        {
            var name = fix.Groups["name"].Value;
            var wanted = fix.Groups["wanted"].Value;

            return new CrashAnalysis(
                CrashCause.IncompatibleMods,
                $"{name} clashes with another mod",
                $"The loader refused to start: {name} {fix.Groups["present"].Value} does not sit with what else is "
                + $"installed. It suggests any {wanted} build instead. Asobu can swap it for you.",
                []);
        }

        // No suggestion, but the pair is still named.
        if (BreakagePattern().Match(report) is { Success: true } breakage)
            return new CrashAnalysis(
                CrashCause.IncompatibleMods,
                $"{breakage.Groups["by"].Value} clashes with {breakage.Groups["name"].Value}",
                $"The loader refused to start: {breakage.Groups["by"].Value} will not run alongside the installed "
                + $"{breakage.Groups["name"].Value} {breakage.Groups["present"].Value}. One of the two has to change.",
                []);

        return new CrashAnalysis(
            CrashCause.IncompatibleMods,
            "Two mods will not run together",
            "The loader stopped before the game started because some of the installed mods are incompatible. "
            + "The log below names them.", []);
    }

    /// <summary>
    /// Causes that have nothing to do with any particular mod. Each one has a signature specific
    /// enough that a false positive is unlikely, which is why they get to answer first.
    /// </summary>
    /// <summary>Where the heap stood when the report was written.</summary>
    private sealed record HeapUse(long UsedMib, long MaxMib)
    {
        /// <summary>
        /// Within a tenth of the ceiling. Not a definition of running out — a healthy game sits
        /// well under this, and one that is over it was going to run out shortly whatever else
        /// happened to kill it first.
        /// </summary>
        public bool IsAtCeiling => MaxMib > 0 && UsedMib >= MaxMib * 0.9;
    }

    /// <summary>
    /// Reads the report's own memory line: free, then allocated, then the ceiling. What was in
    /// use is the allocated figure less the free one — the middle number on its own is only what
    /// the JVM had claimed from the system, which is not the same thing.
    /// </summary>
    private static HeapUse? HeapAtCrash(string report)
    {
        if (HeapPattern().Match(report) is not { Success: true } match) return null;

        if (!long.TryParse(match.Groups["free"].Value, out var free)
            || !long.TryParse(match.Groups["total"].Value, out var total)
            || !long.TryParse(match.Groups["max"].Value, out var max))
        {
            return null;
        }

        return new HeapUse(Math.Max(0, total - free), max);
    }

    private static CrashAnalysis? Environmental(string report)
    {
        // Checked before everything else. The stack trace is Fabric's, so a reader — and the mod
        // heuristics below — would otherwise start looking for a mod to blame for something no
        // mod did.
        if (DuplicateLibraryPattern().Match(report) is { Success: true } duplicate)
        {
            var library = duplicate.Groups["what"].Value.Trim();

            return new CrashAnalysis(CrashCause.DuplicateLibrary,
                library.Length > 0 ? $"Two copies of {library} were on the classpath" : "A library was on the classpath twice",
                "None of your mods did this — the launcher decides what goes on the classpath, and an older Asobu " +
                "could put two builds of one library there when a loader and the game wanted different ones. " +
                "Updating Asobu and launching again is the whole fix; nothing in the instance needs changing.", []);
        }

        var memory = HeapAtCrash(report);

        if (OutOfMemoryPattern().IsMatch(report))
            return new CrashAnalysis(CrashCause.OutOfMemory, "Ran out of memory",
                "The game asked for more memory than it was allowed"
                + (memory is { } said ? $" — it was using {said.UsedMib} MB of the {said.MaxMib} MB it is given" : "")
                + ". Raise this instance's memory in its settings, "
                + "or turn the automatic limit back on and let Asobu size it from the pack.", []);

        // A heap pressed right up against its ceiling, with nothing in the log saying so. The JVM
        // does not always get to write an OutOfMemoryError: it can spend its last minutes in the
        // collector and be killed, or die somewhere unrelated that had the bad luck to need a few
        // bytes. The report writes down where the heap stood, so it is worth reading rather than
        // waiting for a message that may never come.
        if (memory is { IsAtCeiling: true } tight)
            return new CrashAnalysis(CrashCause.OutOfMemory, "Out of memory",
                $"The game was using {tight.UsedMib} MB of the {tight.MaxMib} MB it is allowed when it died, which "
                + "is as much as it can have. Nothing in the log says so outright, so this is read from the memory "
                + "line in the report. Raise this instance's memory in its settings, or turn the automatic limit "
                + "back on and let Asobu size it from the pack.", []);

        if (GraphicsPattern().IsMatch(report))
            return new CrashAnalysis(CrashCause.Graphics, "Graphics driver fault",
                "The crash came from the graphics driver rather than the game. Update your GPU drivers, and if this " +
                "is a laptop check that Asobu is set to the dedicated GPU in Settings.", []);

        if (JavaVersionPattern().IsMatch(report))
            return new CrashAnalysis(CrashCause.Java, "Wrong Java version",
                "This build was compiled for a newer Java than the one running it. Set the instance's Java runtime " +
                "back to Automatic and Asobu will fetch the right one.", []);

        if (CorruptPattern().IsMatch(report))
            return new CrashAnalysis(CrashCause.CorruptFiles, "A downloaded file is damaged",
                "A jar or asset failed to read. Deleting the instance's cached version and launching again re-downloads it.", []);

        return null;
    }

    private static CrashAnalysis? MissingDependency(string report)
    {
        var match = MissingDependencyPattern().Match(report);
        if (!match.Success) return null;

        var name = match.Groups["dep"].Value.Trim().Trim('\'', '"');
        if (name.Length == 0) return null;

        return new CrashAnalysis(CrashCause.MissingDependency, $"Missing dependency: {name}",
            $"A mod needs {name} and it isn't installed, or the installed version is too old. " +
            "Install it and the crash should go with it.", []);
    }

    /// <summary>
    /// Which installed mod a line's class names belong to, or null when none of them do.
    ///
    /// Java packages carry the mod's own name in them — malte0811.ferritecore.mixin.config is
    /// FerriteCore's and gg.essential.loader.stage0 is Essential's — so a class name says who a
    /// line is about even when the line itself never mentions a mod. Vendor and layout segments
    /// are skipped, since "com" and "mixin" belong to everybody.
    /// </summary>
    private static string? OwnerOf(string line, Dictionary<string, string> tokens)
    {
        foreach (Match match in QualifiedClassPattern().Matches(line))
        {
            foreach (var segment in match.Value.Split('.'))
            {
                if (segment.Length < 3 || Noise.Contains(segment) || Vendors.Contains(segment)) continue;
                if (Resolve(segment.ToLowerInvariant(), tokens) is { } owner) return owner;
            }
        }

        return null;
    }

    /// <summary>Package roots that say nothing about which mod a class belongs to.</summary>
    private static readonly HashSet<string> Vendors = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "net", "org", "io", "gg", "me", "dev", "xyz", "eu", "co", "uk", "cc", "top",
        "github", "gitlab", "sun", "jdk", "sponge", "spongepowered", "asm", "impl", "internal",
        "config", "platform", "injection", "compat", "gui", "screen", "render", "renderer",
    };

    /// <summary>
    /// The jar a Forge stack frame says it came from, with the URL escaping undone — Essential
    /// arrives as "Essential%20(forge_1.19.2).jar" and is on disk with its space.
    /// </summary>
    private static string? StampedJar(string line) =>
        FrameJarPattern().Match(line) is { Success: true } match
            ? match.Groups["jar"].Value.Replace("%20", " ")
            : null;

    /// <summary>
    /// Just the "Suspected Mods" block, so the pattern that reads it cannot wander into the mod
    /// list at the bottom of the report, where every installed mod is written the same way.
    /// </summary>
    private static string SuspectedBlock(string report)
    {
        var start = report.IndexOf("Suspected Mods", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return "";

        var end = report.IndexOf("Stacktrace:", start, StringComparison.OrdinalIgnoreCase);
        return end < 0 ? report[start..] : report[start..end];
    }

    /// <summary>The whole line a match landed on, so its surroundings can be judged.</summary>
    private static string LineAround(string report, int index)
    {
        var start = report.LastIndexOf('\n', Math.Min(index, report.Length - 1)) + 1;
        var end = report.IndexOf('\n', index);
        if (end < 0) end = report.Length;

        return report[start..end];
    }

    private static List<CrashSuspect> FindSuspects(string report, IReadOnlyList<ModEntry> installedMods)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        // The jars as they are on disk. What Forge stamps on a frame is the file name itself, so
        // it is matched whole rather than put through the name guessing the rest of this uses.
        var byFile = new HashSet<string>(installedMods.Select(mod => mod.FileName), StringComparer.OrdinalIgnoreCase);
        var directlyNamed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var evidence = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var tokens = BuildTokens(installedMods);

        void Accuse(string fileName, int score, string line)
        {
            scores[fileName] = scores.GetValueOrDefault(fileName) + score;
            if (score >= DirectAccusationScore) directlyNamed.Add(fileName);

            var lines = evidence.TryGetValue(fileName, out var existing) ? existing : evidence[fileName] = [];
            var trimmed = Shorten(line);
            if (lines.Count < 3 && !lines.Contains(trimmed)) lines.Add(trimmed);
        }

        // 1. The loader naming a mod outright. Nothing beats this.
        foreach (Match match in DirectAccusationPattern().Matches(report))
        {
            var named = match.Groups["id"].Value;
            if (Resolve(named, tokens) is { } file) Accuse(file, DirectAccusationScore, match.Value);
        }

        // Forge's own verdict, which it works out from the trace that killed the game and lists
        // one per line. Reading only the first meant the second was thrown away, and the entries
        // are not ranked by us at all — whichever of them also owns the most frames comes top,
        // which is the same thing Forge means by putting it first.
        foreach (Match match in SuspectedModPattern().Matches(SuspectedBlock(report)))
        {
            if (Resolve(match.Groups["id"].Value, tokens) is { } file)
                Accuse(file, DirectAccusationScore, match.Value.Trim());
        }

        // 2. A mixin config file is named after its own mod by convention: sodium.mixins.json.
        //
        // Circumstantial, not an accusation, and it took a real crash to see why. A log mentions
        // these names constantly — Mixin prints one for a missing refmap and says in the same
        // breath that the message can be ignored, and a mod whose companion plugin failed to load
        // is named this way too even when it is a symptom rather than the cause. Treated as proof,
        // that put four healthy mods on screen as "named in the crash" while the mod that actually
        // stopped the game went unmentioned.
        //
        // So: worth something, worth less than a stack frame in the trace that killed the game,
        // and never enough on its own to claim the loader named anybody.
        // Once each, and never from a stack frame. Forge stamps the whole list of loaded mixin
        // configs onto the end of every single frame it prints, so counting those counted nothing
        // about the crash and everything about how long the trace was: in a real report that put
        // ImmediatelyFast top with 340 points, from seventeen mentions on frames belonging to
        // other people's classes, while the mod Forge itself named sat below the cut.
        var mixinCredited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in MixinConfigPattern().Matches(report))
        {
            var line = LineAround(report, match.Index);
            if (HarmlessPattern().IsMatch(line) || StackFramePattern().IsMatch(line)) continue;

            var named = match.Groups["id"].Value;
            if (Resolve(named, tokens) is { } file && mixinCredited.Add(file))
                Accuse(file, MixinConfigScore, line);
        }

        // 3. Stack frames. The first trace in a report is the one that killed the game; frames
        //    below it are usually the loader's own plumbing, so they count for much less.
        var firstTraceDone = false;
        var inTrace = false;

        foreach (var raw in report.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var frame = StackFramePattern().Match(line);

            if (!frame.Success)
            {
                if (inTrace) { inTrace = false; firstTraceDone = true; }
                continue;
            }

            inTrace = true;
            var weight = firstTraceDone ? LaterStackFrameScore : FirstStackFrameScore;

            // Forge writes the jar each frame came out of on the end of the frame itself, and
            // sometimes the mod id in front of the class. That is the loader stating the
            // attribution rather than us inferring it, so it is used first and it is exact.
            //
            // It has to be. A package name is not a mod name: WebDisplays ships as
            // net.montoyo.wd, so a crash whose every frame was its own matched nothing at all
            // by name and the mod that stopped the game was never even listed.
            if (StampedJar(line) is { } stamped && byFile.Contains(stamped))
            {
                Accuse(stamped, weight, line);
                continue;
            }

            if (TransformerPattern().Match(line) is { Success: true } transformer
                && Resolve(transformer.Groups["id"].Value, tokens) is { } fromId)
            {
                Accuse(fromId, weight, line);
                continue;
            }

            var qualified = frame.Groups["at"].Value;

            foreach (var (token, file) in tokens)
            {
                if (!qualified.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
                Accuse(file, weight, line);
            }
        }

        var byName = installedMods.ToDictionary(m => m.FileName, StringComparer.OrdinalIgnoreCase);

        return [.. scores
            .Where(pair => pair.Value >= ReportThreshold && byName.ContainsKey(pair.Key))
            .OrderByDescending(pair => pair.Value)
            .Take(MaxSuspects)
            .Select(pair => new CrashSuspect(
                byName[pair.Key].Name,
                pair.Key,
                pair.Value,
                directlyNamed.Contains(pair.Key),
                evidence.TryGetValue(pair.Key, out var lines) ? lines : []))];
    }

    /// <summary>
    /// Every distinctive string that would identify a mod in a stack trace, mapped back to the jar
    /// it came from. Declared ids win; file names are the fallback for Forge mods, whose TOML
    /// manifest the scanner doesn't read.
    /// </summary>
    private static Dictionary<string, string> BuildTokens(IReadOnlyList<ModEntry> installedMods)
    {
        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in installedMods)
        {
            foreach (var token in TokensFor(mod))
            {
                // First mod to claim a token keeps it. Two mods answering to one word means the
                // word isn't distinctive enough to accuse either of them with.
                if (!tokens.TryAdd(token, mod.FileName) && tokens[token] != mod.FileName)
                    tokens[token] = mod.FileName;
            }
        }

        return tokens;
    }

    private static IEnumerable<string> TokensFor(ModEntry mod)
    {
        if (Usable(mod.ModId)) yield return mod.ModId!;

        // "sodium-fabric-0.5.8+mc1.20.1.jar" -> "sodium". Strip the extension, then everything
        // from the first version-looking segment onwards, then the loader name.
        var stem = Path.GetFileNameWithoutExtension(mod.FileName);
        stem = VersionTailPattern().Replace(stem, "");

        var slug = stem.Replace('_', '-').Replace(' ', '-').Trim('-');
        if (Usable(slug)) yield return slug;

        var head = slug.Split('-', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (Usable(head)) yield return head!;

        var compact = mod.Name.Replace(" ", "").Replace("-", "");
        if (Usable(compact)) yield return compact;
    }

    /// <summary>
    /// Short or generic tokens match half the JDK and would accuse an innocent mod on every
    /// crash, so they are dropped rather than weighted down.
    /// </summary>
    private static bool Usable(string? token) =>
        token is { Length: >= 4 } && !Noise.Contains(token);

    private static string? Resolve(string named, Dictionary<string, string> tokens)
    {
        if (named.Length == 0) return null;
        if (tokens.TryGetValue(named, out var exact)) return exact;

        // A loader sometimes names "sodium-extra" where the jar only carries "sodiumextra".
        var normalised = named.Replace("-", "").Replace("_", "");
        var joined = tokens.FirstOrDefault(pair =>
            pair.Key.Replace("-", "").Replace("_", "").Equals(normalised, StringComparison.OrdinalIgnoreCase)).Value;

        if (joined is not null) return joined;

        // And sometimes the id carries a word the file name has no reason to: Essential ships as
        // "Essential_1-4-1-1_fabric_26-2.jar" and calls itself "essential-loader", so the whole id
        // matches nothing while its first half matches exactly. Tried last, and only on parts that
        // are not themselves noise — dropping every segment would make "fabric-api" match "api"
        // and put the wrong mod's name in front of somebody.
        var parts = named.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !Noise.Contains(part))
            .ToArray();

        if (parts.Length == 0 || parts.Length == named.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).Length)
            return null;

        var trimmed = string.Concat(parts);
        return tokens.FirstOrDefault(pair =>
            pair.Key.Replace("-", "").Replace("_", "").Equals(trimmed, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static string Shorten(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length <= 160 ? trimmed : trimmed[..157] + "…";
    }

    /// <summary>How much of the tail counts as "where the process died".</summary>
    private const int TailScan = 4000;

    /// <summary>
    /// Whether this file records a crash at all. An explicit marker settles it. Failing that, a
    /// log that stops partway through a stack trace is a log of a process that died there — which
    /// is why only the tail is searched, not the whole file.
    /// </summary>
    private static bool LooksLikeCrash(string report)
    {
        if (CrashMarkerPattern().IsMatch(report)) return true;

        var tail = report.Length <= TailScan ? report : report[^TailScan..];
        return StackFramePattern().IsMatch(tail);
    }

    // ---- Patterns ----

    [GeneratedRegex(@"---- Minecraft Crash Report ----|Exception in thread ""main""|" +
        @"A fatal error has been detected by the Java Runtime Environment|" +
        @"There is insufficient memory for the Java Runtime Environment to continue|" +
        @"net\.fabricmc\.loader\.impl\.FormattedException|org\.quiltmc\.loader\.impl\.FormattedException|" +
        @"Failed to start Minecraft|" +
        @"The game crashed whilst|Minecraft has crashed",
        RegexOptions.IgnoreCase)]
    private static partial Regex CrashMarkerPattern();

    [GeneratedRegex(@"Incompatible mods found!|Some of your mods are incompatible with the game or each other",
        RegexOptions.IgnoreCase)]
    private static partial Regex IncompatiblePattern();

    /// <summary>Fabric's suggested fix: which mod to replace, and with what.</summary>
    [GeneratedRegex(
        @"Replace mod '(?<name>[^']+)' \((?<id>[^)]+)\) (?<present>\S+) with any (?<wanted>\S+) version",
        RegexOptions.IgnoreCase)]
    private static partial Regex ReplaceSuggestionPattern();

    /// <summary>The breakage itself, when no fix was suggested.</summary>
    [GeneratedRegex(
        @"Mod '(?<by>[^']+)' \([^)]+\) \S+ is incompatible with [^\n]*?of mod '(?<name>[^']+)' \([^)]+\), "
        + @"yet a conflicting version is present: (?<present>[^!\n]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex BreakagePattern();

    [GeneratedRegex(@"java\.lang\.OutOfMemoryError|Java heap space|GC overhead limit exceeded|unable to create native thread",
        RegexOptions.IgnoreCase)]
    private static partial Regex OutOfMemoryPattern();

    /// <summary>"Memory: 361640112 bytes (344 MiB) / 1962934272 bytes (1872 MiB) up to 4294967296 bytes (4096 MiB)".</summary>
    [GeneratedRegex(@"Memory:\s*\d+ bytes \((?<free>\d+) MiB\) / \d+ bytes \((?<total>\d+) MiB\)"
        + @" up to \d+ bytes \((?<max>\d+) MiB\)", RegexOptions.IgnoreCase)]
    private static partial Regex HeapPattern();

    /// <summary>
    /// Fabric's own words when it finds one library twice:
    ///
    ///     duplicate ASM classes found on classpath: jar:file:/.../asm-9.10.1.jar!/...
    ///
    /// The word before "classes" is the library, which Fabric writes for ASM and for the handful
    /// of others it checks. Optional, because the sentence is worth recognising either way.
    /// </summary>
    [GeneratedRegex(@"duplicate (?<what>[\w.-]+ )?classes found on classpath", RegexOptions.IgnoreCase)]
    private static partial Regex DuplicateLibraryPattern();

    /// <summary>
    /// Fabric naming the mod whose entrypoint threw:
    ///
    ///     Could not execute entrypoint stage 'main' due to errors, provided by 'corner-entity'
    ///
    /// The id in there is the mod's own, which is what the installed jars are matched against.
    /// </summary>
    [GeneratedRegex(@"Could not execute entrypoint stage '[^']*' due to errors,? provided by '(?<id>[^']+)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex EntrypointFailurePattern();

    /// <summary>
    /// The JVM's way of saying a class was compiled against something that is not here any more.
    /// Every one of these means a version mismatch rather than a logic error — the code asked for
    /// a member that existed when it was built.
    /// </summary>
    [GeneratedRegex(
        @"java\.lang\.(?:NoSuchMethodError|NoSuchFieldError|NoClassDefFoundError|AbstractMethodError|"
        + @"IncompatibleClassChangeError|ClassNotFoundException)[^\n]*")]
    private static partial Regex LinkagePattern();

    /// <summary>The line the runtime opens its fatal block with, on every platform.</summary>
    [GeneratedRegex(@"A fatal error has been detected by the Java Runtime Environment", RegexOptions.IgnoreCase)]
    private static partial Regex FatalBannerPattern();

    /// <summary>
    /// How it died: "#  EXCEPTION_ACCESS_VIOLATION (0xc0000005) at pc=0x00007ffa67f622b3".
    ///
    /// Windows names an exception, Unix a signal, and a runtime that caught itself out writes
    /// "Internal Error" with the file and line of its own assertion.
    /// </summary>
    [GeneratedRegex(@"^#\s+(?<signal>EXCEPTION_[A-Z_]+|SIG[A-Z]+|Internal Error)\b",
        RegexOptions.Multiline)]
    private static partial Regex FatalSignalPattern();

    /// <summary>
    /// The frame it died in, which is on the line after the label:
    ///
    ///     # Problematic frame:
    ///     # V  [jvm.dll+0x2222b3]
    ///
    /// The letter is the kind of code — V for the runtime's own, C for a native library, J for
    /// compiled Java and j for interpreted — and it decides how the rest is read.
    /// </summary>
    [GeneratedRegex(@"# Problematic frame:\s*\r?\n#\s+(?<kind>[VCJj])\s+(?<frame>[^\r\n]+)")]
    private static partial Regex FatalFramePattern();

    /// <summary>The binary in a frame: the name up to the offset in "[nvoglv64.dll+0x1044c1e]".</summary>
    [GeneratedRegex(@"^\[(?<lib>[^+\]\s]+)")]
    private static partial Regex FrameLibraryPattern();

    /// <summary>
    /// Compiler replay data, which the runtime writes only when the thread that died was a JIT
    /// compiler thread. Its presence names the thread; nothing else in the printed block does.
    /// </summary>
    [GeneratedRegex(@"Compiler replay data is saved as", RegexOptions.IgnoreCase)]
    private static partial Regex CompilerReplayPattern();

    /// <summary>
    /// The compile that was in progress, which the error file records under its own heading:
    ///
    ///     Current CompileTask:
    ///     C2:  67525 12345       4       net.minecraft.client.Minecraft::runTick (1234 bytes)
    ///
    /// Written only for a thread that was compiling, which makes it the file's answer to the
    /// question the log answers with the replay line. The angle brackets are for constructors,
    /// which are compiled like anything else and named "&lt;init&gt;".
    /// </summary>
    [GeneratedRegex(@"Current CompileTask:\s*\r?\n[^\r\n]*?\s(?<method>[\w$]+(?:\.[\w$]+)*::(?:[\w$]+|<init>|<clinit>))")]
    private static partial Regex CompileTaskPattern();

    /// <summary>The hs_err file, whose path is on the line after the sentence announcing it.</summary>
    [GeneratedRegex(@"An error report file with more information is saved as:\s*\r?\n#\s*(?<path>[^\r\n]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex ErrorFilePattern();

    /// <summary>"# JRE version: OpenJDK Runtime Environment Microsoft-11369865 (17.0.15+6) (build ...)".</summary>
    [GeneratedRegex(@"# JRE version:[^\r\n]*?\((?<version>\d[\w.+-]*)\)")]
    private static partial Regex JreBuildPattern();

    /// <summary>
    /// The runtime failing to get memory from the machine, which it reports instead of the usual
    /// fatal block rather than alongside it — there is no signal and no frame, because nothing
    /// crashed. The line under it says what the allocation was for.
    /// </summary>
    [GeneratedRegex(@"There is insufficient memory for the Java Runtime Environment to continue\."
        + @"(?:\s*\r?\n#\s*(?<what>Native memory[^\r\n]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex NativeMemoryPattern();

    /// <summary>
    /// Graphics drivers by their own file names, in two halves.
    ///
    /// The first are a vendor's own and carry a bitness or a driver generation in the middle of
    /// them — nvoglv64, ig9icd64 — so they are matched as prefixes and nothing else on a machine
    /// is spelled remotely like them.
    ///
    /// The second are ordinary words, and those need a boundary after them or they swallow
    /// libraries that merely start the same way. Without one, "libGL" matches libglib, which is
    /// GLib: a general-purpose library with no connection to a graphics card at all, bundled by
    /// half the native code in existence — and a crash inside it would have been answered with
    /// "update your graphics driver". The lookahead is what a word boundary cannot be here,
    /// since a file name runs straight on into its version and its extension.
    /// </summary>
    [GeneratedRegex(@"^(?:nvoglv|nvwgf2um|nvd3dum|nvcuda|nvapi|nvumdshim|nvldumd|libnvidia|atio6axx|atioglxx|"
        + @"aticfx|amdvlk|amdxc|amdihk|ig\d*icd|igd\d*iumd|igdumd|igxel|igvk|radeonsi|iris_dri|i965_dri|"
        + @"swrast"
        + @"|(?:opengl32|vulkan|nvidia|libGL|libGLX|libGLdispatch|libGLESv2|libEGL)(?=[\d._+-]|$))",
        RegexOptions.IgnoreCase)]
    private static partial Regex GraphicsLibraryPattern();

    /// <summary>Chromium, as MCEF and the mods built on it embed it.</summary>
    [GeneratedRegex(@"^(libcef|chrome_elf|cef|jcef)", RegexOptions.IgnoreCase)]
    private static partial Regex EmbeddedBrowserPattern();

    /// <summary>Java's own engine, under either of the names it goes by.</summary>
    [GeneratedRegex(@"^(jvm\.dll|libjvm)", RegexOptions.IgnoreCase)]
    private static partial Regex JvmLibraryPattern();

    [GeneratedRegex(@"Pixel format not accelerated|Couldn't set pixel format|Failed to create window|GLFW error|" +
        @"EXCEPTION_ACCESS_VIOLATION[\s\S]{0,400}?(nvoglv|atio6axx|amdvlk|ig\d*icd|opengl32|vulkan)|" +
        @"OpenGL \d\.\d.*not supported|no suitable graphics",
        RegexOptions.IgnoreCase)]
    private static partial Regex GraphicsPattern();

    [GeneratedRegex(@"UnsupportedClassVersionError|has been compiled by a more recent version of the Java Runtime|" +
        @"class file version \d+\.\d+",
        RegexOptions.IgnoreCase)]
    private static partial Regex JavaVersionPattern();

    [GeneratedRegex(@"ZipException|Invalid CEN header|error in opening zip file|Corrupted|" +
        @"SHA-?1 mismatch|checksum mismatch",
        RegexOptions.IgnoreCase)]
    private static partial Regex CorruptPattern();

    /// <summary>Fabric and Forge both spell missing dependencies out in plain words.</summary>
    [GeneratedRegex(@"requires (?:any )?version [^,]+ of (?<dep>[\w .'-]+), which is missing|" +
        @"Missing (?:or unsupported )?mandatory dependenc(?:y|ies)[:\s]+(?<dep>[\w .'-]+)|" +
        @"Mod (?:'[^']+'|""[^""]+"") requires (?<dep>[\w.'-]+) ",
        RegexOptions.IgnoreCase)]
    private static partial Regex MissingDependencyPattern();

    /// <summary>The loader pointing straight at one mod.</summary>
    [GeneratedRegex(@"Mixin apply for mod (?<id>[\w.-]+) failed|" +
        @"Failed to create mod instance\. ModID: (?<id>[\w.-]+)|" +
        @"Suspected Mods?:\s*(?<id>[\w.-]+)|" +
        @"^-- MOD (?<id>[\w.-]+) --|" +
        @"Mod File: .*[/\\](?<id>[\w.-]+)\.jar|" +
        @"in mod (?<id>[\w.-]+) failed|" +
        // Fabric, saying whose entrypoint threw. This is the loader naming the mod that stopped
        // the game, and it is the line that matters most in a Fabric crash — without it, a
        // startup failure gets attributed to whatever else happened to be mentioned nearby.
        @"provided by '(?<id>[\w.-]+)'|" +
        @"^\s*(?<id>[\w.-]+) \(.*\) has failed to load correctly",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DirectAccusationPattern();

    /// <summary>"sodium.mixins.json" — mixin configs are named after their own mod by convention.</summary>
    [GeneratedRegex(@"(?<id>[\w-]+)\.mixins\.json", RegexOptions.IgnoreCase)]
    private static partial Regex MixinConfigPattern();

    /// <summary>Java naming which classloader a class came out of, in a failed cast.</summary>
    [GeneratedRegex(@"is in unnamed module of loader '(?<loader>[\w.]+)'")]
    private static partial Regex LoaderSplitPattern();

    /// <summary>Any failed cast at all, only used to say how many there were.</summary>
    [GeneratedRegex(@"cannot be cast to class")]
    private static partial Regex ClassCastPattern();

    /// <summary>A dotted, Java-looking class name, which carries the mod's own name in it.</summary>
    [GeneratedRegex(@"\b[a-z][\w]*(?:\.[\w$]+){2,}\b")]
    private static partial Regex QualifiedClassPattern();

    /// <summary>
    /// A mod's entrypoint class refusing to be what the loader needs it to be.
    ///
    /// Fabric loads an entrypoint by casting the class the mod declared to the interface for that
    /// stage. When that cast fails, the class is on the wrong side of a loader boundary or was
    /// compiled against a different Fabric — either way the mod's build does not fit this one, and
    /// no amount of turning other mods off will change it.
    /// </summary>
    [GeneratedRegex(@"LanguageAdapterException: Class (?<class>[\w.$]+) cannot be cast to (?<wanted>[\w.$]+)")]
    private static partial Regex AdapterMismatchPattern();

    /// <summary>
    /// A line the game itself says can be disregarded.
    ///
    /// Mixin says exactly this about a missing refmap, which is normal in any environment that is
    /// not a development one and means nothing at all. It names a mixin config while it does it,
    /// which is enough to get an entirely healthy mod accused of causing a crash.
    /// </summary>
    [GeneratedRegex(@"you can ignore this message|is not supported in this environment|" +
        @"development environment", RegexOptions.IgnoreCase)]
    private static partial Regex HarmlessPattern();

    [GeneratedRegex(@"^\s+at (?<at>[\w$.]+)", RegexOptions.Multiline)]
    private static partial Regex StackFramePattern();

    /// <summary>Forge's "~[thejar-1.2.3.jar%23265!/:1.2.3]" on the end of a stack frame.</summary>
    [GeneratedRegex(@"~\[(?<jar>[^\]%]+(?:%20[^\]%]+)*\.jar)", RegexOptions.IgnoreCase)]
    private static partial Regex FrameJarPattern();

    /// <summary>And "TRANSFORMER/geckolib3@3.1.40/" in front of one.</summary>
    [GeneratedRegex(@"TRANSFORMER/(?<id>[\w.-]+)@", RegexOptions.IgnoreCase)]
    private static partial Regex TransformerPattern();

    /// <summary>One line of Forge's Suspected Mods block: "WebDisplays (webdisplays), Version: 1.3.3".</summary>
    [GeneratedRegex(@"^\s+.+? \((?<id>[\w.-]+)\), Version:", RegexOptions.Multiline)]
    private static partial Regex SuspectedModPattern();

    /// <summary>Everything from the first version-looking segment to the end of the name.</summary>
    [GeneratedRegex(@"[-_+](?:v?\d.*|mc\d.*|fabric|forge|neoforge|quilt)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTailPattern();
}
