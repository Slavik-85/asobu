using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;
using Avalonia.Media;

namespace Asobu.App.Controls;

/// <summary>
/// Paints the window's title bar to match the launcher rather than the desktop.
///
/// The bar is drawn by Windows, not by us, so it cannot be styled from XAML. Windows 11 will
/// however take a colour for it — three DWM attributes, one call each. That is the whole of it:
/// the alternative was extending the client area and drawing our own bar, which means owning the
/// drag region, the snap layouts, the maximise behaviour and the accessibility of three buttons
/// that already work.
///
/// Windows-only and version-gated. Windows 10 ignores the attributes, Linux never sees this, and
/// both are correct outcomes: a title bar in the system's colours is what every other window on
/// those desktops looks like.
/// </summary>
public static class TitleBarColour
{
    private const int CaptionColour = 35;   // DWMWA_CAPTION_COLOR
    private const int TextColour = 36;      // DWMWA_TEXT_COLOR
    private const int BorderColour = 34;    // DWMWA_BORDER_COLOR

    /// <summary>
    /// Matches the bar to the sidebar, and keeps matching it when the theme changes.
    ///
    /// The colour is read from the theme's own resources rather than written here twice, so the
    /// bar cannot drift from the sidebar it is meant to agree with — changing the palette
    /// changes both.
    /// </summary>
    public static void Follow(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;

        Apply(window);

        // The handle does not exist before the window is opened, and the theme can change under
        // a running window when the desktop does.
        window.Opened += (_, _) => Apply(window);
        window.ActualThemeVariantChanged += (_, _) => Apply(window);
    }

    /// <summary>
    /// The guard lives here rather than only in Follow because the handlers above are separate
    /// call sites, and an analyser reading one at a time cannot see that they were only ever
    /// attached on Windows.
    /// </summary>
    private static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindows()) return;
        if (window.TryGetPlatformHandle()?.Handle is not { } handle || handle == IntPtr.Zero) return;

        // Windows 10 has no such attribute and returns a failure, which is fine — it simply
        // keeps the bar it would have had.
        Set(handle, CaptionColour, Resource(window, "SurfaceAlt"));
        Set(handle, BorderColour, Resource(window, "Border"));
        Set(handle, TextColour, Resource(window, "Text"));
    }

    [SupportedOSPlatform("windows")]
    private static void Set(IntPtr window, int attribute, uint? colour)
    {
        if (colour is not { } value) return;

        try
        {
            _ = DwmSetWindowAttribute(window, attribute, ref value, sizeof(uint));
        }
        catch (DllNotFoundException)
        {
            // No dwmapi. Nothing to do about it and nothing worth saying.
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    /// <summary>
    /// One of the theme's brushes as the COLORREF the DWM wants, which is 0x00BBGGRR — the
    /// reverse of how everybody writes a colour, and the reason to convert in one place.
    /// </summary>
    private static uint? Resource(Window window, string key)
    {
        if (!window.TryFindResource(key, window.ActualThemeVariant, out var found)) return null;
        if (found is not SolidColorBrush { Color: var colour }) return null;

        return (uint)(colour.R | (colour.G << 8) | (colour.B << 16));
    }

    // DllImport rather than the newer LibraryImport: that one is a source generator needing
    // AllowUnsafeBlocks, and switching the whole project to unsafe for a single three-argument
    // call into dwmapi is a poor trade.
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref uint value, int size);
}
