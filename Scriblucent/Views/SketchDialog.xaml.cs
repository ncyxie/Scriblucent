using Scriblucent.Animations;
using Scriblucent.Controls;
using Scriblucent.Localization;
using Scriblucent.Services;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Scriblucent.Views;

public partial class SketchDialog : Window
{
    private enum SketchTool
    {
        Pen,
        Marker,
        Eraser
    }

    // Muted ink-like pigments — shared with Select Color / AppColorPalette.Ink.
    private static readonly Color[] PenPalette = AppColorPalette.Ink;

    private readonly OverlayDialogAnimator _overlayAnimator;
    private readonly Stack<Stroke> _undoStack = new();
    private bool _isApplyingStrokeChange;
    private SketchTool _tool = SketchTool.Pen;
    private Color _strokeColor = Color.FromRgb(0x1A, 0x1A, 0x1A);
    private Color? _backgroundColor = Colors.White;

    public byte[]? SketchPngBytes { get; private set; }
    public int SketchPixelWidth { get; private set; }
    public int SketchPixelHeight { get; private set; }
    public bool UsesTransparentBackground => _backgroundColor is null;

    public SketchDialog()
    {
        InitializeComponent();
        _overlayAnimator = ((OverlayDialogShell)Content).BindAnimator(this);
        Scriblucent.Services.WindowThemeService.Register(this);
        Loaded += SketchDialog_Loaded;
        SketchCanvas.StrokeCollected += SketchCanvas_StrokeCollected;
        // InkCanvas / focus / touch can nudge ancestors (bring-into-view, boundary bounce).
        AddHandler(RequestBringIntoViewEvent, (RequestBringIntoViewEventHandler)((_, e) => e.Handled = true));
        SketchCanvas.RequestBringIntoView += (_, e) => e.Handled = true;
        SketchCanvas.ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
        ManipulationBoundaryFeedback += (_, e) => e.Handled = true;
        BuildPenColorSwatches();
        BuildBackgroundSwatches();
        ApplyCanvasBackground();
        ApplyDrawingAttributes();
        UpdateToolButtonStates();
        UpdatePenColorPreview();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        OverlayDialogBoundsHelper.Apply(this);
    }

    private void SketchDialog_Loaded(object sender, RoutedEventArgs e)
    {
        OverlayDialogBoundsHelper.Apply(this);
        UiMotion.ConfigurePopupsForWindow(this);
        // After open settles, pin the card so later ink measure/adorners cannot re-center it.
        _overlayAnimator.BeginOpen(PinDialogCardAfterOpen);
    }

    /// <summary>
    /// Overlay cards are vertically centered. Any tiny content height change then recenters
    /// the whole panel (reads as a downward/upward jump when inking starts). Lock Top.
    /// </summary>
    private void PinDialogCardAfterOpen()
    {
        if (Content is not OverlayDialogShell { PanelMotionHost: { } host, OverlayRoot: { } root })
            return;

        host.UpdateLayout();
        var top = host.TranslatePoint(new Point(0, 0), root).Y;
        if (double.IsNaN(top) || double.IsInfinity(top) || top < 0)
            return;

        // Open animation leaves translate at 0; keep it there while switching alignment.
        if (host.RenderTransform is TranslateTransform translate)
            translate.Y = 0;

        host.VerticalAlignment = VerticalAlignment.Top;
        host.HorizontalAlignment = HorizontalAlignment.Center;
        host.Margin = new Thickness(host.Margin.Left, top, host.Margin.Right, host.Margin.Bottom);
    }

    private void BuildPenColorSwatches()
    {
        PenColorSwatchPanel.Children.Clear();
        foreach (var color in PenPalette)
            PenColorSwatchPanel.Children.Add(CreateColorSwatch(color, () => SelectPenColor(color)));
    }

    private void BuildBackgroundSwatches()
    {
        BackgroundSwatchPanel.Children.Clear();

        var transparentButton = CreateBackgroundSwatchButton(
            LocalizationService.GetString("SketchBackgroundTransparent"),
            CreateCheckerBrush(),
            () => SetBackgroundColor(null));
        BackgroundSwatchPanel.Children.Add(transparentButton);

        foreach (var color in AppColorPalette.Paper)
        {
            BackgroundSwatchPanel.Children.Add(
                CreateColorSwatch(color, () => SetBackgroundColor(color)));
        }
    }

    private Button CreateColorSwatch(Color color, Action onClick)
    {
        var button = new Button
        {
            Style = (Style)FindResource("SketchSwatchButtonStyle"),
            Background = new SolidColorBrush(color),
            ToolTip = $"#{color.R:X2}{color.G:X2}{color.B:X2}"
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private Button CreateBackgroundSwatchButton(string tooltip, Brush background, Action onClick)
    {
        var button = new Button
        {
            Style = (Style)FindResource("SketchSwatchButtonStyle"),
            Background = background,
            ToolTip = tooltip,
            Width = 28,
            Height = 22
        };
        button.Click += (_, _) => onClick();
        return button;
    }

    private static Brush CreateCheckerBrush()
    {
        var brush = new DrawingBrush
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 8, 8),
            ViewportUnits = BrushMappingMode.Absolute,
            Drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xEE)), null,
                        new RectangleGeometry(new Rect(0, 0, 8, 8))),
                    new GeometryDrawing(new SolidColorBrush(Color.FromRgb(0xD0, 0xD0, 0xD8)), null,
                        new GeometryGroup
                        {
                            Children =
                            {
                                new RectangleGeometry(new Rect(0, 0, 4, 4)),
                                new RectangleGeometry(new Rect(4, 4, 4, 4))
                            }
                        })
                }
            }
        };
        brush.Freeze();
        return brush;
    }

    private void SketchCanvas_StrokeCollected(object sender, InkCanvasStrokeCollectedEventArgs e)
    {
        if (_isApplyingStrokeChange)
            return;

        _undoStack.Push(e.Stroke);
    }

    private void PenToolButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = SketchTool.Pen;
        SketchCanvas.EditingMode = InkCanvasEditingMode.Ink;
        ApplyDrawingAttributes();
        UpdateToolButtonStates();
    }

    private void MarkerToolButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = SketchTool.Marker;
        SketchCanvas.EditingMode = InkCanvasEditingMode.Ink;
        ApplyDrawingAttributes();
        UpdateToolButtonStates();
    }

    private void EraserToolButton_Click(object sender, RoutedEventArgs e)
    {
        _tool = SketchTool.Eraser;
        // Point erase gives finer control than whole-stroke erase.
        SketchCanvas.EditingMode = InkCanvasEditingMode.EraseByPoint;
        UpdateToolButtonStates();
    }

    private void StrokeWidthSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (StrokeWidthLabel is null)
            return;

        StrokeWidthLabel.Text = ((int)Math.Round(StrokeWidthSlider.Value)).ToString();
        ApplyDrawingAttributes();
    }

    private void SelectPenColor(Color color)
    {
        _strokeColor = color;
        if (_tool == SketchTool.Eraser)
        {
            _tool = SketchTool.Pen;
            SketchCanvas.EditingMode = InkCanvasEditingMode.Ink;
        }

        ApplyDrawingAttributes();
        UpdateToolButtonStates();
        UpdatePenColorPreview();
    }

    private void PickPenColorButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog
        {
            Owner = this,
            InitialColor = _strokeColor
        };
        if (dialog.ShowDialog() == true && dialog.SelectedColor is { } color)
            SelectPenColor(color);
    }

    private void SetBackgroundColor(Color? color)
    {
        _backgroundColor = color;
        ApplyCanvasBackground();
    }

    private void PickBackgroundColorButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ColorPickerDialog
        {
            Owner = this,
            InitialColor = _backgroundColor ?? Colors.White
        };
        if (dialog.ShowDialog() == true && dialog.SelectedColor is { } color)
            SetBackgroundColor(color);
    }

    private void ApplyCanvasBackground()
    {
        if (_backgroundColor is null)
        {
            TransparencyChecker.Visibility = Visibility.Visible;
            SketchCanvas.Background = Brushes.Transparent;
            if (CurrentBackgroundColorPreview is not null)
                CurrentBackgroundColorPreview.Background = CreateCheckerBrush();
        }
        else
        {
            TransparencyChecker.Visibility = Visibility.Collapsed;
            SketchCanvas.Background = new SolidColorBrush(_backgroundColor.Value);
            if (CurrentBackgroundColorPreview is not null)
                CurrentBackgroundColorPreview.Background = new SolidColorBrush(_backgroundColor.Value);
        }
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (SketchCanvas.Strokes.Count == 0)
            return;

        _isApplyingStrokeChange = true;
        try
        {
            var lastIndex = SketchCanvas.Strokes.Count - 1;
            var stroke = SketchCanvas.Strokes[lastIndex];
            SketchCanvas.Strokes.RemoveAt(lastIndex);
            if (_undoStack.Count > 0 && ReferenceEquals(_undoStack.Peek(), stroke))
                _undoStack.Pop();
        }
        finally
        {
            _isApplyingStrokeChange = false;
        }
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        _isApplyingStrokeChange = true;
        try
        {
            SketchCanvas.Strokes.Clear();
            _undoStack.Clear();
        }
        finally
        {
            _isApplyingStrokeChange = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => BeginCloseAnimation(false);

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        if (SketchCanvas.Strokes.Count == 0)
        {
            ModernMessageBox.Show(
                LocalizationService.GetString("SketchEmpty"),
                LocalizationService.GetString("SketchDialogTitle"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            return;
        }

        try
        {
            var bitmap = RenderSketchToBitmap();
            SketchPixelWidth = bitmap.PixelWidth;
            SketchPixelHeight = bitmap.PixelHeight;

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            SketchPngBytes = stream.ToArray();
            BeginCloseAnimation(true);
        }
        catch (Exception ex)
        {
            ModernMessageBox.Show(
                $"{LocalizationService.GetString("FailedToInsertSketch")}\n\n{ex.Message}",
                LocalizationService.GetString("Error"),
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private RenderTargetBitmap RenderSketchToBitmap()
    {
        var width = Math.Max(1, (int)Math.Ceiling(SketchCanvas.ActualWidth));
        var height = Math.Max(1, (int)Math.Ceiling(SketchCanvas.ActualHeight));

        // Draw strokes directly. Capturing InkCanvas via VisualBrush fills transparent
        // pixels opaque, so alpha backgrounds would never survive export.
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var bounds = new Rect(0, 0, width, height);
            if (_backgroundColor is { } background)
                context.DrawRectangle(new SolidColorBrush(background), null, bounds);

            SketchCanvas.Strokes.Draw(context);
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private void ApplyDrawingAttributes()
    {
        var width = StrokeWidthSlider?.Value > 0 ? StrokeWidthSlider.Value : 4;
        var attributes = new DrawingAttributes
        {
            Color = _strokeColor,
            FitToCurve = true,
            IgnorePressure = false,
            IsHighlighter = _tool == SketchTool.Marker
        };

        if (_tool == SketchTool.Marker)
        {
            attributes.Width = Math.Max(8, width * 2.2);
            attributes.Height = Math.Max(14, width * 3.5);
        }
        else
        {
            attributes.Width = width;
            attributes.Height = width;
        }

        SketchCanvas.DefaultDrawingAttributes = attributes;

        // Larger eraser tip scales with stroke slider for freer cleanup.
        SketchCanvas.EraserShape = new EllipseStylusShape(Math.Max(8, width * 2.5), Math.Max(8, width * 2.5));
    }

    private void UpdateToolButtonStates()
    {
        PenToolButton.Opacity = _tool == SketchTool.Pen ? 1 : 0.55;
        MarkerToolButton.Opacity = _tool == SketchTool.Marker ? 1 : 0.55;
        EraserToolButton.Opacity = _tool == SketchTool.Eraser ? 1 : 0.55;
    }

    private void UpdatePenColorPreview()
    {
        if (CurrentPenColorPreview is not null)
            CurrentPenColorPreview.Background = new SolidColorBrush(_strokeColor);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            BeginCloseAnimation(false);
            e.Handled = true;
        }
    }

    private void BeginCloseAnimation(bool accepted)
    {
        _overlayAnimator.BeginClose(() =>
        {
            DialogResult = accepted;
            Close();
        });
    }
}
