using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Scriblucent.Animations;
using Scriblucent.Localization;

namespace Scriblucent.Controls;

public partial class UpdateAvailableNotice : UserControl
{
    private bool _shown;
    public event EventHandler? ViewUpdateRequested;
    public event EventHandler? DismissRequested;

    public UpdateAvailableNotice()
    {
        InitializeComponent();
        Loaded += (_, _) => MotionPolicy.Changed += FinishMotion;
        Unloaded += (_, _) => { MotionPolicy.Changed -= FinishMotion; FinishMotion(); };
    }

    public void SetVersion(string version)
    {
        VersionText.Text = string.Format(LocalizationService.GetString("UpdateAvailableVersionFormat"), version);
    }

    public void SetShown(bool shown)
    {
        if (_shown == shown) return;
        _shown = shown;
        IsHitTestVisible = shown;
        var from = Visibility == Visibility.Visible ? Opacity : 0;
        BeginAnimation(OpacityProperty, null);
        NoticeTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        var duration = MotionPolicy.FadeDuration(shown ? UiMotion.OpenMs : UiMotion.CloseMs);
        if (duration == 0) { FinishMotion(); return; }
        Visibility = Visibility.Visible;
        Opacity = shown ? 1 : 0;
        var fade = new DoubleAnimation(from, shown ? 1 : 0, TimeSpan.FromMilliseconds(duration))
        {
            EasingFunction = UiMotion.OutSine, FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) => { if (_shown == shown) FinishMotion(); };
        BeginAnimation(OpacityProperty, fade);
        if (shown && MotionPolicy.Spatial)
            NoticeTranslation.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(4, 0, TimeSpan.FromMilliseconds(duration))
                { EasingFunction = UiMotion.OutSine, FillBehavior = FillBehavior.Stop });
    }

    private void FinishMotion()
    {
        BeginAnimation(OpacityProperty, null);
        NoticeTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        NoticeTranslation.Y = 0;
        Opacity = 1;
        Visibility = _shown ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ViewUpdate_Click(object sender, RoutedEventArgs e) => ViewUpdateRequested?.Invoke(this, EventArgs.Empty);
    private void Dismiss_Click(object sender, RoutedEventArgs e) => DismissRequested?.Invoke(this, EventArgs.Empty);
}
