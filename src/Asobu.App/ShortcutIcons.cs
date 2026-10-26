using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia.Threading;
using SkiaSharp;

namespace Asobu.App;

/// <summary>
/// Asobu's shortcuts on Windows — the desktop, the Start menu, pinned to the taskbar — in the
/// theme's colour. Windows draws those from the icon a shortcut names, never from the running
/// window, so the controller stayed pink on the desktop whatever the launcher looked like.
///
/// Each colour gets an .ico of its own in the install folder, because Explorer caches an icon by
/// its path and would go on showing the old one if a new one were written over the same file.
/// Sakura points the shortcuts back at the executable's own icon.
/// </summary>
internal static class ShortcutIcons
{
    /// <summary>
    /// Waits for the colour to settle. The theme is applied on every step of a slider being
    /// dragged, and rewriting shortcuts for each of those would be Explorer redrawing all the way.
    /// </summary>
    private static DispatcherTimer? _settle;

    private static (SKBitmap? Logo, string Tag) _pending;

    /// <summary>One write at a time: each deletes the icons the others made.</summary>
    private static readonly object Writing = new();

    /// <summary>Takes a copy of <paramref name="logo"/>, or null for the artwork as shipped.</summary>
    public static void Apply(SKBitmap? logo, double shift)
    {
        if (!OperatingSystem.IsWindows() || InstallRoot() is null) return;

        _pending.Logo?.Dispose();
        _pending = (logo?.Copy(), $"{Math.Round(shift):0}");

        _settle ??= new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.Background, (_, _) =>
        {
            _settle!.Stop();
            var (bitmap, tag) = _pending;
            _pending = default;

            // Shell objects want a single-threaded apartment, which the thread pool isn't.
            var worker = new Thread(() => Write(bitmap, tag)) { IsBackground = true };
            worker.SetApartmentState(ApartmentState.STA);
            worker.Start();
        });

        _settle.Stop();
        _settle.Start();
    }

    /// <summary>The Velopack install this runs from, or null for a build folder, which has no shortcuts.</summary>
    private static string? InstallRoot()
    {
        var exe = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        return Path.GetFileName(exe).Equals("current", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(exe) + Path.DirectorySeparatorChar
            : null;
    }

    private static void Write(SKBitmap? logo, string tag)
    {
        lock (Writing)
        try
        {
            var root = InstallRoot()!;
            string? icon = null;

            if (logo is not null)
            {
                icon = Path.Combine(root, $"theme-{tag}.ico");
                if (!File.Exists(icon)) File.WriteAllBytes(icon, Ico(logo));
            }

            Point(root, icon);

            foreach (var old in Directory.GetFiles(root, "theme-*.ico").Where(path => path != icon))
                File.Delete(old);
        }
        catch (Exception)
        {
            // Everything, because this is a thread of its own and anything escaping it ends the
            // app. The shortcuts keep the colour they had, which is all a failure here costs.
        }
        finally
        {
            logo?.Dispose();
        }
    }

    /// <summary>Points every shortcut to this install at <paramref name="icon"/>, or back at the executable.</summary>
    private static void Point(string root, string? icon)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar"),
        };

        // Missing where Windows Script Host has been switched off, and then there is nothing to do.
        if (Type.GetTypeFromProgID("WScript.Shell") is not { } type) return;

        dynamic shell = Activator.CreateInstance(type)!;
        var changed = false;

        try
        {
            foreach (var folder in folders.Where(Directory.Exists))
            foreach (var path in Directory.EnumerateFiles(folder, "*.lnk"))
            {
                var link = shell.CreateShortcut(path);
                if (link.TargetPath is not string target || !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    continue;

                // With the index, because that is how the shortcut reads it back: without it this
                // never matched, and every launch rewrote every shortcut.
                var wanted = (icon ?? target) + ",0";
                if (string.Equals((string)link.IconLocation, wanted, StringComparison.OrdinalIgnoreCase)) continue;

                link.IconLocation = wanted;
                link.Save();
                SHChangeNotify(UpdateItem, PathW, path, IntPtr.Zero);
                changed = true;
            }
        }
        finally
        {
            Marshal.FinalReleaseComObject(shell);
        }

        // The taskbar and Start menu hold icons of their own; this tells them to look again. Only
        // when something moved, since it makes every icon on the desktop redraw.
        if (changed) SHChangeNotify(AssocChanged, 0, null, IntPtr.Zero);
    }

    /// <summary>An .ico of PNG images, which Windows has read since Vista, at the sizes it asks for.</summary>
    private static byte[] Ico(SKBitmap logo)
    {
        int[] sizes = [16, 24, 32, 48, 64, 256];

        var images = sizes.Select(size =>
        {
            using var scaled = logo.Resize(new SKImageInfo(size, size), new SKSamplingOptions(SKCubicResampler.Mitchell));
            using var png = scaled.Encode(SKEncodedImageFormat.Png, 100);
            return png.ToArray();
        }).ToList();

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)images.Count);

        var offset = 6 + 16 * images.Count;

        for (var i = 0; i < images.Count; i++)
        {
            var side = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            writer.Write(side);
            writer.Write(side);
            writer.Write((short)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(images[i].Length);
            writer.Write(offset);
            offset += images[i].Length;
        }

        foreach (var image in images) writer.Write(image);

        writer.Flush();
        return stream.ToArray();
    }

    private const int UpdateItem = 0x2000;
    private const int AssocChanged = 0x08000000;
    private const uint PathW = 0x0005;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);
}
