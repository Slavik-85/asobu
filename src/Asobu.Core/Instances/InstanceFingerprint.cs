using System.Security.Cryptography;
using System.Text;
using Asobu.Core.Mods;

namespace Asobu.Core.Instances;

/// <summary>
/// A short string standing for "this instance would play the same as that one".
///
/// It is what lets somebody press Join and simply start, rather than being asked which of their
/// instances to use — a question they can only answer by remembering what the host is running.
/// Two instances agreeing on version, loader and mods will get along in a world; that is the whole
/// claim, and it is deliberately no stronger.
///
/// <para>
/// Mods are compared by file name rather than by content hash. A hash would be stricter and would
/// also call two copies of the same mod different because one was downloaded from a different
/// mirror. The cost of being wrong here is small in one direction and large in the other: a false
/// mismatch builds an instance somebody already had, while a false match drops them into a world
/// their client cannot play. File names are compared exactly for that reason — a version bump
/// changes the name, and it should.
/// </para>
///
/// <para>
/// Disabled mods are left out. A mod that is not loaded cannot disagree with anything.
/// </para>
/// </summary>
public static class InstanceFingerprint
{
    /// <summary>Enough of a SHA-256 that a collision is not a thing anybody will meet.</summary>
    private const int Characters = 16;

    public static string Of(AsobuPaths paths, Instance instance) =>
        Of(instance.MinecraftVersion,
           instance.Loader,
           instance.LoaderVersion,
           ModNames(Path.Combine(paths.InstanceGameDir(instance.Folder), "mods")));

    /// <summary>The parts spelled out, so a test can ask what makes two instances differ.</summary>
    internal static string Of(string minecraftVersion, string loader, string? loaderVersion, IEnumerable<string> mods)
    {
        var parts = new List<string>
        {
            minecraftVersion.Trim().ToLowerInvariant(),
            loader.Trim().ToLowerInvariant(),

            // A loader without a version is "whatever was current", which is not something two
            // machines can be expected to have landed on identically.
            (loaderVersion ?? "").Trim().ToLowerInvariant(),
        };

        // Sorted, because the order a folder happens to list files in is not part of what an
        // instance is.
        parts.AddRange(mods.Select(name => name.Trim().ToLowerInvariant()).Order(StringComparer.Ordinal));

        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", parts)));
        return Convert.ToHexStringLower(digest)[..Characters];
    }

    private static IEnumerable<string> ModNames(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory)) return [];

        return ModScanner.Scan(modsDirectory)
            .Where(mod => mod.Enabled)
            .Select(mod => Path.GetFileName(mod.Path));
    }
}
