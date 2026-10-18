using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Animations;
using Scriblucent.Behaviors;
using Scriblucent.Localization;
using Scriblucent.Services;
using Scriblucent.Icons;

namespace Scriblucent.Controls;

internal sealed record SelectionDockAction(string TextKey, AppIconKind Icon, Action Execute, Func<bool>? CanExecute = null);

public partial class CollectionSelectionDock : UserControl
{
    private ICollectionSelectionScope? _selection;
    private SelectionDockAction? _primaryAction, _secondaryAction, _deleteAction;
    private int _minimumCount;
    private CancellationTokenSource? _operation;
    internal bool IsOperationRunning => _operation is not null;
    private string? _operationStatus;
    private Func<ContextMenu>? _menu;
    private ContextMenu? _openMenu;
    private bool? _shown;
    private bool? _presented;
    private double _visibleHeight;
    private FrameworkElement? _slot;
    private bool _compact;
    // Only the scroll extent gets clearance; the collection viewport never shrinks.
    public static readonly DependencyProperty ClearanceProperty = DependencyProperty.RegisterAttached("Clearance", typeof(double), typeof(CollectionSelectionDock),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.Inherits | FrameworkPropertyMetadataOptions.AffectsMeasure));
    public static double GetClearance(DependencyObject element) => (double)element.GetValue(ClearanceProperty);
    public static void SetClearance(DependencyObject element, double value) => element.SetValue(ClearanceProperty, value);
    private static readonly DependencyPropertyKey ScrollClearanceKey = DependencyProperty.RegisterReadOnly(nameof(ScrollClearance), typeof(double), typeof(CollectionSelectionDock), new PropertyMetadata(0d));
    public static readonly DependencyProperty ScrollClearanceProperty = ScrollClearanceKey.DependencyProperty;
    public double ScrollClearance => (double)GetValue(ScrollClearanceProperty);
    public static readonly DependencyProperty ScrollTargetProperty = DependencyProperty.Register(nameof(ScrollTarget), typeof(FrameworkElement), typeof(CollectionSelectionDock), new PropertyMetadata(null));
    public FrameworkElement? ScrollTarget { get => (FrameworkElement?)GetValue(ScrollTargetProperty); set => SetValue(ScrollTargetProperty, value); }
    public CollectionSelectionDock()
    {
        InitializeComponent();
        AllToggle.ToggleRequested = ToggleAll;
        Loaded += (_, _) =>
        {
            _slot = Parent as FrameworkElement;
            if (_slot?.Parent is FrameworkElement parent) parent.SizeChanged += AvailableSizeChanged;
            ItemReorderBehavior.DragStateChanged += DragStateChanged;
            _shown = _presented = null; Adapt(); Refresh();
        };
        Unloaded += (_, _) =>
        {
            if (_slot?.Parent is FrameworkElement parent) parent.SizeChanged -= AvailableSizeChanged;
            _slot = null;
            ItemReorderBehavior.DragStateChanged -= DragStateChanged;
            _openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false); _shown = _presented = null;
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != System.Windows.Input.Key.Escape) return;
            if (_openMenu?.IsOpen == true) _openMenu.IsOpen = false; else if (_operation is not null) _operation.Cancel(); else _selection?.End();
            e.Handled = true;
        };
        SizeChanged += (_, _) => UpdateClearance();
        PreviewMouseWheel += ForwardWheel;
    }
    private void ForwardWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
            || ScrollTarget is not { IsLoaded: true, IsVisible: true } target) return;
        var viewer = target as ScrollViewer ?? FindScroll(target);
        if (viewer is null) return;
        // The island is a sibling of its collection, so normal routed input cannot reach it.
        // Use the same wheel controller and policy as input over the cards themselves.
        var wheel = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = Mouse.PreviewMouseWheelEvent, Source = viewer };
        viewer.RaiseEvent(wheel);
        if (!wheel.Handled)
        {
            wheel = new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = Mouse.MouseWheelEvent, Source = viewer };
            viewer.RaiseEvent(wheel);
        }
        e.Handled = wheel.Handled;
    }
    private static ScrollViewer? FindScroll(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer viewer) return viewer;
            if (child is not ItemsPresenter && FindScroll(child) is { } found) return found;
        }
        return null;
    }
    private void DragStateChanged()
    {
        if (ItemReorderBehavior.IsDragging && ReferenceEquals(ItemReorderBehavior.DragOwner, Window.GetWindow(this)))
            _openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        UpdatePresentation();
    }
    private void UpdatePresentation()
    {
        if (Parent is not LayoutRevealHost) return;
        bool show = _shown == true && !(ItemReorderBehavior.IsDragging && ReferenceEquals(ItemReorderBehavior.DragOwner, Window.GetWindow(this)));
        if (_presented == show) return;
        bool animate = _presented is not null && IsLoaded;
        _presented = show;
        FloatingCommandMotion.SetVisible(this, show, animate, fromBottom: true);
    }
    internal void Initialize(ICollectionSelectionScope selection, Action delete, Func<ContextMenu> menu, Action duplicate)
        => Initialize(selection, new("SelectionDuplicate", AppIconKind.Duplicate, duplicate),
            new("MassSelectDeleteSelected", AppIconKind.Delete, delete), menu);
    internal void Initialize(ICollectionSelectionScope selection, SelectionDockAction primary, SelectionDockAction delete,
        Func<ContextMenu> menu, SelectionDockAction? secondary = null, int minimumCount = 0)
    {
        if (_selection is not null) _selection.Changed -= SelectionChanged;
        _selection = selection; _primaryAction = primary; _secondaryAction = secondary; _deleteAction = delete;
        _minimumCount = minimumCount; _menu = menu;
        PrimaryIcon.Kind = primary.Icon;
        SecondaryButton.Content = secondary is null ? null : new AppIcon { Kind = secondary.Icon, Width = 16, Height = 16 };
        SecondaryButton.Visibility = secondary is null ? Visibility.Collapsed : Visibility.Visible;
        selection.Changed += SelectionChanged; Refresh();
    }
    private void SelectionChanged() { if (_operation is null) _operationStatus = null; Refresh(); }
    internal async Task RunOperationAsync(Func<CancellationToken, IProgress<string>, Task> action)
    {
        if (_operation is not null) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        _operationStatus = LocalizationService.GetString("SelectionExportWorking"); Refresh();
        try
        {
            await action(cancellation.Token, new Progress<string>(text => { if (ReferenceEquals(_operation, cancellation)) { _operationStatus = text; Refresh(); } }));
            _operationStatus = LocalizationService.GetString("SelectionExportComplete");
        }
        catch (OperationCanceledException) { _operationStatus = LocalizationService.GetString("SelectionExportCancelled"); }
        catch (Exception error)
        {
            _operationStatus = LocalizationService.GetString("SelectionExportFailed");
            if (IsLoaded) ModernDialog.ShowInfo(Window.GetWindow(this), _operationStatus, error.Message);
        }
        finally { if (ReferenceEquals(_operation, cancellation)) { _operation = null; Refresh(); } }
    }
    internal bool HandleOperationKey(System.Windows.Input.KeyEventArgs e)
    {
        if (_operation is null) return false;
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            for (var p = e.OriginalSource as DependencyObject; p is not null; p = EditorSelectionInput.Parent(p))
                if (p is ComboBox { IsDropDownOpen: true }) return false;
            _operation.Cancel(); e.Handled = true; return true;
        }
        if (e.Key == System.Windows.Input.Key.Delete && !EditorSelectionInput.IsInteractive(e.OriginalSource as DependencyObject))
        { e.Handled = true; return true; }
        return false;
    }
    internal void Dispose()
    {
        _operation?.Cancel();
        _openMenu?.SetCurrentValue(ContextMenu.IsOpenProperty, false);
        if (_selection is not null) _selection.Changed -= SelectionChanged;
        _selection = null; _primaryAction = _secondaryAction = _deleteAction = null; _menu = null;
    }
    internal void FocusOnEntry() => Dispatcher.BeginInvoke(() =>
    {
        if (_selection?.IsActive == true && IsLoaded && IsVisible) Focus();
    }, System.Windows.Threading.DispatcherPriority.Input);
    internal void Refresh()
    {
        if (_selection is not { } selection) return;
        CountText.Text = string.Format(LocalizationService.GetString("PremiumSelectedCount"), selection.Count);
        TotalText.Text = string.Format(LocalizationService.GetString("SelectionMatchingTotal"), selection.TotalCount);
        AllToggle.SetCurrentValue(ToggleButton.IsCheckedProperty, selection.MatchingSelectedCount == 0 ? false : selection.MatchingSelectedCount == selection.TotalCount ? true : (bool?)null);
        StatusText.Text = _operationStatus ?? selection.StatusText;
        StatusText.Visibility = string.IsNullOrEmpty(StatusText.Text) ? Visibility.Collapsed : Visibility.Visible;
        SetLabel(AllButton, LocalizationService.GetString(selection.TotalCount > 0 && selection.MatchingSelectedCount == selection.TotalCount ? "EditorClearSelection" : "EditorSelectAllMatching"));
        SetLabel(DuplicateButton, LocalizationService.GetString(_primaryAction?.TextKey ?? "SelectionDuplicate"));
        if (_secondaryAction is { } secondary) SetLabel(SecondaryButton, LocalizationService.GetString(secondary.TextKey));
        SetLabel(DeleteButton, _deleteAction?.TextKey is "Delete" or "MassSelectDeleteSelected" or null ? string.Format(LocalizationService.GetString("SelectionDeleteCount"), selection.Count) : LocalizationService.GetString(_deleteAction.TextKey));
        SetLabel(MoreButton, LocalizationService.GetString("MassSelectMoreActions"));
        SetLabel(DoneButton, LocalizationService.GetString("EditorSelectionDone"));
        DuplicateLabel.Text = LocalizationService.GetString(_primaryAction?.TextKey ?? "SelectionDuplicate");
        bool busy = _operation is not null;
        CancelOperationButton.Content = LocalizationService.GetString("Cancel");
        CancelOperationButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        AllButton.IsEnabled = !busy && selection.TotalCount > 0;
        DuplicateButton.IsEnabled = !busy && selection.Count > 0 && (_primaryAction?.CanExecute?.Invoke() ?? true);
        SecondaryButton.IsEnabled = !busy && selection.Count > 0 && (_secondaryAction?.CanExecute?.Invoke() ?? true);
        DeleteButton.IsEnabled = !busy && selection.Count > 0 && (_deleteAction?.CanExecute?.Invoke() ?? true);
        MoreButton.IsEnabled = !busy && selection.Count > 0;
        bool show = busy || (selection.IsActive && selection.Count >= _minimumCount);
        _shown = show;
        UpdatePresentation();
        UpdateClearance();
    }
    private void AvailableSizeChanged(object sender, SizeChangedEventArgs e) => Adapt();
    private void Adapt()
    {
        if (_slot?.Parent is not FrameworkElement parent) return;
        double available = parent.ActualWidth - _slot.Margin.Left - _slot.Margin.Right;
        _compact = available < 440;
        DuplicateButton.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        StatusText.MaxWidth = Math.Max(160, Math.Min(360, available - 28));
    }
    private void UpdateClearance()
    {
        if (_presented == true && ActualHeight > 0) _visibleHeight = ActualHeight;
        double clearance = _shown == true ? Math.Max(96, _visibleHeight + (_slot?.Margin.Bottom ?? 16) + 20) : 0;
        SetValue(ScrollClearanceKey, clearance);
    }
    private static void SetLabel(Button button, string text) { button.ToolTip = text; AutomationProperties.SetName(button, text); }
    private void ToggleAll()
    {
        if (_selection is not { } selection) return;
        if (selection.TotalCount > 0 && selection.MatchingSelectedCount == selection.TotalCount) selection.ClearMatching(); else selection.SelectAll();
    }
    private void AllClick(object sender, RoutedEventArgs e) => ToggleAll();
    private void DuplicateClick(object sender, RoutedEventArgs e) => _primaryAction?.Execute();
    private void DoneClick(object sender, RoutedEventArgs e) { if (_operation is not null) _operation.Cancel(); else _selection?.End(); }
    private void SecondaryClick(object sender, RoutedEventArgs e) => _secondaryAction?.Execute();
    private void CancelOperationClick(object sender, RoutedEventArgs e) => _operation?.Cancel();
    private void DeleteClick(object sender, RoutedEventArgs e) => _deleteAction?.Execute();
    private void MoreClick(object sender, RoutedEventArgs e)
    {
        if (_menu?.Invoke() is not { } menu) return;
        if (_compact)
        {
            var duplicate = new MenuItem { Header = LocalizationService.GetString(_primaryAction?.TextKey ?? "SelectionDuplicate"), IsEnabled = DuplicateButton.IsEnabled };
            duplicate.Click += (_, _) => _primaryAction?.Execute();
            menu.Items.Insert(0, duplicate); menu.Items.Insert(1, new Separator());
        }
        var clear = new MenuItem { Header = LocalizationService.GetString("EditorClearSelection"), IsEnabled = _selection?.Count > 0 };
        clear.Click += (_, _) => _selection?.Clear();
        if (menu.Items.Count > 0) menu.Items.Add(new Separator()); menu.Items.Add(clear);
        _openMenu = menu;
        menu.PlacementTarget = MoreButton; menu.Placement = PlacementMode.Bottom;
        menu.Closed += (_, _) => { if (ReferenceEquals(_openMenu, menu)) _openMenu = null; };
        menu.IsOpen = true;
    }
}
