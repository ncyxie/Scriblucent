using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Threading;
using Scriblucent;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class EditorParagraphSpacingTests
{
    [Fact]
    public void Rtf_paragraph_spacing_is_removed_without_changing_indentation()
    {
        RunSta(() =>
        {
            const string rtf = @"{\rtf1\ansi\deff0{\fonttbl{\f0 Calibri;}}\pard\li720\sb480\sa480\f0\fs24 First\par\pard\li720\sb480\sa480 Second\par}";
            var document = new FlowDocument();
            var range = new TextRange(document.ContentStart, document.ContentEnd);
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
            range.Load(stream, DataFormats.Rtf);

            var paragraphs = document.Blocks.OfType<Paragraph>().ToArray();
            Assert.Equal(2, paragraphs.Length);
            Assert.All(paragraphs, paragraph => Assert.True(paragraph.Margin.Top > 0));
            var originalIndents = paragraphs.Select(paragraph => paragraph.Margin.Left).ToArray();

            NoteEditorWindow.NormalizeVerticalBlockMargins(
                document.Blocks, document.ContentStart, document.ContentEnd);

            for (var i = 0; i < paragraphs.Length; i++)
            {
                Assert.Equal(0, paragraphs[i].Margin.Top);
                Assert.Equal(0, paragraphs[i].Margin.Bottom);
                Assert.Equal(originalIndents[i], paragraphs[i].Margin.Left);
            }
        });
    }

    [Fact]
    public void Spacing_reset_covers_nested_margins_in_range_only()
    {
        RunSta(() =>
        {
            var untouched = new Paragraph(new Run("Before"))
            {
                Margin = new Thickness(4, 11, 6, 13)
            };
            var paragraph = new Paragraph(new Run("Pasted"))
            {
                Margin = new Thickness(5, 15, 7, 17),
                Padding = new Thickness(2, 19, 3, 21)
            };
            var item = new ListItem
            {
                Margin = new Thickness(9, 23, 10, 25),
                Padding = new Thickness(11, 27, 12, 29)
            };
            item.Blocks.Add(paragraph);
            var list = new List
            {
                Margin = new Thickness(13, 31, 14, 33),
                Padding = new Thickness(15, 35, 16, 37)
            };
            list.ListItems.Add(item);
            var section = new Section
            {
                Margin = new Thickness(17, 39, 18, 41),
                Padding = new Thickness(19, 43, 20, 45)
            };
            section.Blocks.Add(list);

            var document = new FlowDocument();
            document.Blocks.Add(untouched);
            document.Blocks.Add(section);

            NoteEditorWindow.NormalizeVerticalBlockMargins(
                document.Blocks, paragraph.ContentStart, paragraph.ContentEnd);

            Assert.Equal(11, untouched.Margin.Top);
            Assert.Equal(13, untouched.Margin.Bottom);
            AssertVerticalMarginsCleared(section, 17, 18);
            Assert.Equal(new Thickness(19, 43, 20, 45), section.Padding);
            AssertVerticalMarginsCleared(list, 13, 14);
            Assert.Equal(new Thickness(15, 35, 16, 37), list.Padding);
            Assert.Equal(new Thickness(9, 0, 10, 0), item.Margin);
            Assert.Equal(new Thickness(11, 27, 12, 29), item.Padding);
            AssertVerticalMarginsCleared(paragraph, 5, 7);
            Assert.Equal(new Thickness(2, 19, 3, 21), paragraph.Padding);
        });
    }

    [Fact]
    public void Paste_completion_normalizes_the_inserted_rtf_range()
    {
        RunSta(() =>
        {
            var editor = new RichTextBox();
            editor.Document.Blocks.Clear();
            var before = new Paragraph(new Run("Before"))
            {
                Margin = new Thickness(0, 12, 0, 14)
            };
            var insertionParagraph = new Paragraph();
            editor.Document.Blocks.Add(before);
            editor.Document.Blocks.Add(insertionParagraph);
            editor.CaretPosition = insertionParagraph.ContentStart;

            var completed = false;
            var operation = NoteEditorWindow.QueuePastedTextSpacing(editor, (start, end) =>
            {
                completed = true;
                NoteEditorWindow.NormalizeVerticalBlockMargins(editor.Document.Blocks, start, end);
            });

            const string rtf = @"{\rtf1\ansi\pard\sb480\sa480 First\par\pard\sb480\sa480 Second\par}";
            using var stream = new MemoryStream(Encoding.ASCII.GetBytes(rtf));
            var range = new TextRange(editor.Selection.Start, editor.Selection.End);
            range.Load(stream, DataFormats.Rtf);
            editor.Selection.Select(range.End, range.End);

            var frame = new DispatcherFrame();
            editor.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);

            Assert.True(completed);
            Assert.Equal(DispatcherOperationStatus.Completed, operation.Status);
            Assert.Equal(12, before.Margin.Top);
            Assert.Equal(14, before.Margin.Bottom);
            var imported = editor.Document.Blocks.OfType<Paragraph>()
                .Where(paragraph => !ReferenceEquals(paragraph, before))
                .ToArray();
            Assert.True(imported.Length >= 2);
            Assert.All(imported, paragraph => Assert.Equal(0, paragraph.Margin.Top));
            Assert.All(imported, paragraph => Assert.Equal(0, paragraph.Margin.Bottom));
        });
    }

    [Fact]
    public void Spacing_reset_preserves_the_editors_edge_padding()
    {
        RunSta(() =>
        {
            var editor = new RichTextBox();
            editor.Document.Blocks.Clear();
            var paragraph = new Paragraph(new Run("Text"))
            {
                Margin = new Thickness(0, 18, 0, 20)
            };
            editor.Document.Blocks.Add(paragraph);
            var edgeSpacing = new EditorEdgeSpacing();
            edgeSpacing.UpdateEditor(editor);
            var editorPadding = paragraph.Padding;

            NoteEditorWindow.NormalizeVerticalBlockMargins(
                editor.Document.Blocks, editor.Document.ContentStart, editor.Document.ContentEnd);

            Assert.Equal(0, paragraph.Margin.Top);
            Assert.Equal(0, paragraph.Margin.Bottom);
            Assert.Equal(editorPadding, paragraph.Padding);
            Assert.Equal(EditorEdgeSpacing.PaddingDip, paragraph.Padding.Top);
            Assert.Equal(EditorEdgeSpacing.PaddingDip, paragraph.Padding.Bottom);
        });
    }

    private static void AssertVerticalMarginsCleared(Block block, double marginLeft, double marginRight)
    {
        Assert.Equal(new Thickness(marginLeft, 0, marginRight, 0), block.Margin);
    }

    private static void RunSta(Action scenario)
    {
        Exception? failure = null;
        using var done = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { scenario(); }
            catch (Exception ex) { failure = ex; }
            finally { done.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(done.Wait(TimeSpan.FromSeconds(30)), "STA scenario timed out");
        if (failure is not null)
            throw failure;
    }
}
