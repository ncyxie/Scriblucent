using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Scriblucent.Localization;

namespace Scriblucent.Controls;

/// <summary>
/// The selection chrome of an "in line with text" photo - the corner resize handles and the
/// aspect-lock/delete chips. It lives on the floating-image OVERLAY canvas (like the floating
/// widget's chrome), NOT inside the text flow: the overlay is never clipped by the text content
/// box, so the handles always meet the photo's corners - even when the photo sits on the
/// editor's first row. The coordinator owns the chrome's position, size and visibility.
/// </summary>
public partial class InlineImageChrome : UserControl
{
    public event EventHandler<string>? ResizeStarted;
    public event EventHandler<(double X, double Y)>? ResizeDelta;
    public event EventHandler? ResizeCompleted;
    public event EventHandler? LockToggled;
    public event EventHandler? DeleteRequested;

    public static readonly DependencyProperty LockAspectRatioProperty = DependencyProperty.Register(
        nameof(LockAspectRatio), typeof(bool), typeof(InlineImageChrome),
        new PropertyMetadata(true, (d, _) => ((InlineImageChrome)d).OnLockAspectRatioChanged()));

    public static readonly DependencyProperty ButtonsFlippedProperty = DependencyProperty.Register(
        nameof(ButtonsFlipped), typeof(bool), typeof(InlineImageChrome),
        new PropertyMetadata(false, (d, _) => ((InlineImageChrome)d).ApplyButtonPlacement()));

    public bool LockAspectRatio
    {
        get => (bool)GetValue(LockAspectRatioProperty);
        set => SetValue(LockAspectRatioProperty, value);
    }

    /// <summary>When the photo is pressed against the editor's top edge, the chips (which
    /// normally sit above the photo) flip below it - same as the floating widget.</summary>
    public bool ButtonsFlipped
    {
        get => (bool)GetValue(ButtonsFlippedProperty);
        set => SetValue(ButtonsFlippedProperty, value);
    }

    private string _activeHandle = string.Empty;

    public InlineImageChrome()
    {
        InitializeComponent();
        UpdateLockAspectChrome();
    }

    private void OnLockAspectRatioChanged() => UpdateLockAspectChrome();

    /// <summary>Mirrors the floating widget's lock chip: active style while locked, neutral
    /// otherwise, and the tooltip names the action the next click takes.</summary>
    private void UpdateLockAspectChrome()
    {
        if (LockAspectButton is null)
            return;

        LockAspectButton.ToolTip = LocalizationService.GetString(
            LockAspectRatio ? "UnlockAspectRatio" : "LockAspectRatio");

        if (LockAspectRatio)
        {
            LockAspectButton.SetResourceReference(Control.BackgroundProperty, "EditorToolActiveBackground");
            LockAspectButton.SetResourceReference(Control.BorderBrushProperty, "EditorToolActiveBorder");
            LockAspectButton.SetResourceReference(Control.ForegroundProperty, "EditorToolActiveForeground");
            LockAspectButton.BorderThickness = new Thickness(2);
        }
        else
        {
            LockAspectButton.SetResourceReference(Control.BackgroundProperty, "IconButtonBackground");
            LockAspectButton.BorderBrush = Brushes.Transparent;
            LockAspectButton.SetResourceReference(Control.ForegroundProperty, "IconButtonForeground");
            LockAspectButton.BorderThickness = new Thickness(0);
        }
    }

    private void ApplyButtonPlacement()
    {
        if (LockAspectButton is null || DeleteButton is null)
            return;

        LockAspectButton.VerticalAlignment = ButtonsFlipped ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        LockAspectButton.Margin = ButtonsFlipped
            ? new Thickness(0, 0, 44, -34)
            : new Thickness(0, -34, 44, 0);
        DeleteButton.VerticalAlignment = ButtonsFlipped ? VerticalAlignment.Bottom : VerticalAlignment.Top;
        DeleteButton.Margin = ButtonsFlipped
            ? new Thickness(0, 0, 12, -34)
            : new Thickness(0, -34, 12, 0);
    }

    private void LockAspectButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        LockToggled?.Invoke(this, EventArgs.Empty);
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        DeleteRequested?.Invoke(this, EventArgs.Empty);
    }

    private void ResizeThumb_DragStarted(object sender, DragStartedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag || tag.Length == 0)
            return;

        _activeHandle = tag;
        ResizeStarted?.Invoke(this, tag);
    }

    private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (_activeHandle.Length == 0)
            return;

        // Raw per-event deltas. NOTE: WPF's Thumb reports these relative to the thumb's own
        // position - the coordinator resolves the per-axis semantics (a flow-pinned handle
        // reports totals, a tracking handle reports increments) and accumulates accordingly.
        ResizeDelta?.Invoke(this, (e.HorizontalChange, e.VerticalChange));
    }

    private void ResizeThumb_DragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (_activeHandle.Length == 0)
            return;

        _activeHandle = string.Empty;
        ResizeCompleted?.Invoke(this, EventArgs.Empty);
    }
}
