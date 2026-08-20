using System.Text.RegularExpressions;

namespace Asobu.Core.Diagnostics;

/// <summary>
/// A mod the loader wanted and could not find.
///
/// Distinct from <see cref="ModConflict"/>, which is the same shape of complaint about something
/// that <i>is</i> installed at the wrong version. The difference matters because the answers
/// differ: one is a swap, the other is a download.
/// </summary>
/// <param name="RequiredBy">The mod that asked for it, for saying who wants this.</param>
/// <param name="Id">The loader's id for it, which is the best thing to search a catalogue by.</param>
/// <param name="Name">Its display name where the loader gave one, else the id again.</param>
public sealed record MissingDependency(string RequiredBy, string Id, string Name, string Evidence)
{
    public string Headline => $"{RequiredBy} needs {Name}";
}

/// <summary>
/// Reads dependencies the loader reported missing out of a launch log.
///
/// Both loaders say this plainly, which is what makes it worth parsing at all — nothing here is
/// inferred from a stack trace. A line that does not match is left alone.
/// </summary>
public static partial class MissingDependencies
{
    /// <summary>What Forge writes in the "Actual version" column when a mod is not there.</summary>
    public const string ForgeMissingMarker = "[MISSING]";

    public static IReadOnlyList<MissingDependency> Find(string log)
    {
        var found = new List<MissingDependency>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Fabric().Matches(log))
        {
            var id = match.Groups["id"].Value.Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;

            var name = match.Groups["name"].Success && match.Groups["name"].Value.Trim().Length > 0
                ? match.Groups["name"].Value.Trim()
                : id;

            found.Add(new MissingDependency(match.Groups["by"].Value.Trim(), id, name, match.Value.Trim()));
        }

        foreach (Match match in Forge().Matches(log))
        {
            var id = match.Groups["id"].Value.Trim();
            if (id.Length == 0 || !seen.Add(id)) continue;

            found.Add(new MissingDependency(match.Groups["by"].Value.Trim(), id, id, match.Value.Trim()));
        }

        return found;
    }

    /// <summary>
    /// Fabric and Quilt, which both end the sentence with "which is missing!". The version they
    /// wanted is deliberately not captured: a mod that is not installed at all is fetched at
    /// whatever build suits this instance, and the newest that fits is that build.
    /// </summary>
    [GeneratedRegex(
        @"Mod '(?<by>[^']+)'[^\n]*? requires [^\n]*? of (?:mod '(?<name>[^']+)' \()?(?<id>[A-Za-z0-9_.\-]+)\)?, "
        + @"which is missing",
        RegexOptions.IgnoreCase)]
    private static partial Regex Fabric();

    /// <summary>
    /// Forge's table, where a missing mod is the one whose actual version reads [MISSING]. The
    /// same table reports wrong versions, which is why the marker is matched rather than the
    /// shape — see ModConflicts for the other half.
    /// </summary>
    [GeneratedRegex(
        @"Mod ID: '(?<id>[^']+)', Requested by: '(?<by>[^']+)', Expected range: '[^']*', "
        + @"Actual version: '\[MISSING\]'",
        RegexOptions.IgnoreCase)]
    private static partial Regex Forge();
}
