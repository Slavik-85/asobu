using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Asobu.App.Controls;

/// <summary>
/// The frames of an animated picture, decoded once and held ready to play.
///
/// Mod galleries are full of GIFs — a mechanic is far easier to show moving than to describe —
/// and Avalonia's own Bitmap decodes only the first frame of one, so a gallery of them reads as
/// a gallery of stills. Skia is already underneath Avalonia and can walk the frames, so this
/// borrows it rather than adding an imaging library.
/// </summary>
public sealed class AnimatedFrames
{
    /// <summary>
    /// Wider than this and it is left as a still: past a point the frames cost more to hold than
    /// the animation is worth, whatever they are scaled to afterwards.
    /// </summary>
    public const int SourceLimit = 1400;

    /// <summary>
    /// Frames are scaled to the width they will be shown at. Modrinth serves a gallery GIF as
    /// itself rather than as a small preview, so a thumbnail is often an 854-wide animation in a
    /// 320-wide column — held at source size that is eighty megabytes to show a postage stamp.
    /// </summary>
    public const int ThumbnailWidth = 340;

    public const int ViewerWidth = 900;

    /// <summary>
    /// What a single animation may occupy once decoded. Measured against real gallery GIFs: a
    /// 700x228 of 49 frames is 30MB and plays; an 877x493 of 173 frames is 285MB and does not.
    /// </summary>
    private const long Budget = 96L * 1024 * 1024;

    /// <summary>A GIF that never stops is usually a mistake in the file, not an intention.</summary>
    private const int MaxFrames = 240;

    /// <summary>Some frames declare no delay at all; browsers land on about this.</summary>
    private static readonly TimeSpan DefaultDelay = TimeSpan.FromMilliseconds(100);

    private AnimatedFrames(IReadOnlyList<Bitmap> pictures, IReadOnlyList<TimeSpan> delays)
    {
        Pictures = pictures;
        Delays = delays;
    }

    public IReadOnlyList<Bitmap> Pictures { get; }
    public IReadOnlyList<TimeSpan> Delays { get; }

    /// <summary>
    /// Null when the picture is not animated, is bigger than <paramref name="maxWidth"/>, or
    /// cannot be read — in every one of those cases the caller shows it as an ordinary still,
    /// which is what it looked like before any of this existed.
    /// </summary>
    public static AnimatedFrames? Decode(byte[] bytes, int showAtWidth)
    {
        try
        {
            using var codec = SKCodec.Create(new SKMemoryStream(bytes));

            if (codec is null || codec.FrameCount <= 1) return null;
            if (codec.Info.Width > SourceLimit || codec.Info.Width <= 0) return null;

            return Walk(codec, showAtWidth);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static AnimatedFrames? Walk(SKCodec codec, int showAtWidth)
    {
        var info = new SKImageInfo(codec.Info.Width, codec.Info.Height,
            SKColorType.Bgra8888, SKAlphaType.Premul);

        // Only ever down. Blowing a small animation up to fill a column would cost memory to
        // make it blurrier, and the Image control scales it for nothing anyway.
        var scale = Math.Min(1.0, showAtWidth / (double)info.Width);

        var kept = new SKImageInfo(
            Math.Max(1, (int)Math.Round(info.Width * scale)),
            Math.Max(1, (int)Math.Round(info.Height * scale)),
            SKColorType.Bgra8888,
            SKAlphaType.Premul);

        var frameSize = (long)kept.Width * kept.Height * 4;
        if (frameSize <= 0) return null;

        var count = codec.FrameCount;

        // All of it or none of it. Keeping the first forty frames of a hundred-frame animation
        // does not save it — it plays most of the way through and then jumps, which reads as a
        // fault rather than as a still would.
        if (count > MaxFrames || frameSize * count > Budget) return null;

        var frameInfo = codec.FrameInfo;
        var pictures = new List<Bitmap>(count);
        var delays = new List<TimeSpan>(count);

        // One buffer for the whole walk: a GIF frame is usually a patch drawn over the one
        // before it, so the previous frame has to still be sitting there when the next is
        // decoded. Each is copied out to an Avalonia bitmap before the buffer moves on.
        using var canvas = new SKBitmap(info);

        for (var i = 0; i < count; i++)
        {
            var required = i < frameInfo.Length ? frameInfo[i].RequiredFrame : -1;

            // Nothing to build on: start from a clean slate rather than whatever was there.
            if (required < 0) canvas.Erase(SKColors.Transparent);

            var options = required == i - 1 && i > 0
                ? new SKCodecOptions(i, required)
                : new SKCodecOptions(i);

            var result = codec.GetPixels(info, canvas.GetPixels(), options);

            // A truncated file still has usable frames up to the point it stops.
            if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) break;

            pictures.Add(Shrink(canvas, kept));

            var duration = i < frameInfo.Length ? frameInfo[i].Duration : 0;
            delays.Add(duration > 0 ? TimeSpan.FromMilliseconds(duration) : DefaultDelay);
        }

        return pictures.Count > 1 ? new AnimatedFrames(pictures, delays) : null;
    }

    /// <summary>
    /// Skia's working buffer into an Avalonia bitmap at the size it will be shown. Always a copy,
    /// because the buffer is reused for the next frame the moment this returns.
    /// </summary>
    private static Bitmap Shrink(SKBitmap source, SKImageInfo kept)
    {
        if (kept.Width == source.Width && kept.Height == source.Height) return Copy(source, kept);

        using var scaled = new SKBitmap(kept);
        using var canvas = new SKCanvas(scaled);
        using var image = SKImage.FromBitmap(source);

        canvas.DrawImage(image, new SKRect(0, 0, kept.Width, kept.Height));

        return Copy(scaled, kept);
    }

    private static Bitmap Copy(SKBitmap source, SKImageInfo info) =>
        new(PixelFormat.Bgra8888,
            AlphaFormat.Premul,
            source.GetPixels(),
            new PixelSize(info.Width, info.Height),
            new Vector(96, 96),
            source.RowBytes);
}
