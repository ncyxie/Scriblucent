using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Tests;

public class SketchTests
{
    private static void Sta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception ex) { failure = ex; } });
        thread.SetApartmentState(ApartmentState.STA); thread.Start(); thread.Join();
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static Stroke Stroke() => new(new StylusPointCollection
    {
        new StylusPoint(30, 80, .2f), new StylusPoint(140, 80, .8f), new StylusPoint(260, 80, .4f)
    }, new DrawingAttributes { Color = Colors.CornflowerBlue, Width = 5, Height = 5, FitToCurve = true });

    [Fact]
    public void Png_retains_editable_pressure_ink_and_transparency_at_double_resolution() => Sta(() =>
    {
        var drawing = new SketchDocument { Background = null }; drawing.Strokes.Add(Stroke());
        var bytes = SketchPngCodec.Encode(drawing);
        var reopened = Assert.IsType<SketchDocument>(SketchPngCodec.TryRead(bytes));
        Assert.Equal(1280, reopened.PixelWidth); Assert.Equal(800, reopened.PixelHeight);
        Assert.Null(reopened.Background); Assert.Null(reopened.BaseImage); Assert.Single(reopened.Strokes);
        Assert.Equal(drawing.Strokes[0].StylusPoints[1].PressureFactor, reopened.Strokes[0].StylusPoints[1].PressureFactor, .01);
        Assert.Equal(drawing.Strokes[0].DrawingAttributes.Color, reopened.Strokes[0].DrawingAttributes.Color);
        using var stream = new MemoryStream(bytes);
        var bitmap = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var pixel = new byte[4]; bitmap.CopyPixels(new Int32Rect(0, 0, 1, 1), pixel, 4, 0);
        Assert.Equal(0, pixel[3]);
    });

    [Fact]
    public void Repainting_keeps_one_original_image_and_its_resolution() => Sta(() =>
    {
        var original = new SketchDocument { PixelWidth = 320, PixelHeight = 640, Width = 200, Height = 400 };
        var png = SketchPngCodec.Encode(original);
        // Re-encode just its visible image to model a legacy drawing without sketch metadata.
        using var stream = new MemoryStream(png);
        var source = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source, null, null, null));
        using var flattened = new MemoryStream(); encoder.Save(flattened); var baseBytes = flattened.ToArray();
        var first = SketchPngCodec.Open(baseBytes); Assert.Equal(baseBytes, first.BaseImage);
        Assert.Equal(320, first.PixelWidth); Assert.Equal(640, first.PixelHeight);
        first.Strokes.Add(Stroke());
        var second = SketchPngCodec.Open(SketchPngCodec.Encode(first));
        second.Strokes.Add(Stroke());
        var third = SketchPngCodec.Open(SketchPngCodec.Encode(second));
        Assert.Equal(baseBytes, third.BaseImage); Assert.Equal(2, third.Strokes.Count);
        Assert.Equal(320, third.PixelWidth); Assert.Equal(640, third.PixelHeight);
        Assert.NotNull(SketchPngCodec.DecodeBase(third));
    });

    [Fact]
    public void Corrupt_metadata_falls_back_to_the_visible_picture() => Sta(() =>
    {
        var pixels = new byte[16];
        var bitmap = BitmapSource.Create(2, 2, 96, 96, PixelFormats.Pbgra32, null, pixels, 8);
        var metadata = new BitmapMetadata("png"); metadata.SetQuery("/tEXt/{str=ScriblucentSketch}", "{not valid}");
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var stream = new MemoryStream(); encoder.Save(stream);
        var bytes = stream.ToArray(); Assert.Null(SketchPngCodec.TryRead(bytes));
        var drawing = SketchPngCodec.Open(bytes); Assert.Equal(bytes, drawing.BaseImage); Assert.Empty(drawing.Strokes);
    });

    [Fact]
    public void History_reverses_point_erasing_clear_background_and_branching_as_actions() => Sta(() =>
    {
        var drawing = new SketchDocument(); var history = new SketchHistory(drawing);
        history.Begin(); drawing.Strokes.Add(Stroke()); history.Commit();
        var original = drawing.Strokes[0];
        var fragments = original.GetEraseResult(new[] { new Point(140, 80) }, new EllipseStylusShape(20, 20));
        Assert.True(fragments.Count >= 2);
        history.Begin(); drawing.Strokes.Remove(original); drawing.Strokes.Add(fragments); history.Commit();
        history.Undo(); Assert.Same(original, Assert.Single(drawing.Strokes));
        history.Redo(); Assert.Equal(fragments.Count, drawing.Strokes.Count);
        history.Begin(); drawing.Strokes.Clear(); history.Commit();
        history.Undo(); Assert.Equal(fragments.Count, drawing.Strokes.Count);
        history.Begin(); drawing.Background = null; history.Commit();
        history.Undo(); Assert.Equal(Colors.White, drawing.Background); Assert.True(history.CanRedo);
        history.Begin(); drawing.Background = Colors.Black; history.Commit(); Assert.False(history.CanRedo);
        while (history.CanUndo) history.Undo();
        Assert.False(history.IsChanged); Assert.Empty(drawing.Strokes); Assert.Equal(Colors.White, drawing.Background);
        history.Begin(); history.Commit(); Assert.False(history.IsChanged);
    });

    [Fact]
    public void Shapes_are_erasable_ink_and_an_arrow_is_one_history_action() => Sta(() =>
    {
        var drawing = new SketchDocument(); var history = new SketchHistory(drawing);
        var attributes = new DrawingAttributes { Width = 3, Height = 3 };
        history.Begin(); drawing.Strokes.Add(SketchShapes.Create(SketchTool.Arrow, new Point(20, 20), new Point(200, 50), attributes, true)); history.Commit();
        Assert.Equal(2, drawing.Strokes.Count);
        var end = drawing.Strokes[0].StylusPoints[^1]; Assert.Equal(20, end.Y, 4);
        history.Undo(); Assert.Empty(drawing.Strokes); history.Redo(); Assert.Equal(2, drawing.Strokes.Count);
        var rectangle = SketchShapes.Create(SketchTool.Rectangle, new Point(10, 10), new Point(80, 40), attributes, true);
        Assert.Equal(rectangle[0].GetBounds().Width, rectangle[0].GetBounds().Height, 4);
        var ellipse = SketchShapes.Create(SketchTool.Ellipse, new Point(10, 10), new Point(80, 40), attributes, false);
        Assert.True(ellipse[0].StylusPoints.Count > 10); Assert.False(ellipse[0].DrawingAttributes.FitToCurve);
    });

    [Fact]
    public void Native_note_export_import_keeps_editable_drawing_bytes() => Sta(() =>
    {
        var drawing = new SketchDocument(); drawing.Strokes.Add(Stroke());
        var bytes = SketchPngCodec.Encode(drawing);
        var path = Path.Combine(Path.GetTempPath(), "scriblucent-sketch-" + Guid.NewGuid().ToString("N") + ".scribnote");
        try
        {
            NoteFileService.SaveNotePackage(path, new NoteDocument { Images = [new NoteImageAttachment { Data = Convert.ToBase64String(bytes), Width = 320, Height = 200 }] });
            var restored = NoteFileService.ReadNotePackage(path);
            var restoredBytes = Convert.FromBase64String(Assert.Single(restored.Images).Data);
            Assert.Equal(bytes, restoredBytes); Assert.Single(SketchPngCodec.Open(restoredBytes).Strokes);
        }
        finally { File.Delete(path); }
    });

    [Fact]
    public void Long_drawing_encodes_off_thread_and_reopens_without_changing_pressure() => Sta(() =>
    {
        var drawing = new SketchDocument { Background = Colors.Ivory };
        for (var i = 0; i < 500; i++)
            drawing.Strokes.Add(new Stroke(new StylusPointCollection(Enumerable.Range(0, 80)
                .Select(j => new StylusPoint(20 + j * 7, 20 + i % 350, .2f + j % 5 * .15f))),
                new DrawingAttributes { Width = 2, Height = 2, Color = i % 2 == 0 ? Colors.SteelBlue : Colors.DarkSlateBlue }));
        var bytes = SketchPngCodec.EncodeAsync(drawing).GetAwaiter().GetResult();
        var reopened = SketchPngCodec.Open(bytes);
        Assert.Equal(500, reopened.Strokes.Count);
        Assert.Equal(80, reopened.Strokes[10].StylusPoints.Count);
        Assert.Equal(Colors.Ivory, reopened.Background);
        var history = new SketchHistory(reopened);
        history.Begin(); reopened.Strokes.Clear(); history.Commit(); history.Undo();
        Assert.Equal(500, reopened.Strokes.Count); Assert.False(history.IsChanged);
    });
}
