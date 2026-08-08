using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Scriblucent.Controls;
using Scriblucent.Localization;
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
    internal const char InlineMarkerChar = '\uFFFC';

    private const double DragScrollEdgeThresholdDip = 64;
    private const double DragScrollMinStepDip = 4;
    private const double DragScrollMaxStepDip = 48;
    private const double DragScrollExitMarginDip = 32;
    private const double ExtentPadSlackDip = 24;
    private const double ButtonsFlipThresholdDip = 40;

    private Canvas? _overlay;
    private RichTextBox? _editor;
    private FrameworkElement? _surface;
    private ScrollViewer? _scrollViewer;
    private NoteImageWidget? _selected;
    private Guid? _selectedInlineId;
    private NoteImageWidget? _gestureWidget;
    private Point _dragGrabOffset;
    private bool _hasDragGrab;
    private DispatcherTimer? _dragScrollTimer;
    private int _edgeScrollDirectionY;
    private int _edgeScrollDirectionX;
    private bool _syncScheduled;
    private bool _isRestoring;
    private bool _isAdjustingExtentPad;
    private double _extentPadHeight;
    private int _activeGestures;
    private int _attachGeneration;
    private readonly Dictionary<Guid, NoteImageSession> _sessions = new();
    private readonly Dictionary<Guid, InlineUIContainer> _inlineContainers = new();
    private readonly Dictionary<Paragraph, LineStackingStrategy> _inlineStrategyOverrides = new();
    private readonly Dictionary<Guid, TextBlock> _captionElements = new();
    private List<Guid>? _pendingInlineRestoreOrder;

    /// <summary>True while the host window is loading document content into the flow (before
    /// <see cref="Restore"/> runs). The freshly loaded in-line markers are legitimate there -
    /// the undo/redo sweep must not remove them mid-load.</summary>
    public bool SuppressMarkerSweep { get; set; }

    public event EventHandler? ImagesChanged;

    /// <summary>Raised when the user asks to copy a photo (the window owns the clipboard).</summary>
    public event EventHandler<Guid>? CopyRequested;

    /// <summary>Raised when the user asks to cut a photo (copy, then remove the original).</summary>
    public event EventHandler<Guid>? CutRequested;

    /// <summary>Raised when the user asks to replace a photo's picture (the window owns the file dialog).</summary>
    public event EventHandler<Guid>? ChangePictureRequested;

    /// <summary>Raised when the user asks to insert/edit a photo's caption (the window owns the dialog).</summary>
    public event EventHandler<Guid>? CaptionRequested;

    /// <summary>Raised continuously while an image's geometry is being dragged or resized, so
    /// consumers can refresh the wrap layout live; the final state is reported once through
    /// <see cref="ImagesChanged"/> when the gesture ends.</summary>
    public event EventHandler? ImagesGeometryChanging;
    public event EventHandler? SelectionChanged;

    public bool HasSelection => _selected is not null || _selectedInlineId is not null;
    public Guid? SelectedImageId => _selected?.ImageId ?? _selectedInlineId;
    public bool IsAdjustingExtentPad => _isAdjustingExtentPad;
    public bool HasActiveGestures => _activeGestures > 0;

    /// <summary>
    /// Captures the scroll offset and caret so the view can be preserved across a
    /// serialization cycle - the save path strips the extent pad and the inline photo
    /// containers out of the live flow, and any layout pass in between otherwise clamps the
    /// scroll / snaps the caret (the view jumps, e.g. back to the top of the note).
    /// </summary>
    public (Point ScrollOffset, TextPointer? Caret) CaptureViewState()
        => (GetViewportOffset(), _editor?.CaretPosition);

    /// <summary>Restores the state captured by <see cref="CaptureViewState"/>.</summary>
    public void RestoreViewState((Point ScrollOffset, TextPointer? Caret) state)
    {
        if (_editor is null)
            return;

        // Lay out first so the flow is clean when the caret is placed: with the layout dirty,
        // the RichTextBox defers its caret-following (MakeCaretVisible) to the next layout
        // pass - after any re-assert we queue - and scrolls to the caret, teleporting the view
        // away from a photo that is far from the caret's text.
        _editor.UpdateLayout();

        if (state.Caret is not null)
        {
            try
            {
                // The captured pointer is already a caret position - setting it directly
                // preserves the exact spot (an insertion-position conversion can snap the
                // caret a couple of symbols back at the document edges).
                _editor.CaretPosition = state.Caret;
            }
            catch
            {
                // The position may be stale after the flow round-trip - the caret is best effort.
            }
        }

        _scrollViewer ??= FindScrollViewer(_editor);
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollToHorizontalOffset(state.ScrollOffset.X);
            _scrollViewer.ScrollToVerticalOffset(state.ScrollOffset.Y);

            // The caret-set above can still make the RichTextBox scroll the caret into view
            // (settling past this method). Re-assert the offsets after the frame's layout
            // pass - the Loaded priority runs below Render - so the view always lands exactly
            // where it was captured, no matter what the caret-following does.
            var scrollX = state.ScrollOffset.X;
            var scrollY = state.ScrollOffset.Y;
            var viewer = _scrollViewer;
            _editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                viewer.ScrollToHorizontalOffset(scrollX);
                viewer.ScrollToVerticalOffset(scrollY);
            });
        }
    }

    /// <summary>Sessions whose wrap mode is active for the layout engine. Inline sessions are
    /// part of the text flow itself, so the engine must never touch them.</summary>
    public IReadOnlyList<NoteImageSession> GetWrapSessions()
    {
        if (_sessions.Count == 0)
            return Array.Empty<NoteImageSession>();

        return _sessions.Values
            .Where(session => session.WrapMode is NoteImageWrapMode.Square or NoteImageWrapMode.TopAndBottom)
            .ToList();
    }

    /// <summary>
    /// Sets the text wrap mode for an image; switching to/from
    /// <see cref="NoteImageWrapMode.Inline"/> moves the image between the canvas overlay and
    /// the text flow. Persisted through <see cref="BuildAttachments"/>.
    /// </summary>
    public void SetWrapMode(Guid imageId, NoteImageWrapMode mode)
    {
        if (!_sessions.TryGetValue(imageId, out var session))
            return;

        if (session.WrapMode == mode)
        {
            if (!_isRestoring)
                RaiseImagesChanged();
            return;
        }

        session.WrapMode = mode;
        if (mode == NoteImageWrapMode.Inline)
            ConvertSessionToInline(session);
        else
            ConvertSessionToFloating(session);

        if (!_isRestoring)
            RaiseImagesChanged();
    }

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
        _editor.TextChanged += Editor_TextChanged;

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
            _editor.TextChanged -= Editor_TextChanged;
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

        foreach (var container in _inlineContainers.Values)
            GetInlineHost(container)?.Remove(container);

        _overlay.Children.Clear();
        _inlineContainers.Clear();
        _sessions.Clear();
        _captionElements.Clear();
        _selected = null;
        _selectedInlineId = null;
        _pendingInlineRestoreOrder = null;
        // Safety net: inline elements are not unhooked here, so a mid-gesture teardown could
        // otherwise leave _activeGestures > 0 forever (silently disabling the wheel guard).
        _activeGestures = 0;
        _gestureWidget = null;
        _hasDragGrab = false;
        StopDragScrollTimer();
        SyncInlineParagraphLineStrategies(); // restores line strategies before the overrides are dropped
        _inlineStrategyOverrides.Clear();
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
        foreach (var session in _sessions.Values)
        {
            if (session.Bytes.Length == 0)
                continue;

            if (session.WrapMode != NoteImageWrapMode.Inline && FindWidget(session.Id) is { } widget)
                SyncSessionFromWidget(session, widget);

            list.Add(new NoteImageAttachment
            {
                Id = session.Id,
                Data = Convert.ToBase64String(session.Bytes),
                Width = session.Width,
                Height = session.Height,
                Left = session.DocumentLeft,
                Top = session.DocumentTop,
                LockAspectRatio = session.LockAspectRatio,
                WrapMode = session.WrapMode,
                InlineAnchorOffset = session.InlineAnchorOffset,
                ZIndex = session.ZIndex,
                CaptionLabel = session.CaptionLabel,
                CaptionText = session.CaptionText,
                CaptionNumbering = session.CaptionNumbering,
                CaptionSeparator = session.CaptionSeparator,
                CaptionPosition = session.CaptionPosition,
                CaptionExcludeLabel = session.CaptionExcludeLabel
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
            var pendingInline = new List<NoteImageSession>();
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
                    LockAspectRatio = attachment.LockAspectRatio,
                    WrapMode = attachment.WrapMode,
                    InlineAnchorOffset = attachment.InlineAnchorOffset,
                    ZIndex = attachment.ZIndex,
                    CaptionLabel = attachment.CaptionLabel,
                    CaptionText = attachment.CaptionText,
                    CaptionNumbering = attachment.CaptionNumbering,
                    CaptionSeparator = string.IsNullOrEmpty(attachment.CaptionSeparator) ? ": " : attachment.CaptionSeparator,
                    CaptionPosition = attachment.CaptionPosition,
                    CaptionExcludeLabel = attachment.CaptionExcludeLabel
                };
                _sessions[id] = session;

                if (session.WrapMode == NoteImageWrapMode.Inline)
                {
                    // Anchored in the text flow; inserted after the load pass below.
                    pendingInline.Add(session);
                    continue;
                }

                var widget = CreateWidget(session, load.DisplaySource);
                _overlay.Children.Add(widget);
            }

            EnsureImageExtentPad();
            var boundsMeasurable = TryGetMeasuredBounds(out var boundsW, out var boundsH);
            // Materialized: SyncWidget can add caption elements to the overlay mid-iteration.
            foreach (var widget in EnumerateWidgets().ToList())
            {
                _sessions.TryGetValue(widget.ImageId, out var restoredSession);
                if (!boundsMeasurable)
                {
                    // The editor is not laid out yet - keep the stored geometry untouched; the
                    // post-layout sync re-clamps against the real bounds.
                    if (restoredSession is not null)
                        ProjectSessionToWidget(restoredSession, widget);
                    SyncWidget(widget);
                    continue;
                }

                if (restoredSession is not null)
                {
                    var clamped = NoteImageResizeMath.ClampRectToBounds(
                        restoredSession.DocumentLeft,
                        restoredSession.DocumentTop,
                        restoredSession.Width,
                        restoredSession.Height,
                        boundsW,
                        boundsH,
                        restoredSession.LockAspectRatio,
                        resizeToFit: false);
                    restoredSession.SetGeometry(clamped.Left, clamped.Top, clamped.Width, clamped.Height);
                    ProjectSessionToWidget(restoredSession, widget);
                }
                else
                {
                    ClampWidgetToDocument(widget);
                }

                SyncWidget(widget);
            }

            // Restore the persisted stacking order (the saved ZIndex values may differ from the
            // attachment list order after previous reorders).
            ApplyStackingOrder();

            // "In line" sessions: replace their text markers with inline elements. Sessions
            // without a matching marker fall back to floating at their stored position.
            RestoreInlineSessions(pendingInline);
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
        if (_selected is null && _selectedInlineId is null)
            return false;

        if (e.Key == Key.Escape)
        {
            Select(null);
            e.Handled = true;
            return true;
        }

        if (e.Key is Key.Delete or Key.Back)
        {
            if (_selectedInlineId is { } inlineId)
                RemoveInlineImage(inlineId);
            else if (_selected is not null)
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

        if (_inlineContainers.ContainsKey(imageId))
        {
            SelectInline(imageId);
            return true;
        }

        return false;
    }

    private void SelectInline(Guid imageId)
    {
        if (_selectedInlineId == imageId)
            return;

        if (_selected is not null)
        {
            _selected.IsSelected = false;
            _selected = null;
        }

        if (_selectedInlineId is { } previous
            && _inlineContainers.TryGetValue(previous, out var previousContainer)
            && previousContainer.Child is InlineNoteImageElement previousElement)
        {
            previousElement.IsSelected = false;
        }

        _selectedInlineId = imageId;
        if (_inlineContainers.TryGetValue(imageId, out var container))
        {
            if (container.Child is InlineNoteImageElement element)
                element.IsSelected = true;

            // Park the editor's caret right AFTER the photo. Clicks on the element are swallowed
            // (e.Handled) so the caret never moves, yet WPF scrolls the caret into view when
            // the editor gains focus - with a stale caret at the top of the note that would
            // teleport the view. Anchoring here keeps any focus-scroll at the photo, and the
            // position is deterministic and visible (typing continues after the photo, not
            // before it - where the user cannot tell the caret will land).
            if (_editor is not null)
                ParkCaretPreservingView(container.ContentEnd);
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Select(NoteImageWidget? widget)
    {
        if (_selectedInlineId is { } inlineId)
        {
            if (_inlineContainers.TryGetValue(inlineId, out var inlineContainer)
                && inlineContainer.Child is InlineNoteImageElement inlineElement)
            {
                inlineElement.IsSelected = false;
            }

            _selectedInlineId = null;
        }

        if (ReferenceEquals(_selected, widget))
            return;

        if (_selected is not null)
            _selected.IsSelected = false;

        _selected = widget;

        if (_selected is not null)
            _selected.IsSelected = true;

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the caret to the photo's text position without letting the RichTextBox scroll it
    /// into view. The anchor protects against a later focus-scroll to a stale top caret, but
    /// the click itself must not shift the view - a photo near the viewport edge would jump
    /// under the cursor mid-click-and-drag.
    /// </summary>
    private void ParkCaretPreservingView(TextPointer position)
    {
        if (_editor is null)
            return;

        var offset = GetViewportOffset();
        _editor.CaretPosition = position.GetInsertionPosition(LogicalDirection.Forward) ?? position;
        if (_scrollViewer is not null)
        {
            _scrollViewer.ScrollToHorizontalOffset(offset.X);
            _scrollViewer.ScrollToVerticalOffset(offset.Y);

            // The caret-set above can make the RichTextBox scroll the caret into view on the
            // NEXT layout pass. Re-assert the offsets after the frame's layout - same pattern
            // as RestoreViewState - so the click never shifts the text under the cursor.
            var scrollX = offset.X;
            var scrollY = offset.Y;
            var viewer = _scrollViewer;
            _editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
            {
                viewer.ScrollToHorizontalOffset(scrollX);
                viewer.ScrollToVerticalOffset(scrollY);
            });
        }
    }

    /// <summary>Moves a floating image up one position in the stacking order.</summary>
    public void BringForward(Guid imageId)
    {
        if (FindWidget(imageId) is { } widget)
            MoveInStack(widget, +1);
    }

    /// <summary>Moves a floating image down one position in the stacking order.</summary>
    public void SendBackward(Guid imageId)
    {
        if (FindWidget(imageId) is { } widget)
            MoveInStack(widget, -1);
    }

    private void MoveInStack(NoteImageWidget widget, int delta)
    {
        if (_overlay is null)
            return;

        var index = _overlay.Children.IndexOf(widget);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= _overlay.Children.Count)
            return;

        _overlay.Children.Remove(widget);
        _overlay.Children.Insert(target, widget);
        RenumberStackingOrder();
        if (!_isRestoring)
            RaiseImagesChanged();
    }

    /// <summary>Reflects the overlay's children order onto the sessions' ZIndex values.</summary>
    private void RenumberStackingOrder()
    {
        if (_overlay is null)
            return;

        var position = 0;
        foreach (var child in _overlay.Children)
        {
            if (child is NoteImageWidget widget
                && _sessions.TryGetValue(widget.ImageId, out var session))
            {
                session.ZIndex = position++;
            }
        }
    }

    /// <summary>Orders the overlay children by the sessions' stored ZIndex (restore path).</summary>
    private void ApplyStackingOrder()
    {
        if (_overlay is null)
            return;

        var ordered = EnumerateWidgets()
            .OrderBy(widget => _sessions.TryGetValue(widget.ImageId, out var session) ? session.ZIndex : 0)
            .ToList();
        foreach (var widget in ordered)
        {
            _overlay.Children.Remove(widget);
            _overlay.Children.Add(widget);
        }
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

    /// <summary>Removes an in-line image (element + session) from the text flow.</summary>
    public void RemoveInlineImage(Guid imageId)
    {
        if (_overlay is null)
            return;

        if (!_inlineContainers.Remove(imageId, out var container))
            return;

        GetInlineHost(container)?.Remove(container);
        _sessions.Remove(imageId);
        if (_selectedInlineId == imageId)
        {
            _selectedInlineId = null;
            SelectionChanged?.Invoke(this, EventArgs.Empty);
        }

        SyncInlineParagraphLineStrategies();
        RaiseImagesChanged();
        EnsureImageExtentPad();
    }

    /// <summary>Removes an image of either kind (floating widget or in-line element).</summary>
    public void RemoveImage(Guid imageId)
    {
        if (FindWidget(imageId) is { } widget)
            RemoveWidget(widget);
        else
            RemoveInlineImage(imageId);
    }

    /// <summary>Returns the image's stored bytes (for clipboard copy), or null if unknown.</summary>
    public byte[]? GetImageBytes(Guid imageId)
    {
        if (_sessions.TryGetValue(imageId, out var session))
            return (byte[])session.Bytes.Clone();

        return null;
    }

    /// <summary>
    /// Replaces an image's picture data (Word "Change Picture"). The frame keeps its current
    /// width and re-fits its height to the new image's natural aspect ratio; position, wrap
    /// mode and lock-aspect are untouched. Refreshes the displayed source for either kind.
    /// </summary>
    public bool ReplaceImageBytes(Guid imageId, NoteImageLoader.LoadResult load)
    {
        if (load is null || !_sessions.TryGetValue(imageId, out var session))
            return false;

        var width = Math.Max(1, session.Width);
        var fitted = NoteImageResizeMath.ClampDisplaySize(
            width,
            width * (load.NaturalPixelHeight / Math.Max(1, load.NaturalPixelWidth)));

        session.Bytes = load.Bytes;
        session.NaturalPixelWidth = load.NaturalPixelWidth;
        session.NaturalPixelHeight = load.NaturalPixelHeight;
        session.DecodePixelWidth = load.DecodePixelWidth;
        session.Width = fitted.Width;
        session.Height = fitted.Height;

        if (FindWidget(imageId) is { } widget)
        {
            if (DecodeDisplaySource(session) is { } source)
            {
                widget.Source = source;
                ProjectSessionToWidget(session, widget);
            }

            SyncWidget(widget);
        }
        else if (_inlineContainers.TryGetValue(imageId, out var container)
            && container.Child is InlineNoteImageElement element)
        {
            element.SetDisplaySize(session.Width, session.Height);
            if (DecodeDisplaySource(session) is { } source)
                element.Source = source;

            SyncInlineParagraphLineStrategies();
            SyncInlineButtonPlacement();
        }

        EnsureImageExtentPad();
        RaiseImagesChanged();
        return true;
    }

    /// <summary>Caption options, as configured in the Insert Caption dialog.</summary>
    public sealed record CaptionSettings(
        string Label,
        string Text,
        CaptionNumbering Numbering,
        string Separator,
        CaptionPosition Position,
        bool ExcludeLabel);

    /// <summary>Returns the image's current caption settings, or null when it has none.</summary>
    public CaptionSettings? GetCaption(Guid imageId)
    {
        if (!_sessions.TryGetValue(imageId, out var session) || !session.HasCaption)
            return null;

        return new CaptionSettings(
            session.CaptionLabel!,
            session.CaptionText,
            session.CaptionNumbering,
            session.CaptionSeparator,
            session.CaptionPosition,
            session.CaptionExcludeLabel);
    }

    /// <summary>Sets (or replaces) the image's caption and renumbers the document's captions.</summary>
    public void SetCaption(Guid imageId, CaptionSettings settings)
    {
        if (settings is null || !_sessions.TryGetValue(imageId, out var session))
            return;

        session.CaptionLabel = settings.Label;
        session.CaptionText = settings.Text;
        session.CaptionNumbering = settings.Numbering;
        session.CaptionSeparator = settings.Separator;
        session.CaptionPosition = settings.Position;
        session.CaptionExcludeLabel = settings.ExcludeLabel;

        RenumberCaptions();
        RaiseImagesChanged();
    }

    /// <summary>Removes the image's caption and renumbers the remaining captions.</summary>
    public void RemoveCaption(Guid imageId)
    {
        if (!_sessions.TryGetValue(imageId, out var session) || !session.HasCaption)
            return;

        session.CaptionLabel = null;
        session.CaptionText = string.Empty;
        session.CaptionNumber = 0;
        RefreshCaptionVisuals();
        RenumberCaptions();
        RaiseImagesChanged();
    }

    /// <summary>
    /// Recomputes the per-label caption sequence numbers in document order (floating images by
    /// their document top, in-line images by their anchor position) and refreshes every caption.
    /// Safe to call often; cheap for note-sized documents.
    /// </summary>
    public void RenumberCaptions()
    {
        var captioned = _sessions.Values.Where(session => session.HasCaption).ToList();
        captioned.Sort(CompareCaptionOrder);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in captioned)
        {
            var label = session.CaptionLabel ?? string.Empty;
            counts[label] = counts.GetValueOrDefault(label) + 1;
            session.CaptionNumber = counts[label];
        }

        RefreshCaptionVisuals();
    }

    /// <summary>The rendered caption text, e.g. "Figure 2: caption text" (or "2: caption text"
    /// when the label is excluded).</summary>
    public string BuildCaptionText(NoteImageSession session)
    {
        if (!session.HasCaption)
            return string.Empty;

        var number = FormatCaptionNumber(session.CaptionNumber, session.CaptionNumbering);
        var prefix = session.CaptionExcludeLabel
            ? number
            : $"{session.CaptionLabel} {number}".Trim();
        var separator = session.CaptionText.Length > 0 ? session.CaptionSeparator : string.Empty;
        return $"{prefix}{separator}{session.CaptionText}".Trim();
    }

    private int CompareCaptionOrder(NoteImageSession a, NoteImageSession b)
        => GetCaptionOrderY(a).CompareTo(GetCaptionOrderY(b));

    private double GetCaptionOrderY(NoteImageSession session)
    {
        if (session.WrapMode == NoteImageWrapMode.Inline
            && _editor is not null
            && _inlineContainers.TryGetValue(session.Id, out var container))
        {
            var rect = container.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (!rect.IsEmpty)
                return rect.Top + GetViewportOffset().Y;
        }

        return session.DocumentTop;
    }

    /// <summary>Next sequence number the given label would get (used by the dialog preview).</summary>
    public int GetNextCaptionNumber(string label)
    {
        var count = _sessions.Values.Count(session =>
            session.HasCaption && string.Equals(session.CaptionLabel, label, StringComparison.Ordinal));
        return count + 1;
    }

    public static string FormatCaptionNumber(int number, CaptionNumbering numbering) => numbering switch
    {
        CaptionNumbering.Arabic => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
        CaptionNumbering.RomanUpper => ToRoman(number),
        CaptionNumbering.RomanLower => ToRoman(number).ToLowerInvariant(),
        CaptionNumbering.LettersUpper => ToLetters(number, upper: true),
        CaptionNumbering.LettersLower => ToLetters(number, upper: false),
        _ => string.Empty
    };

    private static string ToRoman(int number)
    {
        if (number <= 0 || number >= 4000)
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        int[] values = { 1000, 900, 500, 400, 100, 90, 50, 40, 10, 9, 5, 4, 1 };
        string[] numerals = { "M", "CM", "D", "CD", "C", "XC", "L", "XL", "X", "IX", "V", "IV", "I" };
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < values.Length; i++)
        {
            while (number >= values[i])
            {
                number -= values[i];
                sb.Append(numerals[i]);
            }
        }

        return sb.ToString();
    }

    private static string ToLetters(int number, bool upper)
    {
        if (number <= 0)
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var sb = new System.Text.StringBuilder();
        while (number > 0)
        {
            number--;
            sb.Insert(0, (char)('A' + (number % 26)));
            number /= 26;
        }

        var text = sb.ToString();
        return upper ? text : text.ToLowerInvariant();
    }

    private void RefreshCaptionVisuals()
    {
        foreach (var session in _sessions.Values)
        {
            if (session.WrapMode == NoteImageWrapMode.Inline)
            {
                if (_inlineContainers.TryGetValue(session.Id, out var container)
                    && container.Child is InlineNoteImageElement element)
                {
                    element.SetCaption(session.HasCaption ? BuildCaptionText(session) : null);
                }
            }
            else if (FindWidget(session.Id) is { } widget)
            {
                var (left, top) = NoteImageLayoutMath.CanvasFromDocument(
                    session.DocumentLeft,
                    session.DocumentTop,
                    GetViewportOffset().X,
                    GetViewportOffset().Y,
                    _editor?.Padding.Left ?? 0,
                    _editor?.Padding.Top ?? 0);
                SyncCaptionElement(session, widget, left, top);
            }
        }
    }

    public void MoveInlineAnchor(Guid imageId, TextPointer? target)
    {
        if (_editor is null || target is null || !_inlineContainers.TryGetValue(imageId, out var container))
            return;

        var host = GetInlineHost(container);
        var position = target.GetInsertionPosition(LogicalDirection.Forward);
        if (host is not null && position is not null)
        {
            host.Remove(container);
            var paragraph = FindParagraphFor(position);
            if (paragraph is not null)
                NoteWrapLayoutEngine.InsertInlineAt(paragraph, position, new Inline[] { container });
            else
                InsertAtDocumentEnd(container);
        }

        UpdateSessionPositionFromContainer(imageId, container);
        SyncInlineParagraphLineStrategies();
        if (!_isRestoring)
            RaiseImagesChanged();
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

        // Materialized: SyncWidget can add caption elements to the overlay mid-iteration.
        foreach (var widget in EnumerateWidgets().ToList())
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

        SyncInlineButtonPlacement();
    }

    /// <summary>
    /// Grows or shrinks a tagged trailing spacer so floating images below text remain
    /// scrollable. Safe to call often; does not raise <see cref="ImagesChanged"/>.
    /// With <paramref name="stabilizeViewport"/>, the pad also covers the current viewport
    /// bottom, so the extent cannot shrink under the view and the scroll position does not
    /// jump back up (used when a drag ends - the drag headroom disappears).
    /// With <paramref name="dragHeadroom"/>, the pad extends to twice the viewport below the
    /// current view, so the document always grows ahead of the scroll and the drag auto-scroll
    /// can keep travelling even in short (text-less) notes where the physical extent alone
    /// would be smaller than the viewport (ScrollableHeight = 0).
    /// </summary>
    public void EnsureImageExtentPad(bool stabilizeViewport = false, bool dragHeadroom = false)
    {
        if (_editor?.Document is null)
            return;

        _scrollViewer ??= FindScrollViewer(_editor);

        var maxImageBottom = 0.0;
        foreach (var session in _sessions.Values)
        {
            // Inline images are part of the text flow - the document extent already covers them.
            if (session.WrapMode == NoteImageWrapMode.Inline)
                continue;
            maxImageBottom = Math.Max(maxImageBottom, session.DocumentBottom);
        }

        // Prefer live widget bottoms during gestures (session may lag one frame on resize).
        foreach (var widget in EnumerateWidgets())
            maxImageBottom = Math.Max(maxImageBottom, widget.DocumentTop + widget.Height);

        // During a body drag the document grows well ahead of the current view, so the
        // auto-scroll can always travel (its cap is the extent, which never binds while the
        // headroom stays ahead of the scroll) and the layout lag is masked.
        var edgeReserve = 0.0;
        if (dragHeadroom && _scrollViewer is not null)
        {
            var viewportH = _scrollViewer.ViewportHeight;
            if (viewportH > 0)
                edgeReserve = _scrollViewer.VerticalOffset + viewportH * 2;
        }

        if (stabilizeViewport && _scrollViewer is not null && _scrollViewer.ViewportHeight > 0)
            maxImageBottom = Math.Max(maxImageBottom, _scrollViewer.VerticalOffset + _scrollViewer.ViewportHeight);

        var neededBottom = NoteImageLayoutMath.ResolveNeededDocumentBottom(
            maxImageBottom,
            edgeReserve,
            ExtentPadSlackDip);

        ApplyExtentPad(neededBottom, dragHeadroom);
    }

    /// <summary>
    /// Removes the floating-image extent spacer so note XAML serialization stays clean.
    /// </summary>
    public void StripImageExtentPad()
    {
        if (_editor?.Document is null)
        {
            _extentPadHeight = 0;
            return;
        }

        ApplyExtentPad(0);
    }

    private void ApplyExtentPad(double neededBottom, bool dragHeadroom = false)
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

        // The content bottom is the last non-pad block's bottom - stable and layout-accurate,
        // unlike the ScrollViewer extent (which lags behind the pad during drags and made a
        // cached value drift, leaving later pads short and images unreachable).
        var contentBottom = MeasureContentBottom();
        if (contentBottom <= 0)
            contentBottom = Math.Max(0, extent - _extentPadHeight);

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

    /// <summary>The document's real content bottom - the last non-pad block's end rect bottom
    /// (the extent pad is always the trailing block). Falls back to the layout-independent
    /// editor height when the flow is not laid out yet.</summary>
    private double MeasureContentBottom()
    {
        if (_editor?.Document is null)
            return 0;

        Block? last = null;
        foreach (var block in _editor.Document.Blocks)
        {
            if (!Equals(block.Tag, ExtentPadTag))
                last = block;
        }

        if (last is null)
            return 0;

        var rect = last.ContentEnd.GetCharacterRect(LogicalDirection.Backward);
        if (rect.IsEmpty)
            return 0;

        // GetCharacterRect reports in the viewport (scroll-inclusive) space - convert to
        // document space, matching UpdateSessionPositionFromContainer / ResolveDefaultInsertOrigin.
        var offset = GetViewportOffset();
        return rect.Bottom + offset.Y;
    }

    private NoteImageWidget CreateWidget(NoteImageSession session, ImageSource source)
    {
        var widget = new NoteImageWidget
        {
            ImageId = session.Id,
            Source = source,
            WrapMode = session.WrapMode
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
        widget.WrapModeChanged += Widget_WrapModeChanged;
        widget.ArrangeRequested += Widget_ArrangeRequested;
        widget.CopyRequested += Widget_CopyRequested;
        widget.CutRequested += Widget_CutRequested;
        widget.ChangePictureRequested += Widget_ChangePictureRequested;
        widget.CaptionRequested += Widget_CaptionRequested;
        widget.PreviewMouseWheel += Widget_PreviewMouseWheel;
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
        widget.WrapModeChanged -= Widget_WrapModeChanged;
        widget.ArrangeRequested -= Widget_ArrangeRequested;
        widget.CopyRequested -= Widget_CopyRequested;
        widget.CutRequested -= Widget_CutRequested;
        widget.ChangePictureRequested -= Widget_ChangePictureRequested;
        widget.CaptionRequested -= Widget_CaptionRequested;
        widget.PreviewMouseWheel -= Widget_PreviewMouseWheel;

        // A widget torn down mid-gesture would otherwise leave _activeGestures > 0 forever,
        // silently disabling the wheel forwarding guard (and the drag-scroll machinery).
        if (ReferenceEquals(_gestureWidget, widget))
        {
            _gestureWidget = null;
            _hasDragGrab = false;
            _activeGestures = Math.Max(0, _activeGestures - 1);
            StopDragScrollTimer();
        }
    }

    private void Widget_PreviewMouseWheel(object? sender, MouseWheelEventArgs e)
    {
        // Floating photos live on the overlay - a sibling of the editor - so the mouse wheel
        // over a picture never reaches the editor's ScrollViewer and the note cannot be
        // scrolled there. Forward the wheel through the editor's ScrollViewer:
        // 1. As a TUNNELING PreviewMouseWheel, so the app's SmoothScrollBehavior (attached to
        //    every ScrollViewer via the implicit style) intercepts it exactly like a real text
        //    wheel - animated smooth scroll, no stall. A bubbling raise bypasses that behavior,
        //    and while a text-wheel impulse is still decaying the animation overwrites each
        //    jump, which reads as "stuck mid photo".
        // 2. If nothing handled the preview (hosts without the behavior), fall back to the
        //    bubbling wheel so the framework's own wheel handling still scrolls.
        // Ctrl+wheel is left alone (zoom belongs to the editor's own wheel route).
        if (_activeGestures > 0
            || e.Delta == 0
            || (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            return;
        }

        _scrollViewer ??= _editor is null ? null : FindScrollViewer(_editor);
        if (_scrollViewer is null)
            return;

        var forwarded = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, e.Delta)
        {
            RoutedEvent = UIElement.PreviewMouseWheelEvent,
            Source = _scrollViewer
        };
        _scrollViewer.RaiseEvent(forwarded);

        if (!forwarded.Handled)
        {
            var bubble = new MouseWheelEventArgs(Mouse.PrimaryDevice, Environment.TickCount, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = _scrollViewer
            };
            _scrollViewer.RaiseEvent(bubble);
        }

        e.Handled = true;
    }

    private void Widget_CopyRequested(object? sender, EventArgs e)
    {
        if (sender is NoteImageWidget widget)
            CopyRequested?.Invoke(this, widget.ImageId);
    }

    private void Widget_CutRequested(object? sender, EventArgs e)
    {
        if (sender is NoteImageWidget widget)
            CutRequested?.Invoke(this, widget.ImageId);
    }

    private void Widget_ChangePictureRequested(object? sender, EventArgs e)
    {
        if (sender is NoteImageWidget widget)
            ChangePictureRequested?.Invoke(this, widget.ImageId);
    }

    private void Widget_CaptionRequested(object? sender, EventArgs e)
    {
        if (sender is NoteImageWidget widget)
            CaptionRequested?.Invoke(this, widget.ImageId);
    }

    private void Widget_SelectionRequested(object? sender, EventArgs e)
    {
        if (sender is NoteImageWidget widget)
        {
            Select(widget);

            // Same caret-anchor rationale as inline selection: keep the caret near the image
            // so a later focus-scroll lands at the photo instead of a stale top caret.
            if (_editor is not null
                && _sessions.TryGetValue(widget.ImageId, out var session)
                && FindNearestTextPosition(session.DocumentLeft, session.DocumentTop) is { } position)
            {
                ParkCaretPreservingView(position);
            }
        }
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

    private void Widget_WrapModeChanged(object? sender, EventArgs e)
    {
        // Conversions between floating and in-line are handled centrally by SetWrapMode.
        if (sender is NoteImageWidget widget)
            SetWrapMode(widget.ImageId, widget.WrapMode);
    }

    private void Widget_ArrangeRequested(object? sender, NoteImageArrangeDirection direction)
    {
        if (sender is not NoteImageWidget widget)
            return;

        if (direction == NoteImageArrangeDirection.BringForward)
            BringForward(widget.ImageId);
        else
            SendBackward(widget.ImageId);
    }

    // ---------------------------------------------------------------- in-line images

    private void ConvertSessionToInline(NoteImageSession session)
    {
        if (_overlay is null || _editor is null)
            return;

        // Drop the floating representation first (if any).
        if (FindWidget(session.Id) is { } widget)
        {
            UnhookWidget(widget);
            _overlay.Children.Remove(widget);
            if (ReferenceEquals(_selected, widget))
                _selected = null;
        }

        // Safety: drop any stale inline representation so the image can never exist twice.
        if (_inlineContainers.Remove(session.Id, out var stale))
            GetInlineHost(stale)?.Remove(stale);

        // Anchor at the nearest text position to the image's document top-left.
        var position = FindNearestTextPosition(session.DocumentLeft, session.DocumentTop);
        var element = CreateInlineElement(session);
        var container = new InlineUIContainer(element);
        _inlineContainers[session.Id] = container;

        if (position is not null && FindParagraphFor(position) is { } paragraph)
            NoteWrapLayoutEngine.InsertInlineAt(paragraph, position, new Inline[] { container });
        else
            InsertAtDocumentEnd(container);

        session.InlineAnchorOffset = null; // recomputed at save time
        SelectInline(session.Id);
        SyncInlineParagraphLineStrategies();
        EnsureImageExtentPad();
    }

    private void ConvertSessionToFloating(NoteImageSession session)
    {
        if (_overlay is null)
            return;

        if (_inlineContainers.Remove(session.Id, out var container))
        {
            GetInlineHost(container)?.Remove(container);
            if (_selectedInlineId == session.Id)
            {
                _selectedInlineId = null;
                if (container.Child is InlineNoteImageElement element)
                    element.IsSelected = false;
            }

            // Remember where the flow put the image so the floating image lands near it.
            UpdateSessionPositionFromContainer(session.Id, container);
            session.InlineAnchorOffset = null;
            SyncInlineParagraphLineStrategies();
        }

        if (!_sessions.ContainsKey(session.Id))
            return;

        // Safety: drop any stale widget so the image can never exist twice.
        if (FindWidget(session.Id) is { } staleWidget)
        {
            UnhookWidget(staleWidget);
            _overlay.Children.Remove(staleWidget);
            if (ReferenceEquals(_selected, staleWidget))
                _selected = null;
        }

        if (DecodeDisplaySource(session) is not { } source)
            return;

        var widget = CreateWidget(session, source);
        _overlay.Children.Add(widget);
        ClampSessionAndProject(session, widget);
        SyncWidget(widget);
        EnsureImageExtentPad();
    }

    private InlineNoteImageElement CreateInlineElement(NoteImageSession session)
    {
        var element = new InlineNoteImageElement
        {
            ImageId = session.Id,
            WrapMode = session.WrapMode,
            LockAspectRatio = session.LockAspectRatio
        };
        element.SetDisplaySize(session.Width, session.Height);
        if (DecodeDisplaySource(session) is { } source)
            element.Source = source;
        HookInlineElement(element);
        return element;
    }

    private void HookInlineElement(InlineNoteImageElement element)
    {
        element.SelectionRequested += Inline_SelectionRequested;
        element.BoundsChanging += Inline_BoundsChanging;
        element.BoundsChanged += Inline_BoundsChanged;
        element.GestureStarted += Inline_GestureStarted;
        element.GestureCompleted += Inline_GestureCompleted;
        element.DeleteRequested += Inline_DeleteRequested;
        element.LockAspectRatioChanged += Inline_LockAspectRatioChanged;
        element.WrapModeChanged += Inline_WrapModeChanged;
        element.ReanchorPreview += Inline_ReanchorPreview;
        element.ReanchorRequested += Inline_ReanchorRequested;
        element.CopyRequested += Inline_CopyRequested;
        element.CutRequested += Inline_CutRequested;
        element.ChangePictureRequested += Inline_ChangePictureRequested;
        element.CaptionRequested += Inline_CaptionRequested;
    }

    private void UnhookInlineElement(InlineNoteImageElement element)
    {
        element.SelectionRequested -= Inline_SelectionRequested;
        element.BoundsChanging -= Inline_BoundsChanging;
        element.BoundsChanged -= Inline_BoundsChanged;
        element.GestureStarted -= Inline_GestureStarted;
        element.GestureCompleted -= Inline_GestureCompleted;
        element.DeleteRequested -= Inline_DeleteRequested;
        element.LockAspectRatioChanged -= Inline_LockAspectRatioChanged;
        element.WrapModeChanged -= Inline_WrapModeChanged;
        element.ReanchorPreview -= Inline_ReanchorPreview;
        element.ReanchorRequested -= Inline_ReanchorRequested;
        element.CopyRequested -= Inline_CopyRequested;
        element.CutRequested -= Inline_CutRequested;
        element.ChangePictureRequested -= Inline_ChangePictureRequested;
        element.CaptionRequested -= Inline_CaptionRequested;
    }

    private void Inline_CopyRequested(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element)
            CopyRequested?.Invoke(this, element.ImageId);
    }

    private void Inline_CutRequested(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element)
            CutRequested?.Invoke(this, element.ImageId);
    }

    private void Inline_ChangePictureRequested(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element)
            ChangePictureRequested?.Invoke(this, element.ImageId);
    }

    private void Inline_CaptionRequested(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element)
            CaptionRequested?.Invoke(this, element.ImageId);
    }

    private void Inline_SelectionRequested(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element && _sessions.ContainsKey(element.ImageId))
            SelectInline(element.ImageId);
    }

    private void Inline_BoundsChanging(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element && _sessions.TryGetValue(element.ImageId, out var session))
        {
            session.Width = element.Width;
            session.Height = element.Height - element.CaptionHeight;
        }
    }

    private void Inline_BoundsChanged(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element && _sessions.TryGetValue(element.ImageId, out var session))
        {
            session.Width = element.Width;
            session.Height = element.Height - element.CaptionHeight;
            MaybeRedeocodeInlineIfNeeded(element, session);
        }

        if (!_isRestoring)
            RaiseImagesChanged();
    }

    private void Inline_GestureStarted(object? sender, EventArgs e) => _activeGestures++;

    private void Inline_GestureCompleted(object? sender, EventArgs e)
    {
        _activeGestures = Math.Max(0, _activeGestures - 1);
        if (_activeGestures == 0)
        {
            EnsureImageExtentPad();
            RenumberCaptions();
        }
    }

    private void Inline_DeleteRequested(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element)
            RemoveInlineImage(element.ImageId);
    }

    private void Inline_LockAspectRatioChanged(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element && _sessions.TryGetValue(element.ImageId, out var session))
        {
            session.LockAspectRatio = element.LockAspectRatio;
            if (!_isRestoring)
                RaiseImagesChanged();
        }
    }

    private void Inline_WrapModeChanged(object? sender, EventArgs e)
    {
        if (sender is InlineNoteImageElement element)
            SetWrapMode(element.ImageId, element.WrapMode);
    }

    private void Inline_ReanchorPreview(object? sender, InlineNoteImageElement.InlineReanchorEventArgs e)
    {
        // Word-style caret preview at the drop position while dragging.
        if (sender is not InlineNoteImageElement element || _editor is null)
            return;

        var position = ResolveEditorPosition(element, e.ElementPoint);
        if (position is not null)
            _editor.CaretPosition = position.GetInsertionPosition(LogicalDirection.Forward) ?? position;
    }

    private void Inline_ReanchorRequested(object? sender, InlineNoteImageElement.InlineReanchorEventArgs e)
    {
        if (sender is not InlineNoteImageElement element)
            return;

        MoveInlineAnchor(element.ImageId, ResolveEditorPosition(element, e.ElementPoint));
    }

    /// <summary>Converts a point relative to an inline element into a text position in the
    /// editor (the element itself holds no editor reference - see the control's note).</summary>
    private TextPointer? ResolveEditorPosition(InlineNoteImageElement element, Point elementPoint)
    {
        if (_editor is null)
            return null;

        Point editorPoint;
        try
        {
            editorPoint = element.TransformToAncestor(_editor).Transform(elementPoint);
        }
        catch (InvalidOperationException)
        {
            editorPoint = elementPoint;
        }

        return _editor.GetPositionFromPoint(editorPoint, snapToText: true);
    }

    /// <summary>The nearest text position to a document-space point (editor coordinate
    /// conversion mirrors <see cref="SyncWidget"/>).</summary>
    private TextPointer? FindNearestTextPosition(double documentLeft, double documentTop)
    {
        if (_editor is null)
            return null;

        var offset = GetViewportOffset();
        var padding = _editor.Padding;
        var point = new Point(
            documentLeft - offset.X + padding.Left,
            documentTop - offset.Y + padding.Top);
        return _editor.GetPositionFromPoint(point, snapToText: true) ?? _editor.Document.ContentEnd;
    }

    private static Paragraph? FindParagraphFor(TextPointer position)
    {
        for (DependencyObject? current = position.Parent; current is not null; current = current is Visual
                 ? VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is Paragraph paragraph)
                return paragraph;
        }

        return null;
    }

    private void InsertAtDocumentEnd(InlineUIContainer container)
    {
        if (_editor?.Document is null)
            return;

        var lastParagraph = _editor.Document.Blocks
            .Select(block => block as Paragraph)
            .LastOrDefault(p => p is not null);
        if (lastParagraph is not null)
        {
            lastParagraph.Inlines.Add(container);
            return;
        }

        var paragraph = new Paragraph(container);
        _editor.Document.Blocks.Add(paragraph);
    }

    private static InlineCollection? GetInlineHost(Inline inline)
        => inline.Parent switch
        {
            Span span => span.Inlines,
            Paragraph paragraph => paragraph.Inlines,
            _ => null
        };

    private void UpdateSessionPositionFromContainer(Guid imageId, InlineUIContainer container)
    {
        if (!_sessions.TryGetValue(imageId, out var session))
            return;

        var rect = container.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty)
            return;

        var offset = GetViewportOffset();
        session.DocumentLeft = rect.Left + offset.X;
        session.DocumentTop = rect.Top + offset.Y;
    }

    private ImageSource? DecodeDisplaySource(NoteImageSession session)
    {
        var dpi = GetDpiScale();
        if (NoteImageLoader.TryCreateDisplaySource(session.Bytes, session.Width, dpi, out var display, out var decodeWidth, out _)
            && display is not null)
        {
            session.DecodePixelWidth = decodeWidth;
            return display;
        }

        return null;
    }

    private void MaybeRedeocodeInlineIfNeeded(InlineNoteImageElement element, NoteImageSession session)
    {
        if (element.Width <= 0)
            return;

        var dpi = GetDpiScale();
        var needed = NoteImageLoader.ResolveDecodePixelWidth(element.Width, dpi, session.NaturalPixelWidth);
        if (needed <= session.DecodePixelWidth)
            return;

        if (NoteImageLoader.TryCreateDisplaySource(session.Bytes, element.Width, dpi, out var display, out var decodeWidth, out _)
            && display is not null)
        {
            element.Source = display;
            session.DecodePixelWidth = decodeWidth;
        }
    }

    private NoteImageWidget? FindWidget(Guid imageId)
    {
        foreach (var widget in EnumerateWidgets())
        {
            if (widget.ImageId == imageId)
                return widget;
        }

        return null;
    }

    // ------------------------------------------------------- in-line serialization

    /// <summary>
    /// Replaces in-line image elements with <see cref="InlineMarkerChar"/> text markers so the
    /// flow serializes without embedding UI elements, and records each image's anchor offset
    /// on its session. Call <see cref="RestoreInlineImagesAfterSerialization"/> afterwards.
    /// Markers carry their session id in <see cref="FrameworkContentElement.Tag"/> so the
    /// pairing never depends on dictionary iteration order.
    /// </summary>
    public void PrepareInlineImagesForSerialization()
    {
        if (_editor?.Document is null)
            return;

        // Stray markers (a failed restore, a pasted RTF with markers, an undo resurrection)
        // are swept before fresh markers are created - the marker char is only ever transient
        // and must never re-enter the serialized content or stay visible as "obj".
        SweepStrayInlineMarkers();

        _pendingInlineRestoreOrder = new List<Guid>();
        foreach (var (id, container) in _inlineContainers.ToList())
        {
            var host = GetInlineHost(container);
            if (host is null || !_sessions.ContainsKey(id))
                continue;

            var marker = new Run(new string(InlineMarkerChar, 1)) { Tag = id };
            // A transparent background differs from the neighbors' null background, which stops
            // the text engine from coalescing the marker run into an adjacent run (which would
            // drop the Tag and orphan the restore). Renders identically.
            marker.Background = Brushes.Transparent;
            host.InsertBefore(container, marker);
            host.Remove(container);
            _pendingInlineRestoreOrder.Add(id);
        }

        RecordInlineAnchorOffsets();
    }

    /// <summary>Replaces the markers created by <see cref="PrepareInlineImagesForSerialization"/>
    /// back with their inline elements.</summary>
    public void RestoreInlineImagesAfterSerialization()
    {
        if (_editor?.Document is null || _pendingInlineRestoreOrder is null)
            return;

        var markers = EnumerateAllRuns(_editor.Document)
            .Where(run => run.Text.Contains(InlineMarkerChar))
            .ToList();
        foreach (var run in markers)
        {
            if (run.Tag is not Guid id || !_inlineContainers.TryGetValue(id, out var container))
            {
                // Unpaired marker (its run was coalesced during serialization and lost the
                // tag, or the container is gone): the char is not user text - remove it
                // instead of leaving an "obj" in the visible text.
                RemoveMarkerChars(run);
                continue;
            }

            var host = run.Parent switch
            {
                Span span => span.Inlines,
                Paragraph paragraph => paragraph.Inlines,
                _ => null
            };
            if (host is not null)
            {
                host.InsertBefore(run, container);
                // Remove exactly the marker chars (a coalesced run may also carry real text,
                // which must survive - never remove the whole run).
                RemoveMarkerChars(run);
            }
            else
            {
                RemoveMarkerChars(run);
            }
        }

        _pendingInlineRestoreOrder = null;
        SyncInlineParagraphLineStrategies();
    }

    /// <summary>Deletes every <see cref="InlineMarkerChar"/> left in the document - the char is
    /// only ever a transient serialization marker, never legitimate user text.</summary>
    private void SweepStrayInlineMarkers()
    {
        if (_editor?.Document is null)
            return;

        foreach (var run in EnumerateAllRuns(_editor.Document)
            .Where(run => run.Text.Contains(InlineMarkerChar))
            .ToList())
        {
            RemoveMarkerChars(run);
        }
    }

    /// <summary>Removes every marker char inside a run, keeping any real text it was coalesced
    /// with.</summary>
    private static void RemoveMarkerChars(Run run)
    {
        if (run.Text.IndexOf(InlineMarkerChar) < 0)
            return;

        run.Text = run.Text.Replace(InlineMarkerChar.ToString(), string.Empty);
    }

    /// <summary>Removes the single marker char at a text position, tolerating the end-of-document
    /// case where <see cref="TextPointer.GetPositionAtOffset"/> returns null.</summary>
    private static void RemoveMarkerCharAt(TextPointer position)
    {
        if (position.Parent is Run run)
        {
            var offset = run.ContentStart.GetOffsetToPosition(position);
            if (offset >= 0 && offset < run.Text.Length && run.Text[offset] == InlineMarkerChar)
            {
                run.Text = run.Text.Remove(offset, 1);
                return;
            }
        }

        // Fallback - the position sits at an element boundary (e.g. the marker is the document's
        // last run and the container was inserted at its start). Locate the marker char in the
        // next text run; never touch the container itself.
        for (var cursor = position;
             cursor is not null && cursor.CompareTo(cursor.DocumentEnd) < 0;
             cursor = cursor.GetNextContextPosition(LogicalDirection.Forward))
        {
            if (cursor.GetPointerContext(LogicalDirection.Forward) != TextPointerContext.Text)
                continue;

            var text = cursor.GetTextInRun(LogicalDirection.Forward);
            var markerIndex = text.IndexOf(InlineMarkerChar);
            if (markerIndex < 0)
                continue;

            if (cursor.GetPositionAtOffset(markerIndex, LogicalDirection.Forward) is { Parent: Run markerRun } markerPosition)
            {
                var offset = markerRun.ContentStart.GetOffsetToPosition(markerPosition);
                markerRun.Text = markerRun.Text.Remove(offset, 1);
            }

            return;
        }
    }

    private void RecordInlineAnchorOffsets()
    {
        var accumulated = 0;
        foreach (var run in EnumerateAllRuns(_editor!.Document))
        {
            var text = run.Text;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == InlineMarkerChar)
                {
                    // The marker carries the session id, so offsets pair up correctly even
                    // when images were converted out of flow order.
                    if (run.Tag is Guid id && _sessions.TryGetValue(id, out var session))
                        session.InlineAnchorOffset = accumulated;
                }

                // Count every character (markers included) - the load-side walk uses the same
                // convention, so the offsets pair up regardless of run boundaries.
                accumulated++;
            }
        }
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Undo/redo can resurrect the in-line photo marker char (renders as "obj") from the
        // save-cycle operations; the char is never legitimate text, so sweep it on sight.
        // Skipped while the marker swap is in flight (serialization), while sessions are
        // being re-anchored (load), and while the host loads document content - all three
        // legitimately carry transient markers.
        if (_pendingInlineRestoreOrder is null && !_isRestoring && !SuppressMarkerSweep)
            SweepStrayInlineMarkers();

        SyncInlineParagraphLineStrategies();
        SyncInlineButtonPlacement();
    }

    /// <summary>
    /// The app's line-spacing feature fixes every paragraph to
    /// <see cref="LineStackingStrategy.BlockLineHeight"/>, which caps each line's box at the
    /// paragraph's <see cref="Paragraph.LineHeight"/> - a tall in-line image would overflow the
    /// line and render ON TOP of the neighbouring text (Word grows the line to fit the object).
    /// Paragraphs hosting an in-line image therefore use <see cref="LineStackingStrategy.MaxHeight"/>
    /// (the LineHeight still acts as the minimum for the other lines); the original strategy is
    /// restored once the last image leaves the paragraph. Idempotent - safe to call often.
    /// </summary>
    public void SyncInlineParagraphLineStrategies()
    {
        if (_editor?.Document is null)
            return;

        foreach (var paragraph in EnumerateParagraphs(_editor.Document))
        {
            if (ContainsInlineImage(paragraph))
            {
                if (paragraph.LineStackingStrategy == LineStackingStrategy.BlockLineHeight)
                {
                    if (!_inlineStrategyOverrides.ContainsKey(paragraph))
                        _inlineStrategyOverrides[paragraph] = paragraph.LineStackingStrategy;
                    paragraph.LineStackingStrategy = LineStackingStrategy.MaxHeight;
                }
            }
            else if (_inlineStrategyOverrides.Remove(paragraph, out var original))
            {
                paragraph.LineStackingStrategy = original;
            }
        }
    }

    private static bool ContainsInlineImage(Paragraph paragraph)
    {
        foreach (var inline in EnumerateInlines(paragraph.Inlines))
        {
            if (inline is InlineUIContainer { Child: InlineNoteImageElement })
                return true;
        }

        return false;
    }

    /// <summary>Flips the inline images' action chips below the image when they would clip
    /// against the editor's top edge (same rule as the floating widgets).</summary>
    private void SyncInlineButtonPlacement()
    {
        if (_editor is null)
            return;

        foreach (var (_, container) in _inlineContainers)
        {
            if (container.Child is not InlineNoteImageElement element)
                continue;

            var rect = container.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty)
                continue;

            element.ButtonsFlipped = rect.Top + _editor.Padding.Top < ButtonsFlipThresholdDip;
        }
    }

    private static IEnumerable<Inline> EnumerateInlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            yield return inline;
            if (inline is Span span)
            {
                foreach (var nested in EnumerateInlines(span.Inlines))
                    yield return nested;
            }
        }
    }

    private static IEnumerable<Paragraph> EnumerateParagraphs(FlowDocument document)
    {
        foreach (Block block in document.Blocks)
        {
            foreach (var paragraph in EnumerateParagraphsInBlock(block))
                yield return paragraph;
        }
    }

    private static IEnumerable<Paragraph> EnumerateParagraphsInBlock(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                yield return paragraph;
                break;
            case Section section:
                foreach (Block child in section.Blocks)
                {
                    foreach (var paragraph in EnumerateParagraphsInBlock(child))
                        yield return paragraph;
                }
                break;
            case List list:
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block child in item.Blocks)
                    {
                        foreach (var paragraph in EnumerateParagraphsInBlock(child))
                            yield return paragraph;
                    }
                }
                break;
            case Table table:
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block child in cell.Blocks)
                            {
                                foreach (var paragraph in EnumerateParagraphsInBlock(child))
                                    yield return paragraph;
                            }
                        }
                    }
                }
                break;
        }
    }

    /// <summary>
    /// Replaces the <see cref="InlineMarkerChar"/> markers left in a freshly loaded document
    /// with inline elements, paired by <see cref="NoteImageSession.InlineAnchorOffset"/>.
    /// Sessions without a matching marker fall back to floating at their stored position.
    /// </summary>
    private void RestoreInlineSessions(List<NoteImageSession> pending)
    {
        if (pending.Count == 0)
            return;

        if (_editor?.Document is null)
        {
            foreach (var session in pending)
                ConvertSessionToFloating(session);
            return;
        }

        // Markers are deleted (never left) by the paired restore, and any that remain are
        // swept below, so no undo interaction can leave an "obj" behind.
        var markers = new List<(int Offset, TextPointer Position)>();
        var accumulated = 0;
        foreach (var run in EnumerateAllRuns(_editor.Document))
        {
            var text = run.Text;
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == InlineMarkerChar)
                    markers.Add((accumulated, run.ContentStart.GetPositionAtOffset(i, LogicalDirection.Forward)));
                accumulated++;
            }
        }

        foreach (var session in pending.OrderBy(s => s.InlineAnchorOffset ?? int.MaxValue))
        {
            var match = markers.FirstOrDefault(m => m.Offset == session.InlineAnchorOffset);
            if (match.Position is null)
            {
                ConvertSessionToFloating(session);
                continue;
            }

            var element = CreateInlineElement(session);
            var container = new InlineUIContainer(element);
            _inlineContainers[session.Id] = container;

            // Insert the element at the marker's position first (the position is still inside
            // a run there, so the insertion always lands), then delete the marker char with
            // the robust removal - including the end-of-document case where
            // GetPositionAtOffset(1) returns null and the char would survive as "obj".
            var paragraph = FindParagraphFor(match.Position);
            if (paragraph is not null)
                NoteWrapLayoutEngine.InsertInlineAt(paragraph, match.Position, new Inline[] { container });
            else
                InsertAtDocumentEnd(container);
            RemoveMarkerCharAt(match.Position);
        }

        // Any markers that never paired (a stray from a paste, an old save, or an edge
        // case) are swept - the marker char is not user text.
        SweepStrayInlineMarkers();
        SyncInlineParagraphLineStrategies();
    }

    private static IEnumerable<Run> EnumerateAllRuns(FlowDocument document)
    {
        foreach (Block block in document.Blocks)
        {
            foreach (var run in EnumerateRunsInBlock(block))
                yield return run;
        }
    }

    private static IEnumerable<Run> EnumerateRunsInBlock(Block block)
    {
        switch (block)
        {
            case Paragraph paragraph:
                foreach (var run in EnumerateRunsInInlines(paragraph.Inlines))
                    yield return run;
                break;
            case Section section:
                foreach (Block child in section.Blocks)
                {
                    foreach (var run in EnumerateRunsInBlock(child))
                        yield return run;
                }
                break;
            case List list:
                foreach (ListItem item in list.ListItems)
                {
                    foreach (Block child in item.Blocks)
                    {
                        foreach (var run in EnumerateRunsInBlock(child))
                            yield return run;
                    }
                }
                break;
            case Table table:
                foreach (TableRowGroup group in table.RowGroups)
                {
                    foreach (TableRow row in group.Rows)
                    {
                        foreach (TableCell cell in row.Cells)
                        {
                            foreach (Block child in cell.Blocks)
                            {
                                foreach (var run in EnumerateRunsInBlock(child))
                                    yield return run;
                            }
                        }
                    }
                }
                break;
        }
    }

    private static IEnumerable<Run> EnumerateRunsInInlines(InlineCollection inlines)
    {
        foreach (Inline inline in inlines)
        {
            switch (inline)
            {
                case Run run:
                    yield return run;
                    break;
                case Span span:
                    // Hyperlink derives from Span, so its inlines are covered here too.
                    foreach (var run in EnumerateRunsInInlines(span.Inlines))
                        yield return run;
                    break;
            }
        }
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
            // Grow extent (with drag headroom) before clamp so downward drag past text
            // content is not pinned, and short text-less documents stay scrollable.
            EnsureImageExtentPad(dragHeadroom: true);
        }
        else
        {
            // Resize stages geometry on the widget; commit to session (owner) then project.
            CommitWidgetGeometryToSession(session, widget);
            EnsureImageExtentPad();
        }

        ClampSessionAndProject(session, widget);
        SyncWidget(widget);
        if (!_isRestoring)
            RaiseImagesGeometryChanging();
    }

    private void Widget_BoundsChanged(object? sender, EventArgs e)
    {
        if (sender is not NoteImageWidget widget)
            return;

        if (_sessions.TryGetValue(widget.ImageId, out var session))
        {
            CommitWidgetGeometryToSession(session, widget);
            EnsureImageExtentPad(stabilizeViewport: true);
            ClampSessionAndProject(session, widget);
        }
        else
        {
            EnsureImageExtentPad(stabilizeViewport: true);
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
            // The drag-only edge reserve is gone - keep the viewport covered so the extent
            // does not shrink under the current scroll position (no jump-back after release).
            EnsureImageExtentPad(stabilizeViewport: true);
            // The photo moved - caption sequence order may have changed.
            RenumberCaptions();
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
        _edgeScrollDirectionY = 0;
        _edgeScrollDirectionX = 0;
    }

    private void DragScrollTimer_Tick(object? sender, EventArgs e)
    {
        if (_activeGestures <= 0 || _gestureWidget is null || !_gestureWidget.IsBodyDragging)
        {
            StopDragScrollTimer();
            return;
        }

        TryEdgeAutoScroll(_gestureWidget);
        // The edge auto-scroll is the only intended view movement during a drag. The caret
        // parked at the photo's text position makes the RichTextBox scroll it into view on
        // every layout (MakeCaretVisible) - with wrap off nothing else masks it, so the text
        // visibly "runs away" from the dragged photo. Pin the view to the post-edge-scroll
        // offsets and re-assert them after the tick's layout churn.
        var pinX = _scrollViewer?.HorizontalOffset ?? 0;
        var pinY = _scrollViewer?.VerticalOffset ?? 0;

        // Follow first so pad/clamp use the pointer target (avoids vanishing when dragged deep).
        FollowPointer(_gestureWidget);
        EnsureImageExtentPad(dragHeadroom: true);
        // The pad lives in the flow; its layout is deferred mid-drag, which leaves the scroll
        // viewer's extent stale. With the drag headroom the extent is far ahead, so we only
        // re-layout when the view approaches it - keeps the drag smooth instead of re-laying
        // out the whole document on every 20ms tick.
        _scrollViewer ??= _editor is null ? null : FindScrollViewer(_editor);
        if (_scrollViewer is not null
            && _scrollViewer.ScrollableHeight - _scrollViewer.VerticalOffset < _scrollViewer.ViewportHeight)
        {
            _editor?.UpdateLayout();
        }

        if (_scrollViewer is not null
            && (Math.Abs(_scrollViewer.HorizontalOffset - pinX) >= 0.01
                || Math.Abs(_scrollViewer.VerticalOffset - pinY) >= 0.01))
        {
            _scrollViewer.ScrollToHorizontalOffset(pinX);
            _scrollViewer.ScrollToVerticalOffset(pinY);
        }

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
        var enter = DragScrollEdgeThresholdDip;
        var exit = enter + DragScrollExitMarginDip;

        var canScrollY = _scrollViewer.ScrollableHeight > 0.5
            || _scrollViewer.ExtentHeight > viewportH + 0.5;
        var canScrollX = _scrollViewer.ScrollableWidth > 0.5
            || _scrollViewer.ExtentWidth > viewportW + 0.5;

        // Hysteresis: the scroll starts when the pointer enters the edge band, but keeps
        // rolling until the pointer leaves a wider band. Hand micro-movements right at the
        // boundary otherwise make the view creep and restart on every tick (the trembling).
        _edgeScrollDirectionY = ResolveEdgeScrollDirection(_edgeScrollDirectionY, pointer.Y, enter, exit, viewportH);
        if (_edgeScrollDirectionY != 0 && canScrollY)
        {
            var step = _edgeScrollDirectionY < 0
                ? ResolveDragScrollStep(enter - pointer.Y)
                : ResolveDragScrollStep(pointer.Y - (viewportH - enter));
            _scrollViewer.ScrollToVerticalOffset(
                _edgeScrollDirectionY < 0
                    ? Math.Max(0, beforeY - step)
                    : Math.Min(_scrollViewer.ScrollableHeight, beforeY + step));
        }

        _edgeScrollDirectionX = ResolveEdgeScrollDirection(_edgeScrollDirectionX, pointer.X, enter, exit, viewportW);
        if (_edgeScrollDirectionX != 0 && canScrollX)
        {
            var step = _edgeScrollDirectionX < 0
                ? ResolveDragScrollStep(enter - pointer.X)
                : ResolveDragScrollStep(pointer.X - (viewportW - enter));
            _scrollViewer.ScrollToHorizontalOffset(
                _edgeScrollDirectionX < 0
                    ? Math.Max(0, beforeX - step)
                    : Math.Min(_scrollViewer.ScrollableWidth, beforeX + step));
        }

        var dx = _scrollViewer.HorizontalOffset - beforeX;
        var dy = _scrollViewer.VerticalOffset - beforeY;
        return Math.Abs(dx) >= 0.01 || Math.Abs(dy) >= 0.01;
    }

    /// <summary>
    /// Resolves the auto-scroll direction for one axis with hysteresis: -1 scrolls toward the
    /// start, +1 toward the end, 0 is idle. The pointer must enter the narrow <paramref name="enter"/>
    /// band to start (or flip), and leave the wider <paramref name="exit"/> band to stop.
    /// </summary>
    private static int ResolveEdgeScrollDirection(int active, double pointer, double enter, double exit, double span)
    {
        // Past the exit margin on both sides: idle.
        if (pointer > exit && pointer < span - exit)
            return 0;
        // Inside the enter band: start scrolling that way (or flip when it is the far edge).
        if (pointer < enter)
            return -1;
        if (pointer > span - enter)
            return 1;
        return active;
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
        // When the image is pressed against the editor's top edge, the action chips above it
        // would clip off-canvas - flip them below the image instead.
        widget.ButtonsFlipped = top < ButtonsFlipThresholdDip;

        if (_sessions.TryGetValue(widget.ImageId, out var captionSession))
            SyncCaptionElement(captionSession, widget, left, top);
    }

    /// <summary>Positions the photo's caption block below/above the widget so the caption
    /// follows the photo through drags, resizes and scrolling.</summary>
    private void SyncCaptionElement(NoteImageSession session, NoteImageWidget widget, double widgetLeft, double widgetTop)
    {
        if (!session.HasCaption)
        {
            if (_captionElements.Remove(session.Id, out var stale))
                _overlay?.Children.Remove(stale);
            return;
        }

        if (_overlay is null)
            return;

        var caption = EnsureCaptionElement(session, widget);
        caption.Text = BuildCaptionText(session);
        var maxWidth = Math.Max(40, session.Width);
        caption.MaxWidth = maxWidth;
        caption.Measure(new Size(maxWidth, double.PositiveInfinity));
        var captionHeight = caption.DesiredSize.Height;
        var top = session.CaptionPosition == CaptionPosition.Below
            ? widgetTop + widget.Height + 4
            : widgetTop - captionHeight - 4;
        Canvas.SetLeft(caption, widgetLeft);
        Canvas.SetTop(caption, top);
    }

    private TextBlock EnsureCaptionElement(NoteImageSession session, NoteImageWidget widget)
    {
        if (_captionElements.TryGetValue(session.Id, out var caption))
            return caption;

        caption = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = Application.Current?.TryFindResource("TextColorSecondary") as Brush ?? Brushes.Gray,
            IsHitTestVisible = true,
            Focusable = false
        };
        caption.MouseLeftButtonDown += (_, _) =>
        {
            Select(widget);
            if (FindNearestTextPosition(session.DocumentLeft, session.DocumentTop) is { } position)
                ParkCaretPreservingView(position);
        };
        caption.PreviewMouseWheel += Widget_PreviewMouseWheel;
        _overlay!.Children.Add(caption);
        _captionElements[session.Id] = caption;
        return caption;
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
            session.LockAspectRatio,
            resizeToFit: false);

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

        if (TryGetMeasuredBounds(out var width, out var height))
        {
            if (liveDragBottom is { } bottom)
                height = Math.Max(height, bottom);
            return (width, height);
        }

        return (400, 300);
    }

    /// <summary>
    /// True only when the editor is actually measured (a real viewport or actual size exists).
    /// At restore time the editor may not be laid out yet (tabs are created before the window
    /// is shown), in which case the fallback bounds must never be used to clamp - otherwise
    /// every stored position would be destroyed against an imaginary small box.
    /// </summary>
    private bool TryGetMeasuredBounds(out double boundsWidth, out double boundsHeight)
    {
        boundsWidth = 0;
        boundsHeight = 0;

        _scrollViewer ??= _editor is null ? null : FindScrollViewer(_editor);
        if (_scrollViewer is not null)
        {
            // The reachable area spans the whole scrollable content - with word wrap off the
            // content can be far wider than the viewport, and images placed there must not be
            // clamped back into the visible slice on restore or resize.
            var width = Math.Max(_scrollViewer.ViewportWidth, _scrollViewer.ExtentWidth);
            var height = Math.Max(_scrollViewer.ExtentHeight, _scrollViewer.ViewportHeight);
            if (width > 0 && height > 0)
            {
                boundsWidth = width;
                boundsHeight = height;
                return true;
            }
        }

        if (_editor is not null && _editor.ActualWidth > 0)
        {
            var width = Math.Max(0, _editor.ActualWidth - _editor.Padding.Left - _editor.Padding.Right);
            var height = Math.Max(0, _editor.ActualHeight - _editor.Padding.Top - _editor.Padding.Bottom);
            if (width > 0 && height > 0)
            {
                boundsWidth = width;
                boundsHeight = height;
                return true;
            }
        }

        return false;
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
            && FindAncestor<NoteImageWidget>(source) is null
            && FindAncestor<InlineNoteImageElement>(source) is null)
        {
            Select(null);
        }
    }

    private void RaiseImagesChanged() => ImagesChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseImagesGeometryChanging() => ImagesGeometryChanging?.Invoke(this, EventArgs.Empty);

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
