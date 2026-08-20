using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace Asobu.App.Controls;

/// <summary>
/// Slides a full-page sheet up from below the window, and back down out of it.
///
/// This exists because the distance has to be the window's height, and a stylesheet cannot know
/// what that is. An Animation's key frames take plain values — no bindings — so a slide written
/// in XAML has to name a number, and any number is wrong at some window size: 460px is about a
/// screenful in a small window and lands halfway up a maximised one, where the page appears in
/// mid-air and then rises the rest of the way.
///
/// Measured at the moment it runs, off the window rather than off the page. The page has only
/// just been made visible when this fires and its own bounds may still be stale or zero, while
/// the window's are always current — and the window's height is what "below the screen" means
/// anyway.
/// </summary>
public static class PageSlide
{
    /// <summary>Matches SheetSlideMilliseconds in the view models; keep the two in step.</summary>
    private const int Milliseconds = 340;

    /// <summary>What to slide by when there is no window to measure, which should not happen.</summary>
    private const double Fallback = 460;

    public static readonly AttachedProperty<bool> IsOpenProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsOpen", typeof(PageSlide));

    /// <summary>
    /// Set while the page is on its way out, and kept set until the slide finishes — the page
    /// has to stay mounted to animate, so whoever sets this owns waiting the duration out.
    /// </summary>
    public static readonly AttachedProperty<bool> IsClosingProperty =
        AvaloniaProperty.RegisterAttached<Control, bool>("IsClosing", typeof(PageSlide));

    static PageSlide()
    {
        // Only the rising edge of each matters. Both are driven by view-model flags that go back
        // to false afterwards, and animating on the way down would play the slide twice.
        IsOpenProperty.Changed.AddClassHandler<Control>((page, e) =>
        {
            if (e.GetNewValue<bool>()) Run(page, rising: true);
        });

        IsClosingProperty.Changed.AddClassHandler<Control>((page, e) =>
        {
            if (e.GetNewValue<bool>()) Run(page, rising: false);
        });
    }

    public static void SetIsOpen(Control target, bool value) => target.SetValue(IsOpenProperty, value);

    public static bool GetIsOpen(Control target) => target.GetValue(IsOpenProperty);

    public static void SetIsClosing(Control target, bool value) => target.SetValue(IsClosingProperty, value);

    public static bool GetIsClosing(Control target) => target.GetValue(IsClosingProperty);

    private static void Run(Control page, bool rising)
    {
        var distance = TopLevel.GetTopLevel(page)?.ClientSize.Height is > 0 and var height
            ? height
            : page.Bounds.Height > 0 ? page.Bounds.Height : Fallback;

        // EaseIn on the way out mirrors the entrance's EaseOut, so the page accelerates away
        // instead of easing to a halt mid-exit. The fade is held back to the far end of the
        // travel in both directions, so the page is visibly moving rather than dissolving.
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(Milliseconds),
            Easing = rising ? new CubicEaseOut() : new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                Frame(0, rising ? distance : 0, rising ? 0 : 1),
                Frame(rising ? 0.6 : 0.45, null, 1),
                Frame(1, rising ? 0 : distance, rising ? 1 : 0),
            },
        };

        try
        {
            // Not awaited: the view model already waits the same duration out before unmounting
            // the page, and nothing here needs to happen afterwards.
            _ = animation.RunAsync(page);
        }
        catch (InvalidCastException)
        {
            // A transform animation wants a Visual. Every caller passes one, but a slide that
            // will not play is never worth taking a page down for.
        }
    }

    private static KeyFrame Frame(double cue, double? y, double opacity)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };

        if (y is { } offset) frame.Setters.Add(new Setter(TranslateTransform.YProperty, offset));

        frame.Setters.Add(new Setter(Visual.OpacityProperty, opacity));

        return frame;
    }
}
