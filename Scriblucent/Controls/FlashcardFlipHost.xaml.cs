using Scriblucent.Animations;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

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
        Unloaded += (_, _) => { UiMotion.Cleanup(this); StopFaceTimelines(); RestoreBakedShadowPlates(); DashboardShadowSuspender.TryResume(this); _isAnimating = false; };
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

    public static readonly DependencyProperty DeferHiddenFaceLayoutProperty = DependencyProperty.Register(
        nameof(DeferHiddenFaceLayout), typeof(bool), typeof(FlashcardFlipHost), new PropertyMetadata(false, (d, _) =>
        {
            var host = (FlashcardFlipHost)d;
            if (host.IsInitialized && !host._isAnimating)
            {
                host.UpdateFaceVisibility(host.IsFlipped ? BackScale : FrontScale);
                host.StableFlipShadow.Visibility = host.DeferHiddenFaceLayout ? Visibility.Collapsed : Visibility.Hidden;
            }
        }));
    public bool DeferHiddenFaceLayout
    {
        get => (bool)GetValue(DeferHiddenFaceLayoutProperty);
        set => SetValue(DeferHiddenFaceLayoutProperty, value);
    }
    private bool _preparingFaces;

    public void AnimateToggle(int durationMs, Action? onComplete = null)
    {
        if (_isAnimating)
            return;

        _preparingFaces = true;
        try
        {
            if (!EnsureExtents()) return;
            if (DeferHiddenFaceLayout)
            {
                var inactive = IsFlipped ? (FrameworkElement)FrontHost : BackFace;
                inactive.Visibility = Visibility.Hidden;
                var size = new Size(_lastExtentWidth, _lastExtentHeight);
                inactive.Measure(size); inactive.Arrange(new Rect(size));
            }
        }
        finally { _preparingFaces = false; }

        var targetFlipped = !IsFlipped;
        if (UiMotion.IsMotionDisabled)
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

        BeginFaceTimelines(duration, targetFlipped);
        SurfaceMotion.AnimateTracks(this, MotionChannel.Showcase,
            [new(FlipScale, ScaleTransform.ScaleYProperty, toScale, true)], duration, UiMotion.InOutSine,
            () =>
            {
                StopFaceTimelines();
                DashboardShadowSuspender.TryResume(this); RestoreBakedShadowPlates();
                ApplyScale(toScale); CommitFlipState(targetFlipped);
                _isAnimating = false; onComplete?.Invoke();
            });
    }

    private void BeginFaceTimelines(int duration, bool targetFlipped)
    {
        AnimationClock VisibilityClock(Visibility from, Visibility to)
        {
            var animation = new ObjectAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(duration), FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new DiscreteObjectKeyFrame(to, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * .5))));
            return animation.CreateClock();
        }
        var frontVisibilityClock = VisibilityClock(IsFlipped ? Visibility.Hidden : Visibility.Visible,
            targetFlipped ? Visibility.Hidden : Visibility.Visible);
        var backVisibilityClock = VisibilityClock(IsFlipped ? Visibility.Visible : Visibility.Hidden,
            targetFlipped ? Visibility.Visible : Visibility.Hidden);
        FrontHost.ApplyAnimationClock(UIElement.VisibilityProperty, frontVisibilityClock);
        BackFace.ApplyAnimationClock(UIElement.VisibilityProperty, backVisibilityClock);
        if (!MotionPolicy.ExpressiveExtras) return;
        AnimationClock DepthClock(double rest, double middle)
        {
            var animation = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
            animation.KeyFrames.Add(new DiscreteDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(middle, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration * .5)), UiMotion.InOutSine));
            animation.KeyFrames.Add(new EasingDoubleKeyFrame(rest, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(duration)), UiMotion.InOutSine));
            return animation.CreateClock();
        }
        FlipShade.ApplyAnimationClock(UIElement.OpacityProperty, DepthClock(0, .12));
        var shadow = StableFlipShadow.Opacity;
        StableFlipShadow.ApplyAnimationClock(UIElement.OpacityProperty, DepthClock(shadow, shadow * .7));
    }

    private void StopFaceTimelines()
    {
        FrontHost.ApplyAnimationClock(UIElement.VisibilityProperty, null);
        BackFace.ApplyAnimationClock(UIElement.VisibilityProperty, null);
        FlipShade.ApplyAnimationClock(UIElement.OpacityProperty, null);
        StableFlipShadow.ApplyAnimationClock(UIElement.OpacityProperty, null);
        FlipShade.Opacity = 0;
    }

    /// <summary>
    /// Replace the two face shadows with one shadow inside the flip transform, so it
    /// contracts with the face instead of exposing a full-size shaded rectangle behind it.
    /// Live Effects are handled by <see cref="DashboardShadowSuspender"/>.
    /// </summary>
    private void HideBakedShadowPlates()
    {
        RestoreBakedShadowPlates();
        CollectBakedShadowPlates(FrontHost);
        CollectBakedShadowPlates(BackFace);
        foreach (var (layer, _) in _hiddenPlates)
            layer.Opacity = 0;
        if (_hiddenPlates.Count > 0)
        {
            StableFlipShadow.Opacity = _hiddenPlates[0].Opacity;
            StableFlipShadow.Visibility = Visibility.Visible;
        }
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
        StableFlipShadow.Visibility = DeferHiddenFaceLayout ? Visibility.Collapsed : Visibility.Hidden;
        FlipShade.Opacity = 0;
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
        UiMotion.Stop(this);
        StopFaceTimelines();
        RestoreBakedShadowPlates();
        DashboardShadowSuspender.TryResume(this);
        _isAnimating = false;
        if (!EnsureExtents())
            return;

        UiMotion.Stop(this);
        UiMotion.Clear(FlipScale, ScaleTransform.ScaleYProperty);
        ApplyScale(flipped ? BackScale : FrontScale);
        CollectionSelectionFrame.For(this)?.RefreshFace();
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
        UiMotion.Cleanup(this);
        StopFaceTimelines();
        DashboardShadowSuspender.TryResume(this);
        RestoreBakedShadowPlates();
        _isAnimating = false;
        FlipShade.Opacity = 0;
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
        var inactive = DeferHiddenFaceLayout && !_isAnimating && !_preparingFaces ? Visibility.Collapsed : Visibility.Hidden;
        if (Math.Abs(scaleY) <= EdgeThreshold)
        {
            FrontHost.Visibility = Visibility.Hidden;
            BackFace.Visibility = Visibility.Hidden;
            return;
        }

        if (scaleY > 0)
        {
            FrontHost.Visibility = Visibility.Visible;
            BackFace.Visibility = inactive;
            return;
        }

        FrontHost.Visibility = inactive;
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
