namespace Asobu.Core.Minecraft;

/// <summary>Root of version_manifest_v2.json — the index of every Minecraft version.</summary>
public sealed class VersionManifest
{
    public required LatestVersions Latest { get; init; }
    public required IReadOnlyList<VersionSummary> Versions { get; init; }

    public VersionSummary? Find(string id) =>
        Versions.FirstOrDefault(v => string.Equals(v.Id, id, StringComparison.OrdinalIgnoreCase));
}

public sealed class LatestVersions
{
    public required string Release { get; init; }
    public required string Snapshot { get; init; }
}

public sealed class VersionSummary
{
    public required string Id { get; init; }

    /// <summary>release | snapshot | old_beta | old_alpha</summary>
    public required string Type { get; init; }

    /// <summary>URL of this version's own JSON descriptor.</summary>
    public required string Url { get; init; }

    /// <summary>SHA1 of the document at <see cref="Url"/>. Present in manifest v2; lets us cache it safely.</summary>
    public string? Sha1 { get; init; }

    public DateTimeOffset ReleaseTime { get; init; }
    public DateTimeOffset Time { get; init; }
    public int ComplianceLevel { get; init; }

    public bool IsRelease => Type == "release";
}
