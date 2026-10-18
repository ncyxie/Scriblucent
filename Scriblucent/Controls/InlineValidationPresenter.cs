using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using Scriblucent.Animations;

namespace Scriblucent.Controls;

/// <summary>One reserved helper line; wrapped errors expand locally without blocking form input.</summary>
public sealed class InlineValidationPresenter : LayoutRevealHost
{
    private readonly TextBlock _label;
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string),
        typeof(InlineValidationPresenter), new PropertyMetadata(string.Empty, Changed));
    public static readonly DependencyProperty IsErrorProperty = DependencyProperty.Register(nameof(IsError), typeof(bool),
        typeof(InlineValidationPresenter), new PropertyMetadata(false, Changed));
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public bool IsError { get => (bool)GetValue(IsErrorProperty); set => SetValue(IsErrorProperty, value); }

    public InlineValidationPresenter()
    {
        IsHitTestVisible = false;
        _label = new TextBlock { MinHeight = 18, TextWrapping = TextWrapping.Wrap, Opacity = 0 };
        _label.SetResourceReference(TextBlock.FontFamilyProperty, "FontFamily.UI");
        _label.SetResourceReference(TextBlock.FontSizeProperty, "Type.Control.Helper");
        _label.SetResourceReference(TextBlock.ForegroundProperty, "AccentDangerSubtleForeground");
        Child = _label;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
        Unloaded += (_, _) =>
        {
            SurfaceMotion.Stop(this, MotionChannel.Presence);
            _label.Text = IsError ? Text : string.Empty;
            _label.Opacity = IsError ? 1 : 0;
        };
    }

    public void Show(string? message)
    {
        if (!string.IsNullOrEmpty(message)) SetCurrentValue(TextProperty, message);
        SetCurrentValue(IsErrorProperty, !string.IsNullOrEmpty(message));
    }
    public void Hide() => SetCurrentValue(IsErrorProperty, false);

    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var presenter = (InlineValidationPresenter)target;
        if (e.Property == TextProperty)
        {
            if (presenter.IsError) presenter.ChangeContent(() => presenter._label.Text = presenter.Text);
            return;
        }
        if (presenter.IsError) presenter.ChangeContent(() => presenter._label.Text = presenter.Text);
        SurfaceMotion.AnimateTracks(presenter, MotionChannel.Presence,
            [new(presenter._label, OpacityProperty, presenter.IsError ? 1 : 0)], 140, UiMotion.InOutSine,
            () => { if (!presenter.IsError) presenter.ChangeContent(() => presenter._label.Text = string.Empty); });
        if (presenter.IsError && UIElementAutomationPeer.FromElement(presenter) is { } peer)
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new ValidationPeer(this);
    private sealed class ValidationPeer(InlineValidationPresenter owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(InlineValidationPresenter);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetNameCore() => owner.IsError ? owner.Text : string.Empty;
    }
}
