using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Scriblucent.Models;

namespace Scriblucent.Services;

internal static class SketchPngCodec
{
    private const string Query = "/tEXt/{str=ScriblucentSketch}";
    private const int MaxPayloadCharacters = 64 * 1024 * 1024;
    private sealed record Payload(int Version, double Width, double Height, int PixelWidth, int PixelHeight,
        string? Background, string Ink, string? BaseImage);

    public static SketchDocument Open(byte[] bytes)
    {
        var document = TryRead(bytes);
        if (document is not null) return document;
        using var stream = new MemoryStream(bytes, false);
        var source = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
        var factor = 640d / Math.Max(source.PixelWidth, source.PixelHeight);
        return new SketchDocument
        {
            Width = source.PixelWidth * factor, Height = source.PixelHeight * factor,
            PixelWidth = source.PixelWidth, PixelHeight = source.PixelHeight,
            BaseImage = (byte[])bytes.Clone(), Background = null
        };
    }

    public static SketchDocument? TryRead(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.OnLoad).Frames[0];
            if (frame.Metadata is not BitmapMetadata metadata || metadata.GetQuery(Query) is not string json
                || json.Length > MaxPayloadCharacters) return null;
            var data = JsonSerializer.Deserialize<Payload>(json);
            if (data is not { Version: 1 } || !double.IsFinite(data.Width) || !double.IsFinite(data.Height)
                || data.Width <= 0 || data.Height <= 0 || data.Width > 8192 || data.Height > 8192
                || data.PixelWidth != frame.PixelWidth || data.PixelHeight != frame.PixelHeight) return null;
            using var ink = new MemoryStream(Convert.FromBase64String(data.Ink), false);
            var result = new SketchDocument
            {
                Width = data.Width, Height = data.Height, PixelWidth = data.PixelWidth, PixelHeight = data.PixelHeight,
                Background = data.Background is null ? null : (Color)ColorConverter.ConvertFromString(data.Background),
                BaseImage = data.BaseImage is null ? null : Convert.FromBase64String(data.BaseImage)
            };
            if (result.BaseImage is { } source && !NoteImageLoader.TryGetNaturalSize(source, out _, out _)) return null;
            result.Strokes.Add(new StrokeCollection(ink));
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
    }

    public static BitmapSource? DecodeBase(SketchDocument document)
    {
        if (document.BaseImage is not { Length: > 0 } bytes) return null;
        var bitmap = new BitmapImage();
        using var stream = new MemoryStream(bytes, false);
        bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        bitmap.DecodePixelWidth = Math.Min(document.PixelWidth, 2048);
        bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
        return bitmap;
    }

    public static byte[] Encode(SketchDocument document)
    {
        if ((long)document.PixelWidth * document.PixelHeight > 64_000_000)
            throw new InvalidOperationException(Localization.LocalizationService.GetString("SketchTooLarge"));
        using var ink = new MemoryStream(); document.Strokes.Save(ink, true);
        var payload = new Payload(1, document.Width, document.Height, document.PixelWidth, document.PixelHeight,
            document.Background?.ToString(), Convert.ToBase64String(ink.ToArray()),
            document.BaseImage is null ? null : Convert.ToBase64String(document.BaseImage));
        var json = JsonSerializer.Serialize(payload);
        if (json.Length > MaxPayloadCharacters) throw new InvalidOperationException(Localization.LocalizationService.GetString("SketchTooLarge"));
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new ScaleTransform(document.PixelWidth / document.Width, document.PixelHeight / document.Height));
            var bounds = new Rect(0, 0, document.Width, document.Height);
            drawing.PushClip(new RectangleGeometry(bounds));
            if (document.Background is { } color) drawing.DrawRectangle(new SolidColorBrush(color), null, bounds);
            if (document.BaseImage is { Length: > 0 } original)
            {
                using var sourceStream = new MemoryStream(original, false);
                var source = BitmapDecoder.Create(sourceStream, BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
                drawing.DrawImage(source, bounds);
            }
            document.Strokes.Draw(drawing);
            drawing.Pop(); drawing.Pop();
        }
        var bitmap = new RenderTargetBitmap(document.PixelWidth, document.PixelHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var metadata = new BitmapMetadata("png"); metadata.SetQuery(Query, json);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap, null, metadata, null));
        using var output = new MemoryStream(); encoder.Save(output); return output.ToArray();
    }

    public static Task<byte[]> EncodeAsync(SketchDocument document)
    {
        // Capture native ink on its owning dispatcher; rasterize/encode on a separate
        // STA so applying a large annotated photo does not freeze the workspace.
        using var stream = new MemoryStream(); document.Strokes.Save(stream, true);
        var ink = stream.ToArray();
        var completion = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var copy = new SketchDocument
                {
                    Width = document.Width, Height = document.Height, PixelWidth = document.PixelWidth,
                    PixelHeight = document.PixelHeight, Background = document.Background, BaseImage = document.BaseImage
                };
                using var input = new MemoryStream(ink, false); copy.Strokes.Add(new StrokeCollection(input));
                completion.SetResult(Encode(copy));
            }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true, Name = "Sketch PNG encoder" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return completion.Task;
    }
}
