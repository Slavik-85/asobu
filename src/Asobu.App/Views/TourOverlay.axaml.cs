using System.ComponentModel;
using System.Linq;
using Asobu.App.ViewModels;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Asobu.App.Views;

/// <summary>
/// Points at things.
///
/// The steps name their target as a string and this finds it in the window, rather than the
/// tour holding references to controls: a control only exists once its page is built, and the
/// tour is written long before that. A step whose target is nowhere to be found dims the window
/// and says its piece without a highlight, which is a dull step rather than a broken launcher.
/// </summary>
public partial class TourOverlay : UserControl
{
    public TourOverlay()
    {
        InitializeComponent();

        DataContextChanged += (_, _) => Hook();
        SizeChanged += (_, _) => Sync();
        AttachedToVisualTree += (_, _) => Sync();
    }

    private TourViewModel? _watched;

    private void Hook()
    {
        if (_watched is not null) _watched.PropertyChanged -= OnStepChanged;

        _watched = DataContext as TourViewModel;

        if (_watched is not null) _watched.PropertyChanged += OnStepChanged;
    }

    private void OnStepChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(TourViewModel.Current)) return;

        // After layout, so a card that just changed its text is measured at its new size and a
        // page that just changed is done arranging.
        Dispatcher.UIThread.Post(Sync, DispatcherPriority.Loaded);
    }

    private void Sync()
    {
        if (DataContext is not TourViewModel { Current: { } step }) return;

        var target = Find(step.TargetName) ?? (step.OrElse is null ? null : Find(step.OrElse));

        if (target?.TranslatePoint(default, Root) is not { } corner)
        {
            // Nothing to point at: dim everything and let the card speak for itself.
            Light.Hole = default;
            Place(new Rect(Root.Bounds.Width / 2, Root.Bounds.Height / 2, 0, 0));
            return;
        }

        var hole = new Rect(corner, target.Bounds.Size);
        Light.Hole = hole;
        Place(hole);
    }

    /// <summary>The named control, if it is currently on screen at all.</summary>
    private Control? Find(string name) =>
        TopLevel.GetTopLevel(this)?
            .GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(control => control.Name == name && control.IsEffectivelyVisible);

    /// <summary>
    /// Puts the card beside what is lit, on whichever side has the room, and never off-screen.
    /// </summary>
    private void Place(Rect hole)
    {
        const double gap = 22;
        const double edge = 20;

        var width = Card.Bounds.Width > 0 ? Card.Bounds.Width : Card.Width;
        var height = Card.Bounds.Height;

        // Before the first arrange the card has no size, and a card placed against a height of
        // zero would be left sitting off-screen where it starts. Ask it how big it wants to be
        // rather than giving up on the step.
        if (height <= 0)
        {
            Card.Measure(new Size(width, double.PositiveInfinity));
            height = Card.DesiredSize.Height;
        }

        var room = Root.Bounds;
        if (room.Width <= 0 || height <= 0) return;

        // Beside the highlight, on the side with more space — which puts it right of the
        // sidebar and left of anything in the far corner.
        var x = hole.Center.X < room.Width / 2 ? hole.Right + gap : hole.X - gap - width;
        var y = hole.Center.Y - (height / 2);

        Card.Margin = new Thickness(
            Clamp(x, edge, room.Width - width - edge),
            Clamp(y, edge, room.Height - height - edge),
            0, 0);

        Reveal();
    }

    private static double Clamp(double value, double low, double high) =>
        high < low ? low : System.Math.Clamp(value, low, high);

    /// <summary>
    /// Re-runs the card's entrance. The class has to stop matching before it can start again,
    /// and that has to happen across two passes or the two changes cancel each other out.
    /// </summary>
    private void Reveal()
    {
        Card.Classes.Remove("on");
        Dispatcher.UIThread.Post(() => Card.Classes.Add("on"), DispatcherPriority.Background);
    }
}
