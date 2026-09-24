using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Controls;

public partial class AiReviewNotice : UserControl
{
    public static readonly DependencyProperty AllowDismissProperty = DependencyProperty.Register(
        nameof(AllowDismiss),
        typeof(bool),
        typeof(AiReviewNotice),
        new PropertyMetadata(false, OnAllowDismissChanged));

    public static readonly RoutedEvent DismissedEvent = EventManager.RegisterRoutedEvent(
        nameof(Dismissed),
        RoutingStrategy.Bubble,
        typeof(RoutedEventHandler),
        typeof(AiReviewNotice));

    public AiReviewNotice()
    {
        InitializeComponent();
    }

    public bool AllowDismiss
    {
        get => (bool)GetValue(AllowDismissProperty);
        set => SetValue(AllowDismissProperty, value);
    }

    public event RoutedEventHandler Dismissed
    {
        add => AddHandler(DismissedEvent, value);
        remove => RemoveHandler(DismissedEvent, value);
    }

    private static void OnAllowDismissChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        if (sender is AiReviewNotice notice)
            notice.DismissButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
    }

    private void DismissButton_Click(object sender, RoutedEventArgs e)
    {
        RaiseEvent(new RoutedEventArgs(DismissedEvent, this));
    }
}
