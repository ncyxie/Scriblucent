using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class EditorPasteTests
{
    [Fact]
    public void Plain_text_is_not_formatted()
    {
        var data = new DataObject();
        data.SetData(DataFormats.UnicodeText, "Just words");
        data.SetData(DataFormats.Text, "Just words");
        Assert.False(EditorPaste.HasRichFormatting(data));
        Assert.Equal(EditorPaste.PasteAction.PlainText, EditorPaste.Resolve(EditorPasteMode.KeepFormatting, data));
        Assert.True(EditorPaste.ApplyNoteLineSpacing(EditorPasteMode.TextOnly, data));
    }

    [Fact]
    public void Rtf_and_html_count_as_formatted()
    {
        var rtf = new DataObject();
        rtf.SetData(DataFormats.Rtf, "{\\rtf1\\pard Hello\\par}");
        rtf.SetData(DataFormats.UnicodeText, "Hello");
        Assert.True(EditorPaste.HasRichFormatting(rtf));

        var html = new DataObject();
        html.SetData(DataFormats.Html, "<html><body><b>Hello</b></body></html>");
        html.SetData(DataFormats.UnicodeText, "Hello");
        Assert.True(EditorPaste.HasRichFormatting(html));
        Assert.False(EditorPaste.ApplyNoteLineSpacing(EditorPasteMode.KeepFormatting, rtf));
        Assert.True(EditorPaste.ApplyNoteLineSpacing(EditorPasteMode.TextOnly, rtf));
    }

    [Fact]
    public void Text_only_paste_drops_the_source_font_and_keep_formatting_leaves_source_spacing() => RunSta(() =>
    {
        var source = new RichTextBox();
        var paragraph = new Paragraph(new Run("Spaced"))
        {
            FontFamily = new FontFamily("Arial"),
            FontSize = 22,
            LineStackingStrategy = LineStackingStrategy.MaxHeight,
            LineHeight = 48
        };
        source.Document.Blocks.Clear();
        source.Document.Blocks.Add(paragraph);
        using var stream = new MemoryStream();
        new TextRange(source.Document.ContentStart, source.Document.ContentEnd).Save(stream, DataFormats.Rtf);
        var data = new DataObject();
        data.SetData(DataFormats.Rtf, Encoding.ASCII.GetString(stream.ToArray()));
        data.SetData(DataFormats.UnicodeText, "Spaced");

        var formatted = new RichTextBox { FontFamily = new FontFamily("Calibri"), FontSize = 14 };
        formatted.Document.Blocks.Clear();
        formatted.Document.Blocks.Add(new Paragraph());
        formatted.CaretPosition = formatted.Document.ContentStart;
        Assert.True(EditorPaste.TryPasteRich(formatted, data));
        var pasted = formatted.Document.Blocks.OfType<Paragraph>()
            .First(item => new TextRange(item.ContentStart, item.ContentEnd).Text.Contains("Spaced", StringComparison.Ordinal));
        Assert.Contains("Arial", pasted.FontFamily.Source, StringComparison.OrdinalIgnoreCase);
        Assert.True(Math.Abs(pasted.FontSize - 22) < 1 || pasted.Inlines.OfType<Run>().Any(run => Math.Abs(run.FontSize - 22) < 1));
        Assert.True(double.IsNaN(pasted.LineHeight) || pasted.LineHeight > 30,
            "Keep formatting must not rewrite the fragment to the note's 14px line.");

        var plain = new RichTextBox { FontFamily = new FontFamily("Calibri"), FontSize = 14 };
        plain.Document.Blocks.Clear();
        plain.Document.Blocks.Add(new Paragraph());
        plain.CaretPosition = plain.Document.ContentStart;
        plain.Selection.Text = EditorPaste.PlainText(data);
        var plainParagraph = plain.Document.Blocks.OfType<Paragraph>().First();
        Assert.DoesNotContain("Arial", plainParagraph.FontFamily.Source, StringComparison.OrdinalIgnoreCase);
        Assert.True(plainParagraph.Inlines.OfType<Run>().All(run =>
            run.ReadLocalValue(TextElement.FontFamilyProperty) == DependencyProperty.UnsetValue
            || !run.FontFamily.Source.Contains("Arial", StringComparison.OrdinalIgnoreCase)));
    });

    private static void RunSta(Action scenario)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)), "STA paste scenario timed out");
        if (failure is not null)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
