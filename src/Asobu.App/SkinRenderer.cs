using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Asobu.Core.Skins;
using SkiaSharp;

namespace Asobu.App;

/// <summary>
/// Draws a skin onto the player model.
///
/// Software rendered, and it can afford to be. The player is six boxes and a skin is 64×64, so
/// the whole model is a few thousand texture pixels — each one drawn as its own little
/// four-cornered patch, sorted back to front. That is far less work than it sounds, and it buys
/// something a 3D toolkit would not: no new dependency, no GPU, and it runs the same everywhere
/// Asobu does.
/// </summary>
/// <summary>Which pieces of the figure to draw. Drawing one is easier with the rest out of the way.</summary>
[Flags]
public enum SkinParts
{
    None = 0,
    Head = 1,
    Body = 2,
    Arms = 4,
    Legs = 8,
    All = Head | Body | Arms | Legs,
}

public static class SkinRenderer
{
    /// <summary>
    /// One box of the model. Sizes are in skin pixels, which is also the unit the model is in —
    /// a head really is eight of its own texture pixels wide.
    /// </summary>
    private readonly record struct Box(
        float X, float Y, float Z, int W, int H, int D, int U, int V, bool Overlay, SkinParts Part,
        bool Mirror = false);

    /// <summary>
    /// How much bigger an outer layer is drawn than the body underneath. Enough that it never
    /// fights the body for the same pixel, small enough to still look like clothing.
    /// </summary>
    private const float OverlaySwell = 0.5f;

    /// <summary>The figure is 32 pixels tall; the rest is air above and below it.</summary>
    private const float ModelHeight = 40f;

    /// <summary>
    /// The model, as the game lays it out. Origin is between the feet with y upwards, so the
    /// figure stands on zero and the head is the highest thing.
    ///
    /// The texture coordinates are the standard unwrap every skin has followed since 1.8. A
    /// 64×32 skin from before then has no left arm or left leg of its own, so those mirror the
    /// right — which is exactly what the game does with them.
    /// </summary>
    private static IEnumerable<Box> Model(SkinModel model, bool legacy, bool overlay, SkinParts parts)
    {
        var arm = model == SkinModel.Slim ? 3 : 4;

        foreach (var box in All())
            if (parts.HasFlag(box.Part))
                yield return box;

        yield break;

        IEnumerable<Box> All()
        {
            yield return new Box(-4, 24, -4, 8, 8, 8, 0, 0, false, SkinParts.Head);
            yield return new Box(-4, 12, -2, 8, 12, 4, 16, 16, false, SkinParts.Body);

            // The player's right arm, which is on the viewer's left when it faces them.
            yield return new Box(4, 12, -2, arm, 12, 4, 40, 16, false, SkinParts.Arms);
            yield return new Box(-4 - arm, 12, -2, arm, 12, 4, legacy ? 40 : 32, legacy ? 16 : 48, false, SkinParts.Arms, true);

            yield return new Box(0, 0, -2, 4, 12, 4, 0, 16, false, SkinParts.Legs);
            yield return new Box(-4, 0, -2, 4, 12, 4, legacy ? 0 : 16, legacy ? 16 : 48, false, SkinParts.Legs, true);

            if (!overlay) yield break;

            yield return new Box(-4, 24, -4, 8, 8, 8, 32, 0, true, SkinParts.Head);

            // Everything but the hat arrived with the 64×64 layout; an old skin simply has no
            // pixels there, and drawing them would sample whatever else lives at those
            // coordinates.
            if (legacy) yield break;

            yield return new Box(-4, 12, -2, 8, 12, 4, 16, 32, true, SkinParts.Body);
            yield return new Box(4, 12, -2, arm, 12, 4, 40, 32, true, SkinParts.Arms);
            yield return new Box(-4 - arm, 12, -2, arm, 12, 4, 48, 48, true, SkinParts.Arms, true);
            yield return new Box(0, 0, -2, 4, 12, 4, 0, 32, true, SkinParts.Legs);
            yield return new Box(-4, 0, -2, 4, 12, 4, 0, 48, true, SkinParts.Legs, true);
        }
    }

    /// <summary>One texture pixel, already flattened to the screen and ready to be filled.</summary>
    private readonly record struct Patch(float[] Xs, float[] Ys, float Depth, uint Colour, int Texel);

    public static WriteableBitmap? Render(
        byte[]? png, SkinModel model, double yaw, double pitch, int width, int height,
        bool overlay = true, SkinParts parts = SkinParts.All)
    {
        if (png is null || png.Length == 0 || width <= 0 || height <= 0) return null;

        using var skin = SKBitmap.Decode(png);

        return skin is null || skin.Width < 64
            ? null
            : Render(skin, model, yaw, pitch, width, height, overlay, parts);
    }

    public static WriteableBitmap Render(
        SKBitmap skin, SkinModel model, double yaw, double pitch, int width, int height,
        bool overlay = true, SkinParts parts = SkinParts.All)
    {
        var patches = new List<Patch>(4096);
        Collect(skin, model, yaw, pitch, width, height, overlay, parts, false, patches);

        patches.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        return ToBitmap(Paint(patches, width, height, null), width, height);
    }

    /// <summary>
    /// Which texture pixel is under each pixel of the figure, so the model itself can be drawn on.
    ///
    /// Rendered exactly as the picture is, but with the transparency ignored and only one layer
    /// present. Both matter: an outer layer is mostly empty, and painting on it means putting
    /// pixels where there are none yet — a pass that skipped them would let somebody draw a hood
    /// only where a hood already was.
    /// </summary>
    public static int[] PickMap(
        byte[]? png, SkinModel model, double yaw, double pitch, int width, int height,
        bool outerLayer, SkinParts parts = SkinParts.All)
    {
        var map = new int[Math.Max(0, width * height)];
        Array.Fill(map, -1);

        if (png is null || png.Length == 0 || width <= 0 || height <= 0) return map;

        using var skin = SKBitmap.Decode(png);
        if (skin is null || skin.Width < 64) return map;

        var patches = new List<Patch>(4096);
        Collect(skin, model, yaw, pitch, width, height, outerLayer, parts, true, patches);

        // Only the layer being drawn on, so a hat never stands in front of the head beneath it.
        patches.RemoveAll(patch => Overlays(patch.Texel) != outerLayer);
        patches.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        Paint(patches, width, height, map);

        return map;
    }

    /// <summary>Whether a texture pixel belongs to the outer layer, by where it sits on the sheet.</summary>
    private static bool Overlays(int texel)
    {
        int x = texel % 64, y = texel / 64;

        return (y < 16 && x >= 32)          // hat
            || (y is >= 32 and < 48)        // jacket, sleeve, trouser
            || (y >= 48 && x < 16)          // left trouser
            || (y >= 48 && x >= 48);        // left sleeve
    }

    private static void Collect(
        SKBitmap skin, SkinModel model, double yaw, double pitch,
        int width, int height, bool overlay, SkinParts parts, bool everyPixel, List<Patch> patches)
    {
        var legacy = skin.Height < 64;

        var sinYaw = (float)Math.Sin(yaw);
        var cosYaw = (float)Math.Cos(yaw);
        var sinPitch = (float)Math.Sin(pitch);
        var cosPitch = (float)Math.Cos(pitch);

        var scale = height / ModelHeight;
        var centreX = width / 2f;
        var centreY = height / 2f + 16 * scale;

        foreach (var box in Model(model, legacy, overlay, parts))
        {
            // An outer layer that is opaque everywhere is not a hat, it is filler. Old skins were
            // saved without an alpha channel at all, so the whole overlay half of the sheet reads
            // back as solid black — and drawn as given, that is a box over the face.
            if (box.Overlay && !everyPixel && !CarriesTransparency(skin, box)) continue;

            var swell = box.Overlay ? OverlaySwell : 0f;
            float x0 = box.X - swell, x1 = box.X + box.W + swell;
            float y0 = box.Y - swell, y1 = box.Y + box.H + swell;
            float z0 = box.Z - swell, z1 = box.Z + box.D + swell;

            int w = box.W, h = box.H, d = box.D;
            int u = box.U, v = box.V;

            // The six faces, each given as the corner its texture starts at and the two spans
            // that walk across it. Written out rather than derived, because the unwrap is a fixed
            // convention rather than a rule — and because deriving the step count from the
            // coordinates breaks the moment an outer layer swells the box.
            //
            // A mirrored limb reads its texture the other way along, and its two side faces trade
            // places — which is what the game does to the left arm and left leg, and without it
            // both arms wrap identically and the left one looks turned around.
            var near = box.Mirror ? u + d + w : u;
            var far = box.Mirror ? u : u + d + w;

            Face(u + d, v + d, w, h, x1, y1, z1, x0 - x1, 0, 0, 0, y0 - y1, 0);
            Face(u + d * 2 + w, v + d, w, h, x0, y1, z0, x1 - x0, 0, 0, 0, y0 - y1, 0);
            Face(near, v + d, d, h, x1, y1, z0, 0, 0, z1 - z0, 0, y0 - y1, 0);
            Face(far, v + d, d, h, x0, y1, z1, 0, 0, z0 - z1, 0, y0 - y1, 0);
            Face(u + d, v, w, d, x1, y1, z0, x0 - x1, 0, 0, 0, 0, z1 - z0);
            Face(u + d + w, v, w, d, x1, y0, z1, x0 - x1, 0, 0, 0, 0, z0 - z1);

            continue;

            void Face(
                int tu, int tv, int across, int down,
                float ox, float oy, float oz,
                float ux, float uy, float uz,
                float vx, float vy, float vz)
            {
                for (var b = 0; b < down; b++)
                for (var a = 0; a < across; a++)
                {
                    var colour = Sample(skin, box.Mirror ? tu + across - 1 - a : tu + a, tv + b);

                    // Nothing to draw, and for an outer layer that is most of it — the parts of
                    // the sheet a skin left blank are how it says "no hat here".
                    if (colour >> 24 == 0 && !everyPixel) continue;

                    var xs = new float[4];
                    var ys = new float[4];
                    var depth = 0f;

                    // Round the patch: (a,b), (a+1,b), (a+1,b+1), (a,b+1).
                    for (var corner = 0; corner < 4; corner++)
                    {
                        var ca = (a + (corner is 1 or 2 ? 1 : 0)) / (float)across;
                        var cb = (b + (corner is 2 or 3 ? 1 : 0)) / (float)down;

                        var px = ox + ux * ca + vx * cb;
                        var py = oy + uy * ca + vy * cb;
                        var pz = oz + uz * ca + vz * cb;

                        var rx = px * cosYaw - pz * sinYaw;
                        var rz = px * sinYaw + pz * cosYaw;
                        var ry = py * cosPitch - rz * sinPitch;

                        xs[corner] = centreX + rx * scale;
                        ys[corner] = centreY - ry * scale;
                        depth += py * sinPitch + rz * cosPitch;
                    }

                    patches.Add(new Patch(
                        xs, ys, depth / 4f, colour,
                        (tv + b) * 64 + (box.Mirror ? tu + across - 1 - a : tu + a)));
                }
            }
        }

        // Back to front. A depth buffer would be the other way to do it and would cost a buffer
        // and a test per pixel; there are only a few thousand of these and they never intersect.
    }

    /// <summary>
    /// The same render as raw BGRA bytes, without a bitmap around it. Exists so the geometry can
    /// be checked without a display: WriteableBitmap needs a live rendering platform, and mirrored
    /// faces or upside-down texture coordinates are exactly the sort of thing that has to be
    /// looked at rather than reasoned about.
    /// </summary>
    public static byte[] Pixels(
        SKBitmap skin, SkinModel model, double yaw, double pitch, int width, int height, bool overlay = true)
    {
        var bitmapless = new List<Patch>();
        Collect(skin, model, yaw, pitch, width, height, overlay, SkinParts.All, false, bitmapless);
        bitmapless.Sort((a, b) => a.Depth.CompareTo(b.Depth));

        return Paint(bitmapless, width, height, null);
    }

    /// <summary>
    /// Whether an overlay box says anywhere that it is see-through. Its unwrap is one block of
    /// the sheet — two depths and two widths across, a depth and a height down — so the whole of
    /// it can be asked at once.
    /// </summary>
    private static bool CarriesTransparency(SKBitmap skin, Box box)
    {
        for (var y = box.V; y < box.V + box.D + box.H; y++)
        for (var x = box.U; x < box.U + (box.D + box.W) * 2; x++)
            if (Sample(skin, x, y) >> 24 < 255)
                return true;

        return false;
    }

    private static uint Sample(SKBitmap skin, int x, int y)
    {
        if (x < 0 || y < 0 || x >= skin.Width || y >= skin.Height) return 0;

        var c = skin.GetPixel(x, y);
        return ((uint)c.Alpha << 24) | ((uint)c.Red << 16) | ((uint)c.Green << 8) | c.Blue;
    }

    /// <summary>
    /// Paints the sorted patches into a bitmap.
    ///
    /// Written into a byte array and copied in one go rather than poked through a pointer, so
    /// none of this needs unsafe code — which the project does not turn on, and does not need to
    /// for a few thousand small quads.
    /// </summary>
    private static byte[] Paint(List<Patch> patches, int width, int height, int[]? pick)
    {
        var pixels = new byte[width * height * 4];

        foreach (var patch in patches)
        {
            var minX = Math.Max(0, (int)Math.Floor(Min(patch.Xs)));
            var maxX = Math.Min(width - 1, (int)Math.Ceiling(Max(patch.Xs)));
            var minY = Math.Max(0, (int)Math.Floor(Min(patch.Ys)));
            var maxY = Math.Min(height - 1, (int)Math.Ceiling(Max(patch.Ys)));

            var a = (byte)(patch.Colour >> 24);
            var r = (byte)(patch.Colour >> 16);
            var g = (byte)(patch.Colour >> 8);
            var b = (byte)patch.Colour;

            for (var y = minY; y <= maxY; y++)
            for (var x = minX; x <= maxX; x++)
            {
                if (!Inside(patch.Xs, patch.Ys, x + 0.5f, y + 0.5f)) continue;

                var at = (y * width + x) * 4;

                // Back to front, so the last one to claim a pixel is the one nearest the eye.
                if (pick is not null) pick[y * width + x] = patch.Texel;

                if (a == 255)
                {
                    pixels[at] = b;
                    pixels[at + 1] = g;
                    pixels[at + 2] = r;
                    pixels[at + 3] = 255;
                    continue;
                }

                // A part-transparent skin pixel over whatever is already there.
                var over = a / 255f;
                pixels[at] = (byte)(b * over + pixels[at] * (1 - over));
                pixels[at + 1] = (byte)(g * over + pixels[at + 1] * (1 - over));
                pixels[at + 2] = (byte)(r * over + pixels[at + 2] * (1 - over));
                pixels[at + 3] = (byte)(a + pixels[at + 3] * (1 - over));
            }
        }

        return pixels;
    }

    private static WriteableBitmap ToBitmap(byte[] pixels, int width, int height)
    {
        var bitmap = new WriteableBitmap(
            new PixelSize(width, height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);

        using (var locked = bitmap.Lock()) Marshal.Copy(pixels, 0, locked.Address, pixels.Length);

        return bitmap;
    }

    /// <summary>
    /// Whether a point is in the quad. The corners are always given in order round it, so a point
    /// inside is on the same side of all four edges — and every quad here is convex, being one
    /// flat texture pixel.
    /// </summary>
    private static bool Inside(float[] xs, float[] ys, float x, float y)
    {
        var sign = 0;

        for (var i = 0; i < 4; i++)
        {
            var j = (i + 1) % 4;
            var cross = (xs[j] - xs[i]) * (y - ys[i]) - (ys[j] - ys[i]) * (x - xs[i]);

            if (cross == 0) continue;

            var side = cross > 0 ? 1 : -1;

            if (sign == 0) sign = side;
            else if (sign != side) return false;
        }

        return true;
    }

    private static float Min(float[] v) => Math.Min(Math.Min(v[0], v[1]), Math.Min(v[2], v[3]));
    private static float Max(float[] v) => Math.Max(Math.Max(v[0], v[1]), Math.Max(v[2], v[3]));
}
