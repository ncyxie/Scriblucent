using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

public partial class CardShadowLayer : UserControl
{
    public CardShadowLayer()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        if (FindName("ShadowRoot") is FrameworkElement shadowRoot)
            shadowRoot.CacheMode = DpiAwareBitmapCache.Create(
                shadowRoot,
                enableClearType: false,
                snapsToDevicePixels: false);
    }
}
