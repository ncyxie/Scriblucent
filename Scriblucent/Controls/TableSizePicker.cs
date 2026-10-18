using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Localization;

namespace Scriblucent.Controls;

/// <summary>A single painted grid and coordinated preview, with native focus and keyboard input.</summary>
public sealed class TableSizePicker : Control
{
    public const int MaximumDimension = 8;
    private const double Step = 22, Cell = 18, Gap = 2;
    private bool _pressed;
    private static readonly DependencyProperty PreviewRowsProperty = DependencyProperty.Register("PreviewRows", typeof(double),
        typeof(TableSizePicker), new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly DependencyProperty PreviewColumnsProperty = DependencyProperty.Register("PreviewColumns", typeof(double),
        typeof(TableSizePicker), new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CellBrushProperty = DependencyProperty.Register(nameof(CellBrush), typeof(Brush),
        typeof(TableSizePicker), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(nameof(GridBrush), typeof(Brush),
        typeof(TableSizePicker), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(nameof(SelectionBrush), typeof(Brush),
        typeof(TableSizePicker), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty SelectionRimProperty = DependencyProperty.Register(nameof(SelectionRim), typeof(Brush),
        typeof(TableSizePicker), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public Brush? CellBrush { get => (Brush?)GetValue(CellBrushProperty); set => SetValue(CellBrushProperty, value); }
    public Brush? GridBrush { get => (Brush?)GetValue(GridBrushProperty); set => SetValue(GridBrushProperty, value); }
    public Brush? SelectionBrush { get => (Brush?)GetValue(SelectionBrushProperty); set => SetValue(SelectionBrushProperty, value); }
    public Brush? SelectionRim { get => (Brush?)GetValue(SelectionRimProperty); set => SetValue(SelectionRimProperty, value); }
    public int Rows { get; private set; } = 3;
    public int Columns { get; private set; } = 3;
    public event EventHandler? SelectionChanged;
    public event EventHandler? InsertRequested;
    public event EventHandler? DismissRequested;
    public string SelectionLabel => string.Format(LocalizationService.GetString("InsertTableSizeFormat"), Rows, Columns);

    public TableSizePicker()
    {
        Focusable = true;
        Cursor = Cursors.Hand;
        FocusVisualStyle = null;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
        SetResourceReference(CellBrushProperty, "InputSurfaceInset");
        SetResourceReference(GridBrushProperty, "BorderColor");
        SetResourceReference(SelectionBrushProperty, "AccentPrimarySubtle");
        SetResourceReference(SelectionRimProperty, "AccentRimBrush");
        Unloaded += (_, _) =>
        {
            _pressed = false;
            SurfaceMotion.Stop(this, MotionChannel.Selection);
            SetCurrentValue(PreviewRowsProperty, (double)Rows);
            SetCurrentValue(PreviewColumnsProperty, (double)Columns);
        };
    }

    public void Select(int rows, int columns, bool animate = true)
    {
        Rows = Math.Clamp(rows, 1, MaximumDimension);
        Columns = Math.Clamp(columns, 1, MaximumDimension);
        AutomationProperties.SetName(this, SelectionLabel);
        SelectionChanged?.Invoke(this, EventArgs.Empty);
        SurfaceMotion.AnimateTracks(this, MotionChannel.Selection,
            [new(this, PreviewRowsProperty, Rows, true), new(this, PreviewColumnsProperty, Columns, true)],
            MotionTokens.InteractionIn, UiMotion.InOutSine, instant: !animate);
    }

    protected override Size MeasureOverride(Size constraint) => new(Step * MaximumDimension, Step * MaximumDimension);
    protected override void OnRender(DrawingContext drawing)
    {
        drawing.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        var gridPen = new Pen(GridBrush, .75);
        for (var row = 0; row < MaximumDimension; row++)
            for (var column = 0; column < MaximumDimension; column++)
                drawing.DrawRoundedRectangle(CellBrush, gridPen, new Rect(column * Step + Gap, row * Step + Gap, Cell, Cell), 3, 3);
        var bounds = new Rect(1, 1, (double)GetValue(PreviewColumnsProperty) * Step - 2,
            (double)GetValue(PreviewRowsProperty) * Step - 2);
        drawing.DrawRoundedRectangle(SelectionBrush, new Pen(SelectionRim, 2), bounds, 5, 5);
        // Selected cell contours stay visible beneath one shared wash and outer selection rim.
        drawing.PushClip(new RectangleGeometry(bounds, 5, 5));
        drawing.PushOpacity(.35);
        for (var row = 0; row < MaximumDimension; row++)
            for (var column = 0; column < MaximumDimension; column++)
                drawing.DrawRoundedRectangle(null, gridPen, new Rect(column * Step + Gap, row * Step + Gap, Cell, Cell), 3, 3);
        drawing.Pop(); drawing.Pop();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        SelectAt(e.GetPosition(this));
    }
    private void SelectAt(Point point)
    {
        var row = Math.Clamp((int)(point.Y / Step) + 1, 1, MaximumDimension);
        var column = Math.Clamp((int)(point.X / Step) + 1, 1, MaximumDimension);
        if (row != Rows || column != Columns) Select(row, column);
    }
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus(); SelectAt(e.GetPosition(this)); _pressed = true; e.Handled = true;
    }
    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        bool commit = _pressed && new Rect(RenderSize).Contains(e.GetPosition(this));
        _pressed = false;
        if (commit) { SelectAt(e.GetPosition(this)); InsertRequested?.Invoke(this, EventArgs.Empty); }
        e.Handled = true;
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        switch (e.Key)
        {
            case Key.Left: Select(Rows, Columns - 1); break;
            case Key.Right: Select(Rows, Columns + 1); break;
            case Key.Up: Select(Rows - 1, Columns); break;
            case Key.Down: Select(Rows + 1, Columns); break;
            case Key.Enter: case Key.Space: InsertRequested?.Invoke(this, EventArgs.Empty); break;
            case Key.Escape: DismissRequested?.Invoke(this, EventArgs.Empty); break;
            case Key.Tab: DismissRequested?.Invoke(this, EventArgs.Empty); return;
            default: return;
        }
        e.Handled = true;
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new PickerPeer(this);
    private sealed class PickerPeer(TableSizePicker owner) : FrameworkElementAutomationPeer(owner), IInvokeProvider
    {
        protected override string GetClassNameCore() => nameof(TableSizePicker);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Button;
        protected override string GetNameCore() => owner.SelectionLabel;
        public override object? GetPattern(PatternInterface patternInterface)
            => patternInterface == PatternInterface.Invoke ? this : base.GetPattern(patternInterface);
        public void Invoke()
        {
            if (!owner.IsEnabled) throw new ElementNotEnabledException();
            owner.InsertRequested?.Invoke(owner, EventArgs.Empty);
        }
    }
}
