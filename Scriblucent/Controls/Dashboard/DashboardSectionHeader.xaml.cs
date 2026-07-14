using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Scriblucent.Behaviors;

namespace Scriblucent.Controls.Dashboard;

public partial class DashboardSectionHeader : UserControl
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(
            nameof(Title),
            typeof(string),
            typeof(DashboardSectionHeader),
            new PropertyMetadata(string.Empty, OnTitleChanged));

    public static readonly DependencyProperty MoveUpToolTipProperty =
        DependencyProperty.Register(
            nameof(MoveUpToolTip),
            typeof(object),
            typeof(DashboardSectionHeader),
            new PropertyMetadata(null, (d, e) => ((DashboardSectionHeader)d).MoveUpButton.ToolTip = e.NewValue));

    public static readonly DependencyProperty MoveDownToolTipProperty =
        DependencyProperty.Register(
            nameof(MoveDownToolTip),
            typeof(object),
            typeof(DashboardSectionHeader),
            new PropertyMetadata(null, (d, e) => ((DashboardSectionHeader)d).MoveDownButton.ToolTip = e.NewValue));

    public static readonly DependencyProperty ToggleToolTipProperty =
        DependencyProperty.Register(
            nameof(ToggleToolTip),
            typeof(object),
            typeof(DashboardSectionHeader),
            new PropertyMetadata(null, (d, e) => ((DashboardSectionHeader)d).ToggleButton.ToolTip = e.NewValue));

    public static readonly DependencyProperty IsExpandedProperty =
        DependencyProperty.Register(
            nameof(IsExpanded),
            typeof(bool),
            typeof(DashboardSectionHeader),
            new PropertyMetadata(true, OnIsExpandedChanged));

    public static readonly DependencyProperty ShowMoveButtonsProperty =
        DependencyProperty.Register(
            nameof(ShowMoveButtons),
            typeof(bool),
            typeof(DashboardSectionHeader),
            new PropertyMetadata(true, OnShowMoveButtonsChanged));

    public static readonly DependencyProperty TrailingContentProperty =
        DependencyProperty.Register(
            nameof(TrailingContent),
            typeof(object),
            typeof(DashboardSectionHeader),
            new PropertyMetadata(null, (d, e) => ((DashboardSectionHeader)d).TrailingContentPresenter.Content = e.NewValue));

    public static readonly DependencyProperty MoveUpCommandProperty =
        DependencyProperty.Register(nameof(MoveUpCommand), typeof(ICommand), typeof(DashboardSectionHeader));

    public static readonly DependencyProperty MoveDownCommandProperty =
        DependencyProperty.Register(nameof(MoveDownCommand), typeof(ICommand), typeof(DashboardSectionHeader));

    public static readonly DependencyProperty ToggleCommandProperty =
        DependencyProperty.Register(nameof(ToggleCommand), typeof(ICommand), typeof(DashboardSectionHeader));

    public static readonly RoutedEvent MoveUpEvent =
        EventManager.RegisterRoutedEvent(nameof(MoveUp), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DashboardSectionHeader));

    public static readonly RoutedEvent MoveDownEvent =
        EventManager.RegisterRoutedEvent(nameof(MoveDown), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DashboardSectionHeader));

    public static readonly RoutedEvent ToggleEvent =
        EventManager.RegisterRoutedEvent(nameof(Toggle), RoutingStrategy.Bubble, typeof(RoutedEventHandler), typeof(DashboardSectionHeader));

    public DashboardSectionHeader()
    {
        InitializeComponent();
        SectionChevronMotionBehavior.SetIsExpanded(ToggleButton, IsExpanded);
    }

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public object? MoveUpToolTip
    {
        get => GetValue(MoveUpToolTipProperty);
        set => SetValue(MoveUpToolTipProperty, value);
    }

    public object? MoveDownToolTip
    {
        get => GetValue(MoveDownToolTipProperty);
        set => SetValue(MoveDownToolTipProperty, value);
    }

    public object? ToggleToolTip
    {
        get => GetValue(ToggleToolTipProperty);
        set => SetValue(ToggleToolTipProperty, value);
    }

    public bool IsExpanded
    {
        get => (bool)GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public bool ShowMoveButtons
    {
        get => (bool)GetValue(ShowMoveButtonsProperty);
        set => SetValue(ShowMoveButtonsProperty, value);
    }

    public object? TrailingContent
    {
        get => GetValue(TrailingContentProperty);
        set => SetValue(TrailingContentProperty, value);
    }

    public ICommand? MoveUpCommand
    {
        get => (ICommand?)GetValue(MoveUpCommandProperty);
        set => SetValue(MoveUpCommandProperty, value);
    }

    public ICommand? MoveDownCommand
    {
        get => (ICommand?)GetValue(MoveDownCommandProperty);
        set => SetValue(MoveDownCommandProperty, value);
    }

    public ICommand? ToggleCommand
    {
        get => (ICommand?)GetValue(ToggleCommandProperty);
        set => SetValue(ToggleCommandProperty, value);
    }

    public event RoutedEventHandler MoveUp
    {
        add => AddHandler(MoveUpEvent, value);
        remove => RemoveHandler(MoveUpEvent, value);
    }

    public event RoutedEventHandler MoveDown
    {
        add => AddHandler(MoveDownEvent, value);
        remove => RemoveHandler(MoveDownEvent, value);
    }

    public event RoutedEventHandler Toggle
    {
        add => AddHandler(ToggleEvent, value);
        remove => RemoveHandler(ToggleEvent, value);
    }

    private static void OnTitleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DashboardSectionHeader header)
            header.TitleText.Text = e.NewValue as string ?? string.Empty;
    }

    private static void OnIsExpandedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is DashboardSectionHeader header)
            SectionChevronMotionBehavior.SetIsExpanded(header.ToggleButton, (bool)e.NewValue);
    }

    private static void OnShowMoveButtonsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not DashboardSectionHeader header)
            return;

        var visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        header.MoveUpButton.Visibility = visibility;
        header.MoveDownButton.Visibility = visibility;
    }

    private void MoveUpButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoveUpCommand?.CanExecute(null) == true)
            MoveUpCommand.Execute(null);
        RaiseEvent(new RoutedEventArgs(MoveUpEvent, this));
    }

    private void MoveDownButton_Click(object sender, RoutedEventArgs e)
    {
        if (MoveDownCommand?.CanExecute(null) == true)
            MoveDownCommand.Execute(null);
        RaiseEvent(new RoutedEventArgs(MoveDownEvent, this));
    }

    private void ToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (ToggleCommand?.CanExecute(null) == true)
            ToggleCommand.Execute(null);
        RaiseEvent(new RoutedEventArgs(ToggleEvent, this));
    }
}
