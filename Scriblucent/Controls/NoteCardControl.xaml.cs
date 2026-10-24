using Scriblucent;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.ViewModels;
using Scriblucent.Localization;
using Scriblucent.Views;
using Scriblucent.Rendering;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace Scriblucent.Controls;

public partial class NoteCardControl : UserControl
{
    private static WeakReference<NoteCardControl>? _dropGlowOwner;

    private Point _dragStart;
    private bool _suppressOpenOnMouseUp;
    private CardTileRequestLease? _tileRequest;
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<System.Windows.Media.Imaging.BitmapSource, System.Windows.Media.Imaging.CroppedBitmap> ScrollFaces = new();
    internal static System.Windows.Media.Imaging.BitmapSource CropScrollTile(System.Windows.Media.Imaging.BitmapSource source)
        => ScrollFaces.GetValue(source, tile =>
        {
            // Tiles include the grid's outer margin; the control already owns that margin.
            // Reuse a frozen view of just the face so cached paint never enlarges/clips the card.
            double scaleX = tile.DpiX / 96, scaleY = tile.DpiY / 96;
            int x = (int)Math.Round(SkiaCardRenderer.CardMarginDip * scaleX);
            int y = (int)Math.Round(SkiaCardRenderer.CardMarginDip * scaleY);
            var face = new System.Windows.Media.Imaging.CroppedBitmap(tile, new Int32Rect(x, y,
                (int)Math.Round(SkiaCardRenderer.CardWidthDip * scaleX), (int)Math.Round(SkiaCardRenderer.MinCardHeightDip * scaleY)));
            face.Freeze();
            return face;
        });
    private void CancelTileRequest() { _tileRequest?.Dispose(); _tileRequest = null; }
    private void TileCacheInvalidated() { if (IsLoaded && IsVisible && IsBitmapMode) ApplyBitmapMode(); }

    /// <summary>
    /// Bitmap mode: the card shows only its cached per-note bitmap (see NoteCardBitmapCache)
    /// instead of the live template. During smooth-scroll sessions every container switches to
    /// bitmap mode, so rebinding a recycled container becomes a single Image source swap instead
    /// of re-rasterizing the live card drawing.
    /// </summary>
    public static readonly DependencyProperty IsBitmapModeProperty = DependencyProperty.Register(
        nameof(IsBitmapMode),
        typeof(bool),
        typeof(NoteCardControl),
        new PropertyMetadata(false, OnIsBitmapModeChanged));

    public bool IsBitmapMode
    {
        get => (bool)GetValue(IsBitmapModeProperty);
        set => SetValue(IsBitmapModeProperty, value);
    }

    private static void OnIsBitmapModeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var card = (NoteCardControl)d;
        card.ApplyBitmapMode();
        // Returning to live needs the preview built (it was skipped while in bitmap mode).
        if (!card.IsBitmapMode && e.OldValue is true)
            card.RebuildContentPreview(card.DataContext as NoteCardViewModel);
    }

    public NoteCardControl()
    {
        InitializeComponent();

        // Use PreviewMouseLeftButtonUp so clicks inside child controls still trigger opening the editor
        PreviewMouseLeftButtonUp += NoteCardControl_PreviewMouseLeftButtonUp;
        Loaded += OnCardLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) CancelTileRequest();
            else if (IsLoaded && IsBitmapMode) ApplyBitmapMode();
        };
        Cursor = Cursors.Hand;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (IsBitmapMode && BitmapLayer?.Source is System.Windows.Media.Imaging.BitmapSource bitmap)
        {
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;
            return new Size(bitmap.PixelWidth / dpi, bitmap.PixelHeight / dpi);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var result = base.MeasureOverride(availableSize);
        if (Scriblucent.Services.ScrollPerfDiag.Enabled && sw.Elapsed.TotalMilliseconds > 3)
        {
            var vm = DataContext as NoteCardViewModel;
            var preview = vm?.Document?.Content;
            Scriblucent.Services.ScrollPerfDiag.Log(
                $"cardMeasure\t{sw.Elapsed.TotalMilliseconds:F2}\tpreviewLen={preview?.Length ?? 0}\tpreviewHead={(preview is null ? "" : preview.Substring(0, Math.Min(60, preview.Length))).Replace("\t", " ").Replace("\n", " ")}");
        }

        return result;
    }

    private void ApplyBitmapMode()
    {
        CancelTileRequest();
        if (BitmapLayer is null || CardBorder is null || CardShadow is null)
            return;

        var bitmapMode = IsBitmapMode;
        BitmapLayer.Visibility = bitmapMode ? Visibility.Visible : Visibility.Collapsed;
        CardBorder.Visibility = bitmapMode ? Visibility.Collapsed : Visibility.Visible;
        CardShadow.Visibility = bitmapMode ? Visibility.Collapsed : Visibility.Visible;

        if (bitmapMode && IsLoaded && IsVisible && DataContext is NoteCardViewModel vm)
        {
            var lease = _tileRequest = new CardTileRequestLease();
            var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var requestedVm = vm;
            BitmapLayer.Source = CropScrollTile(Scriblucent.Rendering.SkiaCardTileCache.GetOrRequest(
                vm, dpi, Scriblucent.Rendering.SkiaTileRenderers.Notes, source =>
            {
                // The render landed on the thread pool and marshaled back; only apply if this
                // container still shows the same note in bitmap mode.
                if (lease.IsDisposed || !IsLoaded || !IsVisible || !IsBitmapMode || !ReferenceEquals(DataContext, requestedVm))
                    return;

                var previousHeight = (BitmapLayer.Source as System.Windows.Media.Imaging.BitmapSource)?.PixelHeight;
                var face = CropScrollTile(source);
                BitmapLayer.Source = face;
                if (previousHeight != face.PixelHeight)
                    InvalidateMeasure();
            }, lease: lease));
        }
        else if (!bitmapMode)
        {
            BitmapLayer.Source = null;
        }
    }

    /// <summary>
    /// Resets transient per-item visual state before this container is recycled for a different
    /// item. DashboardMotionEngine's group-move exit animation (HideCardShadowPlates/BeginMotionPrep)
    /// sets some of this state directly, outside the normal IsBitmapMode/DataContext-driven
    /// ApplyBitmapMode path - if that animation is interrupted before its own completion callback
    /// restores things (e.g. the item never actually leaves the view, or something else stops the
    /// in-flight storyboard), the state would otherwise survive onto whatever note this container
    /// gets rebound to next. Mirrors FlashcardFlipHost.PrepareForReuse for the same reason.
    /// </summary>
    internal void PrepareForReuse()
    {
        IsHitTestVisible = true;
        Opacity = 1;
        UiMotion.EndMotionPrep(this);
        ApplyBitmapMode();
    }

    /// <summary>
    /// Hides the nine-slice shadow layer while a smooth-scroll session is active (see
    /// DashboardScrollRenderGate.SetRealizedCardShadowVisibility) - the shadow is a notable
    /// part of each card's scroll-time draw cost, and flat cards during motion read the same
    /// once the elevation returns at rest.
    /// </summary>
    internal void SetScrollShadowVisible(bool visible)
    {
        if (CardShadow is null || IsBitmapMode)
            return;

        CardShadow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCardLoaded(object sender, RoutedEventArgs e)
    {
        if (ContentPreview is null)
            return;

        DataContextChanged += NoteCardControl_DataContextChanged;
        SkiaCardTileCache.Invalidated += TileCacheInvalidated;
        ObservePreview(DataContext as NoteCardViewModel);
        ApplyBitmapMode();
        if (!IsBitmapMode) RebuildContentPreview(DataContext as NoteCardViewModel);
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); TileCacheInvalidated(); }

    private void NoteCardControl_DataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        ObservePreview(e.NewValue as NoteCardViewModel);
        ApplyBitmapMode();
        // Defer the hidden inline preview while bitmap mode displays the cached tile.
        // Rebuild the preview when the card returns to live rendering.
        if (!IsBitmapMode)
            RebuildContentPreview(e.NewValue as NoteCardViewModel);
    }

    private NoteCardViewModel? _previewViewModel;
    private string? _previewContent;
    private NoteCardViewModel? _observedNote;

    private void ObservePreview(NoteCardViewModel? viewModel)
    {
        if (!ReferenceEquals(_observedNote, viewModel))
        {
            if (_observedNote is not null) _observedNote.PropertyChanged -= PreviewPropertyChanged;
            _observedNote = viewModel;
            if (_observedNote is not null) _observedNote.PropertyChanged += PreviewPropertyChanged;
        }
    }

    private void RebuildContentPreview(NoteCardViewModel? viewModel)
    {
        ObservePreview(viewModel);
        var content = viewModel?.Content ?? string.Empty;
        if (ReferenceEquals(_previewViewModel, viewModel) && _previewContent == content
            && ReferenceEquals(ContentPreview.Preview, viewModel?.Document.CardPreview)) return;
        _previewViewModel = viewModel;
        _previewContent = content;
        ContentPreview.Preview = viewModel?.Preview;
    }

    private void PreviewPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(NoteCardViewModel.Content)) return;
        if (IsBitmapMode) ApplyBitmapMode();
        else RebuildContentPreview(DataContext as NoteCardViewModel);
    }
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        CancelTileRequest();
        SkiaCardTileCache.Invalidated -= TileCacheInvalidated;
        BitmapLayer.Source = null;
        DataContextChanged -= NoteCardControl_DataContextChanged;
        ContentPreview.Preview = null;
        if (_observedNote is not null) _observedNote.PropertyChanged -= PreviewPropertyChanged;
        _observedNote = null;
        _previewViewModel = null;
        _previewContent = null;
        CardSurfaceMotionBehavior.Cleanup(this);
    }

    private void NoteCardControl_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_suppressOpenOnMouseUp)
        {
            _suppressOpenOnMouseUp = false;
            return;
        }

        // Don't open the editor when the three-dot menu button was clicked
        if (MenuButton.IsMouseOver)
            return;

        // Don't open the editor when the mass-select checkbox was clicked
        if (CollectionSelectionToggle.IsSelectionToggle(e.OriginalSource as DependencyObject))
            return;

        // Get the ViewModel from DataContext
        var viewModel = DataContext as NoteCardViewModel;
        var mainWindow = Window.GetWindow(this) as MainWindow;
        var mainVm = mainWindow?.DataContext as MainViewModel;

        if (viewModel != null && mainVm?.IsMassSelectMode == true)
        {
            // The inner shared frame owns selection; do not toggle a second time on mouse-up.
            return;
        }

        // Only proceed if both exist
        if (viewModel != null && mainWindow != null)
            mainWindow.OpenNoteEditor(viewModel);
    }

    private void OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
    }

    private void OnPreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
            return;

        if (DataContext is NoteCardViewModel vm)
            vm.IsMenuVisible = true;

        OpenContextMenu(this, PlacementMode.MousePoint);
        e.Handled = true;
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
            return;

        if (e.LeftButton != MouseButtonState.Pressed)
            return;

        if (DataContext is not NoteCardViewModel draggedNote)
            return;

        if (MenuButton.IsMouseOver)
            return;

        var currentPosition = e.GetPosition(this);
        var delta = currentPosition - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _suppressOpenOnMouseUp = true;
        MainWindow.SetNoteDragInProgress(true);
        try
        {
            DashboardDragGhost.Execute(
                this,
                new DataObject(typeof(NoteCardViewModel), draggedNote),
                DragDropEffects.Move,
                _dragStart);
        }
        finally
        {
            MainWindow.SetNoteDragInProgress(false);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (DashboardDragGhost.IsActiveDragSource(this))
        {
            ResetDropGlow();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        if (DataContext is not NoteCardViewModel target)
        {
            ResetDropGlow();
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var dragged = e.Data.GetData(typeof(NoteCardViewModel)) as NoteCardViewModel;
        var canDrop = dragged is not null && !ReferenceEquals(dragged, target);

        if (!DashboardDragGhost.IsNoteDropGlowSyncActive)
        {
            if (canDrop)
                SetDropGlowOwner(this);
            else if (_dropGlowOwner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, this))
                ResetDropGlow();
        }

        e.Effects = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDragEnter(object sender, DragEventArgs e)
    {
        if (DashboardDragGhost.IsActiveDragSource(this))
            ResetDropGlow();
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        ReleaseDropGlowOwnership();
        ResetDropGlowVisual();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        try
        {
            if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
                return;

            if (DataContext is not NoteCardViewModel targetNote)
                return;

            var draggedNote = e.Data.GetData(typeof(NoteCardViewModel)) as NoteCardViewModel;
            var mainWindow = Window.GetWindow(this) as MainWindow;
            var mainVm = mainWindow?.DataContext as MainViewModel;
            if (draggedNote is null || mainVm is null)
                return;

            var sameGroup = draggedNote.Document.GroupId.HasValue
                && draggedNote.Document.GroupId == targetNote.Document.GroupId;

            var changed = false;
            if (sameGroup)
            {
                var dropPosition = e.GetPosition(this);
                var placeAfter = dropPosition.X >= (ActualWidth / 2d);
                changed = mainVm.TryReorderNotesWithinGroup(draggedNote, targetNote, placeAfter);
            }
            else
            {
                changed = mainVm.TryGroupNotes(draggedNote, targetNote);
            }

            e.Handled = true;
        }
        finally
        {
            ResetDropGlowVisual();
            SetDropGlowSuppressed(false);
        }
    }

    private bool CanAcceptDrop(DragEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
            return false;

        if (DataContext is not NoteCardViewModel target)
            return false;

        var dragged = e.Data.GetData(typeof(NoteCardViewModel)) as NoteCardViewModel;
        return dragged is not null && !ReferenceEquals(dragged, target);
    }

    internal static void SyncDropGlowAtScreenPoint(
        DependencyObject? searchRoot,
        Point screenPoint,
        NoteCardViewModel? draggedNote,
        NoteCardControl? dragSource)
    {
        if (searchRoot is null || draggedNote is null)
            return;

        if (Window.GetWindow(searchRoot) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
        {
            ClearDropGlowOwner();
            return;
        }

        NoteCardControl? glowTarget = null;
        ForEachNoteCard(searchRoot, candidate =>
        {
            if (ReferenceEquals(candidate, dragSource)
                || candidate.DataContext is not NoteCardViewModel targetNote
                || ReferenceEquals(targetNote, draggedNote)
                || !IsScreenPointWithinCard(candidate, screenPoint))
            {
                return;
            }

            glowTarget = candidate;
        });

        SetDropGlowOwner(glowTarget);
    }

    private static void ForEachNoteCard(DependencyObject root, Action<NoteCardControl> visit)
    {
        if (root is NoteCardControl card)
            visit(card);

        var childCount = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < childCount; i++)
            ForEachNoteCard(VisualTreeHelper.GetChild(root, i), visit);
    }

    private static bool IsScreenPointWithinCard(NoteCardControl card, Point screenPoint)
    {
        if (!card.IsVisible || card.ActualWidth <= 0 || card.ActualHeight <= 0)
            return false;

        if (PresentationSource.FromVisual(card) is null)
            return false;

        try
        {
            var local = card.PointFromScreen(screenPoint);
            var margin = card.Margin;
            return local.X >= -margin.Left
                && local.Y >= -margin.Top
                && local.X <= card.ActualWidth + margin.Right
                && local.Y <= card.ActualHeight + margin.Bottom;
        }
        catch
        {
            return false;
        }
    }

    internal static void ClearDropGlowOwner()
    {
        if (_dropGlowOwner?.TryGetTarget(out var owner) == true)
            owner.ResetDropGlowVisual();

        _dropGlowOwner = null;
    }

    internal void ResetDropGlow()
    {
        ReleaseDropGlowOwnership();
        ResetDropGlowVisual();
    }

    private static void SetDropGlowOwner(NoteCardControl? owner)
    {
        if (owner is null)
        {
            ClearDropGlowOwner();
            return;
        }

        if (_dropGlowOwner?.TryGetTarget(out var previous) == true)
        {
            if (ReferenceEquals(previous, owner))
                return;

            previous.ResetDropGlowVisual();
        }

        _dropGlowOwner = new WeakReference<NoteCardControl>(owner);
        owner.EnsureDropGlow(1, 2.5);
    }

    private void ReleaseDropGlowOwnership()
    {
        if (_dropGlowOwner?.TryGetTarget(out var owner) == true && ReferenceEquals(owner, this))
            _dropGlowOwner = null;
    }

    private void ResetDropGlowVisual()
        => DashboardDropGlowVisual.Reset(DropGlow);

    internal void SetDropGlowSuppressed(bool suppressed)
    {
        if (suppressed)
            ResetDropGlow();

        DropGlow.Visibility = suppressed ? Visibility.Collapsed : Visibility.Visible;
    }

    internal void SetDragDimmed(bool dimmed, double fadedOpacity = 0.22)
    {
        DragDimOverlay.Visibility = Visibility.Collapsed;
        DragDimOverlay.Opacity = 0;

        if (dimmed)
        {
            SetDropGlowSuppressed(true);
            Opacity = fadedOpacity;
        }
        else
        {
            SetDropGlowSuppressed(false);
            ClearValue(OpacityProperty);
        }

        IsHitTestVisible = true;
    }

    internal void ClearDragVisualState()
    {
        if (DashboardDragGhost.IsActiveDragSource(this))
            return;

        SetDragDimmed(false);
    }

    internal void PrepareForMoveExit()
    {
        ReleaseDropGlowOwnership();
        ResetDropGlowVisual();
        SetDropGlowSuppressed(true);
        UiMotion.Stop(this);
        SetDragDimmed(false);
    }

    private void EnsureDropGlow(double opacity, double thickness)
        => DashboardDropGlowVisual.Animate(DropGlow, opacity, thickness);

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsBitmapMode)
            return;

        ClearDragVisualState();
        IndicatorRevealMotionBehavior.SyncContainer(this);
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        if (Window.GetWindow(this) is MainWindow { DataContext: MainViewModel { IsMassSelectMode: true } })
            return;

        if (DataContext is NoteCardViewModel vm)
            vm.IsMenuVisible = true;
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        if (DataContext is NoteCardViewModel vm)
            vm.IsMenuVisible = false;
    }

    private void OnMenuButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            OpenContextMenu(button, PlacementMode.Bottom);
    }

    private void OpenContextMenu(UIElement placementTarget, PlacementMode placement)
    {
        if (ContextMenu == null)
            return;

        ContextMenu.DataContext = DataContext;
        UiMotion.OpenContextMenu(ContextMenu, placementTarget, placement);
    }
}
