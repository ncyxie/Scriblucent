using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Controls;
using Scriblucent.Models;
using Scriblucent.Rendering;
using Scriblucent.Services;
using Scriblucent.ViewModels;
using SkiaSharp;

namespace Scriblucent.Tests;

[Collection("Layout reveal")]
public class NotePreviewTests
{
    public static IEnumerable<object[]> FormatsAndModes()
    {
        foreach (var format in new[] { "plain", "rtf", "package" })
            foreach (var mode in Enum.GetValues<NoteImageWrapMode>())
                foreach (var outside in new[] { false, true }) yield return [format, mode, outside];
    }

    [Theory, MemberData(nameof(FormatsAndModes))]
    public void Images_are_detected_only_at_their_excerpt_positions(string format, NoteImageWrapMode mode, bool outside) => Sta(() =>
    {
        var text = "Opening text about a picture. " + (outside ? new string('x', 800) : "");
        if (mode == NoteImageWrapMode.Inline) text += "\uFFFC after";
        var note = Note(Encode(text, format), Image(mode, outside ? 5000 : 0));
        var preview = NotePreviewService.Build(note);
        Assert.Equal(outside ? 0 : 1, Count(preview));
        Assert.DoesNotContain("[[ScriblucentImage:", preview.Text);
    });

    [Theory]
    [InlineData("plain")][InlineData("rtf")][InlineData("package")]
    public void Multiple_photos_are_counted_without_duplicates_and_adjacent_ones_form_one_chip(string format) => Sta(() =>
    {
        var first = Image(NoteImageWrapMode.None, 0);
        var note = Note(Encode("Opening with two photos.", format), first, first, Image(NoteImageWrapMode.Square, 0));
        var preview = NotePreviewService.Build(note);
        Assert.Equal(2, Count(preview));
        Assert.Equal(2, Assert.Single(preview.Parts, p => p.ImageCount > 0).ImageCount);
    });

    [Fact]
    public void Photo_only_notes_signal_all_modes_even_below_empty_space() => Sta(() =>
    {
        var note = Note("", Enum.GetValues<NoteImageWrapMode>().Select(mode => Image(mode, 5000)).ToArray());
        Assert.Equal(4, Count(NotePreviewService.Build(note)));
    });

    [Fact]
    public void Legacy_and_embedded_photos_are_detected_but_checkboxes_and_spacers_are_not() => Sta(() =>
    {
        var legacy = Note("Before [[ScriblucentImage:abc]] after");
        Assert.Equal("Before \uFFFC after", NotePreviewService.Build(legacy).Text);
        var paragraph = new Paragraph(new Run("Before "));
        paragraph.Inlines.Add(new InlineUIContainer(new CheckBox()));
        paragraph.Inlines.Add(new InlineUIContainer(new Border { Width = 100, Height = 30 }));
        paragraph.Inlines.Add(new InlineUIContainer(new System.Windows.Controls.Image { Width = 40, Height = 30 }));
        paragraph.Inlines.Add(new Run(" after"));
        var flow = new FlowDocument(paragraph);
        Assert.Equal(1, Count(NotePreviewService.Capture(flow, [], null, 720)));
    });

    [Fact]
    public void Separated_photos_keep_their_text_positions_and_do_not_collapse_together() => Sta(() =>
    {
        var preview = NotePreviewService.Build(Note("Before \uFFFC between \uFFFC after",
            Image(NoteImageWrapMode.Inline, 0), Image(NoteImageWrapMode.Inline, 0)));
        Assert.Equal(2, preview.Parts.Count(p => p.ImageCount == 1));
        Assert.Equal("Before \uFFFC between \uFFFC after", preview.Text);
    });

    [Theory]
    [InlineData(-10, 1)][InlineData(-100, 0)][InlineData(5000, 0)]
    public void Geometry_uses_intersections_rather_than_just_image_top(double top, int expected) => Sta(() =>
    {
        Assert.Equal(expected, Count(NotePreviewService.Build(Note("Opening.", Image(NoteImageWrapMode.None, top)))));
    });

    [Theory]
    [InlineData(double.NaN)][InlineData(double.PositiveInfinity)][InlineData(0)]
    public void Invalid_dimensions_do_not_create_a_photo_indicator(double width) => Sta(() =>
    {
        var image = Image(NoteImageWrapMode.None, 0); image.Width = width;
        Assert.Equal(0, Count(NotePreviewService.Build(Note("Opening.", image))));
    });

    [Fact]
    public void Large_fonts_paragraphs_and_line_breaks_affect_the_actual_source_extent() => Sta(() =>
    {
        var flow = new FlowDocument { FontSize = 48 };
        var paragraph = new Paragraph(new Run("Large heading")) { Margin = new Thickness(0) };
        paragraph.Inlines.Add(new LineBreak()); paragraph.Inlines.Add(new Run("Another line"));
        flow.Blocks.Add(paragraph); flow.Blocks.Add(new Paragraph(new Run("Third line")) { Margin = new Thickness(0) });
        var note = Note(Encode(flow), Image(NoteImageWrapMode.None, 100)); note.FontSize = 48;
        Assert.Equal(1, Count(NotePreviewService.Build(note)));
    });

    [Theory]
    [InlineData(true, 1)][InlineData(false, 0)]
    public void Source_word_wrap_changes_which_floating_images_belong_to_the_excerpt(bool wrap, int expected) => Sta(() =>
    {
        var note = Note(string.Join(" ", Enumerable.Repeat("Opening words", 15)), Image(NoteImageWrapMode.None, 60));
        note.PreviewLayoutWidth = 200; note.IsWordWrapEnabled = wrap;
        Assert.Equal(expected, Count(NotePreviewService.Build(note)));
    });

    [Fact]
    public void Table_cells_in_one_row_do_not_invent_extra_vertical_space() => Sta(() =>
    {
        var table = new Table { CellSpacing = 0 };
        var group = new TableRowGroup(); var row = new TableRow();
        row.Cells.Add(new TableCell(new Paragraph(new Run("First cell")) { Margin = new Thickness(0) }) { Padding = new Thickness(0) });
        row.Cells.Add(new TableCell(new Paragraph(new Run("Second cell")) { Margin = new Thickness(0) }) { Padding = new Thickness(0) });
        group.Rows.Add(row); table.RowGroups.Add(group);
        var note = Note(Encode(new FlowDocument(table)), Image(NoteImageWrapMode.None, 30));
        Assert.Equal(0, Count(NotePreviewService.Build(note)));
    });

    [Fact]
    public void Legacy_markers_split_by_formatting_and_attachment_references_are_deduplicated() => Sta(() =>
    {
        var image = Image(NoteImageWrapMode.None, 0);
        var paragraph = new Paragraph(new Run("Before [[Scriblucent"));
        paragraph.Inlines.Add(new Bold(new Run($"Image:{image.Id}]]"))); paragraph.Inlines.Add(new Run(" after"));
        var note = Note(Encode(new FlowDocument(paragraph)), image);
        var preview = NotePreviewService.Build(note);
        Assert.Equal(1, Count(preview)); Assert.DoesNotContain("ScriblucentImage", preview.Text);
        var plain = Note($"Before [[ScriblucentImage:{image.Id}]] after", image);
        Assert.Equal(1, Count(NotePreviewService.Build(plain)));
    });

    [Theory]
    [InlineData(1)][InlineData(1.5)][InlineData(2)]
    public void Card_tile_keeps_its_DIP_dimensions_at_high_scaling(double dpi) => Sta(() =>
    {
        var vm = new NoteCardViewModel(Note("Before \uFFFC after.", Image(NoteImageWrapMode.Inline, 0)), _ => { });
        vm.WarmContentPreview();
        using var tile = SkiaCardRenderer.Render(vm, dpi, new CardRenderColors(SKColors.White, SKColors.Gray,
            SKColors.Black, SKColors.Black, SKColors.LightGray, SKColors.Blue), out var height);
        Assert.Equal(164, height, 5);
        Assert.Equal((int)Math.Ceiling(164 * dpi), tile.Height);
        Assert.Equal((int)Math.Ceiling(244 * dpi), tile.Width);
        var layout = NotePreviewRaster.Layout(vm.Preview, (float)(190 * dpi), 3, (float)dpi);
        Assert.Equal(layout.Lines.Sum(l => l.Height), layout.Height, 3);
    });

    [Fact]
    public void Image_touching_the_bottom_of_the_excerpt_is_excluded() => Sta(() =>
    {
        var flow = new FlowDocument(new Paragraph(new Run("Opening.")) { Margin = new Thickness(0) }) { FontSize = 14 };
        var editor = new RichTextBox(flow) { Padding = new Thickness(0), BorderThickness = new Thickness(0) };
        var window = new Window { Content = editor, Width = 500, Height = 300, ShowInTaskbar = false, Opacity = 0 };
        window.Show(); editor.UpdateLayout();
        try
        {
            var rect = flow.ContentStart.GetNextInsertionPosition(LogicalDirection.Forward)!.GetCharacterRect(LogicalDirection.Forward);
            var image = Image(NoteImageWrapMode.None, rect.Bottom);
            Assert.Equal(0, Count(NotePreviewService.Capture(flow, [image], editor, 500)));
            image.Top -= .1;
            Assert.Equal(1, Count(NotePreviewService.Capture(flow, [image], editor, 500)));
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Save_snapshot_is_scroll_independent_and_does_not_change_selection_or_flow() => Sta(() =>
    {
        var flow = new FlowDocument();
        for (var i = 0; i < 150; i++) flow.Blocks.Add(new Paragraph(new Run("Paragraph " + i)) { Margin = new Thickness(0) });
        var editor = new RichTextBox(flow) { Padding = new Thickness(0), BorderThickness = new Thickness(0) };
        var window = new Window { Content = editor, Width = 500, Height = 300, ShowInTaskbar = false, Opacity = 0 };
        window.Show(); editor.UpdateLayout();
        try
        {
            var image = Image(NoteImageWrapMode.None, 30);
            var before = new TextRange(flow.ContentStart, flow.ContentEnd).Text;
            var first = NotePreviewService.Capture(flow, [image], editor, 500);
            editor.ScrollToVerticalOffset(500); editor.UpdateLayout();
            var offset = editor.VerticalOffset; var caret = editor.CaretPosition;
            var second = NotePreviewService.Capture(flow, [image], editor, 500, new Point(0, offset));
            Assert.Equal(first.Text, second.Text);
            Assert.Equal(before, new TextRange(flow.ContentStart, flow.ContentEnd).Text);
            Assert.Same(caret, editor.CaretPosition); Assert.Equal(offset, editor.VerticalOffset);
        }
        finally { window.Close(); }
    });

    [Fact]
    public void Stale_caches_are_rebuilt_and_snapshot_reuse_survives_roundtrip() => Sta(() =>
    {
        var note = Note("Opening", Image(NoteImageWrapMode.None, 0));
        note.SearchablePreview = "stale text";
        var originalDate = note.LastModified;
        var vm = new NoteCardViewModel(note, _ => { });
        Assert.Equal(1, vm.Preview.Parts.Sum(p => p.ImageCount));
        var stored = JsonSerializer.Deserialize<NoteDocument>(JsonSerializer.Serialize(note))!;
        Assert.Null(note.SearchablePreview);
        Assert.DoesNotContain("SearchablePreview", JsonSerializer.Serialize(note));
        var cached = stored.CardPreview;
        Assert.Same(cached, NotePreviewService.Build(stored));
        stored.Images[0].Top = 5000;
        Assert.Equal(0, Count(new NoteCardViewModel(stored, _ => { }).Preview));
        stored.Content = "Changed"; // same length as Opening, must invalidate too.
        Assert.Contains("Changed", NotePreviewService.Build(stored).Text);
        Assert.Equal(originalDate, note.LastModified);
        note.Images[0].Top = 5000; vm.NotifyContentChanged();
        Assert.Equal(0, Count(vm.Preview));
    });

    [Fact]
    public void Collapsed_whitespace_does_not_skip_later_text_and_images_in_a_long_run() => Sta(() =>
    {
        var text = string.Join("    ", Enumerable.Repeat("word", 90)) + " \uFFFC after";
        var preview = NotePreviewService.Build(Note(text, Image(NoteImageWrapMode.Inline, 0)));
        Assert.Equal(1, Count(preview)); Assert.EndsWith("after", preview.Text);
        var padded = NotePreviewService.Build(Note(new string(' ', 900) + "Visible text after leading space", Image(NoteImageWrapMode.None, 5000)));
        Assert.Equal("Visible text after leading space", padded.Text);
    });

    [Fact]
    public void Changing_media_storage_does_not_invalidate_image_associations() => Sta(() =>
    {
        var note = Note("Opening", Image(NoteImageWrapMode.None, 0));
        note.CardPreview = NotePreviewService.Build(note);
        var snapshot = note.CardPreview;
        note.Images[0].Data = "inline-bytes"; note.Images[0].DataFilePath = "picture.png";
        Assert.Same(snapshot, NotePreviewService.Build(note));
    });

    [Fact]
    public void Tall_inline_images_reserve_source_space_for_nearby_floating_images() => Sta(() =>
    {
        var inline = Image(NoteImageWrapMode.Inline, 0); inline.Height = 200;
        var preview = NotePreviewService.Build(Note("Before \uFFFC after.", inline, Image(NoteImageWrapMode.None, 120)));
        Assert.Equal(2, Count(preview));
    });

    [Fact]
    public void Duplicates_share_valid_derived_content_and_keep_independent_note_state() => Sta(() =>
    {
        var source = new NoteCardViewModel(Note("Opening", Image(NoteImageWrapMode.None, 0)), _ => { });
        source.WarmContentPreview();
        var copy = new NoteCardViewModel(new NoteDocument { Content = source.Document.Content,
            Images = MainViewModel.CloneImageAttachments(source.Document.Images) }, _ => { });
        copy.CopyCachedContentPreviewFrom(source);
        Assert.Same(source.Preview, copy.Preview);
        copy.Document.Content = "New body"; copy.NotifyContentChanged();
        Assert.Contains("Opening", source.Content); Assert.Contains("New body", copy.Content);
    });

    [Theory]
    [InlineData("UEsDB broken")][InlineData("e1xydGY broken")]
    public void Damaged_encoded_notes_do_not_show_raw_payload_or_crash(string raw) => Sta(() =>
        Assert.Equal(string.Empty, NotePreviewService.Build(Note(raw)).Text));

    [Theory]
    [InlineData(1)][InlineData(1.5)][InlineData(2)]
    public void Chips_are_atomic_and_fit_the_same_layout_at_all_scalings(double dpi) => Sta(() =>
    {
        var preview = new NotePreviewSnapshot { Text = "An image \uFFFC\uFFFC and some text, then \uFFFC after." };
        var scale = (float)dpi;
        var layout = NotePreviewRaster.Layout(preview, 190 * scale, 2, scale);
        Assert.True(layout.Lines.Count <= 2);
        Assert.All(layout.Lines, line =>
        {
            Assert.True(line.Width <= 190 * scale + .1);
            Assert.All(line.Runs.Where(r => r.ImageLabel is not null), run => Assert.True(run.Width >= 28 * scale));
        });
        var narrow = NotePreviewRaster.Layout(new NotePreviewSnapshot { Text = "\uFFFC" }, 20 * scale, 1, scale);
        Assert.DoesNotContain(narrow.Lines.SelectMany(l => l.Runs), r => r.ImageLabel is not null);
    });

    private static int Count(NotePreviewSnapshot preview) => preview.Text.Count(c => c == '\uFFFC');
    internal static NoteDocument Note(string content, params NoteImageAttachment[] images) => new()
    { Content = content, Images = images.ToList(), FontSize = 14, PreviewLayoutWidth = 720 };
    internal static NoteImageAttachment Image(NoteImageWrapMode mode, double top) => new()
    { WrapMode = mode, Width = 100, Height = 60, Top = top };
    private static string Encode(string text, string format)
    {
        if (format == "plain") return text;
        var flow = new FlowDocument();
        foreach (var line in text.Split('\n')) flow.Blocks.Add(new Paragraph(new Run(line)) { Margin = new Thickness(0) });
        return Encode(flow, format);
    }
    private static string Encode(FlowDocument flow, string format = "package")
    {
        using var stream = new MemoryStream();
        new TextRange(flow.ContentStart, flow.ContentEnd).Save(stream, format == "rtf" ? DataFormats.Rtf : DataFormats.XamlPackage);
        return Convert.ToBase64String(stream.ToArray());
    }
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(30)));
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
