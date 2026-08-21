using System.Text.RegularExpressions;
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
            $"{scale}which happens when the same library ends up both on the launch classpath and inside "
            + "Fabric's own loader. Nothing can be cast between the two copies, so mixins and entrypoints all "
            + "fail at once. None of your mods caused this and turning them off will not help — it is the "
            + "launcher's classpath. Reinstalling the instance's loader is the thing most likely to clear it.",
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

        if (OutOfMemoryPattern().IsMatch(report))
            return new CrashAnalysis(CrashCause.OutOfMemory, "Ran out of memory",
                "The game asked for more memory than it was allowed. Raise this instance's memory in its settings, " +
                "or turn the automatic limit back on and let Asobu size it from the pack.", []);

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
        foreach (Match match in MixinConfigPattern().Matches(report))
        {
            var line = LineAround(report, match.Index);
            if (HarmlessPattern().IsMatch(line)) continue;

            var named = match.Groups["id"].Value;
            if (Resolve(named, tokens) is { } file) Accuse(file, MixinConfigScore, line);
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
            var qualified = frame.Groups["at"].Value;

            foreach (var (token, file) in tokens)
            {
                if (!qualified.Contains(token, StringComparison.OrdinalIgnoreCase)) continue;
                Accuse(file, firstTraceDone ? LaterStackFrameScore : FirstStackFrameScore, line);
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

    /// <summary>Everything from the first version-looking segment to the end of the name.</summary>
    [GeneratedRegex(@"[-_+](?:v?\d.*|mc\d.*|fabric|forge|neoforge|quilt)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTailPattern();
}
