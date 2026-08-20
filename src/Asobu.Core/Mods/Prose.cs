using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Asobu.Core.Mods;

/// <summary>A run of text within a paragraph, and how it should be set.</summary>
public sealed record ProseSpan(string Text, bool Bold = false, bool Italic = false, bool Code = false, string? Link = null)
{
    public bool IsLink => Link is { Length: > 0 };
}

/// <summary>One block of a description. What a renderer lays out, one after another.</summary>
public abstract record ProseBlock;

/// <summary>A heading. <paramref name="Level"/> is 1 to 6, as written.</summary>
public sealed record ProseHeading(int Level, IReadOnlyList<ProseSpan> Spans) : ProseBlock
{
    /// <summary>
    /// How big to set it. Decided here rather than in the view: the parser is the only thing
    /// that knows how deep the heading was, and three sizes cover what a description ever uses.
    /// </summary>
    public double Size => Level switch { 1 => 19, 2 => 16.5, _ => 14.5 };
}

public sealed record ProseParagraph(IReadOnlyList<ProseSpan> Spans) : ProseBlock;

/// <summary>
/// One item of a list. Flattened rather than nested: a description is read top to bottom, and a
/// tree buys nothing that an indent level does not.
/// </summary>
public sealed record ProseItem(int Depth, string Marker, IReadOnlyList<ProseSpan> Spans) : ProseBlock;

public sealed record ProseCode(string Text) : ProseBlock;

public sealed record ProseQuote(IReadOnlyList<ProseSpan> Spans) : ProseBlock;

public sealed record ProseRule : ProseBlock;

/// <summary>
/// Turns a project description into blocks a launcher can lay out.
///
/// Modrinth publishes markdown, CurseForge publishes HTML, and both are written for a web page:
/// headings, lists, links, the occasional table of compatibility. Reducing all of that to one
/// wall of plain text loses the shape of what the author wrote, which is most of what makes a
/// description readable. So a useful subset is understood properly and the rest is dropped —
/// this is deliberately not a browser, and anything it cannot set is better gone than mangled.
///
/// Not handled, on purpose: tables, inline images, nested block quotes and raw layout markup.
/// Tables reduce to punctuation soup in a single column; pictures already have a gallery.
/// </summary>
public static partial class Prose
{
    /// <summary>Enough to explain a mod. Past this it is documentation, not a description.</summary>
    private const int MaxBlocks = 220;

    public static IReadOnlyList<ProseBlock> FromMarkdown(string? body)
    {
        if (body is not { Length: > 0 }) return [];

        // Raw HTML turns up inside markdown constantly — centred divs, badge tables, <br>. The
        // tags go, the line breaks they stood for stay.
        var text = HtmlBreak().Replace(body, "\n");
        text = HtmlTag().Replace(text, "");

        return Blocks(WebUtility.HtmlDecode(text));
    }

    public static IReadOnlyList<ProseBlock> FromHtml(string? body)
    {
        if (body is not { Length: > 0 }) return [];

        // Reduced to markdown first rather than parsed as a document: the shapes worth keeping
        // have a markdown equivalent apiece, and one parser is a great deal less to get wrong
        // than two.
        var text = body;

        text = HtmlScriptOrStyle().Replace(text, "");
        text = HtmlHeading().Replace(text, match => "\n\n" + new string('#', int.Parse(match.Groups["level"].Value)) + " ");
        text = HtmlListItem().Replace(text, "\n- ");
        text = HtmlBold().Replace(text, "**");
        text = HtmlItalic().Replace(text, "*");
        text = HtmlCode().Replace(text, "`");
        text = HtmlQuote().Replace(text, "\n\n> ");
        text = HtmlRule().Replace(text, "\n\n---\n\n");
        text = HtmlLink().Replace(text, match => $"[{match.Groups["text"].Value}]({match.Groups["href"].Value})");
        text = HtmlBlockEnd().Replace(text, "\n\n");
        text = HtmlBreak().Replace(text, "\n");
        text = HtmlTag().Replace(text, "");

        return Blocks(WebUtility.HtmlDecode(text));
    }

    private static IReadOnlyList<ProseBlock> Blocks(string text)
    {
        var blocks = new List<ProseBlock>();
        var paragraph = new List<string>();
        var lines = text.Replace("\r\n", "\n").Split('\n');

        var fenced = false;
        var code = new StringBuilder();

        void FlushParagraph()
        {
            if (paragraph.Count == 0) return;

            var joined = string.Join(" ", paragraph).Trim();
            paragraph.Clear();

            if (joined.Length == 0) return;

            var spans = Spans(joined);
            if (HasWords(spans)) blocks.Add(new ProseParagraph(spans));
        }

        foreach (var raw in lines)
        {
            if (blocks.Count >= MaxBlocks) break;

            var line = raw.TrimEnd();

            if (Fence().IsMatch(line.TrimStart()))
            {
                if (fenced)
                {
                    blocks.Add(new ProseCode(code.ToString().TrimEnd()));
                    code.Clear();
                }
                else
                {
                    FlushParagraph();
                }

                fenced = !fenced;
                continue;
            }

            if (fenced)
            {
                code.AppendLine(raw);
                continue;
            }

            if (line.Trim().Length == 0)
            {
                FlushParagraph();
                continue;
            }

            if (Rule().IsMatch(line.Trim()))
            {
                FlushParagraph();
                blocks.Add(new ProseRule());
                continue;
            }

            if (Heading().Match(line) is { Success: true } heading)
            {
                FlushParagraph();

                var spans = Spans(heading.Groups["text"].Value.Trim());
                if (HasWords(spans)) blocks.Add(new ProseHeading(heading.Groups["hashes"].Value.Length, spans));

                continue;
            }

            if (Item().Match(line) is { Success: true } item)
            {
                FlushParagraph();

                var indent = item.Groups["indent"].Value.Replace("\t", "  ").Length;
                var marker = item.Groups["marker"].Value;

                var spans = Spans(item.Groups["text"].Value.Trim());

                if (HasWords(spans))
                    blocks.Add(new ProseItem(
                        Math.Min(2, indent / 2),
                        marker.EndsWith('.') || marker.EndsWith(')') ? marker : "•",
                        spans));

                continue;
            }

            if (Quote().Match(line) is { Success: true } quote)
            {
                FlushParagraph();

                var spans = Spans(quote.Groups["text"].Value.Trim());
                if (HasWords(spans)) blocks.Add(new ProseQuote(spans));

                continue;
            }

            paragraph.Add(line.Trim());
        }

        FlushParagraph();

        if (fenced && code.Length > 0) blocks.Add(new ProseCode(code.ToString().TrimEnd()));

        return blocks;
    }

    /// <summary>Whether anything survived the markup. A row of badges leaves nothing behind.</summary>
    private static bool HasWords(IReadOnlyList<ProseSpan> spans) =>
        spans.Any(span => !string.IsNullOrWhiteSpace(span.Text));

    /// <summary>
    /// Splits one line into styled runs. A single left-to-right scan rather than a stack of
    /// regex passes, because emphasis nests and replacing it in passes leaves stray asterisks
    /// wherever the author's markup was not quite right — which, in a mod description, is often.
    /// </summary>
    private static IReadOnlyList<ProseSpan> Spans(string line)
    {
        var spans = new List<ProseSpan>();
        var plain = new StringBuilder();

        var bold = false;
        var italic = false;
        var i = 0;

        void Flush()
        {
            if (plain.Length == 0) return;

            spans.Add(new ProseSpan(plain.ToString(), bold, italic));
            plain.Clear();
        }

        while (i < line.Length)
        {
            var rest = line.AsSpan(i);

            // Images first: the syntax starts the same as a link and the gallery already has them.
            if (rest.StartsWith("![") && Image().Match(line, i) is { Success: true } image)
            {
                i += image.Length;
                continue;
            }

            if (rest[0] == '[' && Link().Match(line, i) is { Success: true } link)
            {
                var label = link.Groups["text"].Value;

                // A badge: a picture wrapped in a link, and nothing else. Both halves go.
                if (!OnlyImage().IsMatch(label))
                {
                    Flush();
                    spans.Add(new ProseSpan(label, bold, italic, false, link.Groups["href"].Value));
                }

                i += link.Length;
                continue;
            }

            if (rest[0] == '`' && Inline().Match(line, i) is { Success: true } inline)
            {
                Flush();
                spans.Add(new ProseSpan(inline.Groups["text"].Value, bold, italic, true));

                i += inline.Length;
                continue;
            }

            if (rest.StartsWith("**") || rest.StartsWith("__"))
            {
                Flush();
                bold = !bold;
                i += 2;
                continue;
            }

            if (rest[0] is '*' or '_')
            {
                Flush();
                italic = !italic;
                i++;
                continue;
            }

            plain.Append(line[i]);
            i++;
        }

        Flush();

        return spans;
    }

    [GeneratedRegex(@"^\s{0,3}(?<hashes>#{1,6})\s+(?<text>.+)$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(?<indent>[ \t]*)(?<marker>[-*+]|\d+[.)])\s+(?<text>.+)$")]
    private static partial Regex Item();

    [GeneratedRegex(@"^\s{0,3}>\s?(?<text>.*)$")]
    private static partial Regex Quote();

    [GeneratedRegex(@"^(?:[-*_]\s*){3,}$")]
    private static partial Regex Rule();

    [GeneratedRegex(@"^(?:```|~~~)")]
    private static partial Regex Fence();

    [GeneratedRegex(@"\G!\[[^\]]*\]\([^)]*\)")]
    private static partial Regex Image();

    [GeneratedRegex(@"\G\[(?<text>(?:[^\[\]]|\[[^\]]*\])*)\]\((?<href>[^)\s]*)[^)]*\)")]
    private static partial Regex Link();

    [GeneratedRegex(@"^\s*!\[[^\]]*\](?:\([^)]*\))?\s*$")]
    private static partial Regex OnlyImage();

    [GeneratedRegex(@"\G`(?<text>[^`]+)`")]
    private static partial Regex Inline();

    [GeneratedRegex(@"<\s*br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBreak();

    [GeneratedRegex(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlScriptOrStyle();

    [GeneratedRegex(@"<\s*h(?<level>[1-6])\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlHeading();

    [GeneratedRegex(@"<\s*li\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlListItem();

    [GeneratedRegex(@"</?\s*(?:strong|b)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBold();

    [GeneratedRegex(@"</?\s*(?:em|i)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlItalic();

    [GeneratedRegex(@"</?\s*code\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlCode();

    [GeneratedRegex(@"<\s*blockquote\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlQuote();

    [GeneratedRegex(@"<\s*hr\b[^>]*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlRule();

    [GeneratedRegex(@"<\s*a\b[^>]*href\s*=\s*[""'](?<href>[^""']*)[""'][^>]*>(?<text>.*?)</\s*a\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex HtmlLink();

    [GeneratedRegex(@"</\s*(?:p|div|li|ul|ol|h[1-6]|blockquote|tr|section|article)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex HtmlBlockEnd();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTag();
}
