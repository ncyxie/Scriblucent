using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Shapes;
using Scriblucent.Services;

namespace Scriblucent;

public partial class NoteEditorWindow
{
    private void InvalidateSelectionTiles()
    {
        if (_tableSelectionPath is not null) _tableSelectionPath.Data = Geometry.Empty;
        _selectionTileStart = null;
        _selectionTileEnd = null;
        _selectionLineRects.Clear();
        _selectionTiles.Clear();
        if (_selectionHighlightPath is not null)
            _selectionHighlightPath.Data = Geometry.Empty;
    }

    private void InvalidateFindTiles()
    {
        _findComposedFirstTile = -1;
        _findComposedLastTile = -1;
        _findTiles.Clear();
        if (_findNormalHighlightPath is not null)
            _findNormalHighlightPath.Data = Geometry.Empty;
        InvalidateFindActiveTiles();
    }

    private void InvalidateFindActiveTiles()
    {
        _findActiveTiles.Clear();
        if (_findActiveHighlightPath is not null)
            _findActiveHighlightPath.Data = Geometry.Empty;
    }

    private bool HighlightTilesNeedUpdate(Dictionary<int, HighlightTile> cache)
    {
        if (_contentScrollViewer is null)
            return false;

        var viewer = _contentScrollViewer;
        var width = Math.Max(ContentTextBox.ActualWidth, 1);
        var height = Math.Max(ContentTextBox.ActualHeight, 1);
        var left = Math.Floor(viewer.HorizontalOffset / width) * width - width;
        var first = Math.Max(0, (int)Math.Floor(Math.Max(0,
            viewer.VerticalOffset - height) / HighlightTileHeight));
        var last = Math.Max(first, (int)Math.Floor((viewer.VerticalOffset + height * 2)
            / HighlightTileHeight));
        return cache.Count != last - first + 1
            || !cache.TryGetValue(first, out var firstTile)
            || !cache.ContainsKey(last)
            || Math.Abs(firstTile.Left - left) >= 0.5
            || Math.Abs(firstTile.Width - width * 3) >= 0.5;
    }

    private bool SelectionTilesNeedVisibleValidation()
        => _selectionTiles.Values.Any(tile => tile.Complete && !SelectionTileValidated(tile)
            && IsHighlightTileVisible(tile));

    private bool SelectionTileValidated(HighlightTile tile)
    {
        if (!tile.ValidatedInViewport) return false;
        var top = Math.Max(tile.Index * HighlightTileHeight, _contentScrollViewer!.VerticalOffset);
        var bottom = Math.Min((tile.Index + 1) * HighlightTileHeight,
            _contentScrollViewer.VerticalOffset + ContentTextBox.ActualHeight);
        return top >= tile.ValidatedTop - 0.1 && bottom <= tile.ValidatedBottom + 0.1;
    }

    private bool IsHighlightTileVisible(HighlightTile tile)
    {
        var top = _contentScrollViewer!.VerticalOffset;
        var bottom = top + ContentTextBox.ActualHeight;
        return tile.Index * HighlightTileHeight < bottom
            && (tile.Index + 1) * HighlightTileHeight > top;
    }

    private (int First, int Last) FindComposedTileRange()
        => FindComposedTileRange(_contentScrollViewer!.VerticalOffset,
            ContentTextBox.ActualHeight);

    internal static (int First, int Last) FindComposedTileRange(double top, double viewportHeight)
    {
        var bottom = top + viewportHeight;
        var first = Math.Max(0, (int)Math.Floor((top - HighlightTileHeight) / HighlightTileHeight));
        var last = Math.Max(first, (int)Math.Floor((bottom + HighlightTileHeight) / HighlightTileHeight));
        return (first, last);
    }

    private bool FindCompositionNeedsUpdate()
    {
        if (_contentScrollViewer is null)
            return false;
        var (first, last) = FindComposedTileRange();
        return first != _findComposedFirstTile || last != _findComposedLastTile;
    }

    private List<HighlightTile> PrepareHighlightTiles(
        Dictionary<int, HighlightTile> cache, out bool changed)
    {
        changed = false;
        var viewer = _contentScrollViewer!;
        var x = viewer.HorizontalOffset;
        var y = viewer.VerticalOffset;
        var width = Math.Max(ContentTextBox.ActualWidth, 1);
        var height = Math.Max(ContentTextBox.ActualHeight, 1);
        var left = Math.Floor(x / width) * width - width;
        var first = Math.Max(0, (int)Math.Floor(Math.Max(0, y - height) / HighlightTileHeight));
        var last = Math.Max(first, (int)Math.Floor((y + height * 2) / HighlightTileHeight));

        foreach (var (index, tile) in cache.ToArray())
        {
            if (index >= first && index <= last && Math.Abs(tile.Left - left) < 0.5
                && Math.Abs(tile.Width - width * 3) < 0.5)
                continue;

            cache.Remove(index);
            changed = true;
        }

        for (var index = first; index <= last; index++)
        {
            if (cache.ContainsKey(index))
                continue;

            cache.Add(index, CreateHighlightTile(index, left, width * 3));
            changed = true;
        }

        var center = y + height * 0.5;
        return cache.Values.OrderBy(tile =>
            Math.Abs((tile.Index + 0.5) * HighlightTileHeight - center)).ToList();
    }

    private static HighlightTile CreateHighlightTile(int index, double left, double width)
    {
        var translation = new TranslateTransform(left, index * HighlightTileHeight);
        return new HighlightTile
        {
            Index = index,
            Left = left,
            Width = width,
            NormalGeometry = new GeometryGroup { FillRule = FillRule.Nonzero, Transform = translation }
        };
    }

    private void EnsureHighlightPaths()
    {
        if (_selectionHighlightPath is null)
        {
            _selectionHighlightPath = new Path { IsHitTestVisible = false };
            SelectionHighlightOverlay.Children.Add(_selectionHighlightPath);
        }

        if (_findNormalHighlightPath is null)
        {
            _findNormalHighlightPath = new Path { IsHitTestVisible = false };
            FindHighlightOverlay.Children.Add(_findNormalHighlightPath);
        }

        if (_findActiveHighlightPath is null)
        {
            _findActiveHighlightPath = new Path { IsHitTestVisible = false };
            FindHighlightOverlay.Children.Add(_findActiveHighlightPath);
        }
    }

    // One WPF fill operation joins the adjacent clipped tile edges. Separate translucent
    // Paths blend each antialiased edge against the page and leave a periodic pale seam.
    internal static Geometry ComposeHighlightTileGeometry(IEnumerable<GeometryGroup> tiles,
        double originX = 0, double originY = 0)
    {
        var geometry = new GeometryGroup
        {
            FillRule = FillRule.Nonzero,
            Transform = new TranslateTransform(-originX, -originY)
        };
        foreach (var tile in tiles)
            geometry.Children.Add(tile);
        return geometry;
    }

    private void ComposeSelectionTiles()
    {
        // Measuring a selection can complete pending layout and invalidate its tiles.
        // A rapid deletion may therefore clear the cache during the paint itself.
        if (_selectionTiles.Count == 0)
        {
            _selectionHighlightPath!.Data = Geometry.Empty;
            return;
        }
        var originX = _selectionTiles.Values.Min(tile => tile.Left);
        var originY = _selectionTiles.Keys.Min() * HighlightTileHeight;
        Canvas.SetLeft(_selectionHighlightPath!, originX);
        Canvas.SetTop(_selectionHighlightPath!, originY);
        _selectionHighlightPath!.Data = ComposeHighlightTileGeometry(
            _selectionTiles.Values.Select(tile => tile.NormalGeometry), originX, originY);
    }

    private void ComposeFindNormalTiles()
    {
        var tiles = _findTiles.Values.Where(tile => tile.Index >= _findComposedFirstTile
            && tile.Index <= _findComposedLastTile).ToList();
        if (tiles.Count == 0)
        {
            _findNormalHighlightPath!.Data = Geometry.Empty;
            return;
        }
        var originX = tiles.Min(tile => tile.Left);
        var originY = tiles.Min(tile => tile.Index) * HighlightTileHeight;
        Canvas.SetLeft(_findNormalHighlightPath!, originX);
        Canvas.SetTop(_findNormalHighlightPath!, originY);
        _findNormalHighlightPath!.Data = ComposeHighlightTileGeometry(
            tiles.Select(tile => tile.NormalGeometry), originX, originY);
    }

    private void ComposeFindActiveTiles()
    {
        var tiles = _findActiveTiles.Values.Where(tile => tile.Index >= _findComposedFirstTile
            && tile.Index <= _findComposedLastTile).ToList();
        if (tiles.Count == 0)
        {
            _findActiveHighlightPath!.Data = Geometry.Empty;
            return;
        }
        var originX = tiles.Min(tile => tile.Left);
        var originY = tiles.Min(tile => tile.Index) * HighlightTileHeight;
        Canvas.SetLeft(_findActiveHighlightPath!, originX);
        Canvas.SetTop(_findActiveHighlightPath!, originY);
        _findActiveHighlightPath!.Data = ComposeHighlightTileGeometry(
            tiles.Select(tile => tile.NormalGeometry), originX, originY);
    }

    private void PaintSelectionTiles()
    {
        if (_contentScrollViewer is null || SelectionHighlightOverlay is null)
            return;

        if (!TryGetPaintedSelection(out var start, out var end))
        {
            InvalidateSelectionTiles();
            return;
        }

        ApplySelectionOverlayCommit();
        if (TryPaintTableSelection(start, end)) return;
        EnsureHighlightPaths();
        _selectionHighlightPath!.Fill = CreateSelectionOverlayBrush();
        MarkChangedSelectionTiles(start, end);
        var started = Stopwatch.GetTimestamp();
        var tiles = PrepareHighlightTiles(_selectionTiles, out var changed);
        if (changed)
            _selectionLineRects.Clear();
        var lines = 0;
        var built = 0;
        var pending = false;
        var visited = false;
        foreach (var tile in tiles)
        {
            var replacing = tile.NeedsSelectionRebuild;
            var validating = tile.Complete && !SelectionTileValidated(tile)
                && IsHighlightTileVisible(tile);
            if (tile.Complete && !replacing && !validating)
                continue;

            var work = replacing || validating
                ? tile.PendingReplacement ??= CreateHighlightTile(tile.Index, tile.Left, tile.Width)
                : tile;

            if (visited && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= HighlightBuildBudgetMs)
            {
                pending = true;
                break;
            }

            visited = true;
            built++;
            lines += BuildSelectionTile(work, start, end, started);
            var published = PublishHighlightTile(work);
            if (replacing || validating)
            {
                if (work.Complete)
                {
                    _selectionTiles[tile.Index] = work;
                    changed = true;
                }
                else
                {
                    pending = true;
                }
            }
            else
            {
                changed |= published;
                pending |= !work.Complete;
            }
        }

        if (changed)
            ComposeSelectionTiles();

        if (ScrollPerfDiag.Enabled)
            ScrollPerfDiag.LogEditorSelectionPaint(Stopwatch.GetElapsedTime(started).TotalMilliseconds, lines, built);
        if (pending)
        {
            var visiblePending = tiles.Any(tile => IsHighlightTileVisible(tile)
                && (tile.NeedsSelectionRebuild || !tile.Complete
                    || !SelectionTileValidated(tile)));
            QueueSelectionOverlay(visiblePending
                ? System.Windows.Threading.DispatcherPriority.Render
                : System.Windows.Threading.DispatcherPriority.Background);
        }
    }

    private void MarkChangedSelectionTiles(TextPointer start, TextPointer end)
    {
        if (_selectionTileStart is { } oldStart && _selectionTileEnd is { } oldEnd
            && (oldStart.CompareTo(start) != 0 || oldEnd.CompareTo(end) != 0))
        {
            _selectionLineRects.Clear();
            foreach (var tile in _selectionTiles.Values)
            {
                if (!SelectionChangeTouchesTile(tile, oldStart, start)
                    && !SelectionChangeTouchesTile(tile, oldEnd, end))
                    continue;

                tile.NeedsSelectionRebuild = true;
                tile.PendingReplacement = null;
            }
        }

        _selectionTileStart = start;
        _selectionTileEnd = end;
    }

    private static bool SelectionChangeTouchesTile(HighlightTile tile,
        TextPointer oldEndpoint, TextPointer newEndpoint)
        => SelectionChangeTouchesRange(tile.CoveredStart, tile.CoveredEnd,
            tile.Complete, oldEndpoint, newEndpoint);

    internal static bool SelectionChangeTouchesRange(TextPointer? coveredStart,
        TextPointer? coveredEnd, bool complete, TextPointer oldEndpoint,
        TextPointer newEndpoint)
    {
        if (oldEndpoint.CompareTo(newEndpoint) == 0)
            return false;
        if (!complete || coveredStart is null || coveredEnd is null)
            return true;

        var first = oldEndpoint.CompareTo(newEndpoint) < 0 ? oldEndpoint : newEndpoint;
        var last = ReferenceEquals(first, oldEndpoint) ? newEndpoint : oldEndpoint;
        return first.CompareTo(coveredEnd) <= 0
            && last.CompareTo(coveredStart) >= 0;
    }

    private int BuildSelectionTile(HighlightTile tile, TextPointer selectionStart,
        TextPointer selectionEnd, long started)
    {
        var viewer = _contentScrollViewer!;
        var visible = IsHighlightTileVisible(tile);
        if (!tile.Initialized)
        {
            tile.Initialized = true;
            tile.ValidatedInViewport = visible;
            tile.ValidatedTop = Math.Max(tile.Index * HighlightTileHeight, viewer.VerticalOffset);
            tile.ValidatedBottom = Math.Min((tile.Index + 1) * HighlightTileHeight,
                viewer.VerticalOffset + ContentTextBox.ActualHeight);
            var hit = ContentTextBox.GetPositionFromPoint(
                new Point(SelectionHitX(selectionStart), tile.Index * HighlightTileHeight - viewer.VerticalOffset - 1),
                snapToText: true);
            var line = hit?.GetLineStartPosition(0) ?? hit;
            tile.NextLine = line?.GetLineStartPosition(-1) ?? line;
            tile.CoveredStart = tile.NextLine;
        }
        else if (!visible)
        {
            tile.ValidatedInViewport = false;
        }

        var painted = 0;
        var processed = 0;
        while (tile.NextLine is { } line && processed++ < 64)
        {
            if (processed > 1 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= HighlightBuildBudgetMs)
                return painted;

            if (line.CompareTo(selectionEnd) >= 0)
            {
                tile.CoveredEnd = line;
                break;
            }

            var lineRect = SafeCharacterRect(line, LogicalDirection.Forward);
            var paragraphHeight = line.Paragraph?.LineHeight ?? double.NaN;
            var reach = double.IsFinite(paragraphHeight) && paragraphHeight > 0
                ? Math.Max(HighlightTileHeight, paragraphHeight)
                : HighlightTileHeight;
            if (!lineRect.IsEmpty
                && lineRect.Top + viewer.VerticalOffset
                    >= (tile.Index + 1) * HighlightTileHeight + reach)
            {
                tile.CoveredEnd = line;
                break;
            }

            var (next, coveredEnd) = GetSelectionLineCoverage(line, ContentTextBox.Document);
            tile.CoveredEnd = coveredEnd;
            if (line.CompareTo(selectionStart) >= 0 || next is null
                || next.CompareTo(selectionStart) > 0)
            {
                var drawStart = line.CompareTo(selectionStart) > 0 ? line : selectionStart;
                var drawEnd = next is not null && next.CompareTo(selectionEnd) < 0 ? next : selectionEnd;
                if (drawStart.CompareTo(drawEnd) < 0)
                {
                    if (!TryGetBlueSelectionLineGeometry(tile, line, drawStart, drawEnd, out var geometry)) return painted;
                    if (!geometry.IsEmpty() && geometry.Bounds.Top + viewer.VerticalOffset
                        >= (tile.Index + 1) * HighlightTileHeight)
                        break;
                    AppendSelectionGeometry(tile, geometry);
                    painted++;
                }
            }

            if (next is null || next.CompareTo(line) <= 0)
                break;
            tile.NextLine = next;
        }

        if (processed >= 64 && tile.NextLine is not null
            && tile.NextLine.CompareTo(selectionEnd) < 0)
            return painted;

        tile.Complete = true;
        return painted;
    }

    internal static (TextPointer? NextLine, TextPointer CoverageEnd) GetSelectionLineCoverage(
        TextPointer line, FlowDocument document)
    {
        var next = line.GetLineStartPosition(1);
        // The final visual line still covers text after its start. Without a next
        // line, using the start as the end makes later drags miss this cached tile.
        return (next, next ?? document.ContentEnd);
    }

    private double SelectionHitX(TextPointer start)
    {
        if (EditorTables.Ancestor<TableCell>(start.Parent) is not { } cell) return 8;
        var bounds = WpfEditorGeometry.For(ContentTextBox).ElementBounds(cell);
        return bounds.IsEmpty ? 8 : bounds.Left + cell.Padding.Left + cell.BorderThickness.Left + 1;
    }

    private bool TryGetBlueSelectionLineGeometry(HighlightTile tile, TextPointer line,
        TextPointer start, TextPointer end, out Geometry geometry)
    {
        var viewer = _contentScrollViewer!;
        var visible = IsHighlightTileVisible(tile);
        if (!_selectionLineRects.TryGetValue(line, out var measured)
            || (visible && !measured.ValidatedInViewport))
        {
            if (!WpfEditorGeometry.For(ContentTextBox).TrySelection(start, end, out var viewportGeometry))
            {
                geometry = Geometry.Empty;
                return false;
            }
            var bounds = viewportGeometry.Bounds;
            var fullyVisible = visible && !bounds.IsEmpty
                && bounds.Top > ContentTextBox.Padding.Top + 0.5
                && bounds.Bottom < ContentTextBox.ActualHeight - ContentTextBox.Padding.Bottom - 0.5;
            measured = new BlueLineMeasure(WpfEditorGeometry.Transform(viewportGeometry,
                new Matrix(1, 0, 0, 1, viewer.HorizontalOffset, viewer.VerticalOffset)), fullyVisible);
            _selectionLineRects[line] = measured;
        }

        geometry = WpfEditorGeometry.Transform(measured.DocumentGeometry,
            new Matrix(1, 0, 0, 1, -viewer.HorizontalOffset, -viewer.VerticalOffset));
        return true;
    }

    private void AppendSelectionGeometry(HighlightTile tile, Geometry geometry)
    {
        if (geometry.IsEmpty() || _contentScrollViewer is null) return;
        var local = WpfEditorGeometry.Transform(geometry, new Matrix(1, 0, 0, 1,
            _contentScrollViewer.HorizontalOffset - tile.Left,
            _contentScrollViewer.VerticalOffset - tile.Index * HighlightTileHeight));
        var clipped = Geometry.Combine(local, new RectangleGeometry(new Rect(0, 0, tile.Width, HighlightTileHeight)),
            GeometryCombineMode.Intersect, null);
        if (!clipped.IsEmpty()) { clipped.Freeze(); tile.SelectionGeometry.Add(clipped); }
    }

    private bool PaintFindTiles()
    {
        if (!_findBarOpen || !_findOverlayIndexReady || _findOverlaySpans.Length == 0)
        {
            InvalidateFindTiles();
            return true;
        }

        if (_contentScrollViewer is null || FindHighlightOverlay is null
            || ContentTextBox.ActualWidth <= 1 || ContentTextBox.ActualHeight <= 1)
            return false;

        ApplyFindOverlayCommit();
        EnsureHighlightPaths();
        _findNormalHighlightPath!.Fill = TryFindResource("EditorFindMatchBackground") as Brush
            ?? new SolidColorBrush(Color.FromRgb(254, 243, 199));
        _findActiveHighlightPath!.Fill = TryFindResource("EditorFindActiveMatchBackground") as Brush
            ?? new SolidColorBrush(Color.FromRgb(251, 191, 36));
        var started = Stopwatch.GetTimestamp();
        var normalTiles = PrepareHighlightTiles(_findTiles, out var normalChanged);
        var (composedFirst, composedLast) = FindComposedTileRange();
        var compositionChanged = composedFirst != _findComposedFirstTile
            || composedLast != _findComposedLastTile;
        _findComposedFirstTile = composedFirst;
        _findComposedLastTile = composedLast;
        normalChanged |= compositionChanged;
        List<HighlightTile> activeTiles;
        var activeChanged = false;
        if (_findActiveSpan.Length == 0)
        {
            if (_findActiveTiles.Count > 0)
                InvalidateFindActiveTiles();
            activeTiles = new List<HighlightTile>();
        }
        else
        {
            activeTiles = PrepareHighlightTiles(_findActiveTiles, out activeChanged);
            activeChanged |= compositionChanged;
            if (double.IsFinite(_findActiveTop) && double.IsFinite(_findActiveBottom))
            {
                foreach (var tile in activeTiles)
                {
                    if ((tile.Index + 1) * HighlightTileHeight < _findActiveTop
                        || tile.Index * HighlightTileHeight > _findActiveBottom)
                        tile.Complete = true;
                }
            }
        }
        var matches = 0;
        var built = 0;
        var visited = false;
        var work = activeTiles.Where(IsHighlightTileVisible).Select(tile => (Tile: tile, Active: true))
            .Concat(normalTiles.Where(IsHighlightTileVisible).Select(tile => (Tile: tile, Active: false)))
            .Concat(normalTiles.Where(tile => !IsHighlightTileVisible(tile)).Select(tile => (Tile: tile, Active: false)))
            .Concat(activeTiles.Where(tile => !IsHighlightTileVisible(tile)).Select(tile => (Tile: tile, Active: true)));
        foreach (var (tile, active) in work)
        {
            if (tile.Complete)
                continue;

            if (visited && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= HighlightBuildBudgetMs)
                break;

            visited = true;
            built++;
            matches += BuildFindTile(tile, active ? _findActiveSpan : _findOverlaySpans, started);
            if (PublishHighlightTile(tile)
                && tile.Index >= _findComposedFirstTile
                && tile.Index <= _findComposedLastTile)
            {
                if (active)
                    activeChanged = true;
                else
                    normalChanged = true;
            }
        }

        if (normalChanged)
            ComposeFindNormalTiles();
        if (activeChanged)
            ComposeFindActiveTiles();

        if (ScrollPerfDiag.Enabled)
            ScrollPerfDiag.LogEditorFindPaint(Stopwatch.GetElapsedTime(started).TotalMilliseconds, matches, built);
        if (normalTiles.Any(tile => !tile.Complete) || activeTiles.Any(tile => !tile.Complete))
            QueueFindOverlay(System.Windows.Threading.DispatcherPriority.Background);
        return true;
    }

    private int BuildFindTile(HighlightTile tile, MatchSpan[] spans, long started)
    {
        if (!tile.Initialized)
            InitializeFindTile(tile);

        var processed = 0;
        var steps = 0;
        while (steps < 512)
        {
            if (steps > 0 && Stopwatch.GetElapsedTime(started).TotalMilliseconds >= HighlightBuildBudgetMs)
                return processed;

            if (tile.PendingFindDraw is not null)
            {
                AdvanceFindDraw(tile);
                steps++;
                continue;
            }

            var range = tile.CurrentFindRange;
            if (range is null || range.NextMatch >= range.EndMatch)
            {
                tile.CurrentFindRange = null;
                if (tile.NextLine is null)
                {
                    tile.Complete = true;
                    return processed;
                }

                AdvanceFindTileLine(tile, spans);
                steps++;
                continue;
            }

            var first = range.NextMatch;
            // The active result is a separate layer, so all touching normal hits
            // can share one measured rectangle.
            var (last, end) = FindHighlightSlices.ContiguousGroup(
                spans, first, range.EndMatch, activeIndex: -1);

            var sliceStart = Math.Max(spans[first].Start, range.Start);
            var sliceEnd = Math.Min(end, range.End);
            if (sliceEnd > sliceStart)
            {
                tile.PendingFindDraw = new FindDrawCursor
                {
                    StartOffset = sliceStart,
                    EndOffset = sliceEnd,
                    RunIndex = FirstRunEndingAfter(_findOverlayIndex.Runs, sliceStart)
                };
            }

            range.NextMatch = last;
            processed += last - first;
            steps++;
        }

        return processed;
    }

    private void InitializeFindTile(HighlightTile tile)
    {
        tile.Initialized = true;
        var viewer = _contentScrollViewer!;
        var hit = ContentTextBox.GetPositionFromPoint(new Point(
            tile.Left - viewer.HorizontalOffset,
            tile.Index * HighlightTileHeight - viewer.VerticalOffset - 1), snapToText: true);
        var line = hit?.GetLineStartPosition(0) ?? hit;
        tile.NextLine = line?.GetLineStartPosition(-1) ?? line;
    }

    private void AdvanceFindTileLine(HighlightTile tile, MatchSpan[] spans)
    {
        var viewer = _contentScrollViewer!;
        var line = tile.NextLine!;
        var next = line.GetLineStartPosition(1);
        tile.NextLine = next is not null && next.CompareTo(line) > 0 ? next : null;

        var rect = SafeCharacterRect(line, LogicalDirection.Forward);
        if (rect.IsEmpty || rect.Height <= 0)
            return;

        var top = tile.Index * HighlightTileHeight;
        var bottom = top + HighlightTileHeight;
        var paragraphHeight = line.Paragraph?.LineHeight ?? double.NaN;
        var reach = double.IsFinite(paragraphHeight) && paragraphHeight > 0
            ? Math.Max(rect.Height, paragraphHeight) : rect.Height;
        if (rect.Top + viewer.VerticalOffset >= bottom + reach)
        {
            tile.NextLine = null;
            return;
        }
        if (rect.Bottom + viewer.VerticalOffset < top - reach)
            return;

        // A vertical tile can contain extremely wide unwrapped lines. Search only
        // the horizontal slice of each visual line that this tile can draw.
        var y = rect.Top + rect.Height * 0.5;
        if (!FindHighlightSlices.TryGetHorizontalLineSlice(ContentTextBox,
            tile.Left - viewer.HorizontalOffset - 1,
            tile.Left + tile.Width - viewer.HorizontalOffset + 1,
            y, out var left, out var right))
            return;

        var runs = _findOverlayIndex.Runs;
        var start = GetTextOffsetForPointer(runs, left);
        var end = GetTextOffsetForPointer(runs, right);
        if (end <= start)
            return;

        var (first, count) = FindHighlightSlices.OverlappingRange(spans, start, end);
        if (count == 0)
            return;

        tile.CurrentFindRange = new FindLineRange
        {
            Start = start,
            End = end,
            NextMatch = first,
            EndMatch = first + count
        };
    }

    private void AdvanceFindDraw(HighlightTile tile)
    {
        var cursor = tile.PendingFindDraw!;
        var runs = _findOverlayIndex.Runs;
        if (cursor.Position is null)
        {
            if (cursor.RunIndex >= runs.Count || runs[cursor.RunIndex].Start >= cursor.EndOffset)
            {
                tile.PendingFindDraw = null;
                return;
            }

            var run = runs[cursor.RunIndex];
            var sliceStart = Math.Max(cursor.StartOffset, run.Start) - run.Start;
            var sliceEnd = Math.Min(cursor.EndOffset, run.Start + run.Length) - run.Start;
            if (sliceEnd <= sliceStart
                || !FindHighlightSlices.TryGetSlice(run.Pointer, sliceStart,
                    sliceEnd - sliceStart, out var start, out var end))
            {
                cursor.RunIndex++;
                return;
            }

            cursor.Position = start;
            cursor.RunEnd = end;
        }

        var position = cursor.Position;
        var nextLine = position.GetLineStartPosition(1);
        var segmentEnd = nextLine is null || nextLine.CompareTo(cursor.RunEnd) >= 0
            ? cursor.RunEnd! : nextLine;
        var rect = SelectionLineRect(position, segmentEnd);
        AppendHighlightRect(tile, tile.NormalRects, rect);
        if ((!rect.IsEmpty && rect.Top + _contentScrollViewer!.VerticalOffset
            >= (tile.Index + 1) * HighlightTileHeight)
            || nextLine is null || nextLine.CompareTo(position) <= 0
            || nextLine.CompareTo(cursor.RunEnd) >= 0)
        {
            cursor.Position = null;
            cursor.RunEnd = null;
            cursor.RunIndex++;
        }
        else
        {
            cursor.Position = nextLine;
        }
    }

    private void AppendHighlightRect(HighlightTile tile, List<Rect> destination, Rect rect)
    {
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0 || _contentScrollViewer is null)
            return;

        rect.Offset(_contentScrollViewer.HorizontalOffset - tile.Left,
            _contentScrollViewer.VerticalOffset - tile.Index * HighlightTileHeight);
        rect.Intersect(new Rect(0, 0, tile.Width, HighlightTileHeight));
        if (rect.IsEmpty || rect.Width <= 0 || rect.Height <= 0)
            return;

        if (destination.Count > 0)
        {
            var lastIndex = destination.Count - 1;
            var prior = destination[lastIndex];
            if (Math.Abs(prior.Top - rect.Top) <= 0.5
                && Math.Abs(prior.Height - rect.Height) <= 0.5
                && rect.Left <= prior.Right + 0.25 && rect.Right >= prior.Left - 0.25)
            {
                destination[lastIndex] = Rect.Union(prior, rect);
                return;
            }
        }

        destination.Add(rect);
    }

    private static bool PublishHighlightTile(HighlightTile tile)
    {
        var changed = false;
        if (tile.SelectionGeometry.Count > 0)
        {
            foreach (var geometry in tile.SelectionGeometry) tile.NormalGeometry.Children.Add(geometry);
            tile.SelectionGeometry.Clear();
            changed = true;
        }
        if (tile.NormalRects.Count > 0)
        {
            tile.NormalGeometry.Children.Add(BuildHighlightGeometry(tile.NormalRects));
            tile.NormalRects.Clear();
            changed = true;
        }
        if (tile.Complete && tile.NormalGeometry.CanFreeze)
            tile.NormalGeometry.Freeze();
        return changed;
    }

    private static Geometry BuildHighlightGeometry(List<Rect> rects)
    {
        if (rects.Count == 0)
            return Geometry.Empty;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
            foreach (var rect in rects)
                AddFindMarkRect(context, rect);
        geometry.Freeze();
        return geometry;
    }
}
