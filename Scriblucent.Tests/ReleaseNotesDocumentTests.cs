using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Documents;
using Scriblucent.Services;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class ReleaseNotesDocumentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData(" \r\n ")]
    [InlineData("<div><img src='logo.svg' width='240'></div>")]
    [InlineData("![Preview](https://example.com/image.png)")]
    [InlineData("<!-- hidden --><style>p {color:red}</style><script>hidden()</script>")]
    public void Empty_or_decorative_notes_do_not_create_a_visible_document(string? body) => Sta(() =>
        Assert.Empty(ReleaseNotesDocumentBuilder.Build(body).Blocks));

    [Fact]
    public void Markdown_preserves_headings_bold_italics_lists_code_and_links() => Sta(() =>
    {
        var document = ReleaseNotesDocumentBuilder.Build("""
            ## What's new

            **Smoother** writing with *comfortable* planning and `Save`.

            - **Notes** have [details](https://example.com/release(v2)).
            - Quizzes improved.
            """);
        Assert.Equal(FontWeights.SemiBold, ((Paragraph)document.Blocks.FirstBlock).FontWeight);
        Assert.Equal(FontWeights.Bold, Runs(document).Single(run => run.Text == "Smoother").FontWeight);
        Assert.Equal(FontStyles.Italic, Runs(document).Single(run => run.Text == "comfortable").FontStyle);
        Assert.Equal("Consolas", Runs(document).Single(run => run.Text == "Save").FontFamily.Source);
        Assert.Equal(2, document.Blocks.OfType<System.Windows.Documents.List>().Single().ListItems.Count);
        Assert.Equal("https://example.com/release(v2)", Elements<Hyperlink>(document).Single().NavigateUri.AbsoluteUri);
        Assert.DoesNotContain("**", Text(document));
    });

    [Theory]
    [InlineData("<p><strong>Bold</strong> and <em>italic</em>.</p>")]
    [InlineData("Text with <strong>Bold</strong> and <em>italic</em>.")]
    public void Html_block_and_inline_formatting_use_native_text_elements(string source) => Sta(() =>
    {
        var document = ReleaseNotesDocumentBuilder.Build(source);
        Assert.Equal(FontWeights.Bold, Runs(document).Single(run => run.Text == "Bold").FontWeight);
        Assert.Equal(FontStyles.Italic, Runs(document).Single(run => run.Text == "italic").FontStyle);
        Assert.DoesNotContain('<', Text(document));
    });

    [Fact]
    public void Pipe_tables_preserve_headers_and_all_cells() => Sta(() =>
    {
        var document = ReleaseNotesDocumentBuilder.Build("| Tool | Changes |\n| --- | --- |\n| **Notes** | Smoother typing |\n| Quizzes | Better feedback |");
        var table = Assert.Single(document.Blocks.OfType<Table>());
        Assert.Equal(3, table.RowGroups.Single().Rows.Count);
        Assert.All(table.RowGroups.Single().Rows.First().Cells, cell => Assert.Equal(FontWeights.SemiBold, cell.FontWeight));
        Assert.Contains("Smoother typing", Text(document));
        Assert.Contains("Better feedback", Text(document));
    });

    [Fact]
    public void Nested_and_numbered_lists_preserve_their_structure() => Sta(() =>
    {
        var document = ReleaseNotesDocumentBuilder.Build("3. First step\n4. Next step\n    - Nested detail");
        var ordered = Assert.Single(document.Blocks.OfType<System.Windows.Documents.List>());
        Assert.Equal(TextMarkerStyle.Decimal, ordered.MarkerStyle);
        Assert.Equal(3, ordered.StartIndex);
        Assert.Equal(2, ordered.ListItems.Count);
        Assert.Equal(2, Elements<System.Windows.Documents.List>(document).Count());
        Assert.Contains("Nested detail", Text(document));
    });

    [Fact]
    public void Escaped_markup_and_code_examples_remain_readable() => Sta(() =>
    {
        var document = ReleaseNotesDocumentBuilder.Build("Use &lt;file&gt; &amp; save.\n\n`<strong>literal</strong>`\n\n```html\n<div>Code example</div>\n```");
        var text = Text(document);
        Assert.Contains("Use <file> & save.", text);
        Assert.Contains("<strong>literal</strong>", text);
        Assert.Contains("<div>Code example</div>", text);
    });

    [Fact]
    public void Incomplete_html_remains_inert_without_reentering_the_html_renderer() => Sta(() =>
        Assert.Contains("unfinished", Text(ReleaseNotesDocumentBuilder.Build("<div unfinished"))));

    [Fact]
    public void Only_web_links_are_interactive_and_no_remote_artwork_is_created() => Sta(() =>
    {
        var document = ReleaseNotesDocumentBuilder.Build("[Release](https://example.com) [Local](file:///C:/private.txt) [Other](custom:open) ![Art](https://example.com/art.png)");
        Assert.Single(Elements<Hyperlink>(document));
        Assert.Empty(Elements<InlineUIContainer>(document));
        Assert.Empty(Elements<BlockUIContainer>(document));
        Assert.Contains("Local", Text(document));
        Assert.Contains("Other", Text(document));
        Assert.DoesNotContain("Art", Text(document));
    });

    [Fact]
    public void Long_changelogs_keep_every_entry_and_the_final_section() => Sta(() =>
    {
        var entries = Enumerable.Range(1, 200).Select(index => $"Improvement {index}: smoother writing and planning.").ToArray();
        var body = string.Join('\n', entries.Select(entry => $"- **{entry}**"))
            + "\n\n<h3>Final section</h3><p>All changes included.</p>";
        var document = ReleaseNotesDocumentBuilder.Build(body);
        var text = Text(document);
        foreach (var entry in entries) Assert.Contains(entry, text);
        Assert.EndsWith("All changes included.", text.TrimEnd());
        Assert.DoesNotContain("...", text);
    });

    [Fact]
    public void Current_release_changelog_renders_without_page_markup() => Sta(() =>
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Production", "CHANGELOG.md"));
        var document = ReleaseNotesDocumentBuilder.Build(body);
        var text = Text(document);
        Assert.Contains("Write. Recite. Get it right.", text);
        Assert.Contains("Four tools, one connected workspace", text);
        Assert.Contains("Scriblucent builds on the original NoteCards project", text);
        Assert.Contains("Explore Scriblucent", text);
        Assert.Single(Elements<Table>(document));
        Assert.DoesNotContain("<div", text);
        Assert.DoesNotContain("<picture", text);
        Assert.DoesNotContain("srcset", text);
        Assert.DoesNotContain("**", text);
    });

    private static string Text(FlowDocument document) => new TextRange(document.ContentStart, document.ContentEnd).Text;
    private static IEnumerable<Run> Runs(FlowDocument document) => Elements<Run>(document);
    private static IEnumerable<T> Elements<T>(DependencyObject parent) where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(parent).OfType<DependencyObject>())
        {
            if (child is T element) yield return element;
            foreach (var descendant in Elements<T>(child)) yield return descendant;
        }
    }

    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
