namespace Asobu.Core.Minecraft;

/// <summary>
/// Flattens an inheritsFrom chain (loader version -> vanilla version) into one descriptor.
/// Vanilla versions pass through untouched.
/// </summary>
public static class VersionResolver
{
    /// <param name="load">Fetches a version descriptor by id, from cache or network.</param>
    public static async Task<VersionJson> ResolveAsync(
        string id,
        Func<string, CancellationToken, Task<VersionJson>> load,
        CancellationToken cancellationToken = default)
    {
        var chain = new List<VersionJson>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = await load(id, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            if (!seen.Add(current.Id))
                throw new InvalidOperationException($"Circular inheritsFrom chain at '{current.Id}'.");

            chain.Add(current);
            if (current.InheritsFrom is not { Length: > 0 } parentId) break;
            current = await load(parentId, cancellationToken).ConfigureAwait(false);
        }

        // chain[0] is the most derived, chain[^1] the vanilla root. Fold from the root outward.
        var merged = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--)
            merged = Merge(parent: merged, child: chain[i]);

        return merged;
    }

    private static VersionJson Merge(VersionJson parent, VersionJson child) => new()
    {
        Id = child.Id,
        InheritsFrom = null,

        // Carried down the chain so the loader keeps pointing at the vanilla jar it inherits.
        ClientJarVersionId = parent.ClientJarVersionId ?? parent.Id,
        Type = child.Type ?? parent.Type,
        MainClass = child.MainClass ?? parent.MainClass,
        Assets = child.Assets ?? parent.Assets,
        AssetIndex = child.AssetIndex ?? parent.AssetIndex,
        JavaVersion = child.JavaVersion ?? parent.JavaVersion,
        Downloads = child.Downloads ?? parent.Downloads,
        Logging = child.Logging ?? parent.Logging,
        ComplianceLevel = child.ComplianceLevel ?? parent.ComplianceLevel,
        ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
        MinimumLauncherVersion = child.MinimumLauncherVersion ?? parent.MinimumLauncherVersion,

        // Loader libraries must precede vanilla ones on the classpath, or the loader's patched
        // classes lose to the originals. One entry per artifact, because two builds of one
        // library is not a preference — it is a crash.
        Libraries = Deduplicate([.. child.Libraries, .. parent.Libraries]),

        MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
        Arguments = MergeArguments(parent.Arguments, child.Arguments),
    };

    /// <summary>
    /// One entry per artifact, keeping the newest version of each where the chain disagrees.
    ///
    /// A loader and the vanilla version it inherits from routinely want different builds of the
    /// same library. Concatenating the two lists put both on the classpath, and Fabric refuses to
    /// start at all when it finds them:
    ///
    ///     duplicate ASM classes found on classpath: .../asm-9.10.1.jar, .../asm-9.6.jar
    ///
    /// Newest wins rather than the loader's choice winning outright, because the disagreement
    /// runs both ways — a loader can be older than the game it is being run against, and picking
    /// its build then would hand vanilla code a library from before the version it was compiled
    /// for. These libraries are compatible upwards; that is why both sides felt free to ask for
    /// different ones.
    ///
    /// Position follows the first mention, so a loader library that has to precede vanilla on the
    /// classpath still does even when the version taken is vanilla's.
    /// </summary>
    private static List<Library> Deduplicate(List<Library> libraries)
    {
        var best = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var kept = new List<Library>();

        foreach (var library in libraries)
        {
            var key = Maven.ArtifactKey(library.Name);

            if (!best.TryGetValue(key, out var at))
            {
                best[key] = kept.Count;
                kept.Add(library);
                continue;
            }

            if (IsNewer(library.Name, kept[at].Name)) kept[at] = library;
        }

        return kept;
    }

    /// <summary>
    /// Whether one coordinate names a later build than another. Unreadable versions never win, so
    /// a coordinate this cannot parse leaves whatever was already chosen in place.
    /// </summary>
    private static bool IsNewer(string candidate, string incumbent)
    {
        if (Maven.VersionOf(candidate) is not { } mine) return false;
        if (Maven.VersionOf(incumbent) is not { } theirs) return true;

        return Diagnostics.VersionBound.Compare(mine, theirs) > 0;
    }

    private static Arguments? MergeArguments(Arguments? parent, Arguments? child)
    {
        if (parent is null) return child;
        if (child is null) return parent;

        return new Arguments
        {
            Game = [.. parent.Game, .. child.Game],
            Jvm = [.. parent.Jvm, .. child.Jvm],
        };
    }
}
