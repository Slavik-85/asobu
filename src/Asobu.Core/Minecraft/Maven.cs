namespace Asobu.Core.Minecraft;

public static class Maven
{
    /// <summary>
    /// Turns Maven coordinates into the repository-relative path Mojang and the loaders use.
    /// "com.mojang:logging:1.2.7" becomes "com/mojang/logging/1.2.7/logging-1.2.7.jar".
    /// Supports the optional classifier and "@extension" suffix that Forge relies on.
    /// </summary>
    public static string PathFor(string coordinates)
    {
        var at = coordinates.IndexOf('@');
        var extension = at >= 0 ? coordinates[(at + 1)..] : "jar";
        var body = at >= 0 ? coordinates[..at] : coordinates;

        var parts = body.Split(':');
        if (parts.Length < 3)
            throw new FormatException($"Not a Maven coordinate: '{coordinates}'.");

        var (group, artifact, version) = (parts[0], parts[1], parts[2]);
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";

        return Path.Combine(
            Path.Combine(group.Split('.')),
            artifact,
            version,
            $"{artifact}-{version}{classifier}.{extension}");
    }

    /// <summary>
    /// What identifies a library across versions: everything about the coordinate except the
    /// version itself. "org.ow2.asm:asm:9.6" and "org.ow2.asm:asm:9.10.1" share one.
    ///
    /// The classifier stays in, because a natives payload is a different file rather than a
    /// different build of the same one — dropping it would have a windows natives jar and a
    /// linux one look like two versions of one library.
    /// </summary>
    public static string ArtifactKey(string coordinates)
    {
        var body = coordinates.Split('@')[0];
        var parts = body.Split(':');

        if (parts.Length < 2) return coordinates;

        // group:artifact, then any classifier that followed the version.
        var classifier = parts.Length > 3 ? ":" + string.Join(':', parts[3..]) : "";

        return $"{parts[0]}:{parts[1]}{classifier}";
    }

    /// <summary>The version out of a coordinate, or null when there is not one to read.</summary>
    public static string? VersionOf(string coordinates)
    {
        var parts = coordinates.Split('@')[0].Split(':');

        return parts.Length >= 3 && parts[2].Length > 0 ? parts[2] : null;
    }
}
