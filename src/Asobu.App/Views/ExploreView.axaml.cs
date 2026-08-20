using System;
using System.ComponentModel;
using System.Threading;
using Asobu.App.ViewModels;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Asobu.App.Views;

public partial class ExploreView : UserControl
{
    /// <summary>How far the incoming mod slides in from, in pixels.</summary>
    private const double SlideDistance = 40;

    /// <summary>
    /// How close to the bottom counts as "nearly there". Roughly a screenful, so the next page
    /// is usually already in place by the time the scroll reaches where it would have ended.
    /// </summary>
    private const double LoadMoreReach = 700;

    private CancellationTokenSource? _transition;
    private ExploreViewModel? _watching;

    public ExploreView()
    {
        InitializeComponent();

        // Read off IsPointerOver rather than handled as Entered/Exited events. Those bubble up
        // from every child, so crossing between the title and the Add button fires an exit the
        // banner never actually had — this property is simply true while the pointer is inside.
        HeroArea.PropertyChanged += (_, e) =>
        {
            if (e.Property == InputElement.IsPointerOverProperty)
                Explore?.SetHeroPaused(HeroArea.IsPointerOver);
        };
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Watch(Explore);
    }

    /// <summary>
    /// The page is thrown away and built again on every navigation, so the subscription has to go
    /// with it — otherwise each visit leaves another dead view listening to the view model, all
    /// of them animating controls that are no longer on screen.
    /// </summary>
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        Watch(null);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Watch(Explore);
    }

    private void Watch(ExploreViewModel? explore)
    {
        if (ReferenceEquals(_watching, explore)) return;

        if (_watching is not null) _watching.PropertyChanged -= FeatureChanged;

        _watching = explore;

        if (_watching is not null) _watching.PropertyChanged += FeatureChanged;
    }

    private void FeatureChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ExploreViewModel.Feature)) return;

        // Posted, not run here. This handler shares the view model's PropertyChanged with every
        // binding on the banner, and handlers are called in turn: anything that threw in this
        // one would stop the bindings queued behind it from ever being told the mod changed,
        // leaving a banner that is open and empty. The mod has to reach the controls before
        // there is anything to animate in, anyway.
        Dispatcher.UIThread.Post(
            () =>
            {
                if (Explore is { Feature: not null } explore) PlayTransition(explore.SlideDirection);
            },
            DispatcherPriority.Render);
    }

    /// <summary>
    /// The mod that just arrived slides in from the side it came from and settles with a small
    /// overshoot, while the picture behind it eases out of a slight zoom. Run from here rather
    /// than as a style animation because the same controls are reused for every slide — nothing
    /// starts matching a new selector, so there is no state change for a style to fire on.
    ///
    /// Everything here targets a control rather than a transform object. Animating a
    /// TranslateTransform directly reads better and does not work — RunAsync wants a Visual and
    /// a Transform is not one — while RenderTransform as a whole has no animator at all. What
    /// Avalonia does support is a transform's own properties applied to a control: it builds the
    /// RenderTransform behind the scenes, which is why they are cleared to null below.
    /// </summary>
    private void PlayTransition(int direction)
    {
        _transition?.Cancel();
        _transition = new CancellationTokenSource();
        var token = _transition.Token;

        // A cancelled animation stops wherever it had got to, so everything goes back to rest
        // before the next one starts. Without this a fast double-press can leave the banner
        // stuck half-faded or shifted off to one side.
        HeroContent.RenderTransform = null;
        HeroBackdrop.RenderTransform = null;
        HeroContent.Opacity = 1;
        HeroBackdrop.Opacity = 1;

        var from = direction >= 0 ? SlideDistance : -SlideDistance;

        try
        {
            _ = Slide(from).RunAsync(HeroContent, token);
            _ = Fade(TimeSpan.FromMilliseconds(320)).RunAsync(HeroContent, token);
            _ = Settle().RunAsync(HeroBackdrop, token);
            _ = Fade(TimeSpan.FromMilliseconds(420)).RunAsync(HeroBackdrop, token);
        }
        catch (Exception)
        {
            // Decoration. A banner that arrives without sliding is still a banner; one that
            // takes the page down with it on the way in is not.
        }
    }

    private static Animation Slide(double from) => new()
    {
        Duration = TimeSpan.FromMilliseconds(460),
        // A little past its mark and back: the difference between arriving and being placed.
        Easing = new BackEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            Frame(0, new Setter(TranslateTransform.XProperty, from)),
            Frame(1, new Setter(TranslateTransform.XProperty, 0d)),
        },
    };

    private static Animation Settle() => new()
    {
        Duration = TimeSpan.FromMilliseconds(620),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            Frame(0, new Setter(ScaleTransform.ScaleXProperty, 1.06),
                     new Setter(ScaleTransform.ScaleYProperty, 1.06)),
            Frame(1, new Setter(ScaleTransform.ScaleXProperty, 1.0),
                     new Setter(ScaleTransform.ScaleYProperty, 1.0)),
        },
    };

    private static Animation Fade(TimeSpan duration) => new()
    {
        Duration = duration,
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            Frame(0, new Setter(OpacityProperty, 0d)),
            Frame(1, new Setter(OpacityProperty, 1d)),
        },
    };

    private static KeyFrame Frame(double cue, params Setter[] setters)
    {
        var frame = new KeyFrame { Cue = new Cue(cue) };
        foreach (var setter in setters) frame.Setters.Add(setter);

        return frame;
    }

    /// <summary>
    /// The grid grows as it is scrolled rather than ending at the first page. Driven from the
    /// scroller's own position because the page scrolls as one column — there is no separate
    /// list control underneath to ask.
    /// </summary>
    private void PageScrolled(object? sender, ScrollChangedEventArgs e)
    {
        var remaining = PageScroll.Extent.Height - (PageScroll.Offset.Y + PageScroll.Viewport.Height);

        if (remaining <= LoadMoreReach) Explore?.LoadMore();
    }

    private ExploreViewModel? Explore => DataContext as ExploreViewModel;
}
