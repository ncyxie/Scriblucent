using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Scriblucent.Animations;
using Scriblucent.Icons;

namespace Scriblucent.Controls;

/// <summary>A dashboard-local creation menu. No capture, blocking overlay, or persisted open state.</summary>
public partial class DashboardCreateMenu : UserControl
{
    public static readonly DependencyProperty IsOpenProperty = DependencyProperty.Register(nameof(IsOpen), typeof(bool), typeof(DashboardCreateMenu),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OpenChanged));
    public static readonly DependencyProperty CreateLabelProperty = DependencyProperty.Register(nameof(CreateLabel), typeof(string), typeof(DashboardCreateMenu), new PropertyMetadata(""));
    public static readonly DependencyProperty ImportLabelProperty = DependencyProperty.Register(nameof(ImportLabel), typeof(string), typeof(DashboardCreateMenu), new PropertyMetadata(""));
    public static readonly DependencyProperty CreateIconProperty = DependencyProperty.Register(nameof(CreateIcon), typeof(AppIconKind), typeof(DashboardCreateMenu), new PropertyMetadata(AppIconKind.Plus));
    public static readonly DependencyProperty QuickAddLabelProperty = DependencyProperty.Register(nameof(QuickAddLabel), typeof(string), typeof(DashboardCreateMenu), new PropertyMetadata(""));
    public static readonly DependencyProperty QuickAddCommandProperty = DependencyProperty.Register(nameof(QuickAddCommand), typeof(ICommand), typeof(DashboardCreateMenu));
    public static readonly RoutedEvent CreateRequestedEvent = EventManager.RegisterRoutedEvent(nameof(CreateRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DashboardCreateMenu));
    public static readonly RoutedEvent ImportRequestedEvent = EventManager.RegisterRoutedEvent(nameof(ImportRequested), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DashboardCreateMenu));
    public bool IsOpen { get => (bool)GetValue(IsOpenProperty); set => SetValue(IsOpenProperty, value); }
    public string CreateLabel { get => (string)GetValue(CreateLabelProperty); set => SetValue(CreateLabelProperty, value); }
    public string ImportLabel { get => (string)GetValue(ImportLabelProperty); set => SetValue(ImportLabelProperty, value); }
    public AppIconKind CreateIcon { get => (AppIconKind)GetValue(CreateIconProperty); set => SetValue(CreateIconProperty, value); }
    public string QuickAddLabel { get => (string)GetValue(QuickAddLabelProperty); set => SetValue(QuickAddLabelProperty, value); }
    public ICommand? QuickAddCommand { get => (ICommand?)GetValue(QuickAddCommandProperty); set => SetValue(QuickAddCommandProperty, value); }
    public event RoutedEventHandler CreateRequested { add => AddHandler(CreateRequestedEvent, value); remove => RemoveHandler(CreateRequestedEvent, value); }
    public event RoutedEventHandler ImportRequested { add => AddHandler(ImportRequestedEvent, value); remove => RemoveHandler(ImportRequestedEvent, value); }
    private Window? _observedWindow;
    private DispatcherOperation? _focusOperation;
    private bool _snap, _keyboardToggle;
    internal Button CreateButton => CreateAction;
    internal Button ImportButton => ImportAction;
    internal Button QuickAddButton => QuickAddAction;
    internal Button LauncherButton => ToggleAction;

    public DashboardCreateMenu()
    {
        InitializeComponent();
        Loaded += (_, _) => { MotionPolicy.Changed += PolicyChanged; CloseImmediately(); };
        Unloaded += (_, _) => { MotionPolicy.Changed -= PolicyChanged; CloseImmediately(); };
        IsVisibleChanged += (_, _) => { if (!IsVisible) CloseImmediately(); };
        IsEnabledChanged += (_, _) => { if (!IsEnabled) CloseImmediately(); };
        DataContextChanged += (_, _) => CloseImmediately();
        PreviewKeyDown += MenuKeyDown;
        ToggleAction.PreviewMouseDown += (_, _) => _keyboardToggle = false;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        // The action stack scrolls only when its wrapped labels exceed the available height.
        // The launcher keeps its bottom anchor and does not resize dashboard content.
        // 52 DIP launcher + 12 DIP gap, less the content's two 16 DIP shadow insets.
        ActionViewport.MaxHeight = double.IsInfinity(constraint.Height) ? double.PositiveInfinity : Math.Max(0, constraint.Height - 32);
        return base.MeasureOverride(constraint);
    }

    private static void OpenChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var menu = (DashboardCreateMenu)target;
        if (menu.ActionPanel is null) return;
        if ((bool)e.NewValue && (!menu.IsLoaded || !menu.IsVisible || !menu.IsEnabled))
        { menu.CloseImmediately(); return; }
        menu.Present(!menu._snap);
        if (UIElementAutomationPeer.FromElement(menu) is CreateMenuPeer peer)
            peer.RaisePropertyChangedEvent(ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty,
                (bool)e.OldValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed,
                (bool)e.NewValue ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed);
    }

    private void ToggleAction_Click(object sender, RoutedEventArgs e)
    {
        SetCurrentValue(IsOpenProperty, !IsOpen);
        if (IsOpen && _keyboardToggle) FocusCreation();
        else if (!IsOpen) ToggleAction.Focus();
        _keyboardToggle = false;
    }

    private void CreateAction_Click(object sender, RoutedEventArgs e)
    {
        CloseImmediately();
        RaiseEvent(new RoutedEventArgs(CreateRequestedEvent, this));
    }
    private void ImportAction_Click(object sender, RoutedEventArgs e)
    {
        CloseImmediately();
        RaiseEvent(new RoutedEventArgs(ImportRequestedEvent, this));
    }

    private void Present(bool animate)
    {
        _focusOperation?.Abort(); _focusOperation = null;
        ObserveWindow(IsOpen);
        bool fresh = ActionPanel.Visibility != Visibility.Visible;
        ActionViewport.IsHitTestVisible = IsOpen;
        ActionPanel.IsEnabled = ActionPanel.IsHitTestVisible = IsOpen;
        if (IsOpen) ActionPanel.Visibility = Visibility.Visible;
        if (fresh && IsOpen)
            foreach (var row in new[] { CreateRow, QuickAddRow, ImportRow })
            {
                row.Opacity = 0;
                ((TranslateTransform)row.RenderTransform).Y = MotionPolicy.Spatial ? 8 : 0;
            }
        if (!animate)
        {
            Settle();
            return;
        }
        var open = IsOpen;
        SurfaceMotion.AnimateTracks(PlusIcon, MotionChannel.Presence,
            [new(IconRotation, RotateTransform.AngleProperty, open ? 45 : 0, true)], 220, UiMotion.InOutSine);
        foreach (var row in new[] { CreateRow, QuickAddRow, ImportRow })
        {
            var translate = (TranslateTransform)row.RenderTransform;
            int index = row == CreateRow ? 0 : row == QuickAddRow ? 1 : 2;
            int delay = MotionPolicy.Spatial ? (open ? index : 2 - index) * MotionTokens.Stagger : 0;
            SurfaceMotion.AnimateTracks(row, MotionChannel.Presence,
                [new(row, OpacityProperty, open ? 1 : 0), new(translate, TranslateTransform.YProperty, open || !MotionPolicy.Spatial ? 0 : 8, true)],
                open ? 220 : 160, UiMotion.InOutSine,
                () => { if (!IsOpen && row == CreateRow) ActionPanel.Visibility = Visibility.Collapsed; }, startDelayMs: delay);
        }
    }

    private void Settle()
    {
        SurfaceMotion.Stop(PlusIcon, MotionChannel.Presence);
        IconRotation.Angle = IsOpen ? 45 : 0;
        foreach (var row in new[] { CreateRow, QuickAddRow, ImportRow })
        {
            SurfaceMotion.Stop(row, MotionChannel.Presence);
            row.Opacity = IsOpen ? 1 : 0;
            ((TranslateTransform)row.RenderTransform).Y = 0;
        }
        ActionPanel.Visibility = IsOpen ? Visibility.Visible : Visibility.Collapsed;
    }
    private void PolicyChanged() => Settle();
    internal void CloseImmediately()
    {
        if (ActionPanel is null) return;
        _snap = true;
        try { SetCurrentValue(IsOpenProperty, false); }
        finally { _snap = false; }
        _focusOperation?.Abort(); _focusOperation = null;
        ObserveWindow(false);
        ActionViewport.IsHitTestVisible = false;
        ActionPanel.IsEnabled = ActionPanel.IsHitTestVisible = false;
        Settle();
    }

    private void ObserveWindow(bool observe)
    {
        var owner = observe ? Window.GetWindow(this) : null;
        if (ReferenceEquals(owner, _observedWindow)) return;
        if (_observedWindow is not null)
        {
            _observedWindow.RemoveHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OutsideMouseDown));
            _observedWindow.RemoveHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OutsideWheel));
            _observedWindow.RemoveHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OutsideFocus));
            _observedWindow.RemoveHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OwnerKeyDown));
            _observedWindow.Deactivated -= OwnerDeactivated;
        }
        _observedWindow = owner;
        if (owner is null) return;
        owner.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OutsideMouseDown), true);
        owner.AddHandler(Mouse.PreviewMouseWheelEvent, new MouseWheelEventHandler(OutsideWheel), true);
        owner.AddHandler(Keyboard.GotKeyboardFocusEvent, new KeyboardFocusChangedEventHandler(OutsideFocus), true);
        owner.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OwnerKeyDown), true);
        owner.Deactivated += OwnerDeactivated;
    }
    private bool Owns(object? source)
    {
        for (var node = source as DependencyObject; node is not null;)
        {
            if (ReferenceEquals(node, this)) return true;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node)
                : node is FrameworkContentElement content ? content.Parent : LogicalTreeHelper.GetParent(node);
        }
        return false;
    }
    private void OutsideMouseDown(object sender, MouseButtonEventArgs e) { if (!Owns(e.OriginalSource)) SetCurrentValue(IsOpenProperty, false); }
    private void OutsideWheel(object sender, MouseWheelEventArgs e) { if (!Owns(e.OriginalSource)) SetCurrentValue(IsOpenProperty, false); }
    private void OutsideFocus(object sender, KeyboardFocusChangedEventArgs e) { if (!Owns(e.NewFocus)) SetCurrentValue(IsOpenProperty, false); }
    private void OwnerDeactivated(object? sender, EventArgs e) => CloseImmediately();
    private void OwnerKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        SetCurrentValue(IsOpenProperty, false); ToggleAction.Focus(); e.Handled = true;
    }

    private void FocusCreation()
    {
        _focusOperation?.Abort();
        _focusOperation = Dispatcher.BeginInvoke(() =>
        { _focusOperation = null; if (IsOpen && IsVisible && CreateAction.IsEnabled) CreateAction.Focus(); }, DispatcherPriority.Input);
    }
    private void MenuKeyDown(object sender, KeyEventArgs e)
    {
        if (e.OriginalSource == ToggleAction && e.Key is Key.Enter or Key.Space) _keyboardToggle = true;
        if (!IsOpen && e.OriginalSource == ToggleAction && e.Key == Key.Up)
        { SetCurrentValue(IsOpenProperty, true); FocusCreation(); e.Handled = true; return; }
        if (!IsOpen) return;
        if (e.Key == Key.Escape)
        { SetCurrentValue(IsOpenProperty, false); ToggleAction.Focus(); e.Handled = true; return; }
        Control? next = e.Key switch
        {
            Key.Up => ToggleAction.IsKeyboardFocused ? CreateAction : CreateAction.IsKeyboardFocused ? QuickAddAction : ImportAction,
            Key.Down => ImportAction.IsKeyboardFocused ? QuickAddAction : QuickAddAction.IsKeyboardFocused ? CreateAction : ToggleAction,
            Key.Home => ImportAction,
            Key.End => CreateAction,
            _ => null
        };
        if (next is not null) { next.Focus(); e.Handled = true; }
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new CreateMenuPeer(this);
    private sealed class CreateMenuPeer(DashboardCreateMenu owner) : UserControlAutomationPeer(owner), IExpandCollapseProvider
    {
        protected override string GetClassNameCore() => nameof(DashboardCreateMenu);
        protected override string GetNameCore() => owner.ToggleAction.ToolTip as string ?? base.GetNameCore();
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.ExpandCollapse ? this : base.GetPattern(patternInterface);
        public ExpandCollapseState ExpandCollapseState => owner.IsOpen ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        public void Expand() { if (!owner.IsEnabled) throw new ElementNotEnabledException(); owner.SetCurrentValue(IsOpenProperty, true); }
        public void Collapse() => owner.SetCurrentValue(IsOpenProperty, false);
    }
}
