using Scriblucent.Rendering;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.ComponentModel;
using Scriblucent.Behaviors;
using Scriblucent.Localization;

namespace Scriblucent.Controls;

/// <summary>
/// Wraps a dashboard listing card template (flashcard sets, quizzes, mind maps) and gives it
/// the same scroll-tile behavior as the notes cards: during smooth-scroll sessions the host
/// shows the off-thread Skia-rendered tile, and at rest the live template renders with all
/// its interactions (motion, drag/drop, context menus) intact.
/// </summary>
public class DashboardTileHost : ContentControl
{
    public static readonly DependencyProperty RendererKeyProperty = DependencyProperty.Register(
        nameof(RendererKey),
        typeof(string),
        typeof(DashboardTileHost),
        new PropertyMetadata(null, OnRendererKeyChanged));

    public static readonly DependencyProperty IsBitmapModeProperty = DependencyProperty.Register(
        nameof(IsBitmapMode),
        typeof(bool),
        typeof(DashboardTileHost),
        new PropertyMetadata(false, OnIsBitmapModeChanged));

    private Image? _bitmapLayer;
    private ContentPresenter? _liveContent;
    private bool _bitmapUsesPlaceholder;
    private INotifyPropertyChanged? _observedItem;
    private int _requestVersion;
    private CardTileRequestLease? _tileRequest;
    private void CancelTileRequest() { _tileRequest?.Dispose(); _tileRequest = null; }
    private void TileCacheInvalidated() => AppearanceChanged(null, EventArgs.Empty);
    internal bool HasReadyScrollTile => _bitmapLayer?.Visibility == Visibility.Visible && _liveContent?.Opacity == 0;
    private FrozenCardDrawing.Builder? _warmBuilder;
    private int _warmVersion;
    private string? _warmSignature;
    private double _warmDpi;
    private Size _warmSize;
    internal bool HasCachedScrollTile => _liveContent is { ActualWidth: > 0 } live && DataContext is { } vm
        && SkiaTileRenderers.ForKey(RendererKey ?? "") is { } renderer
        && SkiaCardTileCache.GetReady(vm, VisualTreeHelper.GetDpi(this).PixelsPerDip, renderer,
            (float)live.ActualWidth) is not null;

    public static readonly DependencyProperty PreserveLiveInputInBitmapModeProperty = DependencyProperty.Register(
        nameof(PreserveLiveInputInBitmapMode), typeof(bool), typeof(DashboardTileHost), new PropertyMetadata(false));
    public bool PreserveLiveInputInBitmapMode
    {
        get => (bool)GetValue(PreserveLiveInputInBitmapModeProperty);
        set => SetValue(PreserveLiveInputInBitmapModeProperty, value);
    }

    static DashboardTileHost()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(DashboardTileHost),
            new FrameworkPropertyMetadata(typeof(DashboardTileHost)));
    }

    public string? RendererKey
    {
        get => (string?)GetValue(RendererKeyProperty);
        set => SetValue(RendererKeyProperty, value);
    }

    public bool IsBitmapMode
    {
        get => (bool)GetValue(IsBitmapModeProperty);
        set => SetValue(IsBitmapModeProperty, value);
    }

    public DashboardTileHost()
    {
        DataContextChanged += (_, args) =>
        {
            CancelTileRequest();
            if (PreserveLiveInputInBitmapMode) RestoreLive();
            ObserveItem(); ApplyBitmapMode(); QueueWarm();
        };
        Loaded += (_, _) =>
        {
            ObserveItem(); ScrollTileBehavior.RegisterHost(this);
            SkiaCardTileCache.Invalidated -= TileCacheInvalidated; SkiaCardTileCache.Invalidated += TileCacheInvalidated;
            if (!PreserveLiveInputInBitmapMode) ApplyBitmapMode();
            QueueWarm();
        };
        Unloaded += (_, _) =>
        {
            CancelWarm();
            CancelTileRequest();
            _requestVersion++;
            if (_observedItem is { } item) item.PropertyChanged -= ItemChanged;
            _observedItem = null; IsBitmapMode = false;
            SkiaCardTileCache.Invalidated -= TileCacheInvalidated;
        };
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) { CancelWarm(); CancelTileRequest(); }
            else if (IsLoaded) { if (PreserveLiveInputInBitmapMode) QueueWarm(); else ApplyBitmapMode(); }
        };
        PreviewMouseDown += (_, _) => InterruptScroll();
        PreviewKeyDown += (_, _) => InterruptScroll();
        GotKeyboardFocus += (_, _) => InterruptScroll();
        MouseLeave += (_, _) => QueueWarm();
        SizeChanged += (_, _) => UpdateBitmapLayerBounds();
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _bitmapLayer = GetTemplateChild("BitmapLayer") as Image;
        _liveContent = GetTemplateChild("LiveContent") as ContentPresenter;
        ApplyBitmapMode();
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        CancelTileRequest();
        if (PreserveLiveInputInBitmapMode) { RestoreLive(); QueueWarm(); }
        else if (IsLoaded) ApplyBitmapMode();
    }

    private static void OnRendererKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    { var host = (DashboardTileHost)d; host.CancelTileRequest(); host.ApplyBitmapMode(); }

    private static void OnIsBitmapModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((DashboardTileHost)d).ApplyBitmapMode();

    private void ApplyBitmapMode()
    {
        var layer = _bitmapLayer;
        var live = _liveContent;
        if (layer is null || live is null)
            return;

        var bitmapMode = IsBitmapMode;
        if (PreserveLiveInputInBitmapMode)
        {
            ApplyInputPreservingMode(bitmapMode);
            return;
        }
        CancelTileRequest();
        if (!bitmapMode)
        {
            RestoreLive();
            return;
        }
        if (!IsLoaded || !IsVisible) return;

        // Keep the live element in layout so switching to its bitmap never changes the
        // tile's measured size. Hidden preserves layout and also blocks input to the live card.
        live.Visibility = Visibility.Hidden;
        layer.Visibility = Visibility.Visible;
        UpdateBitmapLayerBounds();

        var renderer = SkiaTileRenderers.ForKey(RendererKey ?? string.Empty);
        var vm = DataContext;
        if (renderer is null || vm is null)
            return;

        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var requestedVm = vm;
        var lease = _tileRequest = new CardTileRequestLease();
        var widthDip = ActualWidth > 0 ? (float)ActualWidth : 0f;
        var cachedTile = SkiaCardTileCache.GetReady(vm, dpi, renderer, widthDip);
        _bitmapUsesPlaceholder = cachedTile is null;
        layer.Source = cachedTile ?? SkiaCardTileCache.GetOrRequest(vm, dpi, renderer, source =>
        {
            // The render landed on the thread pool and marshaled back; only apply if this
            // container still shows the same card in bitmap mode.
            if (lease.IsDisposed || !IsLoaded || !IsVisible || !IsBitmapMode || !ReferenceEquals(DataContext, requestedVm))
                return;

            layer.Source = source;
            _bitmapUsesPlaceholder = false;
            UpdateBitmapLayerBounds();
        }, widthDip, lease);
    }

    private void UpdateBitmapLayerBounds()
    {
        if (_bitmapLayer is not { } layer || _liveContent is null || !IsBitmapMode || PreserveLiveInputInBitmapMode)
            return;

        if (VisualTreeHelper.GetParent(layer) is Canvas canvas)
            canvas.ClipToBounds = true;
        layer.Margin = default;
        layer.HorizontalAlignment = HorizontalAlignment.Left;
        layer.VerticalAlignment = VerticalAlignment.Top;
        Canvas.SetLeft(layer, 0);
        Canvas.SetTop(layer, 0);
        layer.Stretch = _bitmapUsesPlaceholder ? Stretch.Fill : Stretch.None;
        layer.Width = _bitmapUsesPlaceholder ? ActualWidth : double.NaN;
        layer.Height = _bitmapUsesPlaceholder ? ActualHeight : double.NaN;
    }

    private void ObserveItem()
    {
        if (_observedItem is { } old) old.PropertyChanged -= ItemChanged;
        _observedItem = PreserveLiveInputInBitmapMode && IsLoaded ? DataContext as INotifyPropertyChanged : null;
        if (_observedItem is { } item) item.PropertyChanged += ItemChanged;
    }
    private void ItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == "IsSelectedInMassSelect") return; // Selection is painted outside this cache.
        CancelTileRequest(); RestoreLive(); QueueWarm();
    }
    private void AppearanceChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => AppearanceChanged(sender, e)); return; }
        CancelTileRequest();
        if (PreserveLiveInputInBitmapMode) { RestoreLive(); QueueWarm(); }
        else if (IsLoaded) ApplyBitmapMode();
    }
    private bool _warmQueued;
    internal void QueueWarm()
    {
        if (!PreserveLiveInputInBitmapMode || _warmQueued || !IsLoaded || !IsVisible || IsBitmapMode
            || ScrollTileBehavior.GetIsScrolling(this) || !Scriblucent.Animations.MotionPolicy.Spatial) return;
        _warmQueued = true;
        FlashcardTileWarmQueue.Enqueue(this);
    }
    private void CancelWarm()
    {
        FlashcardTileWarmQueue.Cancel(this); _warmQueued = false; _warmBuilder = null;
    }
    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property == ScrollTileBehavior.IsScrollingProperty && PreserveLiveInputInBitmapMode)
        { if (e.NewValue is true) CancelWarm(); else QueueWarm(); }
    }
    internal bool WarmScrollTileSlice()
    {
        using var timing = FlashcardScrollTrace.Measure(FlashcardScrollTrace.Kind.Capture, DataContext);
        var frame = CollectionSelectionFrame.For(this);
        if (!PreserveLiveInputInBitmapMode || !Scriblucent.Animations.MotionPolicy.Spatial || !IsLoaded || !IsVisible || IsBitmapMode
            || ScrollTileBehavior.GetIsScrolling(this) || IsMouseOver || frame?.IsMouseOver == true
            || frame is not null && Scriblucent.Animations.NativeSurfaceMotion.IsRunning(frame, Scriblucent.Animations.MotionChannel.Interaction)
            || _liveContent is not { IsArrangeValid: true } live
            || DataContext is not { } vm || FindFlip()?.IsAnimating == true
            || SkiaTileRenderers.ForKey(RendererKey ?? "") is not { } renderer)
        { _warmQueued = false; _warmBuilder = null; return false; }
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var size = new Size(live.ActualWidth, live.ActualHeight);
        var signature = renderer.BuildSignature(vm);
        if (SkiaCardTileCache.GetReady(vm, dpi, renderer, (float)size.Width) is not null)
        { _warmQueued = false; _warmBuilder = null; return false; }
        if (_warmBuilder is null || _warmVersion != _requestVersion || _warmSignature != signature || _warmSize != size || _warmDpi != dpi)
        {
            _warmBuilder = FrozenCardDrawing.Builder.Create(live);
            _warmVersion = _requestVersion; _warmSignature = signature; _warmSize = size; _warmDpi = dpi;
        }
        if (_warmBuilder is null) { _warmQueued = false; return false; }
        if (!_warmBuilder.Advance(2)) return true;
        if (_warmBuilder.Result is { } snapshot)
        {
            CancelTileRequest();
            _tileRequest = new CardTileRequestLease();
            SkiaCardTileCache.GetReadyOrRequest(vm, dpi, renderer, null, (float)size.Width, () => snapshot, _tileRequest);
        }
        _warmQueued = false; _warmBuilder = null; return false;
    }
    private FlashcardFlipHost? FindFlip() => FindChild<FlashcardFlipHost>(_liveContent);
    private static T? FindChild<T>(DependencyObject? parent) where T : DependencyObject
    {
        if (parent is null) return null;
        if (parent is T found) return found;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindChild<T>(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }
    private void RestoreLive()
    {
        if (!PreserveLiveInputInBitmapMode) CancelTileRequest();
        CancelWarm();
        _requestVersion++;
        if (_liveContent is { } live) { live.Visibility = Visibility.Visible; live.Opacity = 1; }
        if (_bitmapLayer is { } layer)
        {
            if (VisualTreeHelper.GetParent(layer) is Canvas canvas)
                canvas.ClipToBounds = false;
            layer.Visibility = Visibility.Collapsed;
            layer.Source = null;
            _bitmapUsesPlaceholder = false;
            layer.Stretch = Stretch.None;
            layer.Width = double.NaN;
            layer.Height = double.NaN;
            layer.Margin = default;
            Canvas.SetLeft(layer, 0);
            Canvas.SetTop(layer, 0);
        }
    }
    private void ApplyInputPreservingMode(bool bitmapMode)
    {
        // Choose once on entering the session. A worker completion only updates the
        // cache; it must not replace a moving cold card or flash through live paint.
        if (!bitmapMode) { RestoreLive(); QueueWarm(); return; }
        CancelWarm();
        if (_liveContent is not { } live || _bitmapLayer is not { } layer || DataContext is not { } vm
            || !IsLoaded || live.ActualWidth <= 0 || FindFlip()?.IsAnimating == true) return;
        var renderer = SkiaTileRenderers.ForKey(RendererKey ?? "");
        if (renderer is null) return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var tile = SkiaCardTileCache.GetReady(vm, dpi, renderer, (float)live.ActualWidth);
        if (tile is null) { RestoreLive(); return; }
        if (ReferenceEquals(layer.Source, tile) && HasReadyScrollTile) return;
        using (FlashcardScrollTrace.Measure(FlashcardScrollTrace.Kind.Publish, vm))
        {
            if (VisualTreeHelper.GetParent(layer) is Canvas canvas)
                canvas.ClipToBounds = false;
            layer.Stretch = Stretch.None;
            layer.Width = double.NaN;
            layer.Height = double.NaN;
            layer.Margin = default;
            layer.HorizontalAlignment = HorizontalAlignment.Left; layer.VerticalAlignment = VerticalAlignment.Top;
            Canvas.SetLeft(layer, -FrozenCardDrawing.Fringe);
            Canvas.SetTop(layer, -FrozenCardDrawing.Fringe);
            layer.IsHitTestVisible = false;
            layer.Source = tile; layer.Visibility = Visibility.Visible;
            live.Visibility = Visibility.Visible; live.Opacity = 0;
        }
    }
    private void InterruptScroll()
    {
        if (!PreserveLiveInputInBitmapMode) return;
        for (DependencyObject? node = this; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is ScrollViewer viewer) { SmoothScrollBehavior.Interrupt(viewer); break; }
    }
    internal void ReleaseScrollTile()
    {
        RestoreLive(); IsBitmapMode = false; PreserveLiveInputInBitmapMode = false; ObserveItem();
        ThemeManager.ThemeChanged -= AppearanceChanged; LocalizationService.CultureChanged -= AppearanceChanged;
    }

    private sealed class SelectionHeaderState { internal bool Selecting; }
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<object, SelectionHeaderState> SelectionHeaders = new();
    internal void SyncSelectionHeader(bool selecting)
    {
        // Recycled/offscreen flashcards may still have paint from the previous mode.
        // Invalidate once per item/mode, preserving warmed paint during ordinary recycling.
        if (RendererKey != "FlashcardGridCard" || DataContext is not { } item) return;
        var state = SelectionHeaders.GetOrCreateValue(item);
        if (state.Selecting == selecting) return;
        state.Selecting = selecting;
        RefreshScrollPaint();
    }

    internal void RefreshScrollPaint()
    {
        CancelTileRequest(); CancelWarm(); RestoreLive();
        if (DataContext is { } item && SkiaTileRenderers.ForKey(RendererKey ?? "") is { } renderer)
            SkiaCardTileCache.Forget(item, renderer);
        QueueWarm();
    }

}
