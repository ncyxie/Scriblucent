using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;

namespace Scriblucent.Services;

/// <summary>Renders release notes with native text elements and the application's typography.</summary>
internal static class ReleaseNotesDocumentBuilder
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseAutoLinks().Build();
    private static readonly MarkdownPipeline NormalizedHtmlPipeline = new MarkdownPipelineBuilder()
        .UsePipeTables().UseEmphasisExtras().UseAutoLinks().DisableHtml().Build();
    private static readonly Regex Comments = Pattern(@"<!--.*?-->");
    private static readonly Regex NonProse = Pattern(@"<(?<tag>script|style|picture|svg)\b[^>]*>.*?</\k<tag>\s*>");
    private static readonly Regex Tags = Pattern("<(?<close>/)?(?<tag>[a-z][a-z0-9:-]*)(?=\\s|/?>)(?<attributes>(?:[^\"'<>]|\"[^\"]*\"|'[^']*')*)>");
    private static readonly Regex Anchors = Pattern(@"<a\b(?<attributes>[^>]+)>(?<label>.*?)</a\s*>");
    private static readonly Regex Href = Pattern("\\bhref\\s*=\\s*(?:\"(?<url>[^\"]*)\"|'(?<url>[^']*)'|(?<url>[^\\s>]+))");

    internal static FlowDocument Build(string? body)
    {
        var document = new FlowDocument
        {
            PagePadding = new Thickness(6, 4, 6, 4), ColumnWidth = double.PositiveInfinity,
            FontSize = 14, LineHeight = 20, TextAlignment = TextAlignment.Left
        };
        document.SetResourceReference(TextElement.FontFamilyProperty, "FontFamily.UI");
        document.SetResourceReference(TextElement.FontSizeProperty, "Type.Body");
        document.SetResourceReference(TextElement.ForegroundProperty, "TextColor");
        if (!string.IsNullOrWhiteSpace(body))
            AddBlocks(document.Blocks, Markdown.Parse(body, Pipeline));
        return document;
    }

    private static void AddBlocks(BlockCollection target, ContainerBlock source)
    {
        foreach (var block in source)
        {
            switch (block)
            {
                case HtmlBlock html:
                    // Release pages include HTML layout around otherwise ordinary Markdown.
                    // Normalize only HTML blocks, leaving fenced/inline code untouched.
                    AddBlocks(target, Markdown.Parse(NormalizeHtml(html.Lines.ToString()), NormalizedHtmlPipeline));
                    break;
                case HeadingBlock heading:
                    var title = Paragraph(heading.Inline);
                    if (title.Inlines.Count == 0) break;
                    title.FontWeight = FontWeights.SemiBold;
                    title.SetResourceReference(TextElement.FontSizeProperty,
                        heading.Level <= 2 ? "Type.BlockTitle" : "Type.SectionTitle");
                    title.Margin = new Thickness(0, target.Count == 0 ? 0 : 12, 0, 6);
                    target.Add(title);
                    break;
                case ParagraphBlock paragraph:
                    var prose = Paragraph(paragraph.Inline);
                    if (prose.Inlines.Count > 0) target.Add(prose);
                    break;
                case ListBlock list:
                    var items = new System.Windows.Documents.List
                    {
                        MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                        Margin = new Thickness(0, 0, 0, 8), Padding = new Thickness(20, 0, 0, 0)
                    };
                    if (list.IsOrdered && int.TryParse(list.OrderedStart, out var start) && start > 0)
                        items.StartIndex = start;
                    foreach (var child in list.OfType<ListItemBlock>())
                    {
                        var item = new ListItem { Margin = new Thickness(0, 0, 0, 3) };
                        AddBlocks(item.Blocks, child);
                        foreach (var itemBlock in item.Blocks) itemBlock.Margin = new Thickness(0);
                        if (item.Blocks.Count > 0) items.ListItems.Add(item);
                    }
                    if (items.ListItems.Count > 0) target.Add(items);
                    break;
                case MdTable table:
                    var grid = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 2, 0, 10) };
                    for (var i = 0; i < table.ColumnDefinitions.Count; i++)
                        grid.Columns.Add(new TableColumn { Width = new GridLength(1, GridUnitType.Star) });
                    var rows = new TableRowGroup();
                    foreach (var row in table.OfType<MdTableRow>())
                    {
                        var renderedRow = new TableRow();
                        foreach (var cell in row.OfType<Markdig.Extensions.Tables.TableCell>())
                        {
                            var renderedCell = new TableCell { Padding = new Thickness(0, 4, 10, 4) };
                            if (row.IsHeader) renderedCell.FontWeight = FontWeights.SemiBold;
                            AddBlocks(renderedCell.Blocks, cell);
                            foreach (var cellBlock in renderedCell.Blocks) cellBlock.Margin = new Thickness(0);
                            renderedRow.Cells.Add(renderedCell);
                        }
                        rows.Rows.Add(renderedRow);
                    }
                    grid.RowGroups.Add(rows); target.Add(grid);
                    break;
                case CodeBlock code:
                    var codeParagraph = new Paragraph(new Run(code.Lines.ToString()))
                    {
                        FontFamily = new FontFamily("Consolas"), Margin = new Thickness(0, 0, 0, 8)
                    };
                    target.Add(codeParagraph);
                    break;
                case ContainerBlock container:
                    var section = new Section();
                    AddBlocks(section.Blocks, container);
                    if (block is QuoteBlock)
                    {
                        section.Margin = new Thickness(12, 0, 0, 8);
                        section.SetResourceReference(TextElement.ForegroundProperty, "TextColorSecondary");
                    }
                    if (section.Blocks.Count > 0) target.Add(section);
                    break;
            }
        }
    }

    private static Paragraph Paragraph(ContainerInline? source)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
        if (source is not null) AddInlines(paragraph.Inlines, source);
        return paragraph;
    }

    private static void AddInlines(InlineCollection target, ContainerInline source)
    {
        var current = target;
        var htmlScopes = new Stack<(string Tag, InlineCollection Parent)>();
        string? hiddenTag = null;
        foreach (var inline in source)
        {
            if (inline is HtmlInline html)
            {
                var tag = Tags.Match(html.Tag);
                if (!tag.Success) continue;
                var name = tag.Groups["tag"].Value.ToLowerInvariant();
                bool closing = tag.Groups["close"].Success;
                if (hiddenTag is not null) { if (closing && name == hiddenTag) hiddenTag = null; continue; }
                if (name is "script" or "style" or "picture" or "svg") { if (!closing) hiddenTag = name; continue; }
                if (closing)
                {
                    if (htmlScopes.TryPeek(out var scope) && scope.Tag == name)
                    { current = scope.Parent; htmlScopes.Pop(); }
                    continue;
                }
                Span? span = name switch
                {
                    "strong" or "b" => new Bold(),
                    "em" or "i" => new Italic(),
                    "s" or "del" => new Span { TextDecorations = TextDecorations.Strikethrough },
                    "code" => new Span { FontFamily = new FontFamily("Consolas") },
                    "a" => Link(WebUtility.HtmlDecode(Href.Match(tag.Groups["attributes"].Value).Groups["url"].Value)) ?? new Span(),
                    _ => null
                };
                if (span is not null)
                { current.Add(span); htmlScopes.Push((name, current)); current = span.Inlines; }
                else if (name == "br") current.Add(new LineBreak());
                continue;
            }
            if (hiddenTag is not null) continue;
            switch (inline)
            {
                case LiteralInline literal: current.Add(new Run(literal.Content.ToString())); break;
                case HtmlEntityInline entity: current.Add(new Run(entity.Transcoded.ToString())); break;
                case LineBreakInline lineBreak:
                    if (lineBreak.IsHard) current.Add(new LineBreak()); else current.Add(new Run(" "));
                    break;
                case CodeInline code:
                    current.Add(new Run(code.Content) { FontFamily = new FontFamily("Consolas") });
                    break;
                case EmphasisInline emphasis:
                    var span = new Span();
                    if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else
                    {
                        if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold;
                        if (emphasis.DelimiterCount % 2 == 1) span.FontStyle = FontStyles.Italic;
                    }
                    AddInlines(span.Inlines, emphasis); current.Add(span);
                    break;
                case LinkInline link:
                    if (link.IsImage) break;
                    var anchor = Link(link.Url);
                    if (anchor is null) AddInlines(current, link);
                    else { AddInlines(anchor.Inlines, link); if (anchor.Inlines.Count > 0) current.Add(anchor); }
                    break;
                case AutolinkInline auto:
                    var automaticLink = Link(auto.Url);
                    if (automaticLink is null) current.Add(new Run(auto.Url));
                    else { automaticLink.Inlines.Add(new Run(auto.Url)); current.Add(automaticLink); }
                    break;
                case ContainerInline container: AddInlines(current, container); break;
            }
        }
    }

    private static Hyperlink? Link(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")) return null;
        var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
        link.SetResourceReference(TextElement.ForegroundProperty, "AccentPrimarySubtleForeground");
        link.RequestNavigate += (_, args) =>
        {
            args.Handled = true;
            try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
            { Debug.WriteLine($"Release link: {error.Message}"); }
        };
        return link;
    }

    private static string NormalizeHtml(string html)
    {
        var text = NonProse.Replace(Comments.Replace(html, string.Empty), string.Empty);
        text = Anchors.Replace(text, match => string.IsNullOrWhiteSpace(match.Groups["label"].Value)
            ? string.Empty
            : $"[{match.Groups["label"].Value}](<{Href.Match(match.Groups["attributes"].Value).Groups["url"].Value}>)");
        return Tags.Replace(text, match =>
        {
            var name = match.Groups["tag"].Value.ToLowerInvariant();
            bool closing = match.Groups["close"].Success;
            return name switch
            {
                "strong" or "b" => "**",
                "em" or "i" => "*",
                "s" or "del" => "~~",
                "code" => "`",
                "li" => closing ? "\n" : "\n- ",
                "h1" or "h2" or "h3" or "h4" or "h5" or "h6" => closing ? "\n\n" : "\n\n" + new string('#', name[1] - '0') + " ",
                "td" or "th" => " ",
                "br" => "  \n",
                "div" or "p" or "ul" or "ol" or "details" or "summary" or "blockquote" or "tr"
                    or "table" or "section" or "article" or "pre" or "hr" => "\n\n",
                _ => string.Empty
            };
        });
    }

    private static Regex Pattern(string expression)
        => new(expression, RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled, TimeSpan.FromSeconds(1));
}
