using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Scriblucent.Icons;

namespace Scriblucent.Controls.Dashboard;

public partial class EmptyDashboardPanel : UserControl
{
    public static readonly DependencyProperty PromptProperty =
        DependencyProperty.Register(
            nameof(Prompt),
            typeof(string),
            typeof(EmptyDashboardPanel),
            new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ButtonContentProperty =
        DependencyProperty.Register(
            nameof(ButtonContent),
            typeof(object),
            typeof(EmptyDashboardPanel));

    public static readonly DependencyProperty CommandProperty =
        DependencyProperty.Register(
            nameof(Command),
            typeof(ICommand),
            typeof(EmptyDashboardPanel));

    public static readonly DependencyProperty IconKindProperty =
        DependencyProperty.Register(
            nameof(IconKind),
            typeof(AppIconKind),
            typeof(EmptyDashboardPanel),
            new PropertyMetadata(AppIconKind.None, OnIconKindChanged));

    public static readonly RoutedEvent ClickEvent =
        EventManager.RegisterRoutedEvent(
            nameof(Click),
            RoutingStrategy.Bubble,
            typeof(RoutedEventHandler),
            typeof(EmptyDashboardPanel));

    public EmptyDashboardPanel()
    {
        InitializeComponent();
        PromptText.SetBinding(TextBlock.TextProperty, new Binding(nameof(Prompt)) { Source = this });
        PrimaryButton.SetBinding(ContentControl.ContentProperty, new Binding(nameof(ButtonContent)) { Source = this });
        PrimaryButton.SetBinding(Button.CommandProperty, new Binding(nameof(Command)) { Source = this });
        ApplyIconKind();
    }

    public string Prompt
    {
        get => (string)GetValue(PromptProperty);
        set => SetValue(PromptProperty, value);
    }

    public object? ButtonContent
    {
        get => GetValue(ButtonContentProperty);
        set => SetValue(ButtonContentProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    public AppIconKind IconKind
    {
        get => (AppIconKind)GetValue(IconKindProperty);
        set => SetValue(IconKindProperty, value);
    }

    public event RoutedEventHandler Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    private static void OnIconKindChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is EmptyDashboardPanel panel)
            panel.ApplyIconKind();
    }

    private void ApplyIconKind()
    {
        if (GlyphIcon is null || GlyphChip is null)
            return;

        var kind = IconKind;
        GlyphIcon.Kind = kind;
        GlyphChip.Visibility = kind == AppIconKind.None ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(ClickEvent, this));
    }
}
