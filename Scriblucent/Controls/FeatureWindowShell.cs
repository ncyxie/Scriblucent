using System.Windows;
using System.Windows.Controls;

namespace Scriblucent.Controls;

public class FeatureWindowShell : Control
{
    static FeatureWindowShell()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(FeatureWindowShell),
            new FrameworkPropertyMetadata(typeof(FeatureWindowShell)));
    }

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.Register(nameof(Header), typeof(object), typeof(FeatureWindowShell));

    public static readonly DependencyProperty BodyProperty =
        DependencyProperty.Register(nameof(Body), typeof(object), typeof(FeatureWindowShell));

    public static readonly DependencyProperty FooterProperty =
        DependencyProperty.Register(nameof(Footer), typeof(object), typeof(FeatureWindowShell));

    public object Header
    {
        get => GetValue(HeaderProperty);
        set => SetValue(HeaderProperty, value);
    }

    public object Body
    {
        get => GetValue(BodyProperty);
        set => SetValue(BodyProperty, value);
    }

    public object Footer
    {
        get => GetValue(FooterProperty);
        set => SetValue(FooterProperty, value);
    }
}
