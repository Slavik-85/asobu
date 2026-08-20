using System;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Asobu.App.Controls;

/// <summary>
/// Dims the whole window except for one rounded rectangle.
///
/// Drawn as a single shape with an even-odd fill rather than as four bands around the hole:
/// bands cannot round the corners, and a square hole over a round button looks like a mistake.
///
/// The hole glides between positions instead of jumping. That is the entire reason this is a
/// control and not a Border — a moving highlight tells you the tour went somewhere, where a
/// cut tells you something got replaced.
/// </summary>
public sealed class Spotlight : Control
{
    /// <summary>Where the light is. An empty rect dims everything, which is the resting state.</summary>
    public static readonly StyledProperty<Rect> HoleProperty =
        AvaloniaProperty.Register<Spotlight, Rect>(nameof(Hole));

    public static readonly StyledProperty<IBrush?> ScrimProperty =
        AvaloniaProperty.Register<Spotlight, IBrush?>(nameof(Scrim));

    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<Spotlight, double>(nameof(Radius), 14);

    /// <summary>How far outside the target the light spills, so the target isn't touching the edge.</summary>
    public static readonly StyledProperty<double> PaddingAroundProperty =
        AvaloniaProperty.Register<Spotlight, double>(nameof(PaddingAround), 6);

    public Rect Hole
    {
        get => GetValue(HoleProperty);
        set => SetValue(HoleProperty, value);
    }

    public IBrush? Scrim
    {
        get => GetValue(ScrimProperty);
        set => SetValue(ScrimProperty, value);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public double PaddingAround
    {
        get => GetValue(PaddingAroundProperty);
        set => SetValue(PaddingAroundProperty, value);
    }

    static Spotlight()
    {
        AffectsRender<Spotlight>(HoleProperty, ScrimProperty, RadiusProperty, PaddingAroundProperty);
    }

    private const int GlideMilliseconds = 280;

    private static readonly Easing Glide = new CubicEaseOut();

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };

    private Rect _from;
    private Rect _drawn;
    private DateTime _started;
    private bool _moving;

    public Spotlight()
    {
        _timer.Tick += (_, _) => Step();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != HoleProperty) return;

        var target = Hole;

        // The first hole of a run appears where it belongs rather than sweeping in from the
        // corner, which is what interpolating out of an empty rect would look like.
        if (_drawn.Width <= 0 || target.Width <= 0)
        {
            _moving = false;
            _timer.Stop();
            _drawn = target;
            InvalidateVisual();
            return;
        }

        _from = _drawn;
        _started = DateTime.UtcNow;
        _moving = true;
        _timer.Start();
    }

    private void Step()
    {
        var elapsed = (DateTime.UtcNow - _started).TotalMilliseconds;
        var progress = Math.Clamp(elapsed / GlideMilliseconds, 0, 1);
        var eased = Glide.Ease(progress);

        _drawn = new Rect(
            Lerp(_from.X, Hole.X, eased),
            Lerp(_from.Y, Hole.Y, eased),
            Lerp(_from.Width, Hole.Width, eased),
            Lerp(_from.Height, Hole.Height, eased));

        if (progress >= 1)
        {
            _moving = false;
            _timer.Stop();
        }

        InvalidateVisual();
    }

    private static double Lerp(double from, double to, double t) => from + ((to - from) * t);

    public override void Render(DrawingContext context)
    {
        if (Scrim is not { } scrim) return;

        var full = new Rect(Bounds.Size);

        if (_drawn.Width <= 0 || _drawn.Height <= 0)
        {
            context.FillRectangle(scrim, full);
            return;
        }

        var hole = _drawn.Inflate(PaddingAround).Intersect(full);

        // Even-odd: the outer rectangle is filled, and anything inside the second shape is
        // wound out of it again.
        var punched = new GeometryGroup { FillRule = FillRule.EvenOdd };
        punched.Children.Add(new RectangleGeometry(full));
        punched.Children.Add(new RectangleGeometry(hole, Radius, Radius));

        context.DrawGeometry(scrim, null, punched);
    }

    /// <summary>Stops the glide when the overlay leaves, so a hidden control isn't ticking.</summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        _moving = false;
        _timer.Stop();
    }

    /// <summary>Whether a glide is in flight; the card waits for it before repositioning.</summary>
    public bool IsMoving => _moving;
}
