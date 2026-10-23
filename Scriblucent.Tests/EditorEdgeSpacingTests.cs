using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class EditorEdgeSpacingTests
{
    [Fact]
    public void Suspending_edge_spacing_preserves_changes_to_the_marker_gutter()
    {
        RunSta(() =>
        {
            var list = new System.Windows.Documents.List(new ListItem(new Paragraph(new Run("task"))))
            { Padding = new Thickness(48, 3, 2, 4) };
            var document = new FlowDocument(list);
            var spacing = new EditorEdgeSpacing();
            spacing.Update(document);
            list.Padding = new Thickness(160, list.Padding.Top, 8, list.Padding.Bottom);
            spacing.Suspend();
            Assert.Equal(new Thickness(160, 3, 8, 4), list.Padding);
        });
    }

    [Fact]
    public void Empty_caret_uses_viewport_padding_until_text_exists()
    {
        RunSta(() =>
        {
            var paragraph = new Paragraph();
            var document = new FlowDocument(paragraph);
            var editor = new RichTextBox
            {
                Document = document,
                Width = 400,
                Height = 180,
                Padding = new Thickness(18, 0, 18, 0)
            };
            var spacing = new EditorEdgeSpacing();

            Assert.True(spacing.UpdateEditor(editor));
            Assert.Equal(new Thickness(18, 12, 18, 12), editor.Padding);
            Assert.Equal(new Thickness(0), paragraph.Padding);
            editor.Measure(new Size(400, 180));
            editor.Arrange(new Rect(0, 0, 400, 180));
            editor.UpdateLayout();
            var emptyCaret = editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);

            Assert.True(spacing.UpdateEditor(editor, useEmptyViewportPadding: false));
            editor.UpdateLayout();
            var imageReadyCaret = editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);
            Assert.Equal(new Thickness(18, 0, 18, 0), editor.Padding);
            Assert.Equal(new Thickness(0, 12, 0, 12), paragraph.Padding);
            Assert.InRange(Math.Abs(emptyCaret.Top - imageReadyCaret.Top), 0, 1);
            Assert.True(spacing.UpdateEditor(editor));

            editor.Selection.Text = "a";
            Assert.True(spacing.UpdateEditor(editor));
            editor.UpdateLayout();
            var textCaret = editor.CaretPosition.GetCharacterRect(LogicalDirection.Forward);

            Assert.Equal(new Thickness(18, 0, 18, 0), editor.Padding);
            Assert.Equal(new Thickness(0, 12, 0, 12), paragraph.Padding);
            Assert.InRange(Math.Abs(emptyCaret.Top - textCaret.Top), 0, 1);

            editor.SelectAll();
            editor.Selection.Text = string.Empty;
            Assert.True(spacing.UpdateEditor(editor));
            Assert.True(EditorEdgeSpacing.IsEmpty(document));
            Assert.Equal(new Thickness(18, 12, 18, 12), editor.Padding);
        });
    }

    [Fact]
    public void Empty_note_has_no_spacer_blocks_and_can_be_deleted()
    {
        RunSta(() =>
        {
            var paragraph = new Paragraph();
            var document = new FlowDocument(paragraph);
            var editor = new RichTextBox { Document = document };
            var spacing = new EditorEdgeSpacing();
            editor.SelectionChanged += (_, _) => EditorEdgeSpacing.CollapseEmptySelection(editor);
            var applying = false;
            editor.TextChanged += (_, _) =>
            {
                if (applying)
                    return;
                applying = true;
                try { spacing.Update(document); }
                finally { applying = false; }
            };

            spacing.Update(document);
            Assert.Single(document.Blocks.Cast<Block>());
            Assert.True(EditorEdgeSpacing.IsEmpty(document));
            Assert.Equal(EditorEdgeSpacing.PaddingDip, paragraph.Padding.Top);
            Assert.Equal(EditorEdgeSpacing.PaddingDip, paragraph.Padding.Bottom);

            editor.SelectAll();
            Assert.True(editor.Selection.IsEmpty);
            editor.Selection.Text = string.Empty;
            spacing.Update(document);

            Assert.True(EditorEdgeSpacing.IsEmpty(document));
            Assert.DoesNotContain(document.Blocks.Cast<Block>(),
                block => Equals(block.Tag, FloatingImageCoordinator.EditorEndPadTag));
        });
    }

    [Fact]
    public void Enter_and_multiline_paste_do_not_copy_edge_spacing_to_new_lines()
    {
        RunSta(() =>
        {
            var first = new Paragraph(new Run("one"));
            var document = new FlowDocument(first);
            var editor = new RichTextBox { Document = document };
            var spacing = new EditorEdgeSpacing();
            spacing.Update(document);

            editor.Selection.Select(first.ContentEnd, first.ContentEnd);
            editor.Selection.Text = "\r\ntwo\r\nthree";
            spacing.Update(document);

            var blocks = document.Blocks.Cast<Block>().ToArray();
            Assert.Equal(3, blocks.Length);
            Assert.Equal(new Thickness(0, 12, 0, 0), blocks[0].Padding);
            Assert.Equal(new Thickness(0), blocks[1].Padding);
            Assert.Equal(new Thickness(0, 0, 0, 12), blocks[2].Padding);

            spacing.Suspend();
            Assert.All(blocks, block => Assert.Equal(new Thickness(0), block.Padding));
        });
    }

    [Fact]
    public void Replacing_all_text_does_not_carry_old_editor_padding()
    {
        RunSta(() =>
        {
            var original = new Paragraph(new Run("old"));
            var document = new FlowDocument(original);
            var editor = new RichTextBox { Document = document };
            var spacing = new EditorEdgeSpacing();
            spacing.Update(document);

            editor.SelectAll();
            editor.Selection.Text = "replacement";
            spacing.Update(document);

            var replacement = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
            Assert.NotSame(original, replacement);
            Assert.Equal(new Thickness(0, 12, 0, 12), replacement.Padding);
            spacing.Suspend();
            Assert.Equal(new Thickness(0), replacement.Padding);
        });
    }

    [Fact]
    public void Splitting_the_first_paragraph_does_not_add_spacing_between_paragraphs()
    {
        RunSta(() =>
        {
            var first = new Paragraph(new Run("first"));
            var last = new Paragraph(new Run("last"));
            var document = new FlowDocument();
            document.Blocks.Add(first);
            document.Blocks.Add(last);
            var editor = new RichTextBox { Document = document };
            var spacing = new EditorEdgeSpacing();
            spacing.Update(document);

            editor.Selection.Select(first.ContentEnd, first.ContentEnd);
            editor.Selection.Text = "\r\nmiddle";
            spacing.Update(document);

            var blocks = document.Blocks.Cast<Block>().ToArray();
            Assert.Equal(3, blocks.Length);
            Assert.Equal(new Thickness(0, 12, 0, 0), blocks[0].Padding);
            Assert.Equal(new Thickness(0), blocks[1].Padding);
            Assert.Equal(new Thickness(0, 0, 0, 12), blocks[2].Padding);
        });
    }

    [Fact]
    public void Original_padding_and_legacy_cleanup_are_preserved()
    {
        RunSta(() =>
        {
            var first = new Paragraph(new Run("first"))
            {
                Padding = new Thickness(1, 3, 2, 4)
            };
            var last = new Paragraph(new Run("last"))
            {
                Padding = new Thickness(5, 6, 7, 8)
            };
            var document = new FlowDocument();
            document.Blocks.Add(new BlockUIContainer(new Border())
            {
                Tag = FloatingImageCoordinator.EditorEndPadTag
            });
            document.Blocks.Add(first);
            document.Blocks.Add(last);
            document.Blocks.Add(new BlockUIContainer(new Border())
            {
                Tag = FloatingImageCoordinator.ExtentPadTag
            });

            EditorEdgeSpacing.RemoveLegacyPads(document);
            var spacing = new EditorEdgeSpacing();
            spacing.Update(document);
            Assert.Equal(3, document.Blocks.Count);
            Assert.Equal(new Thickness(1, 15, 2, 4), first.Padding);
            Assert.Equal(new Thickness(5, 6, 7, 20), last.Padding);

            spacing.Suspend();
            Assert.Equal(new Thickness(1, 3, 2, 4), first.Padding);
            Assert.Equal(new Thickness(5, 6, 7, 8), last.Padding);
        });
    }

    [Fact]
    public void Style_padding_keeps_its_value_source_after_editor_spacing_is_suspended()
    {
        RunSta(() =>
        {
            var paragraph = new Paragraph(new Run("styled"));
            var style = new Style(typeof(Paragraph));
            style.Setters.Add(new Setter(Block.PaddingProperty, new Thickness(1, 3, 2, 4)));
            paragraph.Style = style;
            var document = new FlowDocument(paragraph);
            var spacing = new EditorEdgeSpacing();

            spacing.Update(document);
            Assert.Equal(new Thickness(1, 15, 2, 16), paragraph.Padding);
            spacing.Suspend();

            Assert.Equal(DependencyProperty.UnsetValue,
                paragraph.ReadLocalValue(Block.PaddingProperty));
            Assert.Equal(new Thickness(1, 3, 2, 4), paragraph.Padding);
        });
    }

    [Fact]
    public void Only_a_single_empty_paragraph_counts_as_an_empty_note()
    {
        RunSta(() =>
        {
            var document = new FlowDocument(new Paragraph());
            Assert.True(EditorEdgeSpacing.IsEmpty(document));

            ((Paragraph)document.Blocks.FirstBlock!).Inlines.Add(new Run(" "));
            Assert.False(EditorEdgeSpacing.IsEmpty(document));

            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph(new LineBreak()));
            Assert.False(EditorEdgeSpacing.IsEmpty(document));

            document.Blocks.Clear();
            document.Blocks.Add(new Paragraph());
            document.Blocks.Add(new Paragraph());
            Assert.False(EditorEdgeSpacing.IsEmpty(document));
        });
    }

    [Fact]
    public void Edge_spacing_scrolls_with_content()
    {
        RunSta(() =>
        {
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            for (var i = 0; i < 100; i++)
                document.Blocks.Add(new Paragraph(new Run($"Line {i}")));

            new EditorEdgeSpacing().Update(document);
            var editor = new RichTextBox
            {
                Document = document,
                Width = 400,
                Height = 180,
                Padding = new Thickness(18, 0, 18, 0)
            };
            editor.Measure(new Size(400, 180));
            editor.Arrange(new Rect(0, 0, 400, 180));
            editor.UpdateLayout();

            var first = document.Blocks.FirstBlock!.ContentStart
                .GetCharacterRect(LogicalDirection.Forward);
            var viewer = FindScrollViewer(editor)!;
            viewer.ScrollToEnd();
            editor.UpdateLayout();
            var last = document.Blocks.LastBlock!.ContentEnd
                .GetCharacterRect(LogicalDirection.Backward);

            Assert.InRange(first.Top, 12, 15);
            Assert.InRange(last.Bottom, 164, 168);
        });
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer viewer)
            return viewer;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } child)
                return child;
        }
        return null;
    }

    private static void RunSta(Action scenario)
    {
        Exception? failure = null;
        var done = new ManualResetEventSlim();
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
