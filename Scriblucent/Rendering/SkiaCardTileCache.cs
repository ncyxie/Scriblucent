using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SkiaSharp;
using Scriblucent.ViewModels;

namespace Scriblucent.Rendering;

/// <summary>
/// The per-note card tile cache with a background render queue. Cards rasterize on the thread
/// pool via Skia (off the UI thread - the property WPF lacks and the root of the measured
/// scroll stalls), then marshal to the UI thread as frozen BitmapSources (a pixel copy, µs).
/// Scroll-time first draws therefore become texture references, not UI-thread rasterization.
/// </summary>
internal static class SkiaCardTileCache
{
    private const int MaxEntries = 240;

    private sealed class Entry
    {
        public required string Signature;
        public required SKBitmap Bitmap;
        public required BitmapSource Source;
        public required double HeightDip;
    }

    private sealed class Request
    {
        public required NoteCardViewModel ViewModel;
        public required double Dpi;
        public required Action<BitmapSource>? Completed;
    }

    private static readonly Dictionary<(NoteCardViewModel Vm, double Dpi), Entry> Cache = new();
    private static readonly Queue<(NoteCardViewModel Vm, double Dpi)> Order = new();
    private static readonly ConcurrentQueue<Request> Pending = new();
    private static readonly ConcurrentDictionary<NoteCardViewModel, byte> InFlight = new();
    private static int _workersStarted;
    private static CardRenderColors _colors;
    private static string _colorsKey = string.Empty;
    private static BitmapSource? _placeholder;
    private static double _placeholderDpi;
    private static bool _themeHookAttached;

    static SkiaCardTileCache()
    {
        ThemeManager.ThemeChanged += (_, _) => Clear();
    }

    public static CardRenderColors Colors
    {
        get
        {
            var theme = ThemeManager.CurrentTheme ?? string.Empty;
            if (_colorsKey != theme)
            {
                _colors = ResolveColors();
                _colorsKey = theme;
                _placeholder = null;
            }

            return _colors;
        }
    }

    public static void Clear()
    {
        foreach (var (_, entry) in Cache)
            entry.Bitmap.Dispose();

        Cache.Clear();
        Order.Clear();
        _placeholder = null;
    }

    /// <summary>
    /// Returns the cached tile for the note, or the themed placeholder and queues an off-thread
    /// render. When the render lands, <paramref name="completed"/> runs on the UI thread with
    /// the frozen BitmapSource (only if the caller is still relevant).
    /// </summary>
    public static BitmapSource GetOrRequest(NoteCardViewModel vm, double dpi, Action<BitmapSource>? completed)
    {
        var colors = Colors;
        var signature = BuildSignature(vm);
        var key = (vm, dpi);

        if (Cache.TryGetValue(key, out var entry) && entry.Signature == signature)
        {
            Touch(key);
            return entry.Source;
        }

        if (completed is not null)
        {
            // De-dupe in-flight renders per view model.
            if (InFlight.TryAdd(vm, 0))
            {
                Pending.Enqueue(new Request { ViewModel = vm, Dpi = dpi, Completed = completed });
                EnsureWorkers();
            }
        }

        return GetPlaceholder(dpi, colors);
    }

    public static double? GetHeightDip(NoteCardViewModel vm, double dpi)
    {
        if (Cache.TryGetValue((vm, dpi), out var entry))
            return entry.HeightDip;

        return null;
    }

    private static void EnsureWorkers()
    {
        if (Interlocked.Exchange(ref _workersStarted, 1) == 1)
            return;

        for (var i = 0; i < 2; i++)
        {
            var worker = new Thread(RenderWorker)
            {
                IsBackground = true,
                Name = $"SkiaTileWorker-{i}",
                Priority = ThreadPriority.BelowNormal
            };
            worker.Start();
        }
    }

    private static void RenderWorker()
    {
        while (true)
        {
            if (!Pending.TryDequeue(out var request))
            {
                Thread.Sleep(4);
                continue;
            }

            SKBitmap bitmap;
            double heightDip;
            try
            {
                bitmap = SkiaCardRenderer.Render(request.ViewModel, request.Dpi, Colors, out heightDip);
            }
            catch
            {
                InFlight.TryRemove(request.ViewModel, out _);
                continue;
            }

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.HasShutdownStarted)
            {
                bitmap.Dispose();
                InFlight.TryRemove(request.ViewModel, out _);
                continue;
            }

            var viewModel = request.ViewModel;
            var dpi = request.Dpi;
            dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() =>
            {
                try
                {
                    var source = ToBitmapSource(bitmap, dpi);
                    var key = (viewModel, dpi);
                    if (Cache.TryGetValue(key, out var existing))
                        existing.Bitmap.Dispose();
                    else
                        Order.Enqueue(key);

                    Cache[key] = new Entry
                    {
                        Signature = BuildSignature(viewModel),
                        Bitmap = bitmap,
                        Source = source,
                        HeightDip = heightDip
                    };

                    while (Order.Count > MaxEntries)
                    {
                        var oldest = Order.Dequeue();
                        if (Cache.Remove(oldest, out var evicted))
                            evicted.Bitmap.Dispose();
                    }
                }
                catch
                {
                    bitmap.Dispose();
                }
                finally
                {
                    InFlight.TryRemove(viewModel, out _);
                }

                request.Completed?.Invoke(GetCached(viewModel, dpi));
            }));
        }
    }

    private static BitmapSource? GetCached(NoteCardViewModel vm, double dpi)
    {
        return Cache.TryGetValue((vm, dpi), out var entry) ? entry.Source : null;
    }

    /// <summary>
    /// <paramref name="dpiScale"/> is WPF's PixelsPerDip (a scale factor, e.g. 1.0/1.5/2.0), not
    /// a raw DPI value - BitmapSource.Create wants the latter, so it's converted here.
    /// </summary>
    private static BitmapSource ToBitmapSource(SKBitmap bitmap, double dpiScale)
    {
        var dpi = 96.0 * dpiScale;
        var source = BitmapSource.Create(
            bitmap.Width,
            bitmap.Height,
            dpi,
            dpi,
            PixelFormats.Bgra32,
            null,
            bitmap.Bytes,
            bitmap.RowBytes);
        source.Freeze();
        return source;
    }

    private static BitmapSource GetPlaceholder(double dpiScale, CardRenderColors colors)
    {
        if (_placeholder is not null && Math.Abs(_placeholderDpi - dpiScale) < 0.1)
            return _placeholder;

        var scale = (float)dpiScale;
        var width = (int)Math.Ceiling((SkiaCardRenderer.CardWidthDip + SkiaCardRenderer.CardMarginDip * 2) * scale);
        var height = (int)Math.Ceiling((SkiaCardRenderer.MinCardHeightDip + SkiaCardRenderer.CardMarginDip * 2) * scale);
        using var bitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        var rect = SKRect.Create(
            SkiaCardRenderer.CardMarginDip * scale,
            SkiaCardRenderer.CardMarginDip * scale,
            SkiaCardRenderer.CardWidthDip * scale,
            SkiaCardRenderer.MinCardHeightDip * scale);
        using var paint = new SKPaint { Color = colors.Background, IsAntialias = true };
        var radius = SkiaCardRenderer.CardCornerRadiusDip * scale;
        canvas.DrawRoundRect(rect, radius, radius, paint);

        _placeholder = ToBitmapSource(bitmap, dpiScale);
        _placeholderDpi = dpiScale;
        return _placeholder;
    }

    private static void Touch((NoteCardViewModel Vm, double Dpi) key)
    {
        // Re-arm the LRU order by moving the key to the tail (bounded, cheap).
        if (Order.Count <= 1)
            return;

        // Only re-queue if not already at the tail.
        var last = Order.ToArray();
        if (last.Length > 0 && last[^1].Vm == key.Vm && last[^1].Dpi == key.Dpi)
            return;

        var list = new List<(NoteCardViewModel, double)>(Order.Count);
        while (Order.Count > 0)
        {
            var item = Order.Dequeue();
            if (item.Vm != key.Vm || item.Dpi != key.Dpi)
                list.Add(item);
        }

        list.Add(key);
        foreach (var item in list)
            Order.Enqueue(item);
    }

    private static string BuildSignature(NoteCardViewModel vm)
        => vm.Document is { } doc
            ? $"{ThemeManager.CurrentTheme}|{doc.Title}|{vm.Content}|{vm.TagsDisplay}|{doc.IsPinned}|{vm.HasSchedule}"
            : $"{ThemeManager.CurrentTheme}|{vm.Title}|{vm.Content}|{vm.TagsDisplay}";

    private static CardRenderColors ResolveColors()
    {
        Color Get(string key, Color fallback)
        {
            if (System.Windows.Application.Current?.TryFindResource(key) is System.Windows.Media.SolidColorBrush brush)
                return brush.Color;

            return fallback;
        }

        var background = Get("NoteCardBackground", System.Windows.Media.Colors.White);
        var border = Get("NoteCardBorder", System.Windows.Media.Colors.Transparent);
        var title = Get("NoteCardTitle", Color.FromRgb(28, 28, 28));
        var text = Get("TextColor", Color.FromRgb(60, 60, 60));
        var tagBackground = Get("TagChipBackground", Color.FromRgb(245, 247, 251));
        var tagText = Get("TagChipForeground", Color.FromRgb(74, 93, 128));

        return new CardRenderColors(
            ToSkia(background),
            ToSkia(border),
            ToSkia(title),
            ToSkia(text),
            ToSkia(tagBackground),
            ToSkia(tagText));
    }

    private static SKColor ToSkia(Color c)
        => new(c.R, c.G, c.B, c.A);
}
