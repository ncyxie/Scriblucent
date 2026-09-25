using System.IO;
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
    /// <summary>Minimum drag distance (DIPs) before an in-line photo's re-anchor drag engages.</summary>
    private const double InlineBodyDragThresholdDip = 4.0;
    /// <summary>Full-document layouts during gestures are throttled - a synchronous layout on
    /// every resize event or drag tick is the jank behind "resizing is very hard" / the drag
    /// not being smooth. The extent lags at most one throttle window; the pad slack and the
    /// natural next-frame layout keep everything correct.</summary>
    private const double FullLayoutThrottleMs = 80;

    private Canvas? _overlay;
    private RichTextBox? _editor;

    /// <summary>Directory where photo attachments are stored as files (the notes media folder).
    /// Set by the host when a note loads; null falls back to inline Base64 attachments.</summary>
    public string? MediaDirectory { get; set; }

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
    private DateTime _lastFullLayoutAt = DateTime.MinValue;
    private int _activeGestures;
    private int _attachGeneration;
    private readonly Dictionary<Guid, NoteImageSession> _sessions = new();
    private readonly Dictionary<Guid, InlineUIContainer> _inlineContainers = new();
    private readonly Dictionary<Paragraph, LineStackingStrategy> _inlineStrategyOverrides = new();
    private readonly Dictionary<Guid, TextBlock> _captionElements = new();
    private List<Guid>? _pendingInlineRestoreOrder;
    /// <summary>Selection chrome of the selected in-line photo (resize handles + chips),
    /// hosted on the overlay canvas so it is never clipped by the text content box - the
    /// handles always meet the photo's corners (the floating widget's pattern).</summary>
    private InlineImageChrome? _inlineChrome;
    private string _chromeResizeTag = string.Empty;
    /// <summary>Photo size captured when an in-line resize gesture starts - the chrome reports
    /// the ACCUMULATED drag delta, so the size must not re-base on every event.</summary>
    private double _chromeStartWidth;
    private double _chromeStartHeight;
    /// <summary>Per-axis accumulation for the in-line resize. WPF's Thumb reports deltas
    /// relative to the thumb's OWN position: on the axes where the handle tracks the growth
    /// (right/bottom edges) the reports are incremental; on the axes the flow pins (left/top
    /// edges - the thumb never moves there) they are TOTALS since drag start. Accumulating
    /// only the incremental axes keeps the totals true for every handle.</summary>
    private double _chromeDragAccumX;
    private double _chromeDragAccumY;
    /// <summary>Body-drag state of the selected in-line photo. The photo lives in the text
    /// flow, where the surrounding text visuals cover the line - OS clicks over it land on the
    /// text (or the editor), so the element itself never receives real input. The re-anchor
    /// drag is therefore driven here, at the editor, like the floating widget's drag.</summary>
    private InlineNoteImageElement? _inlineDragElement;
    private bool _inlineDragging;
    private Point _inlineDragStart;

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
        _editor.PreviewMouseLeftButtonDown += Editor_PreviewMouseLeftButtonDown;
        _editor.PreviewMouseMove += Editor_PreviewMouseMove;
        _editor.PreviewMouseLeftButtonUp += Editor_PreviewMouseLeftButtonUp;
        _editor.PreviewMouseRightButtonUp += Editor_PreviewMouseRightButtonUp;
        _editor.LostMouseCapture += Editor_LostMouseCapture;
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
            _editor.PreviewMouseLeftButtonDown -= Editor_PreviewMouseLeftButtonDown;
            _editor.PreviewMouseMove -= Editor_PreviewMouseMove;
            _editor.PreviewMouseLeftButtonUp -= Editor_PreviewMouseLeftButtonUp;
            _editor.PreviewMouseRightButtonUp -= Editor_PreviewMouseRightButtonUp;
            _editor.LostMouseCapture -= Editor_LostMouseCapture;
            _editor.TextChanged -= Editor_TextChanged;
        }

        if (_surface is not null)
            _surface.SizeChanged -= Host_SizeChanged;

        Clear(raiseChanged: false);
        if (_inlineChrome is not null && _overlay is not null)
            _overlay.Children.Remove(_inlineChrome);
        _inlineChrome = null;
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
            DisplaySource = load.DisplaySource,
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
        _inlineChrome = null; // the overlay cleared it; re-created lazily on the next selection
        _selected = null;
        _selectedInlineId = null;
        _pendingInlineRestoreOrder = null;
        // Safety net: inline elements are not unhooked here, so a mid-gesture teardown could
        // otherwise leave _activeGestures > 0 forever (silently disabling the wheel guard).
        _activeGestures = 0;
        _gestureWidget = null;
        _hasDragGrab = false;
        _inlineDragElement = null;
        _inlineDragging = false;
        _inlineDropIndicator = null; // the overlay cleared it; re-created lazily on the next drag
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

            // The photo bytes are immutable between picture replacements - write the media file
            // once per session and reference it by name, keeping the Base64 out of the store
            // JSON (the store shrinks and saves get cheap). Falls back to the inline Base64
            // when no media directory is available (unusual hosts).
            var dataFilePath = EnsureMediaFile(session);
            string data;
            if (dataFilePath is not null)
            {
                data = string.Empty;
            }
            else
            {
                session.AttachmentData ??= Convert.ToBase64String(session.Bytes);
                data = session.AttachmentData;
            }

            list.Add(new NoteImageAttachment
            {
                Id = session.Id,
                Data = data,
                DataFilePath = dataFilePath ?? string.Empty,
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

    /// <summary>
    /// Like <see cref="BuildAttachments"/> but self-contained: the image bytes are embedded as
    /// Base64 instead of referenced by file. Used for exports/packages, which must travel alone.
    /// </summary>
    public IReadOnlyList<NoteImageAttachment> BuildSelfContainedAttachments()
    {
        var attachments = BuildAttachments();
        foreach (var attachment in attachments)
        {
            if (!string.IsNullOrEmpty(attachment.DataFilePath) && string.IsNullOrEmpty(attachment.Data))
            {
                var filePath = ResolveMediaFilePath(attachment.DataFilePath);
                if (filePath is not null && File.Exists(filePath))
                {
                    try
                    {
                        attachment.Data = Convert.ToBase64String(File.ReadAllBytes(filePath));
                        attachment.DataFilePath = string.Empty;
                    }
                    catch
                    {
                    }
                }
            }
        }

        return attachments;
    }

    /// <summary>Writes the session's bytes to the media directory once (cached on the session)
    /// and returns the file name, or null when no media directory is available.</summary>
    private string? EnsureMediaFile(NoteImageSession session)
    {
        if (string.IsNullOrEmpty(MediaDirectory))
            return null;

        if (session.MediaFilePath is { } cached && File.Exists(cached))
            return Path.GetFileName(cached);

        string directory;
        try
        {
            directory = Directory.CreateDirectory(MediaDirectory!).FullName;
        }
        catch
        {
            return null;
        }

        var fileName = session.Id.ToString("N") + ResolveImageExtension(session.Bytes);
        var filePath = Path.Combine(directory, fileName);
        try
        {
            File.WriteAllBytes(filePath, session.Bytes);
            session.MediaFilePath = filePath;
            return fileName;
        }
        catch
        {
            return null;
        }
    }

    private string? ResolveMediaFilePath(string fileName)
    {
        if (string.IsNullOrEmpty(MediaDirectory) || string.IsNullOrWhiteSpace(fileName))
            return null;

        try
        {
            return Path.Combine(MediaDirectory!, fileName);
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveImageExtension(byte[] bytes)
    {
        if (bytes.Length >= 4 && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47)
            return ".png";
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return ".jpg";
        if (bytes.Length >= 6 && bytes[0] == 0x47 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x38)
            return ".gif";
        if (bytes.Length >= 12 && bytes[0] == 0x52 && bytes[1] == 0x49 && bytes[2] == 0x46 && bytes[3] == 0x46
            && bytes[8] == 0x57 && bytes[9] == 0x45 && bytes[10] == 0x42 && bytes[11] == 0x50)
            return ".webp";
        if (bytes.Length >= 2 && bytes[0] == 0x42 && bytes[1] == 0x4D)
            return ".bmp";
        return ".img";
    }

    public void Restore(IEnumerable<NoteImageAttachment>? attachments)
    {
        RestoreCore(attachments is null ? null : PredecodeAttachments(attachments, GetDpiScale(), MediaDirectory));
    }

    /// <summary>
    /// Restores attachments with the decode phase on a background thread: base64 parsing and
    /// bitmap decoding are the dominant restore costs and can take hundreds of milliseconds
    /// for photo-heavy notes - the UI stays responsive and the note's text remains usable
    /// while the images materialize.
    /// </summary>
    public async Task RestoreAsync(IEnumerable<NoteImageAttachment>? attachments)
    {
        var dpi = GetDpiScale();
        var mediaDirectory = MediaDirectory;
        var items = attachments is null
            ? null
            : await DecodeAttachmentsOnStaThread(attachments, dpi, mediaDirectory).ConfigureAwait(false);

        // The materialization touches WPF objects - marshal it to the editor's UI thread
        // explicitly (the continuation context is not guaranteed in every hosting environment).
        if (_editor is null)
        {
            RestoreCore(items);
            return;
        }

        await _editor.Dispatcher.InvokeAsync(() => RestoreCore(items));
    }

    /// <summary>Runs the attachment decode on a dedicated STA thread - WPF Freezables (the
    /// frozen BitmapSources) are thread-agnostic once frozen, so the decodes can run fully
    /// off the UI thread without freezing the editor on photo-heavy notes.</summary>
    private static Task<List<(NoteImageAttachment Attachment, NoteImageLoader.LoadResult? Load)>?> DecodeAttachmentsOnStaThread(
        IEnumerable<NoteImageAttachment> attachments,
        double dpi,
        string? mediaDirectory)
    {
        var tcs = new TaskCompletionSource<List<(NoteImageAttachment, NoteImageLoader.LoadResult?)>?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                tcs.SetResult(PredecodeAttachments(attachments, dpi, mediaDirectory));
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }

    /// <summary>Parses and decodes every attachment off the UI thread. The clamped display
    /// sizes drive the decode budget (same math as <see cref="Restore"/>).</summary>
    private static List<(NoteImageAttachment Attachment, NoteImageLoader.LoadResult? Load)> PredecodeAttachments(
        IEnumerable<NoteImageAttachment> attachments,
        double dpi,
        string? mediaDirectory)
    {
        var items = new List<(NoteImageAttachment, NoteImageLoader.LoadResult?)>();
        foreach (var attachment in attachments)
        {
            byte[] bytes;
            if (!string.IsNullOrEmpty(attachment.DataFilePath))
            {
                // File-backed attachment: read the media file (missing/corrupt files skip the
                // photo rather than failing the whole restore).
                if (string.IsNullOrEmpty(mediaDirectory))
                    continue;

                try
                {
                    var filePath = Path.Combine(mediaDirectory, attachment.DataFilePath);
                    if (!File.Exists(filePath))
                        continue;
                    bytes = File.ReadAllBytes(filePath);
                }
                catch
                {
                    continue;
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(attachment.Data))
                    continue;

                try
                {
                    bytes = Convert.FromBase64String(attachment.Data);
                }
                catch
                {
                    continue;
                }
            }

            var clamped = attachment.LockAspectRatio
                ? NoteImageResizeMath.ClampDisplaySize(attachment.Width, attachment.Height)
                : NoteImageResizeMath.ClampFreeformSize(attachment.Width, attachment.Height);

            if (NoteImageLoader.TryLoadFromBytes(bytes, clamped.Width, dpi, out var load, out _) && load is not null)
                items.Add((attachment, load));
        }

        return items;
    }

    private void RestoreCore(List<(NoteImageAttachment Attachment, NoteImageLoader.LoadResult? Load)>? items)
    {
        Clear(raiseChanged: false);
        if (_overlay is null || items is null)
            return;

        _isRestoring = true;
        try
        {
            var pendingInline = new List<NoteImageSession>();
            foreach (var (attachment, load) in items)
            {
                if (load is null)
                    continue;

                var clamped = attachment.LockAspectRatio
                    ? NoteImageResizeMath.ClampDisplaySize(attachment.Width, attachment.Height)
                    : NoteImageResizeMath.ClampFreeformSize(attachment.Width, attachment.Height);
                var width = clamped.Width;
                var height = clamped.Height;

                var id = attachment.Id == Guid.Empty ? Guid.NewGuid() : attachment.Id;
                var session = new NoteImageSession
                {
                    Id = id,
                    Bytes = load.Bytes,
                    NaturalPixelWidth = load.NaturalPixelWidth,
                    NaturalPixelHeight = load.NaturalPixelHeight,
                    DecodePixelWidth = load.DecodePixelWidth,
                    DisplaySource = load.DisplaySource,
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
                    // Same clamp as everywhere else - including the flush-on-pin guard against
                    // the pad-lagging extent.
                    ClampSessionAndProject(restoredSession, widget);
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

            // The selection chrome (resize handles + chips) lives on the overlay - it is not
            // clipped by the text content box, so the handles always meet the photo's corners,
            // even on the editor's first row (the floating widget's pattern).
            SyncInlineChrome();

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
            HideInlineChrome();
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

    // ------------------------------------------------------------- in-line selection chrome

    private InlineImageChrome EnsureInlineChrome()
    {
        if (_inlineChrome is not null)
            return _inlineChrome;

        _inlineChrome = new InlineImageChrome();
        _inlineChrome.ResizeStarted += InlineChrome_ResizeStarted;
        _inlineChrome.ResizeDelta += InlineChrome_ResizeDelta;
        _inlineChrome.ResizeCompleted += InlineChrome_ResizeCompleted;
        _inlineChrome.LockToggled += InlineChrome_LockToggled;
        _inlineChrome.DeleteRequested += InlineChrome_DeleteRequested;
        _overlay?.Children.Add(_inlineChrome);
        return _inlineChrome;
    }

    /// <summary>Positions the chrome over the selected in-line photo and shows it; hides it
    /// when nothing is selected. The chrome is a view-layer object on the overlay, so its
    /// handles are never clipped by the text content box.</summary>
    private void SyncInlineChrome()
    {
        var chrome = EnsureInlineChrome();
        if (_overlay is null
            || _editor is null
            || _selectedInlineId is not { } id
            || !_inlineContainers.TryGetValue(id, out var container)
            || container.Child is not InlineNoteImageElement element)
        {
            chrome.Visibility = Visibility.Collapsed;
            return;
        }

        try
        {
            // The element's position in the editor equals its position in the overlay (both
            // siblings at the same origin in the editor surface).
            var origin = element.TransformToAncestor(_editor).Transform(new Point(0, 0));
            Canvas.SetLeft(chrome, origin.X);
            Canvas.SetTop(chrome, origin.Y);
            chrome.Width = element.Width;
            chrome.Height = element.Height - element.CaptionHeight;
            chrome.Visibility = Visibility.Visible;

            // The chips normally sit above the photo; when it is pressed against the editor's
            // top edge, flip them below (same rule as the floating widget).
            var rect = container.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var top = rect.IsEmpty ? 0 : rect.Top + _editor.Padding.Top;
            chrome.ButtonsFlipped = top < ButtonsFlipThresholdDip;
        }
        catch
        {
            chrome.Visibility = Visibility.Collapsed;
        }
    }

    private void HideInlineChrome()
    {
        if (_inlineChrome is not null)
            _inlineChrome.Visibility = Visibility.Collapsed;
    }

    private void InlineChrome_ResizeStarted(object? sender, string tag)
    {
        _chromeResizeTag = tag;
        _activeGestures++;
        _chromeDragAccumX = 0;
        _chromeDragAccumY = 0;
        // Capture the size at gesture START: the chrome reports the ACCUMULATED drag delta,
        // so applying it against the size mutated by every event would compound (a tiny drag
        // balloons the photo - the "shaky, way too fast" resize).
        if (_selectedInlineId is { } id && _sessions.TryGetValue(id, out var session))
        {
            _chromeStartWidth = session.Width;
            _chromeStartHeight = session.Height;
            if (_inlineContainers.TryGetValue(id, out var container) && container.Child is InlineNoteImageElement element)
                element.SetResizeScaling(resizing: true);
        }
    }

    private void InlineChrome_ResizeDelta(object? sender, (double X, double Y) delta)
    {
        if (_selectedInlineId is not { } id
            || !_sessions.TryGetValue(id, out var session)
            || !_inlineContainers.TryGetValue(id, out var container)
            || container.Child is not InlineNoteImageElement element)
        {
            return;
        }

        // WPF's Thumb reports deltas relative to its own position: on the axes the flow pins
        // (the flipped ones - left/top edges) the reports are TOTALS since drag start; on the
        // tracking axes (right/bottom) they are increments. Accumulate only the tracking axes
        // so the resulting totals are the true drag distance for every handle.
        var flipX = _chromeResizeTag is "TL" or "BL";
        var flipY = _chromeResizeTag is "TL" or "TR";
        if (!flipX)
            _chromeDragAccumX += delta.X;
        if (!flipY)
            _chromeDragAccumY += delta.Y;
        var totalX = flipX ? delta.X : _chromeDragAccumX;
        var totalY = flipY ? delta.Y : _chromeDragAccumY;

        // The photo's top-left edge is flow-pinned; the size tracks the pointer 1:1 from there.
        var (freeWidth, freeHeight) = NoteImageResizeMath.ResizeFromCorner(
            _chromeStartWidth,
            _chromeStartHeight,
            totalX,
            totalY,
            flipX,
            flipY,
            keepRatio: false);

        double width;
        double height;
        if (session.LockAspectRatio)
        {
            // The floating widget's proven lock: project the freeform size onto the aspect ray,
            // so a one-axis drag (the left handles with lock on) scales the photo instead of
            // freezing it (the LO smaller-factor rule).
            var projected = NoteImageResizeMath.ProjectCornerScale(
                _chromeStartWidth,
                _chromeStartHeight,
                freeWidth,
                freeHeight);
            width = Math.Clamp(projected.Width, NoteImageResizeMath.MinInlineSideDip, NoteImageResizeMath.MaxSideDip);
            height = Math.Clamp(projected.Height, NoteImageResizeMath.MinInlineSideDip, NoteImageResizeMath.MaxSideDip);
        }
        else
        {
            width = freeWidth;
            height = freeHeight;
        }

        session.Width = width;
        session.Height = height;
        element.SetImageBox(width, height);
        SyncInlineChrome();
    }

    private void InlineChrome_ResizeCompleted(object? sender, EventArgs e)
    {
        _chromeResizeTag = string.Empty;
        _chromeStartWidth = 0;
        _chromeStartHeight = 0;
        _chromeDragAccumX = 0;
        _chromeDragAccumY = 0;
        _activeGestures = Math.Max(0, _activeGestures - 1);
        if (_selectedInlineId is { } resizedId
            && _inlineContainers.TryGetValue(resizedId, out var resizedContainer)
            && resizedContainer.Child is InlineNoteImageElement resizedElement)
        {
            // The gesture is over - render the settled size at full quality again.
            resizedElement.SetResizeScaling(resizing: false);
        }

        if (_activeGestures == 0)
        {
            if (_selectedInlineId is { } id
                && _sessions.TryGetValue(id, out var session)
                && _inlineContainers.TryGetValue(id, out var container)
                && container.Child is InlineNoteImageElement element)
            {
                MaybeRedeocodeInlineIfNeeded(element, session);
            }

            EnsureImageExtentPad();
            RenumberCaptions();
        }

        if (!_isRestoring)
            RaiseImagesChanged();
    }

    private void InlineChrome_LockToggled(object? sender, EventArgs e)
    {
        if (_selectedInlineId is not { } id || !_sessions.TryGetValue(id, out var session))
            return;

        session.LockAspectRatio = !session.LockAspectRatio;
        if (_inlineChrome is not null)
            _inlineChrome.LockAspectRatio = session.LockAspectRatio;

        // The element's DP change raises ImagesChanged through Inline_LockAspectRatioChanged -
        // only fall back to raising here when no element was updated.
        if (_inlineContainers.TryGetValue(id, out var container) && container.Child is InlineNoteImageElement element)
            element.LockAspectRatio = session.LockAspectRatio;
        else if (!_isRestoring)
            RaiseImagesChanged();
    }

    private void InlineChrome_DeleteRequested(object? sender, EventArgs e)
    {
        if (_selectedInlineId is { } id)
            RemoveInlineImage(id);
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

        // The overlay also hosts view-layer objects (the in-line photo's selection chrome,
        // caption blocks), so the neighbor lookup must run over WIDGETS only - a raw
        // children-index swap would trade a widget against the chrome instead of its neighbor.
        var widgets = EnumerateWidgets().ToList();
        var index = widgets.IndexOf(widget);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= widgets.Count)
            return;

        _overlay.Children.Remove(widget);
        var insertAt = _overlay.Children.IndexOf(widgets[target]);
        _overlay.Children.Insert(insertAt + (delta > 0 ? 1 : 0), widget);
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
            HideInlineChrome();
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
        session.DisplaySource = load.DisplaySource;
        session.AttachmentData = null; // the bytes changed - the cached Base64 is stale

        // The bytes changed - the media file must be re-written. A format change would produce
        // a different file name and orphan the previous file - remove it now.
        if (session.MediaFilePath is { } oldPath && File.Exists(oldPath))
        {
            var newName = session.Id.ToString("N") + ResolveImageExtension(load.Bytes);
            if (!string.Equals(Path.GetFileName(oldPath), newName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    File.Delete(oldPath);
                }
                catch
                {
                }
            }
        }

        session.MediaFilePath = null;
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
        session.CaptionDisplayText = null;
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
        if (captioned.Count == 0)
            return;

        captioned.Sort(CompareCaptionOrder);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var session in captioned)
        {
            var label = session.CaptionLabel ?? string.Empty;
            counts[label] = counts.GetValueOrDefault(label) + 1;
            session.CaptionNumber = counts[label];
            session.CaptionDisplayText = null; // the number changed - the rendered text is stale
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
            // The layout itself is throttled - a full document layout on every resize event is
            // the jank behind sluggish resizing.
            if (!bodyDragging)
            {
                TryRunThrottledFullLayout();
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
        element.DeleteRequested += Inline_DeleteRequested;
        element.LockAspectRatioChanged += Inline_LockAspectRatioChanged;
        element.WrapModeChanged += Inline_WrapModeChanged;
        element.CopyRequested += Inline_CopyRequested;
        element.CutRequested += Inline_CutRequested;
        element.ChangePictureRequested += Inline_ChangePictureRequested;
        element.CaptionRequested += Inline_CaptionRequested;
    }

    private void UnhookInlineElement(InlineNoteImageElement element)
    {
        element.SelectionRequested -= Inline_SelectionRequested;
        element.DeleteRequested -= Inline_DeleteRequested;
        element.LockAspectRatioChanged -= Inline_LockAspectRatioChanged;
        element.WrapModeChanged -= Inline_WrapModeChanged;
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
        var needed = NoteImageLoader.ResolveDecodePixelWidth(session.Width, dpi, session.NaturalPixelWidth);

        // The session caches its frozen display source - re-decoding from bytes on every sync
        // or conversion is the dominant image cost. The cache is valid while the needed decode
        // width does not exceed what was decoded.
        if (session.DisplaySource is not null && needed <= session.DecodePixelWidth)
            return session.DisplaySource;

        if (NoteImageLoader.TryCreateDisplaySource(session.Bytes, session.Width, dpi, out var display, out var decodeWidth, out _)
            && display is not null)
        {
            session.DecodePixelWidth = decodeWidth;
            session.DisplaySource = display;
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
            session.DisplaySource = display;
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

    /// <summary>Keystrokes only need the runs the edit touched. A full-document walk on every
    /// character is what made long notes hitch; undo still reports the resurrected marker
    /// inside <see cref="TextChangedEventArgs.Changes"/>.</summary>
    private void SweepStrayInlineMarkers(TextChangedEventArgs e)
    {
        if (_editor?.Document is null || e.Changes.Count == 0)
            return;

        var runs = new HashSet<Run>();
        foreach (var change in e.Changes)
            CollectRunsOverlapping(change, runs);

        foreach (var run in runs)
        {
            if (run.Text.IndexOf(InlineMarkerChar) >= 0)
                RemoveMarkerChars(run);
        }
    }

    private void CollectRunsOverlapping(TextChange change, HashSet<Run> runs)
    {
        var document = _editor!.Document;
        var start = document.ContentStart.GetPositionAtOffset(change.Offset);
        if (start is null)
            return;

        CollectRun(runs, start);
        var end = change.AddedLength > 0
            ? document.ContentStart.GetPositionAtOffset(change.Offset + change.AddedLength)
            : start;
        if (end is not null)
            CollectRun(runs, end);

        if (end is null || start.CompareTo(end) >= 0)
            return;

        var navigator = start;
        var guard = 0;
        while (navigator.CompareTo(end) < 0 && guard++ < 10000)
        {
            CollectRun(runs, navigator);
            var next = navigator.GetNextContextPosition(LogicalDirection.Forward);
            if (next is null || next.CompareTo(navigator) <= 0)
                break;

            navigator = next;
        }
    }

    private static void CollectRun(HashSet<Run> runs, TextPointer pointer)
    {
        if (pointer.Parent is Run run)
            runs.Add(run);
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
            SweepStrayInlineMarkers(e);

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

        // The scan is per-keystroke (via Editor_TextChanged) - early out for the common
        // photo-free note instead of enumerating every paragraph. Pending overrides must
        // still be restored when the last photo leaves its paragraph.
        if (_inlineContainers.Count == 0 && _inlineStrategyOverrides.Count == 0)
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

    /// <summary>Keeps the selected in-line photo's selection chrome in sync after the flow
    /// re-lays out (the chrome itself lives on the overlay - see <see cref="SyncInlineChrome"/>).</summary>
    private void SyncInlineButtonPlacement()
    {
        SyncInlineChrome();
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
        if (_activeGestures <= 0)
        {
            StopDragScrollTimer();
            return;
        }

        if (_inlineDragElement is not null && _inlineDragging)
        {
            DragScrollInlineTick();
            return;
        }

        if (_gestureWidget is null || !_gestureWidget.IsBodyDragging)
        {
            StopDragScrollTimer();
            return;
        }

        if (_scrollViewer is not null)
            TryEdgeAutoScroll(Mouse.GetPosition(_scrollViewer));
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
            // Throttled together with the pad updates - the extent lags at most one window.
            TryRunThrottledFullLayout();
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

    /// <summary>Auto-scroll tick for the in-line photo's re-anchor drag: scrolls the view when
    /// the pointer nears a viewport edge, pins it against the parked caret's MakeCaretVisible,
    /// and re-resolves the drop position under the (stationary) mouse so the drop indicator
    /// keeps tracking while the view rolls.</summary>
    private void DragScrollInlineTick()
    {
        if (_scrollViewer is not null)
            TryEdgeAutoScroll(Mouse.GetPosition(_scrollViewer));

        // The caret parked after the photo makes the RichTextBox scroll it into view on every
        // layout; pin the view to the post-edge-scroll offsets (same as the widget drag).
        var pinX = _scrollViewer?.HorizontalOffset ?? 0;
        var pinY = _scrollViewer?.VerticalOffset ?? 0;

        if (_scrollViewer is not null
            && (Math.Abs(_scrollViewer.HorizontalOffset - pinX) >= 0.01
                || Math.Abs(_scrollViewer.VerticalOffset - pinY) >= 0.01))
        {
            _scrollViewer.ScrollToHorizontalOffset(pinX);
            _scrollViewer.ScrollToVerticalOffset(pinY);
        }

        if (_editor is not null)
            UpdateInlineDropIndicatorAt(Mouse.GetPosition(_editor));
    }

    /// <summary>
    /// Scrolls the editor when the pointer is near a viewport edge during an image gesture.
    /// Placement under the cursor is handled by <see cref="FollowPointer"/> after this returns.
    /// </summary>
    public bool TryEdgeAutoScroll(Point pointerInViewer)
    {
        _scrollViewer ??= _editor is null ? null : FindScrollViewer(_editor);
        if (_scrollViewer is null || _editor is null)
            return false;

        var pointer = pointerInViewer;
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
        session.DisplaySource = display;
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
        var text = session.CaptionDisplayText ??= BuildCaptionText(session);
        var maxWidth = Math.Max(40, session.Width);

        // Syncing runs on every drag/resize/scroll event; the caption only needs re-measuring
        // when its text or wrap width changed - otherwise the previous measure stays valid.
        if (!Equals(caption.Text, text) || Math.Abs(caption.MaxWidth - maxWidth) >= 0.01)
        {
            caption.Text = text;
            caption.MaxWidth = maxWidth;
            caption.Measure(new Size(maxWidth, double.PositiveInfinity));
        }

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

        // The ScrollViewer extent can lag the pad by up to the throttle window - if the clamp
        // would displace the photo during that window, the extent is probably stale (the pad
        // was just re-sized). Flush the layout once and re-clamp against the settled extent,
        // so deep photos are never pinned to a lagging content bottom.
        if ((Math.Abs(clamped.Left - session.DocumentLeft) > 0.5 || Math.Abs(clamped.Top - session.DocumentTop) > 0.5)
            && (DateTime.UtcNow - _lastFullLayoutAt).TotalMilliseconds < FullLayoutThrottleMs)
        {
            _lastFullLayoutAt = DateTime.UtcNow;
            _editor?.UpdateLayout();
            (boundsW, boundsH) = GetDocumentBounds();
            clamped = NoteImageResizeMath.ClampRectToBounds(
                session.DocumentLeft,
                session.DocumentTop,
                session.Width,
                session.Height,
                boundsW,
                boundsH,
                session.LockAspectRatio,
                resizeToFit: false);
        }

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

    /// <summary>Runs a synchronous full-document layout at most once per throttle window
    /// (gesture layouts are the jank behind "resizing is very hard" / the drag not being
    /// smooth). Returns true when the layout ran.</summary>
    private bool TryRunThrottledFullLayout()
    {
        var now = DateTime.UtcNow;
        if ((now - _lastFullLayoutAt).TotalMilliseconds < FullLayoutThrottleMs)
            return false;

        _lastFullLayoutAt = now;
        _editor?.UpdateLayout();
        return true;
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

        // Clicks over the photo resolve to the surrounding text visuals (the flow element is
        // never hit directly), so exclude them here - the inline drag/select handlers below
        // own that territory.
        if (e.OriginalSource is DependencyObject source
            && FindAncestor<NoteImageWidget>(source) is null
            && FindAncestor<InlineNoteImageElement>(source) is null
            && FindAncestor<InlineImageChrome>(source) is null
            && FindInlineElementAt(Mouse.GetPosition(_editor)) is null)
        {
            Select(null);
        }
    }

    /// <summary>Starts the in-line photo's re-anchor drag. The photo's element never receives
    /// real mouse input (the surrounding text visuals cover the line), so the gesture is
    /// driven at the editor: capture the editor, track the mouse, and re-anchor on release.</summary>
    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_activeGestures > 0 || _editor is null || _editor.IsReadOnly)
            return;

        if (FindInlineElementAt(e.GetPosition(_editor)) is not { } element)
            return;

        _inlineDragElement = element;
        _inlineDragging = false;
        _inlineDragStart = e.GetPosition(_editor);
        HideInlineDropIndicator();
        SelectInline(element.ImageId);
        e.Handled = true;
        _editor.CaptureMouse();
    }

    private void Editor_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_inlineDragElement is null || _editor is null)
            return;

        var point = e.GetPosition(_editor);
        if (!_inlineDragging)
        {
            var delta = point - _inlineDragStart;
            if (Math.Abs(delta.X) < InlineBodyDragThresholdDip && Math.Abs(delta.Y) < InlineBodyDragThresholdDip)
                return;

            _inlineDragging = true;
            _activeGestures++;
            StartDragScrollTimer();
        }

        // The overlay drop indicator is the drop feedback - the native caret is left alone
        // (it only draws while the editor has focus, and would mix with the indicator).
        UpdateInlineDropIndicatorAt(point);
        e.Handled = true;
    }

    private void Editor_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_inlineDragElement is null || _editor is null)
            return;

        var element = _inlineDragElement;
        var wasDragging = _inlineDragging;
        var position = ResolveEditorPositionAt(e.GetPosition(_editor));
        _inlineDragElement = null;
        _inlineDragging = false;
        StopDragScrollTimer();
        HideInlineDropIndicator();
        if (_editor.IsMouseCaptured)
            _editor.ReleaseMouseCapture();

        if (wasDragging)
        {
            _activeGestures = Math.Max(0, _activeGestures - 1);
            CompleteInlineDrag(element.ImageId, position);
            if (_activeGestures == 0)
            {
                EnsureImageExtentPad();
                RenumberCaptions();
            }
        }

        e.Handled = true;
    }

    /// <summary>Finishes an in-line photo's re-anchor drag: moves the anchor to the drop
    /// position and re-parks the caret right after the moved photo, so typing continues
    /// predictably after the photo (the same convention as photo selection).</summary>
    public void CompleteInlineDrag(Guid imageId, TextPointer? dropPosition)
    {
        MoveInlineAnchor(imageId, dropPosition);
        if (_editor is null || !_inlineContainers.TryGetValue(imageId, out var container))
            return;

        ParkCaretPreservingView(container.ContentEnd);
    }

    /// <summary>Converts an editor-space point to a text position in the document.</summary>
    private TextPointer? ResolveEditorPositionAt(Point editorPoint)
    {
        if (_editor is null)
            return null;

        return _editor.GetPositionFromPoint(editorPoint, snapToText: true);
    }

    /// <summary>Resolves the drop position under an editor-space point and updates the drop
    /// indicator (the drag's move handler and the auto-scroll tick both use this - the tick
    /// re-resolves while the mouse stays put and the view scrolls underneath it).</summary>
    private void UpdateInlineDropIndicatorAt(Point editorPoint)
    {
        var position = ResolveEditorPositionAt(editorPoint);
        if (position is not null)
            ShowInlineDropIndicator(position);
        else
            HideInlineDropIndicator();
    }

    /// <summary>Aborts an in-progress inline drag if the editor loses mouse capture
    /// mid-gesture (dialog, Alt-Tab, ...). Without this, <see cref="_activeGestures"/> would
    /// stay inflated and silently disable the deselect guard and future inline drags. The
    /// caret is re-parked too, so an aborted drag leaves it in a deterministic spot.</summary>
    private void Editor_LostMouseCapture(object? sender, MouseEventArgs e)
    {
        if (_inlineDragElement is null)
            return;

        var element = _inlineDragElement;
        _inlineDragElement = null;
        if (_inlineDragging)
            _activeGestures = Math.Max(0, _activeGestures - 1);
        _inlineDragging = false;
        StopDragScrollTimer();
        HideInlineDropIndicator();
        CompleteInlineDrag(element.ImageId, dropPosition: null);
    }

    // ------------------------------------------------------------- in-line drop indicator

    private Border? _inlineDropIndicator;

    /// <summary>Lazily creates the drag drop indicator - a caret-like accent bar on the
    /// overlay that shows where the dragged in-line photo will land. It lives on the overlay
    /// (like the selection chrome), so it is never clipped by the text box and never depends
    /// on the editor's focus or its native caret.</summary>
    private Border EnsureInlineDropIndicator()
    {
        if (_inlineDropIndicator is not null)
            return _inlineDropIndicator;

        _inlineDropIndicator = new Border
        {
            Name = "InlineDropIndicator",
            Width = 2,
            CornerRadius = new CornerRadius(1),
            Focusable = false,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        _inlineDropIndicator.SetResourceReference(Border.BackgroundProperty, "AccentPrimary");
        _overlay?.Children.Add(_inlineDropIndicator);
        return _inlineDropIndicator;
    }

    /// <summary>Shows the drop indicator at a text position (where the dragged photo will
    /// land). The indicator is a 2px accent bar at the position's character rect - the same
    /// spot the native caret would draw, minus the focus requirement. The character rect is
    /// already in the editor's coordinate space (padding included), and the overlay shares
    /// the editor's origin.</summary>
    public void ShowInlineDropIndicator(TextPointer position)
    {
        var indicator = EnsureInlineDropIndicator();
        if (_editor is null)
        {
            indicator.Visibility = Visibility.Collapsed;
            return;
        }

        // The forward character rect always exists in a live document (the trailing paragraph
        // mark guarantees one), so the indicator lands exactly where the photo will insert.
        var rect = position.GetCharacterRect(LogicalDirection.Forward);
        if (rect.IsEmpty || rect.Height <= 0)
        {
            indicator.Visibility = Visibility.Collapsed;
            return;
        }

        Canvas.SetLeft(indicator, rect.Left);
        Canvas.SetTop(indicator, rect.Top);
        indicator.Height = rect.Height;
        indicator.Visibility = Visibility.Visible;
    }

    public void HideInlineDropIndicator()
    {
        if (_inlineDropIndicator is not null)
            _inlineDropIndicator.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// The photo lives in the text flow, so a right-click over it lands on the editor's
    /// surface (the flow element is not directly hit-testable there - the editor's template
    /// intercepts) and the editor would open its own menu. Route the right-click to the
    /// photo instead: suppress the editor's processing and open the photo's menu directly.
    /// </summary>
    private void Editor_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_editor is null || FindInlineElementAt(Mouse.GetPosition(_editor)) is not { } element)
            return;

        e.Handled = true;
        SelectInline(element.ImageId);
        var menu = element.ContextMenu;
        if (menu is null)
            return;

        menu.PlacementTarget = element;
        menu.HorizontalOffset = 0;
        menu.VerticalOffset = 0;
        menu.IsOpen = true;
    }

    /// <summary>Finds the in-line photo whose on-screen box contains the given editor-space
    /// point (the box includes the caption row).</summary>
    public InlineNoteImageElement? FindInlineElementAt(Point editorPoint)
    {
        if (_editor is null)
            return null;

        foreach (var (_, container) in _inlineContainers)
        {
            if (container.Child is not InlineNoteImageElement element)
                continue;

            try
            {
                var bounds = element.TransformToAncestor(_editor)
                    .TransformBounds(new Rect(0, 0, element.Width, element.Height));
                if (bounds.Contains(editorPoint))
                    return element;
            }
            catch
            {
                // Element not connected to the visual tree yet (restore) - skip it.
            }
        }

        return null;
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
