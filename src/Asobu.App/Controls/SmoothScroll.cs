using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Asobu.App.Controls;

/// <summary>
/// A ScrollViewer moves a fixed number of lines the instant a wheel notch arrives, which reads
/// as a stutter. This instead sets a target offset and lets a transition ease onto it.
///
/// The easing deliberately runs through Avalonia's transition system rather than a timer: a
/// DispatcherTimer ticks on the UI message loop, so its frames drift against the compositor and
/// visibly drop. A transition is driven by the render clock, so every frame it produces is one
/// that actually gets painted.
///
/// Opt in per scroller with <c>ctrl:SmoothScroll.Enabled="True"</c>.
/// </summary>
public class SmoothScroll : AvaloniaObject
{
    /// <summary>Distance one wheel notch asks for. Larger feels faster, not jumpier.</summary>
    private const double PixelsPerNotch = 105;

    /// <summary>How long the glide onto a new target takes.</summary>
    private static readonly TimeSpan GlideDuration = TimeSpan.FromMilliseconds(220);

    /// <summary>
    /// After this long without a notch the run is over, so re-read the live offset. That also
    /// picks up scrollbar drags and ScrollToHome, which move the offset behind our back.
    /// </summary>
    private const double ReanchorAfterMilliseconds = 320;

    public static readonly AttachedProperty<bool> EnabledProperty =
        AvaloniaProperty.RegisterAttached<SmoothScroll, ScrollViewer, bool>("Enabled");

    public static void SetEnabled(ScrollViewer element, bool value) => element.SetValue(EnabledProperty, value);
    public static bool GetEnabled(ScrollViewer element) => element.GetValue(EnabledProperty);

    // Keyed weakly so a closed page's scroller can be collected normally.
    private static readonly ConditionalWeakTable<ScrollViewer, Glider> Attached = new();

    static SmoothScroll()
    {
        EnabledProperty.Changed.AddClassHandler<ScrollViewer>((scrollViewer, args) =>
        {
            if (args.GetNewValue<bool>()) Attached.GetValue(scrollViewer, viewer => new Glider(viewer));
        });
    }

    private sealed class Glider
    {
        private readonly ScrollViewer _scrollViewer;
        private double _target;
        private DateTime _lastNotch = DateTime.MinValue;

        public Glider(ScrollViewer scrollViewer)
        {
            _scrollViewer = scrollViewer;

            scrollViewer.Transitions ??= [];
            scrollViewer.Transitions.Add(new VectorTransition
            {
                Property = ScrollViewer.OffsetProperty,
                Duration = GlideDuration,
                Easing = new CubicEaseOut(),
            });

            // Tunnel so this runs before the ScrollViewer's own wheel handling and can replace it.
            scrollViewer.AddHandler(InputElement.PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        }

        private void OnWheel(object? sender, PointerWheelEventArgs e)
        {
            var maximum = Math.Max(0, _scrollViewer.Extent.Height - _scrollViewer.Viewport.Height);
            if (maximum <= 0) return;

            var now = DateTime.UtcNow;
            if ((now - _lastNotch).TotalMilliseconds > ReanchorAfterMilliseconds)
                _target = _scrollViewer.Offset.Y;
            _lastNotch = now;

            // Accumulate onto the target rather than the live offset, so spinning the wheel fast
            // travels proportionally further instead of each notch cancelling the last one's run.
            _target = Math.Clamp(_target - e.Delta.Y * PixelsPerNotch, 0, maximum);

            _scrollViewer.Offset = _scrollViewer.Offset.WithY(_target);
            e.Handled = true;
        }
    }
}
