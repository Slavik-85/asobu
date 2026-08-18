using System;
using System.Collections.Generic;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

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

    // Decoding a 4K screenshot costs real memory, so each one is decoded at most once and shared.
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    public static Bitmap? ForInstance(string? instanceId)
    {
        if (Files.Length == 0) return null;

        var index = instanceId is { Length: > 0 }
            ? (int)(Stable(instanceId) % (uint)Files.Length)
            : Random.Shared.Next(Files.Length);

        return Load(Files[index]);
    }

    private static Bitmap? Load(string file)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(file, out var cached)) return cached;

            Bitmap? bitmap = null;
            try
            {
                var uri = new Uri($"avares://Asobu.App/Assets/Backdrops/{file}");
                using var stream = AssetLoader.Open(uri);
                bitmap = new Bitmap(stream);
            }
            catch (Exception)
            {
                // A missing or undecodable file just means no backdrop; the page still works.
            }

            Cache[file] = bitmap;
            return bitmap;
        }
    }

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
