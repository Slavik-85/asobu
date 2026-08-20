using System.Collections.Generic;
using Asobu.Core.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Asobu.App.Controls;

/// <summary>
/// Fills a text block with the game's output, a run per line, coloured by how loud it was.
///
/// One control holding many runs rather than a control per line: a modded start-up runs to
/// thousands of lines, and a thousand TextBlocks costs a thousand layouts. Runs inside a single
/// SelectableTextBlock lay out as one paragraph and can still be selected across, which is what
/// anyone copying a stack trace into a bug report actually wants.
///
/// The same attached-property shape as <see cref="ProseText"/>, and for the same reason: Avalonia
/// has no way to bind a varying number of inlines.
/// </summary>
public static class GameLogText
{
    public static readonly AttachedProperty<IReadOnlyList<GameLogLine>?> LinesProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<GameLogLine>?>("Lines", typeof(GameLogText));

    /// <summary>
    /// The four colours, set from the view so the palette stays in the stylesheet. Each rebuilds
    /// the runs, because XAML applies attributes in the order they are written and whichever
    /// lands last is the one that has everything it needs.
    /// </summary>
    public static readonly AttachedProperty<IBrush?> InfoBrushProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IBrush?>("InfoBrush", typeof(GameLogText));

    public static readonly AttachedProperty<IBrush?> WarnBrushProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IBrush?>("WarnBrush", typeof(GameLogText));

    public static readonly AttachedProperty<IBrush?> ErrorBrushProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IBrush?>("ErrorBrush", typeof(GameLogText));

    public static readonly AttachedProperty<IBrush?> ChatterBrushProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IBrush?>("ChatterBrush", typeof(GameLogText));

    static GameLogText()
    {
        LinesProperty.Changed.AddClassHandler<TextBlock>((text, _) => Apply(text));
        InfoBrushProperty.Changed.AddClassHandler<TextBlock>((text, _) => Apply(text));
        WarnBrushProperty.Changed.AddClassHandler<TextBlock>((text, _) => Apply(text));
        ErrorBrushProperty.Changed.AddClassHandler<TextBlock>((text, _) => Apply(text));
        ChatterBrushProperty.Changed.AddClassHandler<TextBlock>((text, _) => Apply(text));
    }

    public static void SetLines(TextBlock target, IReadOnlyList<GameLogLine>? value) =>
        target.SetValue(LinesProperty, value);

    public static IReadOnlyList<GameLogLine>? GetLines(TextBlock target) => target.GetValue(LinesProperty);

    public static void SetInfoBrush(TextBlock target, IBrush? value) => target.SetValue(InfoBrushProperty, value);

    public static IBrush? GetInfoBrush(TextBlock target) => target.GetValue(InfoBrushProperty);

    public static void SetWarnBrush(TextBlock target, IBrush? value) => target.SetValue(WarnBrushProperty, value);

    public static IBrush? GetWarnBrush(TextBlock target) => target.GetValue(WarnBrushProperty);

    public static void SetErrorBrush(TextBlock target, IBrush? value) => target.SetValue(ErrorBrushProperty, value);

    public static IBrush? GetErrorBrush(TextBlock target) => target.GetValue(ErrorBrushProperty);

    public static void SetChatterBrush(TextBlock target, IBrush? value) => target.SetValue(ChatterBrushProperty, value);

    public static IBrush? GetChatterBrush(TextBlock target) => target.GetValue(ChatterBrushProperty);

    private static void Apply(TextBlock target)
    {
        target.Inlines?.Clear();

        if (GetLines(target) is not { Count: > 0 } lines) return;

        target.Inlines ??= [];

        for (var i = 0; i < lines.Count; i++)
        {
            // The newline goes on the end of each run rather than between them, so a selection
            // dragged over the whole block copies out as lines rather than one long sentence.
            var run = new Run(i == lines.Count - 1 ? lines[i].Text : lines[i].Text + "\n")
            {
                Foreground = BrushFor(target, lines[i].Level),
            };

            target.Inlines.Add(run);
        }
    }

    private static IBrush? BrushFor(TextBlock target, GameLogLevel level) => level switch
    {
        GameLogLevel.Warn => GetWarnBrush(target),
        GameLogLevel.Error => GetErrorBrush(target),
        GameLogLevel.Chatter => GetChatterBrush(target),
        _ => GetInfoBrush(target),
    };
}
