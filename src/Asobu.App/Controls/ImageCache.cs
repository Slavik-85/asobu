using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Asobu.App.Controls;

/// <summary>
/// Decodes pictures at most once and hands the same bitmap back afterwards. Instance banners are
/// often 4K screenshots, and the library shows every instance's icon at once, so re-decoding on
/// each bind would cost real memory and a visible hitch.
/// </summary>
public static class ImageCache
{
    private static readonly Dictionary<string, Bitmap?> Cache = new(StringComparer.Ordinal);

    /// <summary>
    /// A picture from the user's own disk. Keyed on the write time as well as the path, because
    /// replacing an instance's icon or banner reuses the same file name — a path-only key would
    /// keep handing back the picture that was just replaced.
    /// </summary>
    public static Bitmap? FromFile(string? path)
    {
        if (path is not { Length: > 0 }) return null;

        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            var key = $"{path}|{info.LastWriteTimeUtc.Ticks}";

            lock (Cache)
            {
                if (Cache.TryGetValue(key, out var cached)) return cached;

                var bitmap = new Bitmap(path);
                Cache[key] = bitmap;
                return bitmap;
            }
        }
        catch (Exception)
        {
            // An unreadable or undecodable image just means no picture; the page still works.
            return null;
        }
    }

    /// <summary>
    /// An image already fetched over the network, keyed by its URL. Mod logos repeat constantly
    /// across searches, and decoding each one again per keystroke would be pure waste.
    /// </summary>
    public static Bitmap? FromBytes(string key, byte[] bytes)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(key, out var cached)) return cached;

            Bitmap? bitmap = null;
            try
            {
                bitmap = new Bitmap(new MemoryStream(bytes));
            }
            catch (Exception)
            {
            }

            Cache[key] = bitmap;
            return bitmap;
        }
    }

    /// <summary>One of the images compiled into the application itself.</summary>
    public static Bitmap? FromResource(string uri)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(uri, out var cached)) return cached;

            Bitmap? bitmap = null;
            try
            {
                using var stream = AssetLoader.Open(new Uri(uri));
                bitmap = new Bitmap(stream);
            }
            catch (Exception)
            {
            }

            Cache[uri] = bitmap;
            return bitmap;
        }
    }
}

/// <summary>
/// Binds a file path straight to an Image.Source. Lets a plain data object expose "where the
/// picture is" without taking a dependency on Avalonia's imaging types.
/// </summary>
public sealed class ImagePathConverter : IValueConverter
{
    public static readonly ImagePathConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        ImageCache.FromFile(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
