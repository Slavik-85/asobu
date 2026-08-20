using System;
using System.Collections.Generic;
using Asobu.Core;
using Asobu.Core.Mods;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Asobu.App.Controls;

/// <summary>
/// Fills a TextBlock's inlines from a run of styled spans.
///
/// An attached property rather than a control: everything about laying the text out — wrapping,
/// trimming, the font, the colour — is already TextBlock's job and worth keeping there. The only
/// thing missing is that Avalonia has no way to bind a varying number of inlines, and this is it.
/// </summary>
public static class ProseText
{
    public static readonly AttachedProperty<IReadOnlyList<ProseSpan>?> SpansProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IReadOnlyList<ProseSpan>?>(
            "Spans", typeof(ProseText));

    /// <summary>What a link is set in. Held here so the two places that draw one agree.</summary>
    public static readonly AttachedProperty<IBrush?> LinkBrushProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, IBrush?>("LinkBrush", typeof(ProseText));

    static ProseText()
    {
        // Both rebuild the inlines. XAML sets attributes in the order they are written, so
        // whichever of the two lands second is the one that gets the runs right — waiting on
        // Spans alone left every link in the body colour, because the brush had not arrived yet.
        SpansProperty.Changed.AddClassHandler<TextBlock>((text, _) => Apply(text));
        LinkBrushProperty.Changed.AddClassHandler<TextBlock>((text, _) => Apply(text));
    }

    public static void SetSpans(TextBlock target, IReadOnlyList<ProseSpan>? value) =>
        target.SetValue(SpansProperty, value);

    public static IReadOnlyList<ProseSpan>? GetSpans(TextBlock target) => target.GetValue(SpansProperty);

    public static void SetLinkBrush(TextBlock target, IBrush? value) => target.SetValue(LinkBrushProperty, value);

    public static IBrush? GetLinkBrush(TextBlock target) => target.GetValue(LinkBrushProperty);

    private static void Apply(TextBlock target)
    {
        target.Inlines?.Clear();

        if (GetSpans(target) is not { } spans) return;

        target.Inlines ??= [];

        foreach (var span in spans)
        {
            var run = new Run(span.Text);

            if (span.Bold) run.FontWeight = FontWeight.SemiBold;
            if (span.Italic) run.FontStyle = FontStyle.Italic;

            if (span.Code)
            {
                run.FontFamily = FontFamily.Parse("Consolas, Menlo, monospace");
                run.FontSize = target.FontSize - 0.5;
            }

            if (span.IsLink)
            {
                run.Foreground = GetLinkBrush(target) ?? run.Foreground;
                run.TextDecorations = TextDecorations.Underline;

                // Whole-run pointer handling would need an InlineUIContainer per link, which
                // breaks wrapping mid-sentence. The address goes on the block instead: one
                // tooltip and one click for the paragraph, which is honest about what it does.
                ToolTip.SetTip(target, span.Link);
                target.Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand);

                target.PointerPressed -= OpenLink;
                target.PointerPressed += OpenLink;

                target.SetValue(HrefProperty, span.Link);
            }

            target.Inlines.Add(run);
        }
    }

    private static readonly AttachedProperty<string?> HrefProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Href", typeof(ProseText));

    private static void OpenLink(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not TextBlock text || text.GetValue(HrefProperty) is not { Length: > 0 } href) return;

        try
        {
            AsobuLauncher.OpenUrl(href);
        }
        catch (Exception)
        {
            // A link that will not open is not worth taking the page down for.
        }
    }
}
