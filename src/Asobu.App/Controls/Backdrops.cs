using System;
using System.Collections.Generic;
using System.IO;
using Asobu.Core;
using Asobu.Core.Instances;
using Avalonia.Media.Imaging;

namespace Asobu.App.Controls;

/// <summary>
/// The scenery shown behind an instance's hero section. One is chosen per instance and stays
/// put for that instance, so reopening the same pack doesn't reshuffle the artwork underneath
/// you — it's picked from the instance id rather than at random each time.
/// </summary>
public static class Backdrops
{
    private static readonly string[] Files =
    [
        "prettypicture1.jpeg",
        "prettypicture2.webp",
        "prettypicture3.jpg",
        "prettypicture4.png",
        "prettypicture5.jpg",
        "prettypicture6.png",
    ];

    /// <summary>The bundled scenery, for the banner picker to show as thumbnails.</summary>
    public static IReadOnlyList<string> BuiltIn => Files;

    /// <summary>
    /// The picture behind an instance's hero: whichever one it was given, falling back to the
    /// id-picked scenery for instances that were never customised.
    /// </summary>
    public static Bitmap? For(Instance? instance, AsobuPaths paths)
    {
        if (instance is null) return null;

        if (instance.Banner is { Length: > 0 } banner)
        {
            if (banner.StartsWith(Instance.BuiltInBannerPrefix, StringComparison.Ordinal))
                return LoadBuiltIn(banner[Instance.BuiltInBannerPrefix.Length..]);

            if (banner.StartsWith(Instance.CustomBannerPrefix, StringComparison.Ordinal))
                return ImageCache.FromFile(Path.Combine(
                    paths.InstanceDir(instance.Folder), banner[Instance.CustomBannerPrefix.Length..]));
        }

        return ForInstance(instance.Id);
    }

    public static Bitmap? ForInstance(string? instanceId) => Any(instanceId);

    /// <summary>
    /// Some picture, for anything that has none of its own. Picked from the key rather than at
    /// random so the same thing keeps the same scenery — and so two mods in a row on the
    /// Explore banner do not land on the same one.
    /// </summary>
    public static Bitmap? Any(string? key)
    {
        if (Files.Length == 0) return null;

        var index = key is { Length: > 0 }
            ? (int)(Stable(key) % (uint)Files.Length)
            : Random.Shared.Next(Files.Length);

        return LoadBuiltIn(Files[index]);
    }

    public static Bitmap? LoadBuiltIn(string file) =>
        ImageCache.FromResource($"avares://Asobu.App/Assets/Backdrops/{file}");

    /// <summary>
    /// FNV-1a. Deliberately not string.GetHashCode, which is randomised per process and would
    /// hand the same instance a different picture on every launch.
    /// </summary>
    private static uint Stable(string value)
    {
        var hash = 2166136261u;
        foreach (var c in value)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return hash;
    }
}
