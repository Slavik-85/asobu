using System;
using Avalonia;
using Avalonia.Controls;

namespace Asobu.App.Controls;

/// <summary>
/// A grid that decides its own column count from the width it is given, then shares that width
/// out evenly between the columns.
///
/// A WrapPanel would be the obvious choice, but it lays fixed-width children out left to right
/// and leaves whatever is left over as a gap on the right — which at most window sizes is nearly
/// a whole card's worth of nothing. This asks instead how many cards of roughly
/// <see cref="MinItemWidth"/> fit, and then stretches them to use the row exactly.
///
/// Rows are uniform in height, taken from the tallest child, so the grid stays aligned when one
/// card has a longer name or an extra line of status than its neighbours.
/// </summary>
public class FlowGrid : Panel
{
    /// <summary>The narrowest a card may be squeezed before a column is dropped.</summary>
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<FlowGrid, double>(nameof(MinItemWidth), 170);

    /// <summary>
    /// The widest a card may be stretched. Without a ceiling a narrow window would hand a single
    /// column the entire page, and a mod tile blown up to 700px looks like a mistake.
    /// </summary>
    public static readonly StyledProperty<double> MaxItemWidthProperty =
        AvaloniaProperty.Register<FlowGrid, double>(nameof(MaxItemWidth), 260);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<FlowGrid, double>(nameof(Spacing), 14);

    static FlowGrid()
    {
        AffectsMeasure<FlowGrid>(MinItemWidthProperty, MaxItemWidthProperty, SpacingProperty);
    }

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double MaxItemWidth
    {
        get => GetValue(MaxItemWidthProperty);
        set => SetValue(MaxItemWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int _columns = 1;
    private double _itemWidth;
    private double _rowHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        var children = Children;
        if (children.Count == 0) return default;

        var spacing = Spacing;

        // Inside an unbounded parent — a horizontal scroller, say — there is no width to divide
        // up, so fall back to one row of natural-sized cards.
        var available = double.IsInfinity(availableSize.Width)
            ? children.Count * (MinItemWidth + spacing)
            : availableSize.Width;

        _columns = Math.Max(1, (int)Math.Floor((available + spacing) / (MinItemWidth + spacing)));
        _columns = Math.Min(_columns, children.Count);

        _itemWidth = Math.Min(MaxItemWidth, (available - spacing * (_columns - 1)) / _columns);

        _rowHeight = 0;
        foreach (var child in children)
        {
            child.Measure(new Size(_itemWidth, double.PositiveInfinity));
            _rowHeight = Math.Max(_rowHeight, child.DesiredSize.Height);
        }

        var rows = (children.Count + _columns - 1) / _columns;

        return new Size(
            _columns * _itemWidth + spacing * (_columns - 1),
            rows * _rowHeight + spacing * (rows - 1));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var spacing = Spacing;

        for (var i = 0; i < Children.Count; i++)
        {
            var column = i % _columns;
            var row = i / _columns;

            Children[i].Arrange(new Rect(
                column * (_itemWidth + spacing),
                row * (_rowHeight + spacing),
                _itemWidth,
                _rowHeight));
        }

        return finalSize;
    }
}
