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
    OutOfMemory,
    Graphics,
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

    public static CrashAnalysis Analyze(string? report, IReadOnlyList<ModEntry> installedMods)
    {
        if (string.IsNullOrWhiteSpace(report)) return CrashAnalysis.None;

        // A launch log for a session that quit normally sits in the same list as real crashes,
        // and mods log handled stack traces all the time during a session that ends fine. Without
        // this gate every one of those would get a mod accused over a crash that never happened.
        if (!LooksLikeCrash(report)) return new CrashAnalysis(
            CrashCause.Clean,
            "No crash in this log",
            "The game ran to the end of this log without dying. Any errors in it were handled.", []);

        if (Environmental(report) is { } environmental) return environmental;

        if (MissingDependency(report) is { } missing) return missing;

        // Before the suspect hunt: the loader has already said exactly which two mods disagree,
        // and guessing from stack frames after that would be picking through a bin next to a
        // signed confession. Fabric's stack here is all loader frames anyway, so the hunt finds
        // nothing and the whole crash comes back as "nothing obvious".
        if (Incompatible(report) is { } incompatible) return incompatible;

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
        foreach (Match match in MixinConfigPattern().Matches(report))
        {
            var named = match.Groups["id"].Value;
            if (Resolve(named, tokens) is { } file) Accuse(file, DirectAccusationScore, match.Value);
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
        return tokens.FirstOrDefault(pair =>
            pair.Key.Replace("-", "").Replace("_", "").Equals(normalised, StringComparison.OrdinalIgnoreCase)).Value;
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
        @"^\s*(?<id>[\w.-]+) \(.*\) has failed to load correctly",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex DirectAccusationPattern();

    /// <summary>"sodium.mixins.json" — mixin configs are named after their own mod by convention.</summary>
    [GeneratedRegex(@"(?<id>[\w-]+)\.mixins\.json", RegexOptions.IgnoreCase)]
    private static partial Regex MixinConfigPattern();

    [GeneratedRegex(@"^\s+at (?<at>[\w$.]+)", RegexOptions.Multiline)]
    private static partial Regex StackFramePattern();

    /// <summary>Everything from the first version-looking segment to the end of the name.</summary>
    [GeneratedRegex(@"[-_+](?:v?\d.*|mc\d.*|fabric|forge|neoforge|quilt)$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionTailPattern();
}
