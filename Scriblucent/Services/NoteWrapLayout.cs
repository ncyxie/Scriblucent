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
    /// <summary>Default gap between a wrapped image and the text (DIPs). A generous gap keeps
    /// the photo visually apart from the surrounding words - like the breathing room office
    /// suites leave around an image.</summary>
    public const double DefaultWrapGap = 24;

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
    private const int LiveConvergenceIterations = 1;
    private const double RecomputeDebounceMs = 120;
    private const double LiveApplyIntervalMs = 66;

    /// <summary>The other lane must beat the currently used lane by at least this ratio before
    /// the paragraph's text flips sides around a photo - small moves must never make the text
    /// jump left/right ("the text keeps running away").</summary>
    private const double LaneHysteresisRatio = 1.25;

    private sealed class InlineSpacerState
    {
        public required Paragraph Paragraph { get; init; }
        public required List<InlineUIContainer> Containers { get; init; }
        public required InlineCollection Host { get; init; }
        /// <summary>Text position where the stack was inserted - stays valid across margin/element
        /// changes (text positions are stable), so <see cref="CaptureWrapState"/> can restore the
        /// stack verbatim after a serialization clear.</summary>
        public required TextPointer Anchor { get; init; }
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
    /// <summary>Current text-lane side per (paragraph, image): 1 = text flows on the left of the
    /// photo, 2 = text flows on the right. Drives the lane hysteresis so re-applies keep the
    /// paragraph on its side until the other side is clearly better.</summary>
    private readonly Dictionary<(Paragraph Paragraph, Guid ImageId), int> _laneSides = new();
    /// <summary>Right-lane wrap bands per paragraph (paragraph -&gt; bands), produced by
    /// <see cref="ComputeParagraphInsets"/> and consumed by <see cref="ReplaceSquareLineSpacers"/>:
    /// each band is (band top, band bottom, photo right edge + gap).</summary>
    private readonly Dictionary<Paragraph, List<(double Top, double Bottom, double PhotoLeft, double PhotoRight)>> _squareRightBands = new();
    /// <summary>Snapshot of the last pass's square bands - <see cref="SquareBandsChanged"/>
    /// compares against it so an unchanged wrap skips the spacer rebuild entirely (the rebuild's
    /// insertions fire TextChanged, which would re-trigger the pass forever).</summary>
    private readonly Dictionary<Paragraph, List<(double Top, double Bottom, double PhotoLeft, double PhotoRight)>> _lastSquareBands = new();
    /// <summary>Transient leading spacers that indent ONLY the lines beside a Square-wrapped photo
    /// (Word-style per-line lane) - lines above and below the photo stay full-width, instead of
    /// the whole paragraph shoving to one side. Removed and re-placed on every pass.</summary>
    private readonly Dictionary<Paragraph, List<InlineUIContainer>> _squareLineSpacers = new();
    /// <summary>Insertion positions of <see cref="_squareLineSpacers"/>' containers (parallel
    /// lists per paragraph) - recorded so <see cref="CaptureWrapState"/> can restore them.</summary>
    private readonly Dictionary<Paragraph, List<TextPointer>> _squareLineSpacerAnchors = new();
    private long _lastSquareSpacerSignature;
    /// <summary>Per-pass working sets, hoisted to fields (cleared per call) so the convergence
    /// iterations and live drag ticks don't allocate fresh collections every pass.</summary>
    private readonly HashSet<(Paragraph, Guid)> _usedLaneKeys = new();
    private readonly HashSet<Paragraph> _rightLaneParagraphs = new();
    private readonly Dictionary<Paragraph, (double Left, double Top, double Right)> _insets = new();
    private readonly Dictionary<Paragraph, double> _topPushes = new();
    private readonly HashSet<Paragraph> _neededSpacers = new();
    private readonly List<(TextPointer Position, double Top, double Bottom)> _linesInRange = new();
    private readonly List<double> _spacerWidths = new();
    private readonly List<Paragraph> _workParagraphs = new();
    private readonly List<(Paragraph Paragraph, Guid ImageId)> _workLaneKeys = new();
    private BlockUIContainer? _pushSpacer;
    private Paragraph? _pushSpacerTarget;
    private double _pushSpacerHeight;
    private ScrollViewer? _scrollViewer;
    private bool _isApplying;
    /// <summary>TextChanged events from outside the engine's own passes - a settled re-apply is
    /// one where <see cref="_externalTextChangeVersion"/> matches <see cref="_lastAppliedTextChangeVersion"/>
    /// (see <see cref="OnEditorTextChangedExternal"/>).</summary>
    private long _externalTextChangeVersion;
    private long _lastAppliedTextChangeVersion;
    /// <summary>Photo geometry snapshot of the last full pass - a drag/resize/mode change makes
    /// <see cref="SessionsChangedSinceLastApply"/> true so the gesture-end full pass still runs
    /// (the live passes are approximate and must settle exactly).</summary>
    private readonly List<(Guid Id, NoteImageWrapMode Mode, double Left, double Top, double Width, double Height)> _lastAppliedSessions = new();
    private bool _layoutRetryPending;
    private bool _disposed;

    public NoteWrapLayoutEngine(RichTextBox editor, FrameworkElement surface, FloatingImageCoordinator images)
    {
        _editor = editor;
        _surface = surface;
        _images = images;

        _surface.SizeChanged += OnSurfaceSizeChanged;
        _editor.TextChanged += OnEditorTextChangedExternal;

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

    /// <summary>Counts the editor's TextChanged events that fire OUTSIDE the engine's own passes
    /// (the engine sets <see cref="_isApplying"/> during them). A settled re-apply is then
    /// detectable: if no external text change happened since the last pass, the wrap state must
    /// still match it, so the pass (and its spacer/push mutations - the perpetual re-apply loop
    /// that kept notes marked "edited") can be skipped entirely. Real text edits bump the
    /// counter and do re-trigger the wrap.</summary>
    private void OnEditorTextChangedExternal(object? sender, EventArgs e)
    {
        if (!_isApplying)
            _externalTextChangeVersion++;
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
            Apply(live: true);
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

        Apply(live: true);
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
        _laneSides.Clear();
        RemovePushSpacer();
        RemoveInlineSpacers();
        RemoveSquareLineSpacers();
        _squareRightBands.Clear();
        _lastSquareBands.Clear();
        _lastSquareSpacerSignature = 0;
    }

    /// <summary>Restores margins, then immediately recomputes them (after save/export).
    /// With <paramref name="live"/> the recompute uses the fast live convergence (single pass
    /// per phase, no render flush) - good enough to keep the wrap applied through a window
    /// exit animation without the full pass's synchronous cost.</summary>
    public void Reapply(bool live = false)
    {
        Clear();
        Apply(live);
    }

    /// <summary>
    /// Point-in-time wrap state captured before a serialization <see cref="Clear"/> and restored
    /// verbatim afterwards - the live view keeps its exact wrap (one layout, no convergence
    /// recompute), so a save-and-close never shows the unwrapped reflow or a pre-animation stall.
    /// </summary>
    public sealed class WrapStateSnapshot
    {
        internal List<(Paragraph Paragraph, Thickness Applied, Thickness Original)> Margins { get; } = new();
        internal List<KeyValuePair<(Paragraph Paragraph, Guid ImageId), int>> LaneSides { get; } = new();
        internal List<InlinePlacement> InlineSpacers { get; } = new();
        internal List<SquarePlacement> SquareSpacers { get; } = new();
        internal Paragraph? PushTarget { get; set; }
        internal double PushHeight { get; set; }
        internal BlockUIContainer? PushContainer { get; set; }

        internal sealed class InlinePlacement
        {
            public required Paragraph Paragraph { get; init; }
            public required TextPointer Anchor { get; init; }
            public required List<InlineUIContainer> Containers { get; init; }
            public required double Top { get; init; }
            public required double LineHeight { get; init; }
            public required double TotalHeight { get; init; }
        }

        internal sealed class SquarePlacement
        {
            public required Paragraph Paragraph { get; init; }
            public required TextPointer Anchor { get; init; }
            public required InlineUIContainer Container { get; init; }
        }
    }

    /// <summary>Captures the currently applied wrap (margins, lanes, spacers) so it can be
    /// restored verbatim by <see cref="RestoreWrapState"/>. Returns null when nothing is applied.</summary>
    public WrapStateSnapshot? CaptureWrapState()
    {
        if (_disposed || _editor.Document is null)
            return null;

        var snapshot = new WrapStateSnapshot();
        foreach (var (paragraph, original) in _originalMargins)
            snapshot.Margins.Add((paragraph, paragraph.Margin, original));
        foreach (var kvp in _laneSides)
            snapshot.LaneSides.Add(kvp);
        foreach (var (paragraph, state) in _inlineSpacers)
        {
            snapshot.InlineSpacers.Add(new WrapStateSnapshot.InlinePlacement
            {
                Paragraph = paragraph,
                Anchor = state.Anchor,
                Containers = state.Containers,
                Top = state.Top,
                LineHeight = state.LineHeight,
                TotalHeight = state.TotalHeight
            });
        }
        foreach (var (paragraph, containers) in _squareLineSpacers)
        {
            if (!_squareLineSpacerAnchors.TryGetValue(paragraph, out var anchors) || anchors.Count != containers.Count)
                continue;
            for (var i = 0; i < containers.Count; i++)
            {
                snapshot.SquareSpacers.Add(new WrapStateSnapshot.SquarePlacement
                {
                    Paragraph = paragraph,
                    Anchor = anchors[i],
                    Container = containers[i]
                });
            }
        }
        if (_pushSpacer is not null && _pushSpacerTarget is not null)
        {
            snapshot.PushTarget = _pushSpacerTarget;
            snapshot.PushHeight = _pushSpacerHeight;
            snapshot.PushContainer = _pushSpacer;
        }

        if (snapshot.Margins.Count == 0
            && snapshot.InlineSpacers.Count == 0
            && snapshot.SquareSpacers.Count == 0
            && snapshot.PushContainer is null)
        {
            return null;
        }

        return snapshot;
    }

    /// <summary>
    /// Restores a state captured by <see cref="CaptureWrapState"/>: re-applies the margins,
    /// re-inserts the spacer stacks at their recorded anchors (text positions are stable across
    /// the clear - only margins/elements changed), and lays out once. Returns whether anything
    /// was restored. Requires the engine to be in the cleared state (e.g. after a serialization
    /// <see cref="Clear"/>); calling it over an already-wrapped flow would duplicate spacers.
    /// </summary>
    public bool RestoreWrapState(WrapStateSnapshot? snapshot)
    {
        if (snapshot is null || _disposed || _editor.Document is null)
            return false;

        var restored = false;
        foreach (var (paragraph, applied, original) in snapshot.Margins)
        {
            if (paragraph.Parent is null)
                continue;
            paragraph.Margin = applied;
            _originalMargins[paragraph] = original;
            restored = true;
        }

        foreach (var kvp in snapshot.LaneSides)
            _laneSides[kvp.Key] = kvp.Value;

        foreach (var placement in snapshot.SquareSpacers)
        {
            if (placement.Paragraph.Parent is null)
                continue;
            InsertInlineStack(placement.Paragraph, placement.Anchor, new Inline[] { placement.Container });
            if (!_squareLineSpacers.TryGetValue(placement.Paragraph, out var list))
                _squareLineSpacers[placement.Paragraph] = list = new List<InlineUIContainer>();
            list.Add(placement.Container);
            if (!_squareLineSpacerAnchors.TryGetValue(placement.Paragraph, out var anchors))
                _squareLineSpacerAnchors[placement.Paragraph] = anchors = new List<TextPointer>();
            anchors.Add(placement.Anchor);
            restored = true;
        }

        foreach (var placement in snapshot.InlineSpacers)
        {
            if (placement.Paragraph.Parent is null || placement.Containers.Count == 0)
                continue;
            var host = InsertInlineStack(placement.Paragraph, placement.Anchor, placement.Containers);
            _inlineSpacers[placement.Paragraph] = new InlineSpacerState
            {
                Paragraph = placement.Paragraph,
                Containers = placement.Containers,
                Host = host,
                Anchor = placement.Anchor,
                Top = placement.Top,
                LineHeight = placement.LineHeight,
                TotalHeight = placement.TotalHeight
            };
            restored = true;
        }

        if (snapshot.PushContainer is not null && snapshot.PushTarget is not null
            && snapshot.PushTarget.Parent is not null
            && _editor.Document.Blocks.Contains(snapshot.PushTarget))
        {
            _editor.Document.Blocks.InsertBefore(snapshot.PushTarget, snapshot.PushContainer);
            _pushSpacer = snapshot.PushContainer;
            _pushSpacerTarget = snapshot.PushTarget;
            _pushSpacerHeight = snapshot.PushHeight;
            restored = true;
        }

        if (restored)
            _editor.UpdateLayout();

        return restored;
    }

    /// <summary>Recomputes wrap insets and applies them as paragraph margins.</summary>
    /// <param name="live">True during an image drag/resize tick: the pass starts from the
    /// current margins (no reset-to-zero snap) and runs fewer convergence iterations so the
    /// text adjusts incrementally instead of "running away" from the cursor. The gesture end
    /// runs the full clean pass through <see cref="ScheduleApply"/>.</param>
    public void Apply(bool live = false)
    {
        if (_disposed || _isApplying || _editor.Document is null || _editor.Document.Blocks.Count == 0)
            return;

        // Notes with no wrap images and nothing already applied used to capture and restore
        // the caret anyway, and restore always forces UpdateLayout. Skip that hitch.
        if (_images.GetWrapSessions().Count == 0 && !HasAppliedWrap())
            return;

        // The wrap pass removes and re-inserts transient spacer blocks: the extent temporarily
        // shrinks (clamping a deep scroll) and the flow mutations can trigger the caret-following,
        // yanking the view away from a photo that is far from the caret's text. Preserve the view
        // across a pass that actually mutates the flow.
        var viewState = _images.CaptureViewState();
        var mutated = false;
        try
        {
            mutated = ApplyCore(live);
        }
        finally
        {
            if (mutated)
                _images.RestoreViewState(viewState);
        }
    }

    private bool HasAppliedWrap()
        => _originalMargins.Count > 0
            || _inlineSpacers.Count > 0
            || _squareLineSpacers.Count > 0
            || _pushSpacer is not null
            || _laneSides.Count > 0;

    private bool ApplyCore(bool live)
    {
        var sessions = _images.GetWrapSessions();
        if (sessions.Count == 0)
        {
            Clear();
            return true;
        }

        var contentWidth = ResolveContentWidth();
        if (contentWidth <= NoteWrapMath.MinimumLaneWidth)
        {
            ScheduleLayoutRetry();
            return false;
        }

        var paragraphs = EnumerateParagraphs(_editor.Document.Blocks).ToList();
        if (paragraphs.Count == 0)
            return false;

        // Apply can be scheduled before the editor's first layout pass (view size 0, character
        // rects empty) - in that case retry once the layout is ready instead of silently no-oping.
        if (!paragraphs.Any(HasMeasurableExtent))
        {
            ScheduleLayoutRetry();
            return false;
        }

        if (!live && _originalMargins.Count > 0
            && _externalTextChangeVersion == _lastAppliedTextChangeVersion)
        {
            // Nothing external changed the flow since the last pass and the wrap is still
            // applied - re-running would reset the margins and remove/re-insert the push
            // spacer (block mutations firing TextChanged per event), which re-schedules this
            // pass forever: the perpetual re-apply loop that kept notes marked "edited" and
            // churned CPU at rest. The settled state is a true no-op.
            return false;
        }

        if (!live && IsWrapSettled(sessions))
        {
            // Nothing external changed the flow or the photo geometry since the last full pass
            // and the wrap is still applied - re-running would reset the margins and
            // remove/re-insert the push spacer (block mutations firing TextChanged per event),
            // which re-schedules this pass forever: the perpetual re-apply loop that kept
            // notes marked "edited" and churned CPU at rest. The settled state is a true no-op.
            return false;
        }

        _isApplying = true;
        // The wrap's spacer mutations fire the editor's TextChanged per event, and each one
        // sweeps the edited runs for the inline marker char (it comes from the save/load/undo
        // paths, never from wrap). Suppress the sweep across the pass; the prior value is
        // restored so a host-side suppression (document load) is never clobbered.
        var priorSweepSuppression = _images.SuppressMarkerSweep;
        _images.SuppressMarkerSweep = true;
        try
        {
            ApplyWithConvergence(sessions, paragraphs, contentWidth, live);
            if (!live)
            {
                _lastAppliedTextChangeVersion = _externalTextChangeVersion;
                _lastAppliedSessions.Clear();
                foreach (var s in sessions)
                {
                    _lastAppliedSessions.Add((
                        s.Id,
                        s.WrapMode,
                        s.DocumentLeft,
                        s.DocumentTop,
                        s.Width,
                        s.Height));
                }
            }
        }
        finally
        {
            _images.SuppressMarkerSweep = priorSweepSuppression;
            _isApplying = false;
        }

        return true;
    }

    /// <summary>True when the wrap is fully applied and nothing (text or photo geometry) changed
    /// since the last full pass - a re-apply would be a pure no-op whose transient mutations are
    /// the source of the perpetual re-apply loop. Text edits, photo drags/resizes, and wrap-mode
    /// changes all register as "not settled" and do re-trigger the wrap.</summary>
    private bool IsWrapSettled(IReadOnlyList<NoteImageSession> sessions)
    {
        if (_originalMargins.Count == 0)
            return false;

        foreach (var (paragraph, original) in _originalMargins)
        {
            if (paragraph.Parent is null || MarginsCloseEnough(paragraph.Margin, original))
                return false;
        }

        if (_externalTextChangeVersion != _lastAppliedTextChangeVersion)
            return false;

        return !SessionsChangedSinceLastApply(sessions);
    }

    private bool SessionsChangedSinceLastApply(IReadOnlyList<NoteImageSession> sessions)
    {
        if (sessions.Count != _lastAppliedSessions.Count)
            return true;

        for (var i = 0; i < sessions.Count; i++)
        {
            var s = sessions[i];
            var last = _lastAppliedSessions[i];
            if (s.Id != last.Id
                || s.WrapMode != last.Mode
                || Math.Abs(s.DocumentLeft - last.Left) > 0.5
                || Math.Abs(s.DocumentTop - last.Top) > 0.5
                || Math.Abs(s.Width - last.Width) > 0.5
                || Math.Abs(s.Height - last.Height) > 0.5)
            {
                return true;
            }
        }

        return false;
    }

    private void ApplyWithConvergence(
        IReadOnlyList<NoteImageSession> sessions,
        IReadOnlyList<Paragraph> paragraphs,
        double contentWidth,
        bool live)
    {
        // Inline spacers are NOT wiped here - they are updated in place during Phase B so small
        // image adjustments (which keep the same split line) only change the reserved height
        // instead of reflowing the paragraph's text (visible flashing). The pre-pass layout
        // ensures the measurements below see the settled geometry. Live ticks skip both: they
        // start from the current margins (no reset-to-zero snap), nothing changes the flow
        // between drag ticks except our own writes, and every tick ends with the convergence
        // loop's own layout - so the geometry is already current (one less full layout per tick).
        if (!live)
        {
            ResetTransientMargins();
            _editor.UpdateLayout();
        }

        _topPushes.Clear();
        var maxIterations = live ? LiveConvergenceIterations : MaxConvergenceIterations;

        // Phase A: converge Square lanes only - no top pushes yet, so straddling paragraphs
        // keep their pre-push geometry and the between-lines split positions are measured
        // from a settled layout.
        Converge(sessions, paragraphs, contentWidth, _topPushes, allowTopPushes: false, maxIterations, live);

        // Phase B: for straddling paragraphs, reserve the band "between lines" with a transient
        // inline spacer (Word-style split inside the paragraph). Table cells are excluded -
        // Word does not split those.
        var neededSpacers = EstablishInlineSpacers(sessions, paragraphs, contentWidth);

        // Drop spacers whose paragraph no longer straddles any band (image moved away).
        RemoveObsoleteInlineSpacers(neededSpacers);

        // Phase C: re-converge with top pushes (whole-paragraph pushes for paragraphs that start
        // inside a band; the ratchet) - the spacers shift the flow below their bands, Square
        // membership re-adapts. Only a TopAndBottom image can produce pushes or straddling
        // splits, so a square-only note (the common drag case) skips this phase entirely - it
        // would re-run the same computation with empty pushes: two full document layouts per
        // pass for nothing (the drag CPU is dominated by these layouts).
        if (sessions.Any(s => s.WrapMode == NoteImageWrapMode.TopAndBottom))
        {
            Converge(sessions, paragraphs, contentWidth, _topPushes, allowTopPushes: true, maxIterations, live);
        }
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
        bool allowTopPushes,
        int maxIterations,
        bool live)
    {
        for (var iteration = 0; iteration < maxIterations; iteration++)
        {
            var next = ComputeParagraphInsets(sessions, paragraphs, contentWidth, topPushes);
            var marginsChanged = ApplyParagraphInsets(next);
            var bandsChanged = SquareBandsChanged();
            if (!marginsChanged && !bandsChanged && !(allowTopPushes && topPushes.Count == 0))
            {
                // Neither the margins nor the square wrap bands changed since the last pass -
                // the spacers would be identical, so skip the rebuild and the layouts entirely.
                // The rebuild re-inserts every spacer, firing TextChanged per event, which
                // re-schedules this pass - without this the engine re-wraps forever, keeping
                // the note perpetually marked "edited" and burning CPU at rest.
                return;
            }

            RememberSquareBands();

            // Live ticks (image drag/resize) reflow the Square lanes too, so the words are
            // pushed around the moving photo in real time - the same live feel TopAndBottom
            // has. The re-apply is throttled and converges in a couple of passes.
            var spacersChanged = ReplaceSquareLineSpacers(live);

            _editor.UpdateLayout();

            if (!live)
            {
                // WPF defers FlowDocument text layout: character rects stay stale after a paragraph
                // margin change until the queued layout work runs, which would make every re-measure
                // see the pre-margin position and grow pushes forever. Flush the queue so the next
                // measurement reflects the margins just applied. Live ticks skip this: they are
                // transient (the next tick and the gesture-end full pass re-settle the final state),
                // so each tick runs a single layout pass instead of a layout + pump + layout - the
                // drag smoothness win.
                _editor.Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
                _editor.UpdateLayout();
            }

            var grew = allowTopPushes && GrowTopPushes(sessions, paragraphs, topPushes);
            if (!marginsChanged && !grew && !spacersChanged)
                return;
        }
    }

    /// <summary>True when the freshly computed square bands differ from the last pass's within a
    /// small tolerance (the layout reads can jitter sub-pixel). An unchanged wrap needs no spacer
    /// rebuild - skipping it is what breaks the perpetual re-apply loop.</summary>
    private bool SquareBandsChanged()
    {
        if (_squareRightBands.Count != _lastSquareBands.Count)
            return true;

        foreach (var (paragraph, bands) in _squareRightBands)
        {
            if (!_lastSquareBands.TryGetValue(paragraph, out var last) || last.Count != bands.Count)
                return true;

            for (var i = 0; i < bands.Count; i++)
            {
                var a = bands[i];
                var b = last[i];
                if (Math.Abs(a.Top - b.Top) > 0.5
                    || Math.Abs(a.Bottom - b.Bottom) > 0.5
                    || Math.Abs(a.PhotoLeft - b.PhotoLeft) > 0.5
                    || Math.Abs(a.PhotoRight - b.PhotoRight) > 0.5)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private void RememberSquareBands()
    {
        _lastSquareBands.Clear();
        foreach (var (paragraph, bands) in _squareRightBands)
            _lastSquareBands[paragraph] = new List<(double, double, double, double)>(bands);
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
        const int laneLeft = 1;
        const int laneRight = 2;

        _insets.Clear();
        _usedLaneKeys.Clear();
        _rightLaneParagraphs.Clear();
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

                var leftLaneWidth = wrapLeft;
                var rightLaneWidth = contentWidth - wrapRight;
                var laneKey = (paragraph, session.Id);

                if (leftLaneWidth < NoteWrapMath.MinimumLaneWidth
                    && rightLaneWidth < NoteWrapMath.MinimumLaneWidth)
                {
                    // No usable lane - the paragraph cannot wrap this image at all.
                    _laneSides.Remove(laneKey);
                    continue;
                }

                // Hysteresis: keep the paragraph on its current side unless the other lane is
                // clearly better (or the current one became unusable). Prevents the text from
                // jumping left/right on small photo moves - the "text keeps running away" feel.
                int side;
                var previous = _laneSides.GetValueOrDefault(laneKey);
                if (previous == laneLeft
                    && leftLaneWidth >= NoteWrapMath.MinimumLaneWidth
                    && leftLaneWidth * LaneHysteresisRatio >= rightLaneWidth)
                {
                    side = laneLeft;
                }
                else if (previous == laneRight
                    && rightLaneWidth >= NoteWrapMath.MinimumLaneWidth
                    && rightLaneWidth * LaneHysteresisRatio >= leftLaneWidth)
                {
                    side = laneRight;
                }
                else
                {
                    side = rightLaneWidth >= leftLaneWidth ? laneRight : laneLeft;
                }

                _laneSides[laneKey] = side;
                _usedLaneKeys.Add(laneKey);

                if (side == laneRight)
                {
                    // Right lane: the inset is applied PER LINE via transient leading spacers
                    // (ReplaceSquareLineSpacers) - the paragraph itself is NOT indented, so the
                    // lines above and below the photo keep full width. Only the lines inside
                    // the band are shortened. The per-paragraph band list is rebuilt in place
                    // (reused across passes) to avoid per-iteration allocations.
                    if (!_squareRightBands.TryGetValue(paragraph, out var bands))
                        _squareRightBands[paragraph] = bands = new List<(double, double, double, double)>();
                    else
                        bands.Clear();
                    bands.Add((wrapTop, wrapBottom, wrapLeft, wrapRight));
                    _rightLaneParagraphs.Add(paragraph);
                }
                else
                {
                    right = Math.Max(right, contentWidth - wrapLeft);
                }
            }

            if (topPushes.TryGetValue(paragraph, out var push))
                top = Math.Max(top, push);

            if (left > 0 || top > 0 || right > 0)
                _insets[paragraph] = (left, top, right);
        }

        // Forget sides and bands for paragraphs that no longer wrap any image.
        _workLaneKeys.Clear();
        foreach (var key in _laneSides.Keys)
        {
            if (!_usedLaneKeys.Contains(key))
                _workLaneKeys.Add(key);
        }
        foreach (var key in _workLaneKeys)
            _laneSides.Remove(key);

        _workParagraphs.Clear();
        foreach (var key in _squareRightBands.Keys)
        {
            if (!_rightLaneParagraphs.Contains(key))
                _workParagraphs.Add(key);
        }
        foreach (var key in _workParagraphs)
            _squareRightBands.Remove(key);

        return _insets;
    }

    private bool ApplyParagraphInsets(
        Dictionary<Paragraph, (double Left, double Top, double Right)> insets,
        bool includeSides = true)
    {
        var changed = false;

        foreach (var (paragraph, (left, top, right)) in insets)
        {
            var original = GetOriginalMargin(paragraph);
            // Block.Margin defaults to a NaN thickness ("unset"); NaN + inset stays NaN and would
            // render as no margin at all, so insets must be computed against the normalized value.
            var baseMargin = NormalizeMargin(original);
            // Live ticks freeze the Square lanes - only the TopAndBottom top pushes apply.
            var sideLeft = includeSides ? left : 0;
            var sideRight = includeSides ? right : 0;

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
                baseMargin.Left + sideLeft,
                baseMargin.Top + marginTop,
                baseMargin.Right + sideRight,
                baseMargin.Bottom);
            if (!MarginsCloseEnough(paragraph.Margin, margin))
                changed = true;

            paragraph.Margin = margin;
            _originalMargins[paragraph] = original;
        }

        if (includeSides)
        {
            _workParagraphs.Clear();
            foreach (var paragraph in _originalMargins.Keys)
            {
                if (!insets.ContainsKey(paragraph))
                    _workParagraphs.Add(paragraph);
            }
            foreach (var paragraph in _workParagraphs)
            {
                _originalMargins.Remove(paragraph, out var original);
                paragraph.Margin = original;
                changed = true;
            }
        }

        return changed;
    }

    private Thickness GetOriginalMargin(Paragraph paragraph)
        => _originalMargins.TryGetValue(paragraph, out var original) ? original : paragraph.Margin;

    // ------------------------------------------------------------ square per-line spacers

    /// <summary>
    /// Rebuilds the transient leading spacers that indent ONLY the lines of a paragraph that
    /// fall inside a Square-wrapped photo's band (the Word-style per-line lane). Lines above and
    /// below the band keep full width, so pushing a photo into a text block no longer shoves the
    /// whole paragraph to one side. Returns whether the placement changed (convergence signal).
    /// With <paramref name="live"/> the post-removal layout is skipped: the measurements run
    /// against the previous tick's geometry (a couple of px stale - the drag's transient state
    /// and the gesture-end full pass settle exactly), which avoids re-formatting the wrapped
    /// paragraphs' text on every tick - the dominant cost of a drag.
    /// </summary>
    private bool ReplaceSquareLineSpacers(bool live = false)
    {
        RemoveSquareLineSpacers();

        if (!live)
        {
            // The removal above leaves the flow's character rects stale until a layout pass - the
            // line-start measurements below would otherwise see the previous pass's geometry and
            // re-place the spacers at the wrong lines every pass (slow oscillation instead of the
            // 2-pass convergence).
            _editor?.UpdateLayout();
        }

        if (_squareRightBands.Count > 0)
        {
            foreach (var (paragraph, bands) in _squareRightBands)
            {
                if (paragraph.Parent is null || bands.Count == 0)
                    continue;

                var bandTop = double.MaxValue;
                var bandBottom = double.MinValue;
                foreach (var (top, bottom, _, _) in bands)
                {
                    bandTop = Math.Min(bandTop, top);
                    bandBottom = Math.Max(bandBottom, bottom);
                }

                var placements = new List<(TextPointer Position, double Width)>();
                var lineStarts = FindLinesInRange(paragraph, bandTop, bandBottom);
                for (var lineIndex = 0; lineIndex < lineStarts.Count; lineIndex++)
                {
                    var line = lineStarts[lineIndex];
                    var lineEnd = lineIndex + 1 < lineStarts.Count
                        ? lineStarts[lineIndex + 1].Position
                        : paragraph.ContentEnd;

                    // The photo span covering this line (the leftmost photo's left edge to the
                    // rightmost photo's right edge, both + wrap gap).
                    double photoLeft = double.MaxValue;
                    double photoRight = double.MinValue;
                    var intersects = false;
                    foreach (var (lineBandTop, lineBandBottom, bandPhotoLeft, bandPhotoRight) in bands)
                    {
                        if (line.Bottom > lineBandTop && line.Top < lineBandBottom)
                        {
                            intersects = true;
                            photoLeft = Math.Min(photoLeft, bandPhotoLeft);
                            photoRight = Math.Max(photoRight, bandPhotoRight);
                        }
                    }

                    if (!intersects)
                        continue;

                    // The photo is painted one padding-width to the right of its document
                    // point. The keep-out edges have to use that painted box, or one side
                    // of the text loses the gap and the other side gains it.
                    var padLeft = _editor?.Padding.Left ?? 0;
                    var scrollX = GetScrollOffset().X;
                    var layoutLeft = photoLeft + padLeft - scrollX;
                    var layoutRight = photoRight + padLeft - scrollX;

                    // Word-style "word word [photo] word word": the first glyph that would
                    // enter the keep-out box moves to the far side. Words that stay on the
                    // near side end before the gap; the spacer starts the rest at the far gap.
                    var split = FindSplitCharInLine(paragraph, line.Position, lineEnd, layoutLeft);
                    if (split is null)
                        continue; // the photo is past this line's text - the line stays full width

                    var splitRect = split.GetCharacterRect(LogicalDirection.Forward);
                    if (splitRect.IsEmpty)
                        continue;

                    var width = layoutRight - splitRect.Left;
                    if (width < 1)
                        continue;

                    placements.Add((split, width));
                }

                foreach (var (position, width) in placements.OrderByDescending(p => p.Position.CompareTo(paragraph.ContentStart)))
                {
                    var spacer = new Border
                    {
                        Width = width,
                        Height = 1,
                        Background = Brushes.Transparent,
                        IsHitTestVisible = false,
                        Focusable = false
                    };
                    var container = new InlineUIContainer(spacer);
                    InsertInlineStack(paragraph, position, new Inline[] { container });
                    if (!_squareLineSpacers.TryGetValue(paragraph, out var list))
                        _squareLineSpacers[paragraph] = list = new List<InlineUIContainer>();
                    list.Add(container);
                    if (!_squareLineSpacerAnchors.TryGetValue(paragraph, out var anchors))
                        _squareLineSpacerAnchors[paragraph] = anchors = new List<TextPointer>();
                    anchors.Add(position);
                }
            }
        }

        var signature = BuildSquareSpacerSignature();
        if (signature == _lastSquareSpacerSignature)
            return false;

        _lastSquareSpacerSignature = signature;
        return true;
    }

    private void RemoveSquareLineSpacers()
    {
        foreach (var (_, containers) in _squareLineSpacers)
        {
            foreach (var container in containers)
            {
                var host = container.Parent switch
                {
                    Span span => span.Inlines,
                    Paragraph paragraph => paragraph.Inlines,
                    _ => null
                };
                host?.Remove(container);
            }
        }

        _squareLineSpacers.Clear();
        _squareLineSpacerAnchors.Clear();
    }

    /// <summary>Signature of the current spacer placement (paragraph offset, sorted spacer widths)
    /// so the convergence loop can tell "placed the same things as last pass" without depending on
    /// element-position offsets, which are not stable across transient re-insertions. A numeric
    /// hash (no string/formatting allocations) - a hash collision only means "treated as
    /// unchanged", which costs one convergence iteration at most.</summary>
    private long BuildSquareSpacerSignature()
    {
        if (_editor?.Document is null || _squareLineSpacers.Count == 0)
            return 0;

        var hash = 17L;
        foreach (var (paragraph, containers) in _squareLineSpacers
            .OrderBy(kv => _editor.Document.ContentStart.GetOffsetToPosition(kv.Key.ContentStart)))
        {
            hash = unchecked(hash * 31 + _editor.Document.ContentStart.GetOffsetToPosition(paragraph.ContentStart));

            _spacerWidths.Clear();
            foreach (var container in containers)
                _spacerWidths.Add(container.Child is Border { Width: var width } ? width : 0d);
            _spacerWidths.Sort();

            foreach (var width in _spacerWidths)
                hash = unchecked(hash * 31 + (long)Math.Round(width * 100));
        }

        return hash;
    }

    /// <summary>First text position of every line of the paragraph that falls inside
    /// [<paramref name="bandTop"/>, <paramref name="bandBottom"/>], with each line's document-space
    /// vertical extent and its end bound (the next line's start, or the paragraph end). The entry
    /// line is found by binary search on the monotonic line tops, then lines are walked one by one
    /// via the text container's line structure (<see cref="TextPointer.GetLineStartPosition"/>) -
    /// the per-line binary searches they replace were the dominant rect-read cost of a drag tick.</summary>
    private List<(TextPointer Position, double Top, double Bottom)> FindLinesInRange(
        Paragraph paragraph,
        double bandTop,
        double bandBottom)
    {
        _linesInRange.Clear();
        if (paragraph.ContentStart.GetOffsetToPosition(paragraph.ContentEnd) == 0)
            return _linesInRange;

        // A position on the band's first line, then that line's very first character.
        var entry = FindSplitPoint(paragraph, bandTop);
        if (entry is null)
            return _linesInRange;

        var entryRect = entry.GetCharacterRect(LogicalDirection.Forward);
        if (entryRect.IsEmpty)
            return _linesInRange;

        // The band is in document space - the character rects are layout space (scroll
        // independent), so the line extents and the walk's exit bound are corrected by the
        // scroll offset. Without it, a scrolled-down document compares layout tops against
        // document band bottoms and the wrap silently dies below the fold.
        var scrollY = GetScrollOffset().Y;
        var current = FindFirstPositionWithTopAtLeast(paragraph, entryRect.Top);
        while (current is not null)
        {
            var rect = current.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty || rect.Top + scrollY > bandBottom + 0.5)
                break;

            _linesInRange.Add((current, rect.Top + scrollY, rect.Bottom + scrollY));

            var next = current.GetLineStartPosition(1);
            if (next is null || next.CompareTo(paragraph.ContentEnd) >= 0)
                break;

            current = next;
        }

        return _linesInRange;
    }

    /// <summary>First text position inside the line [<paramref name="lineStart"/>, <paramref name="lineEnd"/>)
    /// whose glyph enters the keep-out box at <paramref name="xTarget"/>. A word that started
    /// just outside but runs into the gap moves to the far side, so the near side ends before
    /// the gap. Null when the whole line's text stays clear of the box.</summary>
    private static TextPointer? FindSplitCharInLine(
        Paragraph paragraph,
        TextPointer lineStart,
        TextPointer lineEnd,
        double xTarget)
    {
        var length = lineStart.GetOffsetToPosition(lineEnd);
        if (length <= 0)
            return null;

        TextPointer? found = null;
        var lo = 0;
        var hi = length;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            var position = lineStart.GetPositionAtOffset(mid, LogicalDirection.Forward);
            if (position is null || position.CompareTo(paragraph.ContentEnd) >= 0)
            {
                hi = mid;
                continue;
            }

            var rect = position.GetCharacterRect(LogicalDirection.Forward);
            var right = rect.IsEmpty ? double.NegativeInfinity : rect.Right;
            if (right > xTarget)
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

    /// <summary>First text position whose line top reaches <paramref name="targetTop"/> (the
    /// line tops are monotonic in position, so this is a binary search).</summary>
    private static TextPointer? FindFirstPositionWithTopAtLeast(Paragraph paragraph, double targetTop)
    {
        var start = paragraph.ContentStart;
        var length = start.GetOffsetToPosition(paragraph.ContentEnd);
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
            var top = rect.IsEmpty ? double.NegativeInfinity : rect.Top;
            if (top >= targetTop - 0.5)
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
        _neededSpacers.Clear();
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

        // No TopAndBottom image at all (the common square-only note): nothing can straddle a
        // band - skip the per-paragraph extent reads entirely (a drag tick used to pay them
        // every time, on top of the Square lane pass).
        if (bands.Count == 0)
            return _neededSpacers;

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
                _neededSpacers.Add(paragraph);
            }
        }

        return _neededSpacers;
    }

    private void RemoveObsoleteInlineSpacers(HashSet<Paragraph> needed)
    {
        _workParagraphs.Clear();
        foreach (var paragraph in _inlineSpacers.Keys)
        {
            if (!needed.Contains(paragraph))
                _workParagraphs.Add(paragraph);
        }
        foreach (var paragraph in _workParagraphs)
        {
            RemoveInlineSpacer(_inlineSpacers[paragraph]);
            _inlineSpacers.Remove(paragraph);
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
            Anchor = newSplitPoint,
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
    /// A single tall container can be capped by older paragraphs with a fixed line height
    /// (LineStackingStrategy.BlockLineHeight), letting the
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
        _editor.TextChanged -= OnEditorTextChangedExternal;
        if (_layoutRetryPending)
        {
            _editor.LayoutUpdated -= OnLayoutUpdatedRetry;
            _layoutRetryPending = false;
        }
        Clear();
    }
}
