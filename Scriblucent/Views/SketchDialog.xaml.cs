using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Icons;
using Scriblucent.Localization;
using Scriblucent.Models;
using Scriblucent.Services;

namespace Scriblucent.Views;

public partial class SketchDialog : Window
{
    private readonly SketchDocument _drawing;
    private readonly SketchHistory _history;
    private readonly OverlayDialogAnimator _overlayAnimator;
    private readonly bool _isExisting;
    private readonly Dictionary<SketchTool, ToggleButton> _toolButtons = new();
    private SketchTool _tool;
    private Color _strokeColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private Color _inkColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private Color _markerColor = AppColorPalette.Ink[7];
    private Button _undoButton = null!, _redoButton = null!, _clearButton = null!;
    private Point? _shapeStart, _panStart;
    private Point _panOffset;
    private bool _initialized, _fit = true, _spaceDown, _finishingGesture, _closing, _allowClose, _updatingZoom, _encoding, _confirmingClose;
    private Popup? _palette, _help;

    public byte[]? SketchPngBytes { get; private set; }
    public int SketchPixelWidth => _drawing.PixelWidth;
    public int SketchPixelHeight => _drawing.PixelHeight;
    public bool UsesTransparentBackground => _drawing.Background is null;
    public bool HasChanges => _history.IsChanged;

    public SketchDialog(byte[]? original = null)
    {
        _isExisting = original is not null;
        _drawing = original is null ? new SketchDocument() : SketchPngCodec.Open(original);
        _inkColor = _drawing.Strokes.LastOrDefault(s => !s.DrawingAttributes.IsHighlighter)?.DrawingAttributes.Color ?? _inkColor;
        _markerColor = _drawing.Strokes.LastOrDefault(s => s.DrawingAttributes.IsHighlighter)?.DrawingAttributes.Color ?? _markerColor;
        _strokeColor = _inkColor;
        _history = new SketchHistory(_drawing);
        InitializeComponent();
        _overlayAnimator = SketchShell.BindAnimator(this);
        WindowThemeService.Register(this);
        Paper.Width = SketchCanvas.Width = _drawing.Width;
        Paper.Height = SketchCanvas.Height = _drawing.Height;
        SketchCanvas.Strokes = _drawing.Strokes;
        BaseImage.Source = SketchPngCodec.DecodeBase(_drawing);
        TransparencyChecker.Background = CreateCheckerBrush();
        BuildToolbar();
        if (_isExisting)
        {
            HeadingText.Text = LocalizationService.GetString("SketchEditTitle");
            Title = HeadingText.Text;
            AcceptButton.Content = LocalizationService.GetString("SketchApplyChanges");
        }
        ZoomBox.ItemsSource = new[] { "25%", "50%", "75%", "100%", "150%", "200%", "300%", "400%" };
        _initialized = true;
        ApplyBackground(); SelectTool(SketchTool.Pen); UpdateHistoryButtons();
        Loaded += (_, _) =>
        {
            OverlayDialogBoundsHelper.Apply(this); UiMotion.ConfigurePopupsForWindow(this);
            ResizeWorkspace(); UpdateCanvasViewportClip();
            _overlayAnimator.BeginOpen();
        };
        SizeChanged += (_, _) => { if (_initialized) ResizeWorkspace(); };
        AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, e) => { if (e.ChangedButton == MouseButton.Left) EndGesture(); }), true);
        SketchCanvas.AddHandler(Stylus.StylusDownEvent, new StylusDownEventHandler((_, _) => _history.Begin()), true);
        SketchCanvas.AddHandler(Mouse.MouseMoveEvent, new MouseEventHandler(Canvas_MouseMove), true);
        SketchCanvas.AddHandler(Stylus.StylusUpEvent, new StylusEventHandler((_, _) => Dispatcher.BeginInvoke(EndGesture, System.Windows.Threading.DispatcherPriority.Input)), true);
        SketchCanvas.StrokeCollected += (_, _) => { _history.Commit(); UpdateHistoryButtons(); };
        SketchCanvas.LostMouseCapture += (_, _) => { if (_shapeStart is not null) EndGesture(); else { _history.Commit(); UpdateHistoryButtons(); } };
        Closed += (_, _) => ClosePopups();
        Deactivated += (_, _) => ResetPan();
        AddHandler(RequestBringIntoViewEvent, new RequestBringIntoViewEventHandler((_, e) => e.Handled = true));
    }

    private void BuildToolbar()
    {
        foreach (var (tool, icon, text) in new[]
        {
            (SketchTool.Pen, "PenLine", "SketchPen"), (SketchTool.Marker, "Highlighter", "SketchMarker"),
            (SketchTool.Eraser, "Eraser", "SketchEraser"), (SketchTool.Line, "Minus", "SketchLine"),
            (SketchTool.Arrow, "ArrowUpRight", "SketchArrow"), (SketchTool.Rectangle, "Square", "SketchRectangle"),
            (SketchTool.Ellipse, "Circle", "SketchEllipse")
        })
        {
            if (tool == SketchTool.Line) AddDivider();
            var button = new ToggleButton
            {
                Style = (Style)FindResource("SketchToolButtonStyle"),
                Content = new AppIcon { LucideKind = Enum.Parse<MahApps.Metro.IconPacks.PackIconLucideKind>(icon), Width = 16, Height = 16 },
                ToolTip = LocalizationService.GetString(text)
            };
            System.Windows.Automation.AutomationProperties.SetName(button, LocalizationService.GetString(text));
            button.Click += (_, _) => SelectTool(tool); _toolButtons.Add(tool, button); ToolsPanel.Children.Add(button);
        }
        AddDivider();
        _undoButton = AddAction(AppIconKind.Undo, "Undo", () => { EndGesture(); _history.Undo(); ApplyBackground(); UpdateHistoryButtons(); });
        _redoButton = AddAction(AppIconKind.Redo, "Redo", () => { EndGesture(); _history.Redo(); ApplyBackground(); UpdateHistoryButtons(); });
        _undoButton.ToolTip = LocalizationService.GetString("Undo") + " (Ctrl+Z)";
        _redoButton.ToolTip = LocalizationService.GetString("Redo") + " (Ctrl+Y)";
        AddDivider();
        _clearButton = AddAction(AppIconKind.Trash, "SketchClearMarks", () =>
        {
            EndGesture(); _history.Begin(); _drawing.Strokes.Clear(); _history.Commit(); UpdateHistoryButtons();
        });
    }

    private void AddDivider() => ToolsPanel.Children.Add(new Border
    {
        Width = 1, Height = 20, Margin = new Thickness(5, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = (Brush)FindResource("ActionDividerBrush"), Opacity = .65
    });

    private Button AddAction(AppIconKind icon, string text, Action action)
    {
        var button = new Button
        {
            Style = (Style)FindResource("EditorToolIconButtonStyle"), Content = new AppIcon { Kind = icon },
            ToolTip = LocalizationService.GetString(text)
        };
        System.Windows.Automation.AutomationProperties.SetName(button, LocalizationService.GetString(text));
        button.Click += (_, _) => action(); ToolsPanel.Children.Add(button); return button;
    }

    private void SelectTool(SketchTool tool)
    {
        CancelShape(); _history.Commit();
        if (_tool == SketchTool.Marker) _markerColor = _strokeColor;
        else if (_tool != SketchTool.Eraser) _inkColor = _strokeColor;
        _tool = tool; _strokeColor = tool == SketchTool.Marker ? _markerColor : _inkColor;
        foreach (var (kind, button) in _toolButtons) button.IsChecked = kind == tool;
        SketchCanvas.EditingMode = tool == SketchTool.Eraser ? InkCanvasEditingMode.EraseByPoint
            : tool is SketchTool.Pen or SketchTool.Marker ? InkCanvasEditingMode.Ink : InkCanvasEditingMode.None;
        ApplyDrawingAttributes();
        UpdateToolCursor();
    }

    private void UpdateToolCursor()
    {
        SketchCanvas.Cursor = _spaceDown ? Cursors.Hand
            : _tool is SketchTool.Pen or SketchTool.Marker or SketchTool.Eraser ? Cursors.None : Cursors.Cross;
        CursorContrast.Visibility = CursorOutline.Visibility = Visibility.Collapsed;
    }

    private void ApplyDrawingAttributes()
    {
        if (!_initialized) return;
        var width = StrokeWidthSlider.Value;
        SketchCanvas.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _strokeColor, FitToCurve = _tool is SketchTool.Pen or SketchTool.Marker,
            IgnorePressure = false, IsHighlighter = _tool == SketchTool.Marker,
            Width = _tool == SketchTool.Marker ? Math.Max(8, width * 2.2) : width,
            Height = _tool == SketchTool.Marker ? Math.Max(14, width * 3.5) : width
        };
        SketchCanvas.EraserShape = new EllipseStylusShape(Math.Max(8, width * 2.5), Math.Max(8, width * 2.5));
        CurrentPenColorPreview.Background = new SolidColorBrush(_strokeColor);
    }

    private void StrokeWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StrokeWidthLabel is null) return;
        StrokeWidthLabel.Text = Math.Round(e.NewValue).ToString(); ApplyDrawingAttributes();
    }

    private void InkColor_Click(object sender, RoutedEventArgs e) => OpenPalette(false);
    private void BackgroundColor_Click(object sender, RoutedEventArgs e) => OpenPalette(true);
    private void OpenPalette(bool background)
    {
        if (_help is not null) _help.IsOpen = false;
        if (_palette is { IsOpen: true }) { _palette.IsOpen = false; return; }
        var panel = new StackPanel { Width = 228 };
        var swatches = new WrapPanel(); panel.Children.Add(swatches);
        void Add(Color? color)
        {
            var button = new ToggleButton
            {
                Style = (Style)FindResource("SketchSwatchStyle"),
                Background = color is { } solid ? new SolidColorBrush(solid) : CreateCheckerBrush(),
                IsChecked = background ? _drawing.Background == color : _strokeColor == color,
                ToolTip = color?.ToString() ?? LocalizationService.GetString("SketchBackgroundTransparent")
            };
            System.Windows.Automation.AutomationProperties.SetName(button, button.ToolTip.ToString());
            button.Click += (_, _) =>
            {
                _palette!.IsOpen = false;
                if (background) SetBackground(color); else SetInkColor(color!.Value);
            };
            swatches.Children.Add(button);
        }
        if (background) Add(null);
        var colors = background ? AppColorPalette.Paper : AppColorPalette.Ink;
        var current = background ? _drawing.Background : _strokeColor;
        if (current is { } customColor && !colors.Contains(customColor)) Add(customColor);
        foreach (var color in colors) Add(color);
        var custom = new Button { Content = LocalizationService.GetString("SelectColor"), Style = (Style)FindResource("DialogSecondaryButtonStyle"), Margin = new Thickness(3, 8, 3, 0) };
        custom.Click += (_, _) =>
        {
            _palette!.IsOpen = false;
            var picker = new ColorPickerDialog { Owner = this, InitialColor = background ? _drawing.Background ?? Colors.White : _strokeColor };
            if (picker.ShowDialog() != true || picker.SelectedColor is not { } color) return;
            if (background) SetBackground(color); else SetInkColor(color);
        };
        panel.Children.Add(custom);
        _palette = CreatePopup(panel, background ? BackgroundColorButton : InkColorButton, PlacementMode.Bottom);
        _palette.IsOpen = true;
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (_help is { IsOpen: true }) { _help.IsOpen = false; return; }
        if (_palette is not null) _palette.IsOpen = false;
        var panel = new StackPanel { Width = 290 };
        var instructions = new TextBlock
        {
            Text = LocalizationService.GetString("SketchInputHint").Replace(" · ", "\n"),
            FontSize = 13, TextWrapping = TextWrapping.Wrap, LineHeight = 23
        };
        instructions.SetResourceReference(TextBlock.ForegroundProperty, "TextColor");
        panel.Children.Add(instructions);
        if (_drawing.BaseImage is not null)
        {
            var originalHint = new TextBlock
            {
                Text = LocalizationService.GetString("SketchOriginalProtected"), FontSize = 13,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0)
            };
            originalHint.SetResourceReference(TextBlock.ForegroundProperty, "TextColorSecondary");
            panel.Children.Add(originalHint);
        }
        _help = CreatePopup(panel, HelpButton, PlacementMode.Top);
        _help.IsOpen = true;
    }

    private Popup CreatePopup(UIElement content, UIElement target, PlacementMode placement)
    {
        var face = new SurfaceMaterialBorder
        {
            CornerRadius = (CornerRadius)FindResource("Radius.Control"), Padding = new Thickness(10), BorderThickness = new Thickness(1), Child = content
        };
        face.SetResourceReference(Border.BackgroundProperty, "CardBackground"); face.SetResourceReference(Border.BorderBrushProperty, "PopupBorder");
        SurfaceMaterial.SetKind(face, SurfaceMaterialKind.Raised);
        var popupRoot = new Grid { Margin = new Thickness(12) };
        popupRoot.Children.Add(new BakedElevationLayer { Kind = BakedElevationLayer.ElevationKind.Button }); popupRoot.Children.Add(face);
        var popup = new Popup { Child = popupRoot, AllowsTransparency = true, StaysOpen = false, Placement = placement, PlacementTarget = target };
        UiMotion.AttachPopupOpenAnimation(popup);
        return popup;
    }

    private void ClosePopups()
    {
        if (_palette is not null) _palette.IsOpen = false;
        if (_help is not null) _help.IsOpen = false;
    }

    private void SetInkColor(Color color)
    {
        if (_tool == SketchTool.Eraser) SelectTool(SketchTool.Pen);
        _strokeColor = color;
        if (_tool == SketchTool.Marker) _markerColor = color; else _inkColor = color;
        ApplyDrawingAttributes();
    }

    private void SetBackground(Color? color)
    {
        EndGesture(); _history.Begin(); _drawing.Background = color; _history.Commit(); ApplyBackground(); UpdateHistoryButtons();
    }
    private void ApplyBackground()
    {
        PaperBackground.Background = _drawing.Background is { } color ? new SolidColorBrush(color) : Brushes.Transparent;
        CurrentBackgroundColorPreview.Background = _drawing.Background is { } selected ? new SolidColorBrush(selected) : CreateCheckerBrush();
    }
    private static Brush CreateCheckerBrush()
    {
        var drawing = new DrawingGroup();
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(232, 232, 238)), null, new RectangleGeometry(new Rect(0, 0, 16, 16))));
        var squares = new GeometryGroup(); squares.Children.Add(new RectangleGeometry(new Rect(0, 0, 8, 8))); squares.Children.Add(new RectangleGeometry(new Rect(8, 8, 8, 8)));
        drawing.Children.Add(new GeometryDrawing(new SolidColorBrush(Color.FromRgb(208, 208, 216)), null, squares));
        var brush = new DrawingBrush(drawing) { TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute, Viewport = new Rect(0, 0, 16, 16) };
        brush.Freeze(); return brush;
    }

    private Point DrawingPoint() { var p = Mouse.GetPosition(SketchCanvas); return new Point(Math.Clamp(p.X, 0, _drawing.Width), Math.Clamp(p.Y, 0, _drawing.Height)); }
    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || _spaceDown) return;
        _history.Begin();
        if (_tool is SketchTool.Pen or SketchTool.Marker or SketchTool.Eraser) return;
        _shapeStart = DrawingPoint(); SketchCanvas.CaptureMouse(); e.Handled = true;
    }
    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not null || _spaceDown) return;
        var p = DrawingPoint();
        if (_shapeStart is { } start)
            ShapePreview.SetStrokes(SketchShapes.Create(_tool, start, p, SketchCanvas.DefaultDrawingAttributes, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)));
        var attributes = SketchCanvas.DefaultDrawingAttributes;
        var width = _tool == SketchTool.Eraser ? SketchCanvas.EraserShape.Width : attributes.Width;
        var height = _tool == SketchTool.Eraser ? SketchCanvas.EraserShape.Height : attributes.Height;
        foreach (var cursor in new[] { CursorContrast, CursorOutline })
        {
            cursor.Width = width; cursor.Height = height; Canvas.SetLeft(cursor, p.X - width / 2); Canvas.SetTop(cursor, p.Y - height / 2);
            cursor.Visibility = _tool is SketchTool.Pen or SketchTool.Marker or SketchTool.Eraser ? Visibility.Visible : Visibility.Collapsed;
        }
    }
    private void Canvas_MouseLeave(object sender, MouseEventArgs e) { CursorContrast.Visibility = CursorOutline.Visibility = Visibility.Collapsed; }
    private void EndGesture()
    {
        if (!_initialized || _finishingGesture) return;
        _finishingGesture = true;
        try
        {
            if (_shapeStart is { } start)
            {
                var end = DrawingPoint(); _shapeStart = null;
                if ((end - start).Length >= 1) _drawing.Strokes.Add(SketchShapes.Create(_tool, start, end, SketchCanvas.DefaultDrawingAttributes, Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)));
                ShapePreview.SetStrokes(null); SketchCanvas.ReleaseMouseCapture();
            }
            _history.Commit(); UpdateHistoryButtons();
        }
        finally { _finishingGesture = false; }
    }
    private void CancelShape()
    {
        _shapeStart = null; ShapePreview.SetStrokes(null);
        if (SketchCanvas.IsMouseCaptured && SketchCanvas.EditingMode == InkCanvasEditingMode.None) SketchCanvas.ReleaseMouseCapture();
    }
    private void UpdateHistoryButtons()
    {
        _undoButton.IsEnabled = _history.CanUndo; _redoButton.IsEnabled = _history.CanRedo;
        _clearButton.IsEnabled = _drawing.Strokes.Count > 0;
        AcceptButton.IsEnabled = _isExisting || _drawing.Strokes.Count > 0;
    }

    private void ResizeWorkspace()
    {
        // DesiredSize includes the footer's separation margin; ActualHeight does not.
        // Reserve the dialog stroke too, otherwise native layout clips the bottom gutter.
        CanvasViewport.Height = Math.Max(80, Math.Min(540, ActualHeight - 48 - 42
            - SketchHeader.DesiredSize.Height - SketchFooter.DesiredSize.Height));
        if (_fit) FitCanvas();
    }
    private void CanvasViewport_SizeChanged(object sender, SizeChangedEventArgs e) { if (_initialized && _fit) FitCanvas(); }
    private void FitCanvas()
    {
        if (CanvasViewport.ActualWidth <= 24 || CanvasViewport.ActualHeight <= 24) return;
        SetZoom(Math.Min((CanvasViewport.ActualWidth - 28) / _drawing.Width, (CanvasViewport.ActualHeight - 28) / _drawing.Height), true);
    }
    private void SetZoom(double zoom, bool fit = false)
    {
        _fit = fit; CanvasScale.ScaleX = CanvasScale.ScaleY = zoom;
        // Frame and pointer outlines remain UI-sized while the authored ink scales.
        var radius = (CornerRadius)FindResource("Radius.Control");
        SketchCanvasOutline.BorderThickness = new Thickness(1.5 / zoom);
        SketchCanvasOutline.CornerRadius = new CornerRadius(radius.TopLeft / zoom, radius.TopRight / zoom,
            radius.BottomRight / zoom, radius.BottomLeft / zoom);
        CursorOutline.StrokeThickness = 1 / zoom; CursorContrast.StrokeThickness = 3 / zoom;
        UpdateCanvasViewportClip();
        var label = Math.Round(zoom * 100) + "%";
        _updatingZoom = true;
        try
        {
            ZoomBox.ItemsSource = new[] { "25%", "50%", "75%", "100%", "150%", "200%", "300%", "400%", label }
                .Distinct().OrderBy(s => double.Parse(s.TrimEnd('%'))).ToArray();
            ZoomBox.SelectedItem = label;
        }
        finally { _updatingZoom = false; }
    }
    private void Fit_Click(object sender, RoutedEventArgs e) { _fit = true; FitCanvas(); }
    private void Zoom_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initialized && !_updatingZoom && ZoomBox.SelectedItem is string value)
        {
            var zoom = double.Parse(value.TrimEnd('%')) / 100;
            if (Math.Abs(zoom - CanvasScale.ScaleX) > .001) SetZoom(zoom);
        }
    }
    private void CanvasViewport_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (Mouse.LeftButton == MouseButtonState.Pressed) { e.Handled = true; return; }
        var before = Mouse.GetPosition(Paper); var viewportPoint = Mouse.GetPosition(CanvasScroll);
        SetZoom(Math.Clamp(CanvasScale.ScaleX * (e.Delta > 0 ? 1.15 : 1 / 1.15), .25, 4));
        CanvasScroll.UpdateLayout();
        var after = Paper.TranslatePoint(before, CanvasScroll);
        CanvasScroll.ScrollToHorizontalOffset(CanvasScroll.HorizontalOffset + after.X - viewportPoint.X);
        CanvasScroll.ScrollToVerticalOffset(CanvasScroll.VerticalOffset + after.Y - viewportPoint.Y); e.Handled = true;
    }
    private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_spaceDown || e.ChangedButton != MouseButton.Left) return;
        _panStart = e.GetPosition(CanvasViewport); _panOffset = new Point(CanvasScroll.HorizontalOffset, CanvasScroll.VerticalOffset);
        CanvasViewport.CaptureMouse(); e.Handled = true;
    }
    private void Viewport_MouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start) return;
        var delta = e.GetPosition(CanvasViewport) - start;
        CanvasScroll.ScrollToHorizontalOffset(_panOffset.X - delta.X); CanvasScroll.ScrollToVerticalOffset(_panOffset.Y - delta.Y); e.Handled = true;
    }
    private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart is null) return;
        _panStart = null; CanvasViewport.ReleaseMouseCapture(); e.Handled = true;
    }
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_encoding || _closing) { e.Handled = true; return; }
        if (_palette is { IsOpen: true } || _help is { IsOpen: true })
        {
            if (e.Key == Key.Escape) { ClosePopups(); e.Handled = true; }
            return;
        }
        if (Keyboard.FocusedElement is TextBox) return;
        if (e.Key == Key.Space)
        {
            if (Mouse.LeftButton == MouseButtonState.Pressed) { e.Handled = true; return; }
            _spaceDown = true; CanvasViewport.Cursor = SketchCanvas.Cursor = Cursors.Hand;
            CursorContrast.Visibility = CursorOutline.Visibility = Visibility.Collapsed; e.Handled = true;
        }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && e.Key is Key.Z or Key.Y)
        {
            EndGesture(); if (e.Key == Key.Y || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _history.Redo(); else _history.Undo();
            ApplyBackground(); UpdateHistoryButtons(); e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (_shapeStart is not null) CancelShape(); else BeginCloseAnimation(false); e.Handled = true;
        }
        else if (e.Key == Key.Enter && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { Insert_Click(this, new RoutedEventArgs()); e.Handled = true; }
    }
    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e); if (e.Key != Key.Space) return;
        ResetPan(); e.Handled = true;
    }

    private void ResetPan()
    {
        _spaceDown = false; CanvasViewport.Cursor = null; UpdateToolCursor();
        if (_panStart is not null) { _panStart = null; CanvasViewport.ReleaseMouseCapture(); }
    }

    protected override void OnSourceInitialized(EventArgs e) { base.OnSourceInitialized(e); OverlayDialogBoundsHelper.Apply(this); }
    private void SketchCanvasOutline_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCanvasViewportClip();
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        Dispatcher.BeginInvoke(UpdateCanvasViewportClip, System.Windows.Threading.DispatcherPriority.Loaded);
    }
    private void UpdateCanvasViewportClip()
    {
        if (SketchCanvasViewport is null || SketchCanvasOutline is null || SketchCanvasOutline.ActualWidth <= 0 || SketchCanvasOutline.ActualHeight <= 0) return;
        var frame = SurfaceContour.EffectiveFrame(SketchCanvasOutline);
        var inset = Math.Max(Math.Max(frame.Left, frame.Right), Math.Max(frame.Top, frame.Bottom));
        SketchCanvasViewport.Clip = SurfaceContour.Create(SketchCanvasOutline.RenderSize, SketchCanvasOutline.CornerRadius, frame, inset);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => BeginCloseAnimation(false);
    private async void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (_encoding || _closing) return;
        EndGesture();
        if (!_isExisting && _drawing.Strokes.Count == 0) return;
        try
        {
            if (!_isExisting || HasChanges)
            {
                _encoding = true; SketchShell.IsEnabled = false;
                var label = AcceptButton.Content; AcceptButton.Content = LocalizationService.GetString("SketchPreparing");
                try { SketchPngBytes = await SketchPngCodec.EncodeAsync(_drawing); }
                finally { _encoding = false; SketchShell.IsEnabled = true; AcceptButton.Content = label; }
            }
            BeginCloseAnimation(true);
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(LocalizationService.GetString("FailedToInsertSketch") + "\n\n" + ex.Message, LocalizationService.GetString("Error"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e); if (_allowClose) return;
        if (_encoding) { e.Cancel = true; return; }
        e.Cancel = true;
        // Off motion can finish synchronously. Leave WPF's Closing callback before
        // requesting the final close, rather than reentering it through DialogResult.
        Dispatcher.BeginInvoke(() => { if (IsVisible) BeginCloseAnimation(false); });
    }
    private void BeginCloseAnimation(bool accepted)
    {
        if (_closing || _confirmingClose) return;
        CancelShape(); _history.Commit();
        if (!accepted && HasChanges)
        {
            _confirmingClose = true;
            ModernDialogResult result;
            try
            {
                result = ModernDialog.Show(this, HeadingText.Text, LocalizationService.GetString("SketchDiscardPrompt"),
                    ModernDialogTone.Question, LocalizationService.GetString("SketchDiscard"),
                    LocalizationService.GetString("SketchKeepDrawing"), primaryStyle: ModernDialogButtonStyle.Danger,
                    defaultChoice: ModernDialogResult.Cancel);
            }
            finally { _confirmingClose = false; }
            if (result != ModernDialogResult.Primary) return;
        }
        _closing = true; ClosePopups();
        _overlayAnimator.BeginClose(() =>
        {
            _allowClose = true;
            if (System.Windows.Interop.ComponentDispatcher.IsThreadModal) DialogResult = accepted;
            Close();
        });
    }
}
