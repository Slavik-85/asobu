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
        // classes lose to the originals.
        Libraries = [.. child.Libraries, .. parent.Libraries],

        MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
        Arguments = MergeArguments(parent.Arguments, child.Arguments),
    };

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
