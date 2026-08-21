namespace Asobu.Core.Servers;

/// <summary>
/// One server worth suggesting, and what a client needs to reach it.
/// </summary>
/// <param name="Name">What it calls itself.</param>
/// <param name="Address">The address to connect to, as somebody would type it into Minecraft.</param>
/// <param name="VersionLabel">The range as a person reads it, which is what the page shows.</param>
/// <param name="MinVersion">Oldest Minecraft it accepts, inclusive. Null for no floor.</param>
/// <param name="MaxVersion">Newest it accepts, inclusive. Null for "and everything after".</param>
public sealed record GameServer(
    string Name,
    string Address,
    string VersionLabel,
    string? MinVersion,
    string? MaxVersion)
{
    /// <summary>Whether a Minecraft version is inside this server's range.</summary>
    public bool Accepts(string version)
    {
        if (MinVersion is { Length: > 0 } floor && GameVersions.Compare(version, floor) < 0) return false;
        if (MaxVersion is { Length: > 0 } ceiling && GameVersions.Compare(version, ceiling) > 0) return false;

        return true;
    }
}

/// <summary>
/// Comparing Minecraft version names.
///
/// Only the release ones, which are dotted numbers and nothing else. A snapshot is named like
/// "24w14a" and does not belong on this scale at all — rather than guess where it sits, anything
/// unparseable sorts as newer than everything, so a server never turns somebody away on the
/// strength of a version this could not read.
/// </summary>
public static class GameVersions
{
    public static int Compare(string left, string right)
    {
        var a = Parse(left);
        var b = Parse(right);

        if (a is null || b is null) return 0;

        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var one = i < a.Length ? a[i] : 0;
            var two = i < b.Length ? b[i] : 0;

            if (one != two) return one.CompareTo(two);
        }

        return 0;
    }

    private static int[]? Parse(string version)
    {
        // A release always has a dot in it and a snapshot never does — "24w14a", "rd-132211".
        // Without this the leading digits of a snapshot read as a version number, and 24w14a
        // ranks as newer than everything ever released.
        if (!version.Contains('.', StringComparison.Ordinal)) return null;

        var parts = version.Split('.');
        var numbers = new int[parts.Length];

        for (var i = 0; i < parts.Length; i++)
        {
            // "1.8.9-pre2" and the like: the number is what counts and the tail is not ours to
            // rank. Taking the leading digits keeps a pre-release beside the version it precedes.
            var digits = new string([.. parts[i].TakeWhile(char.IsAsciiDigit)]);

            if (digits.Length == 0 || !int.TryParse(digits, out numbers[i])) return null;
        }

        return numbers;
    }
}

/// <summary>
/// The servers Asobu suggests.
///
/// A short, opinionated list rather than a directory: a list of every server in the world is a
/// website, and one somebody has to scroll is not a suggestion. These are typed in rather than
/// fetched, so the page works offline and cannot start advertising something nobody chose.
/// </summary>
public static class SuggestedServers
{
    public static IReadOnlyList<GameServer> All { get; } =
    [
        // Asobu's own alias, which Hypixel resolves the same as mc.hypixel.net.
        new("Hypixel", "asobu.hypixel.net", "1.8.x+", "1.8", null),
        new("Mineplex", "play.mineplex.com", "1.8.9 - 1.21", "1.8.9", "1.21"),
        new("MCC Island", "play.mccisland.net", "1.21.11+", "1.21.11", null),
        new("PvP Club", "mcpvp.club", "1.21.2+", "1.21.2", null),
        new("PvP Legacy", "pvplegacy.net", "1.21.2+", "1.21.2", null),
    ];
}
