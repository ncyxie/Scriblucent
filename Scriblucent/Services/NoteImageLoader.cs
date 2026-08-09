using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scriblucent.Services;

/// <summary>
/// Decodes image files/bytes into frozen display bitmaps using WIC best practices:
/// stream + OnLoad (no file lock), single-axis DecodePixelWidth, Freeze.
/// Stored bytes remain the source of truth; the BitmapSource is a size-optimized view.
/// </summary>
public static class NoteImageLoader
{
    /// <summary>Decode budget headroom over the display width: photos are decoded at up to
    /// display x DPI x this factor so a modest zoom keeps sharpness. Kept low (1.5) because
    /// every extra factor squares the decoded pixel memory - a 3x headroom made a photo-heavy
    /// note hold ~9x its rendered bitmap size. The zoom already re-decodes at the new size.</summary>
    public const double DefaultDecodeHeadroom = 1.5;
    public const double DefaultInsertMaxWidthDip = 400;
    public const double DefaultInsertMaxHeightDip = 300;

    public sealed class LoadResult
    {
        public required byte[] Bytes { get; init; }
        public required BitmapSource DisplaySource { get; init; }
        public required double NaturalPixelWidth { get; init; }
        public required double NaturalPixelHeight { get; init; }
        public required int DecodePixelWidth { get; init; }
    }

    public static bool TryLoadFromFile(
        string path,
        double targetDisplayWidthDip,
        double dpiScale,
        out LoadResult? result,
        out string? error)
    {
        result = null;
        error = null;

        try
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                error = "File not found.";
                return false;
            }

            var bytes = File.ReadAllBytes(path);
            return TryLoadFromBytes(bytes, targetDisplayWidthDip, dpiScale, out result, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static Task<(bool Ok, LoadResult? Result, string? Error)> TryLoadFromFileAsync(
        string path,
        double targetDisplayWidthDip,
        double dpiScale,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ok = TryLoadFromFile(path, targetDisplayWidthDip, dpiScale, out var result, out var error);
            return (ok, result, error);
        }, cancellationToken);
    }

    public static Task<(bool Ok, LoadResult? Result, string? Error)> TryLoadFromBytesAsync(
        byte[] bytes,
        double targetDisplayWidthDip,
        double dpiScale,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ok = TryLoadFromBytes(bytes, targetDisplayWidthDip, dpiScale, out var result, out var error);
            return (ok, result, error);
        }, cancellationToken);
    }

    public static bool TryLoadFromBytes(
        byte[] bytes,
        double targetDisplayWidthDip,
        double dpiScale,
        out LoadResult? result,
        out string? error)
    {
        result = null;
        error = null;

        try
        {
            if (bytes is null || bytes.Length == 0)
            {
                error = "Empty image data.";
                return false;
            }

            var natural = ReadNaturalSize(bytes);
            if (natural.Width <= 0 || natural.Height <= 0)
            {
                error = "Unrecognized image format.";
                return false;
            }

            var decodeWidth = ResolveDecodePixelWidth(targetDisplayWidthDip, dpiScale, natural.Width);
            var display = DecodeFrozen(bytes, decodeWidth);
            if (display is null)
            {
                error = "Failed to decode image.";
                return false;
            }

            result = new LoadResult
            {
                Bytes = bytes,
                DisplaySource = display,
                NaturalPixelWidth = natural.Width,
                NaturalPixelHeight = natural.Height,
                DecodePixelWidth = decodeWidth
            };
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryCreateDisplaySource(
        byte[] bytes,
        double targetDisplayWidthDip,
        double dpiScale,
        out BitmapSource? display,
        out int decodePixelWidth,
        out string? error)
    {
        display = null;
        decodePixelWidth = 0;
        error = null;

        try
        {
            var natural = ReadNaturalSize(bytes);
            if (natural.Width <= 0)
            {
                error = "Unrecognized image format.";
                return false;
            }

            decodePixelWidth = ResolveDecodePixelWidth(targetDisplayWidthDip, dpiScale, natural.Width);
            display = DecodeFrozen(bytes, decodePixelWidth);
            if (display is null)
            {
                error = "Failed to decode image.";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static (double Width, double Height) FitWithinBox(
        double naturalWidth,
        double naturalHeight,
        double maxWidth,
        double maxHeight)
    {
        if (naturalWidth <= 0 || naturalHeight <= 0)
            return (Math.Min(maxWidth, 200), Math.Min(maxHeight, 150));

        var scale = Math.Min(1.0, Math.Min(maxWidth / naturalWidth, maxHeight / naturalHeight));
        return (Math.Max(1, naturalWidth * scale), Math.Max(1, naturalHeight * scale));
    }

    public static bool TryGetNaturalSize(byte[] bytes, out double width, out double height)
    {
        width = 0;
        height = 0;
        try
        {
            var natural = ReadNaturalSize(bytes);
            width = natural.Width;
            height = natural.Height;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    public static int ResolveDecodePixelWidth(double targetDisplayWidthDip, double dpiScale, double naturalPixelWidth)
    {
        var scale = dpiScale > 0 ? dpiScale : 1.0;
        var budget = (int)Math.Ceiling(Math.Max(1, targetDisplayWidthDip) * scale * DefaultDecodeHeadroom);
        if (naturalPixelWidth > 0)
            budget = (int)Math.Min(budget, Math.Ceiling(naturalPixelWidth));
        return Math.Max(1, budget);
    }

    private static (double Width, double Height) ReadNaturalSize(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var decoder = BitmapDecoder.Create(
            stream,
            BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
            BitmapCacheOption.None);

        if (decoder.Frames.Count == 0)
            return (0, 0);

        var frame = decoder.Frames[0];
        return (frame.PixelWidth, frame.PixelHeight);
    }

    private static BitmapSource? DecodeFrozen(byte[] bytes, int decodePixelWidth)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodePixelWidth > 0)
            bitmap.DecodePixelWidth = decodePixelWidth;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        if (bitmap.CanFreeze)
            bitmap.Freeze();
        return bitmap;
    }
}
