using Scriblucent.Animations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Scriblucent.Controls;

/// <summary>
/// Dual-face flashcard container with a centered vertical flip driven by <see cref="UiMotion"/>.
/// </summary>
public partial class FlashcardFlipHost : UserControl
{
    private const double FrontScale = 1.0;
    private const double BackScale = -1.0;
    private const double EdgeThreshold = 0.001;
    private const double ExtentEpsilon = 0.5;

    public static readonly DependencyProperty FrontContentProperty =
        DependencyProperty.Register(
            nameof(FrontContent),
            typeof(UIElement),
            typeof(FlashcardFlipHost),
            new PropertyMetadata(null, OnFaceContentChanged));

    public static readonly DependencyProperty BackContentProperty =
        DependencyProperty.Register(
            nameof(BackContent),
            typeof(UIElement),
            typeof(FlashcardFlipHost),
            new PropertyMetadata(null, OnFaceContentChanged));

    public static readonly DependencyProperty IsFlippedProperty =
        DependencyProperty.Register(
            nameof(IsFlipped),
            typeof(bool),
            typeof(FlashcardFlipHost),
            new PropertyMetadata(false, OnIsFlippedChanged));

    private bool _suppressFlipSync;
    private bool _isAnimating;
    private double _lastExtentWidth;
    private double _lastExtentHeight;
    private readonly List<(CardShadowLayer Layer, double Opacity)> _hiddenPlates = new();

    public FlashcardFlipHost()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyFlippedVisual(IsFlipped);
        SizeChanged += OnSizeChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                ApplyFlippedVisual(IsFlipped);
        };
    }

    public UIElement? FrontContent
    {
        get => (UIElement?)GetValue(FrontContentProperty);
        set => SetValue(FrontContentProperty, value);
    }

    public UIElement? BackContent
    {
        get => (UIElement?)GetValue(BackContentProperty);
        set => SetValue(BackContentProperty, value);
    }

    public bool IsFlipped
    {
        get => (bool)GetValue(IsFlippedProperty);
        set => SetValue(IsFlippedProperty, value);
    }

    public bool IsAnimating => _isAnimating;

    public void AnimateToggle(int durationMs, Action? onComplete = null)
    {
        if (_isAnimating)
            return;

        if (!EnsureExtents())
            return;

        var targetFlipped = !IsFlipped;
        if (UiMotion.PrefersReducedMotion)
        {
            CommitFlipState(targetFlipped);
            onComplete?.Invoke();
            return;
        }

        _isAnimating = true;
        var fromScale = IsFlipped ? BackScale : FrontScale;
        var toScale = targetFlipped ? BackScale : FrontScale;
        var duration = Math.Max(UiMotion.FlashcardFlipMinMs, durationMs);

        UiMotion.Stop(this);
        UiMotion.Clear(FlipScale, ScaleTransform.ScaleYProperty);
        DashboardShadowSuspender.TrySuspend(this, FrontHost);
        DashboardShadowSuspender.TrySuspend(this, BackFace);
        HideBakedShadowPlates();
        ApplyScale(fromScale);

        UiMotion.RunComposited(
            this,
            new[]
            {
                UiMotion.Track(
                    duration,
                    UiMotion.InOutSine,
                    fromScale,
                    toScale,
                    ApplyScale)
            },
            () =>
            {
                DashboardShadowSuspender.TryResume(this);
                RestoreBakedShadowPlates();
                ApplyScale(toScale);
                CommitFlipState(targetFlipped);
                _isAnimating = false;
                onComplete?.Invoke();
            });
    }

    /// <summary>
    /// Nine-slice plates distort under ScaleY flip; hide for the animation only (live Effects
    /// are handled by <see cref="DashboardShadowSuspender"/>).
    /// </summary>
    private void HideBakedShadowPlates()
    {
        RestoreBakedShadowPlates();
        CollectBakedShadowPlates(FrontHost);
        CollectBakedShadowPlates(BackFace);
        foreach (var (layer, _) in _hiddenPlates)
            layer.Opacity = 0;
    }

    private void CollectBakedShadowPlates(DependencyObject? root)
    {
        if (root is null)
            return;

        if (root is CardShadowLayer layer)
            _hiddenPlates.Add((layer, layer.Opacity));

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            CollectBakedShadowPlates(VisualTreeHelper.GetChild(root, i));
    }

    private void RestoreBakedShadowPlates()
    {
        foreach (var (layer, opacity) in _hiddenPlates)
            layer.Opacity = opacity;
        _hiddenPlates.Clear();
    }

    private void CommitFlipState(bool flipped)
    {
        _suppressFlipSync = true;
        IsFlipped = flipped;
        _suppressFlipSync = false;
        ApplyFlippedVisual(flipped);
    }

    private void ApplyFlippedVisual(bool flipped)
    {
        if (!EnsureExtents())
            return;

        UiMotion.Stop(this);
        UiMotion.Clear(FlipScale, ScaleTransform.ScaleYProperty);
        ApplyScale(flipped ? BackScale : FrontScale);
    }

    /// <summary>
    /// Clears every piece of per-instance state (in-flight animation, hidden shadow plates,
    /// cached extents) before a virtualizing panel recycles this container for a different item.
    /// None of <see cref="Loaded"/>/<see cref="OnSizeChanged"/>/<c>IsVisibleChanged</c> fire on
    /// recycle, so without this a reused container can inherit a stale mid-flip or zeroed-opacity
    /// state from whatever card it previously displayed.
    /// </summary>
    internal void PrepareForReuse()
    {
        RestoreBakedShadowPlates();
        _isAnimating = false;
        _suppressFlipSync = false;
        ResetExtents();
        ApplyFlippedVisual(IsFlipped);
    }

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_isAnimating)
            return;

        if (Math.Abs(e.NewSize.Width - _lastExtentWidth) > ExtentEpsilon
            || Math.Abs(e.NewSize.Height - _lastExtentHeight) > ExtentEpsilon)
        {
            ApplyFlippedVisual(IsFlipped);
        }
    }

    private bool EnsureExtents()
    {
        var constraint = GetMeasureConstraint();
        var height = ResolveExtentHeight(constraint);
        if (height <= 0)
            return false;

        var width = ResolveExtentWidth(constraint, height);
        var stretchHorizontally = ShouldStretchHorizontally(width);

        if (Math.Abs(_lastExtentWidth - width) <= ExtentEpsilon
            && Math.Abs(_lastExtentHeight - height) <= ExtentEpsilon
            && (!stretchHorizontally || Math.Abs(ActualWidth - _lastExtentWidth) <= ExtentEpsilon || ActualWidth <= 0))
        {
            return FlipHost.Height > 0;
        }

        FlipHost.Height = height;
        FlipHost.Width = stretchHorizontally ? double.NaN : width > 0 ? width : double.NaN;
        FlipHost.HorizontalAlignment = stretchHorizontally
            ? HorizontalAlignment.Stretch
            : HorizontalAlignment.Left;
        FlipHost.VerticalAlignment = VerticalAlignment.Stretch;

        _lastExtentWidth = stretchHorizontally && ActualWidth > 0 ? ActualWidth : width;
        _lastExtentHeight = height;
        UpdateFaceVisibility(FlipScale.ScaleY);
        return true;
    }

    private Size GetMeasureConstraint()
    {
        var width = Width > 0
            ? Width
            : ActualWidth > 0
                ? ActualWidth
                : Parent is FrameworkElement parent && parent.ActualWidth > 0
                    ? parent.ActualWidth
                    : double.PositiveInfinity;

        var height = Height > 0
            ? Height
            : ActualHeight > 0
                ? ActualHeight
                : Parent is FrameworkElement parentElement && parentElement.ActualHeight > 0
                    ? parentElement.ActualHeight
                    : double.PositiveInfinity;

        return new Size(width, height);
    }

    private double ResolveExtentHeight(Size constraint)
    {
        if (Height > 0)
            return Height;

        if (ActualHeight > 0)
            return ActualHeight;

        if (!double.IsInfinity(constraint.Height) && constraint.Height > 0)
            return constraint.Height;

        var front = MeasureFace(FrontContent, constraint);
        var back = MeasureFace(BackContent, constraint);
        return Math.Max(front.Height, back.Height);
    }

    private double ResolveExtentWidth(Size constraint, double height)
    {
        if (Width > 0)
            return Width;

        if (ActualWidth > 0)
            return ActualWidth;

        if (!double.IsInfinity(constraint.Width) && constraint.Width > 0)
            return constraint.Width;

        var faceConstraint = new Size(
            constraint.Width,
            double.IsInfinity(constraint.Height) ? height : constraint.Height);
        var front = MeasureFace(FrontContent, faceConstraint);
        var back = MeasureFace(BackContent, faceConstraint);
        return Math.Max(front.Width, back.Width);
    }

    private bool ShouldStretchHorizontally(double resolvedWidth)
        => HorizontalAlignment == HorizontalAlignment.Stretch
           && Width <= 0
           && (ActualWidth > 0 || resolvedWidth <= 0 || double.IsInfinity(resolvedWidth));

    private static Size MeasureFace(UIElement? content, Size constraint)
    {
        if (content is ContentControl presenter)
        {
            presenter.ApplyTemplate();
            presenter.Measure(constraint);
            if (presenter.DesiredSize.Width > 1 && presenter.DesiredSize.Height > 1)
                return presenter.DesiredSize;

            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(presenter); i++)
            {
                if (VisualTreeHelper.GetChild(presenter, i) is FrameworkElement child)
                {
                    child.Measure(constraint);
                    if (child.DesiredSize.Width > 1 && child.DesiredSize.Height > 1)
                        return child.DesiredSize;
                }
            }

            if (presenter.Content is FrameworkElement direct)
            {
                direct.Measure(constraint);
                return direct.DesiredSize;
            }

            return presenter.DesiredSize;
        }

        if (content is not FrameworkElement element)
            return default;

        if (element.Width > 0 && element.Height > 0)
            return new Size(element.Width, element.Height);

        element.Measure(constraint);
        return element.DesiredSize;
    }

    private void ApplyScale(double scaleY)
    {
        FlipScale.ScaleY = scaleY;
        UpdateFaceVisibility(scaleY);
    }

    private void UpdateFaceVisibility(double scaleY)
    {
        if (Math.Abs(scaleY) <= EdgeThreshold)
        {
            FrontHost.Visibility = Visibility.Hidden;
            BackFace.Visibility = Visibility.Hidden;
            return;
        }

        if (scaleY > 0)
        {
            FrontHost.Visibility = Visibility.Visible;
            BackFace.Visibility = Visibility.Hidden;
            return;
        }

        FrontHost.Visibility = Visibility.Hidden;
        BackFace.Visibility = Visibility.Visible;
    }

    private void ResetExtents()
    {
        _lastExtentWidth = 0;
        _lastExtentHeight = 0;
    }

    private static void OnFaceContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FlashcardFlipHost control)
            return;

        control.ResetExtents();
        if (control.IsLoaded)
            control.ApplyFlippedVisual(control.IsFlipped);
    }

    private static void OnIsFlippedChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FlashcardFlipHost control || control._suppressFlipSync || control._isAnimating)
            return;

        control.ApplyFlippedVisual((bool)e.NewValue);
    }
}