using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Models;

namespace Scriblucent.Services;

/// <summary>
/// Pure wrap-geometry math, kept free of WPF layout so it can be unit-tested.
/// Modeled on word-processor "square wrap" semantics (LibreOffice "Parallel", ONLYOFFICE
/// "Square"): a wrap box = image bounds expanded by a gap on all sides. Text lines whose
/// vertical extent overlaps the box are shortened to flow around it; lines fully above or
/// below keep full width. Since a FlowDocument paragraph can only take one lane per line,
/// each affected paragraph is pushed to the side with the larger remaining width (the
/// "biggest/largest" rule), and paragraphs where no lane is wide enough are left unwrapped.
/// </summary>
public static class NoteWrapMath
{
    /// <summary>Default gap between a wrapped image and the text (DIPs).</summary>
    public const double DefaultWrapGap = 10;

    /// <summary>
    /// Minimum lane width before a lane is considered usable, mirroring ONLYOFFICE's
    /// 6.35mm (~18px) minimum and Scribus's ~1-glyph rule - a sliver between the image and
    /// the margin is not worth flowing text into.
    /// </summary>
    public const double MinimumLaneWidth = 24;

    public static (double Left, double Top, double Right, double Bottom) GetWrapRect(
        double imageLeft,
        double imageTop,
        double imageRight,
        double imageBottom,
        double gap)
        => (imageLeft - gap, imageTop - gap, imageRight + gap, imageBottom + gap);

    /// <summary>True when the paragraph's vertical extent [pTop, pBottom] overlaps the wrap box.</summary>
    public static bool IntersectsVertical(double pTop, double pBottom, double wrapTop, double wrapBottom)
        => pBottom > wrapTop && pTop < wrapBottom;

    /// <summary>
    /// Vertical push that moves a paragraph's top to the bottom of a "top and bottom" wrap band
    /// (Word/LibreOffice "None", ONLYOFFICE TOP_AND_BOTTOM): the band reserves the full text
    /// width, so the first paragraph overlapping it is pushed below <paramref name="wrapBottom"/>.
    /// </summary>
    public static double ComputeTopPush(double pTop, double wrapBottom)
        => Math.Max(0, wrapBottom - pTop);

    /// <summary>
    /// Height of the inline spacer that reserves the wrap band "between lines" of a straddling
    /// paragraph (Word-style split inside a paragraph): the spacer sits at <paramref name="spacerTop"/>
    /// and must reach the band bottom so the text after it continues below the image.
    /// </summary>
    public static double ComputeSpacerHeight(double spacerTop, double wrapBottom)
        => Math.Max(0, wrapBottom - spacerTop);

    /// <summary>
    /// Chooses the lane for one image and returns the paragraph insets to apply.
    /// Text runs on the side with more room; a left inset starts text after <paramref name="wrapRight"/>,
    /// a right inset ends text before <paramref name="wrapLeft"/>. Returns Applies=false when neither
    /// lane is wide enough.
    /// </summary>
    public static (double LeftInset, double RightInset, bool Applies) ComputeInsets(
        double wrapLeft,
        double wrapRight,
        double contentWidth,
        double minLaneWidth)
    {
        var leftLane = wrapLeft;
        var rightLane = contentWidth - wrapRight;

        if (leftLane < minLaneWidth && rightLane < minLaneWidth)
            return (0, 0, false);

        if (rightLane >= leftLane)
            return (wrapRight, 0, true);

        return (0, contentWidth - wrapLeft, true);
    }
}

/// <summary>
/// Applies square text wrap around floating note images by adjusting paragraph margins in
/// document space. Margins are a transient overlay: they are restored exactly by
/// <see cref="Clear"/> (used before every content serialization/export so wrap geometry is
/// never baked into saved files) and recomputed on demand. Recompute iterates because
/// changing a paragraph's margin reflows its lines, which shifts the document-space extents
/// used to decide zone membership - the same convergence loop ONLYOFFICE's layout runs.
/// </summary>
public sealed class NoteWrapLayoutEngine : IDisposable
{
    private const int MaxConvergenceIterations = 16;
    private const double RecomputeDebounceMs = 120;
    private const double LiveApplyIntervalMs = 50;

    private sealed class InlineSpacerState
    {
        public required Paragraph Paragraph { get; init; }
        public required List<InlineUIContainer> Containers { get; init; }
        public required InlineCollection Host { get; init; }
        public required double Top { get; init; }
        public required double LineHeight { get; init; }
        public double TotalHeight { get; set; }
    }

    private readonly RichTextBox _editor;
    private readonly FrameworkElement _surface;
    private readonly FloatingImageCoordinator _images;
    private readonly DispatcherTimer _recomputeDebounce;
    private readonly DispatcherTimer _liveApplyThrottle;
    private readonly Dictionary<Paragraph, Thickness> _originalMargins = new();
    private readonly Dictionary<Paragraph, InlineSpacerState> _inlineSpacers = new();
    private BlockUIContainer? _pushSpacer;
    private Paragraph? _pushSpacerTarget;
    private double _pushSpacerHeight;
    private ScrollViewer? _scrollViewer;
    private bool _isApplying;
    private bool _layoutRetryPending;
    private bool _disposed;

    public NoteWrapLayoutEngine(RichTextBox editor, FrameworkElement surface, FloatingImageCoordinator images)
    {
        _editor = editor;
        _surface = surface;
        _images = images;

        _surface.SizeChanged += OnSurfaceSizeChanged;

        _recomputeDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(RecomputeDebounceMs)
        };
        _recomputeDebounce.Tick += OnRecomputeDebounceTick;

        _liveApplyThrottle = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(LiveApplyIntervalMs)
        };
        _liveApplyThrottle.Tick += OnLiveApplyThrottleTick;
    }

    /// <summary>Queues a debounced recompute (text edits, image moves, resize, zoom...).</summary>
    public void ScheduleApply()
    {
        if (_disposed || _isApplying)
            return;

        _liveApplyThrottle.Stop();
        _recomputeDebounce.Stop();
        _recomputeDebounce.Start();
    }

    /// <summary>
    /// Applies the wrap layout live while an image is being dragged or resized: the first
    /// geometry change applies immediately, then the layout keeps refreshing at a fixed
    /// interval until the gesture ends (the final <see cref="ScheduleApply"/> stops it).
    /// A restart-on-event debounce would starve while the mouse keeps moving.
    /// </summary>
    public void ScheduleApplyLive()
    {
        if (_disposed || _isApplying)
            return;

        if (!_liveApplyThrottle.IsEnabled)
        {
            Apply();
            _liveApplyThrottle.Start();
            return;
        }

        _recomputeDebounce.Stop();
    }

    private void OnLiveApplyThrottleTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            _liveApplyThrottle.Stop();
            return;
        }

        Apply();
    }

    /// <summary>Restores every modified paragraph margin (before save/export).</summary>
    public void Clear()
    {
        foreach (var (paragraph, original) in _originalMargins)
        {
            if (paragraph.Parent is not null)
                paragraph.Margin = original;
        }

        _originalMargins.Clear();
        RemovePushSpacer();
        RemoveInlineSpacers();
    }

    /// <summary>Restores margins, then immediately recomputes them (after save/export).</summary>
    public void Reapply()
    {
        Clear();
        Apply();
    }

    /// <summary>Recomputes wrap insets and applies them as paragraph margins.</summary>
    public void Apply()
    {
        if (_disposed || _isApplying || _editor.Document is null || _editor.Document.Blocks.Count == 0)
            return;

        // The wrap pass removes and re-inserts transient spacer blocks: the extent temporarily
        // shrinks (clamping a deep scroll) and the flow mutations can trigger the caret-following,
        // yanking the view away from a photo that is far from the caret's text. Preserve the view
        // across the pass (this also covers the debounced re-apply that follows an auto-save).
        var viewState = _images.CaptureViewState();
        try
        {
            ApplyCore();
        }
        finally
        {
            _images.RestoreViewState(viewState);
        }
    }

    private void ApplyCore()
    {
        var sessions = _images.GetWrapSessions();
        if (sessions.Count == 0)
        {
            Clear();
            return;
        }

        var contentWidth = ResolveContentWidth();
        if (contentWidth <= NoteWrapMath.MinimumLaneWidth)
        {
            ScheduleLayoutRetry();
            return;
        }

        var paragraphs = EnumerateParagraphs(_editor.Document.Blocks).ToList();
        if (paragraphs.Count == 0)
            return;

        // Apply can be scheduled before the editor's first layout pass (view size 0, character
        // rects empty) - in that case retry once the layout is ready instead of silently no-oping.
        if (!paragraphs.Any(HasMeasurableExtent))
        {
            ScheduleLayoutRetry();
            return;
        }

        _isApplying = true;
        try
        {
            ApplyWithConvergence(sessions, paragraphs, contentWidth);
        }
        finally
        {
            _isApplying = false;
        }
    }

    private void ApplyWithConvergence(
        IReadOnlyList<NoteImageSession> sessions,
        IReadOnlyList<Paragraph> paragraphs,
        double contentWidth)
    {
        // Inline spacers are NOT wiped here - they are updated in place during Phase B so small
        // image adjustments (which keep the same split line) only change the reserved height
        // instead of reflowing the paragraph's text (visible flashing).
        ResetTransientMargins();
        _editor.UpdateLayout();

        var topPushes = new Dictionary<Paragraph, double>();

        // Phase A: converge Square lanes only - no top pushes yet, so straddling paragraphs
        // keep their pre-push geometry and the between-lines split positions are measured
        // from a settled layout.
        Converge(sessions, paragraphs, contentWidth, topPushes, allowTopPushes: false);

        // Phase B: for straddling paragraphs, reserve the band "between lines" with a transient
        // inline spacer (Word-style split inside the paragraph). Table cells are excluded -
        // Word does not split those.
        var neededSpacers = EstablishInlineSpacers(sessions, paragraphs, contentWidth);

        // Drop spacers whose paragraph no longer straddles any band (image moved away).
        RemoveObsoleteInlineSpacers(neededSpacers);

        // Phase C: re-converge with top pushes (whole-paragraph pushes for paragraphs that start
        // inside a band; the ratchet) - the spacers shift the flow below their bands, Square
        // membership re-adapts.
        Converge(sessions, paragraphs, contentWidth, topPushes, allowTopPushes: true);
    }

    /// <summary>Clears transient margins and the block spacer, keeping inline spacers intact so
    /// the between-lines splits survive a re-apply and get updated in place.</summary>
    private void ResetTransientMargins()
    {
        foreach (var (paragraph, original) in _originalMargins)
        {
            if (paragraph.Parent is not null)
                paragraph.Margin = original;
        }

        _originalMargins.Clear();
        RemovePushSpacer();
    }

    private void Converge(
        IReadOnlyList<NoteImageSession> sessions,
        IReadOnlyList<Paragraph> paragraphs,
        double contentWidth,
        Dictionary<Paragraph, double> topPushes,
        bool allowTopPushes)
    {
        for (var iteration = 0; iteration < MaxConvergenceIterations; iteration++)
        {
            var next = ComputeParagraphInsets(sessions, paragraphs, contentWidth, topPushes);
            var marginsChanged = ApplyParagraphInsets(next);
            _editor.UpdateLayout();

            // WPF defers FlowDocument text layout: character rects stay stale after a paragraph
            // margin change until the queued layout work runs, which would make every re-measure
            // see the pre-margin position and grow pushes forever. Flush the queue so the next
            // measurement reflects the margins just applied.
            _editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            _editor.UpdateLayout();

            var grew = allowTopPushes && GrowTopPushes(sessions, paragraphs, topPushes);
            if (!marginsChanged && !grew)
                return;
        }
    }

    /// <summary>
    /// For each "top and bottom" image: the first paragraph (document order) whose extent
    /// intersects the wrap band is pushed so its top lands at the band bottom. Later paragraphs
    /// follow in flow, so they need no margin of their own. Returns whether any push grew.
    /// </summary>
    private bool GrowTopPushes(
        IReadOnlyList<NoteImageSession> sessions,
        IReadOnlyList<Paragraph> paragraphs,
        Dictionary<Paragraph, double> topPushes)
    {
        var grew = false;

        foreach (var session in sessions)
        {
            if (session.WrapMode != NoteImageWrapMode.TopAndBottom)
                continue;

            var (_, wrapTop, _, wrapBottom) = NoteWrapMath.GetWrapRect(
                session.DocumentLeft,
                session.DocumentTop,
                session.DocumentRight,
                session.DocumentBottom,
                NoteWrapMath.DefaultWrapGap);

            foreach (var paragraph in paragraphs)
            {
                if (GetParagraphDocumentExtent(paragraph) is not { } extent)
                    continue;

                if (!NoteWrapMath.IntersectsVertical(extent.Top, extent.Bottom, wrapTop, wrapBottom))
                    continue;

                if (_inlineSpacers.ContainsKey(paragraph))
                    break; // band already reserved between this paragraph's lines

                topPushes.TryGetValue(paragraph, out var existing);
                var growth = NoteWrapMath.ComputeTopPush(extent.Top, wrapBottom);
                if (growth > 0.5)
                {
                    topPushes[paragraph] = existing + growth;
                    grew = true;
                }
                else
                {
                    // Target already clears the band - keep the granted push so it stays granted.
                    topPushes[paragraph] = existing;
                }

                break;
            }
        }

        return grew;
    }

    private Dictionary<Paragraph, (double Left, double Top, double Right)> ComputeParagraphInsets(
        IReadOnlyList<NoteImageSession> sessions,
        IReadOnlyList<Paragraph> paragraphs,
        double contentWidth,
        IReadOnlyDictionary<Paragraph, double> topPushes)
    {
        var insets = new Dictionary<Paragraph, (double Left, double Top, double Right)>();
        foreach (var paragraph in paragraphs)
        {
            if (GetParagraphDocumentExtent(paragraph) is not { } extent)
                continue;

            var pTop = extent.Top;
            var pBottom = extent.Bottom;
            double left = 0;
            double top = 0;
            double right = 0;

            foreach (var session in sessions)
            {
                if (session.WrapMode != NoteImageWrapMode.Square)
                    continue;

                var (wrapLeft, wrapTop, wrapRight, wrapBottom) = NoteWrapMath.GetWrapRect(
                    session.DocumentLeft,
                    session.DocumentTop,
                    session.DocumentRight,
                    session.DocumentBottom,
                    NoteWrapMath.DefaultWrapGap);

                if (!NoteWrapMath.IntersectsVertical(pTop, pBottom, wrapTop, wrapBottom))
                    continue;

                var (laneLeft, laneRight, applies) = NoteWrapMath.ComputeInsets(
                    wrapLeft,
                    wrapRight,
                    contentWidth,
                    NoteWrapMath.MinimumLaneWidth);

                if (!applies)
                    continue;

                left = Math.Max(left, laneLeft);
                right = Math.Max(right, laneRight);
            }

            if (topPushes.TryGetValue(paragraph, out var push))
                top = Math.Max(top, push);

            if (left > 0 || top > 0 || right > 0)
                insets[paragraph] = (left, top, right);
        }

        return insets;
    }

    private bool ApplyParagraphInsets(Dictionary<Paragraph, (double Left, double Top, double Right)> insets)
    {
        var changed = false;

        foreach (var (paragraph, (left, top, right)) in insets)
        {
            var original = GetOriginalMargin(paragraph);
            // Block.Margin defaults to a NaN thickness ("unset"); NaN + inset stays NaN and would
            // render as no margin at all, so insets must be computed against the normalized value.
            var baseMargin = NormalizeMargin(original);

            double marginTop = top;
            if (top > 0
                && (ReferenceEquals(paragraph, _pushSpacerTarget)
                    || IsFirstBlockOfDocument(_editor.Document, paragraph)))
            {
                // WPF ignores the top margin of the document's first block (nothing below moves
                // either), so the push is reserved with a transient spacer block instead. Once
                // the spacer exists the target is no longer the first block, so the tracked
                // target keeps using the spacer too.
                marginTop = 0;
                changed |= ApplyPushSpacer(paragraph, top);
            }
            else if (ReferenceEquals(paragraph, _pushSpacerTarget) && top <= 0)
            {
                // Push was removed - drop the spacer and let the margin restore itself below.
                RemovePushSpacer();
                changed = true;
            }

            var margin = new Thickness(
                baseMargin.Left + left,
                baseMargin.Top + marginTop,
                baseMargin.Right + right,
                baseMargin.Bottom);
            if (!MarginsCloseEnough(paragraph.Margin, margin))
                changed = true;

            paragraph.Margin = margin;
            _originalMargins[paragraph] = original;
        }

        foreach (var (paragraph, original) in _originalMargins.ToList())
        {
            if (insets.ContainsKey(paragraph))
                continue;

            paragraph.Margin = original;
            _originalMargins.Remove(paragraph);
            changed = true;
        }

        return changed;
    }

    private Thickness GetOriginalMargin(Paragraph paragraph)
        => _originalMargins.TryGetValue(paragraph, out var original) ? original : paragraph.Margin;

    /// <summary>
    /// Creates or resizes the transient spacer used when the push target is the document's first
    /// block (whose top margin the RichTextBox ignores). Returns true when the spacer changed.
    /// </summary>
    private bool ApplyPushSpacer(Paragraph target, double height)
    {
        if (ReferenceEquals(_pushSpacerTarget, target)
            && _pushSpacer is not null
            && Math.Abs(_pushSpacerHeight - height) < 0.5)
        {
            return false;
        }

        RemovePushSpacer();

        _pushSpacerTarget = target;
        _pushSpacerHeight = height;
        _pushSpacer = new BlockUIContainer(new Border { Height = height });
        _editor.Document.Blocks.InsertBefore(target, _pushSpacer);
        return true;
    }

    private void RemovePushSpacer()
    {
        if (_pushSpacer is null)
            return;

        var document = _editor.Document;
        if (document is not null && document.Blocks.Contains(_pushSpacer))
            document.Blocks.Remove(_pushSpacer);

        _pushSpacer = null;
        _pushSpacerTarget = null;
        _pushSpacerHeight = 0;
    }

    private void RemoveInlineSpacers()
    {
        foreach (var state in _inlineSpacers.Values)
            RemoveInlineSpacer(state);

        _inlineSpacers.Clear();
    }

    private void RemoveInlineSpacer(InlineSpacerState state)
    {
        foreach (var container in state.Containers)
        {
            if (state.Host.Contains(container))
                state.Host.Remove(container);
        }
    }

    /// <summary>
    /// For each "top and bottom" image whose band starts inside a paragraph (not a table cell),
    /// reserve the band between the paragraph's own lines with a transient inline spacer.
    /// Bands are grouped per paragraph: the split follows the topmost band's top and the stack
    /// is sized to the deepest band's bottom, so multiple bands share one spacer.
    /// Returns the paragraphs that still need a spacer.
    /// </summary>
    private HashSet<Paragraph> EstablishInlineSpacers(
        IReadOnlyList<NoteImageSession> sessions,
        IReadOnlyList<Paragraph> paragraphs,
        double contentWidth)
    {
        var bands = new List<(double Top, double Bottom)>();
        foreach (var session in sessions)
        {
            if (session.WrapMode != NoteImageWrapMode.TopAndBottom)
                continue;

            var (_, wrapTop, _, wrapBottom) = NoteWrapMath.GetWrapRect(
                session.DocumentLeft,
                session.DocumentTop,
                session.DocumentRight,
                session.DocumentBottom,
                NoteWrapMath.DefaultWrapGap);
            bands.Add((wrapTop, wrapBottom));
        }

        var needed = new HashSet<Paragraph>();
        foreach (var paragraph in paragraphs)
        {
            if (GetParagraphDocumentExtent(paragraph) is not { } extent)
                continue;

            double? bandTop = null;
            double? bandBottom = null;
            foreach (var (wrapTop, wrapBottom) in bands)
            {
                if (!NoteWrapMath.IntersectsVertical(extent.Top, extent.Bottom, wrapTop, wrapBottom))
                    continue;

                bandTop = bandTop is null ? wrapTop : Math.Min(bandTop.Value, wrapTop);
                bandBottom = bandBottom is null ? wrapBottom : Math.Max(bandBottom.Value, wrapBottom);
            }

            if (bandTop is not null && extent.Top < bandTop.Value && !IsInsideTableCell(paragraph))
            {
                EnsureInlineSpacer(paragraph, bandTop.Value, bandBottom!.Value, contentWidth);
                needed.Add(paragraph);
            }
        }

        return needed;
    }

    private void RemoveObsoleteInlineSpacers(HashSet<Paragraph> needed)
    {
        foreach (var paragraph in _inlineSpacers.Keys.ToList())
        {
            if (!needed.Contains(paragraph))
            {
                RemoveInlineSpacer(_inlineSpacers[paragraph]);
                _inlineSpacers.Remove(paragraph);
            }
        }
    }

    private void EnsureInlineSpacer(Paragraph paragraph, double wrapTop, double wrapBottom, double contentWidth)
    {
        if (_inlineSpacers.TryGetValue(paragraph, out var existing))
        {
            double? splitTop = null;
            if (FindSplitPoint(paragraph, wrapTop) is { } splitPoint)
            {
                var rect = splitPoint.GetCharacterRect(LogicalDirection.Forward);
                if (!rect.IsEmpty)
                    splitTop = rect.Top + GetScrollOffset().Y;
            }

            if (splitTop is not null && Math.Abs(splitTop.Value - existing.Top) < 0.5)
            {
                // The band still starts on the same line as the existing spacer - only the
                // reserved height changes. Updating in place keeps the paragraph's text from
                // reflowing (flashing) during small image adjustments.
                ResizeInlineSpacer(existing, NoteWrapMath.ComputeSpacerHeight(existing.Top, wrapBottom), wrapBottom, contentWidth);
                return;
            }

            // The split moved (the band crossed a line boundary, or the band vanished) - rebuild
            // from the new position. The split must follow the band's top: freezing it (as the
            // previous "extend-only" logic did) swallowed the head forever once the band top
            // crossed below it, pushing the whole paragraph further down with every move.
            RemoveInlineSpacer(existing);
            _inlineSpacers.Remove(paragraph);
        }

        var newSplitPoint = FindSplitPoint(paragraph, wrapTop);
        if (newSplitPoint is null)
            return;

        var lineRect = newSplitPoint.GetCharacterRect(LogicalDirection.Forward);
        if (lineRect.IsEmpty)
            return;

        var spacerTop = lineRect.Top + GetScrollOffset().Y;
        var height = NoteWrapMath.ComputeSpacerHeight(spacerTop, wrapBottom);
        // The containers' own lines follow the paragraph's fixed LineHeight when set - the split
        // line's rect can be taller (inline fonts), which would make the stack come up short.
        var lineHeight = double.IsNaN(paragraph.LineHeight) ? lineRect.Height : paragraph.LineHeight;
        if (height <= 0.5)
            return;

        var containers = BuildSpacerStack(height, lineHeight, contentWidth);
        var host = InsertInlineStack(paragraph, newSplitPoint, containers);

        var state = new InlineSpacerState
        {
            Paragraph = paragraph,
            Containers = containers,
            Host = host,
            Top = spacerTop,
            LineHeight = lineHeight,
            TotalHeight = height
        };

        VerifyAndExtend(state, wrapBottom, contentWidth);

        _inlineSpacers[paragraph] = state;
    }

    /// <summary>
    /// Resizes an existing spacer stack to <paramref name="height"/>, adding or removing
    /// containers as needed. The paragraph's split is untouched - only the reserved height
    /// changes, so the text does not reflow.
    /// </summary>
    private void ResizeInlineSpacer(InlineSpacerState state, double height, double wrapBottom, double contentWidth)
    {
        if (height <= 0.5)
        {
            RemoveInlineSpacer(state);
            _inlineSpacers.Remove(state.Paragraph);
            return;
        }

        var count = state.LineHeight > 0 ? (int)Math.Ceiling(height / state.LineHeight) : 1;
        if (count < 1)
            count = 1;

        while (state.Containers.Count > count)
        {
            var last = state.Containers[^1];
            if (state.Host.Contains(last))
                state.Host.Remove(last);
            state.Containers.RemoveAt(state.Containers.Count - 1);
        }

        var perHeight = height / count;
        foreach (var container in state.Containers)
            ((Border)container.Child).Height = perHeight;

        while (state.Containers.Count < count)
        {
            var extra = BuildSpacerStack(height - perHeight * state.Containers.Count, state.LineHeight, contentWidth);
            Inline last = state.Containers[^1];
            foreach (var container in extra)
            {
                state.Host.InsertAfter(last, container);
                last = container;
            }

            state.Containers.AddRange(extra);
        }

        state.TotalHeight = height;
        VerifyAndExtend(state, wrapBottom, contentWidth);
    }

    /// <summary>
    /// Self-correcting tail check: ensures the paragraph's text clears the band bottom
    /// regardless of how the lines actually stack (fixed line height vs inline-font-inflated
    /// split rect), adding containers when the estimate comes up short.
    /// </summary>
    private void VerifyAndExtend(InlineSpacerState state, double wrapBottom, double contentWidth)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            _editor.UpdateLayout();
            _editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
            _editor.UpdateLayout();

            var tailTop = GetTailTop(state.Containers[^1]);
            if (tailTop is null || tailTop >= wrapBottom - 0.5)
                break;

            var shortfall = wrapBottom - tailTop.Value;
            if (shortfall <= 0.5)
                break;

            var extra = BuildSpacerStack(shortfall, state.LineHeight, contentWidth);
            Inline last = state.Containers[^1];
            foreach (var container in extra)
            {
                state.Host.InsertAfter(last, container);
                last = container;
            }

            state.Containers.AddRange(extra);
            state.TotalHeight += shortfall;
        }
    }

    /// <summary>
    /// A stack of full-width inline containers whose combined height reaches the wrap band.
    /// A single tall container would be capped by paragraphs with a fixed line height
    /// (LineStackingStrategy.BlockLineHeight - the app's line-spacing feature), letting the
    /// following text start only one line below; stacking containers no taller than the line
    /// height sidesteps that cap in both line-stacking modes.
    /// </summary>
    private static List<InlineUIContainer> BuildSpacerStack(double height, double lineHeight, double contentWidth)
    {
        var count = lineHeight > 0 ? (int)Math.Ceiling(height / lineHeight) : 1;
        if (count < 1)
            count = 1;

        var perHeight = height / count;
        var containers = new List<InlineUIContainer>(count);
        for (var i = 0; i < count; i++)
        {
            containers.Add(new InlineUIContainer(new Border
            {
                Width = Math.Max(contentWidth, 1),
                Height = perHeight,
                Background = Brushes.Transparent
            }));
        }

        return containers;
    }

    private double? GetTailTop(InlineUIContainer container)
    {
        var host = container.Parent switch
        {
            Span span => span.Inlines,
            Paragraph p => p.Inlines,
            _ => null
        };
        if (host is null)
            return null;

        var found = false;
        foreach (Inline child in host)
        {
            if (found)
                return child.ContentStart.GetCharacterRect(LogicalDirection.Forward).Top + GetScrollOffset().Y;
            if (ReferenceEquals(child, container))
                found = true;
        }

        return null;
    }

    /// <summary>
    /// First text position whose line extends below <paramref name="wrapTop"/> - the line that
    /// enters the wrap band. Text positions are monotonic in Y, so this is a binary search.
    /// </summary>
    private TextPointer? FindSplitPoint(Paragraph paragraph, double wrapTop)
    {
        var start = paragraph.ContentStart;
        var length = start.GetOffsetToPosition(paragraph.ContentEnd);
        var scrollY = GetScrollOffset().Y;

        TextPointer? found = null;
        var lo = 0;
        var hi = length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var position = start.GetPositionAtOffset(mid, LogicalDirection.Forward);
            if (position is null)
            {
                hi = mid;
                continue;
            }

            var rect = position.GetCharacterRect(LogicalDirection.Forward);
            var bottom = rect.IsEmpty ? double.PositiveInfinity : rect.Bottom + scrollY;
            if (bottom > wrapTop)
            {
                found = position;
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }

        return found;
    }

    /// <summary>Inserts a stack of inlines at an arbitrary text position, splitting a Run when
    /// the position falls mid-run (the tail copies the head's local formatting). Returns the
    /// InlineCollection that now hosts the inserted elements.</summary>
    /// <summary>
    /// Inserts inline elements at an arbitrary text position, splitting the containing run
    /// (and copying its local formatting) when needed. Used by the coordinator to anchor
    /// "in line with text" images and by the wrap engine's between-lines spacers.
    /// </summary>
    public static InlineCollection InsertInlineAt(Paragraph paragraph, TextPointer splitPoint, IReadOnlyList<Inline> toInsert)
        => InsertInlineStack(paragraph, splitPoint, toInsert);

    private static InlineCollection InsertInlineStack(Paragraph paragraph, TextPointer splitPoint, IReadOnlyList<Inline> toInsert)
    {
        switch (splitPoint.Parent)
        {
            case Run run:
            {
                // run.Parent is the logical parent element (Span/Paragraph), not the collection.
                var host = run.Parent is Span runSpan
                    ? runSpan.Inlines
                    : paragraph.Inlines;
                var offset = run.ContentStart.GetOffsetToPosition(splitPoint);
                if (offset > 0 && offset < run.Text.Length)
                {
                    var tail = new Run(run.Text.Substring(offset));
                    run.Text = run.Text.Substring(0, offset);
                    CopyRunLocalFormatting(run, tail);

                    Inline last = run;
                    foreach (var inline in toInsert)
                    {
                        host.InsertAfter(last, inline);
                        last = inline;
                    }

                    host.InsertAfter(last, tail);
                }
                else if (offset <= 0)
                {
                    foreach (var inline in toInsert)
                        host.InsertBefore(run, inline);
                }
                else
                {
                    Inline last = run;
                    foreach (var inline in toInsert)
                    {
                        host.InsertAfter(last, inline);
                        last = inline;
                    }
                }

                return host;
            }
            case Hyperlink hyperlink:
                InsertIntoInlines(hyperlink.Inlines, splitPoint, toInsert);
                return hyperlink.Inlines;
            case Span span:
                InsertIntoInlines(span.Inlines, splitPoint, toInsert);
                return span.Inlines;
            default:
                InsertIntoInlines(paragraph.Inlines, splitPoint, toInsert);
                return paragraph.Inlines;
        }
    }

    private static void InsertIntoInlines(InlineCollection inlines, TextPointer splitPoint, IReadOnlyList<Inline> toInsert)
    {
        foreach (Inline child in inlines)
        {
            if (child.ContentStart.CompareTo(splitPoint) >= 0)
            {
                foreach (var inline in toInsert)
                    inlines.InsertBefore(child, inline);
                return;
            }
        }

        foreach (var inline in toInsert)
            inlines.Add(inline);
    }

    /// <summary>Copies a Run's locally-set formatting so a split tail renders identically to its head.</summary>
    private static void CopyRunLocalFormatting(Run source, Run target)
    {
        var localValues = source.GetLocalValueEnumerator();
        while (localValues.MoveNext())
        {
            var dp = localValues.Current.Property;
            if (dp == Run.TextProperty
                || dp == FrameworkContentElement.NameProperty
                || dp == FrameworkContentElement.TagProperty
                || dp == FrameworkContentElement.DataContextProperty
                || dp == FrameworkContentElement.StyleProperty)
            {
                continue;
            }

            try
            {
                target.SetValue(dp, localValues.Current.Value);
            }
            catch
            {
                // Read-only / unsettable local values (e.g. IsMouseDirectlyOver while the mouse
                // is over the text) must never abort the split.
            }
        }
    }

    private static bool IsInsideTableCell(DependencyObject? current)
    {
        while (current is not null)
        {
            if (current is TableCell)
                return true;

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return false;
    }

    /// <summary>True when the paragraph is the first visual block of the document (possibly
    /// nested in a leading Section) - the case whose top margin the RichTextBox ignores.</summary>
    private static bool IsFirstBlockOfDocument(FlowDocument document, Paragraph paragraph)
    {
        for (Block? current = paragraph; ; )
        {
            var parent = current.Parent;
            switch (parent)
            {
                case Section section:
                    if (!ReferenceEquals(section.Blocks.FirstBlock, current))
                        return false;
                    current = section;
                    continue;
                case FlowDocument doc:
                    return ReferenceEquals(doc.Blocks.FirstBlock, current);
                default:
                    return false;
            }
        }
    }

    private static Thickness NormalizeMargin(Thickness margin)
        => new(
            double.IsNaN(margin.Left) ? 0 : margin.Left,
            double.IsNaN(margin.Top) ? 0 : margin.Top,
            double.IsNaN(margin.Right) ? 0 : margin.Right,
            double.IsNaN(margin.Bottom) ? 0 : margin.Bottom);

    private static bool MarginsCloseEnough(Thickness a, Thickness b)
    {
        a = NormalizeMargin(a);
        b = NormalizeMargin(b);
        return Math.Abs(a.Left - b.Left) < 0.5
               && Math.Abs(a.Top - b.Top) < 0.5
               && Math.Abs(a.Right - b.Right) < 0.5
               && Math.Abs(a.Bottom - b.Bottom) < 0.5;
    }

    /// <summary>Paragraph vertical extent in document space (content-origin, scroll-inclusive).
    /// Returns null when the paragraph has no laid-out geometry yet or measurement fails, so a
    /// single bad paragraph can never abort the whole wrap pass.</summary>
    private (double Top, double Bottom)? GetParagraphDocumentExtent(Paragraph paragraph)
    {
        try
        {
            var scrollY = GetScrollOffset().Y;

            var startRect = paragraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            var endRect = paragraph.ContentEnd.GetCharacterRect(LogicalDirection.Backward);

            if (startRect.IsEmpty || endRect.IsEmpty)
                return null;

            return (startRect.Top + scrollY, endRect.Bottom + scrollY);
        }
        catch
        {
            return null;
        }
    }

    private bool HasMeasurableExtent(Paragraph paragraph)
        => GetParagraphDocumentExtent(paragraph) is { } extent && extent.Bottom > extent.Top;

    /// <summary>Applies again after the next layout pass (used when Apply ran too early).</summary>
    private void ScheduleLayoutRetry()
    {
        if (_disposed || _layoutRetryPending)
            return;

        _layoutRetryPending = true;
        _editor.LayoutUpdated += OnLayoutUpdatedRetry;
    }

    private void OnLayoutUpdatedRetry(object? sender, EventArgs e)
    {
        _editor.LayoutUpdated -= OnLayoutUpdatedRetry;
        _layoutRetryPending = false;
        Apply();
    }

    private double ResolveContentWidth()
    {
        var pageWidth = _editor.Document.PageWidth;
        var width = double.IsNaN(pageWidth) ? _editor.ViewportWidth : pageWidth;
        return Math.Max(0, width - _editor.Padding.Left - _editor.Padding.Right);
    }

    private (double X, double Y) GetScrollOffset()
    {
        _scrollViewer ??= FindScrollViewer(_editor);
        if (_scrollViewer is null)
            return (0, 0);

        return (_scrollViewer.HorizontalOffset, _scrollViewer.VerticalOffset);
    }

    private void OnSurfaceSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged || e.HeightChanged)
            ScheduleApply();
    }

    private void OnRecomputeDebounceTick(object? sender, EventArgs e)
    {
        _recomputeDebounce.Stop();
        Apply();
    }

    private static IEnumerable<Paragraph> EnumerateParagraphs(BlockCollection blocks)
    {
        foreach (Block block in blocks)
        {
            switch (block)
            {
                case Paragraph paragraph:
                    yield return paragraph;
                    break;
                case Section section:
                    foreach (var nested in EnumerateParagraphs(section.Blocks))
                        yield return nested;
                    break;
                case List list:
                    foreach (ListItem item in list.ListItems)
                    {
                        foreach (var nested in EnumerateParagraphs(item.Blocks))
                            yield return nested;
                    }
                    break;
                case Table table:
                    foreach (TableRowGroup group in table.RowGroups)
                    {
                        foreach (TableRow row in group.Rows)
                        {
                            foreach (TableCell cell in row.Cells)
                            {
                                foreach (var nested in EnumerateParagraphs(cell.Blocks))
                                    yield return nested;
                            }
                        }
                    }
                    break;
            }
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer sv)
            return sv;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var found = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (found is not null)
                return found;
        }

        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _recomputeDebounce.Stop();
        _recomputeDebounce.Tick -= OnRecomputeDebounceTick;
        _liveApplyThrottle.Stop();
        _liveApplyThrottle.Tick -= OnLiveApplyThrottleTick;
        _surface.SizeChanged -= OnSurfaceSizeChanged;
        if (_layoutRetryPending)
        {
            _editor.LayoutUpdated -= OnLayoutUpdatedRetry;
            _layoutRetryPending = false;
        }
        Clear();
    }
}
