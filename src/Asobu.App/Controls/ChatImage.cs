using System;
using System.IO;
using SkiaSharp;

namespace Asobu.App.Controls;

/// <summary>
/// Turns whatever somebody picked into something worth sending.
///
/// Every picture is re-encoded before it goes anywhere, and that is not a nicety. The server
/// holds messages in memory and stores none of them, so the ceiling on what it can hold is a
/// ceiling on the size of what arrives — a phone photo straight off disk is eight megabytes, and
/// a handful of those waiting for one offline friend is the whole budget. Shrinking here is what
/// lets the promise upstream stay true.
///
/// Re-encoding has a second effect worth having: a fresh JPEG carries none of the original's
/// metadata, so the GPS tag on a phone screenshot does not travel with it.
/// </summary>
public static class ChatImage
{
    /// <summary>
    /// The longest side after shrinking. Big enough to read a screenshot of a crash or a mod
    /// list, small enough that the result is measured in hundreds of kilobytes.
    /// </summary>
    private const int MaxSide = 1280;

    /// <summary>
    /// What a message may weigh once encoded. Chosen against the server's per-recipient ceiling:
    /// a few of these can be waiting for somebody without crowding out anything else.
    /// </summary>
    public const int MaxBytes = 400 * 1024;

    /// <summary>Quality tried in turn until one fits. 80 is indistinguishable for a screenshot.</summary>
    private static readonly int[] Qualities = [80, 65, 50, 35];

    /// <summary>
    /// Reads a file and gives back a JPEG small enough to send, or null when it is not a picture
    /// or cannot be made to fit.
    /// </summary>
    public static byte[]? Prepare(string path)
    {
        try
        {
            using var source = SKBitmap.Decode(path);
            if (source is null) return null;

            using var sized = Resize(source);

            foreach (var quality in Qualities)
            {
                using var image = SKImage.FromBitmap(sized);
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);

                if (data is null) return null;
                if (data.Size <= MaxBytes) return data.ToArray();
            }

            // Still too big at the lowest quality worth using. Something enormous and
            // photographic; better to say so than to send a smear.
            return null;
        }
        catch (Exception)
        {
            // Not an image, a file that vanished, or something Skia would not decode.
            return null;
        }
    }

    /// <summary>
    /// The picture at no more than <see cref="MaxSide"/> on its longest edge, keeping its shape.
    /// Returned as-is when it is already small enough, so a pixel-art screenshot is not put
    /// through a resampler that would soften it for nothing.
    /// </summary>
    private static SKBitmap Resize(SKBitmap source)
    {
        var longest = Math.Max(source.Width, source.Height);
        if (longest <= MaxSide) return source.Copy();

        var scale = (float)MaxSide / longest;
        var width = Math.Max(1, (int)(source.Width * scale));
        var height = Math.Max(1, (int)(source.Height * scale));

        return source.Resize(new SKImageInfo(width, height), new SKSamplingOptions(SKCubicResampler.Mitchell))
               ?? source.Copy();
    }
}
