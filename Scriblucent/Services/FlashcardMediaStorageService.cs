using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scriblucent.Services;

/// <summary>
/// Stores flashcard image/audio attachments as separate files under
/// %LocalAppData%\Scriblucent\FlashcardMedia, rather than embedding them as base64 inside
/// flashcards.json (the way Notes currently embeds images) - flashcards.json is rewritten in full
/// on every save, and audio clips especially can be far bigger than the compressed images Notes
/// stores, so keeping media out of that file avoids a real, growing save-time cost. Not
/// flashcard-specific in its actual logic (a plain file-based blob store), so it's the natural
/// thing to reuse if Notes' image storage is ever migrated off embedded-base64 later.
/// </summary>
internal static class FlashcardMediaStorageService
{
    private const int MaxImageDimension = 1200;
    private const int JpegQuality = 86;
    private static readonly string[] AllowedAudioExtensions = { ".mp3", ".wav" };

    private static string GetMediaFolderPath()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Scriblucent", "FlashcardMedia");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Decodes, downscales to MaxImageDimension, and re-encodes as JPEG (mirroring
    /// NoteEditorWindow's existing image-storage numbers), writing a new file with a generated
    /// name. Throws on an unreadable/corrupt source file - the caller owns showing that to the
    /// user, since this is an explicit "attach a file" action, not a silent background process.</summary>
    public static string SaveImage(string sourceFilePath)
    {
        var decoded = new BitmapImage();
        decoded.BeginInit();
        decoded.CacheOption = BitmapCacheOption.OnLoad;
        decoded.UriSource = new Uri(sourceFilePath);
        decoded.EndInit();
        decoded.Freeze();

        var resized = ResizeForStorage(decoded);
        var encoder = CreateJpegEncoder(resized);

        var fileName = $"{Guid.NewGuid():N}.jpg";
        var destinationPath = Path.Combine(GetMediaFolderPath(), fileName);

        using (var stream = File.Create(destinationPath))
        {
            encoder.Save(stream);
        }

        return fileName;
    }

    private static BitmapSource ResizeForStorage(BitmapSource source)
    {
        var scale = Math.Min((double)MaxImageDimension / source.PixelWidth, (double)MaxImageDimension / source.PixelHeight);
        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale >= 1.0)
            return source;

        var resized = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        resized.Freeze();
        return resized;
    }

    private static BitmapEncoder CreateJpegEncoder(BitmapSource source)
    {
        var opaque = HasAlpha(source.Format) ? MakeOpaque(source) : source;
        var converted = opaque.Format == PixelFormats.Bgr24
            ? opaque
            : new FormatConvertedBitmap(opaque, PixelFormats.Bgr24, null, 0);

        if (converted.CanFreeze)
            converted.Freeze();

        var encoder = new JpegBitmapEncoder { QualityLevel = JpegQuality };
        encoder.Frames.Add(BitmapFrame.Create(converted));
        return encoder;
    }

    private static bool HasAlpha(PixelFormat format)
        => format == PixelFormats.Bgra32
           || format == PixelFormats.Pbgra32
           || format == PixelFormats.Prgba64
           || format == PixelFormats.Rgba64
           || format == PixelFormats.Rgba128Float
           || format == PixelFormats.Prgba128Float;

    private static BitmapSource MakeOpaque(BitmapSource source)
    {
        var pixelWidth = Math.Max(1, source.PixelWidth);
        var pixelHeight = Math.Max(1, source.PixelHeight);

        // Skia composits the white plate + the image instead of a DrawingVisual + RTB pass.
        using var input = Scriblucent.Rendering.SkiaBitmapUtil.FromSource(source);
        using var output = new SkiaSharp.SKBitmap(pixelWidth, pixelHeight, SkiaSharp.SKColorType.Bgra8888, SkiaSharp.SKAlphaType.Premul);
        using (var canvas = new SkiaSharp.SKCanvas(output))
        {
            canvas.Clear(SkiaSharp.SKColors.White);
            canvas.DrawBitmap(input, 0, 0, Scriblucent.Rendering.SkiaBitmapUtil.LinearSampling);
        }

        return Scriblucent.Rendering.SkiaBitmapUtil.ToSource(output, Scriblucent.Rendering.SkiaBitmapUtil.DefaultDpi);
    }

    /// <summary>Copies the source file as-is (no transcoding) with a generated name. The file
    /// picker's own filter isn't trusted - the extension is validated here too, since "All files"
    /// is always an option in the dialog.</summary>
    public static string SaveAudio(string sourceFilePath)
    {
        var extension = Path.GetExtension(sourceFilePath).ToLowerInvariant();
        if (!AllowedAudioExtensions.Contains(extension))
            throw new NotSupportedException($"Unsupported audio file type: {extension}");

        var fileName = $"{Guid.NewGuid():N}{extension}";
        var destinationPath = Path.Combine(GetMediaFolderPath(), fileName);
        File.Copy(sourceFilePath, destinationPath, overwrite: true);
        return fileName;
    }

    public static void DeleteFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
            return;

        try
        {
            var path = Path.Combine(GetMediaFolderPath(), fileName);
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public static string? ResolvePath(string? fileName)
        => string.IsNullOrWhiteSpace(fileName) ? null : Path.Combine(GetMediaFolderPath(), fileName);

    public static BitmapImage? LoadImage(string? fileName, int decodePixelWidth = 180)
    {
        var path = ResolvePath(fileName);
        if (path is null || !File.Exists(path))
            return null;

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = decodePixelWidth;
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Deferred cleanup, not precise delete-on-remove: card edits only mutate in-memory
    /// state until the document is actually saved, so deleting a file the moment it's removed from
    /// an open editor could delete something still referenced by the last-saved document (e.g. the
    /// user removes an image then cancels). Called once after flashcards are loaded, comparing
    /// what's on disk against every filename actually referenced by a loaded card.</summary>
    public static void DeleteOrphanedFiles(IEnumerable<string> referencedFileNames)
    {
        try
        {
            var referenced = new HashSet<string>(referencedFileNames, StringComparer.OrdinalIgnoreCase);
            var folder = GetMediaFolderPath();

            foreach (var filePath in Directory.EnumerateFiles(folder))
            {
                var fileName = Path.GetFileName(filePath);
                if (!referenced.Contains(fileName))
                    DeleteFile(fileName);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
