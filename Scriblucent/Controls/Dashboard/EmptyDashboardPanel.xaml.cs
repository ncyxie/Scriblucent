using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

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

    public event RoutedEventHandler Click
    {
        add => AddHandler(ClickEvent, value);
        remove => RemoveHandler(ClickEvent, value);
    }

    private void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(ClickEvent, this));
    }
}
