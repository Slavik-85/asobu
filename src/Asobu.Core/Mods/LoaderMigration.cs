using Asobu.Core.Minecraft;

namespace Asobu.Core.Mods;

/// <summary>
/// Mods that are not one project with builds for every loader, but two projects with different
/// names. Swapping an instance's loader has to follow those across, or the person is told their
/// shader loader "has no build" when what really happened is that it is called something else on
/// the other side.
///
/// Deliberately short. Nearly every mod — EntityCulling, FerriteCore, Continuity — publishes one
/// project covering Fabric, Forge, NeoForge and Quilt, and those need no help at all. This is
/// only for the ones where a port lives under its own name, which is a small and well-known set.
/// </summary>
public static class LoaderCounterparts
{
    /// <summary>Modrinth slugs, since both halves of every pair below are on Modrinth.</summary>
    private static readonly Dictionary<string, Dictionary<string, string>> Pairs = new(StringComparer.OrdinalIgnoreCase)
    {
        // The renderer, and its Forge port.
        ["sodium"] = new(StringComparer.OrdinalIgnoreCase) { ["forge"] = "embeddium" },
        ["embeddium"] = new(StringComparer.OrdinalIgnoreCase) { ["fabric"] = "sodium", ["quilt"] = "sodium" },

        // Shaders.
        ["iris"] = new(StringComparer.OrdinalIgnoreCase) { ["forge"] = "oculus" },
        ["oculus"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fabric"] = "iris", ["quilt"] = "iris", ["neoforge"] = "iris",
        },

        // The API half the ecosystem is built on.
        ["fabric-api"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["forge"] = "forgified-fabric-api", ["neoforge"] = "forgified-fabric-api", ["quilt"] = "qsl",
        },
        ["forgified-fabric-api"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fabric"] = "fabric-api", ["quilt"] = "qsl",
        },
        ["qsl"] = new(StringComparer.OrdinalIgnoreCase)
        {
            ["fabric"] = "fabric-api", ["forge"] = "forgified-fabric-api", ["neoforge"] = "forgified-fabric-api",
        },
    };

    /// <summary>What this project is called on the other loader, or null when it is the same one.</summary>
    public static string? For(string projectSlug, string loader) =>
        Pairs.TryGetValue(projectSlug, out var byLoader) && byLoader.TryGetValue(loader, out var counterpart)
            ? counterpart
            : null;
}

/// <summary>
/// One installed mod, and what would become of it on the new loader.
/// </summary>
/// <param name="Installed">The jar as it sits on disk now.</param>
/// <param name="Target">The build to put in its place, or null when there is none to be had.</param>
/// <param name="TargetName">
/// What the replacement is called. Different from the installed name only for a mod whose port
/// lives under another name — Sodium becoming Embeddium — which is worth showing rather than
/// quietly performing.
/// </param>
/// <param name="Loader">Where it was being moved to, so a refusal can name the pairing.</param>
/// <param name="GameVersion">And which Minecraft version, which is the other half of it.</param>
public sealed record ModMove(
    ModEntry Installed,
    ModVersion? Target,
    string? TargetName,
    string Loader = "",
    string GameVersion = "")
{
    public string Name => Installed.Name;

    public bool CanMove => Target is { Url.Length: > 0 };

    /// <summary>True when the replacement is a differently named project, not another build.</summary>
    public bool IsRename => CanMove && TargetName is { Length: > 0 } && !TargetName.Equals(Name, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Names the pairing rather than the mod. "Nothing published for this loader" reads as though
    /// the mod were abandoned; the truth is almost always that this loader and this Minecraft
    /// version have nothing between them, which is a fact about the pair and not about the mod.
    /// </summary>
    public string Summary => CanMove
        ? IsRename ? $"{Name} → {TargetName} {Target!.VersionNumber}" : $"{Name} {Target!.VersionNumber}"
        : Loader is { Length: > 0 } && GameVersion is { Length: > 0 }
            ? $"{Name} — no {Loader} build for Minecraft {GameVersion}"
            : $"{Name} — nothing published for this loader";
}
