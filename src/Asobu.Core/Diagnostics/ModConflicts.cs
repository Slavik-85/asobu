using System.Text.RegularExpressions;

namespace Asobu.Core.Diagnostics;

/// <summary>
/// A version requirement one mod places on another, as the loader stated it.
///
/// Deliberately loose: Fabric writes predicates in prose ("0.90.0 or later"), Forge writes Maven
/// ranges ("[15.2.0,)"), and neither is worth a full grammar here. What matters is the floor and
/// the ceiling, which is what decides whether a given build would satisfy the mod that complained.
/// </summary>
public sealed partial record VersionBound(string? AtLeast, string? Below, string? Exactly)
{
    /// <summary>
    /// An exclusive floor, for a requirement stated as "later than" rather than "at least".
    /// A breakage gives one of these: "incompatible with 1.11.2 or earlier" means strictly above.
    /// </summary>
    public string? Above { get; init; }

    public static readonly VersionBound Any = new(null, null, null);

    public bool IsAny => AtLeast is null && Below is null && Exactly is null && Above is null;

    public bool Accepts(string version)
    {
        if (Exactly is { Length: > 0 } exact) return Compare(version, exact) == 0;
        if (AtLeast is { Length: > 0 } floor && Compare(version, floor) < 0) return false;
        if (Above is { Length: > 0 } exclusive && Compare(version, exclusive) <= 0) return false;
        if (Below is { Length: > 0 } ceiling && Compare(version, ceiling) >= 0) return false;

        return true;
    }

    /// <summary>
    /// Numbers as numbers, so 0.10 beats 0.9. Anything that is not a number — a "+build" tail, a
    /// "-beta" suffix — stops the comparison rather than being guessed at, because a wrong guess
    /// here means swapping a working mod for one that does not fit.
    /// </summary>
    public static int Compare(string left, string right)
    {
        var a = Numbers(left);
        var b = Numbers(right);

        for (var i = 0; i < Math.Max(a.Count, b.Count); i++)
        {
            var x = i < a.Count ? a[i] : 0;
            var y = i < b.Count ? b[i] : 0;

            if (x != y) return x.CompareTo(y);
        }

        return 0;
    }

    private static List<int> Numbers(string version)
    {
        // Plenty of mods put the game version first: Sodium publishes "mc26.2-0.9.1-fabric".
        // Without this the very first piece is "mc26", which has no leading digits, and the whole
        // string parses as no version at all — so every build fails every bound and a swap that
        // had a perfectly good answer available reports that nothing fits.
        version = GameVersionPrefix().Replace(version, "");

        // Build metadata is not part of the version. "1.11.2+26.2" is 1.11.2, and reading the
        // "+26.2" as two more components makes it sort above every plain 1.11.2.
        if (version.IndexOf('+') is >= 0 and var plus) version = version[..plus];

        var parts = new List<int>();

        foreach (var piece in version.Split('.', '-', '_'))
        {
            var digits = new string([.. piece.TakeWhile(char.IsDigit)]);
            if (digits.Length == 0) break;

            parts.Add(int.Parse(digits));
        }

        return parts;
    }

    /// <summary>A leading "mc1.20.1-" or "mc26.2_", which is the game's version and not the mod's.</summary>
    [GeneratedRegex(@"^mc\d+(?:\.\d+)*[-_.]?", RegexOptions.IgnoreCase)]
    private static partial Regex GameVersionPrefix();
}

/// <summary>
/// One mod asking for a different build of another that is already installed. This is the case
/// worth acting on: the mod is there, it is simply the wrong version, and swapping it is a thing
/// the launcher can do without asking anyone to go and read a log.
/// </summary>
/// <summary>The other mod in a disagreement, and what it would have to become instead.</summary>
public sealed record ModSwapTarget(string ModId, string ModName, VersionBound Wanted);

public sealed record ModConflict(
    string RequiredBy,
    string ModId,
    string ModName,
    string? Present,
    VersionBound Wanted,
    string Evidence)
{
    /// <summary>
    /// The same disagreement solved from the other end. Two mods that will not sit together can
    /// usually be fixed by moving either one, and the loader's suggestion is only its first idea
    /// — when no build of the mod it named will do, the other one is worth trying before giving
    /// up and telling someone nothing fits.
    /// </summary>
    public ModSwapTarget? Alternative { get; init; }

    public string Headline => $"{RequiredBy} needs a different {ModName}";

    public string Detail => Present is { Length: > 0 } present
        ? $"Wants {WantedLabel}, found {present}"
        : $"Wants {WantedLabel}";

    public string WantedLabel => Wanted switch
    {
        { Exactly: { Length: > 0 } exact } => exact,
        { AtLeast: { Length: > 0 } floor, Below: { Length: > 0 } ceiling } => $"{floor} up to {ceiling}",
        { AtLeast: { Length: > 0 } floor } => $"{floor} or later",

        // An exclusive floor, which is what every breakage produces. Without this case they all
        // fell through to "a different version" below — so a screen of them said the same thing
        // on every row, and the one number the loader actually gave us was thrown away.
        { Above: { Length: > 0 } exclusive } => $"later than {exclusive}",

        { Below: { Length: > 0 } ceiling } => $"anything below {ceiling}",
        _ => "a different version",
    };
}

/// <summary>
/// Reads a game log for mods that refused to load because another mod is the wrong version.
///
/// Both loaders say so plainly and in a fixed shape, which is what makes this worth parsing at
/// all — unlike a crash, where the cause has to be guessed at. If a line does not match, nothing
/// is claimed about it.
/// </summary>
public static partial class ModConflicts
{
    public static IReadOnlyList<ModConflict> Find(string log)
    {
        var found = new List<ModConflict>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Fabric().Matches(log))
        {
            var id = match.Groups["id"].Value;
            if (!seen.Add(id)) continue;

            found.Add(new ModConflict(
                match.Groups["by"].Value,
                id,
                match.Groups["name"].Success ? match.Groups["name"].Value : id,
                match.Groups["present"].Success ? match.Groups["present"].Value.Trim() : null,
                Bound(match.Groups["wanted"].Value),
                match.Value.Trim()));
        }

        // The breakages, read first so the fixes below can carry the other end of each one.
        var breakages = Breakages(log);

        // Fabric's own suggested fix, which is both the most actionable line in the log and
        // the only one that names a target version for the mod that has to change.
        foreach (Match match in FabricReplace().Matches(log))
        {
            var id = match.Groups["id"].Value;
            if (!seen.Add(id)) continue;

            var name = match.Groups["name"].Value;

            found.Add(new ModConflict(
                match.Groups["with"].Success && match.Groups["with"].Value.Trim().Length > 0
                    ? match.Groups["with"].Value.Trim()
                    : "another mod",
                id,
                name,
                match.Groups["present"].Value.Trim(),
                Bound(match.Groups["wanted"].Value),
                match.Value.Trim())
            {
                // The other end of this same disagreement, found by matching the mod Fabric wants
                // replaced against the breakage that explains why. Matched on the target rather
                // than the objector: the suggestion names the mod that has to move, and it is the
                // one that objected which becomes the alternative.
                Alternative = breakages
                    .FirstOrDefault(b => b.Target.ModId.Equals(id, StringComparison.OrdinalIgnoreCase)
                                      || b.Target.ModName.Equals(name, StringComparison.OrdinalIgnoreCase))
                    ?.Objector,
            });
        }

        // A breakage with no suggested fix beside it. The mod that has to move is the one that
        // is installed, and it has to move past the version the other refuses to sit with.
        //
        // Only where the loader suggested nothing: a breakage already carried as an alternative
        // above is the same disagreement, and listing it again would offer two rows for one
        // problem — and invite someone to fix it twice, from both ends.
        // Every mod already spoken for by a row above, from either end. A breakage whose target is
        // the subject of a suggested fix is that same fix seen from the other side.
        var carried = found
            .SelectMany(conflict => new[] { conflict.ModId, conflict.Alternative?.ModId })
            .Where(id => id is { Length: > 0 })
            .ToHashSet(StringComparer.OrdinalIgnoreCase!);

        foreach (var breakage in breakages)
        {
            var target = breakage.Target;
            if (carried.Contains(target.ModId) || !seen.Add(target.ModId)) continue;

            found.Add(new ModConflict(
                breakage.Objector.ModName, target.ModId, target.ModName, null, target.Wanted,
                breakage.Objector.ModName)
            {
                Alternative = breakage.Objector,
            });
        }

        foreach (Match match in Quilt().Matches(log))
        {
            // Quilt names mods by display name; there is no separate id in the sentence, so the
            // name serves as both. ModConflict's consumers match loosely enough for that.
            var id = match.Groups["name"].Value.Trim();
            if (!seen.Add(id)) continue;

            found.Add(new ModConflict(
                match.Groups["by"].Value.Trim(),
                id,
                id,
                match.Groups["present"].Value.Trim(),
                Bound(match.Groups["wanted"].Value),
                match.Value.Trim()));
        }

        foreach (Match match in Forge().Matches(log))
        {
            var id = match.Groups["id"].Value;

            // Forge reports a mod that is absent through the same table as one at the wrong
            // version, with [MISSING] where the version would be. That is a dependency to
            // fetch, not a build to swap — offering Swap for it would only ever fail with "not
            // in this instance". See MissingDependencies.
            if (match.Groups["present"].Value.Contains(
                    MissingDependencies.ForgeMissingMarker, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!seen.Add(id)) continue;

            found.Add(new ModConflict(
                match.Groups["by"].Value,
                id,
                id,
                match.Groups["present"].Value.Trim(),
                Bound(match.Groups["wanted"].Value),
                match.Value.Trim()));
        }

        return found;
    }

    /// <summary>
    /// Turns whichever way the loader wrote the requirement into a floor and a ceiling. What it
    /// cannot read comes back as <see cref="VersionBound.Any"/>, which means the newest build is
    /// offered — usually right, and never a swap to something older than what is there.
    /// </summary>
    public static VersionBound Bound(string wanted)
    {
        wanted = wanted.Trim();

        // Maven, as Forge writes it: [1.2,) or [1.2,2.0) or (1.2,2.0]
        if (Maven().Match(wanted) is { Success: true } maven)
        {
            var low = maven.Groups["low"].Value;
            var high = maven.Groups["high"].Value;

            return new VersionBound(
                low.Length > 0 ? low : null,
                high.Length > 0 ? high : null,
                null);
        }

        // A family, as Fabric's suggested fix writes it: 0.9.x means at least 0.9 and below
        // 0.10. Checked before the prose forms, which would otherwise read the "0.9" and lose
        // the ceiling — leaving 0.10 looking like an acceptable answer to "any 0.9.x".
        if (Family().Match(wanted) is { Success: true } family)
        {
            var stem = family.Groups["stem"].Value;
            var last = stem.LastIndexOf('.');

            // The final component, raised by one, is where the family ends.
            if (last >= 0 && int.TryParse(stem[(last + 1)..], out var tail))
                return new VersionBound(stem, stem[..(last + 1)] + (tail + 1), null);

            return int.TryParse(stem, out var major)
                ? new VersionBound(stem, (major + 1).ToString(), null)
                : VersionBound.Any;
        }

        // Fabric, as prose or as an operator.
        if (AtLeast().Match(wanted) is { Success: true } atLeast)
            return new VersionBound(atLeast.Groups["v"].Value, null, null);

        if (Below().Match(wanted) is { Success: true } below)
            return new VersionBound(null, below.Groups["v"].Value, null);

        if (Exact().Match(wanted) is { Success: true } exact)
            return new VersionBound(null, null, exact.Groups["v"].Value);

        return VersionBound.Any;
    }

    /// <summary>
    /// Fabric loader, which names both mods and says which version it found. Only the "wrong
    /// version is present" shape is matched: a dependency that is missing altogether is a
    /// different problem, and installing one is not a swap.
    /// </summary>
    [GeneratedRegex(
        @"Mod '(?<by>[^']+)'[^\n]*? requires (?<wanted>[^\n]+?) of (?:mod '(?<name>[^']+)' \()?(?<id>[A-Za-z0-9_\-]+)\)?, but only the wrong version is present: \S+ (?<present>[^!\n]+)!",
        RegexOptions.IgnoreCase)]
    private static partial Regex Fabric();

    /// <summary>
    /// One "X is incompatible with version V or earlier of mod 'Y', yet a conflicting version is
    /// present" line, with both ends of it kept.
    ///
    /// Both are needed because the two ways out of one disagreement point in opposite directions:
    /// <see cref="Target"/> is Y moving past V, which is the fix Fabric states, and
    /// <see cref="Objector"/> is X moving instead, which is the fix it never mentions because it
    /// only ever proposes changing one of the pair.
    /// </summary>
    private sealed record Breakage(ModSwapTarget Objector, ModSwapTarget Target);

    private static List<Breakage> Breakages(string log)
    {
        var found = new List<Breakage>();

        foreach (Match match in FabricBreakage().Matches(log))
        {
            var by = match.Groups["by"].Value.Trim();
            var byId = match.Groups["byId"].Value.Trim();
            var id = match.Groups["id"].Value.Trim();
            var name = match.Groups["name"].Value.Trim();
            var ceiling = match.Groups["ceiling"].Value.Trim();

            if (by.Length == 0 || id.Length == 0 || ceiling.Length == 0) continue;

            found.Add(new Breakage(
                // No bound for the objector: the log says which version of it refuses to sit with
                // what, never which version of it would. Any means the newest build that fits the
                // instance, which is the best guess available and usually the right one.
                new ModSwapTarget(byId.Length > 0 ? byId : by, by, VersionBound.Any),
                new ModSwapTarget(id, name, new VersionBound(null, null, null) { Above = ceiling })));
        }

        return found;
    }

    /// <summary>
    /// The breakage itself. "or earlier" is what makes the fix an exclusive floor: anything at or
    /// below that version is refused, so the mod has to go strictly past it.
    /// </summary>
    [GeneratedRegex(
        @"Mod '(?<by>[^']+)' \((?<byId>[^)]+)\)[^\n]*? is incompatible with version (?<ceiling>\S+) or earlier "
        + @"of mod '(?<name>[^']+)' \((?<id>[^)]+)\), yet a conflicting version is present",
        RegexOptions.IgnoreCase)]
    private static partial Regex FabricBreakage();

    /// <summary>
    /// Fabric's "a potential solution has been determined" block, which is printed whenever the
    /// loader can work out which mod to change:
    ///
    ///   - Replace mod 'Sodium' (sodium) 0.9.2-alpha.4 with any 0.9.x version that is compatible with:
    ///        - iris 1.11.2
    ///
    /// Worth parsing above everything else here: it names the mod to swap, the build that is
    /// installed, and the family to swap to, which is exactly what a swap needs and what the
    /// incompatibility line on its own does not say.
    /// </summary>
    [GeneratedRegex(
        @"Replace mod '(?<name>[^']+)' \((?<id>[^)]+)\) (?<present>\S+) with any (?<wanted>\S+) version[^\n]*\n"
        + @"(?:[^\n]*?-\s*(?<with>[^\s][^\n]*?)\s*\n)?",
        RegexOptions.IgnoreCase)]
    private static partial Regex FabricReplace();

    /// <summary>
    /// Quilt's solver, which words the same situation differently from Fabric's despite being a
    /// fork of it. Its sentences are assembled from templates in the loader's own language file:
    /// a requirement ("Foo requires at least version 1.2 or any newer version of Bar") joined by
    /// a comma to an outcome ("but only a different version is present: 1.0").
    ///
    /// Only the mismatch outcome is matched, for the same reason as Fabric's: a dependency that
    /// is absent altogether needs installing, not swapping.
    /// </summary>
    [GeneratedRegex(
        @"(?<by>[^\n,]+?) (?:transitively )?requires (?<wanted>[^\n]+?) of "
        + @"(?<name>[^\n,]+?), but only a different version is present: (?<present>[^\n!]+)",
        RegexOptions.IgnoreCase)]
    private static partial Regex Quilt();

    /// <summary>Forge and NeoForge, which dump a table of what they wanted against what they got.</summary>
    [GeneratedRegex(
        @"Mod ID: '(?<id>[^']+)', Requested by: '(?<by>[^']+)', Expected range: '(?<wanted>[^']*)', Actual version: '(?<present>[^']*)'",
        RegexOptions.IgnoreCase)]
    private static partial Regex Forge();

    [GeneratedRegex(@"^[\[(](?<low>[^,\])]*),(?<high>[^,\])]*)[\])]$")]
    private static partial Regex Maven();

    /// <summary>"0.9.x", "1.20.x" — a version family with the last component left open.</summary>
    [GeneratedRegex(@"^(?<stem>\d+(?:\.\d+)*)\.[xX*]$")]
    private static partial Regex Family();

    [GeneratedRegex(@"^(?:>=\s*|version\s+)?(?<v>[\w.+\-]+)\s*(?:or later|or above|\+)?$", RegexOptions.IgnoreCase)]
    private static partial Regex AtLeast();

    [GeneratedRegex(@"^<\s*(?<v>[\w.+\-]+)$")]
    private static partial Regex Below();

    [GeneratedRegex(@"^=\s*(?<v>[\w.+\-]+)$")]
    private static partial Regex Exact();
}
