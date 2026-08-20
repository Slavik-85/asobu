using Asobu.App.ViewModels;
using Avalonia.Controls;

namespace Asobu.App.Views;

public partial class BrowseView : UserControl
{
    /// <summary>
    /// How close to the bottom counts as "nearly there". Roughly a screenful, so the next page
    /// is usually already in place by the time the scroll reaches where it would have ended.
    /// </summary>
    private const double LoadMoreReach = 700;

    public BrowseView() => InitializeComponent();

    private void ResultsScrolled(object? sender, ScrollChangedEventArgs e)
    {
        var remaining = ResultScroll.Extent.Height - (ResultScroll.Offset.Y + ResultScroll.Viewport.Height);

        if (remaining <= LoadMoreReach && DataContext is BrowseViewModel browse) browse.LoadMore();
    }
}
