using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Controls;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>
/// Object-layer host for floating note images above a RichTextBox.
/// Geometry is stored in document space; canvas positions are derived from scroll + padding.
/// Domain bytes/decode budget live in <see cref="NoteImageSession"/>, not on the widget.
/// </summary>
public sealed class FloatingImageCoordinator
{
    internal const string ExtentPadTag = "Scriblucent.FloatingImageExtentPad";

    private const double DragScrollEdgeThresholdDip = 64;
    private const double DragScrollMinStepDip = 4;
    private const double DragScrollMaxStepDip = 48;
    private const double ExtentPadSlackDip = 24;

    private Canvas? _overlay;
    private RichTextBox? _editor;
    private FrameworkElement? _surface;
    private ScrollViewer? _scrollViewer;
    private NoteImageWidget? _selected;
    private NoteImageWidget? _gestureWidget;
    private Point _dragGrabOffset;
    private bool _hasDragGrab;
    private DispatcherTimer? _dragScrollTimer;
    private bool _syncScheduled;
    private bool _isRestoring;
    private bool _isAdjustingExtentPad;
    private double _extentPadHeight;
    private double _padBaseContentBottom;
    private int _activeGestures;
    private int _attachGeneration;
    private readonly Dictionary<Guid, NoteImageSession> _sessions = new();

    public event EventHandler? ImagesChanged;
    public event EventHandler? SelectionChanged;

    public bool HasSelection => _selected is not null;
    public Guid? SelectedImageId => _selected?.ImageId;
    public bool IsAdjustingExtentPad => _isAdjustingExtentPad;

    public void Attach(Canvas overlay, RichTextBox editor, FrameworkElement surface)
    {
        Detach();

        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
        _editor = editor ?? throw new ArgumentNullException(nameof(editor));
        _surface = surface ?? throw new ArgumentNullException(nameof(surface));

        _editor.SizeChanged += Host_SizeChanged;
        _surface.SizeChanged += Host_SizeChanged;
        _editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Editor_ScrollChanged), true);
        _editor.PreviewMouseDown += Editor_PreviewMouseDown;

        _scrollViewer = FindScrollViewer(_editor);
        ScheduleSync();
    }

    public void Detach()
    {
        _attachGeneration++;
        _syncScheduled = false;
        StopDragScrollTimer();

        if (_editor is not null)
        {
            StripImageExtentPad();
            _editor.SizeChanged -= Host_SizeChanged;
            _editor.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(Editor_ScrollChanged));
            _editor.PreviewMouseDown -= Editor_PreviewMouseDown;
        }

        if (_surface is not null)
            _surface.SizeChanged -= Host_SizeChanged;

        Clear(raiseChanged: false);
        _sessions.Clear();
        _overlay = null;
        _editor = null;
        _surface = null;
        _scrollViewer = null;
        _selected = null;
        _gestureWidget = null;
        _hasDragGrab = false;
        _activeGestures = 0;
        _extentPadHeight = 0;
        _padBaseContentBottom = 0;
    }

    /// <summary>
    /// Inserts a floating image. Returns the session id, or null if the host is not attached.
    /// </summary>
    public Guid? Insert(
        NoteImageLoader.LoadResult load,
        double displayWidth,
        double displayHeight,
        Point? documentOrigin = null)
    {
        if (_overlay is null || _editor is null)
            return null;

        var id = Guid.NewGuid();
        var origin = documentOrigin ?? ResolveDefaultInsertOrigin(displayWidth, displayHeight);

        var session = new NoteImageSession
        {
            Id = id,
            Bytes = load.Bytes,
            NaturalPixelWidth = load.NaturalPixelWidth,
            NaturalPixelHeight = load.NaturalPixelHeight,
            DecodePixelWidth = load.DecodePixelWidth,
            DocumentLeft = origin.X,
            DocumentTop = origin.Y,
            Width = displayWidth,
            Height = displayHeight,
            LockAspectRatio = true
        };
        _sessions[id] = session;

        var widget = CreateWidget(session, load.DisplaySource);
        _overlay.Children.Add(widget);
        EnsureImageExtentPad();
        ClampSessionAndProject(session, widget);
        Select(widget);
        SyncWidget(widget);
        RaiseImagesChanged();
        return id;
    }

    public void Clear(bool raiseChanged = true)
    {
        if (_overlay is null)
        {
            _sessions.Clear();
            return;
        }

        foreach (var widget in EnumerateWidgets().ToList())
            UnhookWidget(widget);

        _overlay.Children.Clear();
        _sessions.Clear();
        _selected = null;
        EnsureImageExtentPad();
        if (raiseChanged)
            RaiseImagesChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<NoteImageAttachment> BuildAttachments()
    {
        if (_overlay is null || _sessions.Count == 0)
            return Array.Empty<NoteImageAttachment>();

        var list = new List<NoteImageAttachment>();
        foreach (var widget in EnumerateWidgets())
        {
            if (!_sessions.TryGetValue(widget.ImageId, out var session) || session.Bytes.Length == 0)
                continue;

            SyncSessionFromWidget(session, widget);
            list.Add(new NoteImageAttachment
            {
                Id = session.Id,
                Data = Convert.ToBase64String(session.Bytes),
                Width = session.Width,
                Height = session.Height,
                Left = session.DocumentLeft,
                Top = session.DocumentTop,
                LockAspectRatio = session.LockAspectRatio
            });
        }

        return list;
    }

    public void Restore(IEnumerable<NoteImageAttachment>? attachments)
    {
        Clear(raiseChanged: false);
        if (_overlay is null || attachments is null)
            return;

        _isRestoring = true;
        try
        {
            var dpi = GetDpiScale();
            foreach (var attachment in attachments)
            {
                if (string.IsNullOrWhiteSpace(attachment.Data))
                    continue;

                byte[] bytes;
                try
                {
                    bytes = Convert.FromBase64String(attachment.Data);
                }
                catch
                {
                    continue;
                }

                var clamped = attachment.LockAspectRatio
                    ? NoteImageResizeMath.ClampDisplaySize(attachment.Width, attachment.Height)
                    : NoteImageResizeMath.ClampFreeformSize(attachment.Width, attachment.Height);
                var width = clamped.Width;
                var height = clamped.Height;

                if (!NoteImageLoader.TryLoadFromBytes(bytes, width, dpi, out var load, out _) || load is null)
                    continue;

                var id = attachment.Id == Guid.Empty ? Guid.NewGuid() : attachment.Id;
                var session = new NoteImageSession
                {
                    Id = id,
                    Bytes = load.Bytes,
                    NaturalPixelWidth = load.NaturalPixelWidth,
                    NaturalPixelHeight = load.NaturalPixelHeight,
                    DecodePixelWidth = load.DecodePixelWidth,
                    DocumentLeft = attachment.Left,
                    DocumentTop = attachment.Top,
                    Width = width,
                    Height = height,
                    LockAspectRatio = attachment.LockAspectRatio
                };
                _sessions[id] = session;

                var widget = CreateWidget(session, load.DisplaySource);
                _overlay.Children.Add(widget);
            }

            EnsureImageExtentPad();
            foreach (var widget in EnumerateWidgets())
            {
                if (_sessions.TryGetValue(widget.ImageId, out var session))
                    ClampSessionAndProject(session, widget);
                else
                    ClampWidgetToDocument(widget);
                SyncWidget(widget);
            }
        }
        finally
        {
            _isRestoring = false;
            Select(null);
            ScheduleSync();
        }
    }

    public bool TryHandleKeyDown(KeyEventArgs e)
    {
        if (_selected is null)
            return false;

        if (e.Key == Key.Escape)
        {
            Select(null);
            e.Handled = true;
            return true;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            RemoveWidget(_selected);
            e.Handled = true;
            return true;
        }

        return false;
    }

    public void ClearSelection() => Select(null);

    public bool SelectImage(Guid imageId)
    {
        foreach (var widget in EnumerateWidgets())
        {
            if (widget.ImageId == imageId)
            {
                Select(widget);
                return true;
            }
        }

        return false;
    }

    private void Select(NoteImageWidget? widget)
    {
        if (ReferenceEquals(_selected, widget))
            return;

        if (_selected is not null)
            _selected.IsSelected = false;

        _selected = widget;

        if (_selected is not null)
        {
            _selected.IsSelected = true;
            Panel.SetZIndex(_selected, 1000);
            foreach (var other in EnumerateWidgets())
            {
                if (!ReferenceEquals(other, _selected))
                    Panel.SetZIndex(other, 1);
            }
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    public void RemoveWidget(NoteImageWidget widget)
    {
        if (_overlay is null)
            return;

        UnhookWidget(widget);
        _overlay.Children.Remove(widget);
        _sessions.Remove(widget.ImageId);
        if (ReferenceEquals(_selected, widget))
            _selected = null;

        RaiseImagesChanged();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        EnsureImageExtentPad();
    }

    public void SyncAll()
    {
        if (_overlay is null)
            return;

        // Pad growth during body-drag stays on BoundsChanging + drag-timer — scroll sync
        // must not re-enter EnsureImageExtentPad (layout lag / capture churn).
        var bodyDragging = _gestureWidget is { IsBodyDragging: true };
        if (!bodyDragging)
            EnsureImageExtentPad();

        foreach (var widget in EnumerateWidgets())
        {
            if (!bodyDragging)
            {
                if (_sessions.TryGetValue(widget.ImageId, out var session))
                    ClampSessionAndProject(session, widget);
                else
                    ClampWidgetToDocument(widget);
            }

            SyncWidget(widget);
        }
    }

    /// <summary>
    /// Grows or shrinks a tagged trailing spacer so floating images below text remain
    /// scrollable. Safe to call often; does not raise <see cref="ImagesChanged"/>.
    /// </summary>
    public void EnsureImageExtentPad()
    {
        if (_editor?.Document is null)
            return;

        _scrollViewer ??= FindScrollViewer(_editor);

        var maxImageBottom = 0.0;
        foreach (var session in _sessions.Values)
            maxImageBottom = Math.Max(maxImageBottom, session.DocumentBottom);

        // Prefer live widget bottoms during gestures (session may lag one frame on resize).
        foreach (var widget in EnumerateWidgets())
            maxImageBottom = Math.Max(maxImageBottom, widget.DocumentTop + widget.Height);

        var edgeReserve = 0.0;
        if (_gestureWidget is { IsBodyDragging: true } && _scrollViewer is not null)
        {
            var pointer = Mouse.GetPosition(_scrollViewer);
            var viewportH = _scrollViewer.ViewportHeight;
            if (viewportH > 0 && pointer.Y > viewportH - DragScrollEdgeThresholdDip)
            {
                edgeReserve = _scrollViewer.VerticalOffset + viewportH + DragScrollMaxStepDip * 3;
            }
        }

        var neededBottom = NoteImageLayoutMath.ResolveNeededDocumentBottom(
            maxImageBottom,
            edgeReserve,
            ExtentPadSlackDip);

        ApplyExtentPad(neededBottom);
    }

    /// <summary>
    /// Removes the floating-image extent spacer so note XAML serialization stays clean.
    /// </summary>
    public void StripImageExtentPad()
    {
        if (_editor?.Document is null)
        {
            _extentPadHeight = 0;
            _padBaseContentBottom = 0;
            return;
        }

        ApplyExtentPad(0);
    }

    private void ApplyExtentPad(double neededBottom)
    {
        if (_editor?.Document is null)
            return;

        _scrollViewer ??= FindScrollViewer(_editor);
        var document = _editor.Document;
        var existing = FindExtentPadBlock(document);
        var bodyDragging = _gestureWidget is { IsBodyDragging: true };

        var extent = _scrollViewer?.ExtentHeight ?? 0;
        if (extent <= 0 && _editor.ActualHeight > 0)
            extent = Math.Max(0, _editor.ActualHeight - _editor.Padding.Top - _editor.Padding.Bottom);

        // Prefer the last known content bottom so pad growth does not depend on stale Extent.
        var contentBottom = _padBaseContentBottom > 0
            ? _padBaseContentBottom
            : Math.Max(0, extent - _extentPadHeight);
        if (_padBaseContentBottom <= 0 && extent > 0)
            _padBaseContentBottom = contentBottom;

        var neededPad = NoteImageLayoutMath.ResolveExtentPadHeight(neededBottom, contentBottom);

        // Avoid tiny churn from layout noise.
        if (Math.Abs(neededPad - _extentPadHeight) < 0.5 && (neededPad > 0.5) == (existing is not null))
            return;

        _isAdjustingExtentPad = true;
        try
        {
            if (neededPad < 1)
            {
                // Do not tear down the spacer mid-drag — capture/layout churn ends the gesture.
                if (bodyDragging)
                {
                    if (existing is BlockUIContainer { Child: FrameworkElement shrinkHost })
                        shrinkHost.Height = 0;
                    _extentPadHeight = 0;
                    return;
                }

                if (existing is not null)
                    document.Blocks.Remove(existing);
                _extentPadHeight = 0;
                _padBaseContentBottom = 0;
                return;
            }

            if (existing is BlockUIContainer { Child: FrameworkElement padHost })
            {
                padHost.Height = neededPad;
                // Never Remove+Re-Add during body-drag (steals mouse capture).
                if (!bodyDragging && !ReferenceEquals(document.Blocks.LastBlock, existing))
                {
                    document.Blocks.Remove(existing);
                    document.Blocks.Add(existing);
                }
            }
            else
            {
                if (existing is not null && !bodyDragging)
                    document.Blocks.Remove(existing);

                if (existing is null || !bodyDragging)
                {
                    var spacer = new FrameworkElement
                    {
                        Height = neededPad,
                        IsHitTestVisible = false,
                        Focusable = false
                    };
                    var pad = new BlockUIContainer(spacer)
                    {
                        Tag = ExtentPadTag,
                        Margin = new Thickness(0),
                        Padding = new Thickness(0)
                    };
                    document.Blocks.Add(pad);
                }
            }

            _extentPadHeight = neededPad;

            // Synchronous UpdateLayout mid-drag steals capture; let the next layout pass settle.
            if (!bodyDragging)
            {
                _editor.UpdateLayout();
                _scrollViewer ??= FindScrollViewer(_editor);
            }
        }
        finally
        {
            _isAdjustingExtentPad = false;
        }
    }

    private static Block? FindExtentPadBlock(FlowDocument document)
    {
        foreach (var block in document.Blocks)
        {
            if (Equals(block.Tag, ExtentPadTag))
                return block;
        }

        return null;
    }

    private NoteImageWidget CreateWidget(NoteImageSession session, ImageSource source)
    {
        var widget = new NoteImageWidget
        {
            ImageId = session.Id,
            Source = source
        };
        ProjectSessionToWidget(session, widget);
        HookWidget(widget);
        return widget;
    }

    private void HookWidget(NoteImageWidget widget)
    {
        widget.SelectionRequested += Widget_SelectionRequested;
        widget.BoundsChanging += Widget_BoundsChanging;
        widget.BoundsChanged += Widget_BoundsChanged;
        widget.GestureStarted += Widget_GestureStarted;
        widget.GestureCompleted += Widget_GestureCompleted;
        widget.DeleteRequested += Widget_DeleteRequested;
        widget.LockAspectRatioChanged += Widget_LockAspectRatioChanged;
    }

    private void UnhookWidget(NoteImageWidget widget)
    {
        widget.SelectionRequested -= Widget_SelectionRequested;
        widget.BoundsChanging -= Widget_BoundsChanging;
        widget.BoundsChanged -= Widget_BoundsChanged;
        widget.GestureStarted -= Widget_GestureStarted;
        widget.GestureCompleted -= Widget_GestureCompleted;
        widget.DeleteRequested -= Widget_DeleteRequested;
        widget.LockAspectRatioChanged -= Widget_LockAspectRatioChanged;
    }

    private void Widget_SelectionRequested(object? sender, EventArgs e)
    {
        if (sender is NoteImageWidget widget)
            Select(widget);
    }

    private void Widget_DeleteRequested(object? sender, EventArgs e)
    {
        if (sender is NoteImageWidget widget)
            RemoveWidget(widget);
    }

    private void Widget_LockAspectRatioChanged(object? sender, EventArgs e)
    {
        if (sender is not NoteImageWidget widget)
            return;

        if (_sessions.TryGetValue(widget.ImageId, out var session))
        {
            session.LockAspectRatio = widget.LockAspectRatio;
            CommitWidgetGeometryToSession(session, widget);
            ClampSessionAndProject(session, widget);
            SyncWidget(widget);
        }

        if (!_isRestoring)
            RaiseImagesChanged();
    }

    private void Widget_BoundsChanging(object? sender, EventArgs e)
    {
        if (sender is not NoteImageWidget widget)
            return;

        if (!_sessions.TryGetValue(widget.ImageId, out var session))
        {
            ClampWidgetToDocument(widget);
            SyncWidget(widget);
            return;
        }

        if (widget.IsBodyDragging)
        {
            FollowPointer(widget);
            // Grow extent before clamp so downward drag past text content is not pinned.
            EnsureImageExtentPad();
        }
        else
        {
            // Resize stages geometry on the widget; commit to session (owner) then project.
            CommitWidgetGeometryToSession(session, widget);
            EnsureImageExtentPad();
        }

        ClampSessionAndProject(session, widget);
        SyncWidget(widget);
    }

    private void Widget_BoundsChanged(object? sender, EventArgs e)
    {
        if (sender is not NoteImageWidget widget)
            return;

        if (_sessions.TryGetValue(widget.ImageId, out var session))
        {
            CommitWidgetGeometryToSession(session, widget);
            EnsureImageExtentPad();
            ClampSessionAndProject(session, widget);
        }
        else
        {
            EnsureImageExtentPad();
            ClampWidgetToDocument(widget);
        }

        SyncWidget(widget);
        MaybeRedeocodeIfNeeded(widget);
        if (!_isRestoring)
            RaiseImagesChanged();
    }

    private void Widget_GestureStarted(object? sender, EventArgs e)
    {
        _activeGestures++;
        if (sender is NoteImageWidget widget)
        {
            _gestureWidget = widget;
            if (widget.IsBodyDragging)
            {
                CaptureDragGrabOffset(widget);
                StartDragScrollTimer();
            }
        }
    }

    private void Widget_GestureCompleted(object? sender, EventArgs e)
    {
        _activeGestures = Math.Max(0, _activeGestures - 1);
        if (_activeGestures == 0)
        {
            StopDragScrollTimer();
            _gestureWidget = null;
            _hasDragGrab = false;
            EnsureImageExtentPad();
        }
    }

    private void StartDragScrollTimer()
    {
        if (_dragScrollTimer is not null)
            return;

        _dragScrollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(20)
        };
        _dragScrollTimer.Tick += DragScrollTimer_Tick;
        _dragScrollTimer.Start();
    }

    private void StopDragScrollTimer()
    {
        if (_dragScrollTimer is null)
            return;

        _dragScrollTimer.Stop();
        _dragScrollTimer.Tick -= DragScrollTimer_Tick;
        _dragScrollTimer = null;
    }

    private void DragScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_activeGestures <= 0 || _gestureWidget is null || !_gestureWidget.IsBodyDragging)
        {
            StopDragScrollTimer();
            return;
        }

        TryEdgeAutoScroll(_gestureWidget);
        // Follow first so pad/clamp use the pointer target (avoids vanishing when dragged deep).
        FollowPointer(_gestureWidget);
        EnsureImageExtentPad();
        if (_sessions.TryGetValue(_gestureWidget.ImageId, out var session))
            ClampSessionAndProject(session, _gestureWidget);
        else
            ClampWidgetToDocument(_gestureWidget);
        SyncWidget(_gestureWidget);
    }

    /// <summary>
    /// Scrolls the editor when the pointer is near a viewport edge during an image gesture.
    /// Placement under the cursor is handled by <see cref="FollowPointer"/> after this returns.
    /// </summary>
    private bool TryEdgeAutoScroll(NoteImageWidget widget)
    {
        _scrollViewer ??= _editor is null ? null : FindScrollViewer(_editor);
        if (_scrollViewer is null || _editor is null)
            return false;

        var pointer = Mouse.GetPosition(_scrollViewer);
        var viewportW = _scrollViewer.ViewportWidth;
        var viewportH = _scrollViewer.ViewportHeight;
        if (viewportW <= 0 || viewportH <= 0)
            return false;

        var beforeX = _scrollViewer.HorizontalOffset;
        var beforeY = _scrollViewer.VerticalOffset;
        var edge = DragScrollEdgeThresholdDip;

        var canScrollY = _scrollViewer.ScrollableHeight > 0.5
            || _scrollViewer.ExtentHeight > viewportH + 0.5;
        var canScrollX = _scrollViewer.ScrollableWidth > 0.5
            || _scrollViewer.ExtentWidth > viewportW + 0.5;

        if (pointer.Y < edge && canScrollY)
        {
            var step = ResolveDragScrollStep(edge - pointer.Y);
            _scrollViewer.ScrollToVerticalOffset(Math.Max(0, beforeY - step));
        }
        else if (pointer.Y > viewportH - edge && canScrollY)
        {
            var step = ResolveDragScrollStep(pointer.Y - (viewportH - edge));
            _scrollViewer.ScrollToVerticalOffset(Math.Min(_scrollViewer.ScrollableHeight, beforeY + step));
        }

        if (pointer.X < edge && canScrollX)
        {
            var step = ResolveDragScrollStep(edge - pointer.X);
            _scrollViewer.ScrollToHorizontalOffset(Math.Max(0, beforeX - step));
        }
        else if (pointer.X > viewportW - edge && canScrollX)
        {
            var step = ResolveDragScrollStep(pointer.X - (viewportW - edge));
            _scrollViewer.ScrollToHorizontalOffset(Math.Min(_scrollViewer.ScrollableWidth, beforeX + step));
        }

        var dx = _scrollViewer.HorizontalOffset - beforeX;
        var dy = _scrollViewer.VerticalOffset - beforeY;
        return Math.Abs(dx) >= 0.01 || Math.Abs(dy) >= 0.01;
    }

    private void CaptureDragGrabOffset(NoteImageWidget widget)
    {
        if (_overlay is null)
        {
            _hasDragGrab = false;
            return;
        }

        var canvasPt = Mouse.GetPosition(_overlay);
        var left = Canvas.GetLeft(widget);
        var top = Canvas.GetTop(widget);
        if (double.IsNaN(left))
            left = 0;
        if (double.IsNaN(top))
            top = 0;

        _dragGrabOffset = new Point(canvasPt.X - left, canvasPt.Y - top);
        _hasDragGrab = true;
    }

    /// <summary>
    /// Places the image so the grab point stays under the cursor. Session is the geometry owner;
    /// the widget mirror is updated via <see cref="ProjectSessionToWidget"/>.
    /// </summary>
    private void FollowPointer(NoteImageWidget widget)
    {
        if (!_hasDragGrab || _overlay is null || _editor is null)
            return;

        if (!_sessions.TryGetValue(widget.ImageId, out var session))
            return;

        var canvasPt = Mouse.GetPosition(_overlay);
        var offset = GetViewportOffset();
        var padding = _editor.Padding;
        var (left, top) = NoteImageLayoutMath.DocumentOriginFromPointer(
            offset.X,
            offset.Y,
            canvasPt.X,
            canvasPt.Y,
            padding.Left,
            padding.Top,
            _dragGrabOffset.X,
            _dragGrabOffset.Y);

        session.SetGeometry(left, top, session.Width, session.Height);
        ProjectSessionToWidget(session, widget);
    }

    private static double ResolveDragScrollStep(double edgeDistance)
    {
        var t = Math.Clamp(edgeDistance / DragScrollEdgeThresholdDip, 0, 1);
        // Quadratic ease-in: crawl near the edge zone threshold, accelerate deep in.
        return DragScrollMinStepDip + ((DragScrollMaxStepDip - DragScrollMinStepDip) * t * t);
    }

    private void MaybeRedeocodeIfNeeded(NoteImageWidget widget)
    {
        if (!_sessions.TryGetValue(widget.ImageId, out var session) || session.Bytes.Length == 0)
            return;

        var dpi = GetDpiScale();
        var needed = NoteImageLoader.ResolveDecodePixelWidth(session.Width, dpi, session.NaturalPixelWidth);
        if (needed <= session.DecodePixelWidth)
            return;

        if (!NoteImageLoader.TryCreateDisplaySource(session.Bytes, session.Width, dpi, out var display, out var decodeWidth, out _)
            || display is null)
        {
            return;
        }

        widget.Source = display;
        session.DecodePixelWidth = decodeWidth;
    }

    private static void CommitWidgetGeometryToSession(NoteImageSession session, NoteImageWidget widget)
    {
        session.SetGeometry(widget.DocumentLeft, widget.DocumentTop, widget.Width, widget.Height);
    }

    private static void SyncSessionFromWidget(NoteImageSession session, NoteImageWidget widget)
        => CommitWidgetGeometryToSession(session, widget);

    private static void ProjectSessionToWidget(NoteImageSession session, NoteImageWidget widget)
    {
        widget.LockAspectRatio = session.LockAspectRatio;
        widget.DocumentLeft = session.DocumentLeft;
        widget.DocumentTop = session.DocumentTop;
        if (!widget.Width.Equals(session.Width) || !widget.Height.Equals(session.Height))
            widget.SetDisplaySize(session.Width, session.Height);
    }

    private void SyncWidget(NoteImageWidget widget)
    {
        if (_overlay is null || _editor is null)
            return;

        var offset = GetViewportOffset();
        var padding = _editor.Padding;
        double left;
        double top;
        if (_sessions.TryGetValue(widget.ImageId, out var session))
        {
            (left, top) = NoteImageLayoutMath.CanvasFromDocument(
                session.DocumentLeft,
                session.DocumentTop,
                offset.X,
                offset.Y,
                padding.Left,
                padding.Top);
        }
        else
        {
            (left, top) = NoteImageLayoutMath.CanvasFromDocument(
                widget.DocumentLeft,
                widget.DocumentTop,
                offset.X,
                offset.Y,
                padding.Left,
                padding.Top);
        }

        Canvas.SetLeft(widget, left);
        Canvas.SetTop(widget, top);
    }

    private void ClampSessionAndProject(NoteImageSession session, NoteImageWidget widget)
    {
        var (boundsW, boundsH) = GetDocumentBounds();
        var clamped = NoteImageResizeMath.ClampRectToBounds(
            session.DocumentLeft,
            session.DocumentTop,
            session.Width,
            session.Height,
            boundsW,
            boundsH,
            session.LockAspectRatio);

        session.SetGeometry(clamped.Left, clamped.Top, clamped.Width, clamped.Height);
        ProjectSessionToWidget(session, widget);
    }

    private void ClampWidgetToDocument(NoteImageWidget widget)
    {
        if (_sessions.TryGetValue(widget.ImageId, out var session))
        {
            CommitWidgetGeometryToSession(session, widget);
            ClampSessionAndProject(session, widget);
            return;
        }

        var (boundsW, boundsH) = GetDocumentBounds();
        var clamped = NoteImageResizeMath.ClampRectToBounds(
            widget.DocumentLeft,
            widget.DocumentTop,
            widget.Width,
            widget.Height,
            boundsW,
            boundsH);

        widget.DocumentLeft = clamped.Left;
        widget.DocumentTop = clamped.Top;
        if (!widget.Width.Equals(clamped.Width) || !widget.Height.Equals(clamped.Height))
            widget.SetDisplaySize(clamped.Width, clamped.Height);
    }

    private (double Width, double Height) GetDocumentBounds()
    {
        _scrollViewer ??= _editor is null ? null : FindScrollViewer(_editor);
        double? liveDragBottom = null;
        if (_gestureWidget is { IsBodyDragging: true }
            && _sessions.TryGetValue(_gestureWidget.ImageId, out var gestureSession))
        {
            liveDragBottom = gestureSession.DocumentBottom;
        }
        else if (_gestureWidget is { IsBodyDragging: true })
        {
            liveDragBottom = _gestureWidget.DocumentTop + _gestureWidget.Height;
        }

        if (_scrollViewer is not null)
        {
            var width = Math.Max(_scrollViewer.ExtentWidth, _scrollViewer.ViewportWidth);
            var height = NoteImageLayoutMath.ResolveDocumentBoundsHeight(
                _scrollViewer.ExtentHeight,
                _scrollViewer.ViewportHeight,
                _padBaseContentBottom,
                _extentPadHeight,
                liveDragBottom);
            if (width > 0 && height > 0)
                return (width, height);
        }

        if (_editor is not null)
        {
            var width = Math.Max(0, _editor.ActualWidth - _editor.Padding.Left - _editor.Padding.Right);
            var height = Math.Max(0, _editor.ActualHeight - _editor.Padding.Top - _editor.Padding.Bottom);
            height = NoteImageLayoutMath.ResolveDocumentBoundsHeight(
                height,
                height,
                _padBaseContentBottom,
                _extentPadHeight,
                liveDragBottom);
            if (width > 0 && height > 0)
                return (width, height);
        }

        return (400, 300);
    }

    private Point GetViewportOffset()
    {
        _scrollViewer ??= _editor is null ? null : FindScrollViewer(_editor);
        if (_scrollViewer is null)
            return new Point(0, 0);

        return new Point(_scrollViewer.HorizontalOffset, _scrollViewer.VerticalOffset);
    }

    private Point ResolveDefaultInsertOrigin(double width, double height)
    {
        if (_editor is null)
            return new Point(24, 24);

        try
        {
            var caret = _editor.CaretPosition?.GetCharacterRect(LogicalDirection.Forward) ?? Rect.Empty;
            if (!caret.IsEmpty && !double.IsNaN(caret.Left) && !double.IsInfinity(caret.Left))
            {
                var offset = GetViewportOffset();
                return new Point(
                    Math.Max(0, offset.X + caret.Left),
                    Math.Max(0, offset.Y + caret.Bottom + 8));
            }
        }
        catch
        {
            // Fall through to viewport center.
        }

        var view = GetViewportOffset();
        var viewportWidth = _editor.ActualWidth > 0 ? _editor.ActualWidth : 400;
        var viewportHeight = _editor.ActualHeight > 0 ? _editor.ActualHeight : 300;
        return new Point(
            Math.Max(0, view.X + (viewportWidth - width) / 2),
            Math.Max(0, view.Y + (viewportHeight - height) / 2));
    }

    private double GetDpiScale()
    {
        var source = _overlay is null ? null : PresentationSource.FromVisual(_overlay);
        if (source?.CompositionTarget is null)
            return 1.0;
        return source.CompositionTarget.TransformToDevice.M11;
    }

    private void ScheduleSync()
    {
        if (_syncScheduled || _overlay is null)
            return;

        _syncScheduled = true;
        var generation = _attachGeneration;
        var overlay = _overlay;
        overlay.Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (generation != _attachGeneration)
                return;

            _syncScheduled = false;
            SyncAll();
        });
    }

    private void Host_SizeChanged(object sender, SizeChangedEventArgs e) => ScheduleSync();

    private void Editor_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.HorizontalChange == 0 && e.VerticalChange == 0)
            return;
        ScheduleSync();
    }

    private void Editor_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeGestures > 0)
            return;

        if (e.OriginalSource is DependencyObject source
            && FindAncestor<NoteImageWidget>(source) is null)
        {
            Select(null);
        }
    }

    private void RaiseImagesChanged() => ImagesChanged?.Invoke(this, EventArgs.Empty);

    private IEnumerable<NoteImageWidget> EnumerateWidgets()
    {
        if (_overlay is null)
            yield break;

        foreach (var child in _overlay.Children)
        {
            if (child is NoteImageWidget widget)
                yield return widget;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
            return sv;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            var found = FindScrollViewer(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
                return match;

            // Mouse events on RichTextBox text originate from content elements (Run, Paragraph,
            // FlowDocument) that are not Visuals - VisualTreeHelper.GetParent throws for those,
            // which aborts the whole input event (caret placement/focus never runs). Fall back
            // to the logical tree for content elements, same as SmoothScrollBehavior.GetParentObject.
            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }
}
