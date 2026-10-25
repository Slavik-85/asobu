using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Asobu.Core;
using SkiaSharp;

namespace Asobu.App;

/// <summary>A named theme: where the accent sits on the colour wheel, and how tinted the greys are.</summary>
public sealed record ThemePreset(string Name, double Hue, double Tint)
{
    public IBrush Swatch => new SolidColorBrush(Theming.SwatchColour(Hue));
}

/// <summary>
/// Light, dark, and the colour of the place.
///
/// A theme is not a second palette. The Sakura palette in Asobu.axaml was tuned by hand for
/// contrast in both variants, so every other theme is that same palette turned round the colour
/// wheel: accents move to the new hue keeping their lightness, and the greys take the hue at
/// whatever strength the tint asks for. Contrast survives because lightness never moves. The
/// brushes are recoloured where they live, so everything already holding one repaints at once.
/// </summary>
public static class Theming
{
    public const string Custom = "Custom";

    public static readonly IReadOnlyList<ThemePreset> Presets =
    [
        new("Sakura", SakuraHue, 1),
        new("Ocean", 205, 1),
        new("Forest", 150, 1),
        new("Lavender", 265, 1),
        new("Amber", 28, 1),
        new("Slate", 215, 0.25),
    ];

    /// <summary>Where Sakura's accent, #C44370, sits. Every shift is measured from here.</summary>
    private const double SakuraHue = 339;

    /// <summary>Colours that mean something — warnings, success — and stay put whatever the theme.</summary>
    private static readonly HashSet<string> Fixed = ["LogInfo", "LogWarn", "LogError", "LogChatter", "Mint", "Pressed"];

    /// <summary>The accent family, which moves at full strength. Everything else is a tinted grey.</summary>
    private static bool IsAccent(string key) =>
        key.Contains("Accent", StringComparison.Ordinal)
        || key is "TextControlBorderBrushFocused" or "TextControlSelectionHighlightColor"
            or "ComboBoxDropDownGlyphForegroundFocused" or "ComboBoxDropDownGlyphForegroundFocusedPressed";

    /// <summary>Sakura's own colours, read once, so every theme is worked out from them rather than from the last one.</summary>
    private static Dictionary<(ThemeVariant, string), Color[]>? _original;

    public static Color SwatchColour(double hue) => HslColor.FromAhsl(1, hue, 0.55, 0.55).ToRgb();

    public static void Apply(LauncherSettings settings)
    {
        if (Application.Current is not { } app) return;

        app.RequestedThemeVariant = settings.ThemeMode switch
        {
            "Light" => ThemeVariant.Light,
            "Dark" => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };

        var (hue, tint) = settings.Theme == Custom
            ? (settings.ThemeHue, settings.ThemeTint)
            : Presets.FirstOrDefault(p => p.Name == settings.Theme) is { } preset
                ? (preset.Hue, preset.Tint)
                : (SakuraHue, 1);

        var palettes = Palettes(app).ToList();
        _original ??= Snapshot(palettes);

        var delta = hue - SakuraHue;
        var untouched = Math.Abs(delta) < 0.5 && Math.Abs(tint - 1) < 0.01;

        ApplyLogo(app, Math.Abs(delta) < 0.5 ? null : delta);

        foreach (var (variant, palette) in palettes)
        {
            Color Recolour(string key, Color colour) =>
                untouched || Fixed.Contains(key) ? colour
                : Shift(colour, delta, IsAccent(key) ? 1 : tint);

            // Text on the accent flips to dark when the accent comes out light — yellow at
            // Sakura's lightness is far brighter than pink, and white on it is unreadable.
            Color? onAccent = untouched || !_original.TryGetValue((variant, "Accent"), out var accent)
                ? null
                : Luminance(Recolour("Accent", accent[0])) > 0.4 ? Color.Parse("#1A1418") : Colors.White;

            foreach (var key in palette.Keys.OfType<string>().ToList())
            {
                if (!_original.TryGetValue((variant, key), out var colours)) continue;

                switch (palette[key])
                {
                    case SolidColorBrush brush:
                        brush.Color = key == "OnAccent" && onAccent is { } on ? on : Recolour(key, colours[0]);
                        break;
                    case LinearGradientBrush gradient:
                        for (var i = 0; i < gradient.GradientStops.Count && i < colours.Length; i++)
                            gradient.GradientStops[i].Color = Recolour(key, colours[i]);
                        break;
                    case Color:
                        palette[key] = Recolour(key, colours[0]);
                        break;
                }
            }
        }

        // The title bar is Windows' to draw, from colours it was handed once.
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            Controls.TitleBarColour.Refresh(window);
    }

    /// <summary>The controller artwork as shipped, read once and recoloured from each time.</summary>
    private static SKBitmap? _logo;

    /// <summary>
    /// The controller, turned round the wheel with the accent: the "Logo" resource the sidebar and
    /// the tray note draw, and the window's and tray's icon. Null puts the artwork back as it is.
    /// </summary>
    private static void ApplyLogo(Application app, double? delta)
    {
        WindowIcon icon;

        if (delta is not { } shift)
        {
            using (var asset = AssetLoader.Open(LogoUri)) app.Resources["Logo"] = new Bitmap(asset);
            icon = App.TrayImage();
        }
        else
        {
            using (var asset = AssetLoader.Open(LogoUri)) _logo ??= SKBitmap.Decode(asset);

            using var turned = _logo.Copy();
            var pixels = turned.Pixels;

            for (var i = 0; i < pixels.Length; i++)
            {
                var p = pixels[i];
                if (p.Alpha == 0) continue;

                var c = Shift(Color.FromArgb(p.Alpha, p.Red, p.Green, p.Blue), shift, 1);
                pixels[i] = new SKColor(c.R, c.G, c.B, c.A);
            }

            turned.Pixels = pixels;

            using var png = turned.Encode(SKEncodedImageFormat.Png, 100);
            var bitmap = new Bitmap(png.AsStream());

            app.Resources["Logo"] = bitmap;
            icon = new WindowIcon(bitmap);
        }

        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
            window.Icon = icon;

        foreach (var tray in TrayIcon.GetIcons(app) ?? []) tray.Icon = icon;
    }

    private static readonly Uri LogoUri = new("avares://Asobu.App/Assets/asobu.png");

    /// <summary>
    /// Asobu.axaml's Light and Dark dictionaries, which is where every colour the app uses is
    /// defined. The XAML compiler turns App.axaml's StyleInclude into the Styles object itself, so
    /// at runtime that is what is in the list — looking only for a StyleInclude found nothing.
    /// </summary>
    private static IEnumerable<(ThemeVariant, ResourceDictionary)> Palettes(Application app)
    {
        foreach (var style in app.Styles)
        {
            var loaded = style is StyleInclude include ? include.Loaded : style;
            if (loaded is not Styles { Resources: ResourceDictionary resources } || loaded is Avalonia.Themes.Fluent.FluentTheme)
                continue;

            foreach (var (variant, palette) in resources.ThemeDictionaries)
                if (palette is ResourceDictionary dictionary)
                    yield return (variant, dictionary);
        }
    }

    private static Dictionary<(ThemeVariant, string), Color[]> Snapshot(
        IEnumerable<(ThemeVariant Variant, ResourceDictionary Palette)> palettes)
    {
        var original = new Dictionary<(ThemeVariant, string), Color[]>();

        foreach (var (variant, palette) in palettes)
        foreach (var key in palette.Keys.OfType<string>())
        {
            Color[]? colours = palette[key] switch
            {
                SolidColorBrush brush => [brush.Color],
                LinearGradientBrush gradient => [.. gradient.GradientStops.Select(stop => stop.Color)],
                Color colour => [colour],
                _ => null,
            };

            if (colours is not null) original[(variant, key)] = colours;
        }

        return original;
    }

    private static Color Shift(Color colour, double delta, double saturation)
    {
        var hsl = colour.ToHsl();

        return HslColor.FromAhsl(
            hsl.A, ((hsl.H + delta) % 360 + 360) % 360, Math.Clamp(hsl.S * saturation, 0, 1), hsl.L).ToRgb();
    }

    /// <summary>WCAG relative luminance, 0 for black and 1 for white.</summary>
    private static double Luminance(Color colour)
    {
        static double Channel(byte value)
        {
            var c = value / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(colour.R) + 0.7152 * Channel(colour.G) + 0.0722 * Channel(colour.B);
    }
}
