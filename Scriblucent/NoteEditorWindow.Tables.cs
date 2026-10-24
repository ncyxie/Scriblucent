using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent;

public partial class NoteEditorWindow
{
    private readonly EditorTableLayout _tableLayout = new();
    private EditorPasteMode? _pasteModeOverride;
    private DispatcherOperation? _tableLayoutOperation;
    private Table? _tableEditTarget;
    private bool _tableDeleteRequested;
    private (Table Table, int Row, int Column)? _tableEditFocus;
    private Table? _hoverTable;
    private int _hoverDivider;
    private Point _tablePointer;
    private bool? _tableToolsContext;
    private Table? _resizeTable;
    private int _resizeDivider;
    private Point _resizeStart;
    private double[]? _resizeOriginal;
    private double[]? _resizePreview;
    private System.Windows.Shapes.Path? _tableSelectionPath;
    private bool _tableGeometryPending;
    private FlowDocument? _tableSnapshotDocument;
    private Table[] _editorTables = [];
    private bool _tableStructureDirty = true;

    private double TableViewportWidth => Math.Max(40, ContentTextBox.ActualWidth
        - ContentTextBox.Padding.Left - ContentTextBox.Padding.Right - 10);

    private void InitializeTableEditing()
    {
        ContentTextBox.PreviewMouseMove += TableEditor_MouseMove;
        ContentTextBox.PreviewMouseLeftButtonDown += TableEditor_MouseDown;
        ContentTextBox.PreviewMouseLeftButtonUp += TableEditor_MouseUp;
        ContentTextBox.MouseLeave += (_, _) =>
        {
            if (_resizeTable is null) { _hoverTable = null; _hoverDivider = 0; ContentTextBox.ClearValue(CursorProperty); RefreshTableChrome(); }
        };
        ContentTextBox.LostMouseCapture += (_, _) => CancelTableResize();
        ContentTextBox.LayoutUpdated += (_, _) =>
        {
            if (!_tableGeometryPending) return;
            _tableGeometryPending = false;
            InvalidateSelectionTiles();
            QueueSelectionOverlay();
            RefreshTableChrome();
        };
        ContentTextBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Undo,
            (_, e) => { TableAwareUndo(false); e.Handled = true; },
            (_, e) => { e.CanExecute = ContentTextBox.CanUndo; e.Handled = true; }));
        ContentTextBox.CommandBindings.Add(new CommandBinding(ApplicationCommands.Redo,
            (_, e) => { TableAwareUndo(true); e.Handled = true; },
            (_, e) => { e.CanExecute = ContentTextBox.CanRedo; e.Handled = true; }));
        if (_defaultContentTextBoxContextMenu is { } defaultMenu)
        {
            var keepFormatting = CreatePasteModeMenuItem("PasteKeepFormatting", EditorPasteMode.KeepFormatting);
            var pasteAsText = CreatePasteModeMenuItem("PasteAsText", EditorPasteMode.TextOnly);
            var pasteAsPhoto = CreatePasteAsPhotoMenuItem();
            var pasteAsTable = CreatePasteAsTableMenuItem();
            keepFormatting.Visibility = Visibility.Collapsed;
            pasteAsText.Visibility = Visibility.Collapsed;
            pasteAsPhoto.Visibility = Visibility.Collapsed;
            pasteAsTable.Visibility = Visibility.Collapsed;
            var pasteIndex = defaultMenu.Items.Count;
            for (var i = 0; i < defaultMenu.Items.Count; i++)
            {
                if (defaultMenu.Items[i] is MenuItem { Command: { } command } && command == ApplicationCommands.Paste)
                {
                    pasteIndex = i + 1;
                    break;
                }
            }
            defaultMenu.Items.Insert(pasteIndex, keepFormatting);
            defaultMenu.Items.Insert(pasteIndex + 1, pasteAsText);
            defaultMenu.Items.Insert(pasteIndex + 2, pasteAsPhoto);
            defaultMenu.Items.Insert(pasteIndex + 3, pasteAsTable);
            defaultMenu.Opened += (_, _) => SyncPasteOffers(defaultMenu);
        }
    }

    private void SyncPasteOffers(ItemsControl? menu)
    {
        if (menu is null) return;
        var formatted = ClipboardOffersTextPasteChoices();
        var photo = ClipboardIsPhoto();
        var table = ClipboardHasTableGrid();
        foreach (var item in menu.Items)
        {
            if (item is not FrameworkElement element)
                continue;
            element.Visibility = element.Tag switch
            {
                "PasteKeepFormatting" or "PasteAsText" => formatted ? Visibility.Visible : Visibility.Collapsed,
                "PasteAsPhoto" => photo ? Visibility.Visible : Visibility.Collapsed,
                "TablePasteAsTable" => table ? Visibility.Visible : Visibility.Collapsed,
                _ => element.Visibility
            };
        }
    }

    private void PasteWithMode(EditorPasteMode mode)
    {
        _pasteModeOverride = mode;
        try
        {
            ApplicationCommands.Paste.Execute(null, ContentTextBox);
        }
        finally
        {
            _pasteModeOverride = null;
        }
    }

    private MenuItem CreatePasteModeMenuItem(string localizationKey, EditorPasteMode mode)
    {
        var item = new MenuItem
        {
            Header = LocalizationService.GetString(localizationKey),
            Tag = localizationKey
        };
        item.Click += (_, _) => Dispatcher.InvokeAsync(() => PasteWithMode(mode), DispatcherPriority.Input);
        return item;
    }

    private MenuItem CreatePasteAsPhotoMenuItem()
    {
        var item = new MenuItem
        {
            Header = LocalizationService.GetString("PasteAsPhoto"),
            Tag = "PasteAsPhoto"
        };
        item.Click += (_, _) => Dispatcher.InvokeAsync(
            () => ApplicationCommands.Paste.Execute(null, ContentTextBox),
            DispatcherPriority.Input);
        return item;
    }

    private static bool ClipboardOffersTextPasteChoices()
    {
        try { return Clipboard.GetDataObject() is { } data && EditorPaste.OffersTextPasteChoices(data); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    private static bool ClipboardIsPhoto()
    {
        try { return Clipboard.GetDataObject() is { } data && EditorPaste.IsPhoto(data); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    private Table[] EditorTableSnapshot()
    {
        if (_tableStructureDirty || !ReferenceEquals(_tableSnapshotDocument, ContentTextBox.Document))
        {
            _tableSnapshotDocument = ContentTextBox.Document;
            _editorTables = EditorTables.Enumerate(ContentTextBox.Document.Blocks).ToArray();
            _tableStructureDirty = false;
        }
        return _editorTables;
    }

    private void TableEditor_TextChanged(TextChangedEventArgs e)
    {
        // Table structure has multiple text symbols. Ordinary single-character typing
        // cannot add or remove a table, and should not scan a long note on every key.
        if (e.UndoAction is UndoAction.Undo or UndoAction.Redo
            || e.Changes.Any(change => change.AddedLength > 1 || change.RemovedLength > 1))
            _tableStructureDirty = true;
        ScheduleTableLayout();
    }

    private void ScheduleTableLayout()
    {
        if (_isDisposed || _isSerializingEditorContent || _resizeTable is not null
            || _tableLayoutOperation is { Status: DispatcherOperationStatus.Pending }) return;
        _tableLayoutOperation = Dispatcher.InvokeAsync(() =>
        {
            if (_isDisposed || _resizeTable is not null) return;
            if (_tableLayout.Apply(ContentTextBox.Document, TableViewportWidth, EditorTableSnapshot()))
            {
                InvalidateSelectionTiles();
                InvalidateFindTiles();
                QueueSelectionOverlay();
                if (_findBarOpen) QueueFindOverlay(DispatcherPriority.Render);
                _wrapLayout?.ScheduleApply();
            }
            _tableGeometryPending = true;
            UpdateTableToolsState();
            RefreshTableChrome();
        }, DispatcherPriority.Loaded);
    }

    private Table? ActiveTable()
        => EditorTables.Ancestor<Table>((_heldSelectionStart ?? ContentTextBox.Selection.Start).Parent);

    private void UpdateTableToolsState()
    {
        if (InsertTableButton is null) return;
        var inside = ActiveTable() is not null;
        if (_tableToolsContext == inside) return;
        _tableToolsContext = inside;
        InsertTableButton.SetBinding(ToolTipProperty, new System.Windows.Data.Binding(
            inside ? "[TableToolsHint]" : "[InsertTable]") { Source = FindResource("Loc") });
    }

    private void OpenTableTools()
    {
        MaterializeSelectionForAction();
        var table = ActiveTable();
        if (table is null) return;
        var cell = FindAncestorTableCell(ContentTextBox.Selection.Start)
            ?? EditorTables.Rows(table).FirstOrDefault()?.Cells.FirstOrDefault();
        if (cell is null) return;
        var menu = BuildTableContextMenu(cell);
        menu.PlacementTarget = InsertTableButton;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void RunTableEdit(Table table, Action action, Action? afterAttach = null, bool preserveView = false)
    {
        if (EditorTables.ParentBlocks(table) is null) return;
        MaterializeSelectionForAction();
        var view = _floatingImages.CaptureViewState();
        var hasSelection = EditorTables.TryGetRange(ContentTextBox.Selection.Start, ContentTextBox.Selection.End,
            out var selectionTable, out var selectedCells) && ReferenceEquals(selectionTable, table);
        var caretAtStart = ContentTextBox.CaretPosition.CompareTo(ContentTextBox.Selection.Start) == 0;
        var textStart = CaptureTableEndpoint(table, ContentTextBox.Selection.Start);
        var textEnd = CaptureTableEndpoint(table, ContentTextBox.Selection.End);
        var initialRow = 0; var initialColumn = 0;
        int? caretOffset = null;
        if (FindAncestorTableCell(ContentTextBox.CaretPosition) is { } caretCell)
            if (TryGetTableCellIndices(table, caretCell, out initialRow, out initialColumn))
                caretOffset = caretCell.ContentStart.GetOffsetToPosition(ContentTextBox.CaretPosition);
        SuspendEditorEdgeSpacing();
        _wrapLayout?.Clear();
        _tableLayout.RestorePreferred(table);
        _tableEditTarget = table;
        _tableDeleteRequested = false;
        _tableEditFocus = null;
        try
        {
            EditorTables.Edit(ContentTextBox, table, action, () => _tableDeleteRequested, afterAttach);
            _floatingImages.ReconcileInlineElementsAfterTableEdit();
            _tableLayout.Forget(table);
        }
        finally
        {
            _tableEditTarget = null;
            ApplyEditorEdgeSpacing();
            _tableLayout.Apply(ContentTextBox.Document, TableViewportWidth);
            _wrapLayout?.Reapply();
            _floatingImages.RestoreViewState(view);
        }
        if (_tableEditFocus is { } focus && table.Parent is not null)
            FocusTableCell(focus.Table, focus.Row, focus.Column);
        else if (hasSelection && table.Parent is not null && EditorTables.IsRectangular(table))
        {
            var rows = EditorTables.Rows(table);
            if (selectedCells.LastRow < rows.Length && selectedCells.LastColumn < table.Columns.Count)
                ContentTextBox.Selection.Select(rows[selectedCells.FirstRow].Cells[selectedCells.FirstColumn].ContentStart,
                    rows[selectedCells.LastRow].Cells[selectedCells.LastColumn].ContentEnd);
        }
        else if (table.Parent is not null)
            FocusTableCell(table, initialRow, initialColumn);
        if (preserveView && table.Parent is not null)
        {
            var caret = view.Caret;
            if (caretOffset is { } offset && TryGetTableCell(table, initialRow, initialColumn, out var originalCell))
                caret = originalCell.ContentStart.GetPositionAtOffset(Math.Min(offset,
                    originalCell.ContentStart.GetOffsetToPosition(originalCell.ContentEnd)), LogicalDirection.Forward);
            _floatingImages.RestoreViewState((view.ScrollOffset, caret));
            if (hasSelection)
            {
                var rows = EditorTables.Rows(table);
                if (selectedCells.LastRow < rows.Length && selectedCells.LastColumn < table.Columns.Count)
                {
                    var start = rows[selectedCells.FirstRow].Cells[selectedCells.FirstColumn].ContentStart;
                    var end = rows[selectedCells.LastRow].Cells[selectedCells.LastColumn].ContentEnd;
                    ReplaceEditorSelection(caretAtStart ? end : start, caretAtStart ? start : end);
                }
            }
            else if (RestoreTableEndpoint(table, textStart) is { } start && RestoreTableEndpoint(table, textEnd) is { } end)
            {
                ReplaceEditorSelection(caretAtStart ? end : start, caretAtStart ? start : end);
            }
        }
        MarkEditorContentChanged();
        UpdateEditedIndicator();
        ContentTextBox.Focus();
        ScheduleTableLayout();
    }

    private static (int Row, int Column, int Offset)? CaptureTableEndpoint(Table table, TextPointer position)
        => FindAncestorTableCell(position) is { } cell && TryGetTableCellIndices(table, cell, out var row, out var column)
            ? (row, column, cell.ContentStart.GetOffsetToPosition(position)) : null;

    private static TextPointer? RestoreTableEndpoint(Table table, (int Row, int Column, int Offset)? endpoint)
        => endpoint is { } value && TryGetTableCell(table, value.Row, value.Column, out var cell)
            ? cell.ContentStart.GetPositionAtOffset(Math.Clamp(value.Offset, 0,
                cell.ContentStart.GetOffsetToPosition(cell.ContentEnd)), LogicalDirection.Forward) : null;

    private void TableAwareUndo(bool redo)
    {
        CancelTableResize();
        MaterializeSelectionForAction();
        _tableLayout.RestorePreferred(ContentTextBox.Document);
        if (redo) { if (ContentTextBox.CanRedo) ContentTextBox.Redo(); }
        else if (ContentTextBox.CanUndo) ContentTextBox.Undo();
        _floatingImages.ReconcileInlineElementsAfterTableEdit();
        _tableLayout.Clear();
        _tableLayout.Apply(ContentTextBox.Document, TableViewportWidth);
        ScheduleTableLayout();
    }

    private MenuItem CreatePasteAsTableMenuItem()
    {
        var item = new MenuItem { Header = LocalizationService.GetString("TablePasteAsTable"), Tag = "TablePasteAsTable" };
        item.Click += (_, _) => Dispatcher.InvokeAsync(() =>
        {
            if (TryGetClipboardTableGrid(out var grid))
            {
                MaterializeSelectionForAction();
                var table = EditorTables.Create(grid.Length, grid[0].Length, ResolveTableCellBorderBrush());
                EditorTables.PasteGrid(table, 0, 0, grid, ResolveTableCellBorderBrush());
                ContentTextBox.BeginChange();
                try { InsertTableAtCaret(table); }
                finally { ContentTextBox.EndChange(); }
                _tableLayout.Apply(ContentTextBox.Document, TableViewportWidth);
                FocusTableCell(table, 0, 0);
                MarkEditorContentChanged();
                UpdateEditedIndicator();
                ContentTextBox.Focus();
                ScheduleTableLayout();
            }
        }, DispatcherPriority.Input);
        return item;
    }

    private static bool ClipboardHasTableGrid()
        => TryGetClipboardTableGrid(out _);

    private static bool TryGetClipboardTableGrid(out string[][] grid)
    {
        grid = [];
        try { return Clipboard.GetDataObject() is { } data && EditorTables.TryReadGrid(data, out grid); }
        catch (System.Runtime.InteropServices.COMException) { return false; }
    }

    private bool TryPasteTableGrid(IDataObject data)
    {
        var cell = FindAncestorTableCell(ContentTextBox.Selection.Start);
        var table = cell is not null ? FindAncestorTable(cell) : null;
        if (table is null || !EditorTables.IsRectangular(table)
            || !TryGetTableCellIndices(table, cell!, out var row, out var column)) return false;
        if (!EditorTables.TryReadGrid(data, out var grid)) return false;
        RunTableEdit(table, () =>
        {
            EditorTables.PasteGrid(table, row, column, grid, ResolveTableCellBorderBrush());
            FocusTableCell(table, row, column);
        }, () =>
        {
            var rows = EditorTables.Rows(table);
            for (var r = row; r < row + grid.Length; r++)
                for (var c = column; c < column + grid[0].Length; c++)
                    foreach (var paragraph in rows[r].Cells[c].Blocks.OfType<Paragraph>())
                        ApplyLineSpacingToParagraph(paragraph, _lineSpacingMultiplier);
        });
        return true;
    }

    private void AddTableLayoutMenu(ContextMenu menu, Table table, TableCell cell, int rowIndex, int columnIndex)
    {
        MenuItem Edit(string key, Action action) => CreateTableMenuItem(key, action, table, preserveView: true);
        var layout = CreateTableSubMenu("TableLayout");
        layout.Items.Add(Edit("TableFitToNote", () =>
        {
            var widths = table.Columns.Select(column => column.Width.Value).ToArray();
            var factor = EditorTableLayout.ColumnSpace(table, TableViewportWidth) / widths.Sum();
            EditorTableLayout.SetWidths(table, widths.Select(width => width * factor).ToArray());
            FocusTableCell(table, rowIndex, columnIndex);
        }));
        layout.Items.Add(Edit("TableDistributeColumns", () =>
        {
            var width = table.Columns.Sum(column => column.Width.Value) / table.Columns.Count;
            EditorTableLayout.SetWidths(table, Enumerable.Repeat(width, table.Columns.Count).ToArray());
            FocusTableCell(table, rowIndex, columnIndex);
        }));
        menu.Items.Add(layout);
        var header = Edit("TableHeaderRow", () =>
        {
            EditorTables.ToggleHeader(table);
            FocusTableCell(table, rowIndex, columnIndex);
        });
        header.IsCheckable = true;
        header.IsChecked = EditorTables.HasHeader(table);
        menu.Items.Add(header);

        var cells = EditorTables.SelectedCells(ContentTextBox.Selection.Start, ContentTextBox.Selection.End, table, cell);
        var shade = CreateTableSubMenu("TableCellShading");
        var palette = new (string Key, Color? Color)[]
        {
            ("TableShadingNone", null), ("TableShadingGray", Color.FromArgb(45, 128, 128, 128)),
            ("TableShadingBlue", Color.FromArgb(55, 80, 140, 230)), ("TableShadingGreen", Color.FromArgb(55, 70, 170, 105)),
            ("TableShadingYellow", Color.FromArgb(55, 220, 175, 50)), ("TableShadingRed", Color.FromArgb(55, 220, 85, 90))
        };
        foreach (var (key, color) in palette)
            shade.Items.Add(Edit(key, () =>
            {
                foreach (var selected in cells)
                {
                    if (color is { } value) selected.Background = new SolidColorBrush(value);
                    else selected.ClearValue(TextElement.BackgroundProperty);
                }
                _tableEditFocus = null;
            }));
        menu.Items.Add(shade);
        menu.Items.Add(new Separator());
    }

    private bool TryHandleTableKey(KeyEventArgs e)
    {
        if (e.Handled || (!ContentTextBox.IsKeyboardFocusWithin && _resizeTable is null)) return false;
        if (e.Key == Key.Escape && _resizeTable is not null)
        {
            CancelTableResize(); e.Handled = true; return true;
        }
        if (e.Key is Key.Delete or Key.Back && Keyboard.Modifiers == ModifierKeys.None)
        {
            MaterializeSelectionForAction();
            if (EditorTables.TryGetRange(ContentTextBox.Selection.Start, ContentTextBox.Selection.End, out var selected, out var cells)
                && selected is not null)
            {
                RunTableEdit(selected, () => EditorTables.Clear(EditorTables.Cells(selected, cells)), preserveView: true);
                e.Handled = true;
                return true;
            }
        }
        if (e.Key != Key.Enter || Keyboard.Modifiers != ModifierKeys.Control) return false;
        var table = ActiveTable();
        if (table is null) return false;
        var blocks = EditorTables.ParentBlocks(table);
        if (blocks is null) return false;
        var paragraph = table.NextBlock as Paragraph;
        if (paragraph is null)
        {
            paragraph = new Paragraph();
            ContentTextBox.BeginChange();
            try
            {
                blocks.InsertAfter(table, paragraph);
                ApplyLineSpacingToParagraph(paragraph, _lineSpacingMultiplier);
            }
            finally { ContentTextBox.EndChange(); }
        }
        ContentTextBox.CaretPosition = paragraph.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        e.Handled = true;
        return true;
    }

    private (Table? Table, int Divider) TableDividerAt(Point point)
    {
        foreach (var table in EditorTableSnapshot().Reverse())
        {
            if (table.Parent is null) continue;
            var divider = EditorTableLayout.HitDivider(table, ContentTextBox, point);
            if (divider > 0) return (table, divider);
        }
        return (null, 0);
    }

    private void TableEditor_MouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(ContentTextBox);
        _tablePointer = point;
        if (_resizeTable is { } table && _resizeOriginal is { } original)
        {
            _resizePreview = EditorTableLayout.Resize(original, _resizeDivider, point.X - _resizeStart.X,
                EditorTableLayout.ColumnSpace(table, TableViewportWidth));
            EditorTableLayout.SetWidths(table, _resizePreview);
            _tableGeometryPending = true;
            RefreshTableChrome();
            e.Handled = true;
            return;
        }
        var (hitTable, divider) = TableDividerAt(point);
        _hoverTable = hitTable;
        _hoverDivider = divider;
        if (hitTable is not null) ContentTextBox.Cursor = Cursors.SizeWE;
        else ContentTextBox.ClearValue(CursorProperty);
        RefreshTableChrome();
    }

    private void TableEditor_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.None) return;
        var point = e.GetPosition(ContentTextBox);
        var (table, divider) = TableDividerAt(point);
        if (table is null) return;
        MaterializeSelectionForAction();
        ContentTextBox.Focus();
        _resizeTable = table;
        _resizeDivider = divider;
        _resizeStart = point;
        _resizeOriginal = table.Columns.Select(column => column.Width.Value).ToArray();
        _resizePreview = (double[])_resizeOriginal.Clone();
        _tablePointer = point;
        if (!ContentTextBox.CaptureMouse()) { CancelTableResize(); return; }
        ContentTextBox.Cursor = Cursors.SizeWE;
        e.Handled = true;
    }

    private void TableEditor_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_resizeTable is not { } table || _resizePreview is not { } desired) return;
        var original = _resizeOriginal!;
        EditorTableLayout.SetWidths(table, original);
        _resizeTable = null;
        _resizeOriginal = _resizePreview = null;
        ContentTextBox.ReleaseMouseCapture();
        ContentTextBox.ClearValue(CursorProperty);
        if (original.Where((width, i) => Math.Abs(width - desired[i]) > 0.1).Any())
        {
            var caretCell = FindAncestorTableCell(ContentTextBox.CaretPosition);
            var row = 0; var column = 0;
            if (caretCell is not null) TryGetTableCellIndices(table, caretCell, out row, out column);
            RunTableEdit(table, () =>
            {
                EditorTableLayout.SetWidths(table, desired);
                FocusTableCell(table, row, column);
            }, preserveView: true);
        }
        RefreshTableChrome();
        e.Handled = true;
    }

    private void CancelTableResize()
    {
        if (_resizeTable is not { } table || _resizeOriginal is not { } widths) return;
        _resizeTable = null;
        _resizeOriginal = _resizePreview = null;
        EditorTableLayout.SetWidths(table, widths);
        _hoverTable = null;
        _hoverDivider = 0;
        if (ContentTextBox.IsMouseCaptured) ContentTextBox.ReleaseMouseCapture();
        ContentTextBox.ClearValue(CursorProperty);
        ScheduleTableLayout();
        RefreshTableChrome();
    }

    private void RefreshTableChrome()
    {
        if (TableResizeOverlay is null) return;
        TableResizeOverlay.Children.Clear();
        var table = _resizeTable ?? _hoverTable;
        if (table is null || table.Parent is null) return;
        var bounds = EditorTableLayout.Bounds(table, ContentTextBox);
        if (bounds.IsEmpty || bounds.Bottom < 0 || bounds.Top > ContentTextBox.ActualHeight) return;
        var divider = _resizeTable is null ? _hoverDivider : _resizeDivider;
        var x = EditorTableLayout.DividerPosition(table, ContentTextBox, divider);
        if (!double.IsFinite(x)) return;
        var brush = TryFindResource("AccentRimBrush") as Brush ?? Brushes.Gray;
        var top = Math.Max(ContentTextBox.Padding.Top, bounds.Top);
        var bottom = Math.Min(ContentTextBox.ActualHeight - ContentTextBox.Padding.Bottom, bounds.Bottom);
        if (bottom <= top) return;
        TableResizeOverlay.Children.Add(new Line
        {
            X1 = x, X2 = x, Y1 = top, Y2 = bottom,
            Stroke = brush, StrokeThickness = 1, Opacity = _resizeTable is null ? 0.5 : 0.8
        });
        var height = Math.Min(18, bottom - top);
        var grip = new Rectangle { Width = 4, Height = height, RadiusX = 2, RadiusY = 2, Fill = brush };
        Canvas.SetLeft(grip, x - 2);
        Canvas.SetTop(grip, Math.Clamp(_tablePointer.Y - height / 2, top, bottom - height));
        TableResizeOverlay.Children.Add(grip);
    }

    private bool TryPaintTableSelection(TextPointer start, TextPointer end)
    {
        if (_tableSelectionPath is not null) _tableSelectionPath.Data = Geometry.Empty;
        if (!EditorTables.TryGetRange(start, end, out var table, out var range) || table is null) return false;
        _tableSelectionPath ??= new System.Windows.Shapes.Path { IsHitTestVisible = false };
        if (!TableSelectionOverlay.Children.Contains(_tableSelectionPath)) TableSelectionOverlay.Children.Add(_tableSelectionPath);
        var geometry = new GeometryGroup { FillRule = FillRule.Nonzero };
        var rows = EditorTables.Rows(table);
        var measurement = WpfEditorGeometry.For(ContentTextBox);
        var clip = new Rect(ContentTextBox.Padding.Left, ContentTextBox.Padding.Top,
            Math.Max(0, ContentTextBox.ActualWidth - ContentTextBox.Padding.Left - ContentTextBox.Padding.Right),
            Math.Max(0, ContentTextBox.ActualHeight - ContentTextBox.Padding.Top - ContentTextBox.Padding.Bottom));
        for (var row = range.FirstRow; row <= range.LastRow; row++)
        {
            var rowBounds = measurement.ElementBounds(rows[row].Cells[range.FirstColumn]);
            if (rowBounds.IsEmpty || rowBounds.Bottom <= clip.Top || rowBounds.Top >= clip.Bottom) continue;
            for (var column = range.FirstColumn; column <= range.LastColumn; column++)
            {
                var bounds = measurement.ElementBounds(rows[row].Cells[column]);
                bounds.Intersect(clip);
                if (!bounds.IsEmpty) geometry.Children.Add(new RectangleGeometry(bounds));
            }
        }
        _tableSelectionPath.Fill = CreateSelectionOverlayBrush();
        _tableSelectionPath.Data = geometry;
        if (_selectionHighlightPath is not null) _selectionHighlightPath.Data = Geometry.Empty;
        return true;
    }
}
