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
}
